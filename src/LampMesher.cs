// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>A pool of light a lamp throws on the ground, drawn into the terrain's night
    /// light map (Clipmap.LightTexture) rather than as geometry, so it follows the terrain.</summary>
    struct GroundLight
    {
        public double Lat, Lon;
        public float Radius; // metres
        public Color32 Colour;
    }

    /// <summary>Street lamps of one tile: 4 vertices per lamp head (a camera-facing sprite the
    /// shader expands) in its TileFrame, the pools of light they throw on the roads, and the
    /// glow of the lit streets between them (ribbons in the tile's flat frame).</summary>
    sealed class LampMeshData : IPooledData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Color32> Colors = new List<Color32>();
        /// <summary>uv0: xy corner (-1..1), w size (m).</summary>
        public readonly List<Vector4> Corners = new List<Vector4>();
        public readonly List<int> Triangles = new List<int>();
        public readonly List<GroundLight> Lights = new List<GroundLight>();
        public readonly RibbonMeshData Glow = new RibbonMeshData();
        public Vector3 Origin;
        public Bounds Bounds;
        public int Lamps;

        public long HeldBytes => MeshPools.Bytes(Vertices, 12) + MeshPools.Bytes(Colors, 4) + MeshPools.Bytes(Corners, 16) +
                                 MeshPools.Bytes(Triangles, 4) + MeshPools.Bytes(Lights, 24) + Glow.HeldBytes;

        public static LampMeshData Rent() => DataPool<LampMeshData>.Rent();

        /// <summary>Done with (Unity has its copies, the tile its own list of pools): emptied and
        /// kept for the next tile (MeshPools).</summary>
        public static void Recycle(LampMeshData d)
        {
            if (d == null)
                return;
            d.Vertices.Clear();
            d.Colors.Clear();
            d.Corners.Clear();
            d.Triangles.Clear();
            d.Lights.Clear();
            d.Glow.Reset();
            d.Origin = default;
            d.Bounds = default;
            d.Lamps = 0;
            DataPool<LampMeshData>.Return(d);
        }
    }

    /// <summary>
    /// Places street lamps along a tile's roads: spaced by road class, on one or both sides,
    /// and - unless OSM says lit=yes/no - only where there are buildings nearby, so towns light
    /// up and country roads stay dark. Lamps mapped in OSM (highway=street_lamp, from Overpass)
    /// are placed as they are, and generated ones keep clear of them. Thread-safe.
    /// </summary>
    static class LampMesher
    {
        struct RoadLight
        {
            public double Spacing, Offset, Height;
            public float Size; // of the lamp head's glow, metres
            public bool BothSides, Major;

            public RoadLight(double spacing, double offset, double height, float size, bool bothSides, bool major)
            {
                Spacing = spacing;
                Offset = offset;
                Height = height;
                Size = size;
                BothSides = bothSides;
                Major = major;
            }
        }

        const double NearBuildings = 2;  // cells: a generated lamp needs a building within ~2 cells
        const double Cell = 45.0;         // metres
        const double ClearOfMapped = 20.0;
        const float BoundsMargin = 100f;  // lamp sprites grow with distance beyond the mesh bounds
        const float PoolOverlap = 0.6f;   // pool radius per lamp spacing: neighbouring pools just meet
        const float PoolLight = 0.45f;    // light a pool adds at its centre (two overlapping ~ 0.9)
        const float GlowLight = 0.2f;     // light a lit street has between its lamps, at its middle
        const double GlowSpill = 6.0;     // metres the glow reaches beyond the lamp posts
        const double EarthRadius = 6371000.0;

        // High-pressure sodium, warm LED, cool LED.
        static readonly Color32[] Colours =
        {
            new Color32(255, 150, 60, 255), new Color32(255, 206, 140, 255), new Color32(214, 226, 255, 255),
        };

        static bool Light(string kind, out RoadLight l)
        {
            switch (kind)
            {
                case "motorway":
                case "trunk":
                    l = new RoadLight(45, 11, 11, 1.26f, true, true);
                    return true;
                case "motorway_link":
                case "trunk_link":
                    l = new RoadLight(40, 6, 10, 1.12f, false, true);
                    return true;
                case "primary":
                case "primary_link":
                    l = new RoadLight(36, 8, 10, 1.12f, true, true);
                    return true;
                case "secondary":
                case "secondary_link":
                    l = new RoadLight(34, 7, 9, 1.05f, true, true);
                    return true;
                case "tertiary":
                case "tertiary_link":
                    l = new RoadLight(32, 6, 8, 0.98f, false, false);
                    return true;
                case "minor":
                case "residential":
                case "unclassified":
                case "living_street":
                    l = new RoadLight(30, 5, 7, 0.84f, false, false);
                    return true;
                case "pedestrian":
                    l = new RoadLight(22, 3, 5, 0.70f, false, false);
                    return true;
                case "service":
                case "busway":
                    l = new RoadLight(28, 4, 7, 0.77f, false, false);
                    return true;
                default:
                    l = default;
                    return false;
            }
        }

        /// <summary>sprites: the lamps as points of light; pools: their light on the ground. Far
        /// off neither (only the glow of the lit streets).</summary>
        public static LampMeshData Build(OsmTile tile, HeightGrid grid, List<BuildingMesher.Exclusion> exclusions,
                                         bool sprites = true, bool pools = true)
        {
            TileFrame frame = TileFrame.For(tile, grid);
            TileFrame flat = TileFrame.Flat(tile);
            LampMeshData mesh = LampMeshData.Rent();
            mesh.Origin = frame.Origin;
            var lit = new List<double>();
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            double south = tile.South, north = tile.North, west = tile.West, east = tile.East;

            // Where the buildings are, by 45 m cells.
            var built = new HashSet<long>();
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
                PlanePoint(frame, lat * 1e-7 / n, lon * 1e-7 / n, out double x, out double z);
                built.Add(Key(Math.Floor(x / Cell), Math.Floor(z / Cell)));
            }

            // Lamps mapped in OSM, as they are.
            var mapped = new HashSet<long>();
            for (int i = 0; i + 1 < tile.Lamps.Count; i += 2)
            {
                double lat = tile.Lamps[i] * 1e-7, lon = tile.Lamps[i + 1] * 1e-7;
                if (lat < south || lat > north || lon < west || lon > east)
                    continue;
                PlanePoint(frame, lat, lon, out double x, out double z);
                mapped.Add(Key(Math.Floor(x / ClearOfMapped), Math.Floor(z / ClearOfMapped)));
                ulong h = BuildingMesher.Mix((ulong)(tile.Lamps[i] * 31L + tile.Lamps[i + 1]));
                Add(mesh, frame, grid, exclusions, lat, lon, 0.0, 0.0, 8.0, 1.0f, 17f, Colour(h, false), sprites, pools, ref min, ref max);
            }

            foreach (OsmRoad road in tile.RoadList)
            {
                if (road.Lit == 2 || road.Points == null || road.Points.Length < 4 || !Light(road.Kind, out RoadLight light))
                    continue;
                bool tagged = road.Lit == 1;
                if (road.Kind == "service" && !tagged)
                    continue; // driveways and parking aisles: only where OSM says they are lit
                ulong h = BuildingMesher.Mix((ulong)road.Id * 0x9E37 + 17);
                Color32 colour = Colour(h, light.Major);
                double next = (h % 1000) / 1000.0 * light.Spacing; // phase, so lamps don't line up across roads
                int side = (h >> 24) % 2 == 0 ? 1 : -1;
                double travelled = 0.0;
                int count = 0;
                int[] p = road.Points;
                lit.Clear();
                for (int i = 0; i + 3 < p.Length; i += 2)
                {
                    double lat0 = p[i] * 1e-7, lon0 = p[i + 1] * 1e-7, lat1 = p[i + 2] * 1e-7, lon1 = p[i + 3] * 1e-7;
                    PlanePoint(frame, lat0, lon0, out double x0, out double z0);
                    PlanePoint(frame, lat1, lon1, out double x1, out double z1);
                    double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                    if (length < 0.01)
                        continue;
                    // Right-hand normal in the local plane (x east, z north).
                    double nx = (z1 - z0) / length, nz = -(x1 - x0) / length;
                    for (; next <= travelled + length; next += light.Spacing, count++)
                    {
                        double t = (next - travelled) / length;
                        double lat = lat0 + (lat1 - lat0) * t, lon = lon0 + (lon1 - lon0) * t;
                        if (lat < south || lat > north || lon < west || lon > east)
                            continue; // the neighbouring tile has this stretch
                        int s = light.BothSides ? (count % 2 == 0 ? 1 : -1) : side;
                        double ox = nx * s * light.Offset, oz = nz * s * light.Offset;
                        double x = x0 + (x1 - x0) * t + ox, z = z0 + (z1 - z0) * t + oz;
                        if (!tagged && !Near(built, x, z))
                            continue; // no town here: country roads stay dark
                        if (mapped.Count > 0 && mapped.Contains(Key(Math.Floor(x / ClearOfMapped), Math.Floor(z / ClearOfMapped))))
                            continue;
                        if (Add(mesh, frame, grid, exclusions, lat, lon, ox, oz, light.Height, light.Size,
                                (float)light.Spacing * PoolOverlap, colour, sprites, pools, ref min, ref max))
                            lit.Add(next);
                    }
                    travelled += length;
                }
                if (lit.Count > 0)
                    Glow(mesh, tile, flat, road, light, colour, lit);
            }

            if (mesh.Lamps > 0)
            {
                var bounds = new Bounds();
                bounds.SetMinMax(min, max);
                bounds.Expand(BoundsMargin * 2f);
                mesh.Bounds = bounds;
            }
            return mesh;
        }

        static void PlanePoint(TileFrame frame, double lat, double lon, out double x, out double z)
        {
            TileFrame.Direction(lat, lon, out double dx, out double dy, out double dz);
            frame.Plane(dx, dy, dz, 6371000.0, out x, out z); // plane coordinates hardly depend on the radius
        }

        static long Key(double cx, double cz) => ((long)cx << 32) ^ (uint)(int)cz;

        static bool Near(HashSet<long> built, double x, double z)
        {
            double cx = Math.Floor(x / Cell), cz = Math.Floor(z / Cell);
            for (int i = -(int)NearBuildings; i <= NearBuildings; i++)
                for (int j = -(int)NearBuildings; j <= NearBuildings; j++)
                    if (built.Contains(Key(cx + i, cz + j)))
                        return true;
            return false;
        }

        static Color32 Colour(ulong h, bool major)
        {
            int roll = (int)((h >> 40) % 100);
            // Main roads still have more sodium lamps; side streets have gone LED faster.
            int sodium = major ? 55 : 35, warm = major ? 25 : 40;
            return Colours[roll < sodium ? 0 : roll < sodium + warm ? 1 : 2];
        }

        /// <summary>One street lamp: its head `height` above the road point moved sideways by
        /// (ox, oz), and its pool of light between the pole and the road's middle. False where
        /// lights are excluded.</summary>
        static bool Add(LampMeshData mesh, TileFrame frame, HeightGrid grid, List<BuildingMesher.Exclusion> exclusions,
                        double lat, double lon, double ox, double oz, double height, float glow, float pool, Color32 colour,
                        bool sprites, bool pools, ref Vector3 min, ref Vector3 max)
        {
            if (sprites)
            {
                if (!Sprite(mesh, frame, grid, exclusions, lat, lon, ox, oz, height, glow, colour, ref min, ref max))
                    return false;
            }
            else
            {
                TileFrame.Direction(lat, lon, out double dx, out double dy, out double dz);
                if (BuildingMesher.Excluded(exclusions, dx, dy, dz))
                    return false;
            }
            // The pool is centred a third of the way from the pole back to the road's middle.
            if (pools)
                Pool(mesh, lat, lon, ox * 0.35, oz * 0.35, pool, colour, PoolLight);
            return true;
        }

        /// <summary>The street glowing between its lamps: a soft band along the road from a bit
        /// before the first lamp of each row to a bit after the last (lamps at `lit` metres along).</summary>
        static void Glow(LampMeshData mesh, OsmTile tile, TileFrame flat, OsmRoad road, RoadLight light, Color32 colour, List<double> lit)
        {
            double reach = light.Spacing * 0.75;
            float halfWidth = (float)(light.Offset + GlowSpill);
            var glow = new Color32((byte)(colour.r * GlowLight), (byte)(colour.g * GlowLight), (byte)(colour.b * GlowLight), 255);
            var runs = new List<List<double>>();
            var ends = new List<double>();
            double from = lit[0] - reach, to = lit[0] + reach;
            for (int i = 1; i <= lit.Count; i++)
            {
                if (i < lit.Count && lit[i] - reach <= to)
                {
                    to = lit[i] + reach;
                    continue;
                }
                runs.Clear();
                ends.Clear();
                RibbonMeshData.Runs(tile, flat, road.Points, from, to, runs, ends);
                for (int r = 0; r < runs.Count; r++)
                {
                    if (runs[r].Count < 4)
                        continue;
                    // The light fades out where the lamps end, not where the tile does.
                    float fadeIn = ends[2 * r] - from < 0.5 ? 1f : 0f, fadeOut = to - ends[2 * r + 1] < 0.5 ? 1f : 0f;
                    mesh.Glow.Add(runs[r], 0.0, halfWidth, glow, fadeIn, fadeOut);
                }
                if (i < lit.Count)
                {
                    from = lit[i] - reach;
                    to = lit[i] + reach;
                }
            }
        }

        /// <summary>A light's sprite `height` above the ground at (lat, lon) moved sideways by
        /// (ox, oz) metres; false where lights are excluded (KSC and the like).</summary>
        static bool Sprite(LampMeshData mesh, TileFrame frame, HeightGrid grid, List<BuildingMesher.Exclusion> exclusions,
                           double lat, double lon, double ox, double oz, double height, float size, Color32 colour,
                           ref Vector3 min, ref Vector3 max)
        {
            TileFrame.Direction(lat, lon, out double dx, out double dy, out double dz);
            if (BuildingMesher.Excluded(exclusions, dx, dy, dz))
                return false;
            double ground = grid.At(lat, lon);
            Vector3 head = frame.Local(dx, dy, dz, ground + height) + new Vector3((float)ox, 0f, (float)oz);
            int v = mesh.Vertices.Count;
            for (int k = 0; k < 4; k++)
            {
                float cx = k == 0 || k == 3 ? -1f : 1f, cy = k < 2 ? -1f : 1f;
                mesh.Vertices.Add(head);
                mesh.Corners.Add(new Vector4(cx, cy, 0f, size));
                mesh.Colors.Add(colour);
            }
            mesh.Triangles.Add(v);
            mesh.Triangles.Add(v + 1);
            mesh.Triangles.Add(v + 2);
            mesh.Triangles.Add(v);
            mesh.Triangles.Add(v + 2);
            mesh.Triangles.Add(v + 3);
            min = Vector3.Min(min, head);
            max = Vector3.Max(max, head);
            mesh.Lamps++;
            return true;
        }

        /// <summary>A pool of light on the ground (for the terrain's light map), `east`/`north`
        /// metres from (lat, lon).</summary>
        static void Pool(LampMeshData mesh, double lat, double lon, double east, double north, float radius, Color32 colour, float light)
        {
            mesh.Lights.Add(new GroundLight
            {
                Lat = lat + north / EarthRadius * 180.0 / Math.PI,
                Lon = lon + east / (EarthRadius * Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.01)) * 180.0 / Math.PI,
                Radius = radius,
                Colour = new Color32((byte)(colour.r * light), (byte)(colour.g * light), (byte)(colour.b * light), 255),
            });
        }
    }
}
