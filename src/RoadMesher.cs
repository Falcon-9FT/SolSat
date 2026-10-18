// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Ribbons along lines (roads, their markings, the glow of lit streets) in a tile's local
    /// plane: east/north metres from the tile centre (TileFrame.Flat's Plane). Every point of a
    /// line gets two vertices; the shader (TileProject passes 4-6) moves them apart along the
    /// miter vector by the half width, plus a texel of the level drawn into for antialiasing,
    /// so the same mesh serves every level of the clipmap.
    /// </summary>
    sealed class RibbonMeshData : IPooledData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Color32> Colors = new List<Color32>();
        /// <summary>xy: miter vector (the normal over the cosine of half the turn), z: half
        /// width (m), w: side (-1 left, 1 right).</summary>
        public readonly List<Vector4> Ribbon = new List<Vector4>();
        /// <summary>x: metres along the line, y: dash length (0: solid), z: gap length, w: the
        /// line's length. Lit streets use y and z to say which ends fade out.</summary>
        public readonly List<Vector4> Dash = new List<Vector4>();
        public readonly List<int> Triangles = new List<int>();
        /// <summary>Extent of the ribbons, local metres.</summary>
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;

        /// <summary>Half width of a fill: wider than any texel, so all of it counts as covered.</summary>
        public const float FillWidth = 1e5f;
        /// <summary>Half width marking a fill's edge: a texel either side of the line, its left
        /// side covered, its right side not, antialiased between (TileProject's RoadFrag).</summary>
        public const float EdgeWidth = -1f;

        public int Count => Vertices.Count;

        public long HeldBytes => MeshPools.Bytes(Vertices, 12) + MeshPools.Bytes(Colors, 4) + MeshPools.Bytes(Ribbon, 16) +
                                 MeshPools.Bytes(Dash, 16) + MeshPools.Bytes(Triangles, 4);

        public static RibbonMeshData Rent() => DataPool<RibbonMeshData>.Rent();

        /// <summary>Emptied, as new.</summary>
        public void Reset()
        {
            Vertices.Clear();
            Colors.Clear();
            Ribbon.Clear();
            Dash.Clear();
            Triangles.Clear();
            MinX = MinY = double.MaxValue;
            MaxX = MaxY = double.MinValue;
        }

        /// <summary>Done with (Unity has its copy): emptied and kept for the next tile (MeshPools).</summary>
        public static void Recycle(RibbonMeshData d)
        {
            if (d == null)
                return;
            d.Reset();
            DataPool<RibbonMeshData>.Return(d);
        }

        /// <summary>Triangles filling a polygon (flat x, y pairs; triangles index them): its
        /// vertices stay where they are and every texel whose centre is inside counts as covered.
        /// Pair it with AddEdge along its rings for antialiased edges.</summary>
        public void AddFill(double[] xy, int count, List<int> triangles, Color32 colour)
        {
            int first = Vertices.Count;
            for (int i = 0; i < count; i++)
            {
                double x = xy[2 * i], y = xy[2 * i + 1];
                Vertices.Add(new Vector3((float)x, (float)y, 0f));
                Colors.Add(colour);
                Ribbon.Add(new Vector4(0f, 0f, FillWidth, 0f));
                Dash.Add(Vector4.zero);
                MinX = Math.Min(MinX, x);
                MinY = Math.Min(MinY, y);
                MaxX = Math.Max(MaxX, x);
                MaxY = Math.Max(MaxY, y);
            }
            foreach (int t in triangles)
                Triangles.Add(first + t);
        }

        /// <summary>The antialiased edge of a fill along one of its rings, which must have the
        /// fill on its left (outer rings anticlockwise, holes clockwise).</summary>
        public void AddEdge(List<double> ring, Color32 colour) => Add(ring, 0.0, EdgeWidth, colour);

        /// <summary>A ribbon along a line (flat x, y pairs), shifted `offset` metres to its
        /// right. Turns sharper than 120 degrees start a new ribbon rather than a long spike.</summary>
        public void Add(List<double> line, double offset, float halfWidth, Color32 colour, float dash = 0f, float gap = 0f)
        {
            int n = line.Count / 2;
            int start = 0, first = Vertices.Count;
            double along = 0.0;
            for (int i = 1; i < n - 1; i++)
            {
                Normal(line, i - 1, out double ax, out double ay);
                Normal(line, i, out double bx, out double by);
                if (ax * bx + ay * by < -0.5)
                {
                    along = Strip(line, start, i, offset, halfWidth, colour, dash, gap, along);
                    start = i;
                }
            }
            along = Strip(line, start, n - 1, offset, halfWidth, colour, dash, gap, along);
            for (int v = first; v < Vertices.Count; v++)
            {
                Vector4 d = Dash[v];
                d.w = (float)along;
                Dash[v] = d;
            }
        }

        /// <summary>Right-hand unit normal of segment i (x east, y north); zero if it has no length.</summary>
        static void Normal(List<double> line, int i, out double nx, out double ny)
        {
            double dx = line[2 * i + 2] - line[2 * i], dy = line[2 * i + 3] - line[2 * i + 1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6)
            {
                nx = ny = 0.0;
                return;
            }
            nx = dy / length;
            ny = -dx / length;
        }

        /// <summary>Points first..last as one mitered strip; returns the distance along at its end.</summary>
        double Strip(List<double> line, int first, int last, double offset, float halfWidth, Color32 colour, float dash, float gap, double along)
        {
            if (last <= first)
                return along;
            int v0 = Vertices.Count;
            double px = 0.0, py = 0.0;
            for (int i = first; i <= last; i++)
            {
                double mx, my;
                if (i == first)
                {
                    Normal(line, i, out mx, out my);
                }
                else if (i == last)
                {
                    Normal(line, i - 1, out mx, out my);
                }
                else
                {
                    Normal(line, i - 1, out double ax, out double ay);
                    Normal(line, i, out double bx, out double by);
                    double sx = ax + bx, sy = ay + by, s = Math.Sqrt(sx * sx + sy * sy);
                    if (s < 1e-6)
                    {
                        mx = ax;
                        my = ay;
                    }
                    else
                    {
                        sx /= s;
                        sy /= s;
                        // Across the turn the ribbon keeps its width: the miter is longer.
                        double cos = Math.Max(sx * ax + sy * ay, 0.5);
                        if (ax == 0.0 && ay == 0.0)
                            cos = 1.0;
                        mx = sx / cos;
                        my = sy / cos;
                    }
                }
                double x = line[2 * i] + mx * offset, y = line[2 * i + 1] + my * offset;
                if (i > first)
                    along += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                px = x;
                py = y;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vertices.Add(new Vector3((float)x, (float)y, 0f));
                    Colors.Add(colour);
                    Ribbon.Add(new Vector4((float)mx, (float)my, halfWidth, side));
                    Dash.Add(new Vector4((float)along, dash, gap, 0f));
                }
                double reach = Math.Max(halfWidth, 0f) * 2.0; // miters are at most twice as long
                MinX = Math.Min(MinX, x - reach);
                MinY = Math.Min(MinY, y - reach);
                MaxX = Math.Max(MaxX, x + reach);
                MaxY = Math.Max(MaxY, y + reach);
            }
            for (int i = 0; i < last - first; i++)
            {
                int a0 = v0 + 2 * i, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                Triangles.Add(a0);
                Triangles.Add(b0);
                Triangles.Add(a1);
                Triangles.Add(a1);
                Triangles.Add(b0);
                Triangles.Add(b1);
            }
            return along;
        }

        /// <summary>The stretches of a line of OSM points (flat lat,lon pairs in 1e-7 degrees)
        /// inside the tile and between `from` and `to` metres along it, in the local plane.
        /// Neighbouring tiles cut the same line at the same place, so their ribbons meet.
        /// `ends`, if given, gets each stretch's first and last metre along the line.</summary>
        public static void Runs(OsmTile tile, TileFrame frame, int[] p, double from, double to, List<List<double>> runs,
                                List<double> ends = null)
        {
            double south = tile.South, north = tile.North, west = tile.West, east = tile.East;
            List<double> run = null;
            double travelled = 0.0;
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                double lat0 = p[i] * 1e-7, lon0 = p[i + 1] * 1e-7, lat1 = p[i + 2] * 1e-7, lon1 = p[i + 3] * 1e-7;
                Point(frame, lat0, lon0, out double x0, out double y0);
                Point(frame, lat1, lon1, out double x1, out double y1);
                double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (length < 0.01)
                    continue;
                double t0 = Math.Max(0.0, (from - travelled) / length), t1 = Math.Min(1.0, (to - travelled) / length);
                travelled += length;
                Clip(lat0, lat1 - lat0, south, north, ref t0, ref t1);
                Clip(lon0, lon1 - lon0, west, east, ref t0, ref t1);
                if (t1 - t0 < 1e-9)
                {
                    run = null;
                    continue;
                }
                if (run == null || t0 > 1e-9)
                {
                    run = new List<double> { x0 + (x1 - x0) * t0, y0 + (y1 - y0) * t0 };
                    runs.Add(run);
                    ends?.Add(travelled - length + t0 * length);
                    ends?.Add(0.0);
                }
                run.Add(x0 + (x1 - x0) * t1);
                run.Add(y0 + (y1 - y0) * t1);
                if (ends != null)
                    ends[ends.Count - 1] = travelled - length + t1 * length;
                if (t1 < 1.0 - 1e-9)
                    run = null;
            }
        }

        /// <summary>Liang-Barsky: narrows [t0, t1] to where v0 + dv t stays within [min, max].</summary>
        static void Clip(double v0, double dv, double min, double max, ref double t0, ref double t1)
        {
            if (Math.Abs(dv) < 1e-15)
            {
                if (v0 < min || v0 > max)
                    t1 = t0 - 1.0;
                return;
            }
            double a = (min - v0) / dv, b = (max - v0) / dv;
            if (a > b)
            {
                double s = a;
                a = b;
                b = s;
            }
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
        }

        public static void Point(TileFrame frame, double lat, double lon, out double x, out double y)
        {
            TileFrame.Direction(lat, lon, out double dx, out double dy, out double dz);
            frame.Plane(dx, dy, dz, 6371000.0, out x, out y);
        }
    }

    /// <summary>
    /// Roads and railways of a tile as ribbons for the clipmap's road layer (Clipmap.Roads):
    /// the paved surface as wide as its class and lanes make it, and on bigger roads the
    /// markings - edge lines, dashed lane lines, a centre line (yellow in the Americas); a
    /// railway as a bed of pale ballast with the dark track (sleepers and rails) along it.
    /// Colour channels say what a ribbon is (r: surface, g: its shade times r, b: white
    /// paint, a: yellow paint) and are combined by keeping the maximum, so overlaps and
    /// neighbouring tiles drawing the same line change nothing, and where a road crosses a
    /// railway the darker track stays on top. Thread-safe.
    ///
    /// Shades (the terrain shader's SolSatRoads turns them into colours): 0.05 ballast,
    /// 0.15 (light concrete) .. 0.9 (dark asphalt) roads, 1 railway track.
    /// </summary>
    static class RoadMesher
    {
        public enum Centre { None, Dashed, Solid }

        public struct Style
        {
            public double Width, LaneWidth;
            public int Lanes;          // lanes of paint to draw (0: no markings)
            public float Shade;        // 1 dark asphalt .. 0 light concrete
            public bool Edges, OneWay;
            public Centre Centre;
        }

        const float Paint = 0.2f;        // half width of painted lines: wider than real, or a metre-texel loses them
        const float Ballast = 0.05f, Track = 1f;
        static readonly Color32 White = new Color32(0, 0, 255, 0), Yellow = new Color32(0, 0, 0, 255);

        /// <summary>How a road looks. Vector tiles don't say how many lanes a road has, so the
        /// usual count for its class is taken - more in the Americas, whose city streets and
        /// arterials are wider than Europe's.</summary>
        public static bool Styled(OsmRoad road, bool americas, out Style s)
        {
            int lanes = road.Lanes;
            s = default;
            switch (road.Kind)
            {
                case "motorway":
                    s = Lanes(lanes, americas ? 3 : 2, 3.7, 3.5, 0.9f, true, true, Centre.None);
                    break;
                case "trunk":
                    s = Lanes(lanes, 2, 3.6, 2.5, 0.88f, true, true, Centre.None);
                    break;
                case "motorway_link":
                case "trunk_link":
                    s = Lanes(lanes, 1, 3.7, 2.5, 0.86f, true, true, Centre.None);
                    break;
                case "primary":
                    s = Lanes(lanes, americas ? 4 : 2, 3.4, 1.5, 0.82f, true, false, Centre.Solid);
                    break;
                case "secondary":
                    s = Lanes(lanes, americas ? 3 : 2, 3.3, 1.2, 0.78f, true, false, Centre.Solid);
                    break;
                case "tertiary":
                    s = Lanes(lanes, 2, 3.2, americas ? 2.0 : 0.6, 0.74f, false, false, Centre.Dashed);
                    break;
                case "primary_link":
                case "secondary_link":
                case "tertiary_link":
                    s = Lanes(lanes, 1, 3.4, 1.5, 0.78f, true, true, Centre.None);
                    break;
                case "busway":
                    s = Lanes(lanes, 2, 3.3, 0.6, 0.78f, false, false, Centre.Dashed);
                    break;
                case "minor":
                case "residential":
                case "unclassified":
                    s = new Style { Width = americas ? 8.5 : 6.0, Shade = 0.68f };
                    break;
                case "living_street":
                    s = new Style { Width = 5.5, Shade = 0.6f };
                    break;
                // Service roads (driveways, parking aisles, alleys) would criss-cross every car
                // park and yard; the imagery shows them well enough.
                case "pedestrian":
                    s = new Style { Width = 5.0, Shade = 0.2f };
                    break;
                default:
                    return false;
            }
            return true;
        }

        static Style Lanes(int lanes, int fallback, double laneWidth, double shoulders, float shade, bool edges, bool oneWay, Centre centre)
        {
            int n = lanes > 0 ? Math.Min(lanes, 8) : fallback;
            return new Style
            {
                Width = n * laneWidth + shoulders, LaneWidth = laneWidth, Lanes = n, Shade = shade,
                Edges = edges, OneWay = oneWay, Centre = centre,
            };
        }

        public static RibbonMeshData Build(OsmTile tile)
        {
            TileFrame frame = TileFrame.Flat(tile);
            RibbonMeshData mesh = RibbonMeshData.Rent();
            double lon = OsmTile.LonOf(tile.X + 0.5);
            bool americas = lon < -30.0 && lon > -170.0;
            float dash = americas ? 3f : 4.5f, gap = americas ? 9f : 7.5f;
            var runs = new List<List<double>>();
            // Lesser roads first; all channels keep their maximum anyway.
            var roads = new List<OsmRoad>(tile.RoadList);
            roads.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));
            foreach (OsmRoad road in roads)
            {
                if (road.Points == null || road.Points.Length < 4)
                    continue;
                if (Railway(road.Kind, out double bed, out double track))
                {
                    runs.Clear();
                    RibbonMeshData.Runs(tile, frame, road.Points, 0.0, double.MaxValue, runs);
                    foreach (List<double> line in runs)
                    {
                        if (line.Count < 4)
                            continue;
                        mesh.Add(line, 0.0, (float)(bed * 0.5), new Color32(255, (byte)(Ballast * 255f), 0, 0));
                        mesh.Add(line, 0.0, (float)(track * 0.5), new Color32(255, (byte)(Track * 255f), 0, 0));
                    }
                    continue;
                }
                if (!Styled(road, americas, out Style s))
                    continue;
                runs.Clear();
                RibbonMeshData.Runs(tile, frame, road.Points, 0.0, double.MaxValue, runs);
                var surface = new Color32(255, (byte)(s.Shade * 255f), 0, 0);
                foreach (List<double> line in runs)
                {
                    if (line.Count < 4)
                        continue;
                    mesh.Add(line, 0.0, (float)(s.Width * 0.5), surface);
                    if (s.Lanes == 0)
                        continue;
                    double paint = s.Lanes * s.LaneWidth * 0.5;   // the painted part, shoulders outside
                    if (s.Edges)
                    {
                        // On divided roads in the Americas the left edge line is yellow.
                        mesh.Add(line, -paint, Paint, s.OneWay && americas ? Yellow : White);
                        mesh.Add(line, paint, Paint, White);
                    }
                    if (s.OneWay)
                    {
                        for (int k = 1; k < s.Lanes; k++)
                            mesh.Add(line, -paint + k * s.LaneWidth, Paint, White, dash, gap);
                        continue;
                    }
                    // Two-way: the centre line, then lane lines on each side.
                    if (s.Centre != Centre.None)
                    {
                        bool solid = s.Centre == Centre.Solid && americas;
                        mesh.Add(line, 0.0, Paint, americas ? Yellow : White, solid ? 0f : dash, solid ? 0f : gap);
                    }
                    for (int k = 1; k < s.Lanes / 2; k++)
                    {
                        mesh.Add(line, -k * s.LaneWidth, Paint, White, dash, gap);
                        mesh.Add(line, k * s.LaneWidth, Paint, White, dash, gap);
                    }
                }
            }
            return mesh;
        }

        /// <summary>Widths of a railway's ballast bed and of its track (the sleepers, rails on
        /// them), metres. Trams run in the street, on rails the imagery shows; they are left out.</summary>
        public static bool Railway(string kind, out double bed, out double track)
        {
            switch (kind)
            {
                case "rail":
                    bed = 4.8;
                    track = 2.6;
                    return true;
                case "rail_siding":
                case "light_rail":
                case "subway":
                    bed = 4.0;
                    track = 2.5;
                    return true;
                case "narrow_gauge":
                    bed = 3.4;
                    track = 1.9;
                    return true;
                default:
                    bed = track = 0.0;
                    return false;
            }
        }

        public static int Rank(string kind)
        {
            switch (kind)
            {
                case "motorway": return 9;
                case "trunk": return 8;
                case "primary": return 7;
                case "secondary": return 6;
                case "tertiary": return 5;
                default: return kind.EndsWith("_link", StringComparison.Ordinal) ? 6 : 1;
            }
        }
    }
}
