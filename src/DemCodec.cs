// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Runtime.InteropServices;

namespace SolSatellite
{
    /// <summary>Elevation tiles as the sampler stores them. Plain .NET, safe on worker threads.</summary>
    static unsafe class DemCodec
    {
        /// <summary>A Terrarium-encoded WebP tile (height = R * 256 + G + B / 256 - 32768 m) as
        /// stored heights in new unmanaged memory (free with Marshal.FreeHGlobal). Flat sea-level
        /// areas - exactly 0 m in the data, around most of the pixel too - become water: that is
        /// how the sea comes (Copernicus); land that low is a single pixel here and there.</summary>
        public static IntPtr Decode(byte[] data)
        {
            uint[] px = WebP.Decode(data, out int w, out int h);
            const int n = DemSampler.Size;
            if (w != n || h != n)
                throw new FormatException($"terrain tile is {w}x{h}, not {n}x{n}");
            var zero = new bool[n * n];
            for (int i = 0; i < zero.Length; i++)
                zero[i] = (px[i] & 0xffffff) == 0x800000;
            IntPtr mem = Marshal.AllocHGlobal(n * n * 2);
            var q = (ushort*)mem;
            ushort seaLevel = DemSampler.Store(0.0);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    if (!zero[i])
                    {
                        uint p = px[i];
                        double height = ((p >> 16) & 0xff) * 256.0 + ((p >> 8) & 0xff) + (p & 0xff) / 256.0 - 32768.0;
                        q[i] = DemSampler.Store(height);
                        continue;
                    }
                    int around = 0, zeros = 0;
                    for (int yy = Math.Max(0, y - 1); yy <= Math.Min(n - 1, y + 1); yy++)
                    {
                        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(n - 1, x + 1); xx++)
                        {
                            if (xx == x && yy == y)
                                continue;
                            around++;
                            if (zero[yy * n + xx])
                                zeros++;
                        }
                    }
                    q[i] = zeros * 2 >= around ? (ushort)0 : seaLevel;
                }
            }
            return mem;
        }
    }
}
