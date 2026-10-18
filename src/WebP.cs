// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;

namespace SolSatellite
{
    /// <summary>
    /// Decoder for lossless WebP (VP8L, RFC 9649): what terrain height tiles come as (heights
    /// can't be lossy). Pure managed code, safe on worker threads. Lossy WebP isn't supported.
    /// </summary>
    static class WebP
    {
        /// <summary>The image as ARGB pixels, row by row from the top.</summary>
        public static uint[] Decode(byte[] data, out int width, out int height)
        {
            if (data == null || data.Length < 20 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
                data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
                throw new FormatException("not a WebP file");
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                int size = data[pos + 4] | data[pos + 5] << 8 | data[pos + 6] << 16 | data[pos + 7] << 24;
                if (size < 0 || pos + 8 + size > data.Length)
                    throw new FormatException("truncated WebP chunk");
                if (data[pos] == 'V' && data[pos + 1] == 'P' && data[pos + 2] == '8' && data[pos + 3] == 'L')
                    return new Vp8l(data, pos + 8, size).Decode(out width, out height);
                pos += 8 + size + (size & 1);
            }
            throw new FormatException("no lossless (VP8L) image in the WebP file");
        }
    }

    /// <summary>One VP8L bitstream; follows libwebp's decoder (vp8l_dec.c, huffman_utils.c).</summary>
    sealed class Vp8l
    {
        const int LiteralCodes = 256, LengthCodes = 24, DistanceCodes = 40, MaxCacheBits = 11;
        const int RootBits = 8, MaxCodeLength = 15, LengthsTableBits = 7;
        static readonly int[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        // (xi, yi) offsets of the 120 short distance codes.
        static readonly sbyte[] DistanceMap =
        {
            0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2,
            -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0,
            1, 3, -1, 3, 3, 1, -3, 1, 2, 3, -2, 3, 3, 2,
            -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1,
            3, 3, -3, 3, 2, 4, -2, 4, 4, 2, -4, 2, 0, 5,
            3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5,
            5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2, 4, 4,
            -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0,
            1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2,
            -6, 2, 4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6,
            6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5,
            -5, 5, 7, 1, -7, 1, 4, 6, -4, 6, 6, 4, -6, 4,
            2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3,
            -7, 3, 5, 6, -5, 6, 6, 5, -6, 5, 8, 0, 4, 7,
            -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6,
            8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
            -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6,
            8, 7,
        };

        sealed class Transform
        {
            public int Type, XSize, Bits;
            public uint[] Data;
        }

        sealed class Group
        {
            public int[] Green, Red, Blue, Alpha, Distance;
        }

        readonly byte[] buf;
        readonly int end;
        readonly long available; // bits in the stream
        int pos;
        ulong window;
        int count;               // bits in window
        long consumed;
        readonly List<Transform> transforms = new List<Transform>();
        int seen;                // transform types read

        public Vp8l(byte[] data, int start, int length)
        {
            buf = data;
            pos = start;
            end = start + length;
            available = (long)length * 8;
        }

        public uint[] Decode(out int width, out int height)
        {
            if (Read(8) != 0x2f)
                throw new FormatException("bad VP8L signature");
            width = (int)Read(14) + 1;
            height = (int)Read(14) + 1;
            Read(1); // alpha used
            if (Read(3) != 0)
                throw new FormatException("unknown VP8L version");
            uint[] image = DecodeImageStream(width, height, true);
            if (consumed > available)
                throw new FormatException("truncated VP8L data");
            return image;
        }

        // ------------------------------------------------------------------ bits

        void Fill()
        {
            while (count <= 56)
            {
                if (pos < end)
                    window |= (ulong)buf[pos++] << count;
                count += 8; // past the end: zeros (consumed tells)
            }
        }

        uint Read(int n)
        {
            if (n == 0)
                return 0;
            if (count < n)
                Fill();
            uint v = (uint)(window & ((1UL << n) - 1));
            window >>= n;
            count -= n;
            consumed += n;
            return v;
        }

        int ReadSymbol(int[] table)
        {
            if (count < MaxCodeLength)
                Fill();
            int e = table[(int)(window & 0xff)];
            int nbits = (e >> 16) - RootBits;
            if (nbits > 0)
            {
                int root = (int)(window & 0xff);
                window >>= RootBits;
                count -= RootBits;
                consumed += RootBits;
                e = table[root + (e & 0xffff) + (int)(window & ((1UL << nbits) - 1))];
            }
            int bits = e >> 16;
            window >>= bits;
            count -= bits;
            consumed += bits;
            return e & 0xffff;
        }

        void CheckEnd()
        {
            if (consumed > available + 64)
                throw new FormatException("truncated VP8L data");
        }

        // ------------------------------------------------------------------ prefix codes

        static int NextKey(int key, int len)
        {
            int step = 1 << (len - 1);
            while ((key & step) != 0)
                step >>= 1;
            return step != 0 ? (key & (step - 1)) + step : key;
        }

        static void Replicate(int[] table, int start, int step, int end, int code)
        {
            do
            {
                end -= step;
                table[start + end] = code;
            } while (end > 0);
        }

        static int NextTableBitSize(int[] count, int len, int rootBits)
        {
            int left = 1 << (len - rootBits);
            while (len < MaxCodeLength)
            {
                left -= count[len];
                if (left <= 0)
                    break;
                ++len;
                left <<= 1;
            }
            return len - rootBits;
        }

        /// <summary>Lookup table for a canonical prefix code: entries are (bits &lt;&lt; 16) | value;
        /// a root entry with more than rootBits bits points to a second-level table. Null if
        /// the code lengths don't make a valid code.</summary>
        static int[] BuildTable(int[] lengths, int n, int rootBits)
        {
            var count = new int[MaxCodeLength + 1];
            for (int s = 0; s < n; s++)
            {
                if (lengths[s] > MaxCodeLength)
                    return null;
                count[lengths[s]]++;
            }
            if (count[0] == n)
                return null;
            var offset = new int[MaxCodeLength + 1];
            for (int len = 1; len < MaxCodeLength; len++)
            {
                if (count[len] > 1 << len)
                    return null;
                offset[len + 1] = offset[len] + count[len];
            }
            var sorted = new int[n];
            for (int s = 0; s < n; s++)
                if (lengths[s] > 0)
                    sorted[offset[lengths[s]]++] = s;
            int codes = offset[MaxCodeLength];
            if (codes == 1)
            {
                var single = new int[1 << rootBits];
                for (int i = 0; i < single.Length; i++)
                    single[i] = sorted[0]; // zero bits
                return single;
            }
            int size = Fill(null, rootBits, count, sorted, codes);
            if (size == 0)
                return null;
            var table = new int[size];
            Fill(table, rootBits, count, sorted, codes);
            return table;
        }

        /// <summary>Fills the table (or with null just measures it); 0 if the code is not complete.</summary>
        static int Fill(int[] root, int rootBits, int[] counts, int[] sorted, int codes)
        {
            var count = (int[])counts.Clone();
            int rootMask = (1 << rootBits) - 1;
            int tableOff = 0, tableBits = rootBits, tableSize = 1 << rootBits, totalSize = 1 << rootBits;
            int key = 0, symbol = 0, numNodes = 1, numOpen = 1, low = -1;
            int len, step;
            for (len = 1, step = 2; len <= rootBits; ++len, step <<= 1)
            {
                numOpen <<= 1;
                numNodes += numOpen;
                numOpen -= count[len];
                if (numOpen < 0)
                    return 0;
                for (; count[len] > 0; --count[len])
                {
                    if (root != null)
                        Replicate(root, tableOff + key, step, tableSize, len << 16 | sorted[symbol]);
                    symbol++;
                    key = NextKey(key, len);
                }
            }
            for (len = rootBits + 1, step = 2; len <= MaxCodeLength; ++len, step <<= 1)
            {
                numOpen <<= 1;
                numNodes += numOpen;
                numOpen -= count[len];
                if (numOpen < 0)
                    return 0;
                for (; count[len] > 0; --count[len])
                {
                    if ((key & rootMask) != low)
                    {
                        tableOff += tableSize;
                        tableBits = NextTableBitSize(count, len, rootBits);
                        tableSize = 1 << tableBits;
                        totalSize += tableSize;
                        low = key & rootMask;
                        if (root != null)
                            root[low] = (tableBits + rootBits) << 16 | (tableOff - low);
                    }
                    if (root != null)
                        Replicate(root, tableOff + (key >> rootBits), step, tableSize, (len - rootBits) << 16 | sorted[symbol]);
                    symbol++;
                    key = NextKey(key, len);
                }
            }
            return numNodes == 2 * codes - 1 ? totalSize : 0;
        }

        int[] ReadCode(int alphabet)
        {
            var lengths = new int[Math.Max(alphabet, 256)];
            if (Read(1) == 1)
            {
                // Simple code: one or two symbols.
                int symbols = (int)Read(1) + 1;
                int first = (int)Read(Read(1) == 0 ? 1 : 8);
                lengths[first] = 1;
                if (symbols == 2)
                    lengths[Read(8)] = 1;
            }
            else
            {
                var codeLengthLengths = new int[CodeLengthOrder.Length];
                int n = (int)Read(4) + 4;
                for (int i = 0; i < n; i++)
                    codeLengthLengths[CodeLengthOrder[i]] = (int)Read(3);
                ReadCodeLengths(codeLengthLengths, alphabet, lengths);
            }
            CheckEnd();
            int[] table = BuildTable(lengths, alphabet, RootBits);
            if (table == null)
                throw new FormatException("bad VP8L prefix code");
            return table;
        }

        void ReadCodeLengths(int[] codeLengthLengths, int symbols, int[] lengths)
        {
            int[] table = BuildTable(codeLengthLengths, codeLengthLengths.Length, LengthsTableBits);
            if (table == null)
                throw new FormatException("bad VP8L code length code");
            int max = symbols;
            if (Read(1) == 1)
            {
                int nbits = 2 + 2 * (int)Read(3);
                max = 2 + (int)Read(nbits);
                if (max > symbols)
                    throw new FormatException("bad VP8L code length count");
            }
            int symbol = 0, previous = 8;
            while (symbol < symbols)
            {
                if (max-- == 0)
                    break;
                if (count < LengthsTableBits)
                    Fill();
                int e = table[(int)(window & ((1UL << LengthsTableBits) - 1))];
                int bits = e >> 16;
                window >>= bits;
                count -= bits;
                consumed += bits;
                int length = e & 0xffff;
                if (length < 16)
                {
                    lengths[symbol++] = length;
                    if (length != 0)
                        previous = length;
                }
                else
                {
                    int slot = length - 16;
                    int repeat = (int)Read(slot == 0 ? 2 : slot == 1 ? 3 : 7) + (slot == 2 ? 11 : 3);
                    if (symbol + repeat > symbols)
                        throw new FormatException("bad VP8L code lengths");
                    int value = length == 16 ? previous : 0;
                    while (repeat-- > 0)
                        lengths[symbol++] = value;
                }
            }
        }

        // ------------------------------------------------------------------ image

        static int SubSample(int size, int bits) => (size + (1 << bits) - 1) >> bits;

        uint[] DecodeImageStream(int xsize, int ysize, bool level0)
        {
            int width = xsize;
            if (level0)
                while (Read(1) == 1)
                    ReadTransform(ref width, ysize);

            int cacheBits = 0;
            if (Read(1) == 1)
            {
                cacheBits = (int)Read(4);
                if (cacheBits < 1 || cacheBits > MaxCacheBits)
                    throw new FormatException("bad VP8L colour cache size");
            }

            int metaBits = 0, metaWidth = 0, groupCount = 1;
            int[] meta = null;
            if (level0 && Read(1) == 1)
            {
                metaBits = (int)Read(3) + 2;
                metaWidth = SubSample(width, metaBits);
                uint[] image = DecodeImageStream(metaWidth, SubSample(ysize, metaBits), false);
                meta = new int[image.Length];
                for (int i = 0; i < image.Length; i++)
                {
                    meta[i] = (int)((image[i] >> 8) & 0xffff);
                    groupCount = Math.Max(groupCount, meta[i] + 1);
                }
            }

            int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
            var groups = new Group[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                groups[g] = new Group
                {
                    Green = ReadCode(LiteralCodes + LengthCodes + cacheSize),
                    Red = ReadCode(LiteralCodes),
                    Blue = ReadCode(LiteralCodes),
                    Alpha = ReadCode(LiteralCodes),
                    Distance = ReadCode(DistanceCodes),
                };
            }

            uint[] data = new uint[width * ysize];
            DecodeData(data, width, ysize, groups, meta, metaBits, metaWidth, cacheBits);
            if (!level0)
                return data;
            for (int i = transforms.Count - 1; i >= 0; i--)
                data = Inverse(transforms[i], data, ysize);
            return data;
        }

        void ReadTransform(ref int xsize, int ysize)
        {
            int type = (int)Read(2);
            if ((seen & 1 << type) != 0)
                throw new FormatException("repeated VP8L transform");
            seen |= 1 << type;
            var t = new Transform { Type = type, XSize = xsize };
            switch (type)
            {
                case 0: // predictor
                case 1: // cross colour
                    t.Bits = (int)Read(3) + 2;
                    t.Data = DecodeImageStream(SubSample(xsize, t.Bits), SubSample(ysize, t.Bits), false);
                    break;
                case 3: // colour indexing
                    int colours = (int)Read(8) + 1;
                    t.Bits = colours > 16 ? 0 : colours > 4 ? 1 : colours > 2 ? 2 : 3;
                    xsize = SubSample(t.XSize, t.Bits);
                    uint[] palette = DecodeImageStream(colours, 1, false);
                    t.Data = new uint[1 << (8 >> t.Bits)];
                    t.Data[0] = palette[0];
                    for (int i = 1; i < colours; i++)
                        t.Data[i] = AddPixels(palette[i], t.Data[i - 1]);
                    break;
            }
            transforms.Add(t);
        }

        void DecodeData(uint[] data, int width, int height, Group[] groups, int[] meta, int metaBits, int metaWidth, int cacheBits)
        {
            int total = width * height;
            int mask = meta == null ? -1 : (1 << metaBits) - 1;
            uint[] cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            int shift = 32 - cacheBits;
            int cacheLimit = LiteralCodes + LengthCodes + (cache != null ? cache.Length : 0);
            int at = 0, col = 0, row = 0;
            Group g = groups[0];
            while (at < total)
            {
                if ((col & mask) == 0)
                    g = meta == null ? groups[0] : groups[meta[(row >> metaBits) * metaWidth + (col >> metaBits)]];
                int code = ReadSymbol(g.Green);
                if (code < LiteralCodes || code >= LiteralCodes + LengthCodes)
                {
                    uint argb;
                    if (code < LiteralCodes)
                    {
                        uint red = (uint)ReadSymbol(g.Red), blue = (uint)ReadSymbol(g.Blue), alpha = (uint)ReadSymbol(g.Alpha);
                        argb = alpha << 24 | red << 16 | (uint)code << 8 | blue;
                    }
                    else if (code < cacheLimit)
                    {
                        argb = cache[code - LiteralCodes - LengthCodes];
                    }
                    else
                    {
                        throw new FormatException("bad VP8L symbol");
                    }
                    data[at++] = argb;
                    if (cache != null)
                        cache[(int)((0x1e35a7bdu * argb) >> shift)] = argb;
                    if (++col >= width)
                    {
                        col = 0;
                        row++;
                        CheckEnd();
                    }
                }
                else
                {
                    int length = Prefix(code - LiteralCodes);
                    int distance = Distance(width, Prefix(ReadSymbol(g.Distance)));
                    if (distance > at || length > total - at)
                        throw new FormatException("bad VP8L backward reference");
                    for (int k = 0; k < length; k++, at++)
                    {
                        uint argb = data[at] = data[at - distance];
                        if (cache != null)
                            cache[(int)((0x1e35a7bdu * argb) >> shift)] = argb;
                    }
                    col += length;
                    while (col >= width)
                    {
                        col -= width;
                        row++;
                    }
                    if (meta != null && (col & mask) != 0 && at < total)
                        g = groups[meta[(row >> metaBits) * metaWidth + (col >> metaBits)]];
                    CheckEnd();
                }
            }
        }

        int Prefix(int symbol)
        {
            if (symbol < 4)
                return symbol + 1;
            int extra = (symbol - 2) >> 1;
            int offset = (2 + (symbol & 1)) << extra;
            return offset + (int)Read(extra) + 1;
        }

        static int Distance(int width, int code)
        {
            if (code > 120)
                return code - 120;
            int d = DistanceMap[2 * (code - 1)] + DistanceMap[2 * (code - 1) + 1] * width;
            return d >= 1 ? d : 1;
        }

        // ------------------------------------------------------------------ transforms

        static uint AddPixels(uint a, uint b) =>
            ((a & 0xff00ff00u) + (b & 0xff00ff00u)) & 0xff00ff00u | ((a & 0x00ff00ffu) + (b & 0x00ff00ffu)) & 0x00ff00ffu;

        static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);

        static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        static uint Select(uint t, uint l, uint tl)
        {
            int predictLeft = 0, predictTop = 0;
            for (int s = 0; s < 32; s += 8)
            {
                int a = (int)(t >> s) & 0xff, b = (int)(l >> s) & 0xff, c = (int)(tl >> s) & 0xff;
                predictLeft += Math.Abs(a - c); // distance of the estimate T + L - TL from L
                predictTop += Math.Abs(b - c);  // ... from T
            }
            return predictLeft < predictTop ? l : t;
        }

        static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8)
                r |= (uint)Clip255(((int)(c0 >> s) & 0xff) + ((int)(c1 >> s) & 0xff) - ((int)(c2 >> s) & 0xff)) << s;
            return r;
        }

        static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
        {
            uint ave = Average2(c0, c1), r = 0;
            for (int s = 0; s < 32; s += 8)
            {
                int a = (int)(ave >> s) & 0xff, b = (int)(c2 >> s) & 0xff;
                r |= (uint)Clip255(a + (a - b) / 2) << s;
            }
            return r;
        }

        static uint Predict(int mode, uint l, uint t, uint tl, uint tr)
        {
            switch (mode)
            {
                case 1: return l;
                case 2: return t;
                case 3: return tr;
                case 4: return tl;
                case 5: return Average2(Average2(l, tr), t);
                case 6: return Average2(l, tl);
                case 7: return Average2(l, t);
                case 8: return Average2(tl, t);
                case 9: return Average2(t, tr);
                case 10: return Average2(Average2(l, tl), Average2(t, tr));
                case 11: return Select(t, l, tl);
                case 12: return ClampedAddSubtractFull(l, t, tl);
                case 13: return ClampedAddSubtractHalf(l, t, tl);
                default: return 0xff000000u;
            }
        }

        static uint[] Inverse(Transform t, uint[] d, int height)
        {
            int w = t.XSize;
            switch (t.Type)
            {
                case 0:
                {
                    d[0] = AddPixels(d[0], 0xff000000u);
                    for (int x = 1; x < w; x++)
                        d[x] = AddPixels(d[x], d[x - 1]);
                    int tilesPerRow = SubSample(w, t.Bits);
                    for (int y = 1; y < height; y++)
                    {
                        int row = y * w, modes = (y >> t.Bits) * tilesPerRow;
                        d[row] = AddPixels(d[row], d[row - w]);
                        for (int x = 1; x < w; x++)
                        {
                            int i = row + x, up = i - w;
                            int mode = (int)(t.Data[modes + (x >> t.Bits)] >> 8) & 0xf;
                            // Top-right of the last pixel in a row is the row's first pixel, as in libwebp.
                            d[i] = AddPixels(d[i], Predict(mode, d[i - 1], d[up], d[up - 1], d[up + 1]));
                        }
                    }
                    return d;
                }
                case 1:
                {
                    int tilesPerRow = SubSample(w, t.Bits);
                    for (int y = 0; y < height; y++)
                    {
                        int row = y * w, codes = (y >> t.Bits) * tilesPerRow;
                        for (int x = 0; x < w; x++)
                        {
                            uint m = t.Data[codes + (x >> t.Bits)];
                            int greenToRed = (sbyte)(m & 0xff), greenToBlue = (sbyte)((m >> 8) & 0xff), redToBlue = (sbyte)((m >> 16) & 0xff);
                            uint argb = d[row + x];
                            int green = (sbyte)((argb >> 8) & 0xff);
                            int red = (int)(argb >> 16) & 0xff, blue = (int)argb & 0xff;
                            red = (red + (greenToRed * green >> 5)) & 0xff;
                            blue = (blue + (greenToBlue * green >> 5)) & 0xff;
                            blue = (blue + (redToBlue * (sbyte)red >> 5)) & 0xff;
                            d[row + x] = argb & 0xff00ff00u | (uint)red << 16 | (uint)blue;
                        }
                    }
                    return d;
                }
                case 2:
                    for (int i = 0; i < d.Length; i++)
                    {
                        uint argb = d[i], green = (argb >> 8) & 0xff;
                        d[i] = argb & 0xff00ff00u | ((argb & 0x00ff00ffu) + (green << 16 | green)) & 0x00ff00ffu;
                    }
                    return d;
                default:
                {
                    var o = new uint[w * height];
                    int perPixel = 8 >> t.Bits, inWidth = SubSample(w, t.Bits);
                    if (perPixel < 8)
                    {
                        int countMask = (1 << t.Bits) - 1;
                        uint bitMask = (1u << perPixel) - 1;
                        for (int y = 0; y < height; y++)
                        {
                            int src = y * inWidth;
                            uint packed = 0;
                            for (int x = 0; x < w; x++)
                            {
                                if ((x & countMask) == 0)
                                    packed = (d[src++] >> 8) & 0xff;
                                o[y * w + x] = t.Data[packed & bitMask];
                                packed >>= perPixel;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < o.Length; i++)
                            o[i] = t.Data[(d[i] >> 8) & 0xff];
                    }
                    return o;
                }
            }
        }
    }
}
