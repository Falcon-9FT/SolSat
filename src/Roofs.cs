// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;

namespace SolSatellite
{
    /// <summary>A roof plane: height above the eaves h = A x + B y + C (x east, y north, metres).</summary>
    struct RoofPlane
    {
        public double A, B, C;

        public double At(double x, double y) => A * x + B * y + C;
    }

    enum RoofShape : byte { Flat, Gabled, Hipped, HalfHipped, Pyramidal, Skillion, Gambrel, Mansard, Round, Dome, Onion }

    /// <summary>A footprint's oriented bounding rectangle: centre, long axis (unit) and half sizes.</summary>
    struct RoofBox
    {
        public double Cx, Cy, Ux, Uy, Half, HalfWidth;
        public double Vx => -Uy;
        public double Vy => Ux;
    }

    /// <summary>
    /// Pitched roofs as the lower envelope of planes over a footprint: where several planes
    /// cover a point, the roof is the lowest of them. Planes rising from the eaves inward make
    /// gables, hips and pyramids; two slopes per side mansard and gambrel roofs; tangent planes
    /// of a curve barrel vaults and domes. A face is the part of the footprint where its plane
    /// is the lowest - the footprint clipped by one half-plane per other plane - and a wall's
    /// top follows the envelope along the wall, so gables reach up to the ridge. Coordinates
    /// are metres on the tile's ground plane. Thread-safe.
    /// </summary>
    static class Roofs
    {
        public static RoofShape Parse(string tag)
        {
            switch (tag)
            {
                case null: case "": case "flat": case "sawtooth": case "butterfly": return RoofShape.Flat;
                case "gabled": case "saltbox": case "gabled_row": case "side_hipped": return RoofShape.Gabled;
                case "hipped": case "crosspitched": case "quadruple_saltbox": case "side_half-hipped": return RoofShape.Hipped;
                case "half-hipped": case "half_hipped": return RoofShape.HalfHipped;
                case "pyramidal": case "cone": case "conical": return RoofShape.Pyramidal;
                case "skillion": case "lean_to": case "shed": return RoofShape.Skillion;
                case "gambrel": return RoofShape.Gambrel;
                case "mansard": return RoofShape.Mansard;
                case "round": case "barrel": case "arched": return RoofShape.Round;
                case "dome": return RoofShape.Dome;
                case "onion": return RoofShape.Onion;
                default: return RoofShape.Flat;
            }
        }

        // ------------------------------------------------------------------ footprint

        [ThreadStatic] static int[] hullOrder;

        /// <summary>Convex hull of n points (flat x,y pairs), counter-clockwise, into hull; its
        /// point count. Andrew's monotone chain. The points are sorted in this thread's index
        /// array, without a comparer: an array, a closure and a delegate for every building
        /// were ~2 MB of garbage for a city tile, at every detail band (2026-10-03).</summary>
        public static int Hull(double[] pts, int n, double[] hull)
        {
            int[] order = hullOrder;
            if (order == null || order.Length < n)
                hullOrder = order = new int[Math.Max(n, 64)];
            for (int i = 0; i < n; i++)
                order[i] = i;
            SortByXY(order, n, pts);
            int k = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                int start = k;
                for (int o = 0; o < n; o++)
                {
                    int i = order[pass == 0 ? o : n - 1 - o];
                    double x = pts[2 * i], y = pts[2 * i + 1];
                    while (k >= start + 2 && Cross(hull[2 * k - 4], hull[2 * k - 3], hull[2 * k - 2], hull[2 * k - 1], x, y) <= 1e-9)
                        k--;
                    hull[2 * k] = x;
                    hull[2 * k + 1] = y;
                    k++;
                }
                k--; // the last point starts the other chain
            }
            return Math.Max(k, 0);
        }

        static double Cross(double ax, double ay, double bx, double by, double cx, double cy) =>
            (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        /// <summary>Point a before point b: by x, then y (points equal both ways are the same
        /// point, so their order doesn't change the hull).</summary>
        static bool Before(double[] p, int a, int b)
        {
            double ax = p[2 * a], bx = p[2 * b];
            if (ax != bx)
                return ax < bx;
            return p[2 * a + 1] < p[2 * b + 1];
        }

        /// <summary>Sorts the first n indices by their points (Before): insertion sort for the
        /// few points most outlines have, heapsort for more.</summary>
        static void SortByXY(int[] o, int n, double[] p)
        {
            if (n <= 32)
            {
                for (int i = 1; i < n; i++)
                {
                    int v = o[i], j = i - 1;
                    while (j >= 0 && Before(p, v, o[j]))
                    {
                        o[j + 1] = o[j];
                        j--;
                    }
                    o[j + 1] = v;
                }
                return;
            }
            for (int i = n / 2 - 1; i >= 0; i--)
                SiftDown(o, i, n, p);
            for (int end = n - 1; end > 0; end--)
            {
                int t = o[0];
                o[0] = o[end];
                o[end] = t;
                SiftDown(o, 0, end, p);
            }
        }

        static void SiftDown(int[] o, int root, int n, double[] p)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= n)
                    return;
                if (child + 1 < n && Before(p, o[child], o[child + 1]))
                    child++;
                if (!Before(p, o[root], o[child]))
                    return;
                int t = o[root];
                o[root] = o[child];
                o[child] = t;
                root = child;
            }
        }

        /// <summary>Drops hull points that hardly bend it (within `tolerance` m of the line
        /// through their neighbours): noise in the outline shouldn't make roof facets.</summary>
        public static int Simplify(double[] hull, int h, double tolerance)
        {
            bool changed = true;
            while (changed && h > 3)
            {
                changed = false;
                for (int i = 0; i < h && h > 3; i++)
                {
                    int a = (i + h - 1) % h, c = (i + 1) % h;
                    double ax = hull[2 * a], ay = hull[2 * a + 1], cx = hull[2 * c], cy = hull[2 * c + 1];
                    double len = Math.Sqrt((cx - ax) * (cx - ax) + (cy - ay) * (cy - ay));
                    if (len < 1e-6 || Math.Abs(Cross(ax, ay, hull[2 * i], hull[2 * i + 1], cx, cy)) / len < tolerance)
                    {
                        Array.Copy(hull, 2 * (i + 1), hull, 2 * i, 2 * (h - i - 1));
                        h--;
                        changed = true;
                        i--;
                    }
                }
            }
            return h;
        }

        public static double Area(double[] pts, int start, int n)
        {
            double sum = 0.0;
            for (int i = 0, j = n - 1; i < n; j = i++)
                sum += pts[2 * (start + j)] * pts[2 * (start + i) + 1] - pts[2 * (start + i)] * pts[2 * (start + j) + 1];
            return 0.5 * sum;
        }

        /// <summary>Smallest rectangle around a convex hull (one side lies on a hull edge),
        /// its long axis U.</summary>
        public static RoofBox Box(double[] hull, int h)
        {
            var best = new RoofBox();
            double bestArea = double.MaxValue;
            for (int i = 0; i < h; i++)
            {
                int j = (i + 1) % h;
                double ux = hull[2 * j] - hull[2 * i], uy = hull[2 * j + 1] - hull[2 * i + 1];
                double len = Math.Sqrt(ux * ux + uy * uy);
                if (len < 1e-6)
                    continue;
                ux /= len;
                uy /= len;
                Extents(hull, h, ux, uy, out double cu, out double cv, out double hu, out double hv);
                double area = hu * hv;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = new RoofBox { Ux = ux, Uy = uy, Half = hu, HalfWidth = hv, Cx = ux * cu - uy * cv, Cy = uy * cu + ux * cv };
                }
            }
            if (best.HalfWidth > best.Half)
                best = new RoofBox { Ux = best.Vx, Uy = best.Vy, Half = best.HalfWidth, HalfWidth = best.Half, Cx = best.Cx, Cy = best.Cy };
            return best;
        }

        /// <summary>Middle and half extents of points along axis u and the axis left of it.</summary>
        static void Extents(double[] pts, int n, double ux, double uy, out double cu, out double cv, out double hu, out double hv)
        {
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double x = pts[2 * k], y = pts[2 * k + 1];
                double pu = x * ux + y * uy, pv = -x * uy + y * ux;
                minU = Math.Min(minU, pu);
                maxU = Math.Max(maxU, pu);
                minV = Math.Min(minV, pv);
                maxV = Math.Max(maxV, pv);
            }
            cu = 0.5 * (minU + maxU);
            cv = 0.5 * (minV + maxV);
            hu = 0.5 * (maxU - minU);
            hv = 0.5 * (maxV - minV);
        }

        // ------------------------------------------------------------------ planes

        /// <summary>A plane rising with `slope` from the line through (ox, oy) in direction m
        /// (unit, pointing inward), h0 high on that line.</summary>
        static RoofPlane Rising(double ox, double oy, double mx, double my, double slope, double h0) =>
            new RoofPlane { A = slope * mx, B = slope * my, C = h0 - slope * (ox * mx + oy * my) };

        /// <summary>
        /// The planes of a roof `height` high over a footprint with this hull and box. Ridges run
        /// along the box's long axis, or across it (roof:orientation=across), or square to the
        /// direction the slopes face when that is tagged (degrees from north); a skillion falls
        /// towards that direction. Nothing for flat roofs.
        /// </summary>
        public static void Planes(RoofShape shape, double[] hull, int h, RoofBox box, double height, float direction, bool across,
                                  List<RoofPlane> planes)
        {
            planes.Clear();
            if (shape == RoofShape.Flat || h < 3 || height <= 0.0)
                return;
            // Ridge axis r, across it q, and the footprint's half extents along them.
            double rx = box.Ux, ry = box.Uy;
            if (!float.IsNaN(direction) && shape != RoofShape.Skillion)
            {
                double a = direction * Math.PI / 180.0;
                double fx = Math.Sin(a), fy = Math.Cos(a); // the way the slope faces (east, north)
                // Square to that, unless it is nearly the box's own axes (mapped directions are rough).
                double along = Math.Abs(fx * box.Vx + fy * box.Vy), acrossBox = Math.Abs(fx * box.Ux + fy * box.Uy);
                if (along > 0.94)
                {
                    rx = box.Ux;
                    ry = box.Uy;
                }
                else if (acrossBox > 0.94)
                {
                    rx = box.Vx;
                    ry = box.Vy;
                }
                else
                {
                    rx = fy;
                    ry = -fx;
                }
            }
            else if (across)
            {
                rx = box.Vx;
                ry = box.Vy;
            }
            double qx = -ry, qy = rx;
            Extents(hull, h, rx, ry, out double cr, out double cq, out double halfR, out double halfQ);
            double cx = rx * cr + qx * cq, cy = ry * cr + qy * cq;
            if (halfQ < 0.5 || halfR < 0.5)
                return;
            double s = height / halfQ;
            switch (shape)
            {
                case RoofShape.Gabled:
                    Sides(planes, cx, cy, qx, qy, halfQ, s, 0.0);
                    break;
                case RoofShape.Hipped:
                    Sides(planes, cx, cy, qx, qy, halfQ, s, 0.0);
                    Sides(planes, cx, cy, rx, ry, halfR, s, 0.0);
                    break;
                case RoofShape.HalfHipped:
                {
                    Sides(planes, cx, cy, qx, qy, halfQ, s, 0.0);
                    // Hips over the upper part of the gables only.
                    double run = Math.Min(halfQ * 0.5, halfR * 0.4);
                    Sides(planes, cx, cy, rx, ry, halfR, 0.45 * height / run, 0.55 * height);
                    break;
                }
                case RoofShape.Gambrel:
                    Broken(planes, cx, cy, qx, qy, halfQ, height, 60.0, 0.6);
                    break;
                case RoofShape.Mansard:
                    Broken(planes, cx, cy, qx, qy, halfQ, height, 70.0, 0.8);
                    Broken(planes, cx, cy, rx, ry, halfR, height, 70.0, 0.8, halfQ);
                    break;
                case RoofShape.Round:
                    Vault(planes, cx, cy, qx, qy, halfQ, height);
                    break;
                case RoofShape.Pyramidal:
                    Pyramid(planes, hull, h, height);
                    break;
                case RoofShape.Skillion:
                {
                    double dx = box.Vx, dy = box.Vy;
                    if (!float.IsNaN(direction))
                    {
                        double a = direction * Math.PI / 180.0;
                        dx = Math.Sin(a);
                        dy = Math.Cos(a);
                    }
                    Extents(hull, h, dx, dy, out double cd, out _, out double halfD, out _);
                    if (halfD < 0.5)
                        return;
                    // Highest on the side away from the direction it falls towards.
                    double k = height / (2.0 * halfD);
                    planes.Add(new RoofPlane { A = -k * dx, B = -k * dy, C = k * (cd + halfD) });
                    break;
                }
                case RoofShape.Dome:
                case RoofShape.Onion:
                    Dome(planes, hull, h, height);
                    break;
            }
        }

        /// <summary>A plane up from each of the two sides square to axis (mx, my), half apart.</summary>
        static void Sides(List<RoofPlane> planes, double cx, double cy, double mx, double my, double half, double slope, double h0)
        {
            planes.Add(Rising(cx - half * mx, cy - half * my, mx, my, slope, h0));
            planes.Add(Rising(cx + half * mx, cy + half * my, -mx, -my, slope, h0));
        }

        /// <summary>Two slopes per side: steep (`steep` degrees) up to `share` of the height,
        /// then shallow up to the ridge. reach: how far in the shallow part goes (the other
        /// axis' half for the ends of a mansard).</summary>
        static void Broken(List<RoofPlane> planes, double cx, double cy, double mx, double my, double half, double height,
                           double steep, double share, double reach = 0.0)
        {
            double h1 = share * height;
            double d1 = Math.Min(h1 / Math.Tan(steep * Math.PI / 180.0), 0.45 * half);
            double run = (reach > 0.0 ? Math.Min(reach, half) : half) - d1;
            double s2 = run > 0.1 ? (height - h1) / run : 0.0;
            Sides(planes, cx, cy, mx, my, half, h1 / d1, 0.0);
            Sides(planes, cx, cy, mx, my, half, s2, h1 - s2 * d1);
        }

        /// <summary>A barrel vault across axis (mx, my): tangent planes of a half ellipse.</summary>
        static void Vault(List<RoofPlane> planes, double cx, double cy, double mx, double my, double half, double height)
        {
            planes.Add(new RoofPlane { C = height });
            foreach (double t0 in new[] { 0.98, 0.88, 0.7, 0.45, 0.2 })
            {
                double h0 = height * Math.Sqrt(1.0 - t0 * t0), slope = height * t0 / Math.Sqrt(1.0 - t0 * t0) / half;
                // Falling outward from the tangent point on either side.
                for (int side = -1; side <= 1; side += 2)
                {
                    double px = cx + side * t0 * half * mx, py = cy + side * t0 * half * my;
                    planes.Add(Rising(px, py, -side * mx, -side * my, slope, h0));
                }
            }
        }

        /// <summary>Faces from every hull edge up to one apex over the hull's centroid.</summary>
        static void Pyramid(List<RoofPlane> planes, double[] hull, int h, double height)
        {
            Centroid(hull, h, out double gx, out double gy);
            for (int i = 0; i < h; i++)
            {
                int j = (i + 1) % h;
                double ex = hull[2 * j] - hull[2 * i], ey = hull[2 * j + 1] - hull[2 * i + 1];
                double len = Math.Sqrt(ex * ex + ey * ey);
                if (len < 0.3)
                    continue;
                double mx = -ey / len, my = ex / len; // inward for a counter-clockwise hull
                double d = (gx - hull[2 * i]) * mx + (gy - hull[2 * i + 1]) * my;
                if (d > 0.3)
                    planes.Add(Rising(hull[2 * i], hull[2 * i + 1], mx, my, height / d, 0.0));
            }
        }

        /// <summary>A dome over the hull's centroid, as wide as its farthest corner: tangent
        /// planes of a half ellipsoid in 12 directions and 4 rings, and a flat top.</summary>
        static void Dome(List<RoofPlane> planes, double[] hull, int h, double height)
        {
            Centroid(hull, h, out double gx, out double gy);
            double radius = 0.0;
            for (int i = 0; i < h; i++)
                radius = Math.Max(radius, Math.Sqrt((hull[2 * i] - gx) * (hull[2 * i] - gx) + (hull[2 * i + 1] - gy) * (hull[2 * i + 1] - gy)));
            if (radius < 0.5)
                return;
            planes.Add(new RoofPlane { C = height });
            foreach (double t0 in new[] { 0.97, 0.85, 0.6, 0.3 })
            {
                double h0 = height * Math.Sqrt(1.0 - t0 * t0), slope = height * t0 / Math.Sqrt(1.0 - t0 * t0) / radius;
                for (int k = 0; k < 12; k++)
                {
                    double a = k * Math.PI / 6.0, ux = Math.Cos(a), uy = Math.Sin(a);
                    planes.Add(Rising(gx + t0 * radius * ux, gy + t0 * radius * uy, -ux, -uy, slope, h0));
                }
            }
        }

        static void Centroid(double[] pts, int n, out double gx, out double gy)
        {
            double a = 0.0, x = 0.0, y = 0.0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double c = pts[2 * j] * pts[2 * i + 1] - pts[2 * i] * pts[2 * j + 1];
                a += c;
                x += (pts[2 * j] + pts[2 * i]) * c;
                y += (pts[2 * j + 1] + pts[2 * i + 1]) * c;
            }
            if (Math.Abs(a) < 1e-9)
            {
                gx = pts[0];
                gy = pts[1];
                return;
            }
            gx = x / (3.0 * a);
            gy = y / (3.0 * a);
        }

        /// <summary>Lowest envelope height at a point.</summary>
        public static double At(List<RoofPlane> planes, double x, double y)
        {
            double h = double.MaxValue;
            foreach (RoofPlane p in planes)
                h = Math.Min(h, p.At(x, y));
            return h;
        }

        /// <summary>Raises all planes so the envelope is nowhere below the eaves over these
        /// points (the outline may poke out of a simplified hull a little).</summary>
        public static void Lift(List<RoofPlane> planes, double[] pts, int start, int n)
        {
            if (planes.Count == 0)
                return;
            double low = 0.0;
            for (int i = start; i < start + n; i++)
                low = Math.Min(low, At(planes, pts[2 * i], pts[2 * i + 1]));
            if (low >= 0.0)
                return;
            for (int i = 0; i < planes.Count; i++)
            {
                RoofPlane p = planes[i];
                p.C -= low;
                planes[i] = p;
            }
        }

        // ------------------------------------------------------------------ envelope

        /// <summary>
        /// The envelope along a wall from a to b: breakpoints as parameters t (0..1) and heights,
        /// where the lowest plane changes (a gable's peak, a mansard's break); their count.
        /// </summary>
        public static int Profile(List<RoofPlane> planes, double ax, double ay, double bx, double by, double[] ts, double[] hs)
        {
            int n = planes.Count;
            int cur = -1;
            double best = double.MaxValue, bestSlope = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double h0 = planes[i].At(ax, ay), slope = planes[i].At(bx, by) - h0;
                if (h0 < best - 1e-9 || h0 < best + 1e-9 && slope < bestSlope)
                {
                    best = h0;
                    bestSlope = slope;
                    cur = i;
                }
            }
            int count = 0;
            double t = 0.0;
            ts[count] = 0.0;
            hs[count++] = best;
            double alpha = best, beta = bestSlope;
            for (int guard = 0; guard < n + 1; guard++)
            {
                int next = -1;
                double nextT = 1.0, nextSlope = 0.0, nextAlpha = 0.0;
                for (int j = 0; j < n; j++)
                {
                    if (j == cur)
                        continue;
                    double a = planes[j].At(ax, ay), b = planes[j].At(bx, by) - a;
                    if (b >= beta - 1e-12)
                        continue; // never gets below the current one further on
                    double x = (a - alpha) / (beta - b);
                    if (x > t + 1e-9 && (x < nextT - 1e-12 || Math.Abs(x - nextT) <= 1e-12 && b < nextSlope))
                    {
                        nextT = x;
                        next = j;
                        nextSlope = b;
                        nextAlpha = a;
                    }
                }
                if (next < 0 || nextT >= 1.0)
                    break;
                ts[count] = nextT;
                hs[count++] = alpha + beta * nextT;
                t = nextT;
                cur = next;
                alpha = nextAlpha;
                beta = nextSlope;
            }
            ts[count] = 1.0;
            hs[count++] = alpha + beta;
            return count;
        }

        /// <summary>The face of plane i: the part of a ring (flat x,y, count points) where it is
        /// the lowest plane, as flat x,y into face; false if there is none to speak of.</summary>
        public static bool Face(List<RoofPlane> planes, int i, double[] ring, int start, int count, List<double> face, List<double> work)
        {
            face.Clear();
            for (int k = start; k < start + count; k++)
            {
                face.Add(ring[2 * k]);
                face.Add(ring[2 * k + 1]);
            }
            RoofPlane p = planes[i];
            for (int j = 0; j < planes.Count && face.Count >= 6; j++)
            {
                if (j == i)
                    continue;
                RoofPlane q = planes[j];
                double a = p.A - q.A, b = p.B - q.B, c = p.C - q.C;
                if (Math.Abs(a) < 1e-12 && Math.Abs(b) < 1e-12 && Math.Abs(c) < 1e-9)
                {
                    if (j < i)
                        return false; // the same plane twice: the first one has the face
                    continue;
                }
                ClipHalfPlane(face, a, b, c, work); // keep a x + b y + c <= 0
            }
            return face.Count >= 6 && Math.Abs(SignedArea(face)) > 0.01;
        }

        static void ClipHalfPlane(List<double> poly, double a, double b, double c, List<double> work)
        {
            work.Clear();
            int n = poly.Count / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + n - 1) % n;
                double px = poly[2 * j], py = poly[2 * j + 1], cx = poly[2 * i], cy = poly[2 * i + 1];
                double fp = a * px + b * py + c, fc = a * cx + b * cy + c;
                bool pin = fp <= 1e-9, cin = fc <= 1e-9;
                if (pin != cin)
                {
                    double t = fp / (fp - fc);
                    work.Add(px + (cx - px) * t);
                    work.Add(py + (cy - py) * t);
                }
                if (cin)
                {
                    work.Add(cx);
                    work.Add(cy);
                }
            }
            poly.Clear();
            poly.AddRange(work);
        }

        static double SignedArea(List<double> r)
        {
            double sum = 0.0;
            int n = r.Count / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
                sum += r[2 * j] * r[2 * i + 1] - r[2 * i] * r[2 * j + 1];
            return 0.5 * sum;
        }
    }
}
