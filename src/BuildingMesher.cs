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
    /// <summary>Terrain radius (planet centre to ground, metres) on a lat/lon grid around a
    /// tile. Filled on the main thread - the PQS isn't thread-safe - then read by workers.</summary>
    sealed class HeightGrid
    {
        public readonly int Size;
        public readonly double South, West, North, East;
        public readonly double[] Radius;

        public HeightGrid(int size, double south, double west, double north, double east)
        {
            Size = size;
            South = south;
            West = west;
            North = north;
            East = east;
            Radius = new double[size * size];
        }

        public void Point(int index, out double lat, out double lon)
        {
            int i = index % Size, j = index / Size;
            lat = South + (North - South) * j / (Size - 1);
            lon = West + (East - West) * i / (Size - 1);
        }

        /// <summary>The same grid around a planet `factor` times as big (terrain included).</summary>
        public HeightGrid Scaled(double factor)
        {
            var g = new HeightGrid(Size, South, West, North, East);
            for (int i = 0; i < Radius.Length; i++)
                g.Radius[i] = Radius[i] * factor;
            return g;
        }

        public double At(double lat, double lon)
        {
            double fx = (lon - West) / (East - West) * (Size - 1);
            double fy = (lat - South) / (North - South) * (Size - 1);
            fx = Math.Max(0.0, Math.Min(Size - 1, fx));
            fy = Math.Max(0.0, Math.Min(Size - 1, fy));
            int ix = Math.Min((int)fx, Size - 2), iy = Math.Min((int)fy, Size - 2);
            double tx = fx - ix, ty = fy - iy;
            int k = iy * Size + ix;
            double bottom = Radius[k] + (Radius[k + 1] - Radius[k]) * tx;
            double top = Radius[k + Size] + (Radius[k + Size + 1] - Radius[k + Size]) * tx;
            return bottom + (top - bottom) * ty;
        }
    }

    /// <summary>
    /// Local frame at a tile's centre, on the ground there: x east, y up, z north. The tile's
    /// GameObject sits at Origin under the PQS with LookRotation(north, up). Origin is rounded
    /// to floats first and everything is measured from the rounded point, so the placement is
    /// exact. Deterministic: every mesher gets the same frame for the same tile and grid.
    /// </summary>
    struct TileFrame
    {
        public double CenterLat, CenterLon;
        public Vector3 Origin;
        double ox, oy, oz, ex, ey, ez, nx, ny, nz, ux, uy, uz;

        public static TileFrame For(OsmTile tile, HeightGrid grid)
        {
            double lat = OsmTile.LatOf(tile.Y + 0.5), lon = OsmTile.LonOf(tile.X + 0.5);
            return At(lat, lon, grid.At(lat, lon));
        }

        /// <summary>The frame at the tile's centre without terrain heights: enough for Plane,
        /// whose east/north coordinates don't depend on the origin's radius.</summary>
        public static TileFrame Flat(OsmTile tile) => Flat(tile.X, tile.Y);

        public static TileFrame Flat(int x, int y) => At(OsmTile.LatOf(y + 0.5), OsmTile.LonOf(x + 0.5), 6371000.0);

        static TileFrame At(double lat, double lon, double r0)
        {
            var f = new TileFrame { CenterLat = lat, CenterLon = lon };
            Direction(f.CenterLat, f.CenterLon, out f.ux, out f.uy, out f.uz);
            double lo = f.CenterLon * Math.PI / 180.0, la = f.CenterLat * Math.PI / 180.0;
            f.ex = -Math.Sin(lo);
            f.ey = 0.0;
            f.ez = Math.Cos(lo);
            f.nx = -Math.Sin(la) * Math.Cos(lo);
            f.ny = Math.Cos(la);
            f.nz = -Math.Sin(la) * Math.Sin(lo);
            f.Origin = new Vector3((float)(f.ux * r0), (float)(f.uy * r0), (float)(f.uz * r0));
            f.ox = f.Origin.x;
            f.oy = f.Origin.y;
            f.oz = f.Origin.z;
            return f;
        }

        /// <summary>Local position of the point at `radius` from the planet centre along a direction.</summary>
        public Vector3 Local(double dx, double dy, double dz, double radius)
        {
            double px = dx * radius - ox, py = dy * radius - oy, pz = dz * radius - oz;
            return new Vector3((float)(px * ex + py * ey + pz * ez), (float)(px * ux + py * uy + pz * uz), (float)(px * nx + py * ny + pz * nz));
        }

        /// <summary>Distance of the origin from the planet's centre.</summary>
        public double Radius => Math.Sqrt(ox * ox + oy * oy + oz * oz);

        /// <summary>East/north coordinates of that point in the local frame.</summary>
        public void Plane(double dx, double dy, double dz, double radius, out double east, out double north)
        {
            double px = dx * radius - ox, py = dy * radius - oy, pz = dz * radius - oz;
            east = px * ex + py * ey + pz * ez;
            north = px * nx + py * ny + pz * nz;
        }

        /// <summary>The direction (unit) of the ground at east/north in the frame's plane: Plane's
        /// inverse, near the centre (a kilometre or ten).</summary>
        public void Unplane(double east, double north, out double dx, out double dy, out double dz)
        {
            double px = ox + east * ex + north * nx, py = oy + east * ey + north * ny, pz = oz + east * ez + north * nz;
            double l = Math.Sqrt(px * px + py * py + pz * pz);
            dx = px / l;
            dy = py / l;
            dz = pz / l;
        }

        public static void Direction(double lat, double lon, out double x, out double y, out double z)
        {
            double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
            double cl = Math.Cos(la);
            x = cl * Math.Cos(lo);
            y = Math.Sin(la);
            z = cl * Math.Sin(lo);
        }
    }

    /// <summary>A building vertex as the graphics card gets it: one interleaved stream (Layout),
    /// so Unity takes a tile's vertices in one copy (MeshUpload).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BuildingVertex
    {
        public Vector3 Position, Normal;
        /// <summary>rgb: the surface's colour (its material's texture is scaled to it), a: random per building.</summary>
        public Color32 Color;
        /// <summary>Metres along the wall or across the roof; metres up (walls: above the
        /// building's ground level); style: 0 plain wall, 1 homes, 2 offices, 3 industry, 9..10
        /// flat roof, 11..12 pitched roof (the fraction: how much satellite imagery the roof
        /// shows); material (texture layer, Materials.cs).</summary>
        public Vector4 Uv0;
        /// <summary>On walls: window column (0..n along the wall, n windows), floor height
        /// (0: no windows), height of the eaves (windows stay below), metres between windows
        /// (negative: shop windows on the ground floor).</summary>
        public Vector4 Uv1;

        public const int Size = 60;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
        };
    }

    /// <summary>Building mesh arrays for one tile, in its TileFrame.</summary>
    sealed class BuildingMeshData : IPooledData
    {
        public readonly List<BuildingVertex> Vertices = new List<BuildingVertex>();
        public readonly List<int> Triangles = new List<int>();
        public Vector3 Origin; // PQS-local, exactly representable as floats
        public double CenterLat, CenterLon;
        public Bounds Bounds;
        public int Buildings, Pitched;

        public int Count => Vertices.Count;

        public void Add(Vector3 position, Vector3 normal, Color32 color, Vector4 uv0, Vector4 uv1) =>
            Vertices.Add(new BuildingVertex { Position = position, Normal = normal, Color = color, Uv0 = uv0, Uv1 = uv1 });

        public long HeldBytes => MeshPools.Bytes(Vertices, BuildingVertex.Size) + MeshPools.Bytes(Triangles, 4);

        /// <summary>Kept data for about this many vertices (and 1.6 indices a vertex, as buildings
        /// have); with `reserve` (the count is a good guess) its lists made to hold them.</summary>
        public static BuildingMeshData Rent(int vertices, bool reserve)
        {
            int indices = vertices / 5 * 8;
            BuildingMeshData d = DataPool<BuildingMeshData>.Rent(vertices * 60L + indices * 4L);
            if (!reserve)
                return d;
            MeshPools.Reserve(d.Vertices, vertices);
            MeshPools.Reserve(d.Triangles, indices);
            return d;
        }

        /// <summary>Done with (Unity has its copy): emptied and kept for the next tile (MeshPools).</summary>
        public static void Recycle(BuildingMeshData d)
        {
            if (d == null)
                return;
            d.Vertices.Clear();
            d.Triangles.Clear();
            d.Origin = default;
            d.CenterLat = d.CenterLon = 0.0;
            d.Bounds = default;
            d.Buildings = d.Pitched = 0;
            DataPool<BuildingMeshData>.Return(d);
        }
    }

    /// <summary>
    /// Extrudes footprints into buildings: walls from below the ground up to the eaves, and a
    /// roof - flat (triangulated with its courtyards), or pitched (Roofs.cs: gabled, hipped,
    /// pyramidal, skillion, mansard, gambrel, round, dome) where tagged so, or guessed so for
    /// houses and other small simple footprints. A floor goes under parts that float (min_height).
    /// Heights come from height / levels tags, otherwise from the building type and footprint.
    /// Walls and roofs get a material (plaster, brick, concrete, glass, corrugated metal, wood
    /// siding; clay tiles, slate or shingles, metal, gravel) and a colour: tagged, or likely ones
    /// for the type and the part of the world; windows follow the floors. Thread-safe: only
    /// Unity value types are touched.
    /// </summary>
    static class BuildingMesher
    {
        public struct Exclusion
        {
            public double X, Y, Z;  // PQS-local unit direction of the centre
            public double CosAngle; // cos of the angular radius
        }

        const double Sink = 3.0;           // walls go this far below the lowest ground point
        const double LevelHeight = 3.0;
        const double MinArea = 3.0;        // m^2; smaller footprints are mapping noise
        const float RoofImageryFull = 30f; // roofs of buildings up to this tall show the imagery,
        const float RoofImageryNone = 60f; // taller ones lean in it too much and get a plain colour

        // Texture layers, as Materials.cs packs them.
        const int Concrete = 2, Gravel = 3, Plaster = 5, Brick = 6, Glass = 7, Metal = 8, Wood = 9, ClayTiles = 10, Slate = 11;

        enum Style { Plain = 0, Home = 1, Office = 2, Industrial = 3 }

        /// <summary>Parts of the world that build alike, for what isn't tagged.</summary>
        enum Region { Europe, Russia, NorthAmerica, SunBelt, Arid, EastAsia, Other }

        static Color32 C(byte r, byte g, byte b) => new Color32(r, g, b, 255);

        // Wall colours by material (and region where that matters).
        static readonly Color32[] PlasterColours =
        {
            C(236, 232, 222), C(226, 218, 200), C(214, 200, 176), C(232, 214, 170), C(220, 196, 182), C(205, 214, 196),
            C(198, 208, 218), C(210, 190, 160), C(190, 190, 186), C(228, 190, 150), C(240, 238, 232),
        };
        static readonly Color32[] AridPlaster = { C(222, 206, 178), C(232, 220, 196), C(208, 186, 150), C(238, 234, 224), C(200, 176, 140), C(226, 212, 190) };
        static readonly Color32[] StuccoColours = { C(226, 216, 196), C(214, 200, 180), C(236, 232, 224), C(200, 190, 170), C(222, 206, 184) };
        static readonly Color32[] StoneColours = { C(204, 194, 174), C(186, 180, 170), C(214, 206, 190), C(170, 166, 158) };
        static readonly Color32[] BrickColours = { C(152, 80, 62), C(130, 66, 52), C(168, 98, 72), C(112, 64, 52), C(196, 166, 112), C(170, 120, 90) };
        static readonly Color32[] RussianBrick = { C(214, 208, 196), C(214, 208, 196), C(206, 200, 188), C(152, 80, 62), C(140, 72, 58), C(152, 80, 62), C(196, 166, 112), C(130, 66, 52) };
        static readonly Color32[] ConcreteColours = { C(190, 190, 184), C(176, 178, 176), C(204, 200, 190), C(160, 162, 164), C(214, 212, 204), C(206, 196, 176), C(170, 176, 184) };
        static readonly Color32[] GlassColours = { C(78, 96, 112), C(64, 86, 104), C(88, 98, 104), C(66, 90, 86), C(100, 90, 78), C(110, 130, 150) };
        static readonly Color32[] MetalWalls = { C(178, 180, 182), C(150, 160, 170), C(200, 200, 196), C(120, 138, 156), C(156, 72, 58), C(96, 120, 98), C(210, 196, 160) };
        static readonly Color32[] SidingColours = { C(232, 232, 226), C(222, 212, 184), C(184, 198, 212), C(176, 176, 170), C(224, 208, 150), C(172, 186, 160), C(150, 120, 96) };
        static readonly Color32[] TimberColours = { C(140, 56, 44), C(114, 84, 62), C(96, 128, 92), C(212, 184, 112), C(110, 140, 172), C(180, 160, 130), C(160, 120, 80) };

        // Roof colours.
        static readonly Color32[] ClayColours = { C(172, 92, 66), C(152, 78, 58), C(184, 108, 78), C(124, 68, 52), C(116, 108, 104), C(96, 60, 50) };
        static readonly Color32[] AsianTiles = { C(100, 104, 110), C(80, 84, 90), C(120, 80, 60), C(92, 96, 104) };
        static readonly Color32[] SlateColours = { C(74, 78, 84), C(62, 64, 70), C(88, 90, 96) };
        static readonly Color32[] ShingleColours = { C(78, 78, 80), C(96, 88, 82), C(60, 60, 62), C(112, 104, 96), C(84, 72, 64), C(70, 76, 84) };
        static readonly Color32[] RussianMetal = { C(88, 120, 90), C(130, 62, 52), C(150, 150, 152), C(72, 88, 112), C(112, 86, 72), C(70, 70, 72), C(150, 150, 146), C(136, 136, 132) };
        static readonly Color32[] MetalRoofs = { C(120, 120, 122), C(150, 150, 152), C(110, 70, 60), C(80, 100, 80), C(60, 64, 70) };
        static readonly Color32[] EternitColours = { C(150, 150, 146), C(136, 136, 132), C(160, 158, 150) };
        static readonly Color32[] GravelColours = { C(128, 126, 122), C(110, 110, 108), C(146, 142, 136), C(96, 96, 98), C(136, 130, 120) };
        static readonly Color32[] FlatConcrete = { C(160, 160, 156), C(140, 140, 138), C(176, 174, 168) };
        static readonly Color32[] GreenRoof = { C(92, 112, 70), C(104, 120, 76) };

        /// <summary>Per-worker buffers, so meshing a tile doesn't feed the garbage collector.</summary>
        sealed class Scratch
        {
            public double[] Flat = new double[2 * 256];
            public double[] Whole = new double[2 * 256];
            public double[] Hull = new double[2 * 260];
            public double[] FaceXY = new double[2 * 256];
            public double[] Offset = new double[2 * 256];
            public readonly double[] Ts = new double[64], Hs = new double[64];
            public readonly List<int> HoleStarts = new List<int>(), NoHoles = new List<int>();
            public readonly List<int> Counts = new List<int>(); // points of each ring of the footprint being built
            public readonly List<int> Tris = new List<int>();
            public readonly List<RoofPlane> Planes = new List<RoofPlane>();
            public readonly List<double> Face = new List<double>(), Work = new List<double>();

            public void Ensure(int n)
            {
                if (Flat.Length < 2 * n)
                {
                    int size = Math.Max(n, Flat.Length);
                    Flat = new double[4 * size];
                    Whole = new double[4 * size];
                    Hull = new double[4 * size + 16];
                    Offset = new double[4 * size];
                }
            }

            public double[] FaceArray(int n)
            {
                if (FaceXY.Length < 2 * n)
                    FaceXY = new double[4 * n];
                return FaceXY;
            }
        }

        [ThreadStatic] static Scratch scratch;

        /// <summary>One building's walls and roof, as worked out before meshing.</summary>
        struct Plan
        {
            public Style Style;
            public RoofShape Shape;
            public double Overhang;                // how far a pitched roof reaches past the walls (m)
            public double Ground, Bottom, Eaves;   // radii
            public float Height;                   // to the top, above the ground
            public int WallLayer, RoofLayer;
            public Color32 WallColour, RoofColour;
            public float FloorHeight, CellWidth;   // windows
            public bool Shops;
            public float Imagery;                  // how much satellite imagery the roof shows
        }

        /// <summary>minSize: buildings narrower and lower than this (m) are left out (far off);
        /// eaves: pitched roofs reach out past the walls; pitched: roofs other than flat at all;
        /// boxes: nearly rectangular footprints are built as their rectangle (fewer walls, far off).</summary>
        public static BuildingMeshData Build(OsmTile tile, HeightGrid grid, List<Exclusion> exclusions, bool guessRoofs = true,
                                             double minSize = 0.0, bool eaves = true, bool pitched = true, bool boxes = false)
        {
            Scratch s = scratch ??= new Scratch();
            TileFrame frame = TileFrame.For(tile, grid);
            double r0 = frame.Radius;
            Region region = RegionOf(frame.CenterLat, frame.CenterLon);
            // Walls, eaves and roofs: 6 (flat roofs) to 10 (houses with pitched roofs) vertices an
            // outline point in full detail; further off (minSize) small buildings are left out.
            int points = 0;
            foreach (OsmBuilding b in tile.Buildings)
                foreach (int[] ring in b.Rings)
                    points += ring.Length / 2;
            BuildingMeshData mesh = minSize > 0.0 ? BuildingMeshData.Rent(points * 2, false) : BuildingMeshData.Rent(points * 8, true);
            mesh.CenterLat = frame.CenterLat;
            mesh.CenterLon = frame.CenterLon;
            mesh.Origin = frame.Origin;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            // A point of the ground plane at a radius.
            Vector3 At(double x, double y, double radius)
            {
                frame.Unplane(x, y, out double dx, out double dy, out double dz);
                Vector3 v = frame.Local(dx, dy, dz, radius);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                return v;
            }
            Func<double, double, double, Vector3> at = At; // one delegate for the whole tile

            foreach (OsmBuilding b in tile.Buildings)
            {
                int n = 0;
                foreach (int[] ring in b.Rings)
                    n += ring.Length / 2;
                int[] outline = b.Whole ?? b.Rings[0];
                int nw = outline.Length / 2;
                if (n < 3 || nw < 3)
                    continue;
                s.Ensure(Math.Max(n, nw) + 4);

                // The whole outline: ground level, where it is, its shape.
                double gMin = double.MaxValue, gMax = double.MinValue;
                double cx = 0, cy = 0, cz = 0;
                for (int i = 0; i < nw; i++)
                {
                    double lat = outline[2 * i] * 1e-7, lon = outline[2 * i + 1] * 1e-7;
                    TileFrame.Direction(lat, lon, out double dx, out double dy, out double dz);
                    double g = grid.At(lat, lon);
                    gMin = Math.Min(gMin, g);
                    gMax = Math.Max(gMax, g);
                    cx += dx;
                    cy += dy;
                    cz += dz;
                    frame.Plane(dx, dy, dz, r0, out s.Whole[2 * i], out s.Whole[2 * i + 1]);
                }
                if (Excluded(exclusions, cx, cy, cz))
                    continue;

                // This tile's piece: outer ring, then holes.
                s.HoleStarts.Clear();
                s.Counts.Clear();
                int k = 0;
                for (int r = 0; r < b.Rings.Count; r++)
                {
                    int[] ring = b.Rings[r];
                    if (r > 0)
                        s.HoleStarts.Add(k);
                    s.Counts.Add(ring.Length / 2);
                    for (int i = 0; i < ring.Length; i += 2, k++)
                    {
                        TileFrame.Direction(ring[i] * 1e-7, ring[i + 1] * 1e-7, out double dx, out double dy, out double dz);
                        frame.Plane(dx, dy, dz, r0, out s.Flat[2 * k], out s.Flat[2 * k + 1]);
                    }
                }
                double area = 0.0;
                k = 0;
                for (int r = 0; r < s.Counts.Count; r++)
                {
                    int count = s.Counts[r];
                    double a = Roofs.Area(s.Flat, k, count);
                    area += r == 0 ? Math.Abs(a) : -Math.Abs(a);
                    k += count;
                }
                if (area < MinArea)
                    continue;
                double wholeArea = b.Whole != null ? Math.Abs(Roofs.Area(s.Whole, 0, nw)) : Math.Abs(Roofs.Area(s.Flat, 0, s.Counts[0]));
                if (boxes && b.Whole == null && s.Counts.Count == 1 && n > 4 && Boxed(s, ref n, ref nw, area))
                    wholeArea = area = Math.Abs(Roofs.Area(s.Flat, 0, 4));

                if (!Prepare(b, s, nw, wholeArea, gMin, gMax, region, guessRoofs, pitched, out Plan plan))
                    continue;
                if (minSize > 0.0 && plan.Height < minSize && Math.Sqrt(wholeArea) < minSize)
                    continue; // a pixel or less from where it is seen
                if (!eaves)
                    plan.Overhang = 0.0;
                ulong seed = Mix((ulong)b.Id);
                byte seedByte = (byte)(seed >> 24);
                plan.WallColour.a = seedByte;
                plan.RoofColour.a = seedByte;

                Walls(mesh, s, plan, at);
                if (s.Planes.Count == 0)
                {
                    s.Tris.Clear();
                    Earcut.Triangulate(s.Flat, n, s.HoleStarts, s.Tris);
                    Cap(mesh, s.Flat, n, s.Tris, plan.Eaves, true, plan.RoofColour, 9f + plan.Imagery, plan.RoofLayer, at);
                }
                else
                {
                    Roof(mesh, s, s.Counts[0], plan, at);
                    mesh.Pitched++;
                }
                if (plan.Bottom > plan.Ground + 0.01)
                {
                    // Under a part that floats: a floor.
                    s.Tris.Clear();
                    Earcut.Triangulate(s.Flat, n, s.HoleStarts, s.Tris);
                    Cap(mesh, s.Flat, n, s.Tris, plan.Bottom, false, plan.WallColour, 0f, Concrete, at);
                }
                mesh.Buildings++;
            }

            if (mesh.Buildings > 0)
            {
                var bounds = new Bounds();
                bounds.SetMinMax(min, max);
                mesh.Bounds = bounds;
            }
            return mesh;
        }

        /// <summary>Heights, roof, materials, colours and windows of a building; its roof
        /// planes into s.Planes (none for a flat roof). False if there is nothing to build.</summary>
        static bool Prepare(OsmBuilding b, Scratch s, int nw, double area, double gMin, double gMax, Region region, bool guessRoofs,
                            bool pitched, out Plan plan)
        {
            plan = default;
            ulong seed = Mix((ulong)b.Id);
            float jitter = 0.85f + 0.3f * (seed & 0xFFFF) / 65535f;
            string type = b.Type ?? "";

            // The outline's shape: pitched roofs only on simple ones (no courtyard, nearly convex).
            int h = Roofs.Hull(s.Whole, nw, s.Hull);
            double hullArea = h >= 3 ? Roofs.Area(s.Hull, 0, h) : 0.0;
            bool simple = pitched && !b.Cut && b.Rings.Count == 1 && hullArea > 1.0 && area / hullArea >= 0.8;
            RoofBox box = default;
            if (simple)
            {
                h = Roofs.Simplify(s.Hull, h, 0.25);
                box = Roofs.Box(s.Hull, h);
                simple = h >= 3 && box.HalfWidth >= 1.0;
            }

            bool heightTagged = !float.IsNaN(b.Height) && b.Height > 0f;
            bool levelsTagged = !float.IsNaN(b.Levels) && b.Levels >= 1f;
            double guess = heightTagged ? b.Height
                : levelsTagged ? b.Levels * LevelHeight + 0.5
                : DefaultHeight(type, area) * jitter;
            // Parts nothing is known of are mostly towers, domes and walls of landmarks: no windows.
            Style style = type.Length > 0 ? StyleOf(type) : b.Part ? Style.Plain : GuessStyle((float)guess, area);

            RoofShape shape = Roofs.Parse(b.RoofShape);
            if (shape == RoofShape.Flat && b.RoofShape == null && guessRoofs && simple)
                shape = GuessShape(type, guess, area, box, region, seed);
            if (!simple)
                shape = RoofShape.Flat;

            double roofHeight = 0.0;
            bool roofTagged = false;
            if (shape != RoofShape.Flat)
            {
                if (!float.IsNaN(b.RoofHeight) && b.RoofHeight > 0.2f)
                {
                    roofHeight = b.RoofHeight;
                    roofTagged = true;
                }
                else if (!float.IsNaN(b.RoofLevels) && b.RoofLevels > 0f)
                {
                    roofHeight = Math.Max(1.5, b.RoofLevels * 2.8);
                }
                else
                {
                    roofHeight = DefaultRoofHeight(shape, box, type, region, seed);
                }
            }

            double minHeight = !float.IsNaN(b.MinHeight) ? b.MinHeight : !float.IsNaN(b.MinLevel) ? b.MinLevel * LevelHeight : 0.0;
            double height, eaves;
            if (heightTagged)
            {
                height = b.Height;
                if (!roofTagged)
                    roofHeight = Math.Min(roofHeight, 0.5 * Math.Max(height - minHeight, 0.0));
                eaves = height - roofHeight;
            }
            else if (levelsTagged)
            {
                eaves = b.Levels * LevelHeight + 0.5;
                height = eaves + roofHeight;
            }
            else
            {
                height = guess;
                eaves = Math.Max(height - roofHeight, Math.Min(2.6, height));
                height = eaves + roofHeight;
            }
            height = Math.Min(Math.Max(height, 2.0), 1000.0);
            eaves = Math.Min(Math.Max(eaves, 0.5), height);
            roofHeight = height - eaves;
            if (roofHeight < 0.3)
                shape = RoofShape.Flat;
            if (type == "roof" && minHeight <= 0.0)
                minHeight = eaves - 1.0; // canopies: a slab on (invisible) posts
            minHeight = Math.Min(Math.Max(minHeight, 0.0), eaves);

            s.Planes.Clear();
            if (shape != RoofShape.Flat)
            {
                Roofs.Planes(shape, s.Hull, h, box, roofHeight, b.RoofDirection, b.RoofOrientation == 2, s.Planes);
                Roofs.Lift(s.Planes, s.Whole, 0, nw);
            }

            double ground = 0.5 * (gMin + gMax);
            double eavesRadius = ground + eaves;
            // On a slope the roof stays clear of the uphill ground.
            eavesRadius += Math.Max(0.0, gMax + Math.Min(eaves, 2.0) - eavesRadius);
            double bottom = minHeight > 0.0 ? ground + minHeight : gMin - Sink;
            if (bottom > eavesRadius + (s.Planes.Count > 0 ? roofHeight : 0.0) - 0.3)
                return false;

            if (height < 2.6 && style != Style.Plain)
                style = Style.Plain;
            plan.Style = style;
            plan.Shape = s.Planes.Count > 0 ? shape : RoofShape.Flat;
            // Eaves reach out past the walls; not where the tile cut the building (the piece in
            // the next tile meets it there) nor on domes and the like.
            bool overhangs = shape == RoofShape.Gabled || shape == RoofShape.Hipped || shape == RoofShape.HalfHipped ||
                             shape == RoofShape.Pyramidal || shape == RoofShape.Skillion || shape == RoofShape.Gambrel;
            plan.Overhang = overhangs && b.Whole == null && s.Planes.Count > 0 ? Math.Min(Math.Max(0.08 * box.HalfWidth, 0.2), 0.6) : 0.0;
            plan.Ground = ground;
            plan.Bottom = bottom;
            plan.Eaves = eavesRadius;
            plan.Height = (float)height;
            plan.Imagery = Mathf.Clamp01((RoofImageryNone - (float)height) / (RoofImageryNone - RoofImageryFull));

            // Walls.
            int wall = WallLayer(b.WallMaterial, out bool stone);
            if (wall < 0)
                wall = GuessWall(style, type, height, region, seed);
            plan.WallLayer = wall;
            plan.WallColour = b.WallColor != 0 ? Tagged(b.WallColor, wall == Glass ? 0.3f : 1f, GlassColours[0])
                : Pick(WallColours(wall, region, stone), seed >> 32);

            // Roof.
            bool eternit;
            if (s.Planes.Count > 0)
            {
                int roof = RoofLayer(b.RoofMaterial, out eternit, out bool green);
                if (roof < 0)
                    roof = GuessRoof(region, seed, out eternit);
                plan.RoofLayer = roof;
                plan.RoofColour = b.RoofColor != 0 ? Tagged(b.RoofColor, 1f, default)
                    : Pick(green ? GreenRoof : RoofColours(roof, region, eternit), seed >> 40);
            }
            else
            {
                int roof = FlatRoofLayer(b.RoofMaterial, out bool green);
                if (roof < 0)
                    roof = ((seed >> 44) & 3) == 0 ? Concrete : Gravel;
                plan.RoofLayer = roof;
                Color32[] palette = green ? GreenRoof : roof == Concrete ? FlatConcrete : roof == Metal ? MetalRoofs : roof == Glass ? GlassColours : GravelColours;
                plan.RoofColour = b.RoofColor != 0 ? Tagged(b.RoofColor, 1f, default) : Pick(palette, seed >> 40);
            }

            // Windows: storeys as tagged, or as usual for the style.
            float levels = levelsTagged ? b.Levels - (float.IsNaN(b.MinLevel) ? 0f : b.MinLevel) : 0f;
            double wallHeight = eaves - minHeight;
            float floor = levels >= 1f ? (float)(wallHeight / levels) : style == Style.Office ? 3.6f : style == Style.Industrial ? 5.5f : 3.0f;
            plan.FloorHeight = style == Style.Plain || wallHeight < 2.2 ? 0f : Mathf.Clamp(floor, 2.6f, 6f);
            plan.CellWidth = style == Style.Office ? 2.6f : style == Style.Industrial ? 6f : 3.2f * (0.9f + 0.25f * ((seed >> 52) & 0xFF) / 255f);
            plan.Shops = minHeight <= 0.0 && plan.FloorHeight > 0f && wall != Glass &&
                         (type == "commercial" || type == "retail" || type == "mixed_use" || type == "supermarket" ||
                          style == Style.Office && ((seed >> 56) & 1) == 0 ||
                          type == "apartments" && levels >= 5f && (region == Region.Europe || region == Region.Russia) && ((seed >> 57) & 3) == 0);
            return true;
        }

        // ------------------------------------------------------------------ meshing

        /// <summary>Walls ring by ring: outer rings counter-clockwise and holes clockwise seen
        /// from above, so every wall faces away from the building's inside; their tops follow the
        /// roof (gables up to the ridge).</summary>
        static void Walls(BuildingMeshData mesh, Scratch s, Plan plan, Func<double, double, double, Vector3> at)
        {
            int k = 0;
            var style = (float)plan.Style;
            float eaves = (float)(plan.Eaves - plan.Ground);
            float vb = (float)(plan.Bottom - plan.Ground);
            for (int r = 0; r < s.Counts.Count; r++)
            {
                int count = s.Counts[r];
                bool ccw = Roofs.Area(s.Flat, k, count) > 0;
                bool reverse = (r == 0) != ccw;
                bool pitched = r == 0 && s.Planes.Count > 0;
                double u = 0.0;
                for (int i = 0; i < count; i++)
                {
                    int ia = k + (reverse ? count - 1 - i : i);
                    int ib = k + (reverse ? (2 * count - 2 - i) % count : (i + 1) % count);
                    double ax = s.Flat[2 * ia], ay = s.Flat[2 * ia + 1], bx = s.Flat[2 * ib], by = s.Flat[2 * ib + 1];
                    double ddx = bx - ax, ddn = by - ay;
                    double len = Math.Sqrt(ddx * ddx + ddn * ddn);
                    if (len < 0.05)
                        continue;
                    var normal = new Vector3((float)(ddn / len), 0f, (float)(-ddx / len));
                    int windows = plan.FloorHeight > 0f && len >= 0.6 * plan.CellWidth ? Math.Max(1, (int)Math.Round(len / plan.CellWidth)) : 0;
                    float floor = windows > 0 ? plan.FloorHeight : 0f;
                    int m;
                    if (pitched)
                    {
                        m = Roofs.Profile(s.Planes, ax, ay, bx, by, s.Ts, s.Hs);
                    }
                    else
                    {
                        s.Ts[0] = 0.0;
                        s.Hs[0] = 0.0;
                        s.Ts[1] = 1.0;
                        s.Hs[1] = 0.0;
                        m = 2;
                    }
                    for (int q = 0; q + 1 < m; q++)
                    {
                        double t0 = s.Ts[q], t1 = s.Ts[q + 1];
                        if (t1 - t0 < 1e-6)
                            continue;
                        double x0 = ax + ddx * t0, y0 = ay + ddn * t0, x1 = ax + ddx * t1, y1 = ay + ddn * t1;
                        double top0 = plan.Eaves + s.Hs[q], top1 = plan.Eaves + s.Hs[q + 1];
                        int v = mesh.Count;
                        float u0 = (float)(u + len * t0), u1 = (float)(u + len * t1);
                        float vt0 = (float)(top0 - plan.Ground), vt1 = (float)(top1 - plan.Ground);
                        float w0 = (float)(windows * t0), w1 = (float)(windows * t1);
                        float bay = windows > 0 ? (float)(len / windows) : 1f, last = plan.Shops ? -bay : bay;
                        var win0 = new Vector4(w0, floor, eaves, last);
                        var win1 = new Vector4(w1, floor, eaves, last);
                        mesh.Add(at(x0, y0, plan.Bottom), normal, plan.WallColour, new Vector4(u0, vb, style, plan.WallLayer), win0);
                        mesh.Add(at(x1, y1, plan.Bottom), normal, plan.WallColour, new Vector4(u1, vb, style, plan.WallLayer), win1);
                        mesh.Add(at(x1, y1, top1), normal, plan.WallColour, new Vector4(u1, vt1, style, plan.WallLayer), win1);
                        mesh.Add(at(x0, y0, top0), normal, plan.WallColour, new Vector4(u0, vt0, style, plan.WallLayer), win0);
                        // (a top, b top, b bottom), (a top, b bottom, a bottom): facing the normal.
                        mesh.Triangles.Add(v + 3);
                        mesh.Triangles.Add(v + 2);
                        mesh.Triangles.Add(v + 1);
                        mesh.Triangles.Add(v + 3);
                        mesh.Triangles.Add(v + 1);
                        mesh.Triangles.Add(v);
                    }
                    u += len;
                }
                k += count;
            }
        }

        /// <summary>
        /// A footprint (one ring, in s.Flat) that is nearly its smallest rectangle, replaced by
        /// that rectangle - far off, where a building is a few pixels, its four walls look the
        /// same as a dozen; true if it was. The whole outline (s.Whole) becomes the rectangle too.
        /// </summary>
        static bool Boxed(Scratch s, ref int n, ref int nw, double area)
        {
            int h = Roofs.Hull(s.Flat, n, s.Hull);
            if (h < 3)
                return false;
            RoofBox box = Roofs.Box(s.Hull, h);
            double boxArea = 4.0 * box.Half * box.HalfWidth;
            if (boxArea <= 0.0 || area / boxArea < 0.7)
                return false;
            // Counter-clockwise: V is left of U.
            double[] u = { -1, 1, 1, -1 }, v = { -1, -1, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                double x = box.Cx + box.Ux * box.Half * u[i] + box.Vx * box.HalfWidth * v[i];
                double y = box.Cy + box.Uy * box.Half * u[i] + box.Vy * box.HalfWidth * v[i];
                s.Flat[2 * i] = s.Whole[2 * i] = x;
                s.Flat[2 * i + 1] = s.Whole[2 * i + 1] = y;
            }
            n = nw = 4;
            s.Counts[0] = 4;
            return true;
        }

        /// <summary>A pitched roof: each plane's face, flat-shaded, its texture running along
        /// the eaves and up the slope; reaching out past the walls, with a soffit under that.</summary>
        static void Roof(BuildingMeshData mesh, Scratch s, int count, Plan plan, Func<double, double, double, Vector3> at)
        {
            float style = 11f + plan.Imagery;
            double[] outline = s.Flat;
            if (plan.Overhang > 0.0 && Eaves(s, count, plan, at, mesh))
                outline = s.Offset;
            for (int i = 0; i < s.Planes.Count; i++)
            {
                if (!Roofs.Face(s.Planes, i, outline, 0, count, s.Face, s.Work))
                    continue;
                int fn = s.Face.Count / 2;
                double[] xy = s.FaceArray(fn);
                s.Face.CopyTo(xy);
                s.Tris.Clear();
                Earcut.Triangulate(xy, fn, s.NoHoles, s.Tris);
                if (s.Tris.Count == 0)
                    continue;
                RoofPlane p = s.Planes[i];
                var normal = new Vector3((float)-p.A, 1f, (float)-p.B).normalized;
                Vector3 tangent = Vector3.Cross(normal, Vector3.up);
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.right;
                Vector3 binormal = Vector3.Cross(tangent, normal);
                int v0 = mesh.Count;
                for (int j = 0; j < fn; j++)
                {
                    double x = xy[2 * j], y = xy[2 * j + 1];
                    Vector3 v = at(x, y, plan.Eaves + p.At(x, y));
                    mesh.Add(v, normal, plan.RoofColour, new Vector4(Vector3.Dot(v, tangent), Vector3.Dot(v, binormal), style, plan.RoofLayer), Vector4.zero);
                }
                AddTriangles(mesh, xy, s.Tris, v0, true);
            }
        }

        /// <summary>
        /// The footprint pushed out by the overhang into s.Offset (mitred corners), and under the
        /// roof's edge all round a soffit facing down, from the walls' tops out to the eaves. False
        /// (no overhang) where a corner is too sharp to push out.
        /// </summary>
        static bool Eaves(Scratch s, int count, Plan plan, Func<double, double, double, Vector3> at, BuildingMeshData mesh)
        {
            double[] f = s.Flat, o = s.Offset;
            double d = plan.Overhang, sign = Roofs.Area(f, 0, count) > 0 ? 1.0 : -1.0;
            for (int i = 0; i < count; i++)
            {
                int a = (i + count - 1) % count, c = (i + 1) % count;
                // Outward normals of the edges before and after the corner.
                OutwardNormal(f, a, i, sign, out double n1x, out double n1y);
                OutwardNormal(f, i, c, sign, out double n2x, out double n2y);
                double cos = n1x * n2x + n1y * n2y;
                if (cos < -0.3)
                    return false;
                double k = d / (1.0 + cos);
                o[2 * i] = f[2 * i] + (n1x + n2x) * k;
                o[2 * i + 1] = f[2 * i + 1] + (n1y + n2y) * k;
            }
            var down = Vector3.down;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                double ax = f[2 * i], ay = f[2 * i + 1], bx = f[2 * j], by = f[2 * j + 1];
                double ex = bx - ax, ey = by - ay;
                if (ex * ex + ey * ey < 0.0025)
                    continue;
                int m = Roofs.Profile(s.Planes, ax, ay, bx, by, s.Ts, s.Hs);
                double oax = o[2 * i], oay = o[2 * i + 1], obx = o[2 * j], oby = o[2 * j + 1];
                for (int q = 0; q + 1 < m; q++)
                {
                    double t0 = s.Ts[q], t1 = s.Ts[q + 1];
                    if (t1 - t0 < 1e-6)
                        continue;
                    double x0 = ax + ex * t0, y0 = ay + ey * t0, x1 = ax + ex * t1, y1 = ay + ey * t1;
                    double u0x = oax + (obx - oax) * t0, u0y = oay + (oby - oay) * t0, u1x = oax + (obx - oax) * t1, u1y = oay + (oby - oay) * t1;
                    int v = mesh.Count;
                    Vector3 p0 = at(x0, y0, plan.Eaves + s.Hs[q]);
                    Vector3 p1 = at(x1, y1, plan.Eaves + s.Hs[q + 1]);
                    Vector3 p2 = at(u1x, u1y, plan.Eaves + Roofs.At(s.Planes, u1x, u1y));
                    Vector3 p3 = at(u0x, u0y, plan.Eaves + Roofs.At(s.Planes, u0x, u0y));
                    float layer = plan.WallLayer == Glass ? Concrete : plan.WallLayer;
                    mesh.Add(p0, down, plan.WallColour, new Vector4(p0.x, p0.z, 0f, layer), Vector4.zero);
                    mesh.Add(p1, down, plan.WallColour, new Vector4(p1.x, p1.z, 0f, layer), Vector4.zero);
                    mesh.Add(p2, down, plan.WallColour, new Vector4(p2.x, p2.z, 0f, layer), Vector4.zero);
                    mesh.Add(p3, down, plan.WallColour, new Vector4(p3.x, p3.z, 0f, layer), Vector4.zero);
                    // Facing down: counter-clockwise seen from above (the quad runs along the
                    // wall, then out: clockwise for a counter-clockwise ring).
                    bool ccw = sign > 0;
                    mesh.Triangles.Add(v);
                    mesh.Triangles.Add(ccw ? v + 3 : v + 1);
                    mesh.Triangles.Add(v + 2);
                    mesh.Triangles.Add(v);
                    mesh.Triangles.Add(v + 2);
                    mesh.Triangles.Add(ccw ? v + 1 : v + 3);
                }
            }
            return true;
        }

        static void OutwardNormal(double[] f, int a, int b, double sign, out double nx, out double ny)
        {
            double ex = f[2 * b] - f[2 * a], ey = f[2 * b + 1] - f[2 * a + 1];
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-9)
            {
                nx = ny = 0.0;
                return;
            }
            // Right of the edge for a counter-clockwise ring, left for a clockwise one.
            nx = sign * ey / len;
            ny = -sign * ex / len;
        }

        /// <summary>A flat cap over the footprint (with its holes): a roof facing up, or a floor down.</summary>
        static void Cap(BuildingMeshData mesh, double[] flat, int n, List<int> tris, double radius, bool up, Color32 color, float style, int layer,
                        Func<double, double, double, Vector3> at)
        {
            if (tris.Count == 0)
                return;
            int v0 = mesh.Count;
            Vector3 normal = up ? Vector3.up : Vector3.down;
            for (int k = 0; k < n; k++)
            {
                Vector3 v = at(flat[2 * k], flat[2 * k + 1], radius);
                mesh.Add(v, normal, color, new Vector4(v.x, v.z, style, layer), Vector4.zero);
            }
            AddTriangles(mesh, flat, tris, v0, up);
        }

        static void AddTriangles(BuildingMeshData mesh, double[] flat, List<int> tris, int v0, bool up)
        {
            for (int t = 0; t < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                // Upward normal is (b - a) x (c - a) having a positive y (x east, z north).
                double cross = (flat[2 * b + 1] - flat[2 * a + 1]) * (flat[2 * c] - flat[2 * a]) -
                               (flat[2 * b] - flat[2 * a]) * (flat[2 * c + 1] - flat[2 * a + 1]);
                if (Math.Abs(cross) < 1e-6)
                    continue; // zero-area sliver
                if ((cross < 0) == up)
                {
                    int tmp = b;
                    b = c;
                    c = tmp;
                }
                mesh.Triangles.Add(v0 + a);
                mesh.Triangles.Add(v0 + b);
                mesh.Triangles.Add(v0 + c);
            }
        }

        public static bool Excluded(List<Exclusion> exclusions, double x, double y, double z)
        {
            if (exclusions == null || exclusions.Count == 0)
                return false;
            double len = Math.Sqrt(x * x + y * y + z * z);
            foreach (Exclusion e in exclusions)
                if ((x * e.X + y * e.Y + z * e.Z) / len > e.CosAngle)
                    return true;
            return false;
        }

        public static ulong Mix(ulong x)
        {
            // splitmix64 finaliser
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        static Color32 Pick(Color32[] palette, ulong r)
        {
            Color32 c = palette[(int)(r % (ulong)palette.Length)];
            int shade = (int)((r >> 8) % 17) - 8; // a little variety within a colour
            return new Color32(Clamp(c.r + shade), Clamp(c.g + shade), Clamp(c.b + shade), 255);
        }

        /// <summary>A tagged colour, toned down: OSM "red" or "blue" rarely looks like that from
        /// the air; blended with `with` by 1 - weight (glass keeps most of its own colour).</summary>
        static Color32 Tagged(uint rgba, float weight, Color32 with)
        {
            int r = (int)(rgba >> 24), g = (int)((rgba >> 16) & 0xFF), b = (int)((rgba >> 8) & 0xFF);
            int grey = (r * 30 + g * 59 + b * 11) / 100;
            r = grey + (r - grey) * 3 / 4;
            g = grey + (g - grey) * 3 / 4;
            b = grey + (b - grey) * 3 / 4;
            int lift = Math.Max(0, 60 - Math.Max(r, Math.Max(g, b)));
            r += lift;
            g += lift;
            b += lift;
            if (weight < 1f)
            {
                r = (int)(with.r + (r - with.r) * weight);
                g = (int)(with.g + (g - with.g) * weight);
                b = (int)(with.b + (b - with.b) * weight);
            }
            return new Color32(Clamp(r), Clamp(g), Clamp(b), 255);
        }

        static byte Clamp(int v) => (byte)Math.Max(0, Math.Min(255, v));

        // ------------------------------------------------------------------ what it looks like

        static Region RegionOf(double lat, double lon)
        {
            if (lat >= 24 && lat <= 33.5 && lon >= -125 && lon <= -75)
                return Region.SunBelt; // the southern US: stucco on block, hipped roofs
            if (lat >= 24 && lat <= 72 && lon >= -170 && lon <= -52)
                return Region.NorthAmerica;
            if (lat >= 14 && lat <= 32.5 && lon >= -118 && lon <= -86)
                return Region.Arid; // Mexico
            if (lat >= 12 && lat <= 37 && lon >= -18 && lon <= 63)
                return Region.Arid; // North Africa, Arabia, the Middle East
            if (lat >= 24 && lat <= 40 && lon >= 44 && lon <= 70)
                return Region.Arid; // Iran, Afghanistan, Pakistan
            if (lat >= 6 && lat <= 30 && lon >= 68 && lon <= 90)
                return Region.Arid; // India
            if (lat >= 44 && lon >= 24)
                return Region.Russia; // and its neighbours: panel blocks, metal roofs
            if (lat >= 35 && lat <= 72 && lon >= -25 && lon < 45)
                return Region.Europe;
            if (lat >= 18 && lat <= 46 && lon >= 100 && lon <= 146)
                return Region.EastAsia;
            return Region.Other;
        }

        /// <summary>A pitched roof for an untagged building, where such buildings mostly have one.</summary>
        static RoofShape GuessShape(string type, double height, double area, RoofBox box, Region region, ulong seed)
        {
            double chance = PitchedChance(type, height, area, box, region);
            if (((seed >> 48) & 0xFFFF) / 65535.0 >= chance)
                return RoofShape.Flat;
            double pick = ((seed >> 20) & 0xFFFF) / 65535.0;
            double hipped = region == Region.SunBelt ? 0.6 : region == Region.NorthAmerica ? 0.45 : region == Region.Russia ? 0.4
                : region == Region.EastAsia ? 0.5 : 0.3;
            if (pick < hipped)
                return RoofShape.Hipped;
            if (region == Region.Europe && pick < hipped + 0.06)
                return RoofShape.HalfHipped;
            return RoofShape.Gabled;
        }

        static double PitchedChance(string type, double height, double area, RoofBox box, Region region)
        {
            if (box.HalfWidth > 15.0)
                return 0.0; // wider than 30 m: a flat roof, or a hall's too low to see
            bool arid = region == Region.Arid;
            switch (type)
            {
                case "house": case "detached": case "semidetached_house": case "bungalow": case "cabin": case "farm": case "hut":
                case "static_caravan": case "chapel": case "church": case "dacha": case "allotment_house": case "barn": case "stable":
                case "cowshed": case "sty": case "farm_auxiliary": case "shed": case "boathouse": case "villa": case "chalet":
                    return arid ? 0.15 : 0.95;
                case "terrace":
                    return arid ? 0.1 : 0.8;
                case "garage": case "garages": case "carport":
                    return region == Region.NorthAmerica || region == Region.SunBelt ? 0.6 : 0.15;
                case "residential": case "apartments":
                    return !arid && height <= 12.0 && area < 600.0 ? 0.5 : 0.0;
                case "school": case "kindergarten": case "public": case "civic": case "religious": case "temple": case "townhall":
                    return !arid && area < 1500.0 ? 0.35 : 0.0;
                case "industrial": case "warehouse": case "hangar": case "manufacture":
                    return !arid && box.HalfWidth <= 12.0 ? 0.2 : 0.0;
                case "": case "yes":
                    if (height > 11.0 || area > 450.0)
                        return !arid && height <= 12.0 && area <= 1500.0 ? 0.12 : 0.0;
                    if (area < 15.0)
                        return 0.2;
                    switch (region)
                    {
                        case Region.Europe: return 0.75;
                        case Region.Russia: return 0.8;
                        case Region.NorthAmerica: case Region.SunBelt: return 0.9;
                        case Region.EastAsia: return 0.6;
                        case Region.Arid: return 0.06;
                        default: return 0.45;
                    }
                default:
                    return 0.05;
            }
        }

        static double DefaultRoofHeight(RoofShape shape, RoofBox box, string type, Region region, ulong seed)
        {
            double r = ((seed >> 8) & 0xFFFF) / 65535.0;
            double pitch = region == Region.SunBelt ? 20.0 + 10.0 * r
                : region == Region.NorthAmerica ? 22.0 + 13.0 * r
                : region == Region.Europe || region == Region.Russia ? 28.0 + 14.0 * r
                : region == Region.EastAsia ? 25.0 + 10.0 * r
                : 22.0 + 12.0 * r;
            bool church = type == "church" || type == "chapel" || type == "cathedral";
            if (church)
                pitch = 45.0 + 8.0 * r;
            else if (box.HalfWidth > 9.0)
                pitch = Math.Min(pitch, 18.0 + 8.0 * r); // halls
            double w = box.HalfWidth, t = Math.Tan(pitch * Math.PI / 180.0), height;
            switch (shape)
            {
                case RoofShape.Skillion: height = 2.0 * w * Math.Tan(10.0 * Math.PI / 180.0); break;
                case RoofShape.Pyramidal: height = w * Math.Tan((pitch + 5.0) * Math.PI / 180.0); break;
                case RoofShape.Dome: height = 0.85 * Math.Sqrt(box.Half * box.Half + w * w); break;
                case RoofShape.Onion: height = 1.4 * Math.Sqrt(box.Half * box.Half + w * w); break;
                case RoofShape.Round: height = 0.6 * w; break;
                case RoofShape.Gambrel: case RoofShape.Mansard: height = Math.Min(w * Math.Tan(48.0 * Math.PI / 180.0), 6.0); break;
                default: height = w * t; break;
            }
            return Math.Min(Math.Max(height, 0.8), church ? 20.0 : 12.0);
        }

        /// <summary>Wall layer for building:material / facade_material; -1 if not one we know.</summary>
        static int WallLayer(string material, out bool stone)
        {
            stone = false;
            switch (material)
            {
                case "brick": case "bricks": case "clay":
                    return Brick;
                case "concrete": case "cement_block": case "reinforced_concrete": case "panel": case "concrete_panel": case "block":
                    return Concrete;
                case "glass": case "mirror": case "glass_curtain":
                    return Glass;
                case "metal": case "steel": case "aluminium": case "aluminum": case "metal_plates": case "copper": case "tin":
                    return Metal;
                case "wood": case "timber": case "timber_framing": case "log": case "wooden": case "vinyl": case "plastic": case "siding":
                    return Wood;
                case "stone": case "sandstone": case "limestone": case "granite": case "marble":
                    stone = true;
                    return Plaster;
                case "plaster": case "stucco": case "render": case "plastered": case "rendered": case "tiles": case "adobe": case "mud":
                    return Plaster;
                default:
                    return -1;
            }
        }

        static int GuessWall(Style style, string type, double height, Region region, ulong seed)
        {
            double r = ((seed >> 28) & 0xFFFF) / 65535.0;
            switch (style)
            {
                case Style.Office:
                    if (height >= 30.0 || type == "office")
                        return r < 0.55 ? Glass : r < 0.85 ? Concrete : Plaster;
                    return r < 0.45 ? Plaster : r < 0.7 ? Concrete : r < 0.85 ? Brick : Glass;
                case Style.Industrial:
                    return r < 0.55 ? Metal : r < 0.8 ? Concrete : Brick;
                case Style.Plain:
                    if (region == Region.NorthAmerica || region == Region.SunBelt)
                        return r < 0.4 ? Wood : r < 0.7 ? Metal : Concrete;
                    return r < 0.35 ? Concrete : r < 0.6 ? Brick : r < 0.8 ? Metal : Wood;
            }
            bool tall = height >= 12.0 || type == "apartments" || type == "residential" && height > 9.0;
            switch (region)
            {
                case Region.NorthAmerica:
                    return tall ? (r < 0.5 ? Brick : r < 0.8 ? Plaster : Concrete) : (r < 0.45 ? Wood : r < 0.75 ? Brick : Plaster);
                case Region.SunBelt:
                    return tall ? (r < 0.5 ? Plaster : r < 0.75 ? Concrete : Brick) : (r < 0.6 ? Plaster : r < 0.8 ? Brick : Wood);
                case Region.Russia:
                    return tall ? (r < 0.45 ? Concrete : r < 0.8 ? Brick : Plaster) : (r < 0.35 ? Wood : r < 0.65 ? Brick : Plaster);
                case Region.Europe:
                    return tall ? (r < 0.5 ? Plaster : r < 0.75 ? Brick : Concrete) : (r < 0.55 ? Plaster : r < 0.9 ? Brick : Wood);
                case Region.Arid:
                    return r < 0.85 ? Plaster : Concrete;
                case Region.EastAsia:
                    return tall ? (r < 0.5 ? Concrete : r < 0.8 ? Plaster : Glass) : (r < 0.5 ? Plaster : r < 0.75 ? Concrete : Wood);
                default:
                    return r < 0.6 ? Plaster : r < 0.85 ? Concrete : Brick;
            }
        }

        static Color32[] WallColours(int layer, Region region, bool stone)
        {
            switch (layer)
            {
                case Brick: return region == Region.Russia ? RussianBrick : BrickColours;
                case Concrete: return ConcreteColours;
                case Glass: return GlassColours;
                case Metal: return MetalWalls;
                case Wood: return region == Region.NorthAmerica || region == Region.SunBelt ? SidingColours : TimberColours;
                default:
                    return stone ? StoneColours : region == Region.Arid ? AridPlaster
                        : region == Region.NorthAmerica || region == Region.SunBelt ? StuccoColours : PlasterColours;
            }
        }

        /// <summary>Pitched roof layer for roof:material / roof_material; -1 if not one we know.</summary>
        static int RoofLayer(string material, out bool eternit, out bool green)
        {
            eternit = green = false;
            switch (material)
            {
                case "roof_tiles": case "tile": case "tiles": case "clay": case "ceramic":
                    return ClayTiles;
                case "slate": case "stone": case "asphalt": case "asphalt_shingle": case "shingle": case "tar_paper": case "bitumen":
                case "wood": case "wood_shingle":
                    return Slate;
                case "metal": case "copper": case "zinc": case "tin": case "steel": case "metal_sheet": case "aluminium":
                    return Metal;
                case "eternit": case "asbestos": case "fibre_cement": case "cement": case "concrete":
                    eternit = true;
                    return Metal;
                case "glass":
                    return Glass;
                case "grass": case "plants": case "thatch":
                    green = material != "thatch";
                    return Gravel;
                default:
                    return -1;
            }
        }

        static int FlatRoofLayer(string material, out bool green)
        {
            green = false;
            switch (material)
            {
                case "concrete": case "cement": return Concrete;
                case "metal": case "copper": case "zinc": case "steel": case "metal_sheet": return Metal;
                case "glass": return Glass;
                case "grass": case "plants": green = true; return Gravel;
                case null: return -1;
                default: return Gravel; // gravel, tar paper, asphalt, bitumen, roofing felt...
            }
        }

        static int GuessRoof(Region region, ulong seed, out bool eternit)
        {
            double r = ((seed >> 36) & 0xFFFF) / 65535.0;
            eternit = false;
            switch (region)
            {
                case Region.Europe:
                    return r < 0.55 ? ClayTiles : r < 0.75 ? Slate : Metal;
                case Region.Russia:
                    eternit = r >= 0.6 && r < 0.85; // grey corrugated sheets
                    return r < 0.85 ? Metal : ClayTiles;
                case Region.NorthAmerica:
                    return r < 0.8 ? Slate : r < 0.92 ? Metal : ClayTiles; // asphalt shingles, mostly
                case Region.SunBelt:
                    return r < 0.65 ? Slate : r < 0.85 ? ClayTiles : Metal;
                case Region.EastAsia:
                    return r < 0.5 ? ClayTiles : r < 0.8 ? Metal : Slate;
                case Region.Arid:
                    return ClayTiles;
                default:
                    return r < 0.5 ? Metal : ClayTiles;
            }
        }

        static Color32[] RoofColours(int layer, Region region, bool eternit)
        {
            switch (layer)
            {
                case ClayTiles: return region == Region.EastAsia ? AsianTiles : ClayColours;
                case Slate: return region == Region.NorthAmerica || region == Region.SunBelt ? ShingleColours : SlateColours;
                case Metal: return eternit ? EternitColours : region == Region.Russia ? RussianMetal : MetalRoofs;
                case Glass: return GlassColours;
                case Concrete: return FlatConcrete;
                default: return GravelColours;
            }
        }

        static Style StyleOf(string type)
        {
            switch (type)
            {
                case "garage": case "garages": case "shed": case "carport": case "roof": case "hut": case "kiosk":
                case "silo": case "storage_tank": case "tank": case "transformer_tower": case "water_tower": case "bunker":
                case "greenhouse": case "barn": case "stable": case "cowshed": case "sty": case "farm_auxiliary":
                case "parking": case "service": case "toilets": case "ruins": case "construction": case "container":
                case "tent": case "digester": case "slurry_tank": case "bridge": case "wall": case "gasometer": case "military":
                    return Style.Plain;
                case "office": case "commercial": case "retail": case "supermarket": case "government": case "public":
                case "civic": case "hospital": case "university": case "school": case "college": case "train_station":
                case "transportation": case "kindergarten": case "stadium": case "sports_hall": case "museum": case "terminal":
                case "mixed_use":
                    return Style.Office;
                case "industrial": case "warehouse": case "manufacture": case "hangar": case "factory": case "depot":
                case "workshop":
                    return Style.Industrial;
                default:
                    return Style.Home;
            }
        }

        /// <summary>Vector tiles don't say what a building is: tall ones get office windows,
        /// big low halls industrial ones, sheds none.</summary>
        static Style GuessStyle(float height, double area) =>
            height >= 25f ? Style.Office
            : area >= 2500.0 && height <= 16f ? Style.Industrial
            : area < 40.0 ? Style.Plain
            : Style.Home;

        static float DefaultHeight(string type, double area)
        {
            switch (type)
            {
                case "garage": case "garages": case "shed": case "carport": case "hut": case "kiosk": case "toilets":
                case "container": case "tent": case "service": case "transformer_tower": case "bunker":
                    return 3f;
                case "roof": return 5f;
                case "greenhouse": return 4f;
                case "house": case "detached": case "semidetached_house": case "bungalow": case "cabin": case "farm":
                case "static_caravan": case "dacha":
                    return 6.5f;
                case "terrace": return 8f;
                case "barn": case "stable": case "cowshed": case "sty": case "farm_auxiliary": return 6f;
                case "apartments": case "dormitory": case "hotel": return area < 300 ? 10f : 16f;
                case "residential": return area < 200 ? 7f : 13f;
                case "office": return 18f;
                case "commercial": case "retail": case "supermarket": return 8f;
                case "industrial": case "manufacture": case "warehouse": case "factory": case "depot": case "workshop":
                    return 9f;
                case "hangar": return 14f;
                case "church": case "cathedral": case "chapel": case "mosque": case "temple": case "synagogue": case "religious":
                    return 14f;
                case "hospital": return 18f;
                case "school": case "university": case "college": case "public": case "civic": case "government":
                case "kindergarten":
                    return 11f;
                case "train_station": case "transportation": case "terminal": return 12f;
                case "silo": return 18f;
                case "storage_tank": case "tank": return 12f;
                case "stadium": return 25f;
                case "grandstand": return 10f;
                case "parking": return 10f;
                default:
                    // building=yes, the most common tag: guess from the footprint.
                    return area < 50 ? 3.5f : area < 250 ? 6.5f : area < 2000 ? 10f : 12f;
            }
        }
    }
}
