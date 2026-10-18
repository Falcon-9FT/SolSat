// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;

namespace SolSatellite
{
    /// <summary>
    /// A tile of real terrain heights handed to the sampler: 512x512 heights (DemSampler.Real),
    /// and its eight neighbours at the same zoom (0: not loaded or no data) for sampling across
    /// its edges and for fading it out towards them. Plain data and pointers only: the
    /// sampler also runs inside BurstPQS's Burst-compiled jobs.
    /// </summary>
    public unsafe struct DemTileRef
    {
        public int Z, X, Y, Pad;
        public ushort* Data;
        /// <summary>Neighbours' data: (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1);
        /// y grows southwards, as in tile numbers.</summary>
        public fixed long N[8];
    }

    /// <summary>A place whose terrain stays Sol's own (KSC, launch sites, cities): fully inside
    /// CosInner, fading to the real relief by CosOuter (cosines of angles from the centre).</summary>
    public struct DemExclusion
    {
        public double X, Y, Z, CosInner, CosOuter;
    }

    public struct DemParams
    {
        public double Scale;    // game metres per real metre (Sol Quarter: 0.25)
        public double LandMin;  // land is lifted this far above the sea (game m): no flicker against the ocean
        public double WaterMax; // the sea floor is at least this deep (game m, negative)
        public double Blend;    // part of a tile over which a zoom fades out where its neighbours aren't loaded
    }

    /// <summary>
    /// The terrain height of a point from real elevation tiles: Sol's own height (the PQS's,
    /// before this) overlaid by each zoom loaded there, coarse to fine, each fading out at the
    /// edge of what is loaded of it; Sol's own again around excluded places. Depends only on
    /// the point and the tiles given, so every quad gets the same height at a shared edge.
    /// </summary>
    public static unsafe class DemSampler
    {
        public const int Size = 512;
        /// <summary>Stored heights: (h - Offset) / Step + 1 in a ushort; 0 marks water.</summary>
        public const double Step = 0.25, Offset = -1000.0;
        const double MaxSinLat = 0.99627207622; // sin(85.0511287798 deg): the end of Web Mercator

        /// <summary>refs: sorted by zoom, then Y, then X. hSol: the height above the sphere so far (game m).</summary>
        public static double Height(double hSol, double dx, double dy, double dz, DemTileRef* refs, int count,
                                    DemExclusion* exclusions, int exclusionCount, ref DemParams p)
        {
            double keep = 0.0;
            for (int i = 0; i < exclusionCount; i++)
            {
                DemExclusion e = exclusions[i];
                double c = dx * e.X + dy * e.Y + dz * e.Z;
                if (c >= e.CosInner)
                    return hSol;
                if (c > e.CosOuter)
                {
                    double t = (c - e.CosOuter) / (e.CosInner - e.CosOuter);
                    t = t * t * (3.0 - 2.0 * t);
                    if (t > keep)
                        keep = t;
                }
            }
            if (count == 0 || dy > MaxSinLat || dy < -MaxSinLat)
                return hSol;
            // Web Mercator, 0..1 across the world.
            double mx = Math.Atan2(dz, dx) * (0.5 / Math.PI) + 0.5;
            double my = 0.5 - Math.Log((1.0 + dy) / (1.0 - dy)) * (0.25 / Math.PI);
            double h = hSol;
            int start = 0;
            while (start < count)
            {
                int z = refs[start].Z, end = start + 1;
                while (end < count && refs[end].Z == z)
                    end++;
                double n = 1L << z;
                double fx = mx * n, fy = my * n;
                int tx = (int)Math.Floor(fx), ty = (int)Math.Floor(fy);
                double u = fx - tx, v = fy - ty;
                int nn = 1 << z;
                if (tx >= nn)
                    tx -= nn;
                else if (tx < 0)
                    tx += nn;
                DemTileRef* r = Find(refs, start, end, tx, ty);
                if (r != null)
                {
                    double w = Weight(r, u, v, p.Blend);
                    if (w > 0.0)
                        h += (Sample(r, u, v, hSol, ref p) - h) * w;
                }
                start = end;
            }
            return h + (hSol - h) * keep;
        }

        /// <summary>
        /// The height after the terrain patches (TerrainPatches): h (game m above the sphere,
        /// after the real relief) pulled to their targets by their weights, the targets averaged
        /// by weight where patches overlap. Not inside the places kept as Sol has them, fading in
        /// around them as the real relief does.
        /// </summary>
        public static double Patch(double h, double dx, double dy, double dz, DemPatch* patches, int count,
                                   DemExclusion* exclusions, int exclusionCount, double sphereRadius)
        {
            if (count == 0)
                return h;
            double keep = 0.0;
            for (int i = 0; i < exclusionCount; i++)
            {
                DemExclusion e = exclusions[i];
                double c = dx * e.X + dy * e.Y + dz * e.Z;
                if (c >= e.CosInner)
                    return h;
                if (c > e.CosOuter)
                {
                    double t = (c - e.CosOuter) / (e.CosInner - e.CosOuter);
                    t = t * t * (3.0 - 2.0 * t);
                    if (t > keep)
                        keep = t;
                }
            }
            double sumW = 0.0, sumWT = 0.0;
            for (int i = 0; i < count; i++)
            {
                DemPatch* p = patches + i;
                double u = dx * p->Ux + dy * p->Uy + dz * p->Uz;
                if (u <= 0.0 || u < p->MinUp)
                    continue;
                double fx = ((dx * p->Ex + dy * p->Ey + dz * p->Ez) * p->PlaneRadius / u - p->X0) * p->InvStep;
                double fy = ((dx * p->Nx + dy * p->Ny + dz * p->Nz) * p->PlaneRadius / u - p->Y0) * p->InvStep;
                if (fx < 0.0 || fy < 0.0 || fx > p->Cols - 1 || fy > p->Rows - 1)
                    continue;
                int ix = (int)fx, iy = (int)fy;
                if (ix > p->Cols - 2)
                    ix = p->Cols - 2;
                if (iy > p->Rows - 2)
                    iy = p->Rows - 2;
                double tx = fx - ix, ty = fy - iy;
                int k = iy * p->Cols + ix;
                double w = Bilerp(p->Weight, k, p->Cols, tx, ty);
                if (w <= 0.0)
                    continue;
                sumW += w;
                sumWT += w * (Bilerp(p->Height, k, p->Cols, tx, ty) + (p->PlaneRadius - sphereRadius));
            }
            if (sumW <= 0.0)
                return h;
            double weight = (sumW < 1.0 ? sumW : 1.0) * (1.0 - keep);
            return h + (sumWT / sumW - h) * weight;
        }

        /// <summary>The weight one patch has in a direction, as Patch takes it (0 off its grid).</summary>
        public static double PatchWeight(double dx, double dy, double dz, DemPatch* p)
        {
            double u = dx * p->Ux + dy * p->Uy + dz * p->Uz;
            if (u <= 0.0 || u < p->MinUp)
                return 0.0;
            double fx = ((dx * p->Ex + dy * p->Ey + dz * p->Ez) * p->PlaneRadius / u - p->X0) * p->InvStep;
            double fy = ((dx * p->Nx + dy * p->Ny + dz * p->Nz) * p->PlaneRadius / u - p->Y0) * p->InvStep;
            if (fx < 0.0 || fy < 0.0 || fx > p->Cols - 1 || fy > p->Rows - 1)
                return 0.0;
            int ix = (int)fx, iy = (int)fy;
            if (ix > p->Cols - 2)
                ix = p->Cols - 2;
            if (iy > p->Rows - 2)
                iy = p->Rows - 2;
            return Bilerp(p->Weight, iy * p->Cols + ix, p->Cols, fx - ix, fy - iy);
        }

        static double Bilerp(float* v, int k, int cols, double tx, double ty)
        {
            double bottom = v[k] + (v[k + 1] - v[k]) * tx;
            double top = v[k + cols] + (v[k + cols + 1] - v[k + cols]) * tx;
            return bottom + (top - bottom) * ty;
        }

        static DemTileRef* Find(DemTileRef* refs, int lo, int hi, int x, int y)
        {
            hi--;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                DemTileRef* r = refs + mid;
                int c = r->Y != y ? (r->Y < y ? -1 : 1) : r->X != x ? (r->X < x ? -1 : 1) : 0;
                if (c == 0)
                    return r;
                if (c < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return null;
        }

        /// <summary>1 inside the loaded tiles of a zoom, easing to 0 towards any neighbour that
        /// isn't loaded (sides and corners alike), so zooms blend without steps.</summary>
        static double Weight(DemTileRef* r, double u, double v, double blend)
        {
            double d = 2.0;
            if (r->N[3] == 0) d = Math.Min(d, u);
            if (r->N[4] == 0) d = Math.Min(d, 1.0 - u);
            if (r->N[1] == 0) d = Math.Min(d, v);
            if (r->N[6] == 0) d = Math.Min(d, 1.0 - v);
            if (r->N[0] == 0) d = Math.Min(d, Math.Sqrt(u * u + v * v));
            if (r->N[2] == 0) d = Math.Min(d, Math.Sqrt((1.0 - u) * (1.0 - u) + v * v));
            if (r->N[5] == 0) d = Math.Min(d, Math.Sqrt(u * u + (1.0 - v) * (1.0 - v)));
            if (r->N[7] == 0) d = Math.Min(d, Math.Sqrt((1.0 - u) * (1.0 - u) + (1.0 - v) * (1.0 - v)));
            if (d >= blend)
                return 1.0;
            double t = d / blend;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>Cubic B-spline over 4x4 pixels around the point, across into neighbours where
        /// loaded: a smooth surface (no creases between pixels) and a gentle low-pass about a
        /// pixel wide, so the PQS's vertices - about two pixels apart (Dem.MeshZoom) - don't pick
        /// single peaks and pits out of the data and turn slopes into spikes.</summary>
        static double Sample(DemTileRef* r, double u, double v, double hSol, ref DemParams p)
        {
            double px = u * Size - 0.5, py = v * Size - 0.5;
            int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
            BSpline(px - x0, out double wx0, out double wx1, out double wx2, out double wx3);
            BSpline(py - y0, out double wy0, out double wy1, out double wy2, out double wy3);
            return Row(r, x0, y0 - 1, wx0, wx1, wx2, wx3, hSol, ref p) * wy0 +
                   Row(r, x0, y0, wx0, wx1, wx2, wx3, hSol, ref p) * wy1 +
                   Row(r, x0, y0 + 1, wx0, wx1, wx2, wx3, hSol, ref p) * wy2 +
                   Row(r, x0, y0 + 2, wx0, wx1, wx2, wx3, hSol, ref p) * wy3;
        }

        static double Row(DemTileRef* r, int x0, int y, double w0, double w1, double w2, double w3, double hSol, ref DemParams p) =>
            Corner(r, x0 - 1, y, hSol, ref p) * w0 + Corner(r, x0, y, hSol, ref p) * w1 +
            Corner(r, x0 + 1, y, hSol, ref p) * w2 + Corner(r, x0 + 2, y, hSol, ref p) * w3;

        /// <summary>Uniform cubic B-spline weights for pixels -1, 0, 1, 2 at fraction t past pixel 0.</summary>
        static void BSpline(double t, out double w0, out double w1, out double w2, out double w3)
        {
            double t2 = t * t, t3 = t2 * t, s = 1.0 - t;
            w0 = s * s * s / 6.0;
            w1 = (3.0 * t3 - 6.0 * t2 + 4.0) / 6.0;
            w2 = (-3.0 * t3 + 3.0 * t2 + 3.0 * t + 1.0) / 6.0;
            w3 = t3 / 6.0;
        }

        static double Corner(DemTileRef* r, int x, int y, double hSol, ref DemParams p)
        {
            int sx = x < 0 ? -1 : x >= Size ? 1 : 0, sy = y < 0 ? -1 : y >= Size ? 1 : 0;
            ushort* data = r->Data;
            if (sx != 0 || sy != 0)
            {
                int k = sy < 0 ? sx + 1 : sy > 0 ? sx + 6 : sx < 0 ? 3 : 4;
                long other = r->N[k];
                if (other != 0)
                {
                    data = (ushort*)other;
                    x -= sx * Size;
                    y -= sy * Size;
                }
                else
                {
                    x = x < 0 ? 0 : x >= Size ? Size - 1 : x;
                    y = y < 0 ? 0 : y >= Size ? Size - 1 : y;
                }
            }
            return Ground(data[y * Size + x], hSol, ref p);
        }

        /// <summary>A stored height as terrain height (game m). Water stays under the sea, down to
        /// Sol's own sea floor; land below sea level stays land unless Sol has water there too
        /// (the Caspian, the Dead Sea).</summary>
        public static double Ground(ushort q, double hSol, ref DemParams p)
        {
            if (q == 0)
                return Math.Min(hSol, p.WaterMax);
            double g = ((q - 1) * Step + Offset) * p.Scale;
            if (g >= 0.0)
                return g + p.LandMin;
            return hSol < 0.0 ? Math.Min(g, p.WaterMax) : p.LandMin;
        }

        /// <summary>A real height (m) as stored.</summary>
        public static ushort Store(double h)
        {
            double q = Math.Round((h - Offset) / Step) + 1.0;
            return (ushort)(q < 1.0 ? 1.0 : q > 65535.0 ? 65535.0 : q);
        }

        /// <summary>The stored height at a pixel as real metres (NaN for water).</summary>
        public static double Real(ushort q) => q == 0 ? double.NaN : (q - 1) * Step + Offset;
    }
}
