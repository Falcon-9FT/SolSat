// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// Real terrain for Earth (Dem*.cs): elevation tiles streamed around the camera and the vessel,
// laid into the PQS terrain by a PQSMod of our own (PQSMod_SolSatDem below; with BurstPQS, by
// its adapter in SolSatellite.BurstPQS.dll), with more subdivision levels near the camera
// where the data is good. Tiles come from Mapterhorn (Terrarium-encoded lossless WebP, 512 px):
// national lidar/DTMs where countries publish them (most of Europe, the USA, Japan, ...),
// Copernicus 30 m elsewhere. Heights are scaled to Sol's planet (Quarter: x0.25), KSC and the
// other launch sites keep Sol's own terrain, and so does the ground under landed vessels until
// they take off (the terrain mustn't move under them).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

[assembly: KSPAssembly("SolSatellite", 1, 0)]

namespace SolSatellite
{
    /// <summary>A part of the world with its own terrain quality. Boxes: south, west, north,
    /// east (degrees) in fours; none: everywhere.</summary>
    sealed class DemRegion
    {
        public readonly string Id, Name;
        readonly double[] boxes;
        public int Levels;   // PQS subdivision levels added near the camera
        public int Zoom;     // finest elevation tiles downloaded (where there are any)
        public float Detail; // how much farther out rough terrain gets its finer quads (1: as Sol)

        public DemRegion(string id, string name, int levels, int zoom, float detail, params double[] boxes)
        {
            Id = id;
            Name = name;
            Levels = levels;
            Zoom = zoom;
            Detail = detail;
            this.boxes = boxes;
        }

        public bool Contains(double lat, double lon)
        {
            if (boxes.Length == 0)
                return true;
            for (int i = 0; i + 3 < boxes.Length; i += 4)
                if (lat >= boxes[i] && lat <= boxes[i + 2] && lon >= boxes[i + 1] && lon <= boxes[i + 3])
                    return true;
            return false;
        }

        /// <summary>Mapterhorn's coverage: lidar or national DTMs (1-10 m) in these, Copernicus
        /// 30 m elsewhere. Where a zoom has no tiles the next coarser one is used. No extra PQS
        /// levels by default: Parallax places its grass and rocks per triangle and subdivides the
        /// last level's quads a fixed number of times, so each level more is 4x of both near
        /// the camera (+4 ate 28 GB on 2026-10-01).</summary>
        public static List<DemRegion> Defaults() => new List<DemRegion>
        {
            new DemRegion("usa", "США", 0, 14, 1.3f,
                24.4, -125.0, 49.5, -66.9,       // lower 48
                51.0, -170.0, 71.5, -129.9,      // Alaska
                18.8, -160.5, 22.4, -154.7),     // Hawaii
            new DemRegion("europe", "Европа", 0, 14, 1.3f,
                35.0, -11.0, 72.0, 32.0,
                63.0, -25.0, 67.0, -13.0),       // Iceland
            new DemRegion("japan", "Япония", 0, 14, 1.3f, 24.0, 122.0, 46.0, 146.0),
            new DemRegion("nz", "Новая Зеландия", 0, 14, 1.3f, -47.5, 166.0, -34.0, 179.0),
            new DemRegion("canada", "Канада", 0, 13, 1.3f, 41.6, -141.0, 84.0, -52.0),
            new DemRegion("australia", "Австралия", 0, 13, 1.3f, -44.0, 112.0, -10.0, 154.0),
            new DemRegion("world", "Остальной мир", 0, 12, 1.3f),
        };
    }

    /// <summary>A spherical cap: centre (PQS-local unit vector) and angular radius.</summary>
    struct DemCap
    {
        public double X, Y, Z, Angle;

        public DemCap(Vector3d dir, double angle)
        {
            X = dir.x;
            Y = dir.y;
            Z = dir.z;
            Angle = angle;
        }

        public bool Overlaps(DemCap o)
        {
            double sum = Angle + o.Angle;
            return sum >= Math.PI || X * o.X + Y * o.Y + Z * o.Z >= Math.Cos(sum);
        }
    }

    /// <summary>One elevation tile (512 px, Web Mercator).</summary>
    sealed class DemTile
    {
        public enum State { New, Disk, Net, Loading, Ready, Missing }

        public readonly int Z, X, Y;
        public readonly long Key;
        public readonly DemCap Cap;      // the tile itself
        public readonly DemCap Reach;    // ... and what its loading changes (blend into neighbours)
        public State Status;
        public IntPtr Data;              // 512 x 512 stored heights (DemSampler.Store), unmanaged
        public bool Active;              // part of the terrain
        public int Leases;               // quad builds using it right now
        public float WantedAt;
        public double Order;             // coarse first, then nearest
        public int Attempts;
        public float RetryAt;
        public UnityWebRequest Request;
        public volatile IntPtr Decoded;  // set by the worker
        public volatile string Error;
        public bool FromNetwork;

        public DemTile(int z, int x, int y, double blend)
        {
            Z = z;
            X = x;
            Y = y;
            Key = Geo.TileKey(z, x, y);
            double n = 1 << z;
            Vector3d c = Center(z, x, y);
            double half = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double lon = (x + (i & 1)) / n * 360.0 - 180.0, lat = Geo.LatFromMercatorY(y + (i >> 1), n);
                half = Math.Max(half, Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(c, Geo.Direction(lat, lon))))));
            }
            Cap = new DemCap(c, half);
            Reach = new DemCap(c, half * (1.0 + 2.0 * blend) + half * 0.01);
        }

        public static Vector3d Center(int z, int x, int y)
        {
            double n = 1 << z;
            return Geo.Direction(Geo.LatFromMercatorY(y + 0.5, n), (x + 0.5) / n * 360.0 - 180.0);
        }
    }

    public struct DemLease
    {
        public IntPtr Refs, Exclusions, Patches;
        public int Count, ExclusionCount, PatchCount, Handle;
        public DemParams Params;
    }

    /// <summary>What SolSatellite.BurstPQS.dll needs: the tiles for one quad's build job.</summary>
    public static class DemApi
    {
        static int errors;

        public static bool Lease(PQ quad, out DemLease lease)
        {
            DemTerrain t = DemTerrain.Instance;
            lease = default;
            try
            {
                return t != null && t.Lease(quad, out lease);
            }
            catch (Exception e)
            {
                if (errors++ < 10)
                    Debug.LogException(e);
                lease = default;
                return false; // Sol's own heights for this quad
            }
        }

        public static void Release(int handle) => DemTerrain.Instance?.Release(handle);
    }

    /// <summary>Earth's real-relief PQSMod: after Sol's own heights (and its coast smoothing),
    /// before the material mods. Without BurstPQS (or with it falling back) KSP calls
    /// OnVertexBuildHeight itself; GetSurfaceHeight always does.</summary>
    public class PQSMod_SolSatDem : PQSMod
    {
        static int errors;

        /// <summary>Nothing of ours may break the PQS: logged (a few times) and carried on.</summary>
        static void Guard(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                if (errors++ < 10)
                    Debug.LogException(e);
            }
        }

        public override void OnSetup() => Guard(() => DemTerrain.Instance?.OnSphereSetup(this));

        public override void OnPostSetup() => Guard(() => DemTerrain.Instance?.OnPostSetup(this));

        public override void OnVertexBuildHeight(PQS.VertexBuildData data)
        {
            DemTerrain t = DemTerrain.Instance;
            if (t != null)
                data.vertHeight = t.Height(sphere, data.vertHeight, data.directionFromCenter);
        }

        public override void OnQuadBuilt(PQ quad) => Guard(() => DemTerrain.Instance?.OnQuadBuilt(quad));

        public override void OnQuadDestroy(PQ quad) => Guard(() => DemTerrain.Instance?.OnQuadDestroy(quad));
    }

    /// <summary>
    /// Streams elevation tiles, keeps Earth's PQS mod fed and the terrain consistent: a tile
    /// that arrives (or goes) changes the height function there, so the quads built from the
    /// old one are built again - all of them, or neighbours would no longer meet.
    /// </summary>
    sealed unsafe class DemTerrain : IDisposable
    {
        public static DemTerrain Instance { get; private set; }

        const string BodyName = "Earth";
        const int ModOrder = 110;               // Sol: VertexHeightMapBicubic 10, launch site decals 15, coast smoothing 105
        const double RealRadius = 6371010.0;    // Sol's Earth at full scale
        const int MaxZoom = 17;
        const int MaxExtraLevels = 10;     // beyond this KSP's float quad positions get too coarse
        const int MaxDiskLoads = 6;
        const double Blend = 0.25;
        const double NoSubdivide = -1e30;       // a quad's subdivideThresholdFactor that keeps it from subdividing
        const float RebuildMs = 3f;             // quads rebuilt per frame: this long at most
        const int RebuildsPerFrame = 8;         // ... and this many (each one makes Parallax redo its scatters)
        const float RebuildCooldown = 2f;       // s: a quad built since then waits (changes come in waves)
        const float WaveWait = 1.5f;            // s: loaded tiles wait this long for the rest of their area
        const int WaveSize = 48;                // ... or until this many are ready
        const double ProtectRadius = 1500.0;    // m (game) around a landed vessel the terrain mustn't change
        const double HoldRadius = 60000.0;      // ... and with it waiting, nothing new around it either
        const double Deg = Math.PI / 180.0;

        static readonly int RefSize = Marshal.SizeOf(typeof(DemTileRef));
        static readonly int ExclusionSize = Marshal.SizeOf(typeof(DemExclusion));
        static readonly FieldInfo MaxLevel1 = typeof(PQS).GetField("maxLevel1", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo ModOnQuadDestroy = typeof(PQS).GetMethod("Mod_OnQuadDestroy", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo ColliderMinLevel = typeof(PQSMod_QuadMeshColliders).GetField("minLevel", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo ParallaxFakeQuad = typeof(Parallax.TerrainShaderQuadData).GetField("newQuad", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo LocalSpaceList = typeof(PQS).GetField("LocalSpacePQList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo PreciseUpdatePosition = typeof(PQ).GetMethod("PreciseUpdateSubQuadsPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        sealed class QuadInfo
        {
            public DemCap Cap;
            public int Version;
            public float BuiltAt;
        }

        sealed class LeaseRecord
        {
            public IntPtr Block;
            public List<DemTile> Tiles;
            public List<object> Patches; // PatchStore's records
        }

        readonly Settings settings;
        readonly string cacheRoot;
        CelestialBody body;
        PQS pqs;
        PQSMod_SolSatDem mod;
        int baseLevel = -1;                     // Earth's own maxLevel (Sol's)
        bool injected;

        readonly Dictionary<long, DemTile> tiles = new Dictionary<long, DemTile>();
        readonly HashSet<long> missing = new HashSet<long>();
        readonly List<DemTile> wanted = new List<DemTile>();
        readonly List<DemTile> finished = new List<DemTile>();   // decoded by workers; locked
        readonly List<DemTile> changed = new List<DemTile>();
        readonly List<DemTile> ready = new List<DemTile>();
        int diskLoads, netLoads, strikes, wantFrame;
        float netTokens, pausedUntil, firstReadyAt = -1f;
        bool networkDisabled, disposed;

        readonly List<DemTile> active = new List<DemTile>();     // sorted by zoom, y, x
        int version;
        IntPtr globalBlock;
        int globalCount, globalExclusions;
        readonly List<KeyValuePair<IntPtr, int>> freeLater = new List<KeyValuePair<IntPtr, int>>();

        List<DemExclusion> exclusions = new List<DemExclusion>();
        readonly List<DemCap> exclusionCaps = new List<DemCap>();
        float nextExclusions;
        readonly List<DemCap> protectedCaps = new List<DemCap>(), holdCaps = new List<DemCap>();

        readonly Dictionary<PQ, QuadInfo> quads = new Dictionary<PQ, QuadInfo>();
        readonly HashSet<PQ> stale = new HashSet<PQ>();
        readonly HashSet<PQ> inside = new HashSet<PQ>();  // stale ones changed away from their edges only (patches)
        readonly List<PQ> candidates = new List<PQ>(), gone = new List<PQ>();
        readonly Stopwatch clock = new Stopwatch();

        readonly Dictionary<int, LeaseRecord> leases = new Dictionary<int, LeaseRecord>();
        int nextLease = 1;
        readonly Dictionary<PQSMod_QuadMeshColliders, int> colliderOffsets = new Dictionary<PQSMod_QuadMeshColliders, int>();
        bool? adapterLoaded;
        DemParams parameters;
        float nextLog, nextMemoryCheck;
        bool memoryShort;
        long processBytes;

        /// <summary>The terrain changed in these places (tiles came or went, exclusions moved,
        /// terrain patches were set or removed).</summary>
        public event Action<List<DemCap>> Changed;

        /// <summary>Terrain set by other mods (TerrainPatches).</summary>
        public readonly PatchStore Patches;

        /// <summary>Patches take effect: our PQS mod is on Earth.</summary>
        public bool PatchesWork => injected && Enabled && pqs != null;

        // Status for the window.
        public int Downloaded { get; private set; }
        public long DownloadedBytes { get; private set; }
        public int Rebuilt { get; private set; }
        public int ActiveCount => active.Count;
        public int StaleCount => stale.Count;
        public int QueuedCount { get; private set; }
        public int LoadingCount => diskLoads + netLoads;
        public int MissingCount => missing.Count;
        public int ResidentCount { get; private set; }
        public bool Holding => holdCaps.Count > 0;
        public bool MemoryShort => memoryShort;
        public long ProcessBytes => processBytes;
        public bool Paused;
        public string RegionName { get; private set; } = "";
        public int LevelNow => pqs != null ? pqs.maxLevel : 0;
        public int BaseLevel => baseLevel;
        public bool Attached => injected;

        /// <summary>SolSatellite.BurstPQS.dll is in (BurstPQS installed): quads are built in its
        /// Burst jobs. Without it BurstPQS builds Earth KSP's own way (slower).</summary>
        public bool AdapterLoaded
        {
            get
            {
                if (adapterLoaded == null)
                {
                    adapterLoaded = false;
                    foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                        if (a.name == "SolSatellite.BurstPQS")
                            adapterLoaded = true;
                }
                return adapterLoaded.Value;
            }
        }

        public string NetworkStatus
        {
            get
            {
                if (networkDisabled)
                    return "отключена: сервер отказывает (до перезапуска игры)";
                float wait = pausedUntil - Time.realtimeSinceStartup;
                if (wait > 0f)
                    return $"сервер отказал, повтор через {wait:F0} с";
                return Paused ? "пауза" : "в норме";
            }
        }

        public DemTerrain(Settings settings, string cacheDir)
        {
            this.settings = settings;
            cacheRoot = Path.Combine(cacheDir, settings.TerrainSource);
            Patches = new PatchStore(PatchesChanged);
            Instance = this;
        }

        /// <summary>A patch came, changed or went: the quads it reaches are built again (and what
        /// stands on the ground follows), whether or not any real relief is loaded there.</summary>
        void PatchesChanged(List<DemCap> caps, List<DemPatch> patches)
        {
            version++;
            MarkTouched(caps, patches);
            Changed?.Invoke(caps);
        }

        /// <summary>The terrain's radius in a direction without the patches (TerrainPatches.NativeRadius).</summary>
        public double NativeRadius(Vector3d dir)
        {
            if (pqs == null || !pqs.isActive || disposed)
                return double.NaN;
            Patches.Bypass = true;
            try
            {
                return pqs.GetSurfaceHeight(dir.normalized);
            }
            catch (Exception)
            {
                return double.NaN;
            }
            finally
            {
                Patches.Bypass = false;
            }
        }

        /// <summary>Visible quads under a patch still waiting to be built again (TerrainPatches.Pending).</summary>
        public int PatchPending(string key)
        {
            if (!Patches.CapOf(key, out DemCap cap))
                return 0;
            int n = 0;
            foreach (PQ q in stale)
                if (q != null && q.isBuilt && q.isVisible && !q.isSubdivided && quads.TryGetValue(q, out QuadInfo info) && info.Cap.Overlaps(cap))
                    n++;
            return n;
        }

        bool Enabled => settings.TerrainEnabled && !disposed;

        int ExtraLevels(DemRegion r) => Math.Max(0, Math.Min(MaxExtraLevels, r.Levels));

        int MostExtraLevels()
        {
            int most = 0;
            foreach (DemRegion r in settings.TerrainRegions)
                most = Math.Max(most, ExtraLevels(r));
            return most;
        }

        DemRegion RegionAt(double lat, double lon)
        {
            foreach (DemRegion r in settings.TerrainRegions)
                if (r.Contains(lat, lon))
                    return r;
            return settings.TerrainRegions[settings.TerrainRegions.Count - 1];
        }

        // ------------------------------------------------------------------ the PQS

        /// <summary>Puts the mod under Earth's PQS (picked up when the sphere next starts: on
        /// entering the space centre or a flight) and gives it room for the extra levels.</summary>
        public void Attach()
        {
            if (body == null)
                body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null || body.pqsController == null)
                return;
            pqs = body.pqsController;
            if (baseLevel < 0)
                baseLevel = pqs.maxLevel;
            if (!injected && Enabled)
            {
                var go = new GameObject("SolSatellite real terrain");
                go.transform.SetParent(pqs.transform, false);
                go.layer = pqs.gameObject.layer;
                mod = go.AddComponent<PQSMod_SolSatDem>();
                mod.modEnabled = true;
                mod.order = ModOrder;
                mod.sphere = pqs;
                injected = true;
                Debug.Log($"[SolSatellite] Real terrain mod added to {BodyName} (order {ModOrder}); Sol's maxLevel {baseLevel}");
            }
            SetLevelsForStart();
        }

        /// <summary>The PQS sizes its level tables when it starts: room for the most levels any
        /// region asks for (the camera's region lowers it again while flying). KSP's terrain
        /// presets (saved in settings.cfg) would override it; they only list stock planets.</summary>
        void SetLevelsForStart()
        {
            if (pqs == null || baseLevel < 0)
                return;
            pqs.maxLevel = baseLevel + (Enabled && injected ? MostExtraLevels() : 0);
            if (PQSCache.PresetList?.GetPreset(pqs.gameObject.name) != null)
                Debug.LogWarning($"[SolSatellite] KSP's terrain preset for {pqs.gameObject.name} sets its levels; real terrain gets no extra levels there");
        }

        public void OnSceneSwitch()
        {
            Attach();
            quads.Clear();
            stale.Clear();
            inside.Clear();
            placesFound = false;
        }

        /// <summary>From the mod's OnSetup, when the sphere starts (before any quad is built):
        /// the tiles already on disk around where the game starts go in now, so the terrain is
        /// built right the first time and vessels are put down on it.</summary>
        public void OnSphereSetup(PQSMod_SolSatDem m)
        {
            mod = m;
            pqs = m.sphere;
            body = FlightGlobals.GetBodyByName(BodyName);
            FixColliderLevels(); // before UpdateQuadsInit builds the first quads
            parameters = MakeParams();
            FindPlaces();
            FitParallax();
            exclusions = ComputeExclusions();
            UpdateExclusionCaps();
            RebuildGlobal();
            if (Enabled)
            {
                try
                {
                    Preload();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>Quads get colliders from Sol's own last level down, not only at ours: the PQS
        /// goes deeper only near the camera and where its region asks for it. Whichever of the
        /// collider mod's OnSetup and ours runs first, it ends up there. UpdateColliders keeps
        /// them off on quads that have subdivided (their children have their own).</summary>
        void FixColliderLevels()
        {
            if (pqs == null || baseLevel <= 0)
                return;
            foreach (PQSMod_QuadMeshColliders c in pqs.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true))
            {
                if (!colliderOffsets.TryGetValue(c, out int own))
                    colliderOffsets[c] = own = Math.Abs(c.maxLevelOffset);
                int min = Math.Max(1, baseLevel - own);
                c.maxLevelOffset = Math.Max(0, pqs.maxLevel - min);
                ColliderMinLevel?.SetValue(c, min);
            }
        }

        void UpdateColliders()
        {
            foreach (PQ q in quads.Keys)
            {
                MeshCollider c = q != null ? q.meshCollider : null;
                if (c == null)
                    continue;
                bool on = q.isBuilt && q.isActive && !q.isSubdivided && c.sharedMesh != null;
                if (c.enabled != on)
                    c.enabled = on;
            }
        }

        public void OnPostSetup(PQSMod_SolSatDem m)
        {
            FixColliderLevels();
            var colliders = new List<string>();
            foreach (PQSMod_QuadMeshColliders c in m.sphere.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true))
                colliders.Add($"{c.name} from level {(ColliderMinLevel != null ? ColliderMinLevel.GetValue(c) : "?")}");
            var names = new List<string>();
            foreach (PQSMod pm in m.sphere.GetComponentsInChildren<PQSMod>(true))
                if (pm.modEnabled)
                    names.Add($"{pm.GetType().Name}({pm.order})");
            Debug.Log($"[SolSatellite] Real terrain: {m.sphere.name} maxLevel {m.sphere.maxLevel} (Sol's {baseLevel}), " +
                      $"{active.Count} tiles in, {exclusions.Count} places kept as Sol has them; colliders: {string.Join(", ", colliders.ToArray())}; " +
                      $"BurstPQS adapter {(AdapterLoaded ? "loaded" : "not loaded")}; mods: {string.Join(", ", names.ToArray())}");
        }

        DemParams MakeParams() => new DemParams
        {
            Scale = (pqs != null ? pqs.radius : body != null ? body.Radius : RealRadius / 4.0) / RealRadius,
            LandMin = settings.TerrainLandLift,
            WaterMax = -settings.TerrainShoreDepth,
            Blend = Blend,
        };

        // ------------------------------------------------------------------ heights

        /// <summary>The managed path: KSP's own quad builds (no BurstPQS) and GetSurfaceHeight.</summary>
        public double Height(PQS sphere, double vertHeight, Vector3d dir)
        {
            int patches = Patches.GlobalCount;
            if (!Enabled || globalCount == 0 && patches == 0 || sphere.isBuildingMaps)
                return vertHeight;
            double r = sphere.radius;
            var refs = (DemTileRef*)globalBlock;
            var ex = (DemExclusion*)((byte*)globalBlock + globalCount * RefSize);
            double h = vertHeight - r;
            if (globalCount > 0)
                h = DemSampler.Height(h, dir.x, dir.y, dir.z, refs, globalCount, ex, globalExclusions, ref parameters);
            if (patches > 0)
                h = DemSampler.Patch(h, dir.x, dir.y, dir.z, Patches.Global, patches, ex, globalExclusions, r);
            return r + h;
        }

        /// <summary>The tiles a quad's build job needs, kept in memory until Release.</summary>
        public bool Lease(PQ quad, out DemLease lease)
        {
            lease = default;
            if (!Enabled || active.Count == 0 && Patches.Count == 0 || quad.sphereRoot == null || quad.sphereRoot.isBuildingMaps)
                return false;
            DemCap cap = QuadCap(quad);
            var used = new List<DemTile>();
            foreach (DemTile t in active)
                if (t.Cap.Overlaps(cap))
                    used.Add(t);
            int patchCount = Patches.CountOverlapping(cap);
            if (used.Count == 0 && patchCount == 0)
                return false;
            int exCount = 0;
            for (int i = 0; i < exclusions.Count; i++)
                if (exclusionCaps[i].Overlaps(cap))
                    exCount++;
            IntPtr block = Marshal.AllocHGlobal(used.Count * RefSize + Math.Max(1, exCount) * ExclusionSize + Math.Max(1, patchCount) * PatchStore.Size);
            var refs = (DemTileRef*)block;
            var leased = new List<DemTile>(used.Count * 3);
            for (int i = 0; i < used.Count; i++)
                FillRef(refs + i, used[i], leased);
            var ex = (DemExclusion*)((byte*)block + used.Count * RefSize);
            int k = 0;
            for (int i = 0; i < exclusions.Count; i++)
                if (exclusionCaps[i].Overlaps(cap))
                    ex[k++] = exclusions[i];
            foreach (DemTile t in leased)
                t.Leases++;
            var patches = (DemPatch*)((byte*)ex + Math.Max(1, exCount) * ExclusionSize);
            List<object> leasedPatches = patchCount > 0 ? new List<object>(patchCount) : null;
            patchCount = patchCount > 0 ? Patches.Lease(cap, patches, leasedPatches) : 0;
            int handle = nextLease++;
            leases[handle] = new LeaseRecord { Block = block, Tiles = leased, Patches = leasedPatches };
            lease = new DemLease
            {
                Refs = block,
                Count = used.Count,
                Exclusions = (IntPtr)ex,
                ExclusionCount = exCount,
                Patches = (IntPtr)patches,
                PatchCount = patchCount,
                Handle = handle,
                Params = parameters,
            };
            return true;
        }

        public void Release(int handle)
        {
            if (!leases.TryGetValue(handle, out LeaseRecord l))
                return;
            leases.Remove(handle);
            foreach (DemTile t in l.Tiles)
                t.Leases--;
            PatchStore.Release(l.Patches);
            Marshal.FreeHGlobal(l.Block);
        }

        /// <summary>A tile and its neighbours for the sampler; adds the tiles to the list (to lease).</summary>
        void FillRef(DemTileRef* r, DemTile t, List<DemTile> list)
        {
            r->Z = t.Z;
            r->X = t.X;
            r->Y = t.Y;
            r->Pad = 0;
            r->Data = (ushort*)t.Data;
            list?.Add(t);
            int n = 1 << t.Z, k = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    long ptr = 0;
                    int y = t.Y + dy;
                    if (y >= 0 && y < n && tiles.TryGetValue(Geo.TileKey(t.Z, ((t.X + dx) % n + n) % n, y), out DemTile o) && o.Active)
                    {
                        ptr = (long)o.Data;
                        list?.Add(o);
                    }
                    r->N[k++] = ptr;
                }
            }
        }

        /// <summary>The active tiles, sorted, with exclusions after them: for the managed path.</summary>
        void RebuildGlobal()
        {
            active.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            if (globalBlock != IntPtr.Zero)
                freeLater.Add(new KeyValuePair<IntPtr, int>(globalBlock, Time.frameCount + 3)); // someone may still read it this frame
            int n = active.Count, e = exclusions.Count;
            globalBlock = Marshal.AllocHGlobal(Math.Max(1, n) * RefSize + Math.Max(1, e) * ExclusionSize);
            var refs = (DemTileRef*)globalBlock;
            for (int i = 0; i < n; i++)
                FillRef(refs + i, active[i], null);
            var ex = (DemExclusion*)((byte*)globalBlock + n * RefSize);
            for (int i = 0; i < e; i++)
                ex[i] = exclusions[i];
            globalCount = n;
            globalExclusions = e;
        }

        static DemCap QuadCap(PQ quad)
        {
            float half = PQS.cacheMeshSize * 0.5f;
            Matrix4x4 m = quad.quadMatrix;
            var corners = new Vector3d[4];
            Vector3d sum = Vector3d.zero;
            for (int i = 0; i < 4; i++)
            {
                var v = new Vector3((i & 1) == 0 ? -half : half, 0f, (i & 2) == 0 ? -half : half);
                corners[i] = ((Vector3d)m.MultiplyPoint3x4(v)).normalized;
                sum += corners[i];
            }
            Vector3d c = sum.normalized;
            double angle = 0.0;
            foreach (Vector3d k in corners)
                angle = Math.Max(angle, Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(c, k)))));
            return new DemCap(c, angle * 1.05 + 1e-7);
        }

        // ------------------------------------------------------------------ quads

        public void OnQuadBuilt(PQ quad)
        {
            if (quad.sphereRoot == null || quad.sphereRoot.isBuildingMaps)
                return;
            quads[quad] = new QuadInfo { Cap = QuadCap(quad), Version = version, BuiltAt = Time.realtimeSinceStartup };
            stale.Remove(quad);
            inside.Remove(quad);
            if (Enabled && baseLevel > 0)
                SetSubdivision(quad);
        }

        public void OnQuadDestroy(PQ quad)
        {
            quads.Remove(quad);
            stale.Remove(quad);
            inside.Remove(quad);
        }

        /// <summary>Rough ground gets its finer quads farther out (the region's Detail); the
        /// sea floor, never seen, no deeper than Sol's own levels.</summary>
        void SetSubdivision(PQ quad)
        {
            if (quad.subdivision < 1)
                return;
            double top = quad.meshVertMax, bottom = quad.meshVertMin;
            if (top < parameters.WaterMax - 1.0)
            {
                quad.subdivideThresholdFactor = quad.subdivision >= baseLevel ? NoSubdivide : 1.0;
                return;
            }
            Vector3d c = quad.positionPlanetRelative;
            Geo.LatLon(c.normalized, out double lat, out double lon);
            DemRegion r = RegionAt(lat, lon);
            double size = Math.PI * 0.5 * quad.sphereRoot.radius / (1 << quad.subdivision);
            double rough = Math.Max(0.0, Math.Min(1.0, (top - bottom) / (size * 0.05)));
            quad.subdivideThresholdFactor = 1.0 + (Math.Max(1.0, r.Detail) - 1.0) * rough;
        }

        void MarkStale(List<DemCap> caps)
        {
            foreach (KeyValuePair<PQ, QuadInfo> kv in quads)
            {
                foreach (DemCap c in caps)
                {
                    if (kv.Value.Cap.Overlaps(c))
                    {
                        stale.Add(kv.Key);
                        inside.Remove(kv.Key);
                        break;
                    }
                }
            }
        }

        /// <summary>Patches changed (before and after, cap by cap): only quads with a vertex the
        /// change reaches are built again. A patch is small; most quads under its cap - all but
        /// the finest near it - have no vertex where it has weight and would look the same, and
        /// every rebuild costs Parallax's detail and scatters there a moment (2026-10-03: 69
        /// airport gradings near KSC rebuilt 330 quads, the ground flickered while they came).</summary>
        void MarkTouched(List<DemCap> caps, List<DemPatch> patches)
        {
            Vector3[] cache = PQS.cacheVerts;
            int side = PQS.cacheSideVertCount;
            if (cache == null || side < 4 || cache.Length < side * side || patches == null || patches.Count != caps.Count)
            {
                MarkStale(caps);
                return;
            }
            foreach (KeyValuePair<PQ, QuadInfo> kv in quads)
            {
                PQ q = kv.Key;
                if (q == null)
                    continue;
                int touch = 0;
                for (int i = 0; i < caps.Count && touch < 2; i++)
                    if (kv.Value.Cap.Overlaps(caps[i]))
                        touch = Math.Max(touch, Touches(q, patches[i], cache, side));
                if (touch == 0)
                    continue;
                if (touch == 1 && (!stale.Contains(q) || inside.Contains(q)))
                    inside.Add(q);
                else
                    inside.Remove(q);
                stale.Add(q);
            }
        }

        /// <summary>0: no vertex of the quad has weight from the patch; 1: some have; 2: some
        /// within a row of an edge (the neighbours' normals along it change too). Vertex
        /// directions as KSP and BurstPQS build them (quadMatrix x PQS.cacheVerts).</summary>
        static int Touches(PQ q, DemPatch patch, Vector3[] cache, int side)
        {
            Matrix4x4 m = q.quadMatrix;
            DemPatch* p = &patch;
            int touch = 0;
            for (int z = 0; z < side; z++)
            {
                bool edgeRow = z <= 1 || z >= side - 2;
                for (int x = 0; x < side; x++)
                {
                    Vector3d d = ((Vector3d)m.MultiplyPoint3x4(cache[z * side + x])).normalized;
                    if (DemSampler.PatchWeight(d.x, d.y, d.z, p) <= 0.0)
                        continue;
                    if (edgeRow || x <= 1 || x >= side - 2)
                        return 2;
                    touch = 1;
                }
            }
            return touch;
        }

        /// <summary>Builds again quads built from older tiles: the nearest visible ones first,
        /// a few milliseconds a frame. Subdivided or hidden ones wait until they show.</summary>
        void RebuildStale()
        {
            if (stale.Count == 0 || pqs == null || !pqs.quadAllowBuild)
                return;
            candidates.Clear();
            gone.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (PQ q in stale)
            {
                if (q == null || !q.isBuilt || q.isCached || !q.isActive || !quads.TryGetValue(q, out QuadInfo info))
                    gone.Add(q);
                else if (!q.isSubdivided && q.isVisible && now - info.BuiltAt >= RebuildCooldown)
                    candidates.Add(q);
            }
            foreach (PQ q in gone)
            {
                stale.Remove(q);
                inside.Remove(q);
            }
            if (candidates.Count == 0)
                return;
            candidates.Sort((a, b) => a.gcd1.CompareTo(b.gcd1));
            clock.Reset();
            clock.Start();
            int done = 0;
            foreach (PQ q in candidates)
            {
                if (clock.Elapsed.TotalMilliseconds > RebuildMs || done++ >= RebuildsPerFrame)
                    break;
                stale.Remove(q);
                Rebuild(q, !inside.Remove(q));
            }
            clock.Stop();
        }

        /// <summary>A quad built again in place, the way KSP builds a new one: hidden (Parallax
        /// takes its scatters away), destroyed for the mods (Parallax's terrain data, colliders),
        /// built and shown again, edges stitched to its neighbours; `edges`: the change reached
        /// its edges, so the neighbours' normals along them are worked out again too.
        /// Built before it is shown, within the frame: SetVisible on an unbuilt quad has BurstPQS
        /// build it in its next update, and for that frame the quad showed its old mesh without
        /// Parallax's terrain quad (near the camera Parallax's material on it draws nothing: a
        /// hole in the ground) and without a collider; scatters stay drawn as they were until
        /// Parallax has placed them anew (ScatterGhosts).</summary>
        void Rebuild(PQ q, bool edges)
        {
            Parallax.ScatterSystemQuadData placed = null;
            try
            {
                bool visible = q.isVisible;
                if (visible)
                {
                    placed = ScatterGhosts.Detach(q);
                    q.SetInvisible();
                }
                DropParallaxFakeQuad(q);
                ModOnQuadDestroy?.Invoke(pqs, new object[] { q });
                ParallaxDecalFix.Forget(q); // built again below: back under the decal that reaches it then
                // A last-level quad sits at the height its centre had when KSP placed it, and its
                // mesh is built relative to where it sits; whenever the floating origin shifts KSP
                // moves it to the height its centre has now (PQ.PreciseUpdateSubQuadsPosition).
                // Built again from new terrain but left where it was, it would then jump by the
                // difference: whole chunks up or down (2026-10-02, Tibet). Moved first.
                if (InLocalSpace(q))
                    PreciseUpdatePosition?.Invoke(q, null);
                q.isBuilt = false;
                q.Build(); // at once (BurstPQS: its job, waited for); hidden, so no scatters yet
                if (visible)
                    q.SetVisible(); // built: Parallax places its scatters (BurstPQS's OnQuadVisible)
                if (q.isBuilt && q.mesh != null)
                {
                    PQS.EdgeState state = q.GetEdgeState();
                    if (state != q.edgeState)
                    {
                        q.mesh.triangles = PQS.cacheIndices[(int)state];
                        q.edgeState = state;
                    }
                    q.QueueForNormalUpdate();
                    if (edges)
                    {
                        NormalsAgain(q.north);
                        NormalsAgain(q.south);
                        NormalsAgain(q.east);
                        NormalsAgain(q.west);
                    }
                    ShowParallaxQuad(q);
                }
                Rebuilt++;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                ScatterGhosts.Keep(q, placed); // let go at once if Parallax placed nothing anew
            }
        }

        /// <summary>Parallax hides a last-level quad near the camera under its own material
        /// (Custom/DiscardAll) while a finer copy of it draws. Built again, the quad gets a new
        /// copy - unless it was queued for normals already (a neighbour rebuilt this frame), and
        /// then nothing drew there until the next range check after its normals: the quad shows
        /// with Parallax's material meanwhile, as Parallax does itself out of range.</summary>
        static void ShowParallaxQuad(PQ q)
        {
            try
            {
                if (ParallaxFakeQuad == null || q.meshRenderer == null ||
                    !Parallax.PQSMod_Parallax.terrainQuadData.TryGetValue(q, out Parallax.TerrainShaderQuadData data) || data?.quadMaterial == null)
                    return;
                if (ParallaxFakeQuad.GetValue(data) is GameObject fake && fake != null)
                    return; // the copy draws it
                Material hide = Parallax.ConfigLoader.transparentMaterial;
                if (hide != null && q.meshRenderer.sharedMaterial == hide)
                    q.meshRenderer.sharedMaterial = data.quadMaterial;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static void NormalsAgain(PQ n)
        {
            if (n != null && n.isBuilt)
                n.QueueForNormalUpdate();
        }

        /// <summary>Whether KSP keeps the quad in double-precision local space (the last level),
        /// where it repositions it by the terrain height on floating origin shifts.</summary>
        bool InLocalSpace(PQ q) =>
            q.CreateParent != null && LocalSpaceList?.GetValue(pqs) is List<PQ> list && list.Contains(q);

        /// <summary>Near the camera Parallax draws a last-level quad through a subdivided copy.
        /// Its Cleanup (from OnQuadDestroy) disposes that copy's arrays and then has Unity destroy
        /// it at the end of the frame, whose OnDisable cleans up a second time and sets the mesh
        /// to zero vertices from the disposed arrays. KSP hardly ever destroys such a quad; a
        /// rebuild does it every time: the copy goes first, at once, so it is cleaned up once.</summary>
        static void DropParallaxFakeQuad(PQ q)
        {
            try
            {
                if (ParallaxFakeQuad == null || !Parallax.PQSMod_Parallax.terrainQuadData.TryGetValue(q, out Parallax.TerrainShaderQuadData data) || data == null)
                    return;
                if (ParallaxFakeQuad.GetValue(data) is GameObject fake && fake != null)
                    UnityEngine.Object.DestroyImmediate(fake);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Everything near the camera again (settings changed).</summary>
        public void RebuildAll()
        {
            foreach (PQ q in quads.Keys)
                stale.Add(q);
            inside.Clear();
        }

        // ------------------------------------------------------------------ exclusions

        Parallax.PQSMod_Parallax[] parallaxMods = new Parallax.PQSMod_Parallax[0];
        readonly Dictionary<Parallax.PQSMod_Parallax, int> parallaxLevels = new Dictionary<Parallax.PQSMod_Parallax, int>();
        bool placesFound;

        /// <summary>Parallax tessellates the quads of the PQS's last level, each a fixed number of
        /// times: with our levels added those quads are 2^n times smaller, so it gets n times
        /// fewer (the same triangle size as with Sol's levels).</summary>
        void FitParallax()
        {
            if (pqs == null || baseLevel <= 0)
                return;
            int extra = Math.Max(0, pqs.maxLevel - baseLevel);
            foreach (Parallax.PQSMod_Parallax m in parallaxMods)
            {
                if (m == null)
                    continue;
                if (!parallaxLevels.TryGetValue(m, out int own))
                    parallaxLevels[m] = own = m.subdivisionLevel;
                int level = Math.Max(1, own - extra);
                if (m.subdivisionLevel != level)
                    m.subdivisionLevel = level;
            }
        }

        PQSCity[] cities = new PQSCity[0];
        PQSCity2[] cities2 = new PQSCity2[0];
        PQSMod[] flatteners = new PQSMod[0];

        /// <summary>The places that keep Sol's terrain, looked up once per sphere start (walking
        /// the PQS's children - thousands of quads - every few seconds would stutter).</summary>
        void FindPlaces()
        {
            if (pqs == null || placesFound)
                return;
            placesFound = true;
            parallaxMods = pqs.GetComponentsInChildren<Parallax.PQSMod_Parallax>(true);
            cities = pqs.GetComponentsInChildren<PQSCity>(true);
            cities2 = pqs.GetComponentsInChildren<PQSCity2>(true);
            var mods = new List<PQSMod>();
            mods.AddRange(pqs.GetComponentsInChildren<PQSMod_MapDecalTangent>(true));
            mods.AddRange(pqs.GetComponentsInChildren<PQSMod_MapDecal>(true));
            mods.AddRange(pqs.GetComponentsInChildren<PQSMod_FlattenArea>(true));
            mods.AddRange(pqs.GetComponentsInChildren<PQSMod_FlattenAreaTangential>(true));
            flatteners = mods.ToArray();
        }

        List<DemExclusion> ComputeExclusions()
        {
            var list = new List<DemExclusion>();
            if (pqs == null)
                return list;
            double radius = pqs.radius;
            void Add(Vector3d dir, double inner, double outer)
            {
                if (dir.sqrMagnitude < 1e-12)
                    return;
                dir = dir.normalized;
                list.Add(new DemExclusion
                {
                    X = dir.x, Y = dir.y, Z = dir.z,
                    CosInner = Math.Cos(inner / radius), CosOuter = Math.Cos(outer / radius),
                });
            }
            Vector3d Local(Vector3 world) => pqs.transform.InverseTransformPoint(world);
            // Every launch site KSCSwitcher can put KSC at: known from the start, wherever KSC is now.
            // Places SiteGrounds levels (KSC, the pads SolSatellite places) get the real relief
            // around them and its patch under them instead.
            foreach (Vector3d site in LaunchSites())
                if (!SiteGrounds.NearLevelled(site.normalized, radius))
                    Add(site, 2500.0, 4000.0);
            foreach (PQSCity c in cities)
            {
                if (c == null || SiteGrounds.Levelled(c))
                    continue;
                bool ksc = c.name == "KSC";
                Vector3d d = c.transform.parent != null ? Local(c.transform.position) : (Vector3d)c.repositionRadial;
                if (d.sqrMagnitude < 1.0)
                    d = c.repositionRadial;
                Add(d, ksc ? 2500.0 : 500.0, ksc ? 4000.0 : 900.0);
            }
            foreach (PQSCity2 c in cities2)
                if (c != null)
                    Add(Geo.Direction(c.lat, c.lon), 400.0, 700.0);
            foreach (PQSMod m in flatteners)
            {
                if (m == null || !m.modEnabled)
                    continue;
                switch (m)
                {
                    case PQSMod_MapDecalTangent d when d.radius > 0.0:
                        Add(d.position, d.radius, d.radius * 1.5 + 200.0);
                        break;
                    case PQSMod_MapDecal d when d.radius > 0.0:
                        Add(d.position, d.radius, d.radius * 1.5 + 200.0);
                        break;
                    case PQSMod_FlattenArea f when f.outerRadius > 0.0:
                        Add(f.position, f.outerRadius, f.outerRadius * 1.5 + 200.0);
                        break;
                    case PQSMod_FlattenAreaTangential f when f.outerRadius > 0.0:
                        Add(f.position, f.outerRadius, f.outerRadius * 1.5 + 200.0);
                        break;
                }
            }
            return list;
        }

        List<Vector3d> launchSites;

        /// <summary>KSCSwitcher's launch sites (Sol's Launchsites configs), as PQS-local directions.</summary>
        List<Vector3d> LaunchSites()
        {
            if (launchSites != null)
                return launchSites;
            launchSites = new List<Vector3d>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (GameDatabase.Instance == null)
                return launchSites;
            foreach (ConfigNode root in GameDatabase.Instance.GetConfigNodes("KSCSWITCHER"))
            {
                ConfigNode sites = root.GetNode("LaunchSites");
                if (sites == null)
                    continue;
                foreach (ConfigNode site in sites.GetNodes("Site"))
                {
                    ConfigNode city = site.GetNode("PQSCity");
                    if (city != null &&
                        double.TryParse(city.GetValue("latitude"), System.Globalization.NumberStyles.Float, ci, out double lat) &&
                        double.TryParse(city.GetValue("longitude"), System.Globalization.NumberStyles.Float, ci, out double lon))
                        launchSites.Add(Geo.Direction(lat, lon));
                }
            }
            return launchSites;
        }

        bool Excluded(Vector3d dir)
        {
            foreach (DemExclusion e in exclusions)
                if (dir.x * e.X + dir.y * e.Y + dir.z * e.Z >= e.CosInner)
                    return true;
            return false;
        }

        void UpdateExclusionCaps()
        {
            exclusionCaps.Clear();
            foreach (DemExclusion e in exclusions)
                exclusionCaps.Add(new DemCap(new Vector3d(e.X, e.Y, e.Z), Math.Acos(Math.Max(-1.0, Math.Min(1.0, e.CosOuter)))));
        }

        /// <summary>The places kept as Sol has them are looked at again on the next update
        /// (SiteGrounds levelled one).</summary>
        public void RefreshExclusionsSoon() => nextExclusions = 0f;

        /// <summary>KSCSwitcher puts KSC in place after the sphere has started: the places kept
        /// as Sol has them are looked at again now and then; the terrain is rebuilt where they move.</summary>
        void RefreshExclusions(float now)
        {
            if (now < nextExclusions)
                return;
            nextExclusions = now + 20f;
            List<DemExclusion> fresh = ComputeExclusions();
            bool same = fresh.Count == exclusions.Count;
            for (int i = 0; same && i < fresh.Count; i++)
                same = Math.Abs(fresh[i].X - exclusions[i].X) + Math.Abs(fresh[i].Y - exclusions[i].Y) + Math.Abs(fresh[i].Z - exclusions[i].Z) < 2e-6; // ~3 m: float noise of world positions
            if (same)
                return;
            var caps = new List<DemCap>(exclusionCaps);
            exclusions = fresh;
            UpdateExclusionCaps();
            caps.AddRange(exclusionCaps);
            version++;
            RebuildGlobal();
            if (active.Count == 0 && Patches.Count == 0)
                return; // Sol's terrain everywhere anyway: nothing to build again
            MarkStale(caps);
            Changed?.Invoke(caps);
            Debug.Log($"[SolSatellite] Real terrain: {exclusions.Count} places kept as Sol has them (moved)");
        }

        // ------------------------------------------------------------------ landed vessels

        /// <summary>The terrain under landed vessels (and ones just above it) must not move. Where
        /// tiles near one are still missing, nothing around it comes in until it is airborne.</summary>
        void UpdateProtection()
        {
            protectedCaps.Clear();
            holdCaps.Clear();
            if (!HighLogic.LoadedSceneIsFlight || pqs == null)
                return;
            double radius = pqs.radius;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.mainBody != body)
                    continue;
                bool low = v.LandedOrSplashed || v.situation == Vessel.Situations.PRELAUNCH ||
                           v.heightFromTerrain >= 0f && v.heightFromTerrain < 150f;
                if (!low)
                    continue;
                Vector3d dir = Geo.Direction(v.latitude, v.longitude);
                if (Excluded(dir))
                    continue; // on a launch site: its ground stays Sol's anyway
                var near = new DemCap(dir, ProtectRadius / radius);
                protectedCaps.Add(near);
                foreach (DemTile t in wanted)
                {
                    if (!t.Active && t.Status != DemTile.State.Missing && t.Reach.Overlaps(near))
                    {
                        holdCaps.Add(new DemCap(dir, HoldRadius / radius));
                        break;
                    }
                }
            }
        }

        bool Held(DemTile t)
        {
            foreach (DemCap c in holdCaps)
                if (t.Reach.Overlaps(c))
                    return true;
            return false;
        }

        bool Protects(DemTile t)
        {
            foreach (DemCap c in protectedCaps)
                if (t.Reach.Overlaps(c))
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ which tiles

        DemTile GetTile(int z, int x, int y)
        {
            long key = Geo.TileKey(z, x, y);
            if (!tiles.TryGetValue(key, out DemTile t))
                tiles[key] = t = new DemTile(z, x, y, Blend);
            return t;
        }

        /// <summary>Tiles wanted around a point: each zoom as far out as the PQS builds quads fine
        /// enough to show it (times the prefetch factor), coarse zooms first.</summary>
        void WantAround(Focus f, float now, bool create)
        {
            if (!f.Valid || pqs == null)
                return;
            double radius = pqs.radius;
            DemRegion region = RegionAt(f.Lat, f.Lon);
            int minZoom = Math.Max(0, Math.Min(settings.TerrainMinZoom, MaxZoom));
            double cosLat = Math.Max(Math.Cos(f.Lat * Deg), 0.02);
            int maxZoom = Math.Max(minZoom, Math.Min(Math.Min(region.Zoom, MaxZoom), MeshZoom(radius, cosLat)));
            double alt = Math.Abs(f.Alt);
            // Vertex spacing / distance where the PQS subdivides (cacheSideVertCount 15, minDetailDistance).
            double ratio = Math.PI / (2.0 * (PQS.cacheSideVertCount - 1) * Math.Max(pqs.minDetailDistance, 1.0));
            double prefetch = Math.Max(1.0, settings.TerrainPrefetch) * Math.Max(1.0, region.Detail);
            Vector3d here = Geo.Direction(f.Lat, f.Lon);
            for (int z = minZoom; z <= maxZoom; z++)
            {
                int n = 1 << z;
                double pixel = 2.0 * Math.PI * radius * cosLat / (DemSampler.Size * (double)n);
                double reach = prefetch * pixel / ratio;
                if (z > minZoom && alt > reach)
                    break;
                double horizontal = Math.Max(0.0, reach - alt) / 1.3;
                double tileSize = pixel * DemSampler.Size;
                int k = Math.Min(4, (int)Math.Ceiling(horizontal / tileSize) + 1);
                int cx = (int)Math.Floor(Geo.MercatorX(f.Lon, n)), cy = (int)Math.Floor(Geo.MercatorY(f.Lat, n));
                for (int dy = -k; dy <= k; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= n)
                        continue;
                    for (int dx = -k; dx <= k; dx++)
                    {
                        int x = ((cx + dx) % n + n) % n;
                        long key = Geo.TileKey(z, x, y);
                        tiles.TryGetValue(key, out DemTile t);
                        double d;
                        if (dx == 0 && dy == 0)
                        {
                            d = 0.0;
                        }
                        else
                        {
                            Vector3d c = t != null ? new Vector3d(t.Cap.X, t.Cap.Y, t.Cap.Z) : DemTile.Center(z, x, y);
                            double half = t != null ? t.Cap.Angle : tileSize / radius * 0.75;
                            d = Math.Max(0.0, Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(here, c)))) - half) * radius;
                            if (d > horizontal)
                                continue;
                        }
                        if (t == null)
                        {
                            if (!create)
                                continue;
                            t = GetTile(z, x, y);
                        }
                        if (t.WantedAt != now || wantFrame != Time.frameCount)
                            wanted.Add(t);
                        t.WantedAt = now;
                        t.Order = z * 1e9 + d;
                    }
                }
            }
        }

        /// <summary>The finest zoom the PQS's last level can show: its vertices are
        /// (pi/2 R / 2^maxLevel) / 14 apart, and the tiles' pixels about half that (the sampler's
        /// B-spline smooths over about a pixel). Finer tiles would only be aliased into spikes:
        /// until 2026-10-02 a zoom finer was used and mountains came out jagged.</summary>
        int MeshZoom(double radius, double cosLat)
        {
            double spacing = Math.PI * 0.5 * radius / Math.Pow(2.0, pqs.maxLevel) / (PQS.cacheSideVertCount - 1);
            double z = Math.Log(2.0 * Math.PI * radius * cosLat / (DemSampler.Size * spacing), 2.0);
            return Math.Max(0, (int)Math.Ceiling(z - 1e-6));
        }

        bool AncestorMissing(DemTile t)
        {
            int x = t.X, y = t.Y;
            for (int z = t.Z - 1; z >= 0; z--)
            {
                x >>= 1;
                y >>= 1;
                if (missing.Contains(Geo.TileKey(z, x, y)))
                    return true;
            }
            return false;
        }

        string FileOf(DemTile t) => Path.Combine(cacheRoot, t.Z.ToString(), t.X.ToString(), t.Y + ".webp");

        string MissingFileOf(DemTile t) => Path.Combine(cacheRoot, t.Z.ToString(), t.X.ToString(), t.Y + ".missing");

        void Classify(DemTile t)
        {
            if (AncestorMissing(t))
            {
                SetMissing(t, false);
                return;
            }
            if (File.Exists(FileOf(t)))
                t.Status = DemTile.State.Disk;
            else if (File.Exists(MissingFileOf(t)))
                SetMissing(t, false);
            else
                t.Status = DemTile.State.Net;
        }

        void SetMissing(DemTile t, bool save)
        {
            t.Status = DemTile.State.Missing;
            missing.Add(t.Key);
            if (!save)
                return;
            string file = MissingFileOf(t);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, new byte[0]);
                }
                catch (Exception) { }
            });
        }

        // ------------------------------------------------------------------ loading

        void StartDisk(DemTile t)
        {
            t.Status = DemTile.State.Loading;
            t.FromNetwork = false;
            diskLoads++;
            string file = FileOf(t);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    t.Decoded = DemCodec.Decode(File.ReadAllBytes(file));
                    DiskCache.Touch(file);
                }
                catch (Exception e)
                {
                    t.Error = e.Message;
                    try
                    {
                        File.Delete(file); // damaged: fetched again
                    }
                    catch (Exception) { }
                }
                lock (finished)
                    finished.Add(t);
            });
        }

        void StartNet(DemTile t)
        {
            t.Status = DemTile.State.Loading;
            t.FromNetwork = true;
            netLoads++;
            netTokens -= 1f;
            string url = settings.TerrainUrl.Replace("{z}", t.Z.ToString()).Replace("{x}", t.X.ToString()).Replace("{y}", t.Y.ToString());
            UnityWebRequest req = UnityWebRequest.Get(url);
            req.timeout = 30;
            req.SetRequestHeader("User-Agent", settings.TerrainUserAgent);
            t.Request = req;
            req.SendWebRequest().completed += _ => FinishNet(t, req);
        }

        void FinishNet(DemTile t, UnityWebRequest req)
        {
            t.Request = null;
            try
            {
                if (disposed)
                    return;
                netLoads--;
                long code = req.responseCode;
                if (code == 404 || code == 204)
                {
                    SetMissing(t, true);
                    return;
                }
                if (req.isNetworkError || code >= 500 || code == 0)
                {
                    Retry(t);
                    return;
                }
                if (code == 403 || code == 429)
                {
                    Refused($"HTTP {code}");
                    t.Status = DemTile.State.Net;
                    return;
                }
                byte[] data = req.downloadHandler.data;
                if (req.isHttpError || data == null || data.Length < 20 || data[0] != 'R' || data[8] != 'W')
                {
                    Retry(t);
                    return;
                }
                strikes = 0;
                Downloaded++;
                DownloadedBytes += data.Length;
                string file = FileOf(t);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        t.Decoded = DemCodec.Decode(data);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        string tmp = file + ".part" + Thread.CurrentThread.ManagedThreadId;
                        File.WriteAllBytes(tmp, data);
                        if (File.Exists(file))
                            File.Delete(tmp);
                        else
                            File.Move(tmp, file);
                    }
                    catch (Exception e)
                    {
                        t.Error = e.Message;
                    }
                    lock (finished)
                        finished.Add(t);
                });
                t.Status = DemTile.State.Loading; // decoding
                diskLoads++;                       // counted as a load until decoded
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                t.Status = DemTile.State.Net;
            }
            finally
            {
                req.Dispose();
            }
        }

        void Retry(DemTile t)
        {
            t.Attempts++;
            t.Status = DemTile.State.Net;
            t.RetryAt = Time.realtimeSinceStartup + Math.Min(120f, 5f * t.Attempts * t.Attempts);
        }

        void Refused(string why)
        {
            if (Time.realtimeSinceStartup < pausedUntil)
                return;
            strikes++;
            if (strikes > 4)
            {
                networkDisabled = true;
                Debug.LogWarning($"[SolSatellite] Terrain server keeps refusing ({why}); terrain downloads off for this session");
                return;
            }
            float pause = Mathf.Min(900f, 60f * (1 << (strikes - 1)));
            pausedUntil = Time.realtimeSinceStartup + pause;
            Debug.LogWarning($"[SolSatellite] Terrain server refused ({why}); pausing terrain downloads for {pause:F0} s");
        }

        void CollectFinished()
        {
            lock (finished)
            {
                foreach (DemTile t in finished)
                {
                    diskLoads--;
                    if (disposed)
                    {
                        if (t.Decoded != IntPtr.Zero)
                            Marshal.FreeHGlobal(t.Decoded);
                        t.Decoded = IntPtr.Zero;
                        continue;
                    }
                    if (t.Decoded == IntPtr.Zero)
                    {
                        if (t.FromNetwork)
                        {
                            Debug.LogWarning($"[SolSatellite] Terrain tile {t.Z}/{t.X}/{t.Y} unreadable: {t.Error}");
                            t.Attempts++;
                        }
                        t.Status = t.Attempts >= 3 ? DemTile.State.Missing : DemTile.State.Net; // a damaged cache file: again from the server
                        t.RetryAt = Time.realtimeSinceStartup + 10f;
                        continue;
                    }
                    t.Data = t.Decoded;
                    t.Decoded = IntPtr.Zero;
                    t.Status = DemTile.State.Ready;
                }
                finished.Clear();
            }
        }

        // ------------------------------------------------------------------ the frame

        public void Update(Focus camera, Focus vessel)
        {
            float now = Time.realtimeSinceStartup;
            FreeLater();
            Patches.FreeUnused();
            if (pqs == null || body == null)
                Attach();
            CollectFinished();
            if (pqs == null || !pqs.isActive || disposed)
                return;
            parameters = MakeParams();
            SetLevelNow(camera);
            UpdateColliders();
            if (!Enabled)
            {
                if (active.Count > 0)
                    DeactivateAll();
                RebuildStale();
                return;
            }
            RefreshExclusions(now);
            CheckMemory(now);

            wanted.Clear();
            wantFrame = Time.frameCount;
            WantAround(camera, now, true);
            WantAround(vessel, now, true);
            wanted.Sort((a, b) => a.Order.CompareTo(b.Order));
            UpdateProtection();

            float rate = Mathf.Max(settings.TerrainRequestsPerSecond, 0.1f);
            netTokens = Mathf.Min(netTokens + Time.unscaledDeltaTime * rate, Mathf.Max(rate, 1f));
            bool networkOpen = !Paused && !networkDisabled && now >= pausedUntil;
            int queued = 0, coming = 0;
            changed.Clear();
            ready.Clear();
            foreach (DemTile t in wanted)
            {
                if (t.Status == DemTile.State.New)
                    Classify(t);
                switch (t.Status)
                {
                    case DemTile.State.Disk:
                        queued++;
                        coming++;
                        if (diskLoads < MaxDiskLoads)
                            StartDisk(t);
                        break;
                    case DemTile.State.Net:
                        queued++;
                        coming++;
                        if (networkOpen && now >= t.RetryAt && netLoads < Math.Max(1, settings.TerrainConnections) && netTokens >= 1f)
                            StartNet(t);
                        break;
                    case DemTile.State.Loading:
                        coming++;
                        break;
                    case DemTile.State.Ready:
                        if (!t.Active && !Held(t))
                            ready.Add(t);
                        break;
                }
            }
            QueuedCount = queued;
            // Each change has every quad under it built again (and Parallax's scatters on it
            // redone): tiles go in together, once the area is complete or after a moment.
            if (ready.Count == 0)
            {
                firstReadyAt = -1f;
            }
            else
            {
                if (firstReadyAt < 0f)
                    firstReadyAt = now;
                if (!memoryShort && (coming == 0 || now - firstReadyAt >= WaveWait || ready.Count >= WaveSize))
                {
                    foreach (DemTile t in ready)
                    {
                        t.Active = true;
                        active.Add(t);
                        changed.Add(t);
                    }
                    firstReadyAt = -1f;
                }
            }
            Evict(now);
            if (changed.Count > 0)
                Apply(changed);
            if (!memoryShort)
                RebuildStale();
            Log(now);
        }

        /// <summary>The game's own memory (private bytes) against the machine's: past 80% no
        /// tiles go in and nothing is rebuilt (each rebuild makes Parallax allocate again) until
        /// it is back under 70%.</summary>
        void CheckMemory(float now)
        {
            if (now < nextMemoryCheck)
                return;
            nextMemoryCheck = now + 5f;
            processBytes = ProcessMemory.Private(); // Process.PrivateMemorySize64 is 0 under KSP's Mono
            long total = (long)SystemInfo.systemMemorySize * 1048576L;
            if (total <= 0 || processBytes <= 0)
                return;
            bool was = memoryShort;
            memoryShort = memoryShort ? processBytes > total * 7 / 10 : processBytes > total * 8 / 10;
            if (memoryShort != was)
                Debug.LogWarning(memoryShort
                    ? $"[SolSatellite] Real terrain paused: the game uses {processBytes / 1073741824.0:0.0} GB of {total / 1073741824.0:0} GB"
                    : $"[SolSatellite] Real terrain resumed: the game uses {processBytes / 1073741824.0:0.0} GB");
        }

        /// <summary>The camera's region decides how deep the PQS goes now (within what it was
        /// started with).</summary>
        void SetLevelNow(Focus camera)
        {
            if (baseLevel < 0 || !injected)
                return;
            int room = MaxLevel1 != null ? (int)MaxLevel1.GetValue(pqs) - 1 : pqs.maxLevel;
            if (room < baseLevel)
                return; // the sphere hasn't sized its tables yet
            int level = baseLevel;
            if (Enabled && camera.Valid)
            {
                DemRegion r = RegionAt(camera.Lat, camera.Lon);
                RegionName = r.Name;
                level += ExtraLevels(r);
            }
            level = Math.Max(1, Math.Min(level, room));
            if (pqs.maxLevel != level)
                pqs.maxLevel = level;
            FitParallax();
        }

        void Apply(List<DemTile> tilesChanged)
        {
            version++;
            RebuildGlobal();
            var caps = new List<DemCap>(tilesChanged.Count);
            foreach (DemTile t in tilesChanged)
                caps.Add(t.Reach);
            MarkStale(caps);
            Changed?.Invoke(caps);
        }

        void DeactivateAll()
        {
            changed.Clear();
            foreach (DemTile t in active)
            {
                t.Active = false;
                changed.Add(t);
            }
            active.Clear();
            Apply(changed);
        }

        /// <summary>Within the memory limit: tiles not wanted for longest go first (not ones a
        /// build is using, nor ones under a landed vessel); requests not wanted for a while too.</summary>
        void Evict(float now)
        {
            int resident = 0;
            foreach (DemTile t in tiles.Values)
                if (t.Data != IntPtr.Zero)
                    resident++;
            ResidentCount = resident;
            int limit = Math.Max(32, settings.TerrainMemoryTiles);
            var drop = new List<DemTile>();
            foreach (DemTile t in tiles.Values)
            {
                bool idle = now - t.WantedAt > 60f;
                if (t.Status == DemTile.State.Net && idle || t.Status == DemTile.State.Disk && idle || t.Status == DemTile.State.New)
                    drop.Add(t);
                else if (t.Status == DemTile.State.Missing && idle && tiles.Count > 4096)
                    drop.Add(t);
                else if (t.Status == DemTile.State.Ready && !t.Active && now - t.WantedAt > 20f && t.Leases == 0)
                    drop.Add(t);
            }
            if (resident > limit)
            {
                var candidatesToFree = new List<DemTile>();
                foreach (DemTile t in tiles.Values)
                    if (t.Data != IntPtr.Zero && t.Leases == 0 && now - t.WantedAt > 5f && !Protects(t) && !drop.Contains(t))
                        candidatesToFree.Add(t);
                candidatesToFree.Sort((a, b) => a.WantedAt.CompareTo(b.WantedAt));
                for (int i = 0; i < candidatesToFree.Count && resident > limit; i++, resident--)
                    drop.Add(candidatesToFree[i]);
            }
            foreach (DemTile t in drop)
            {
                if (t.Active)
                {
                    t.Active = false;
                    active.Remove(t);
                    changed.Add(t);
                }
                if (t.Data != IntPtr.Zero)
                {
                    // Pointers to it in the managed path's block go when that is rebuilt (this frame).
                    freeLater.Add(new KeyValuePair<IntPtr, int>(t.Data, Time.frameCount + 3));
                    t.Data = IntPtr.Zero;
                }
                if (t.Status == DemTile.State.Loading)
                    continue;
                tiles.Remove(t.Key);
            }
        }

        void FreeLater()
        {
            for (int i = freeLater.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount < freeLater[i].Value)
                    continue;
                Marshal.FreeHGlobal(freeLater[i].Key);
                freeLater.RemoveAt(i);
            }
        }

        void Log(float now)
        {
            if (now < nextLog)
                return;
            nextLog = now + 30f;
            Debug.Log($"[SolSatellite] real terrain: {active.Count} tiles in ({ResidentCount} in memory), {QueuedCount} to load, " +
                      $"{diskLoads + netLoads} loading, {Downloaded} downloaded ({DownloadedBytes / 1048576.0:0.0} MB), {missing.Count} without data; " +
                      $"maxLevel {pqs.maxLevel} (Sol {baseLevel}, {RegionName}), quads {quads.Count}, to rebuild {stale.Count}, rebuilt {Rebuilt}; " +
                      (Patches.Count > 0 ? $"terrain patches {Patches.Count}; " : "") +
                      $"game memory {processBytes / 1073741824.0:0.0} GB, managed {GC.GetTotalMemory(false) / 1048576} MB" +
                      (memoryShort ? "; PAUSED: memory short" : "") +
                      (holdCaps.Count > 0 ? "; waiting: a landed vessel stands on Sol's terrain" : ""));
        }

        // ------------------------------------------------------------------ start of a scene

        /// <summary>Where the scene starts: the active vessel (from the save), or KSC.</summary>
        Focus StartFocus()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                FlightState state = HighLogic.CurrentGame?.flightState;
                if (state != null && state.activeVesselIdx >= 0 && state.activeVesselIdx < state.protoVessels.Count)
                {
                    ProtoVessel pv = state.protoVessels[state.activeVesselIdx];
                    int index = pv.orbitSnapShot?.ReferenceBodyIndex ?? -1;
                    if (index >= 0 && index < FlightGlobals.Bodies.Count && FlightGlobals.Bodies[index] == body)
                        return new Focus { Valid = true, Lat = pv.latitude, Lon = pv.longitude, Alt = pv.altitude };
                }
            }
            if (pqs != null)
            {
                foreach (PQSCity c in cities)
                {
                    if (c == null || c.name != "KSC")
                        continue;
                    Geo.LatLon(((Vector3d)c.repositionRadial).normalized, out double lat, out double lon);
                    return new Focus { Valid = true, Lat = lat, Lon = lon, Alt = 100.0 };
                }
            }
            return default;
        }

        /// <summary>The tiles on disk around the start, decoded on all cores and put in at once.</summary>
        void Preload()
        {
            Focus f = StartFocus();
            if (!f.Valid)
                return;
            float now = Time.realtimeSinceStartup;
            wanted.Clear();
            wantFrame = Time.frameCount;
            WantAround(f, now, true);
            var load = new List<DemTile>();
            foreach (DemTile t in wanted)
            {
                if (t.Status == DemTile.State.New)
                    Classify(t);
                if (t.Status == DemTile.State.Disk)
                    load.Add(t);
            }
            var watch = Stopwatch.StartNew();
            if (load.Count > 0)
            {
                foreach (DemTile t in load)
                    StartDisk(t);
                while (watch.ElapsedMilliseconds < 4000)
                {
                    bool busy = false;
                    foreach (DemTile t in load)
                        busy |= t.Status == DemTile.State.Loading && t.Decoded == IntPtr.Zero && t.Error == null;
                    if (!busy)
                        break;
                    Thread.Sleep(2);
                }
                Thread.Sleep(5); // the last workers add themselves to the list
                CollectFinished();
            }
            changed.Clear();
            foreach (DemTile t in wanted)
            {
                if (t.Status == DemTile.State.Ready && !t.Active)
                {
                    t.Active = true;
                    active.Add(t);
                    changed.Add(t);
                }
            }
            if (changed.Count > 0)
                Apply(changed);
            Debug.Log($"[SolSatellite] Real terrain: {changed.Count} tiles from the cache around ({f.Lat:0.###}, {f.Lon:0.###}) " +
                      $"in {watch.ElapsedMilliseconds} ms before the terrain is built");
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (DemTile t in tiles.Values)
            {
                t.Request?.Abort();
                if (t.Data != IntPtr.Zero)
                    Marshal.FreeHGlobal(t.Data);
                t.Data = IntPtr.Zero;
            }
            tiles.Clear();
            active.Clear();
            globalCount = 0;
            foreach (KeyValuePair<IntPtr, int> p in freeLater)
                Marshal.FreeHGlobal(p.Key);
            freeLater.Clear();
            if (globalBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(globalBlock);
            globalBlock = IntPtr.Zero;
            Patches.Dispose();
            if (Instance == this)
                Instance = null;
        }
    }
}
