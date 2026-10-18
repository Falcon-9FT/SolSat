// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// Ribbons and fills (RibbonMeshData) for the clipmap's road layer, in the flat frame of the
    /// place they belong to (an OSM tile's roads and railways). The owner keeps the Unity mesh
    /// alive while it is registered with RoadLayer.
    /// </summary>
    sealed class GroundMesh
    {
        public readonly Mesh Mesh;
        public readonly TileFrame Frame;
        public readonly double MinX, MinY, MaxX, MaxY; // extent in the frame's plane, metres
        public readonly int Vertices;

        // Where the frame lies on the clipmap's plane, and the extent there (RoadLayer's).
        internal int Plane = -1;
        internal Matrix4x4 Matrix;
        internal double X0, Y0, X1, Y1;
        internal bool New;

        public GroundMesh(RibbonMeshData data, TileFrame frame, string name)
        {
            Mesh = RoadLayer.ToMesh(data, name);
            Frame = frame;
            MinX = data.MinX;
            MinY = data.MinY;
            MaxX = data.MaxX;
            MaxY = data.MaxY;
            Vertices = data.Count;
        }

        public void Destroy() => UnityEngine.Object.Destroy(Mesh);
    }

    /// <summary>
    /// Keeps the road layer in step with the ground meshes registered: new ones are drawn into
    /// every level they reach, a level whose window moved gets its new strip of ground drawn, a
    /// level new or reset is drawn whole. The layer keeps the maximum of what is drawn, so
    /// drawing something twice changes nothing; taking something out needs the levels drawn
    /// anew (Redraw), which waits a moment so several removals cost one redraw. Call Draw after
    /// the clipmap's Update, outside rendering.
    /// </summary>
    sealed class RoadLayer
    {
        const float RedrawDelay = 0.5f;

        readonly Func<Clipmap, GroundLayer> layerOf; // the clipmap's layer it draws into
        readonly HashSet<GroundMesh> meshes = new HashSet<GroundMesh>();
        readonly List<GroundMesh> added = new List<GroundMesh>();
        readonly int[] version = new int[Clipmap.MaxLevels];
        readonly Vector4[] strips = new Vector4[2];
        Clipmap drawnFor;
        int plane = -1;
        float redrawAt = -1f;

        public int Count => meshes.Count;

        /// <summary>layerOf: the clipmap layer drawn into (made on first use), e.g. c => c.EnsureRoads().</summary>
        public RoadLayer(Func<Clipmap, GroundLayer> layerOf)
        {
            this.layerOf = layerOf;
        }

        public void Add(GroundMesh m)
        {
            if (!meshes.Add(m))
                return;
            m.New = true;
            added.Add(m);
        }

        /// <summary>erase: what it drew must go now (it was switched off or is wrong); else it
        /// stays until its levels are drawn anew anyway, as it is still right.</summary>
        public void Remove(GroundMesh m, bool erase)
        {
            if (m == null || !meshes.Remove(m))
                return;
            if (m.New)
                added.Remove(m);
            m.New = false;
            if (erase)
                Redraw();
        }

        /// <summary>Every level drawn anew shortly, from what is registered then.</summary>
        public void Redraw()
        {
            if (redrawAt < 0f)
                redrawAt = Time.realtimeSinceStartup + RedrawDelay;
        }

        public void Draw(Clipmap clipmap, bool enabled)
        {
            if (clipmap == null || !clipmap.Active || !enabled)
            {
                for (int k = 0; k < Clipmap.MaxLevels; k++)
                    version[k] = -1;
                foreach (GroundMesh m in added)
                    m.New = false;
                added.Clear();
                redrawAt = -1f;
                return;
            }
            GroundLayer layer = layerOf(clipmap);
            if (clipmap != drawnFor || clipmap.PlaneVersion != plane)
            {
                drawnFor = clipmap;
                plane = clipmap.PlaneVersion;
                for (int k = 0; k < Clipmap.MaxLevels; k++)
                    version[k] = -1;
            }
            if (redrawAt >= 0f && Time.realtimeSinceStartup >= redrawAt)
            {
                redrawAt = -1f;
                for (int k = 0; k < Clipmap.MaxLevels; k++)
                    version[k] = -1;
            }
            bool whole = false;
            for (int k = 0; k < Clipmap.MaxLevels && !whole; k++)
                whole = clipmap.LevelReady(k) && version[k] != clipmap.LevelVersion(k);
            // Frames are fitted lazily: those of meshes about to be drawn.
            if (whole)
                foreach (GroundMesh m in meshes)
                    Fit(m, clipmap);
            else
                foreach (GroundMesh m in added)
                    Fit(m, clipmap);

            for (int k = 0; k < Clipmap.MaxLevels; k++)
            {
                if (!clipmap.LevelReady(k))
                {
                    version[k] = -1;
                    continue;
                }
                int v = clipmap.LevelVersion(k);
                double inv = 1.0 / clipmap.LevelWidth(k);
                Vector4 window = clipmap.LevelWindow(k);
                if (version[k] == v)
                {
                    foreach (GroundMesh m in added)
                        DrawMesh(layer, k, m, window, inv);
                }
                else if (version[k] >= 0 && clipmap.MovedOnceSince(k, version[k], out Vector4 old) &&
                         GroundLayer.Intersect(old, window, out Vector4 kept))
                {
                    layer.ClearExposed(k, old);
                    int n = GroundLayer.Exposed(old, window, strips);
                    foreach (GroundMesh m in meshes)
                        for (int i = 0; i < n; i++)
                            DrawMesh(layer, k, m, strips[i], inv);
                    foreach (GroundMesh m in added)
                        DrawMesh(layer, k, m, kept, inv);
                }
                else
                {
                    layer.ClearLevel(k);
                    foreach (GroundMesh m in meshes)
                        DrawMesh(layer, k, m, window, inv);
                }
                version[k] = v;
            }
            foreach (GroundMesh m in added)
                m.New = false;
            added.Clear();
            layer.End();
        }

        static void Fit(GroundMesh m, Clipmap clipmap)
        {
            if (m.Plane == clipmap.PlaneVersion)
                return;
            m.Plane = clipmap.PlaneVersion;
            m.Matrix = PlaneMatrix(m.Frame, clipmap.Plane);
            // The ribbons' widths are in the extent already; each level adds its own texels (Pad).
            Extent(m.Matrix, m.MinX, m.MinY, m.MaxX, m.MaxY, 0.0, out m.X0, out m.Y0, out m.X1, out m.Y1);
        }

        static void DrawMesh(GroundLayer layer, int k, GroundMesh m, Vector4 area, double inv)
        {
            if (m.Plane >= 0)
                layer.Draw(k, m.Mesh, m.Matrix, m.X0, m.Y0, m.X1, m.Y1, area, GroundLayer.RoadPass);
        }

        /// <summary>Where a flat frame lies on the clipmap's plane: an affine map (over a few
        /// kilometres both are flat to a fraction of a metre), from three points.</summary>
        public static Matrix4x4 PlaneMatrix(TileFrame frame, TangentPlane plane)
        {
            double lat = frame.CenterLat, lon = frame.CenterLon, d = 0.01;
            Map(frame, plane, lat, lon, out double lx0, out double ly0, out double px0, out double py0);
            Map(frame, plane, lat, lon + d, out double lx1, out double ly1, out double px1, out double py1);
            Map(frame, plane, lat + d, lon, out double lx2, out double ly2, out double px2, out double py2);
            // Solve P - P0 = A (L - L0) from the two differences.
            double ax = lx1 - lx0, ay = ly1 - ly0, bx = lx2 - lx0, by = ly2 - ly0;
            double det = ax * by - bx * ay;
            double m00 = ((px1 - px0) * by - (px2 - px0) * ay) / det, m01 = ((px2 - px0) * ax - (px1 - px0) * bx) / det;
            double m10 = ((py1 - py0) * by - (py2 - py0) * ay) / det, m11 = ((py2 - py0) * ax - (py1 - py0) * bx) / det;
            var m = Matrix4x4.identity;
            m.m00 = (float)m00;
            m.m01 = (float)m01;
            m.m10 = (float)m10;
            m.m11 = (float)m11;
            m.m03 = (float)(px0 - m00 * lx0 - m01 * ly0);
            m.m13 = (float)(py0 - m10 * lx0 - m11 * ly0);
            return m;
        }

        static void Map(TileFrame frame, TangentPlane plane, double lat, double lon, out double lx, out double ly, out double px, out double py)
        {
            RibbonMeshData.Point(frame, lat, lon, out lx, out ly);
            plane.Project(Geo.Direction(lat, lon), out px, out py);
        }

        /// <summary>A local extent mapped onto the plane, grown by margin.</summary>
        public static void Extent(Matrix4x4 m, double minX, double minY, double maxX, double maxY, double margin,
                                  out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
            for (int c = 0; c < 4; c++)
            {
                double lx = (c & 1) == 0 ? minX : maxX, ly = (c & 2) == 0 ? minY : maxY;
                double px = m.m00 * lx + m.m01 * ly + m.m03, py = m.m10 * lx + m.m11 * ly + m.m13;
                x0 = Math.Min(x0, px);
                y0 = Math.Min(y0, py);
                x1 = Math.Max(x1, px);
                y1 = Math.Max(y1, py);
            }
            x0 -= margin;
            y0 -= margin;
            x1 += margin;
            y1 += margin;
        }

        public static Mesh ToMesh(RibbonMeshData r, string name)
        {
            Mesh mesh = MeshUpload.Streams(name, r.Vertices, r.Colors, r.Ribbon, r.Dash, r.Triangles,
                                           new Bounds(Vector3.zero, Vector3.one * 1e7f)); // drawn by command buffers, never culled
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
