// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>A 3D road vertex as the graphics card gets it: one interleaved stream (Layout),
    /// so Unity takes a tile's vertices in one copy (MeshUpload). See Road3DMesher for what the
    /// channels hold.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Road3DVertex
    {
        public Vector3 Position, Normal;
        public Vector4 Tangent;
        public Color32 Color;
        public Vector4 Uv0, Uv1, Uv2, Uv3;

        public const int Size = 108;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4),
        };
    }

    /// <summary>Vertex data of one tile's 3D roads and railways (Road3D.shader), in the tile's
    /// frame (TileFrame.For). Ground: what lies on the ground; Structures: bridges, which cast
    /// shadows. See Road3DMesher for what the vertex channels hold.</summary>
    sealed class Road3DMeshData : IPooledData
    {
        public readonly Part Ground = new Part(), Structures = new Part();
        public Vector3 Origin;
        public int Ways;

        public long HeldBytes => Ground.HeldBytes + Structures.HeldBytes;

        /// <summary>Kept data for about this many ground vertices (3 indices a vertex, as 3D roads
        /// have); with `reserve` (the count is a good guess) its lists made to hold them.</summary>
        public static Road3DMeshData Rent(int vertices, bool reserve)
        {
            Road3DMeshData d = DataPool<Road3DMeshData>.Rent(vertices * 124L);
            if (reserve)
                d.Ground.Reserve(vertices, vertices * 3);
            return d;
        }

        /// <summary>Done with (Unity has its copies): emptied and kept for the next tile (MeshPools).</summary>
        public static void Recycle(Road3DMeshData d)
        {
            if (d == null)
                return;
            d.Ground.Reset();
            d.Structures.Reset();
            d.Origin = default;
            d.Ways = 0;
            DataPool<Road3DMeshData>.Return(d);
        }

        public sealed class Part
        {
            public readonly List<Road3DVertex> Vertices = new List<Road3DVertex>();
            public readonly List<int> Triangles = new List<int>();
            public Vector3 Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            public Vector3 Max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            public int Count => Vertices.Count;

            public long HeldBytes => MeshPools.Bytes(Vertices, Road3DVertex.Size) + MeshPools.Bytes(Triangles, 4);

            public void Reserve(int vertices, int indices)
            {
                MeshPools.Reserve(Vertices, vertices);
                MeshPools.Reserve(Triangles, indices);
            }

            public void Reset()
            {
                Vertices.Clear();
                Triangles.Clear();
                Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            }

            public int Add(Vector3 p, Vector3 n, Vector3 along, Color32 c, Vector4 uv0, Vector4 uv1, Vector4 uv2, Vector4 uv3)
            {
                Vertices.Add(new Road3DVertex
                {
                    Position = p, Normal = n, Tangent = new Vector4(along.x, along.y, along.z, 1f), Color = c,
                    Uv0 = uv0, Uv1 = uv1, Uv2 = uv2, Uv3 = uv3,
                });
                Min = Vector3.Min(Min, p);
                Max = Vector3.Max(Max, p);
                return Vertices.Count - 1;
            }

            public void Quad(int a, int b, int c, int d)
            {
                // a, b at one station (left, right), c, d at the next: two triangles facing the normal side.
                Triangles.Add(a);
                Triangles.Add(c);
                Triangles.Add(b);
                Triangles.Add(b);
                Triangles.Add(c);
                Triangles.Add(d);
            }
        }
    }

    /// <summary>
    /// Roads and railways as real geometry for the camera's surroundings (the clipmap's road
    /// layer paints them further out). Built per OSM tile from the same data:
    ///  * a road: its carriageway as wide as its class and lanes make it, the painted lines
    ///    worked out per pixel in the shader (crisp at any distance) from the marking set in
    ///    uv3; in towns kerbs and sidewalks of paving stones, broken off at junctions so they
    ///    don't run across the other road (and markings stop there too); in the country gravel
    ///    shoulders. Bigger roads lie a few centimetres above lesser ones where they meet.
    ///  * a railway: a ballast bed with sleepers (the shader's), two steel rails on top.
    ///  * on OSM bridges: the same carried on a deck between the ground at the bridge's ends,
    ///    with parapets, and piers down to the ground; one over a road or railway (an overpass)
    ///    is lifted clear of it, its road climbing to it on ramps either side.
    /// Vertices hold their place across and along the way and their height above the ground;
    /// Road3D.shader puts them on the terrain as drawn (HeightField), the positions here (from
    /// the tile's height grid) are only the fallback. Thread-safe.
    ///
    /// Channels: uv0 (texture u, v in metres; material: Materials.Kind; markings on 0..1),
    /// uv1 (height above the ground or deck; 1 on a bridge deck; t along the bridge 0..1; lift
    /// at its middle), uv2 (the bridge's ends: x, z and x, z in the tile frame), uv3 (markings:
    /// half width of the painted part, lane width, lanes, style bits: 1 edge lines, 2 one-way,
    /// 4 solid centre line, 8 dashed centre line, 16 Americas, 32 yellow left edge), colour
    /// (tint; alpha 255 on top faces, whose normal the shader
    /// takes from the terrain).
    /// </summary>
    static class Road3DMesher
    {
        const double MaxStep = 6.0;          // metres between stations along a way
        const double Kerb = 0.15;            // kerb height
        const double Shoulder = 1.0;         // gravel shoulders of country roads
        const double DeckDepth = 1.2;        // bridge deck thickness
        const double Parapet = 0.9, ParapetWidth = 0.25;
        const double PierSpacing = 30.0, PierLength = 1.6;
        const double Clearance = 6.5;        // an overpass is this much higher than the ground at its ends
        const double RampLength = 120.0;     // ... and its road climbs to it over this (about 5%)
        const double PierMinimum = 3.0;      // no piers under ramps lower than this
        const double BridgeSnap = 2.5;       // a way within this of a bridge line, and along it, is on the bridge
        const double BallastHeight = 0.35, RailHeight = 0.17, RailWidth = 0.07;
        const double BuildingCell = 45.0;    // metres: town = a building within a couple of cells
        const double OpenEnd = 4.0;          // a bridge end this near the tile's edge goes on in the next tile

        // Layers of the material arrays (Materials.Kind).
        const float Asphalt = 0f, Paving = 1f, Concrete = 2f, Ballast = 3f, Steel = 4f;

        static readonly Color32 Plain = new Color32(255, 255, 255, 0), PlainTop = new Color32(255, 255, 255, 255);

        enum WayKind { Road, Rail }

        sealed class Way
        {
            public WayKind Kind;
            public string OsmKind;
            public RoadMesher.Style Style;
            public double Half;        // half width of the carriageway or ballast bed
            public double Sidewalk;    // width of a sidewalk in town (0: none)
            public double Gauge;
            public double Base;        // height of the carriageway above the ground
            public float Material;
            public Color32 Tint;
            public Vector4 Marks;
            public readonly List<List<double>> Runs = new List<List<double>>();
        }

        struct Junction
        {
            public int Degree;          // segments of ways meeting there
            public int Roads, Rails;    // ways of each kind
            public double RoadReach;    // how far from it the roads there reach (half width and sidewalk)
            public double RailReach;    // ... and the railways (half the ballast bed)

            /// <summary>Ways cross or branch here (not just one way going on as another).</summary>
            public bool Real => Degree >= 3 || Roads > 0 && Rails > 0;
        }

        /// <summary>minWidth: ways narrower than this (m) are left out (far off, a pixel or less).</summary>
        public static Road3DMeshData Build(OsmTile tile, HeightGrid grid, double minWidth = 0.0)
        {
            TileFrame frame = TileFrame.For(tile, grid);
            TileFrame flat = TileFrame.Flat(tile);
            // Lanes, kerbs and pavements, cut short along the way to follow the ground: ~50-150
            // vertices a road point in full detail; further off (minWidth) narrow ways are left out.
            int points = 0;
            foreach (OsmRoad road in tile.RoadList)
                if (road.Points != null)
                    points += road.Points.Length / 2;
            Road3DMeshData data = minWidth > 0.0 ? Road3DMeshData.Rent(points * 30, false) : Road3DMeshData.Rent(points * 80, true);
            data.Origin = frame.Origin;
            double centreLon = OsmTile.LonOf(tile.X + 0.5);
            bool americas = centreLon < -30.0 && centreLon > -170.0;

            var ways = new List<Way>();
            foreach (OsmRoad road in tile.RoadList)
            {
                if (road.Points == null || road.Points.Length < 4)
                    continue;
                Way w = Classify(road, americas);
                if (w == null || 2.0 * w.Half < minWidth)
                    continue;
                RibbonMeshData.Runs(tile, flat, road.Points, 0.0, double.MaxValue, w.Runs);
                w.Runs.RemoveAll(r => r.Count < 4);
                if (w.Runs.Count > 0)
                    ways.Add(w);
            }
            data.Ways = ways.Count;
            if (ways.Count == 0)
                return data;

            HashSet<long> town = BuildingCells(tile, flat);
            List<double> bridges = BridgeSegments(tile, flat);
            Dictionary<long, Junction> junctions = Junctions(ways);
            Bounds(tile, flat, out double minX, out double minY, out double maxX, out double maxY);
            var ctx = new Context
            {
                Data = data, Frame = frame, Grid = grid, Town = town, Bridges = bridges, Junctions = junctions,
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, Ways = ways,
            };
            // Stations of every way first: an overpass ends where the ways leading to it begin,
            // and those climb to it.
            var placed = new List<(Way, List<double>, List<Station>)>();
            foreach (Way w in ways)
                foreach (List<double> run in w.Runs)
                    placed.Add((w, run, Stations(ctx, w, run)));
            foreach (var (w, run, st) in placed)
                Approaches(ctx, run, st);
            foreach (var (w, run, st) in placed)
                Emit(ctx, w, st);
            return data;
        }

        static Way Classify(OsmRoad road, bool americas)
        {
            if (RoadMesher.Railway(road.Kind, out double bed, out double track))
            {
                return new Way
                {
                    Kind = WayKind.Rail, OsmKind = road.Kind, Half = bed * 0.5, Gauge = road.Kind == "narrow_gauge" ? 1.0 : 1.435,
                    Material = Ballast, Tint = Plain,
                };
            }
            if (!RoadMesher.Styled(road, americas, out RoadMesher.Style s))
                return null;
            var w = new Way { Kind = WayKind.Road, OsmKind = road.Kind, Style = s, Half = s.Width * 0.5 };
            w.Base = 0.05 + 0.012 * RoadMesher.Rank(road.Kind);
            switch (road.Kind)
            {
                case "primary":
                case "primary_link":
                    w.Sidewalk = 3.0;
                    break;
                case "secondary":
                case "secondary_link":
                case "tertiary":
                case "tertiary_link":
                    w.Sidewalk = 2.5;
                    break;
                case "residential":
                case "unclassified":
                case "living_street":
                case "minor":
                    w.Sidewalk = 2.0;
                    break;
            }
            if (road.Kind == "pedestrian")
            {
                w.Material = Paving;
                w.Tint = Plain;
                return w;
            }
            w.Material = Asphalt;
            byte tint = (byte)Mathf.Clamp(255f * (1.25f - 0.5f * s.Shade), 150f, 255f);
            w.Tint = new Color32(tint, tint, tint, 0);
            if (s.Lanes > 0)
            {
                int style = (s.Edges ? 1 : 0) | (s.OneWay ? 2 : 0) | (americas ? 16 : 0);
                if (!s.OneWay && s.Centre != RoadMesher.Centre.None)
                    style |= s.Centre == RoadMesher.Centre.Solid && americas ? 4 : 8; // elsewhere centre lines are dashed
                if (s.OneWay && s.Edges && americas)
                    style |= 32;
                w.Marks = new Vector4((float)(s.Lanes * s.LaneWidth * 0.5), (float)s.LaneWidth, s.Lanes, style);
            }
            return w;
        }

        // ------------------------------------------------------------------ what is around

        static long Cell(double x, double y, double size) => ((long)Math.Floor(x / size) << 32) ^ (uint)(int)Math.Floor(y / size);

        /// <summary>Where the buildings are, by cells: roads there get sidewalks.</summary>
        static HashSet<long> BuildingCells(OsmTile tile, TileFrame flat)
        {
            var cells = new HashSet<long>();
            foreach (OsmBuilding b in tile.Buildings)
            {
                int[] ring = b.Rings[0];
                double lat = 0, lon = 0;
                for (int i = 0; i < ring.Length; i += 2)
                {
                    lat += ring[i];
                    lon += ring[i + 1];
                }
                int n = ring.Length / 2;
                RibbonMeshData.Point(flat, lat * 1e-7 / n, lon * 1e-7 / n, out double x, out double y);
                cells.Add(Cell(x, y, BuildingCell));
            }
            return cells;
        }

        static bool InTown(HashSet<long> town, double x, double y)
        {
            if (town.Count == 0)
                return false;
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                    if (town.Contains(Cell(x + i * BuildingCell, y + j * BuildingCell, BuildingCell)))
                        return true;
            return false;
        }

        /// <summary>The bridge lines as segments (x0, y0, x1, y1, ...) in the tile's plane.</summary>
        static List<double> BridgeSegments(OsmTile tile, TileFrame flat)
        {
            var segments = new List<double>();
            foreach (int[] line in tile.Bridges)
            {
                double px = 0, py = 0;
                for (int i = 0; i + 1 < line.Length; i += 2)
                {
                    RibbonMeshData.Point(flat, line[i] * 1e-7, line[i + 1] * 1e-7, out double x, out double y);
                    if (i > 0)
                    {
                        segments.Add(px);
                        segments.Add(py);
                        segments.Add(x);
                        segments.Add(y);
                    }
                    px = x;
                    py = y;
                }
            }
            return segments;
        }

        static void Bounds(OsmTile tile, TileFrame flat, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (double lat in new[] { tile.South, tile.North })
            {
                foreach (double lon in new[] { tile.West, tile.East })
                {
                    RibbonMeshData.Point(flat, lat, lon, out double x, out double y);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        static long Key(double x, double y) => ((long)Math.Round(x * 4.0) << 32) ^ (uint)(int)Math.Round(y * 4.0);

        /// <summary>Points where ways meet (OSM nodes they share): where they cross or branch,
        /// and level crossings - not where one way just goes on as another (a road changing
        /// class or lanes), nor where a way is cut at the tile's edge.</summary>
        static Dictionary<long, Junction> Junctions(List<Way> ways)
        {
            var all = new Dictionary<long, Junction>();
            var seen = new Dictionary<long, Way>();
            foreach (Way w in ways)
            {
                foreach (List<double> run in w.Runs)
                {
                    int n = run.Count / 2;
                    for (int i = 0; i < n; i++)
                    {
                        long k = Key(run[2 * i], run[2 * i + 1]);
                        all.TryGetValue(k, out Junction j);
                        j.Degree += i == 0 || i == n - 1 ? 1 : 2;
                        // Each way counted once per point.
                        if (!seen.TryGetValue(k, out Way before) || before != w)
                        {
                            if (w.Kind == WayKind.Rail)
                            {
                                j.Rails++;
                                j.RailReach = Math.Max(j.RailReach, w.Half + 1.0);
                            }
                            else
                            {
                                j.Roads++;
                                j.RoadReach = Math.Max(j.RoadReach, w.Half + w.Sidewalk + 1.0);
                            }
                            seen[k] = w;
                        }
                        all[k] = j;
                    }
                }
            }
            var real = new Dictionary<long, Junction>();
            foreach (var kv in all)
                if (kv.Value.Roads + kv.Value.Rails >= 2 && kv.Value.Real)
                    real[kv.Key] = kv.Value;
            return real;
        }

        // ------------------------------------------------------------------ geometry

        sealed class Context
        {
            public Road3DMeshData Data;
            public TileFrame Frame;
            public HeightGrid Grid;
            public HashSet<long> Town;
            public List<double> Bridges;
            public Dictionary<long, Junction> Junctions;
            public List<Way> Ways;
            public double MinX, MinY, MaxX, MaxY;
            public readonly Dictionary<long, double> EndLifts = new Dictionary<long, double>(); // overpass ends: their lift
        }

        /// <summary>A place along a way.</summary>
        struct Station
        {
            public double X, Y;        // plane
            public double Along;
            public double Rx, Ry;      // mitered right vector (longer at turns, so the width holds)
            public double Ax, Ay;      // unit direction along
            public bool Town, Cut, Bridge, Ramp;
            public double T, Lift;     // on a bridge: share of the way along it, lift above the line between its ends
            public double EndAx, EndAy, EndBx, EndBy;
        }

        static void Emit(Context ctx, Way w, List<Station> st)
        {
            if (st.Count < 2)
                return;
            if (w.Kind == WayKind.Rail)
                RailFaces(ctx, w, st);
            else
                RoadFaces(ctx, w, st);
            BridgeStructures(ctx, w, st);
        }

        /// <summary>The way's points, closer together where needed, with what is true at each.</summary>
        static List<Station> Stations(Context ctx, Way w, List<double> run)
        {
            // Along distances of the run's own points, and where junctions cut it.
            int n = run.Count / 2;
            var along = new double[n];
            for (int i = 1; i < n; i++)
                along[i] = along[i - 1] + Math.Sqrt(Sq(run[2 * i] - run[2 * i - 2]) + Sq(run[2 * i + 1] - run[2 * i - 1]));
            // Roads keep their sidewalks and markings off junctions and level crossings;
            // railways their ballast off level crossings (the road runs over the rails there).
            var cuts = new List<double>(); // pairs: from, to
            for (int i = 0; i < n; i++)
            {
                if (!ctx.Junctions.TryGetValue(Key(run[2 * i], run[2 * i + 1]), out Junction j))
                    continue;
                double reach = w.Kind == WayKind.Rail ? (j.Roads > 0 ? j.RoadReach : 0.0) : Math.Max(j.RoadReach, j.RailReach);
                if (reach <= 0.0)
                    continue;
                cuts.Add(along[i] - reach);
                cuts.Add(along[i] + reach);
            }

            // Stations at the run's points, every MaxStep between, and at the cuts' edges.
            var marks = new List<double>();
            for (int i = 0; i < n; i++)
                marks.Add(along[i]);
            for (int i = 0; i + 1 < n; i++)
            {
                int steps = (int)Math.Ceiling((along[i + 1] - along[i]) / MaxStep);
                for (int k = 1; k < steps; k++)
                    marks.Add(along[i] + (along[i + 1] - along[i]) * k / steps);
            }
            foreach (double c in cuts)
                if (c > 0.0 && c < along[n - 1])
                    marks.Add(c);
            marks.Sort();
            var st = new List<Station>(marks.Count);
            int seg = 0;
            double last = double.MinValue;
            foreach (double a in marks)
            {
                if (a - last < 0.05)
                    continue;
                last = a;
                while (seg < n - 2 && a > along[seg + 1])
                    seg++;
                double len = Math.Max(along[seg + 1] - along[seg], 1e-9);
                double t = Math.Max(0.0, Math.Min(1.0, (a - along[seg]) / len));
                double x0 = run[2 * seg], y0 = run[2 * seg + 1], x1 = run[2 * seg + 2], y1 = run[2 * seg + 3];
                var s = new Station
                {
                    X = x0 + (x1 - x0) * t, Y = y0 + (y1 - y0) * t, Along = a,
                    Ax = (x1 - x0) / len, Ay = (y1 - y0) / len,
                };
                bool cut = false;
                for (int k = 0; k + 1 < cuts.Count && !cut; k += 2)
                    cut = a > cuts[k] + 0.01 && a < cuts[k + 1] - 0.01;
                s.Cut = cut;
                s.Town = w.Kind == WayKind.Road && w.Sidewalk > 0.0 && InTown(ctx.Town, s.X, s.Y);
                s.Bridge = OnBridge(ctx.Bridges, s.X, s.Y, s.Ax, s.Ay);
                st.Add(s);
            }

            // Mitered right vectors: the mean of the segments' normals, longer through turns.
            for (int i = 0; i < st.Count; i++)
            {
                Station s = st[i];
                double ax = s.Ax, ay = s.Ay;
                if (i > 0 && i < st.Count - 1)
                {
                    double bx = st[i + 1].X - s.X, by = st[i + 1].Y - s.Y, l = Math.Sqrt(bx * bx + by * by);
                    double px = s.X - st[i - 1].X, py = s.Y - st[i - 1].Y, lp = Math.Sqrt(px * px + py * py);
                    if (l > 1e-6 && lp > 1e-6)
                    {
                        bx /= l;
                        by /= l;
                        px /= lp;
                        py /= lp;
                        double mx = px + bx, my = py + by, ml = Math.Sqrt(mx * mx + my * my);
                        if (ml > 1e-6)
                        {
                            ax = mx / ml;
                            ay = my / ml;
                            double cos = Math.Max(px * ax + py * ay, 0.5);
                            s.Rx = ay / cos;
                            s.Ry = -ax / cos;
                        }
                    }
                }
                if (s.Rx == 0.0 && s.Ry == 0.0)
                {
                    s.Rx = ay;
                    s.Ry = -ax;
                }
                s.Ax = ax;
                s.Ay = ay;
                st[i] = s;
            }

            // Bridges: runs of stations on one, between their ends (an end at the tile's edge
            // goes on in the next tile: the deck keeps the other end's height), lifted over
            // whatever they cross.
            for (int i = 0; i < st.Count;)
            {
                if (!st[i].Bridge)
                {
                    i++;
                    continue;
                }
                int j = i;
                while (j + 1 < st.Count && st[j + 1].Bridge)
                    j++;
                if (j == i)
                {
                    st[i] = Unbridge(st[i]);
                    i++;
                    continue;
                }
                double ax = st[i].X, ay = st[i].Y, bx = st[j].X, by = st[j].Y;
                bool openA = NearEdge(ctx, ax, ay), openB = NearEdge(ctx, bx, by);
                if (openA && !openB)
                {
                    ax = bx;
                    ay = by;
                }
                else if (openB && !openA)
                {
                    bx = ax;
                    by = ay;
                }
                double lift = Crosses(ctx, w, st, i, j) ? Clearance : 0.0;
                double from = st[i].Along, length = Math.Max(st[j].Along - from, 1e-6);
                for (int k = i; k <= j; k++)
                {
                    Station s = st[k];
                    s.T = (s.Along - from) / length;
                    s.Lift = lift;
                    s.EndAx = ax;
                    s.EndAy = ay;
                    s.EndBx = bx;
                    s.EndBy = by;
                    st[k] = s;
                }
                if (lift > 0.0)
                {
                    // Ramps: the way climbs to the overpass from either side (up to another bridge).
                    for (int k = i - 1; k >= 0 && !st[k].Bridge && from - st[k].Along < RampLength; k--)
                        st[k] = Ramp(st[k], lift * (1.0 - (from - st[k].Along) / RampLength));
                    double to = st[j].Along;
                    for (int k = j + 1; k < st.Count && !st[k].Bridge && st[k].Along - to < RampLength; k++)
                        st[k] = Ramp(st[k], lift * (1.0 - (st[k].Along - to) / RampLength));
                    // An overpass ending where its way does: the ways going on from there climb too.
                    if (i == 0)
                        EndLift(ctx, run[0], run[1], lift);
                    if (j == st.Count - 1)
                        EndLift(ctx, run[run.Count - 2], run[run.Count - 1], lift);
                }
                i = j + 1;
            }
            return st;
        }

        static void EndLift(Context ctx, double x, double y, double lift)
        {
            long k = Key(x, y);
            ctx.EndLifts[k] = ctx.EndLifts.TryGetValue(k, out double had) ? Math.Max(had, lift) : lift;
        }

        /// <summary>A way beginning or ending where an overpass does climbs to it.</summary>
        static void Approaches(Context ctx, List<double> run, List<Station> st)
        {
            if (st.Count < 2 || ctx.EndLifts.Count == 0)
                return;
            if (!st[0].Bridge && ctx.EndLifts.TryGetValue(Key(run[0], run[1]), out double first))
            {
                for (int k = 0; k < st.Count && !st[k].Bridge && st[k].Along < RampLength; k++)
                    st[k] = Ramp(st[k], first * (1.0 - st[k].Along / RampLength));
            }
            int last = st.Count - 1;
            if (!st[last].Bridge && ctx.EndLifts.TryGetValue(Key(run[run.Count - 2], run[run.Count - 1]), out double end))
            {
                double total = st[last].Along;
                for (int k = last; k >= 0 && (!st[k].Bridge || st[k].Ramp) && total - st[k].Along < RampLength; k--)
                {
                    double lift = end * (1.0 - (total - st[k].Along) / RampLength);
                    if (!st[k].Bridge || lift > st[k].Lift)
                        st[k] = Ramp(st[k], lift);
                }
            }
        }

        static Station Unbridge(Station s)
        {
            s.Bridge = false;
            return s;
        }

        /// <summary>A station on an overpass's ramp: carried like a deck, at its own place's
        /// ground lifted by `lift`.</summary>
        static Station Ramp(Station s, double lift)
        {
            s.Bridge = true;
            s.Ramp = true;
            s.T = 0.5;
            s.Lift = lift;
            s.EndAx = s.EndBx = s.X;
            s.EndAy = s.EndBy = s.Y;
            return s;
        }

        static double Sq(double v) => v * v;

        static bool NearEdge(Context ctx, double x, double y) =>
            x - ctx.MinX < OpenEnd || ctx.MaxX - x < OpenEnd || y - ctx.MinY < OpenEnd || ctx.MaxY - y < OpenEnd;

        /// <summary>Whether a point of a way, going (ax, ay), lies on a bridge line and along it.</summary>
        static bool OnBridge(List<double> segments, double x, double y, double ax, double ay)
        {
            for (int i = 0; i + 3 < segments.Count; i += 4)
            {
                double x0 = segments[i], y0 = segments[i + 1], x1 = segments[i + 2], y1 = segments[i + 3];
                double dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
                if (l2 < 1e-6)
                    continue;
                double t = ((x - x0) * dx + (y - y0) * dy) / l2;
                if (t < -0.02 || t > 1.02)
                    continue;
                double px = x0 + dx * t - x, py = y0 + dy * t - y;
                if (px * px + py * py > BridgeSnap * BridgeSnap)
                    continue;
                double l = Math.Sqrt(l2);
                if (Math.Abs((dx * ax + dy * ay) / l) > 0.9)
                    return true;
            }
            return false;
        }

        /// <summary>Whether the stretch i..j of a way crosses another way of the tile (one not
        /// on a bridge where they cross): then it is an overpass, higher in the middle.</summary>
        static bool Crosses(Context ctx, Way self, List<Station> st, int i, int j)
        {
            foreach (Way w in ctx.Ways)
            {
                if (w == self)
                    continue;
                foreach (List<double> run in w.Runs)
                {
                    for (int k = 0; k + 3 < run.Count; k += 2)
                    {
                        double cx = run[k], cy = run[k + 1], dx = run[k + 2], dy = run[k + 3];
                        for (int s = i; s < j; s++)
                        {
                            if (Intersect(st[s].X, st[s].Y, st[s + 1].X, st[s + 1].Y, cx, cy, dx, dy, out double ix, out double iy) &&
                                !OnBridge(ctx.Bridges, ix, iy, dx - cx, dy - cy))
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        static bool Intersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, out double ix, out double iy)
        {
            ix = iy = 0.0;
            double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-9)
                return false;
            double t = ((cx - ax) * sy - (cy - ay) * sx) / den, u = ((cx - ax) * ry - (cy - ay) * rx) / den;
            if (t <= 0.0 || t >= 1.0 || u <= 0.0 || u >= 1.0)
                return false;
            ix = ax + rx * t;
            iy = ay + ry * t;
            return true;
        }

        /// <summary>A vertex: the station's point moved u across, h above the ground (on a
        /// bridge: above its deck) - a nominal position from the height grid, the shader puts
        /// it on the terrain.</summary>
        static int Vertex(Context ctx, Road3DMeshData.Part part, in Station s, double u, double h, Vector3 normal,
                          float texU, float texV, float material, float markings, Color32 colour, Vector4 marks)
        {
            double px = s.X + s.Rx * u, py = s.Y + s.Ry * u;
            double nominal = h;
            if (s.Bridge)
            {
                double ga = Ground(ctx, s.EndAx, s.EndAy), gb = Ground(ctx, s.EndBx, s.EndBy), here = Ground(ctx, px, py);
                double deck = ga + (gb - ga) * s.T + s.Lift;
                nominal = Math.Max(deck, here) - here + h;
            }
            Vector3 p = Local(ctx, px, py, nominal);
            var along = new Vector3((float)s.Ax, 0f, (float)s.Ay);
            return part.Add(p, normal, along, colour, new Vector4(texU, texV, material, markings),
                            new Vector4((float)h, s.Bridge ? 1f : 0f, (float)s.T, (float)s.Lift),
                            new Vector4((float)s.EndAx, (float)s.EndAy, (float)s.EndBx, (float)s.EndBy), marks);
        }

        /// <summary>Ground radius from the height grid at a point of the plane.</summary>
        static double Ground(Context ctx, double x, double y)
        {
            ctx.Frame.Unplane(x, y, out double dx, out double dy, out double dz);
            double lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dy))) * 180.0 / Math.PI, lon = Math.Atan2(dz, dx) * 180.0 / Math.PI;
            return ctx.Grid.At(lat, lon);
        }

        static Vector3 Local(Context ctx, double x, double y, double height)
        {
            ctx.Frame.Unplane(x, y, out double dx, out double dy, out double dz);
            double lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dy))) * 180.0 / Math.PI, lon = Math.Atan2(dz, dx) * 180.0 / Math.PI;
            return ctx.Frame.Local(dx, dy, dz, ctx.Grid.At(lat, lon) + height);
        }

        /// <summary>A face along the way: at each station where `present` says so, columns
        /// across from (u0, h0) to (u1, h1), joined to the previous station's if it had them.
        /// Unflipped, a face's front is along x (col 0 to col 1): up for columns going right,
        /// left for columns going up; flip turns it round.</summary>
        delegate bool Present(in Station s);

        static void Strip(Context ctx, Road3DMeshData.Part part, List<Station> st, Present present, double u0, double h0, double u1, double h1,
                          int columns, Func<Station, Vector3> normal, float material, float markings, Color32 colour, Vector4 marks,
                          bool vertical, bool flip)
        {
            int[] last = null;
            var now = new int[columns];
            double width = Math.Sqrt(Sq(u1 - u0) + Sq(h1 - h0));
            for (int i = 0; i < st.Count; i++)
            {
                Station s = st[i];
                if (!present(s))
                {
                    last = null;
                    continue;
                }
                Vector3 n = normal(s);
                for (int c = 0; c < columns; c++)
                {
                    double k = columns == 1 ? 0.0 : (double)c / (columns - 1);
                    double u = u0 + (u1 - u0) * k, h = h0 + (h1 - h0) * k;
                    // Textures: across and along on top faces; along and up the face on walls.
                    float tu = vertical ? (float)s.Along : (float)u, tv = vertical ? (float)(width * k) : (float)s.Along;
                    now[c] = Vertex(ctx, part, s, u, h, n, tu, tv, material, s.Cut ? 0f : markings, colour, marks);
                }
                if (last != null)
                {
                    for (int c = 0; c + 1 < columns; c++)
                    {
                        if (flip)
                            part.Quad(now[c], now[c + 1], last[c], last[c + 1]);
                        else
                            part.Quad(last[c], last[c + 1], now[c], now[c + 1]);
                    }
                }
                last ??= new int[columns];
                Array.Copy(now, last, columns);
            }
        }

        static Vector3 Right(in Station s)
        {
            double l = Math.Sqrt(s.Rx * s.Rx + s.Ry * s.Ry);
            return new Vector3((float)(s.Rx / l), 0f, (float)(s.Ry / l));
        }

        static readonly Func<Station, Vector3> Up = s => Vector3.up;
        static readonly Func<Station, Vector3> Down = s => Vector3.down;
        static readonly Func<Station, Vector3> Rightward = s => Right(s);
        static readonly Func<Station, Vector3> Leftward = s => -Right(s);

        /// <summary>Carriageway; in town kerbs and sidewalks, broken off at junctions; in the
        /// country gravel shoulders; on bridges a deck with parapets.</summary>
        static void RoadFaces(Context ctx, Way w, List<Station> st)
        {
            Road3DMeshData.Part ground = ctx.Data.Ground;
            double half = w.Half, b = w.Base, side = w.Sidewalk, top = b + Kerb;
            Color32 tint = w.Tint;
            tint.a = 255;
            // The carriageway: three columns, so it bends with the terrain across its width.
            Strip(ctx, ground, st, (in Station s) => true, -half, b, half, b, 3, Up, w.Material, 1f, tint, w.Marks, false, false);

            if (side > 0.0)
            {
                foreach (int dir in new[] { -1, 1 })
                {
                    double edge = dir * half, outer = dir * (half + side);
                    Present on = (in Station s) => s.Town && !s.Cut;
                    // Kerb face (towards the carriageway), sidewalk, and the sidewalk's outer edge down to the ground.
                    Strip(ctx, ground, st, on, edge, b, edge, top, 2, dir < 0 ? Rightward : Leftward, Concrete, 0f, Plain, default, true, dir < 0);
                    Strip(ctx, ground, st, on, dir < 0 ? outer : edge, top, dir < 0 ? edge : outer, top, 2, Up, Paving, 0f, PlainTop, default, false, false);
                    Present ground2 = (in Station s) => s.Town && !s.Cut && !s.Bridge;
                    Strip(ctx, ground, st, ground2, outer, top, outer, -0.2, 2, dir < 0 ? Leftward : Rightward, Concrete, 0f, Plain, default, true, dir < 0);
                }
            }
            if (w.OsmKind != "pedestrian" && w.OsmKind != "living_street")
            {
                // Gravel shoulders where there are no sidewalks.
                foreach (int dir in new[] { -1, 1 })
                {
                    Present on = (in Station s) => !s.Town && !s.Bridge;
                    double edge = dir * half, outer = dir * (half + Shoulder);
                    Strip(ctx, ground, st, on, dir < 0 ? outer : edge, b - 0.03, dir < 0 ? edge : outer, b - 0.03, 2, Up, Ballast, 0f, PlainTop, default, false, false);
                }
            }
        }

        /// <summary>Ballast bed with sleepers on its top (the shader's), and two steel rails.</summary>
        static void RailFaces(Context ctx, Way w, List<Station> st)
        {
            Road3DMeshData.Part ground = ctx.Data.Ground;
            double bed = w.Half, top = bed - 0.8, h = BallastHeight;
            Present bedOn = (in Station s) => !s.Cut || s.Bridge;
            Strip(ctx, ground, st, bedOn, -bed, -0.05, -top, h, 2, Up, Ballast, 0f, PlainTop, default, false, false);
            Strip(ctx, ground, st, bedOn, -top, h, top, h, 3, Up, Ballast, 1f, PlainTop, default, false, false);
            Strip(ctx, ground, st, bedOn, top, h, bed, -0.05, 2, Up, Ballast, 0f, PlainTop, default, false, false);
            // Rails on the sleepers; at level crossings sunk into the road.
            Present onBed = (in Station s) => !s.Cut || s.Bridge;
            Present inRoad = (in Station s) => s.Cut && !s.Bridge;
            double half = RailWidth * 0.5;
            foreach (bool crossing in new[] { false, true })
            {
                Present present = crossing ? inRoad : onBed;
                double railTop = crossing ? 0.16 : h + RailHeight, foot = crossing ? 0.0 : h + 0.02;
                foreach (int dir in new[] { -1, 1 })
                {
                    double c = dir * w.Gauge * 0.5;
                    Strip(ctx, ground, st, present, c - half, railTop, c + half, railTop, 2, Up, Steel, 0f, Plain, default, false, false);
                    Strip(ctx, ground, st, present, c - half, foot, c - half, railTop, 2, Leftward, Steel, 0f, Plain, default, true, false);
                    Strip(ctx, ground, st, present, c + half, foot, c + half, railTop, 2, Rightward, Steel, 0f, Plain, default, true, true);
                }
            }
        }

        /// <summary>On bridges: the deck's sides and underside, parapets, piers.</summary>
        static void BridgeStructures(Context ctx, Way w, List<Station> st)
        {
            bool any = false;
            foreach (Station s in st)
                any |= s.Bridge;
            if (!any)
                return;
            Road3DMeshData.Part part = ctx.Data.Structures;
            double edge = w.Kind == WayKind.Rail ? w.Half : w.Half + (w.Sidewalk > 0.0 ? w.Sidewalk : 0.5);
            double top = w.Kind == WayKind.Rail ? 0.0 : w.Sidewalk > 0.0 ? w.Base + Kerb : w.Base;
            double under = top - DeckDepth;
            Present on = (in Station s) => s.Bridge;
            foreach (int dir in new[] { -1, 1 })
            {
                double u = dir * edge, inner = dir * (edge - ParapetWidth);
                // Deck side, up into the parapet's outer face.
                Strip(ctx, part, st, on, u, under, u, top + Parapet, 2, dir < 0 ? Leftward : Rightward, Concrete, 0f, Plain, default, true, dir > 0);
                // Parapet top and inner face.
                Strip(ctx, part, st, on, dir < 0 ? u : inner, top + Parapet, dir < 0 ? inner : u, top + Parapet, 2, Up, Concrete, 0f, Plain, default, false, false);
                Strip(ctx, part, st, on, inner, top + Parapet, inner, top, 2, dir < 0 ? Rightward : Leftward, Concrete, 0f, Plain, default, true, dir > 0);
            }
            // Underside.
            Strip(ctx, part, st, on, -edge, under, edge, under, 2, Down, Concrete, 0f, Plain, default, false, true);
            if (w.Kind == WayKind.Rail)
            {
                // The ballast on a bridge lies in a trough: its deck under the bed.
                Strip(ctx, part, st, on, -edge, 0.0, edge, 0.0, 2, Up, Concrete, 0f, PlainTop, default, false, false);
            }

            // Piers every PierSpacing, away from the ends: walls from below the ground to the
            // deck's underside (the shader folds them away where the deck is low).
            double next = 0.0;
            for (int i = 0; i < st.Count; i++)
            {
                Station s = st[i];
                if (!s.Bridge || !s.Ramp && (s.T < 0.08 || s.T > 0.92) || s.Ramp && s.Lift < PierMinimum)
                    continue;
                if (s.Along < next)
                    continue;
                next = s.Along + PierSpacing;
                Pier(ctx, part, s, edge - 1.0, under);
            }
        }

        static void Pier(Context ctx, Road3DMeshData.Part part, Station s, double halfWidth, double under)
        {
            double l = PierLength * 0.5;
            Vector3 right = Right(s), along = new Vector3((float)s.Ax, 0f, (float)s.Ay);
            // Corners: (u, a) offsets across and along.
            var corners = new[] { (-halfWidth, -l), (halfWidth, -l), (halfWidth, l), (-halfWidth, l) };
            var normals = new[] { -along, right, along, -right };
            for (int f = 0; f < 4; f++)
            {
                var (u0, a0) = corners[f];
                var (u1, a1) = corners[(f + 1) % 4];
                Station p0 = Shift(s, a0), p1 = Shift(s, a1);
                int b0 = PierVertex(ctx, part, p0, u0, -1.0, false, normals[f], 0f, 0f);
                int b1 = PierVertex(ctx, part, p1, u1, -1.0, false, normals[f], (float)(Math.Abs(u1 - u0) + Math.Abs(a1 - a0)), 0f);
                int t0 = PierVertex(ctx, part, p0, u0, under, true, normals[f], 0f, 8f);
                int t1 = PierVertex(ctx, part, p1, u1, under, true, normals[f], (float)(Math.Abs(u1 - u0) + Math.Abs(a1 - a0)), 8f);
                part.Triangles.Add(b0);
                part.Triangles.Add(t0);
                part.Triangles.Add(b1);
                part.Triangles.Add(b1);
                part.Triangles.Add(t0);
                part.Triangles.Add(t1);
            }
        }

        static Station Shift(Station s, double along)
        {
            s.X += s.Ax * along;
            s.Y += s.Ay * along;
            s.Along += along;
            return s;
        }

        static int PierVertex(Context ctx, Road3DMeshData.Part part, Station s, double u, double h, bool onDeck, Vector3 normal, float tu, float tv)
        {
            Station v = s;
            if (!onDeck)
            {
                v.Bridge = false; // its foot stands on the ground
                v.T = 0.0;
                v.Lift = 0.0;
            }
            return Vertex(ctx, part, v, u, h, normal, tu, tv, Concrete, 0f, Plain, default);
        }
    }
}
