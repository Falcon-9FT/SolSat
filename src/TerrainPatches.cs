// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Terrain set by other mods: grids of target heights laid into Earth's terrain after the real
// relief. KerbalAvia grades its airports with them (it calls TerrainPatches by reflection);
// SolSatellite itself knows nothing of what a patch is for.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>A patch for the sampler: target heights and weights on a grid over a plane tangent
    /// to the planet. Plain data and pointers only: BurstPQS's jobs read it.</summary>
    public unsafe struct DemPatch
    {
        public double Ux, Uy, Uz, Ex, Ey, Ez, Nx, Ny, Nz; // the plane: up, east, north (PQS-local unit vectors)
        public double PlaneRadius;                        // its distance from the planet's centre, game m
        public double X0, Y0, InvStep;                    // grid origin and 1 / spacing on the plane, game m
        public double MinUp;                              // directions with less of Up than this miss the grid
        public int Cols, Rows;
        public float* Height;                             // game m above PlaneRadius, Cols x Rows, row by row
        public float* Weight;                             // 0: the terrain as it is .. 1: Height
    }

    /// <summary>
    /// What other mods call (by reflection; main thread only). A patch is a grid of target
    /// heights with a weight per node over a plane tangent to the planet; it is applied after the
    /// real relief and after the places kept as Sol has them (inside those it does nothing),
    /// so GetSurfaceHeight, KSP's quad builds, BurstPQS's jobs and the quads' colliders all
    /// follow it. Where patches overlap, their targets are averaged by weight. Needs the real
    /// terrain on (its PQS mod is what applies them).
    /// </summary>
    public static class TerrainPatches
    {
        public const int ApiVersion = 1;

        /// <summary>Patches take effect: the real terrain is attached to Earth's PQS.</summary>
        public static bool Available => DemTerrain.Instance != null && DemTerrain.Instance.PatchesWork;

        /// <summary>
        /// Adds or replaces the patch `key`. frame: 9 doubles, the PQS-local unit vectors up,
        /// east and north of the plane; planeRadius: the plane's distance from the planet's
        /// centre (game m). Node (i, j) is at x = x0 + i * step east and y = y0 + j * step north
        /// on the plane (game m): the direction (up * planeRadius + east * x + north * y),
        /// normalised. heights: nx * ny values, i fastest, game m above planeRadius; weights:
        /// 0..1, the same layout. The same data again changes nothing. False if it can't be
        /// used (bad sizes or numbers, no real terrain).
        /// </summary>
        public static bool Set(string key, double[] frame, double planeRadius, double x0, double y0, double step,
                               int nx, int ny, float[] heights, float[] weights) =>
            DemTerrain.Instance != null && DemTerrain.Instance.Patches.Set(key, frame, planeRadius, x0, y0, step, nx, ny, heights, weights);

        public static bool Remove(string key) => DemTerrain.Instance != null && DemTerrain.Instance.Patches.Remove(key);

        /// <summary>The terrain's radius (game m from the planet's centre) in a PQS-local
        /// direction as it is without any patch; NaN outside a scene with Earth's terrain.</summary>
        public static double NativeRadius(double x, double y, double z) =>
            DemTerrain.Instance != null ? DemTerrain.Instance.NativeRadius(new Vector3d(x, y, z)) : double.NaN;

        /// <summary>Quads near the camera under the patch that are still built from before it
        /// changed (they are built again over the next frames).</summary>
        public static int Pending(string key) => DemTerrain.Instance != null ? DemTerrain.Instance.PatchPending(key) : 0;

        public static int Count => DemTerrain.Instance != null ? DemTerrain.Instance.Patches.Count : 0;
    }

    /// <summary>
    /// The patches, kept in unmanaged memory: one block per patch (heights, then weights) and a
    /// global array of DemPatch for the managed path, rebuilt when they change. A quad build
    /// leases the patches it needs (DemTerrain.Lease); a patch replaced or removed while leased
    /// is freed only once its last lease is released and the global array no longer points at it.
    /// </summary>
    sealed unsafe class PatchStore : IDisposable
    {
        sealed class Record
        {
            public string Key;
            public DemPatch Patch;
            public DemCap Cap;
            public IntPtr Data;
            public int Leases;
            public int DeadSince = -1; // frame it was replaced or removed
            public double[] Frame;
            public double PlaneRadius, X0, Y0, Step;
            public float[] Heights, Weights;
        }

        static readonly int PatchSize = Marshal.SizeOf(typeof(DemPatch));

        readonly Dictionary<string, Record> live = new Dictionary<string, Record>();
        readonly List<Record> dead = new List<Record>();
        readonly Action<List<DemCap>, List<DemPatch>> changed; // where, and the patches before and after (valid during the call)
        IntPtr globalBlock;
        int globalCount;
        readonly List<KeyValuePair<IntPtr, int>> freeLater = new List<KeyValuePair<IntPtr, int>>();

        /// <summary>Set while NativeRadius asks the PQS for the terrain without patches.</summary>
        public bool Bypass;

        public PatchStore(Action<List<DemCap>, List<DemPatch>> changed)
        {
            this.changed = changed;
        }

        public int Count => live.Count;
        public int GlobalCount => Bypass ? 0 : globalCount;
        public DemPatch* Global => (DemPatch*)globalBlock;
        public static int Size => PatchSize;

        public bool Set(string key, double[] frame, double planeRadius, double x0, double y0, double step,
                        int nx, int ny, float[] heights, float[] weights)
        {
            if (string.IsNullOrEmpty(key) || frame == null || frame.Length != 9 || heights == null || weights == null)
                return false;
            if (nx < 2 || ny < 2 || nx > 4096 || ny > 4096 || heights.Length != nx * ny || weights.Length != nx * ny)
                return false;
            if (!Finite(planeRadius) || planeRadius <= 0.0 || !Finite(x0) || !Finite(y0) || !Finite(step) || step <= 0.0)
                return false;
            foreach (double v in frame)
                if (!Finite(v))
                    return false;
            for (int k = 0; k < heights.Length; k++)
                if (float.IsNaN(heights[k]) || float.IsInfinity(heights[k]) || float.IsNaN(weights[k]) || float.IsInfinity(weights[k]))
                    return false;
            if (live.TryGetValue(key, out Record had) && Same(had, frame, planeRadius, x0, y0, step, nx, ny, heights, weights))
                return true;

            var up = new Vector3d(frame[0], frame[1], frame[2]).normalized;
            var east = new Vector3d(frame[3], frame[4], frame[5]).normalized;
            var north = new Vector3d(frame[6], frame[7], frame[8]).normalized;
            int n = nx * ny;
            IntPtr data = Marshal.AllocHGlobal(n * 2 * sizeof(float));
            var h = (float*)data;
            var w = h + n;
            for (int k = 0; k < n; k++)
            {
                h[k] = heights[k];
                w[k] = Mathf.Clamp01(weights[k]);
            }
            // Its reach: the grid's corners.
            Vector3d centre = (up * planeRadius + east * (x0 + step * (nx - 1) * 0.5) + north * (y0 + step * (ny - 1) * 0.5)).normalized;
            double minUp = 1.0, angle = 0.0;
            foreach (double cx in new[] { x0, x0 + step * (nx - 1) })
                foreach (double cy in new[] { y0, y0 + step * (ny - 1) })
                {
                    Vector3d c = (up * planeRadius + east * cx + north * cy).normalized;
                    minUp = Math.Min(minUp, Vector3d.Dot(c, up));
                    angle = Math.Max(angle, Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(c, centre)))));
                }
            var r = new Record
            {
                Key = key, Data = data, Frame = (double[])frame.Clone(), PlaneRadius = planeRadius, X0 = x0, Y0 = y0, Step = step,
                Heights = (float[])heights.Clone(), Weights = (float[])weights.Clone(),
                Cap = new DemCap(centre, angle * 1.02 + 1e-7),
                Patch = new DemPatch
                {
                    Ux = up.x, Uy = up.y, Uz = up.z, Ex = east.x, Ey = east.y, Ez = east.z, Nx = north.x, Ny = north.y, Nz = north.z,
                    PlaneRadius = planeRadius, X0 = x0, Y0 = y0, InvStep = 1.0 / step,
                    MinUp = Math.Max(0.0, minUp - 1e-9), Cols = nx, Rows = ny, Height = h, Weight = w,
                },
            };
            var caps = new List<DemCap> { r.Cap };
            var patches = new List<DemPatch> { r.Patch };
            if (had != null)
            {
                caps.Add(had.Cap);
                patches.Add(had.Patch); // its data is freed a few frames later at the earliest
                Retire(had);
            }
            live[key] = r;
            RebuildGlobal();
            Debug.Log($"[SolSatellite] Terrain patch {key}: {nx}x{ny} every {step:0.#} m ({(had != null ? "replaced" : "new")}, {live.Count} in all)");
            changed(caps, patches);
            return true;
        }

        public bool Remove(string key)
        {
            if (key == null || !live.TryGetValue(key, out Record r))
                return false;
            live.Remove(key);
            Retire(r);
            RebuildGlobal();
            Debug.Log($"[SolSatellite] Terrain patch {key} removed ({live.Count} left)");
            changed(new List<DemCap> { r.Cap }, new List<DemPatch> { r.Patch });
            return true;
        }

        void Retire(Record r)
        {
            r.DeadSince = Time.frameCount;
            dead.Add(r);
        }

        static bool Same(Record r, double[] frame, double planeRadius, double x0, double y0, double step, int nx, int ny, float[] heights, float[] weights)
        {
            if (r.Patch.Cols != nx || r.Patch.Rows != ny || r.PlaneRadius != planeRadius || r.X0 != x0 || r.Y0 != y0 || r.Step != step)
                return false;
            for (int i = 0; i < 9; i++)
                if (r.Frame[i] != frame[i])
                    return false;
            for (int k = 0; k < heights.Length; k++)
                if (r.Heights[k] != heights[k] || r.Weights[k] != weights[k])
                    return false;
            return true;
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>The live patches in one array for the managed path (GetSurfaceHeight, KSP's
        /// own builds); the old array is freed a few frames later.</summary>
        void RebuildGlobal()
        {
            if (globalBlock != IntPtr.Zero)
                freeLater.Add(new KeyValuePair<IntPtr, int>(globalBlock, Time.frameCount + 3));
            globalBlock = Marshal.AllocHGlobal(Math.Max(1, live.Count) * PatchSize);
            var p = (DemPatch*)globalBlock;
            int k = 0;
            foreach (Record r in live.Values)
                p[k++] = r.Patch;
            globalCount = k;
        }

        public int CountOverlapping(DemCap cap)
        {
            int n = 0;
            foreach (Record r in live.Values)
                if (r.Cap.Overlaps(cap))
                    n++;
            return n;
        }

        /// <summary>Copies the patches over `cap` to `into` and leases them; the records go to
        /// `leased` for Release.</summary>
        public int Lease(DemCap cap, DemPatch* into, List<object> leased)
        {
            int n = 0;
            foreach (Record r in live.Values)
            {
                if (!r.Cap.Overlaps(cap))
                    continue;
                into[n++] = r.Patch;
                r.Leases++;
                leased.Add(r);
            }
            return n;
        }

        public static void Release(List<object> leased)
        {
            if (leased == null)
                return;
            foreach (object o in leased)
                ((Record)o).Leases--;
        }

        /// <summary>The cap of a live patch (to find the quads under it).</summary>
        public bool CapOf(string key, out DemCap cap)
        {
            cap = default;
            if (key == null || !live.TryGetValue(key, out Record r))
                return false;
            cap = r.Cap;
            return true;
        }

        /// <summary>Frees what nothing reads any more; once a frame.</summary>
        public void FreeUnused()
        {
            int frame = Time.frameCount;
            for (int i = freeLater.Count - 1; i >= 0; i--)
            {
                if (frame < freeLater[i].Value)
                    continue;
                Marshal.FreeHGlobal(freeLater[i].Key);
                freeLater.RemoveAt(i);
            }
            for (int i = dead.Count - 1; i >= 0; i--)
            {
                Record r = dead[i];
                if (r.Leases > 0 || frame < r.DeadSince + 3)
                    continue;
                Marshal.FreeHGlobal(r.Data);
                r.Data = IntPtr.Zero;
                dead.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            foreach (Record r in live.Values)
                Marshal.FreeHGlobal(r.Data);
            live.Clear();
            foreach (Record r in dead)
                if (r.Leases == 0 && r.Data != IntPtr.Zero)
                    Marshal.FreeHGlobal(r.Data); // leased ones are left to leak rather than freed under a job
            dead.Clear();
            foreach (var kv in freeLater)
                Marshal.FreeHGlobal(kv.Key);
            freeLater.Clear();
            if (globalBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(globalBlock);
            globalBlock = IntPtr.Zero;
            globalCount = 0;
        }
    }
}
