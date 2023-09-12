﻿using nQuant.Master;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

/* Fast pairwise nearest neighbor based algorithm for multilevel thresholding
Copyright (C) 2004-2016 Mark Tyler and Dmitry Groshev
Copyright (c) 2018-2023 Miller Cy Chan
* error measure; time used is proportional to number of bins squared - WJ */

namespace PnnQuant
{
    public class PnnQuantizer : Ditherable
    {
        protected byte alphaThreshold = 0xF;
        protected bool dither = true, hasSemiTransparency = false;
        protected int m_transparentPixelIndex = -1;
        protected Color m_transparentColor = Color.Transparent;
        protected Color[] m_palette;
        protected readonly Random rand = new();
        protected readonly Dictionary<int, ushort[]> closestMap = new();
        protected readonly Dictionary<int, ushort> nearestMap = new();

        protected double PR = 0.299, PG = 0.587, PB = 0.114, PA = .3333;
        protected double ratio = .5, weight = 1;

        protected static readonly float[,] coeffs = new float[,] {
            {0.299f, 0.587f, 0.114f},
            {-0.14713f, -0.28886f, 0.436f},
            {0.615f, -0.51499f, -0.10001f}
        };
		
        private sealed class Pnnbin
        {
            internal float ac, rc, gc, bc;
            internal float cnt;
            internal int nn, fw, bk, tm, mtm;
            internal float err;
        }
		
		protected PnnQuantizer(PnnQuantizer quantizer) {
			alphaThreshold = quantizer.alphaThreshold;
			hasSemiTransparency = quantizer.hasSemiTransparency;
			m_transparentPixelIndex = quantizer.m_transparentPixelIndex;
			m_transparentColor = quantizer.m_transparentColor;
		}

        public PnnQuantizer()
        {
        }

        internal bool HasAlpha
        {
            get => m_transparentPixelIndex >= 0;
        }

        public int GetColorIndex(int argb)
        {
            return BitmapUtilities.GetARGBIndex(argb, hasSemiTransparency, HasAlpha);
        }

        private void Find_nn(Pnnbin[] bins, int idx)
        {
            int nn = 0;
            var err = 1e100;

            var bin1 = bins[idx];
            var n1 = bin1.cnt;
            var wa = bin1.ac;
            var wr = bin1.rc;
            var wg = bin1.gc;
            var wb = bin1.bc;

            int start = 0;
            if (BlueNoise.TELL_BLUE_NOISE[idx & 4095] > -88)
                start = (PG < coeffs[0, 1]) ? coeffs.GetLength(0) : 1;

            for (int i = bin1.fw; i != 0; i = bins[i].fw)
            {
                var n2 = bins[i].cnt;
                var nerr2 = (n1 * n2) / (n1 + n2);
                if (nerr2 >= err)
                    continue;

                var nerr = 0.0;
                if (hasSemiTransparency)
                {
                    start = 1;
                    nerr += nerr2 * (1 - ratio) * PA * BitmapUtilities.Sqr(bins[i].ac - wa);
                    if (nerr >= err)
                        continue;
                }

                nerr += nerr2 * (1 - ratio) * PR * BitmapUtilities.Sqr(bins[i].rc - wr);
                if (nerr >= err)
                    continue;

                nerr += nerr2 * (1 - ratio) * PG * BitmapUtilities.Sqr(bins[i].gc - wg);
                if (nerr >= err)
                    continue;

                nerr += nerr2 * (1 - ratio) * PB * BitmapUtilities.Sqr(bins[i].bc - wb);
                if (nerr >= err)
                    continue;

                for (int j = start; j < coeffs.GetLength(0); ++j)
                {
                    nerr += nerr2 * ratio * BitmapUtilities.Sqr(coeffs[j, 0] * (bins[i].rc - wr));
                    if (nerr >= err)
                        break;

                    nerr += nerr2 * ratio * BitmapUtilities.Sqr(coeffs[j, 1] * (bins[i].gc - wg));
                    if (nerr >= err)
                        break;

                    nerr += nerr2 * ratio * BitmapUtilities.Sqr(coeffs[j, 2] * (bins[i].bc - wb));
                    if (nerr >= err)
                        break;
                }

                err = nerr;
                nn = i;
            }
            bin1.err = (float) err;
            bin1.nn = nn;
        }

        protected delegate float QuanFn(float cnt);
        protected virtual QuanFn GetQuanFn(int nMaxColors, short quan_rt)
        {
            if (quan_rt > 0) {
                if (nMaxColors < 64)
                    return cnt => (int)Math.Sqrt(cnt);
                return cnt => (float)Math.Sqrt(cnt);
            }
            if (quan_rt < 0)
                return cnt => (int) Math.Cbrt(cnt);
            return cnt => cnt;
        }
        internal virtual void Pnnquan(int[] pixels, ref Color[] palettes, ref int nMaxColors)
        {
            short quan_rt = 1;
            var bins = new Pnnbin[ushort.MaxValue + 1];

            /* Build histogram */
            foreach (var pixel in pixels)
            {
                var c = Color.FromArgb(pixel);
                if (c.A <= alphaThreshold)
                    c = m_transparentColor;

                int index = BitmapUtilities.GetARGBIndex(c.ToArgb(), hasSemiTransparency, nMaxColors < 64 || HasAlpha);
                if (bins[index] == null)
                    bins[index] = new Pnnbin();
                bins[index].ac += c.A;
                bins[index].rc += c.R;
                bins[index].gc += c.G;
                bins[index].bc += c.B;
                bins[index].cnt += 1.0f;               
            }

            /* Cluster nonempty bins at one end of array */
            int maxbins = 0;
            for (int i = 0; i < bins.Length; ++i)
            {
                if (bins[i] == null)
                    continue;

                var d = 1.0f / bins[i].cnt;
                bins[i].ac *= d;
                bins[i].rc *= d;
                bins[i].gc *= d;
                bins[i].bc *= d;

                bins[maxbins++] = bins[i];
            }

            if (nMaxColors < 16)
                nMaxColors = -1;
                
            weight = Math.Min(0.9, nMaxColors * 1.0 / maxbins);
            if (weight < .03 && PG < 1 && PG >= coeffs[0, 1]) {
                PR = PG = PB = PA = 1;
                if (nMaxColors >= 64)
                    quan_rt = 0;
            }

            var quanFn = GetQuanFn(nMaxColors, quan_rt);

            int j = 0;
            for (; j < maxbins - 1; ++j)
            {
                bins[j].fw = j + 1;
                bins[j + 1].bk = j;

                bins[j].cnt = quanFn(bins[j].cnt);
            }
            bins[j].cnt = quanFn(bins[j].cnt);

            int h, l, l2;
            /* Initialize nearest neighbors and build heap of them */
            var heap = new int[bins.Length + 1];
            for (int i = 0; i < maxbins; ++i)
            {
                Find_nn(bins, i);
                /* Push slot on heap */
                double err = bins[i].err;
                for (l = ++heap[0]; l > 1; l = l2)
                {
                    l2 = l >> 1;
                    if (bins[h = heap[l2]].err <= err)
                        break;
                    heap[l] = h;
                }
                heap[l] = i;
            }

            /* Merge bins which increase error the least */
            int extbins = maxbins - nMaxColors;
            for (int i = 0; i < extbins;)
            {
                Pnnbin tb;
                /* Use heap to find which bins to merge */
                for (; ; )
                {
                    int b1 = heap[1];
                    tb = bins[b1]; /* One with least error */
                    /* Is stored error up to date? */
                    if ((tb.tm >= tb.mtm) && (bins[tb.nn].mtm <= tb.tm))
                        break;
                    if (tb.mtm == ushort.MaxValue) /* Deleted node */
                        b1 = heap[1] = heap[heap[0]--];
                    else /* Too old error value */
                    {
                        Find_nn(bins, b1);
                        tb.tm = i;
                    }
                    /* Push slot down */
                    var err = bins[b1].err;
                    for (l = 1; (l2 = l + l) <= heap[0]; l = l2)
                    {
                        if ((l2 < heap[0]) && (bins[heap[l2]].err > bins[heap[l2 + 1]].err))
                            ++l2;
                        if (err <= bins[h = heap[l2]].err)
                            break;
                        heap[l] = h;
                    }
                    heap[l] = b1;
                }

                /* Do a merge */
                var nb = bins[tb.nn];
                var n1 = tb.cnt;
                var n2 = nb.cnt;
                var d = 1.0f / (n1 + n2);
                tb.ac = d * (float) Math.Round(n1 * tb.ac + n2 * nb.ac);
                tb.rc = d * (float) Math.Round(n1 * tb.rc + n2 * nb.rc);
                tb.gc = d * (float) Math.Round(n1 * tb.gc + n2 * nb.gc);
                tb.bc = d * (float) Math.Round(n1 * tb.bc + n2 * nb.bc);
                tb.cnt += n2;
                tb.mtm = ++i;

                /* Unchain deleted bin */
                bins[nb.bk].fw = nb.fw;
                bins[nb.fw].bk = nb.bk;
                nb.mtm = ushort.MaxValue;
            }

            /* Fill palette */
            if (extbins < 0)
                palettes = new Color[maxbins];

            int k = 0;
            for (int i = 0; ; ++k)
            {
                var alpha = (hasSemiTransparency || HasAlpha) ? Math.Clamp((int)Math.Round(bins[i].ac), Byte.MinValue, Byte.MaxValue) : Byte.MaxValue;
                palettes[k] = Color.FromArgb(alpha, Math.Clamp((int)bins[i].rc, Byte.MinValue, Byte.MaxValue), Math.Clamp((int)bins[i].gc, Byte.MinValue, Byte.MaxValue), Math.Clamp((int)bins[i].bc, Byte.MinValue, Byte.MaxValue));

                if ((i = bins[i].fw) == 0)
                    break;
            }

            if (k < nMaxColors - 1)
            {
                nMaxColors = k + 1;
                Console.WriteLine("Maximum number of colors: " + palettes.Length);
            }
        }
        internal virtual ushort NearestColorIndex(Color[] palette, int pixel, int pos)
        {
            if (nearestMap.TryGetValue(pixel, out var k))
                return k;

            var c = Color.FromArgb(pixel);
            if (c.A <= alphaThreshold)
                c = m_transparentColor;
            if (palette.Length > 2 && HasAlpha && c.A > alphaThreshold)
                k = 1;

            double pr = PR, pg = PG, pb = PB, pa = PA;
            if(palette.Length < 3)
                pr = pg = pb = pa = 1;

            double mindist = int.MaxValue;
            var nMaxColors = palette.Length;
            for (int i = k; i < nMaxColors; ++i)
            {
                var c2 = palette[i];
                var curdist = pa * BitmapUtilities.Sqr(c2.A - c.A);
                if (curdist > mindist)
                    continue;

                curdist += pr * BitmapUtilities.Sqr(c2.R - c.R);
                if (curdist > mindist)
                    continue;

                curdist += pg * BitmapUtilities.Sqr(c2.G - c.G);
                if (curdist > mindist)
                    continue;

                curdist += pb * BitmapUtilities.Sqr(c2.B - c.B);
                if (curdist > mindist)
                    continue;

                mindist = curdist;
                k = (ushort)i;
            }
            nearestMap[pixel] = k;
            return k;
        }
        
        protected virtual ushort ClosestColorIndex(Color[] palette, int pixel, int pos)
        {
            ushort k = 0;
            var c = Color.FromArgb(pixel);
            if (c.A <= alphaThreshold)
                return NearestColorIndex(palette, pixel, pos);

            if (!closestMap.TryGetValue(pixel, out var closest))
            {
                closest = new ushort[4];
                closest[2] = closest[3] = ushort.MaxValue;

                double pr = PR, pg = PG, pb = PB, pa = PA;
                if(palette.Length < 3)
                    pr = pg = pb = pa = 1;

                var nMaxColors = palette.Length;
                for (; k < nMaxColors; ++k)
                {
                    var c2 = palette[k];
                    var err = pr * BitmapUtilities.Sqr(c.R - c2.R);
                    if (err >= closest[3])
                        break;

                    err += pg * BitmapUtilities.Sqr(c.G - c2.G);
                    if (err >= closest[3])
                        break;

                    err += pb * BitmapUtilities.Sqr(c.B - c2.B);
                    if (err >= closest[3])
                        break;

                    if (hasSemiTransparency)
                        err += pa * BitmapUtilities.Sqr(c.A - c2.A);

                    if (err < closest[2])
                    {
                        closest[1] = closest[0];
                        closest[3] = closest[2];
                        closest[0] = k;
                        closest[2] = (ushort) err;
                    }
                    else if (err < closest[3])
                    {
                        closest[1] = k;
                        closest[3] = (ushort) err;
                    }
                }

                if (closest[3] == ushort.MaxValue)
                    closest[1] = closest[0];
                
                closestMap[pixel] = closest;
            }

            var MAX_ERR = palette.Length << 2;
            int idx = (pos + 1) % 2;
            if (closest[3] * .67 < (closest[3] - closest[2]))
                idx = 0;
            else if (closest[0] > closest[1])
                idx = pos % 2;

            if (closest[idx + 2] >= MAX_ERR || (HasAlpha && closest[idx] == 0))
                return NearestColorIndex(palette, pixel, pos);
            return closest[idx];
        }

        public virtual ushort DitherColorIndex(Color[] palette, int pixel, int pos)
        {
            if (dither)
                return NearestColorIndex(palette, pixel, pos);
            return ClosestColorIndex(palette, pixel, pos);
        }

        protected bool IsValidFormat(PixelFormat pixelFormat, int nMaxColors)
        {
            if (pixelFormat == PixelFormat.Undefined)
                return false;

            int bitDepth = Image.GetPixelFormatSize(pixelFormat);
            return Math.Pow(2, bitDepth) >= nMaxColors;
        }

        protected virtual int[] Dither(int[] pixels, Color[] palettes, int width, int height, bool dither)
        {
            this.dither = dither;
            if (hasSemiTransparency)
                weight *= -1;
            var qPixels = GilbertCurve.Dither(width, height, pixels, palettes, this, null, weight);

            if (!dither)
                BlueNoise.Dither(width, height, pixels, palettes, this, qPixels);
            return qPixels;
        }

        internal int[] GrabPixels(Bitmap source, int nMaxColors, ref bool hasSemiTransparency)
		{
			var bitmapWidth = source.Width;
			var bitmapHeight = source.Height;
			var pixels = new int[bitmapWidth * bitmapHeight];
			int semiTransCount = 0;
			if (!BitmapUtilities.GrabPixels(source, pixels, ref semiTransCount, ref m_transparentColor, ref m_transparentPixelIndex, alphaThreshold, nMaxColors))
				return null;
            this.hasSemiTransparency = hasSemiTransparency = semiTransCount > 0;
			return pixels;
		}

        public Bitmap QuantizeImage(Bitmap source, PixelFormat pixelFormat, int nMaxColors, bool dither)
        {
            if (nMaxColors <= 32)
                PR = PG = PB = PA = 1;
            else {
                PR = coeffs[0, 0]; PG = coeffs[0, 1]; PB = coeffs[0, 2];
            }

			if (!IsValidFormat(pixelFormat, nMaxColors))
			{
				if (nMaxColors > 256)
					pixelFormat = HasAlpha ? PixelFormat.Format16bppArgb1555 : PixelFormat.Format16bppRgb565;
				else
					pixelFormat = (nMaxColors > 16) ? PixelFormat.Format8bppIndexed : (nMaxColors > 2) ? PixelFormat.Format4bppIndexed : PixelFormat.Format1bppIndexed;
			}

            var bitmapWidth = source.Width;
            var bitmapHeight = source.Height;

            var dest = new Bitmap(bitmapWidth, bitmapHeight, pixelFormat);
            var pixels = GrabPixels(source, nMaxColors, ref hasSemiTransparency);
            if (pixels == null)
                return dest;

            var palettes = dest.Palette.Entries;
			if (palettes.Length != nMaxColors)
				palettes = new Color[nMaxColors];

			if (nMaxColors > 2)
				Pnnquan(pixels, ref palettes, ref nMaxColors);
			else {
				if (HasAlpha)
				{
					palettes[0] = m_transparentColor;
					palettes[1] = Color.Black;
				}
				else
				{
					palettes[0] = Color.Black;
					palettes[1] = Color.White;
				}
			}
			m_palette = palettes;

            var qPixels = Dither(pixels, m_palette, bitmapWidth, bitmapHeight, dither);

            if (HasAlpha && nMaxColors <= 256)
            {
                var k = qPixels[m_transparentPixelIndex];
                if (nMaxColors > 2)
                    m_palette[k] = m_transparentColor;
                else if (m_palette[k] != m_transparentColor)
                    BitmapUtilities.Swap(ref m_palette[0], ref m_palette[1]);
            }
            closestMap.Clear();
            nearestMap.Clear();

            if (nMaxColors > 256)
                return BitmapUtilities.ProcessImagePixels(dest, qPixels, hasSemiTransparency, m_transparentPixelIndex);
            
            return BitmapUtilities.ProcessImagePixels(dest, m_palette, qPixels, HasAlpha);
        }
    }

}
