// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>Day and night at the camera, from the sun's elevation above its horizon.</summary>
    static class Sky
    {
        public static double SunElevation(CelestialBody body, Vector3d cameraWorld)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || body == null)
                return 90.0;
            Vector3d up = (cameraWorld - body.position).normalized;
            Vector3d toSun = (sun.position - cameraWorld).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, toSun)))) * 180.0 / Math.PI;
        }

        /// <summary>Street and airfield lights: on from 3 degrees above the horizon, full at 3 below.</summary>
        public static float Lights(double elevation) => Mathf.Clamp01((float)((3.0 - elevation) / 6.0));

        /// <summary>Lit windows: from sunset, all of them at 6 degrees below.</summary>
        public static float Windows(double elevation) => Mathf.Clamp01((float)((2.0 - elevation) / 8.0));
    }

    /// <summary>
    /// OpenStreetMap buildings, street lamps and roads around the camera and around the vessel
    /// being flown (Focus.cs), in z14 tiles (~1.5-2.4 km) cached in PluginData/Cache/osm. Each
    /// has its own radius. Roads and the light lamps throw on the ground aren't objects but
    /// drawn into layers of the clipmap the terrain shader reads (DrawRoads, DrawLights). A
    /// tile nothing is near any more is dropped, and so are its downloads, Overpass included.
    /// Data comes in two steps:
    ///  * OpenFreeMap vector tiles first: a CDN, several requests at once, a tile in a fraction
    ///    of a second - footprints, heights, colours and roads;
    ///  * then, carefully and in the background, the same tile from the Overpass API with every
    ///    tag (building types, roof colours, mapped lamps, lit roads), and the tile is rebuilt
    ///    with it. Overpass is asked one query at a time, only when /api/status reports a free
    ///    slot, only for tiles within the building radius that have buildings at all, and only
    ///    while nothing moves so fast that the tile would be left behind before the reply.
    /// If vector tiles can't be had, Overpass is the only source. Buildings come from Overture
    /// Maps instead where it has the tile (Overture.cs): OpenStreetMap's with every tag, and
    /// Microsoft's and Google's footprints where OSM has none. They are fetched once a tile has
    /// its OSM data and merged into its cache file; a new tile waits a few seconds for them
    /// before its buildings are first shown, and Overpass then needn't send buildings at all.
    /// Real-world data lands by latitude and longitude, so on a smaller planet (Sol's Quarter
    /// scale: a quarter of the real Earth) it comes out smaller; what the meshers make in real
    /// metres - heights, widths, kerbs, lamp posts - is made at real size around a planet as big
    /// as the real one (a scaled height grid) and the tile's object is scaled down to fit.
    /// Far off, buildings and 3D roads narrower than a pixel or two are left out (BandEdges).
    /// Parsing and meshing run on worker threads; terrain heights are sampled on the main thread
    /// within a time budget. Tiles are children of the PQS, like KSC's own buildings; buildings
    /// near the active vessel get colliders baked on a job thread.
    /// </summary>
    sealed class BuildingManager : IDisposable
    {
        const double MaxAltitude = 15000.0;      // camera altitude up to which buildings are built
        const double LampMaxAltitude = 40000.0;  // ... and lamps (towns at night show from high up)
        const double Road3DMaxAltitude = 6000.0; // ... and 3D roads
        const double DropAltitude = 45000.0;     // above this everything is unloaded
        const double KeepMargin = 1500.0;    // what is built stays until this far beyond its radius
        const double FineSpacing = 65.0;    // m between terrain samples under buildings and 3D roads
        const double CoarseSpacing = 300.0; // ... under lamps alone (beyond the building radius)
        const double HeightMsPerFrame = 3.0; // main-thread time for terrain height samples (PQS: ~50 us each)
        const double UploadMsPerFrame = 3.0; // ... for handing meshes to Unity (one tile at least)
        // Worker threads reading, parsing and meshing tiles: all but a few of the processor's
        // threads (the game's main and render threads, Unity's jobs), at a lower priority than those.
        static readonly int MaxWorkers = Math.Max(4, Math.Min(12, Environment.ProcessorCount - 4));
        const int ProbeBatch = 1024;        // disk cache look-ups per batch, on a worker thread
        const int MaxVectorRequests = 8;
        // Detail by distance from the camera or vessel (m): up to the first edge everything is
        // built; beyond each, buildings smaller and 3D roads narrower than about two pixels at
        // that distance are left out. Pitched roofs keep their eaves up to the first edge and are
        // flat beyond the second; street lamps are points with pools of light on the ground up
        // to the second, points alone up to the third, and further out only the glow of the lit
        // streets (millions of lamps a pixel each would add up to a white blur).
        static readonly double[] BandEdges = { 1000.0, 2500.0, 5000.0, 10000.0 };
        const int WorkVisits = 800;  // tiles with something left to do looked at each frame, nearest first,
        const int PerSlice = 600;    // ... and all of them this many a frame, in turn
        // Tiles the nearest-first pass looks at a frame, at most: near the frontier, then a stretch
        // further on (lookCursor). With one tile left to do (an Overpass query) among 187 000 of
        // a 130 km lamp radius it went through all of them every frame, 12 ms (2026-10-03).
        const int NearLooks = 4000;
        const int WorkLooks = 12000;
        // Tiles whose data is on its way to a mesh (read or downloaded, parsed, waiting for heights
        // or a worker, meshed, waiting to be uploaded) at most: new reads and downloads wait
        // while this many are under way, so parsed data can't pile up faster than it is meshed
        // (2026-10-02: 16 000 parsed tiles queued, gigabytes of managed memory, long GC pauses).
        const int MaxStaged = 48;
        const int MaxVectorAttempts = 3;
        const int MaxOverpassAttempts = 6;
        const double ColliderRadius = 2500.0;
        const double ShadowRadius = 3000.0;  // buildings cast shadows only this close to the camera
        // Street lamps aren't seen by day: while the sun is higher than this at a focus (degrees)
        // lamps are built only in tiles read anyway (for buildings, roads, the scatter mask), not
        // in tiles of their own further out; once it is lower than NightLampsBelow again the whole
        // lamp radius is back (the lights come on at 3 degrees). 2026-10-03, Dubai by day with
        // lamps out to 130 km: 33 000 tiles waiting to be read for nothing seen.
        const double DayLampsAbove = 10.0;
        const double NightLampsBelow = 7.0;
        const int EraseLimit = 48;           // more lamp tiles gone at once: redraw instead of erasing each
        const double OverpassSeconds = 30.0; // a tile should stay wanted this long for an Overpass query
        const float OvertureWait = 6f;       // a new tile's buildings wait this long for Overture's
        const int MaxOvertureAttempts = 3;
        const string UserAgent = "SolSatellite/0.3 (personal KSP mod)";

        static readonly int SatParamsId = Shader.PropertyToID("_SolSatParams");
        static readonly int BuildId = Shader.PropertyToID("_SolSatBuild");
        static readonly int LightsId = Shader.PropertyToID("_SolSatLights");
        static readonly int CamUpId = Shader.PropertyToID("_SolSatCamUp");
        static readonly int FocusId = Shader.PropertyToID("_SolSatFocus");
        static readonly int Road3DId = Shader.PropertyToID("_SolSatRoad3D");
        static readonly int NightUpId = Shader.PropertyToID("_SolSatNightUp");
        static readonly int LampRangeId = Shader.PropertyToID("_SolSatLampRange");
        static readonly int NightFocusId = Shader.PropertyToID("_SolSatNightFocus");

        enum Job { None, Read, Vector, VectorParse, SupplementParse, Overpass, OverpassParse, OvertureParse, Mesh }

        /// <summary>Whether the disk cache has a tile: looked up in batches off the main thread.</summary>
        enum CacheProbe : byte { Unknown, Pending, Found, None }

        sealed class Tile
        {
            public int X, Y;
            public long Key;
            public double Distance;     // to the nearest focus, over the ground
            public double CameraDistance;
            public double ViewDistance, CameraView; // the same in a straight line, the focus's height over the ground included
            public int Index = -1;      // in `ordered`, as of the last scan
            public bool Removed;
            public bool Busy;           // in `busy`: a job, download or Overture request under way
            public bool InStaged;       // in `staged`: its data is on its way to a mesh
            public CacheProbe Probe;
            public bool WantBuildings, WantLamps, WantRoads, WantRoads3D, WantMask;
            public bool Keep, InBuildings, InLamps, InRoads, InRoads3D, InMask; // while scanning: near enough to some focus

            public HeightGrid Grid;
            public int GridNext;
            public int FineGrid, CoarseGrid;   // height grid sizes: under buildings and 3D roads, under lamps alone
            public int Band, MeshingBand, BuiltBand = -1; // detail by distance (BandEdges): wanted, being meshed, built
            public bool FlatGrid;      // Grid is a flat stand-in: lamps alone far off are only the glow of lit streets (light map)
            // Data versions: Stamp goes up with each new data (a download or a parse; not a cache
            // read). Meshed again from the same data, a tile keeps the parts its detail band doesn't change.
            public int Stamp, MeshingStamp, RoadsStamp = -1, MaskStamp = -1, LampsStamp = -1;
            public bool KeepRoads, KeepMask, KeepLamps; // left as they are by the running or finished mesh job
            public bool GridDone => Grid != null && GridNext >= Grid.Radius.Length;
            public bool TerrainStale;  // the real relief changed under it since its heights were sampled
            public bool HasCap;
            public DemCap Cap;         // the tile on the sphere, for TerrainChanged

            public bool CacheChecked;
            public OsmSource? Shown;   // source of the data built or being built; null: none yet
            public OsmTile Pending;    // data waiting to be meshed
            public bool WantDetail;    // vector data with buildings: Overpass has more to say
            public bool LacksRail, LacksBridges; // cached before railways, bridges were kept
            public bool LacksAreas;              // ... before areas (forests, towns, water) were: the scatter mask needs them
            public bool Supplementing;   // the vector tile being fetched only adds those to the cached data
            public bool VectorFailed, OverpassFailed, Heavy;
            public int VectorAttempts, OverpassAttempts;
            public float VectorRetryAt, OverpassRetryAt;
            public bool HasOverture;      // the cached buildings are Overture's (or it has none here)
            public bool NeedsOverture;    // ... not yet
            public bool OvertureFailed;   // not again this session
            public int OvertureAttempts;
            public float OvertureRetryAt, OvertureUntil; // buildings wait for Overture until then
            public OvertureRequest Overture;

            // At most one job per tile. Worker threads set WorkDone last.
            public Job Job;
            public volatile bool WorkDone;
            public string Error;
            public OsmTile WorkData;
            public BuildingMeshData WorkMesh;
            public LampMeshData WorkLamps;
            public RibbonMeshData WorkRoads, WorkMask;
            public Road3DMeshData WorkRoads3D;
            public UnityWebRequest Request;
            public bool MeshingBuildings, MeshingLamps, MeshingRoads, MeshingRoads3D, MeshingMask; // what the running or finished mesh job covers
            public bool ReadyToUpload;
            public BuildingMeshData ReadyMesh;
            public LampMeshData ReadyLamps;
            public RibbonMeshData ReadyRoads, ReadyMask;
            public Road3DMeshData ReadyRoads3D;

            public GameObject Root;       // at the tile's frame; buildings, lamps and 3D roads are its children
            public GroundMesh Mask;       // in the scatter mask layer
            public bool MaskBuilt;
            public bool BuildingsBuilt, LampsBuilt, RoadsBuilt, Roads3DBuilt;
            public GameObject Road3DObject, BridgeObject; // 3D roads, and their bridges (which cast shadows)
            public Mesh Road3DMesh, BridgeMesh;
            public int Road3DVertices;
            public GameObject Object;     // buildings
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public int Buildings, Vertices, Pitched;
            public GameObject OldObject;  // replaced buildings, kept only for their collider until the new one is baked
            public Mesh OldMesh;
            public bool Baking, HasCollider;
            public JobHandle Bake;
            public GameObject LampObject;
            public Mesh LampMesh;
            public int Lamps, LampVertices, Pools;

            // Pools of light for the terrain's night light map, in the clipmap's plane.
            public List<GroundLight> Lights;
            public Mesh LightMesh;
            public double LightX0, LightY0, LightX1, LightY1;
            public bool LightNew; // not drawn into the light map yet

            // Lit streets in the tile's flat frame (light map); roads go to the road layer.
            public Mesh GlowMesh;
            public bool HasGlowExtent;
            public double GlowMinX, GlowMinY, GlowMaxX, GlowMaxY; // the lit streets' extent in the flat frame
            public GroundMesh Road;
            // Flat frame -> clipmap plane, and the glow's extent there, for plane FramePlane.
            public int FramePlane = -1;
            public bool ExtentsStale;  // ribbons replaced: extents to be worked out again
            public Matrix4x4 Frame;
            public double GlowX0, GlowY0, GlowX1, GlowY1;
        }

        /// <summary>A focus as a scan sees it.</summary>
        struct ScanFocus
        {
            public bool Valid;
            public double Building, Lamp, Road, Road3D, Mask; // radii (m), 0: none
            public double Radius, Keep, Create;  // the widest, kept out to, new tiles out to (m)
            public double Tx, Ty, MetersPerTile; // where it is in tile units, a tile's width there
            public double Height;                // over the ground (m)
            public int Kept;
        }

        struct StatusCounts
        {
            public int Built, Detailed, DetailWaiting, Failed, Overture;
        }

        struct BakeJob : IJob
        {
            public int MeshId;
            public void Execute() => Physics.BakeMesh(MeshId, false);
        }

        readonly CelestialBody body;
        readonly Settings settings;
        readonly string cacheDir;
        readonly Material material, lampMaterial, road3DMaterial;
        readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        readonly List<Tile> ordered = new List<Tile>();  // by distance, as of the last scan
        readonly List<Tile> orphans = new List<Tile>(); // removed while a worker still runs for them
        readonly List<Tile> busy = new List<Tile>();    // a job, download or Overture request under way: polled every frame
        int frontier;  // ordered[0..frontier) had nothing left to do when last looked at
        // New tiles are made only as far out as there is room for this many with something left
        // to do, beyond those done: the pipeline works outwards anyway, and with lamps out to
        // 100 km there are a quarter of a million tile places (6.5 million at 500 km: seconds a
        // scan and gigabytes of memory, 2026-10-02). The horizon grows as tiles get done.
        const int MaxWaiting = 30000;
        int settledCount;               // tiles with nothing left to do, as of the last scan
        readonly ScanFocus[] scanFoci = new ScanFocus[2];
        readonly double[] scanCreate = new double[2]; // per focus: new tiles out to (m), for the log
        ulong[] scanBits = new ulong[0];  // tile places around a focus that have a tile (Scan)
        // The settings window's tile counts, a slice of the tiles a frame while it shows them.
        const int CountedPerFrame = 20000;
        StatusCounts counting, counted;
        int countCursor = -1;
        float statusWantedUntil;
        int[] sortKeys = new int[0], sortCounts = new int[0];
        Tile[] sortTiles = new Tile[0];
        const double SortStep = 25.0;        // m: tiles are sorted by distance in steps of this
        const int MaxSortBuckets = 1 << 20;
        // Disk cache look-ups (File.Exists takes tens of microseconds): a batch at a time on a worker.
        readonly List<Tile> probeQueue = new List<Tile>();
        Tile[] probing;
        bool[] probeFound;
        volatile bool probeDone;
        readonly System.Diagnostics.Stopwatch heightClock = new System.Diagnostics.Stopwatch();
        readonly System.Random jitter = new System.Random();
        List<BuildingMesher.Exclusion> exclusions = new List<BuildingMesher.Exclusion>();
        PhysicMaterial physics;
        int workers;
        float nextScan, nextExclusions, nextPoolTrim;
        readonly Focus[] foci = new Focus[2];     // camera, vessel
        readonly bool[] daylight = new bool[2];   // per focus: the sun high there (DayLampsAbove)
        readonly int[] scanReach = new int[2], scanKept = new int[2]; // last scan, per focus, for the log
        readonly double[] scanTile = new double[2];
        readonly Focus[] scanned = new Focus[2];  // as of the last scan, for the speed
        float lastScan;
        double groundSpeed; // fastest focus, metres per real second
        bool lightsOn;

        // Night light map bookkeeping.
        Clipmap lightClipmap;
        int lightPlane = -1;
        readonly int[] lightVersion = new int[Clipmap.MaxLevels];
        readonly List<Erase> erase = new List<Erase>();
        readonly Vector4[] strips = new Vector4[2];
        bool lightsInMap;

        // Tiles whose light is new since the light map was last drawn (their LightNew set), and
        // whose pools still need a mesh: so a frame looks at those, not at all.
        readonly List<Tile> lightsNew = new List<Tile>(), poolsToMesh = new List<Tile>();
        readonly List<Tile> litNear = new List<Tile>(); // LitTiles' answer
        readonly HashSet<Tile> lit = new HashSet<Tile>(); // tiles with light for the light map (pools or lit streets)
        // Levels of the light map drawn whole in a frame: once this many draws are done, the rest
        // wait for the next frame (a wide level draws every lit tile, thousands) - one at least.
        const int LightDrawBudget = 3000;
        int lightDraws;
        // Light pool meshes are built here, one tile after another (main thread).
        static readonly List<Vector3> poolVertices = new List<Vector3>();
        static readonly List<Color32> poolColours = new List<Color32>();
        static readonly List<Vector4> poolCorners = new List<Vector4>();
        static readonly List<int> poolTriangles = new List<int>();
        readonly HashSet<Tile> withMesh = new HashSet<Tile>(); // tiles with a buildings mesh (UpdateColliders)
        // Replaced or dropped builds whose collider bake (a job reading the mesh) was still
        // running: hidden, and destroyed once it is done (Retire) rather than waited for.
        struct Retired
        {
            public JobHandle Bake;
            public GameObject Object;
            public Mesh Mesh;
        }
        readonly List<Retired> retired = new List<Retired>();
        // The slowest part of the last Upload, for TimedVisit's name of a slow visit.
        string uploadPart;
        double uploadPartMs;
        string roads3DNote; // a slow 3D roads upload in detail (ReplaceRoads3D)
        readonly List<Tile> colliderList = new List<Tile>();
        readonly List<Tile> staged = new List<Tile>(), stagedVisit = new List<Tile>();
        const double LookupSpan = 50000.0;              // ... looked up by place in areas smaller than this (m)

        readonly RoadLayer roads;  // roads are drawn into the clipmap's road layer through it
        readonly RoadLayer maskLayer; // and where scatters belong into its scatter mask layer
        const double MaskRadius = 11000.0; // m: Parallax's trees reach 10.5 km
        readonly OvertureStore overture;
        readonly double worldScale; // planet radius / the real Earth's
        readonly System.Diagnostics.Stopwatch uploadClock = new System.Diagnostics.Stopwatch();
        float nextFullScan;
        double scanSignature = double.NaN;
        int slice; // where the turn of the far tiles in Advance has got to
        int lookCursor; // where the nearest-first pass's stretch beyond NearLooks has got to
        bool horizonLimited; // as of the last scan, new tiles weren't made out to some focus's full radius

        // Throughput since the last "osm throughput" log line: cache reads by why, meshes and
        // uploads, parts kept as they were on a re-mesh, and the workers' time by job.
        enum ReadWhy { New, Detail, Relief, Parts }
        static readonly string[] ReadWhyNames = { "new", "detail band", "relief", "new parts" };
        readonly int[] readsBy = new int[4];
        int meshesDone, uploadsDone, partsKept;
        readonly long[] jobTicks = new long[9]; // by Job
        readonly int[] jobRuns = new int[9];
        float throughputSince;

        // OpenFreeMap
        string vectorTemplate;
        UnityWebRequest tileJsonRequest;
        float tileJsonRetryAt;
        int tileJsonFailures, vectorRequests, vectorFailuresInRow;
        string vectorProblem;

        // Overpass
        Tile overpassTile;
        UnityWebRequest overpassStatus, overpassQuery;
        float overpassWaitUntil, overpassStarted, overpassSearchAt;
        bool overpassSlotFree, statusUnsupported;
        int overpassStrikes;
        string overpassState;

        /// <summary>Where things are loaded around and how far, for the log.</summary>
        public string Coverage
        {
            get
            {
                var sb = new StringBuilder();
                string[] names = { "camera", "vessel" };
                for (int i = 0; i < foci.Length; i++)
                {
                    Focus f = foci[i];
                    if (!f.Valid)
                        continue;
                    sb.Append($"{names[i]} {f.Lat:0.####},{f.Lon:0.####} at {f.Alt:0} m " +
                              $"(radii b{BuildingRadius(f) / 1000:0.#} l{LampRadius(i) / 1000:0.#}{(daylight[i] ? " by day" : "")} r{RoadRadius(f) / 1000:0.#} " +
                              $"3d{Road3DRadius(f) / 1000:0.#} km; " +
                              $"new tiles out to {scanCreate[i] / 1000:0.#} km, reach {scanReach[i]} tiles of {scanTile[i]:0} m, kept {scanKept[i]}); ");
                }
                sb.Append($"tiles {tiles.Count} ({settledCount} done, {lit.Count} lit), world scale {worldScale:0.###}");
                return sb.ToString();
            }
        }

        /// <summary>Why nothing new is built, when that is because of altitude.</summary>
        public string AltitudeNote { get; private set; }
        public int OverpassCancelled { get; private set; }
        public int BuildingCount { get; private set; }
        public int PitchedCount { get; private set; }
        public int LampCount { get; private set; }
        public long BuildingVertices { get; private set; }
        public long LampVertices { get; private set; }
        public int PoolCount { get; private set; }
        public int BuildingTiles { get; private set; }
        public int LampTiles { get; private set; }
        public int ShadowTiles { get; private set; }
        public int RoadTiles { get; private set; }
        public long RoadVertices { get; private set; }
        public int Road3DTiles { get; private set; }
        public long Road3DVertices { get; private set; }
        public int VectorDownloaded { get; private set; }
        public int VectorFailures { get; private set; }
        public int OverpassDownloaded { get; private set; }
        public long DownloadedBytes { get; private set; }

        public BuildingManager(CelestialBody body, Settings settings, Shader shader, Shader lampShader, Shader road3DShader,
                               string cacheDir, RoadLayer roads, RoadLayer maskLayer)
        {
            this.body = body;
            this.settings = settings;
            this.cacheDir = cacheDir;
            this.roads = roads;
            this.maskLayer = maskLayer;
            worldScale = body.Radius / 6371000.0;
            if (Math.Abs(worldScale - 1.0) < 0.01)
                worldScale = 1.0;
            material = new Material(shader) { name = "SolSatellite buildings" };
            material.SetTexture("_SolSatMatColour", Materials.ColourOrPlain);
            material.SetTexture("_SolSatMatNormal", Materials.NormalOrPlain);
            material.SetVectorArray("_SolSatMatAverage", Materials.Average);
            overture = new OvertureStore(settings.OvertureUrl, Path.Combine(cacheDir, "overture"));
            if (lampShader != null)
                lampMaterial = new Material(lampShader) { name = "SolSatellite lamps" };
            if (road3DShader != null && Materials.Colour != null)
            {
                road3DMaterial = new Material(road3DShader) { name = "SolSatellite 3D roads" };
                road3DMaterial.SetTexture("_SolSatRoadColour", Materials.Colour);
                road3DMaterial.SetTexture("_SolSatRoadNormal", Materials.Normal);
            }
        }

        bool LampsOn => settings.LampsEnabled && lampMaterial != null;

        double BuildingRadius(Focus f) =>
            f.Valid && settings.BuildingsEnabled && f.Alt <= MaxAltitude ? settings.BuildingRadiusKm * 1000.0 : 0.0;

        double LampRadius(int i)
        {
            Focus f = foci[i];
            if (!f.Valid || !LampsOn || f.Alt > LampMaxAltitude)
                return 0.0;
            double radius = settings.LampRadiusKm * 1000.0;
            if (daylight[i])
                radius = Math.Min(radius, Math.Max(Math.Max(BuildingRadius(f), RoadRadius(f)), Math.Max(Road3DRadius(f), ScatterMaskRadius(f))));
            return radius;
        }

        /// <summary>Whether the sun is high at each focus (DayLampsAbove, NightLampsBelow).</summary>
        void UpdateDaylight()
        {
            for (int i = 0; i < foci.Length; i++)
            {
                Focus f = foci[i];
                if (!f.Valid)
                {
                    daylight[i] = false;
                    continue;
                }
                double sun = Sky.SunElevation(body, body.GetWorldSurfacePosition(f.Lat, f.Lon, f.Alt));
                daylight[i] = daylight[i] ? sun > NightLampsBelow : sun > DayLampsAbove;
            }
        }

        double RoadRadius(Focus f) =>
            f.Valid && settings.RoadsEnabled && f.Alt <= LampMaxAltitude ? settings.RoadRadiusKm * 1000.0 : 0.0;

        /// <summary>Where the scatter mask is drawn (ScatterMask): Parallax's scatters reach this far.</summary>
        double ScatterMaskRadius(Focus f) =>
            f.Valid && settings.ScatterMask && ScatterMask.Ready && f.Alt <= MaxAltitude ? MaskRadius : 0.0;

        double Road3DRadius(Focus f) =>
            f.Valid && Roads3DOn && f.Alt <= Road3DMaxAltitude ? settings.Road3DRadiusKm * 1000.0 : 0.0;

        bool Roads3DOn => settings.Roads3D && road3DMaterial != null;

        bool OvertureOn => settings.OvertureEnabled && overture.Available;

        /// <summary>A tile's buildings are held back a moment for Overture's.</summary>
        bool WaitingForOverture(Tile t, float now) => OvertureOn && t.NeedsOverture && !t.OvertureFailed && now < t.OvertureUntil;

        /// <summary>Overpass answers in tens of seconds: not worth asking while flying past.</summary>
        bool OverpassInTime => groundSpeed * OverpassSeconds < settings.BuildingRadiusKm * 1000.0;

        /// <summary>Builds and keeps what is near the camera or near the vessel.</summary>
        public void Update(Focus camera, Focus vessel)
        {
            foci[0] = camera;
            foci[1] = vessel;
            float now = Time.realtimeSinceStartup;
            UpdateDaylight();
            long step = System.Diagnostics.Stopwatch.GetTimestamp();
            CountSome(now);
            Timed(ref step, "counting tiles for the window");
            PollOverpass(now); // requests in flight finish even when nothing new is wanted
            PollOrphans();
            if (now >= nextPoolTrim)
            {
                nextPoolTrim = now + 5f;
                MeshPools.Trim(); // mesh data no tile took for a minute is let go
                Timed(ref step, "mesh pools trimmed");
            }
            if (settings.OvertureEnabled)
            {
                overture.MaxRequests = Math.Max(1, settings.OvertureConnections);
                overture.Update();
                Timed(ref step, "Overture requests");
            }
            bool low = false, building = false;
            for (int i = 0; i < foci.Length; i++)
            {
                Focus f = foci[i];
                low |= f.Valid && f.Alt <= DropAltitude;
                building |= BuildingRadius(f) > 0.0 || LampRadius(i) > 0.0 || RoadRadius(f) > 0.0 || Road3DRadius(f) > 0.0 ||
                            ScatterMaskRadius(f) > 0.0;
            }
            AltitudeNote = null;
            if (settings.BuildingsEnabled && BuildingRadius(camera) <= 0.0 && BuildingRadius(vessel) <= 0.0)
                AltitudeNote = $"Здания строятся ниже {MaxAltitude / 1000.0:0} км: камера на {camera.Alt / 1000.0:0} км" +
                               (vessel.Valid ? $", аппарат на {vessel.Alt / 1000.0:0} км" : "");
            if (!settings.BuildingsEnabled && !LampsOn && !settings.RoadsEnabled && !settings.ScatterMask || !low || body.pqsController == null)
            {
                if (tiles.Count > 0)
                {
                    step = System.Diagnostics.Stopwatch.GetTimestamp();
                    Clear();
                    Timed(ref step, "everything unloaded");
                }
                return;
            }
            if (!building)
                return; // high up: what is built stays (up to DropAltitude), nothing new is built

            step = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= nextExclusions)
            {
                RefreshExclusions();
                Timed(ref step, "exclusions");
            }
            if (now >= nextScan)
            {
                // Thousands of tiles: look again only when a focus moved a good bit, a radius
                // changed, or a while has passed.
                nextScan = now + 0.5f;
                if (now >= nextFullScan || ScanMoved())
                {
                    Perf.Begin(Perf.Part.CityScan);
                    Scan();
                    Perf.End(Perf.Part.CityScan);
                    // Again after a while only while the horizon grows as tiles get done: with
                    // everything out to the radii made, a scan of 187 000 tiles every 10 s was a
                    // 105 ms frame for nothing (2026-10-03). Moving, radii, day and night start one anyway.
                    nextFullScan = now + (horizonLimited ? 10f : 60f);
                    Timed(ref step, $"scan of {tiles.Count} tiles");
                }
            }
            UpdateTileJson(now);
            Perf.Begin(Perf.Part.CityPipeline);
            Advance(now);
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            StartOverpass(now);
            Timed(ref step, "Overpass");
            Perf.End(Perf.Part.CityPipeline);
            Perf.Begin(Perf.Part.CityColliders);
            UpdateColliders();
            Timed(ref step, "building colliders");
            Perf.End(Perf.Part.CityColliders);
        }

        /// <summary>Names a piece of the mod's work that took long (Perf.Slow), for the log's
        /// note on slow frames; `start` moves on to now.</summary>
        static void Timed(ref long start, string what)
        {
            if (Perf.Slow(start, out double ms))
                Perf.Named(ms, what);
            start = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>Visit, timed: a slow one is named with what it did.</summary>
        void TimedVisit(Tile t, float now, ref bool uploaded)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Job job = t.Job;
            bool ready = t.ReadyToUpload, pending = t.Pending != null;
            int grid = t.GridNext;
            uploadPart = null;
            uploadPartMs = 0.0;
            Visit(t, now, ref uploaded);
            if (Perf.Slow(start, out double ms))
                Perf.Named(ms, $"tile {t.X}/{t.Y} {Did(t, job, ready, pending, grid)} (band {t.Band}, {t.Vertices} building vertices, " +
                               $"{t.LampVertices} lamp vertices{(uploadPart != null ? $"; longest part: {uploadPart} {uploadPartMs:0} ms" : "")})");
        }

        /// <summary>Upload's parts, timed: the slowest goes into a slow visit's name.</summary>
        void UploadPart(ref long start, string part)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > uploadPartMs)
            {
                uploadPartMs = ms;
                uploadPart = part;
            }
            start = now;
        }

        /// <summary>A build that is going but whose collider bake still reads its mesh: hidden
        /// now, destroyed when the bake is done.</summary>
        void Retire(JobHandle bake, GameObject obj, Mesh mesh)
        {
            if (obj != null)
            {
                MeshRenderer r = obj.GetComponent<MeshRenderer>();
                if (r != null)
                    r.enabled = false;
            }
            retired.Add(new Retired { Bake = bake, Object = obj, Mesh = mesh });
        }

        /// <summary>Retired builds whose bake is done go (all of them: force).</summary>
        void FinishRetired(bool force)
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                Retired r = retired[i];
                if (!force && !r.Bake.IsCompleted)
                    continue;
                r.Bake.Complete();
                if (r.Object != null)
                    UnityEngine.Object.Destroy(r.Object);
                if (r.Mesh != null)
                    UnityEngine.Object.Destroy(r.Mesh);
                retired.RemoveAt(i);
            }
        }

        static string Did(Tile t, Job job, bool ready, bool pending, int grid)
        {
            if (ready && !t.ReadyToUpload)
                return "uploaded";
            if (job != Job.None && t.Job != job)
                return "finished " + job;
            if (pending && t.Pending == null && t.Job == Job.Mesh)
                return "meshing started";
            if (t.GridNext != grid)
                return $"terrain heights ({t.GridNext - grid})";
            if (job == Job.None && t.Job != Job.None)
                return t.Job + " started";
            return "dropped parts or else";
        }

        /// <summary>Per-frame shader values; call right before the terrain renders.</summary>
        public void UpdateMaterial(Clipmap clipmap, bool clipOn, float strength, Vector3d cameraWorld)
        {
            material.SetVector(SatParamsId, new Vector4(0f, strength, clipOn ? 1f : 0f, 0f));
            if (clipOn)
                clipmap.Apply(material);
            Vector3d up = (cameraWorld - body.position).normalized;
            var camUp = new Vector4((float)up.x, (float)up.y, (float)up.z, 0f);
            material.SetVector(CamUpId, camUp);
            float radius = settings.BuildingRadiusKm * 1000f;
            double sun = Sky.SunElevation(body, cameraWorld);
            material.SetVector(BuildId, new Vector4(Sky.Windows(sun), radius, 1f / (0.2f * radius), 0f));
            // Buildings and lamps near the vessel stay drawn while the camera is away from it.
            Vector4 focus = Vector4.zero;
            if (foci[1].Valid)
            {
                Vector3d p = body.GetWorldSurfacePosition(foci[1].Lat, foci[1].Lon, foci[1].Alt);
                focus = new Vector4((float)p.x, (float)p.y, (float)p.z, 1f);
            }
            material.SetVector(FocusId, focus);
            Shader.SetGlobalVector(NightUpId, camUp);
            Shader.SetGlobalVector(NightFocusId, focus);
            if (road3DMaterial != null)
            {
                road3DMaterial.SetVector(SatParamsId, new Vector4(0f, strength, clipOn ? 1f : 0f, 0f));
                if (clipOn)
                    clipmap.Apply(road3DMaterial);
                road3DMaterial.SetVector(CamUpId, camUp);
                road3DMaterial.SetVector(FocusId, focus);
                float r3 = settings.Road3DRadiusKm * 1000f;
                road3DMaterial.SetVector(Road3DId, new Vector4(r3, 1f / Mathf.Max(0.15f * r3, 50f), 0f, 0f));
            }
            if (lampMaterial == null)
                return;
            lampMaterial.SetVector(FocusId, focus);
            float night = Sky.Lights(sun);
            float lampRadius = settings.LampRadiusKm * 1000f;
            lampMaterial.SetVector(CamUpId, camUp);
            lampMaterial.SetVector(LightsId, new Vector4(night, lampRadius, 1f / (0.15f * lampRadius), settings.LampBrightness));
            // Points of light fade out from the second detail band's edge to the third's, where
            // tiles have none any more (only their streets' glow).
            float pointsTo = (float)BandEdges[2], pointsFrom = (float)BandEdges[1];
            lampMaterial.SetVector(LampRangeId, new Vector4(pointsTo, 1f / (pointsTo - pointsFrom), 0f, 0f));
            bool on = night > 0.01f;
            if (on != lightsOn)
            {
                lightsOn = on; // no lamp draws at all by day
                foreach (Tile t in tiles.Values)
                    if (t.LampObject != null)
                        t.LampObject.GetComponent<MeshRenderer>().enabled = on;
            }
        }

        // ------------------------------------------------------------------ tiles to show

        /// <summary>Has a focus moved a quarter of a tile, or a radius changed, since the last scan?</summary>
        bool ScanMoved()
        {
            double signature = 0.0;
            for (int i = 0; i < foci.Length; i++)
            {
                Focus f = foci[i], s = scanned[i];
                signature += (i + 1) * (BuildingRadius(f) + 3.0 * LampRadius(i) + 7.0 * RoadRadius(f) + 11.0 * Road3DRadius(f) +
                                        13.0 * ScatterMaskRadius(f));
                if (f.Valid != s.Valid)
                    return true;
                // A quarter of a tile, or 1% of the widest radius: with lamps out to 50 km a
                // camera swinging round the vessel would rescan tens of thousands of tiles twice a
                // second for nothing.
                double radius = Math.Max(Math.Max(Math.Max(BuildingRadius(f), LampRadius(i)), Math.Max(RoadRadius(f), Road3DRadius(f))),
                                         ScatterMaskRadius(f));
                if (f.Valid && Focus.Distance(f, s, body.Radius) > Math.Max(0.25 * MetersPerTile(f.Lat), 0.01 * radius))
                    return true;
                if (f.Valid && Math.Abs(f.Alt - s.Alt) > Math.Max(150.0, 0.15 * Math.Abs(s.Alt)))
                    return true;
            }
            return signature != scanSignature;
        }

        void Scan()
        {
            UpdateSpeed();
            int n = 1 << OsmTile.Zoom;
            double signature = 0.0;
            for (int i = 0; i < foci.Length; i++)
            {
                Focus f = foci[i];
                ref ScanFocus s = ref scanFoci[i];
                s = default;
                s.Building = BuildingRadius(f);
                s.Lamp = LampRadius(i);
                s.Road = RoadRadius(f);
                s.Road3D = Road3DRadius(f);
                s.Mask = ScatterMaskRadius(f);
                s.Radius = Math.Max(Math.Max(Math.Max(Math.Max(s.Building, s.Lamp), s.Road), s.Road3D), s.Mask);
                scanReach[i] = scanKept[i] = 0;
                scanCreate[i] = 0.0;
                if (s.Radius <= 0.0 || Math.Abs(f.Lat) > 84.0)
                    continue; // (web map tiles end at 85 degrees and shrink to nothing towards them)
                s.Valid = true;
                signature += (i + 1) * (s.Building + 3.0 * s.Lamp + 7.0 * s.Road + 11.0 * s.Road3D + 13.0 * s.Mask);
                s.Keep = s.Radius + KeepMargin;
                s.Tx = OsmTile.TileX(f.Lon);
                s.Ty = OsmTile.TileY(f.Lat);
                s.MetersPerTile = MetersPerTile(f.Lat);
                s.Height = HeightOverGround(f);
                // New tiles only as far out as there is room for MaxWaiting more than are done.
                double horizon = Math.Sqrt((settledCount + MaxWaiting) / Math.PI) * s.MetersPerTile;
                s.Create = Math.Min(s.Radius, horizon);
                scanTile[i] = s.MetersPerTile;
                scanCreate[i] = s.Create;
            }
            horizonLimited = false;
            for (int i = 0; i < foci.Length; i++)
                horizonLimited |= scanFoci[i].Valid && scanFoci[i].Create < scanFoci[i].Radius;
            lookCursor = 0;

            // The tiles there are: how far they are, by arithmetic.
            foreach (Tile t in tiles.Values)
                Evaluate(t);

            // New tiles where there are none yet, within each focus's horizon.
            for (int i = 0; i < foci.Length; i++)
            {
                ScanFocus s = scanFoci[i];
                if (!s.Valid)
                    continue;
                int cx = (int)Math.Floor(s.Tx), cy = (int)Math.Floor(s.Ty);
                double createTiles = s.Create / s.MetersPerTile;
                int reach = (int)Math.Ceiling(createTiles) + 1;
                scanReach[i] = reach;
                // Which places around the focus have a tile, as bits: far cheaper than a
                // dictionary look-up for each of tens of thousands of places.
                int w = 2 * reach + 1, x0 = cx - reach, y0 = cy - reach;
                int words = (int)(((long)w * w + 63) / 64);
                if (scanBits.Length < words)
                    scanBits = new ulong[words];
                Array.Clear(scanBits, 0, words);
                foreach (Tile t in tiles.Values)
                {
                    int bx = t.X + (int)Math.Round((double)(cx - t.X) / n) * n - x0; // across the antimeridian too
                    int by = t.Y - y0;
                    if ((uint)bx < (uint)w && (uint)by < (uint)w)
                    {
                        long bit = (long)by * w + bx;
                        scanBits[bit >> 6] |= 1UL << (int)(bit & 63);
                    }
                }
                for (int y = Math.Max(0, cy - reach); y <= Math.Min(n - 1, cy + reach); y++)
                {
                    // Only the columns of this row that the circle reaches.
                    double rowGap = Math.Max(0.0, Math.Max(y - s.Ty, s.Ty - (y + 1)));
                    if (rowGap > createTiles)
                        continue;
                    int span = Math.Min(reach, (int)Math.Ceiling(Math.Sqrt(createTiles * createTiles - rowGap * rowGap)) + 1);
                    for (int ux = cx - span; ux <= cx + span; ux++)
                    {
                        long bit = (long)(y - y0) * w + (ux - x0);
                        if ((scanBits[bit >> 6] & (1UL << (int)(bit & 63))) != 0)
                            continue;
                        if (RectDistance(s.Tx, s.Ty, ux, y) * s.MetersPerTile > s.Create)
                            continue;
                        int x = ((ux % n) + n) % n;
                        long key = ((long)x << 20) | (uint)y;
                        if (tiles.ContainsKey(key))
                            continue; // made just now for the other focus
                        var t = new Tile { X = x, Y = y, Key = key };
                        t.FineGrid = GridFor(y, FineSpacing, 33);
                        t.CoarseGrid = GridFor(y, CoarseSpacing, 13);
                        tiles[key] = t;
                        Evaluate(t);
                    }
                }
            }
            for (int i = 0; i < foci.Length; i++)
                scanKept[i] = scanFoci[i].Kept;

            ordered.Clear();
            foreach (Tile t in tiles.Values)
                ordered.Add(t);
            int settled = 0;
            foreach (Tile t in ordered)
            {
                if (!t.Keep)
                {
                    Remove(t); // nothing near it any more
                    continue;
                }
                t.WantBuildings = t.InBuildings;
                t.WantLamps = t.InLamps;
                t.WantRoads = t.InRoads;
                t.WantRoads3D = t.InRoads3D;
                t.WantMask = t.InMask;
                t.Band = BandFor(t);
                UpdateShadows(t);
                if (Settled(t))
                    settled++;
            }
            settledCount = settled;
            ordered.RemoveAll(t => t.Removed);
            SortByDistance();
            frontier = 0; // wants and bands changed: everything is looked at again, nearest first
            countCursor = -1;
            scanSignature = signature;
        }

        /// <summary>A tile's distances to the foci and what it is near enough to (built parts
        /// stay a little beyond their radius, so they don't flicker at the edge).</summary>
        void Evaluate(Tile t)
        {
            t.Distance = t.CameraDistance = t.ViewDistance = t.CameraView = double.MaxValue;
            t.Keep = t.InBuildings = t.InLamps = t.InRoads = t.InRoads3D = t.InMask = false;
            for (int i = 0; i < scanFoci.Length; i++)
            {
                ref ScanFocus s = ref scanFoci[i];
                if (!s.Valid)
                    continue;
                double d = TileDistance(t, s.Tx, s.Ty, s.MetersPerTile);
                if (d > s.Keep)
                    continue;
                double view = Math.Sqrt(d * d + s.Height * s.Height);
                t.Distance = Math.Min(t.Distance, d);
                t.ViewDistance = Math.Min(t.ViewDistance, view);
                if (i == 0)
                {
                    t.CameraDistance = Math.Min(t.CameraDistance, d);
                    t.CameraView = Math.Min(t.CameraView, view);
                }
                t.Keep = true;
                s.Kept++;
                t.InBuildings |= s.Building > 0.0 && d <= s.Building + (t.BuildingsBuilt ? KeepMargin : 0.0);
                t.InLamps |= s.Lamp > 0.0 && d <= s.Lamp + (t.LampsBuilt ? KeepMargin : 0.0);
                t.InRoads |= s.Road > 0.0 && d <= s.Road + (t.RoadsBuilt ? KeepMargin : 0.0);
                t.InRoads3D |= s.Road3D > 0.0 && d <= s.Road3D + (t.Roads3DBuilt ? KeepMargin * 0.3 : 0.0);
                t.InMask |= s.Mask > 0.0 && d <= s.Mask + (t.MaskBuilt ? KeepMargin : 0.0);
            }
        }

        /// <summary>A focus's height over the ground (m): seen from high up even the tiles right
        /// below are far, and their detail goes by that straight-line distance.</summary>
        double HeightOverGround(Focus f)
        {
            PQS pqs = body.pqsController;
            if (pqs == null)
                return 0.0;
            try
            {
                double ground = pqs.GetSurfaceHeight(Geo.Direction(f.Lat, f.Lon)) - body.Radius;
                if (body.ocean)
                    ground = Math.Max(ground, 0.0);
                return Math.Max(0.0, f.Alt - ground);
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        /// <summary>Sorts `ordered` by distance in steps of SortStep (counting sort: tens of
        /// thousands of tiles in a fraction of a millisecond; within a step the order doesn't
        /// matter) and numbers the tiles.</summary>
        void SortByDistance()
        {
            int n = ordered.Count;
            if (sortKeys.Length < n)
            {
                sortKeys = new int[n + n / 4];
                sortTiles = new Tile[sortKeys.Length];
            }
            int buckets = 1;
            for (int i = 0; i < n; i++)
            {
                int b = (int)Math.Min(ordered[i].Distance / SortStep, MaxSortBuckets - 1);
                sortKeys[i] = b;
                buckets = Math.Max(buckets, b + 1);
            }
            if (sortCounts.Length < buckets + 1)
                sortCounts = new int[buckets + 1];
            Array.Clear(sortCounts, 0, buckets + 1);
            for (int i = 0; i < n; i++)
                sortCounts[sortKeys[i] + 1]++;
            for (int b = 1; b <= buckets; b++)
                sortCounts[b] += sortCounts[b - 1];
            for (int i = 0; i < n; i++)
                sortTiles[sortCounts[sortKeys[i]]++] = ordered[i];
            for (int i = 0; i < n; i++)
            {
                Tile t = sortTiles[i];
                sortTiles[i] = null;
                ordered[i] = t;
                t.Index = i;
            }
        }

        /// <summary>A tile's detail band by its distance; near the edge of the band it was built
        /// in, it stays in that one (so it isn't meshed again and again).</summary>
        static int BandFor(Tile t)
        {
            double d = t.ViewDistance;
            int band = 0;
            while (band < BandEdges.Length && d > BandEdges[band])
                band++;
            int built = t.BuiltBand;
            if (built >= 0 && band != built)
            {
                double low = built == 0 ? 0.0 : BandEdges[built - 1], high = built < BandEdges.Length ? BandEdges[built] : double.MaxValue;
                if (d >= low * 0.85 && d <= high * 1.15)
                    band = built;
            }
            return band;
        }

        /// <summary>Street lamps' look in a detail band (StartMesh): points of light with pools of
        /// light on the ground, points alone, or only the glow of the lit streets.</summary>
        static int LampLook(int band) => band <= 1 ? 2 : band == 2 ? 1 : 0;

        /// <summary>Something the tile has built looks different in the band it is in now, so it
        /// is meshed again (from the cache). Lamps far off are the streets' glow in every band out
        /// there; built in an unknown band (-1: the relief changed under them), everything is.</summary>
        static bool Redetail(Tile t) =>
            t.Band != t.BuiltBand &&
            (t.WantBuildings && t.BuildingsBuilt || t.WantRoads3D && t.Roads3DBuilt ||
             t.WantLamps && t.LampsBuilt && (t.BuiltBand < 0 || LampLook(t.Band) != LampLook(t.BuiltBand)));

        /// <summary>Smallest building (its width or height, m) and narrowest road kept in a band:
        /// about three and two pixels at the band's near edge (1080 lines, 60 degree view).</summary>
        static double BandMinSize(int band) => band == 0 ? 0.0 : BandEdges[band - 1] * 0.003;

        static double BandMinWidth(int band) => band == 0 ? 0.0 : BandEdges[band - 1] * 0.0022;

        /// <summary>Height grid size for a tile row: samples about `spacing` m apart over the
        /// tile and its margins.</summary>
        int GridFor(int y, double spacing, int max)
        {
            double m = MetersPerTile(OsmTile.LatOf(y + 0.5));
            return Math.Max(3, Math.Min(max, (int)Math.Ceiling(m * 1.3 / spacing) + 1));
        }

        /// <summary>Only buildings near the camera cast shadows: further out a shadow is a few
        /// pixels, while every shadow cascade would draw the whole town once more.</summary>
        void UpdateShadows(Tile t)
        {
            if (t.Renderer == null)
                return;
            ShadowCastingMode mode = t.CameraView <= ShadowRadius ? ShadowCastingMode.On : ShadowCastingMode.Off;
            if (t.Renderer.shadowCastingMode == mode)
                return;
            t.Renderer.shadowCastingMode = mode;
            ShadowTiles += mode == ShadowCastingMode.On ? 1 : -1;
        }

        /// <summary>How fast the camera or the vessel moves over the ground (fast to rise, slow to fall).</summary>
        void UpdateSpeed()
        {
            float now = Time.realtimeSinceStartup;
            float dt = now - lastScan;
            double speed = 0.0;
            for (int i = 0; i < foci.Length; i++)
            {
                if (dt > 0f && dt < 5f && foci[i].Valid && scanned[i].Valid)
                    speed = Math.Max(speed, Focus.Distance(foci[i], scanned[i], body.Radius) / dt);
                scanned[i] = foci[i];
            }
            groundSpeed = Math.Max(speed, groundSpeed * 0.7);
            lastScan = now;
        }

        double MetersPerTile(double lat) =>
            2.0 * Math.PI * body.Radius * Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.01) / (1 << OsmTile.Zoom);

        /// <summary>Distance from a point to tile (x, y)'s square, in tile units.</summary>
        static double RectDistance(double px, double py, int x, int y)
        {
            double dx = Math.Max(0.0, Math.Max(x - px, px - (x + 1)));
            double dy = Math.Max(0.0, Math.Max(y - py, py - (y + 1)));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Metres from a point (tile units tx, ty) to a tile.</summary>
        static double TileDistance(Tile t, double tx, double ty, double metersPerTile)
        {
            int n = 1 << OsmTile.Zoom;
            // The tile's x nearest to the point, across the antimeridian too.
            double x = t.X + Math.Round((tx - t.X - 0.5) / n) * n;
            return RectDistance(tx, ty, (int)x, t.Y) * metersPerTile;
        }

        // ------------------------------------------------------------------ pipeline

        bool VectorUsable => settings.VectorTiles && tileJsonFailures < 3;

        /// <summary>The pipeline's next steps. Work under way is looked at every frame, so a
        /// finished download or job hands its slot on at once. Then the nearest tiles with
        /// something left to do get the workers, downloads and time budgets first (with a big
        /// lamp radius tens of thousands of tiles wait, and far ones mustn't hold up the near
        /// ones); all tiles are also looked at in turn, a slice a frame, for anything else.</summary>
        void Advance(float now)
        {
            heightClock.Reset();
            uploadClock.Reset();
            bool uploaded = false;
            long step = System.Diagnostics.Stopwatch.GetTimestamp();
            PollBusy(now);
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateProbes();
            Timed(ref step, "disk cache look-ups");
            // Data on its way to a mesh first, nearest first: its heights, meshing and upload
            // never wait behind the queue of tiles that have nothing yet.
            PruneStaged();
            stagedVisit.Clear();
            stagedVisit.AddRange(staged);
            stagedVisit.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (Tile t in stagedVisit)
                TimedVisit(t, now, ref uploaded);
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            int count = ordered.Count;
            while (frontier < count && Settled(ordered[frontier]))
                frontier++;
            Timed(ref step, "settled tiles skipped");
            int visits = 0, looked = 0;
            for (int i = frontier; i < count && visits < WorkVisits && looked < NearLooks; i++, looked++)
            {
                Tile t = ordered[i];
                if (Settled(t))
                    continue;
                TimedVisit(t, now, ref uploaded);
                visits++;
            }
            if (visits < WorkVisits && frontier + looked < count)
            {
                if (lookCursor < frontier + looked || lookCursor >= count)
                    lookCursor = frontier + looked;
                for (; lookCursor < count && visits < WorkVisits && looked < WorkLooks; lookCursor++, looked++)
                {
                    Tile t = ordered[lookCursor];
                    if (Settled(t))
                        continue;
                    TimedVisit(t, now, ref uploaded);
                    visits++;
                }
            }
            Timed(ref step, "nearest tiles with something to do");
            if (count == 0)
                return;
            int n = Math.Min(count, PerSlice);
            if (slice >= count)
                slice = 0;
            for (int k = 0; k < n; k++)
                TimedVisit(ordered[(slice + k) % count], now, ref uploaded);
            slice = (slice + n) % count;
        }

        /// <summary>Nothing for Visit to do with the tile, now or later: what it wants is built,
        /// or can't be had (Overpass takes over failed downloads by itself). Mirrors Visit.</summary>
        bool Settled(Tile t)
        {
            if (t.Removed)
                return true;
            if (t.Job != Job.None || t.Overture != null || t.Pending != null || t.ReadyToUpload)
                return false;
            if (t.TerrainStale && (t.Shown == null || t.Band <= 1 || t.Band != t.BuiltBand))
                return false;
            if (t.BuildingsBuilt && !t.WantBuildings || t.LampsBuilt && !t.WantLamps ||
                t.RoadsBuilt && !t.WantRoads || t.Roads3DBuilt && !t.WantRoads3D || t.MaskBuilt && !t.WantMask)
                return false;
            if (t.Shown != null)
            {
                if (t.WantBuildings && !t.BuildingsBuilt || t.WantLamps && !t.LampsBuilt ||
                    t.WantRoads && !t.RoadsBuilt || t.WantRoads3D && !t.Roads3DBuilt || t.WantMask && !t.MaskBuilt)
                    return false;
                if (Redetail(t))
                    return false;
                if (NeedsSupplement(t) && VectorUsable && !t.VectorFailed)
                    return false;
                return !(t.NeedsOverture && t.WantBuildings && OvertureOn && !t.OvertureFailed);
            }
            if (!t.WantBuildings && !t.WantLamps && !t.WantRoads && !t.WantRoads3D && !t.WantMask)
                return true;
            return t.CacheChecked && t.VectorFailed;
        }

        /// <summary>The tile has something to do again: the nearest-first pass reaches it.</summary>
        void Unsettle(Tile t)
        {
            if (t.Index >= 0 && t.Index < frontier)
                frontier = t.Index;
        }

        void MarkBusy(Tile t)
        {
            if (t.Busy)
                return;
            t.Busy = true;
            busy.Add(t);
        }

        /// <summary>Whether the tile's data is on its way to a mesh (holding memory).</summary>
        static bool HoldsData(Tile t) =>
            !t.Removed && (t.Pending != null || t.ReadyToUpload || t.Job == Job.Read || t.Job == Job.Vector || t.Job == Job.VectorParse ||
                           t.Job == Job.SupplementParse || t.Job == Job.OverpassParse || t.Job == Job.OvertureParse || t.Job == Job.Mesh);

        void Stage(Tile t)
        {
            if (t.InStaged)
                return;
            t.InStaged = true;
            staged.Add(t);
        }

        void PruneStaged()
        {
            for (int i = staged.Count - 1; i >= 0; i--)
            {
                Tile t = staged[i];
                if (HoldsData(t))
                    continue;
                t.InStaged = false;
                staged[i] = staged[staged.Count - 1];
                staged.RemoveAt(staged.Count - 1);
            }
        }

        /// <summary>Room for another tile's data on its way to a mesh (MaxStaged).</summary>
        bool CanStage => staged.Count < MaxStaged;

        /// <summary>Downloads, worker jobs and Overture requests under way, every frame.</summary>
        void PollBusy(float now)
        {
            for (int i = 0; i < busy.Count; i++)
            {
                Tile t = busy[i];
                if (!t.Removed)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    Job job = t.Job;
                    if (t.Job != Job.None && t.Job != Job.Overpass)
                        Poll(t, now);
                    if (t.Job == Job.None && t.Overture != null && t.Overture.Done && workers < MaxWorkers)
                        FinishOverture(t, now);
                    if (Perf.Slow(start, out double ms))
                        Perf.Named(ms, $"tile {t.X}/{t.Y}: {job} polled");
                }
                if (t.Removed || (t.Job == Job.None || t.Job == Job.Overpass) && t.Overture == null)
                {
                    t.Busy = false;
                    busy.RemoveAt(i--);
                }
            }
        }

        /// <summary>Looks tiles up in the disk cache, a batch at a time on a worker thread.</summary>
        void UpdateProbes()
        {
            if (probing != null)
            {
                if (!probeDone)
                    return;
                for (int i = 0; i < probing.Length; i++)
                {
                    Tile t = probing[i];
                    if (t.Removed)
                        continue;
                    t.Probe = probeFound[i] ? CacheProbe.Found : CacheProbe.None;
                    Unsettle(t);
                }
                probing = null;
                probeFound = null;
            }
            if (probeQueue.Count == 0)
                return;
            Tile[] batch = probing = probeQueue.ToArray();
            probeQueue.Clear();
            var xs = new int[batch.Length];
            var ys = new int[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                xs[i] = batch[i].X;
                ys[i] = batch[i].Y;
            }
            bool[] found = probeFound = new bool[batch.Length];
            probeDone = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < found.Length; i++)
                {
                    try
                    {
                        found[i] = File.Exists(CachePath(xs[i], ys[i]));
                    }
                    catch (Exception)
                    {
                        found[i] = false;
                    }
                }
                probeDone = true;
            });
        }

        void Visit(Tile t, float now, ref bool uploaded)
        {
            // The terrain changed under it: heights again, and the mesh near the camera (farther
            // out at its next change of detail, which comes on the way there anyway).
            if (t.TerrainStale && t.Job == Job.None && !t.ReadyToUpload)
            {
                if (t.Pending != null)
                {
                    t.TerrainStale = false;
                    t.Grid = null;
                    t.GridNext = 0;
                }
                else if (t.Shown == null)
                {
                    t.TerrainStale = false;
                }
                else if (t.Band <= 1 || t.Band != t.BuiltBand)
                {
                    t.TerrainStale = false;
                    t.Grid = null;
                    t.GridNext = 0;
                    t.BuiltBand = -1; // meshed again from the cache
                }
            }
            // Buildings or 3D roads now wanted where the heights were sampled for lamps only: finer ones.
            if ((t.WantBuildings || t.WantRoads3D) && t.Grid != null && t.Grid.Size < t.FineGrid && t.Job == Job.None && !t.ReadyToUpload)
            {
                t.Grid = null;
                t.GridNext = 0;
            }
            // Lamps near enough now to be points of light where only their glow was (no heights): heights.
            if (t.FlatGrid && t.WantLamps && LampLook(t.Band) > 0 && t.Grid != null && t.Job == Job.None && !t.ReadyToUpload)
            {
                t.Grid = null;
                t.GridNext = 0;
            }
            // Terrain heights, nearest first, for what stands on the ground (roads are drawn
            // into the terrain's own texture), once the tile's data has come and says there
            // is some: sea, forest and fields need none.
            if ((t.WantBuildings || t.WantLamps || t.WantRoads3D) && !t.GridDone && t.Pending != null && heightClock.Elapsed.TotalMilliseconds < HeightMsPerFrame)
            {
                if (t.Grid == null)
                    t.Grid = NewGrid(t, t.Pending);
                SampleHeights(t);
            }

            if (t.Job != Job.None)
            {
                Poll(t, now);
                if (t.Job != Job.None)
                    return;
            }
            if (t.Overture != null && t.Overture.Done && workers < MaxWorkers)
            {
                FinishOverture(t, now);
                return;
            }
            if (t.ReadyToUpload)
            {
                if (!uploaded || uploadClock.Elapsed.TotalMilliseconds < UploadMsPerFrame)
                {
                    Perf.Begin(Perf.Part.Meshes);
                    uploadClock.Start();
                    Upload(t);
                    uploadClock.Stop();
                    Perf.End(Perf.Part.Meshes);
                    uploaded = true;
                }
                return;
            }
            if (t.Pending != null)
            {
                if ((t.GridDone || !t.WantBuildings && !t.WantLamps && !t.WantRoads3D) && workers < MaxWorkers)
                    StartMesh(t);
                return;
            }
            if (t.BuildingsBuilt && !t.WantBuildings)
                DropBuildings(t);
            if (t.LampsBuilt && !t.WantLamps)
                DropLamps(t);
            if (t.RoadsBuilt && !t.WantRoads)
                DropRoads(t);
            if (t.Roads3DBuilt && !t.WantRoads3D)
                DropRoads3D(t);
            if (t.MaskBuilt && !t.WantMask)
                DropMask(t);
            if (t.Shown != null)
            {
                // A part now wanted that the tile wasn't meshed with: again, from the cache.
                bool newPart = t.WantBuildings && !t.BuildingsBuilt && !WaitingForOverture(t, now) || t.WantLamps && !t.LampsBuilt ||
                               t.WantRoads && !t.RoadsBuilt || t.WantRoads3D && !t.Roads3DBuilt || t.WantMask && !t.MaskBuilt;
                if ((newPart || Redetail(t)) && workers < MaxWorkers && CanStage)
                {
                    StartRead(t, newPart ? ReadWhy.Parts : t.BuiltBand < 0 ? ReadWhy.Relief : ReadWhy.Detail);
                }
                else if (NeedsSupplement(t) && VectorUsable && !t.VectorFailed && vectorTemplate != null && vectorRequests < MaxVectorRequests && CanStage)
                {
                    // Once: the vector tile again, for what the cached data lacks and this tile
                    // needs. The rest of it (Overpass detail included) stays as it is.
                    t.LacksRail = t.LacksBridges = t.LacksAreas = false;
                    t.Supplementing = true;
                    StartVector(t);
                }
                else if (t.NeedsOverture && t.WantBuildings && OvertureOn && t.Overture == null && !t.OvertureFailed &&
                         now >= t.OvertureRetryAt && overture.Pending < 3 * overture.MaxRequests)
                {
                    t.Overture = overture.Want(t.X, t.Y);
                    t.OvertureUntil = Math.Max(t.OvertureUntil, now + OvertureWait);
                    MarkBusy(t);
                }
                return; // Overpass detail, if any, is picked up by StartOverpass
            }
            if (!t.WantBuildings && !t.WantLamps && !t.WantRoads && !t.WantRoads3D && !t.WantMask)
                return;

            if (!t.CacheChecked)
            {
                // The disk cache first: looked up in batches on a worker thread (UpdateProbes),
                // nearest tiles first, then read by a worker.
                switch (t.Probe)
                {
                    case CacheProbe.Unknown:
                        if (probeQueue.Count < ProbeBatch)
                        {
                            t.Probe = CacheProbe.Pending;
                            probeQueue.Add(t);
                        }
                        return;
                    case CacheProbe.Found:
                        if (workers < MaxWorkers && CanStage)
                            StartRead(t, ReadWhy.New);
                        return;
                    case CacheProbe.None:
                        t.CacheChecked = true;
                        break;
                    default:
                        return; // being looked up
                }
            }
            if (!VectorUsable)
                t.VectorFailed = true; // Overpass takes over (StartOverpass)
            else if (!t.VectorFailed && vectorTemplate != null && vectorRequests < MaxVectorRequests && now >= t.VectorRetryAt && CanStage)
                StartVector(t);
        }

        void Poll(Tile t, float now)
        {
            switch (t.Job)
            {
                case Job.Vector:
                    if (t.Request.isDone)
                        FinishVector(t, now);
                    return;
                case Job.Overpass:
                    return; // PollOverpass
            }
            if (!t.WorkDone)
                return;
            workers--;
            t.WorkDone = false;
            Job job = t.Job;
            t.Job = Job.None;
            Unsettle(t);
            OsmTile data = t.WorkData;
            t.WorkData = null;
            switch (job)
            {
                case Job.Read:
                    t.CacheChecked = true;
                    if (data != null)
                    {
                        Accept(t, data, false);
                        break;
                    }
                    if (t.Error != null)
                        Debug.LogWarning($"[SolSatellite] OSM tile {t.X}/{t.Y}: {t.Error}");
                    t.Shown = null; // cache unusable: fetch again
                    break;
                case Job.SupplementParse:
                    if (data != null)
                    {
                        Accept(t, data);
                        break;
                    }
                    Debug.LogWarning($"[SolSatellite] Adding railways to OSM tile {t.X}/{t.Y}: {t.Error}");
                    break;
                case Job.VectorParse:
                    if (data != null)
                    {
                        Accept(t, data);
                        break;
                    }
                    Debug.LogWarning($"[SolSatellite] Vector tile {t.X}/{t.Y}: {t.Error}");
                    t.VectorFailed = true;
                    break;
                case Job.OvertureParse:
                    if (data != null)
                    {
                        Accept(t, data);
                        break;
                    }
                    Debug.LogWarning($"[SolSatellite] Overture buildings for tile {t.X}/{t.Y}: {t.Error}");
                    t.OvertureFailed = true;
                    break;
                case Job.OverpassParse:
                    if (data != null)
                    {
                        Accept(t, data);
                        overpassStrikes = 0;
                        break;
                    }
                    Debug.LogWarning($"[SolSatellite] Overpass tile {t.X}/{t.Y}: {t.Error}");
                    // Overpass reports running out of time or memory in the reply: ask again, for more.
                    string error = t.Error ?? "";
                    if (error.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                        t.Heavy = true;
                    OverpassRetry(t, now, 30f);
                    break;
                case Job.Mesh:
                    if (t.Error != null)
                    {
                        Debug.LogError($"[SolSatellite] Meshing OSM tile {t.X}/{t.Y} failed: {t.Error}");
                        RecycleWork(t);
                    }
                    else
                    {
                        meshesDone++;
                        t.ReadyMesh = t.WorkMesh;
                        t.ReadyLamps = t.WorkLamps;
                        t.ReadyRoads = t.WorkRoads;
                        t.ReadyRoads3D = t.WorkRoads3D;
                        t.ReadyMask = t.WorkMask;
                        t.ReadyToUpload = true;
                    }
                    t.WorkMesh = null;
                    t.WorkLamps = null;
                    t.WorkRoads = null;
                    t.WorkRoads3D = null;
                    t.WorkMask = null;
                    break;
            }
        }

        /// <summary>Data for a tile: build it, and decide whether Overpass should add detail.
        /// fresh: new data (not read again from the cache), what is built from older data goes.</summary>
        void Accept(Tile t, OsmTile data, bool fresh = true)
        {
            if (fresh)
                t.Stamp++;
            t.Pending = data;
            t.Shown = data.Source;
            t.WantDetail = data.Source == OsmSource.OpenFreeMap && data.Buildings.Count > 0;
            t.LacksRail = !data.HasRail;
            t.LacksAreas = !data.HasAreas;
            t.LacksBridges = !data.HasBridges;
            t.HasOverture = data.HasOverture;
            if (!t.HasOverture && !t.NeedsOverture)
                t.OvertureUntil = Time.realtimeSinceStartup + OvertureWait; // new data: its buildings wait a moment
            t.NeedsOverture = !data.HasOverture;
        }

        /// <summary>Overture's answer for a tile: merged into its cache (none: OSM's buildings
        /// stay), or tried again later.</summary>
        void FinishOverture(Tile t, float now)
        {
            OvertureRequest r = t.Overture;
            t.Overture = null;
            if (r.Data != null || r.Missing)
            {
                StartParse(t, Job.OvertureParse, r.Data);
                return;
            }
            if (++t.OvertureAttempts >= MaxOvertureAttempts)
                t.OvertureFailed = true; // keeps OSM's buildings this session
            else
                t.OvertureRetryAt = now + 10f * t.OvertureAttempts;
        }

        /// <summary>Old cached data lacks something this tile shows: railways on its road layer,
        /// bridges under its 3D roads (far off, none are needed).</summary>
        static bool NeedsSupplement(Tile t) => t.LacksRail && t.WantRoads || t.LacksBridges && t.WantRoads3D || t.LacksAreas && t.WantMask;

        string CachePath(Tile t) => CachePath(t.X, t.Y);

        // Not ".bin": Kopernicus opens every .bin file under GameData at startup.
        string CachePath(int x, int y) => Path.Combine(cacheDir, OsmTile.Zoom.ToString(), x.ToString(), y + ".sosm");

        void StartWorker(Tile t, Job job, Action work)
        {
            workers++;
            t.Job = job;
            t.Error = null;
            t.WorkDone = false;
            MarkBusy(t);
            Stage(t);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread thread = Thread.CurrentThread;
                System.Threading.ThreadPriority priority = thread.Priority;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    thread.Priority = System.Threading.ThreadPriority.BelowNormal;
                    work();
                }
                catch (Exception e)
                {
                    t.Error = job == Job.Mesh ? e.ToString() : e.Message;
                }
                finally
                {
                    try
                    {
                        thread.Priority = priority;
                    }
                    catch (Exception)
                    {
                    }
                }
                Interlocked.Add(ref jobTicks[(int)job], System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref jobRuns[(int)job]);
                t.WorkDone = true;
            });
        }

        void StartRead(Tile t, ReadWhy why)
        {
            readsBy[(int)why]++;
            string path = CachePath(t);
            StartWorker(t, Job.Read, () =>
            {
                try
                {
                    t.WorkData = OsmTile.Read(path); // null for a format we no longer read
                    DiskCache.Touch(path);
                }
                catch (FileNotFoundException)
                {
                    throw;
                }
                catch (Exception)
                {
                    File.Delete(path); // damaged: fetch again
                    throw;
                }
            });
        }

        void StartParse(Tile t, Job job, byte[] reply)
        {
            int x = t.X, y = t.Y;
            string path = CachePath(t);
            StartWorker(t, job, () =>
            {
                OsmTile cached = job != Job.VectorParse && File.Exists(path) ? OsmTile.Read(path) : null;
                OsmTile data;
                if (job == Job.OvertureParse)
                {
                    // Overture's buildings in place of OSM's; no tile there: OSM's stay.
                    data = cached ?? throw new InvalidDataException("no OSM data cached for the tile");
                    if (reply != null)
                    {
                        OsmTile fresh = Mvt.ParseOverture(x, y, reply);
                        OsmBuilding.Return(data.Buildings); // OSM's, read from the cache: done with
                        data.Buildings.AddRange(fresh.Buildings);
                    }
                    data.HasOverture = true;
                }
                else
                {
                    data = job == Job.OverpassParse ? OsmTile.Parse(x, y, reply) : Mvt.Parse(x, y, reply);
                    if (job == Job.SupplementParse && cached != null)
                    {
                        data = Supplement(cached, data);
                    }
                    else if (cached != null && cached.HasOverture)
                    {
                        // Fresh roads and lamps; the buildings stay Overture's.
                        OsmBuilding.Return(data.Buildings); // the vector tile's: done with
                        data.Buildings.AddRange(cached.Buildings);
                        data.HasOverture = true;
                    }
                    if (job != Job.SupplementParse)
                        KeepAreas(data, cached); // Overpass has none: the vector tile's stay
                }
                data.Write(path);
                t.WorkData = data;
            });
        }

        /// <summary>Adds to cached data what a fresh vector tile has and it lacks: railways,
        /// bridges. Buildings, roads and lamps stay as they were.</summary>
        static OsmTile Supplement(OsmTile cached, OsmTile fresh)
        {
            if (cached == fresh)
                return fresh;
            if (!cached.HasRail)
            {
                foreach (OsmRoad r in fresh.RoadList)
                    if (OsmTile.IsRailway(r.Kind))
                        cached.RoadList.Add(r);
                cached.HasRail = true;
            }
            if (!cached.HasBridges)
            {
                cached.Bridges.AddRange(fresh.Bridges);
                cached.HasBridges = true;
            }
            KeepAreas(cached, fresh);
            return cached;
        }

        /// <summary>Areas and runway lines (vector tiles only) carried over into data that lacks them.</summary>
        static void KeepAreas(OsmTile into, OsmTile from)
        {
            if (into.HasAreas || from == null || !from.HasAreas)
                return;
            into.Areas.AddRange(from.Areas);
            into.AerowayLines.AddRange(from.AerowayLines);
            into.HasAreas = true;
        }

        void StartMesh(Tile t)
        {
            OsmTile data = t.Pending;
            t.Pending = null;
            HeightGrid grid = t.Grid;
            List<BuildingMesher.Exclusion> excluded = exclusions;
            // Buildings need the fine height grid; with a coarse one (sampled for lamps) they wait.
            // Nothing on the ground in the data (sea, desert, fields): built as none on the flat
            // stand-in grid it has. Before 2026-10-03 such tiles were read from the cache again
            // and again for buildings that never came (around KSC 1500 reads a second, each
            // redoing the tile's scatter mask, so Parallax placed its scatters there anew).
            bool fine = Bare(data) || grid != null && grid.Size >= t.FineGrid;
            bool buildings = t.MeshingBuildings = t.WantBuildings && fine && !WaitingForOverture(t, Time.realtimeSinceStartup);
            bool guessRoofs = settings.GuessRoofs;
            // Meshed at real size (around a real-size planet), the tile's object scaled to fit.
            double scale = worldScale;
            int band = t.MeshingBand = t.Band;
            double minSize = BandMinSize(band) / scale, minWidth = BandMinWidth(band) / scale;
            bool eaves = band == 0, pitched = band <= 1, boxes = band >= 1, sprites = band <= 2, pools = band <= 1;
            // Meshed again from the same data (read again from the cache for another detail band,
            // a new part or the relief): what doesn't change stays as it is - roads and the scatter
            // mask (flat, from the data alone; a new mask has Parallax place its scatters around
            // anew), lamps unless they look different in the new band or the relief changed.
            // Before 2026-10-03 every change of band redid them all: flying over a city, dozens of
            // tiles a second.
            int stamp = t.MeshingStamp = t.Stamp;
            t.KeepRoads = t.WantRoads && t.RoadsBuilt && t.RoadsStamp == stamp;
            t.KeepMask = t.WantMask && t.MaskBuilt && t.MaskStamp == stamp;
            t.KeepLamps = t.WantLamps && t.LampsBuilt && t.LampsStamp == stamp && t.BuiltBand >= 0 && LampLook(t.BuiltBand) == LampLook(band);
            partsKept += (t.KeepRoads ? 1 : 0) + (t.KeepMask ? 1 : 0) + (t.KeepLamps ? 1 : 0);
            bool lamps = t.MeshingLamps = t.WantLamps && !t.KeepLamps;
            bool roads = t.MeshingRoads = t.WantRoads && !t.KeepRoads;
            bool roads3D = t.MeshingRoads3D = t.WantRoads3D && fine;
            bool mask = t.MeshingMask = t.WantMask && !t.KeepMask;
            StartWorker(t, Job.Mesh, () =>
            {
                HeightGrid real = grid == null || scale == 1.0 ? grid : grid.Scaled(1.0 / scale);
                t.WorkMesh = buildings ? BuildingMesher.Build(data, real, excluded, guessRoofs, minSize, eaves, pitched, boxes) : null;
                t.WorkLamps = lamps ? LampMesher.Build(data, real, excluded, sprites, pools) : null;
                t.WorkRoads = roads ? RoadMesher.Build(data) : null;
                t.WorkRoads3D = roads3D ? Road3DMesher.Build(data, real, minWidth) : null;
                t.WorkMask = mask ? MaskMesher.Build(data) : null;
                OsmBuilding.Return(data.Buildings); // meshed: nothing refers to them any more
            });
        }

        /// <summary>Data with nothing that stands on the ground or needs its heights.</summary>
        static bool Bare(OsmTile data) =>
            data.Buildings.Count == 0 && data.RoadList.Count == 0 && data.Lamps.Count == 0 && data.Bridges.Count == 0;

        /// <summary>The height grid a tile's data needs: fine under buildings, coarse under
        /// lamps alone (seen from afar at night), none to speak of with nothing on the ground or
        /// with lamps alone so far off that they are only the glow of the lit streets (that lies
        /// flat in the light map; each height is a PQS look-up on the main thread).</summary>
        HeightGrid NewGrid(Tile t, OsmTile data)
        {
            double south = OsmTile.LatOf(t.Y + 1), north = OsmTile.LatOf(t.Y);
            double west = OsmTile.LonOf(t.X), east = OsmTile.LonOf(t.X + 1);
            // A margin for buildings that stick out of the tile they belong to.
            double mLat = (north - south) * 0.15, mLon = (east - west) * 0.15;
            bool empty = Bare(data);
            bool flat = empty || !t.WantBuildings && !t.WantRoads3D && LampLook(t.Band) == 0;
            t.FlatGrid = flat && !empty;
            int size = flat ? 2 : t.WantBuildings || t.WantRoads3D ? t.FineGrid : t.CoarseGrid;
            var grid = new HeightGrid(size, south - mLat, west - mLon, north + mLat, east + mLon);
            if (flat)
            {
                for (int i = 0; i < grid.Radius.Length; i++)
                    grid.Radius[i] = body.Radius;
                t.GridNext = grid.Radius.Length;
            }
            return grid;
        }

        /// <summary>Samples terrain heights for a tile until this frame's time budget is used up.</summary>
        void SampleHeights(Tile t)
        {
            PQS pqs = body.pqsController;
            double[] radius = t.Grid.Radius;
            Perf.Begin(Perf.Part.Heights);
            heightClock.Start();
            while (t.GridNext < radius.Length && heightClock.Elapsed.TotalMilliseconds < HeightMsPerFrame)
            {
                for (int i = 0; i < 16 && t.GridNext < radius.Length; i++)
                {
                    t.Grid.Point(t.GridNext, out double lat, out double lon);
                    double r = pqs.GetSurfaceHeight(Geo.Direction(lat, lon));
                    if (body.ocean)
                        r = Math.Max(r, body.Radius); // coastal buildings stand on the sea, not under it
                    radius[t.GridNext++] = r;
                }
            }
            heightClock.Stop();
            Perf.End(Perf.Part.Heights);
        }

        // ------------------------------------------------------------------ objects

        void Upload(Tile t)
        {
            BuildingMeshData md = t.ReadyMesh;
            LampMeshData ld = t.ReadyLamps;
            RibbonMeshData rd = t.ReadyRoads;
            Road3DMeshData r3 = t.ReadyRoads3D;
            RibbonMeshData rm = t.ReadyMask;
            t.ReadyMesh = null;
            t.ReadyLamps = null;
            t.ReadyRoads = null;
            t.ReadyRoads3D = null;
            t.ReadyMask = null;
            t.ReadyToUpload = false;
            uploadsDone++;
            long part = System.Diagnostics.Stopwatch.GetTimestamp();
            if (t.MeshingMask)
            {
                ReplaceMask(t, rm);
                t.MaskStamp = t.MeshingStamp;
            }
            else if (!t.KeepMask)
            {
                DropMask(t);
            }
            UploadPart(ref part, "scatter mask");
            if (t.MeshingRoads)
            {
                ReplaceRoads(t, rd);
                t.RoadsStamp = t.MeshingStamp;
            }
            else if (!t.KeepRoads)
            {
                DropRoads(t);
            }
            UploadPart(ref part, "roads");

            bool hasBuildings = md != null && md.Buildings > 0, hasLamps = ld != null && ld.Lamps > 0;
            bool has3D = r3 != null && r3.Ground.Count + r3.Structures.Count > 0;
            if (t.Root == null && (hasBuildings || hasLamps || has3D))
            {
                var frame = new TangentPlane(OsmTile.LatOf(t.Y + 0.5), OsmTile.LonOf(t.X + 0.5), body.Radius);
                t.Root = new GameObject($"SolSatellite tile {t.X}/{t.Y}") { layer = 15 };
                t.Root.transform.SetParent(body.pqsController.transform, false);
                Vector3 origin = hasBuildings ? md.Origin : hasLamps ? ld.Origin : r3.Origin; // of the real-size frame
                t.Root.transform.localPosition = origin * (float)worldScale;
                t.Root.transform.localRotation = Quaternion.LookRotation((Vector3)frame.North, (Vector3)frame.Up);
                t.Root.transform.localScale = Vector3.one * (float)worldScale;
            }

            if (t.MeshingBuildings || t.MeshingRoads3D || t.MeshingLamps || t.KeepLamps)
                t.BuiltBand = t.MeshingBand;
            UploadPart(ref part, "tile object");
            if (t.MeshingBuildings)
                ReplaceBuildings(t, md);
            else
                DropBuildings(t);
            UploadPart(ref part, "buildings");
            if (t.MeshingLamps)
            {
                ReplaceLamps(t, ld);
                t.LampsStamp = t.MeshingStamp;
            }
            else if (!t.KeepLamps)
            {
                DropLamps(t);
            }
            UploadPart(ref part, "lamps");
            roads3DNote = null;
            if (t.MeshingRoads3D)
                ReplaceRoads3D(t, r3);
            else
                DropRoads3D(t);
            UploadPart(ref part, roads3DNote ?? "3D roads");
            // Unity has its copies now: the lists go back to the pools for the next tile.
            BuildingMeshData.Recycle(md);
            LampMeshData.Recycle(ld);
            RibbonMeshData.Recycle(rd);
            RibbonMeshData.Recycle(rm);
            Road3DMeshData.Recycle(r3);
            UploadPart(ref part, "mesh data back to the pools");
        }

        /// <summary>Mesh data a worker made that won't be uploaded, back to the pools.</summary>
        static void RecycleWork(Tile t)
        {
            BuildingMeshData.Recycle(t.WorkMesh);
            LampMeshData.Recycle(t.WorkLamps);
            RibbonMeshData.Recycle(t.WorkRoads);
            RibbonMeshData.Recycle(t.WorkMask);
            Road3DMeshData.Recycle(t.WorkRoads3D);
            t.WorkMesh = null;
            t.WorkLamps = null;
            t.WorkRoads = null;
            t.WorkMask = null;
            t.WorkRoads3D = null;
        }

        /// <summary>The tile's part of the scatter mask; Parallax's scatters around it are placed again (ScatterMask).</summary>
        void ReplaceMask(Tile t, RibbonMeshData rm)
        {
            DropMask(t);
            t.MaskBuilt = true;
            if (rm == null || rm.Count == 0)
                return;
            t.Mask = new GroundMesh(rm, TileFrame.Flat(t.X, t.Y), $"SolSatellite scatter mask {t.X}/{t.Y}");
            maskLayer.Add(t.Mask);
            ScatterMask.Changed(t.X, t.Y);
        }

        /// <summary>What it drew stays in the mask layer until that is drawn anew: it is still right.</summary>
        void DropMask(Tile t)
        {
            if (t.Mask != null)
            {
                maskLayer.Remove(t.Mask, false);
                t.Mask.Destroy();
            }
            t.Mask = null;
            t.MaskBuilt = false;
        }

        void ReplaceRoads3D(Tile t, Road3DMeshData r3)
        {
            DropRoads3D(t);
            t.Roads3DBuilt = true;
            if (r3 == null || r3.Ground.Count + r3.Structures.Count == 0 || t.Root == null)
                return;
            // Timed in parts (2026-10-03: a band 0 tile of Bangkok took 35-54 ms here): the mesh,
            // handing it to the graphics card, the objects.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), mesh = 0, gpu = 0;
            if (r3.Ground.Count > 0)
            {
                t.Road3DMesh = Road3DMesh(r3.Ground, $"SolSatellite 3D roads {t.X}/{t.Y}");
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                t.Road3DMesh.UploadMeshData(true);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                mesh += t1 - t0;
                gpu += t2 - t1;
                t.Road3DObject = Child(t, "3D roads", t.Road3DMesh, road3DMaterial);
                var renderer = t.Road3DObject.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
            }
            if (r3.Structures.Count > 0)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                t.BridgeMesh = Road3DMesh(r3.Structures, $"SolSatellite bridges {t.X}/{t.Y}");
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                t.BridgeMesh.UploadMeshData(true);
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                mesh += t2 - t1;
                gpu += t3 - t2;
                t.BridgeObject = Child(t, "bridges", t.BridgeMesh, road3DMaterial);
                var renderer = t.BridgeObject.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double total = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs;
            if (total >= 4.0)
                roads3DNote = $"3D roads ({r3.Ground.Count + r3.Structures.Count} vertices: mesh {mesh * toMs:0} ms, " +
                              $"to the graphics card {gpu * toMs:0} ms, objects {total - (mesh + gpu) * toMs:0} ms)";
            t.Road3DVertices = r3.Ground.Count + r3.Structures.Count;
            Road3DVertices += t.Road3DVertices;
            Road3DTiles++;
        }

        static Mesh Road3DMesh(Road3DMeshData.Part p, string name)
        {
            // The shader moves vertices onto the terrain and lifts bridges: room for that.
            var bounds = new Bounds();
            bounds.SetMinMax(p.Min, p.Max);
            bounds.Expand(60f);
            return MeshUpload.Interleaved(name, p.Vertices, Road3DVertex.Layout, p.Triangles, bounds); // then UploadMeshData(true)
        }

        void DropRoads3D(Tile t)
        {
            DestroyAll(t.Road3DObject, t.BridgeObject, t.Road3DMesh, t.BridgeMesh);
            if (t.Road3DVertices > 0)
            {
                Road3DVertices -= t.Road3DVertices;
                Road3DTiles--;
            }
            t.Road3DObject = t.BridgeObject = null;
            t.Road3DMesh = t.BridgeMesh = null;
            t.Road3DVertices = 0;
            t.Roads3DBuilt = false;
        }

        static void DestroyAll(UnityEngine.Object a, UnityEngine.Object b, UnityEngine.Object c, UnityEngine.Object d)
        {
            if (a != null)
                UnityEngine.Object.Destroy(a);
            if (b != null)
                UnityEngine.Object.Destroy(b);
            if (c != null)
                UnityEngine.Object.Destroy(c);
            if (d != null)
                UnityEngine.Object.Destroy(d);
        }

        GameObject Child(Tile t, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name) { layer = 15 };
            go.transform.SetParent(t.Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            return go;
        }

        void ReplaceBuildings(Tile t, BuildingMeshData md)
        {
            // The build being replaced may still be baking its collider (a job reading its
            // mesh): not waited for here - that took a frame 30-40 ms near the vessel when the
            // detail band changed (2026-10-03) - it goes once the bake is done (Retire).
            bool stillBaking = t.Baking && !t.Bake.IsCompleted;
            if (t.Baking && !stillBaking)
                t.Bake.Complete();
            if (t.Object != null || stillBaking)
            {
                if (t.HasCollider && md.Buildings > 0 && t.OldObject == null)
                {
                    // An older build: keep its collider until the new one is baked.
                    t.OldObject = t.Object;
                    t.OldMesh = t.Mesh;
                    t.OldObject.GetComponent<MeshRenderer>().enabled = false;
                }
                else if (stillBaking)
                {
                    Retire(t.Bake, t.Object, t.Mesh);
                }
                else
                {
                    UnityEngine.Object.Destroy(t.Object);
                    UnityEngine.Object.Destroy(t.Mesh);
                }
                if (t.Object != null)
                    Uncount(t);
                t.Object = null;
                t.Mesh = null;
                t.HasCollider = false;
            }
            t.Baking = false;
            t.BuildingsBuilt = true;
            if (md.Buildings == 0)
            {
                TrackColliders(t);
                return;
            }

            Mesh mesh = MeshUpload.Interleaved($"SolSatellite buildings {t.X}/{t.Y}", md.Vertices, BuildingVertex.Layout, md.Triangles, md.Bounds);
            t.Object = Child(t, "buildings", mesh, material);
            t.Renderer = t.Object.GetComponent<MeshRenderer>();
            t.Renderer.shadowCastingMode = ShadowCastingMode.Off;
            t.Renderer.receiveShadows = true;
            UpdateShadows(t);
            t.Mesh = mesh;
            t.Buildings = md.Buildings;
            t.Vertices = md.Vertices.Count;
            t.Pitched = md.Pitched;
            PitchedCount += t.Pitched;
            BuildingCount += t.Buildings;
            BuildingVertices += t.Vertices;
            BuildingTiles++;
            TrackColliders(t);
        }

        /// <summary>Keeps withMesh, the tiles UpdateColliders looks at, in step with the tile.</summary>
        void TrackColliders(Tile t)
        {
            if (!t.Removed && (!ReferenceEquals(t.Mesh, null) || !ReferenceEquals(t.OldObject, null) || t.Baking || t.HasCollider))
                withMesh.Add(t);
            else
                withMesh.Remove(t);
        }

        /// <summary>Takes a tile's buildings out of the totals.</summary>
        void Uncount(Tile t)
        {
            if (t.Renderer != null && t.Renderer.shadowCastingMode == ShadowCastingMode.On)
                ShadowTiles--;
            if (t.Buildings > 0)
                BuildingTiles--;
            BuildingCount -= t.Buildings;
            BuildingVertices -= t.Vertices;
            PitchedCount -= t.Pitched;
            t.Buildings = t.Vertices = t.Pitched = 0;
            t.Renderer = null;
        }

        void ReplaceLamps(Tile t, LampMeshData ld)
        {
            DropLamps(t);
            t.LampsBuilt = true;
            if (ld == null || ld.Lamps == 0 && ld.Lights.Count == 0 && ld.Glow.Count == 0)
                return;
            t.Lights = ld.Lights.Count > 0 ? new List<GroundLight>(ld.Lights) : null; // DrawLights puts them into the terrain's light map
            if (ld.Lamps > 0 && t.Root != null)
            {
                Mesh mesh = MeshUpload.Streams($"SolSatellite lamps {t.X}/{t.Y}", ld.Vertices, ld.Colors, ld.Corners, null, ld.Triangles, ld.Bounds);
                mesh.UploadMeshData(true); // only drawn: no copy in main memory
                t.LampObject = Child(t, "lamps", mesh, lampMaterial);
                var renderer = t.LampObject.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = lightsOn;
                t.LampMesh = mesh;
                t.Lamps = ld.Lamps;
                t.LampVertices = ld.Vertices.Count;
                LampCount += t.Lamps;
                LampVertices += t.LampVertices;
                LampTiles++;
            }
            if (ld.Lights.Count > 0)
                poolsToMesh.Add(t);
            if (ld.Glow.Count > 0)
            {
                t.GlowMesh = RoadLayer.ToMesh(ld.Glow, $"SolSatellite lit streets {t.X}/{t.Y}");
                // Only the extent: the ribbons' lists, kept for tens of thousands of tiles, were
                // gigabytes of managed memory.
                t.HasGlowExtent = true;
                t.GlowMinX = ld.Glow.MinX;
                t.GlowMinY = ld.Glow.MinY;
                t.GlowMaxX = ld.Glow.MaxX;
                t.GlowMaxY = ld.Glow.MaxY;
                t.ExtentsStale = true;
                MarkLightNew(t); // DrawLights adds it to the light map
                UpdateLit(t);
            }
            t.Pools = ld.Lights.Count;
            PoolCount += t.Pools;
        }

        void DropBuildings(Tile t)
        {
            if (t.Object != null)
                Uncount(t);
            if (t.Baking && !t.Bake.IsCompleted)
            {
                // Still baking its collider: goes when that's done (ReplaceBuildings).
                Retire(t.Bake, t.Object, t.Mesh);
                t.Object = null;
                t.Mesh = null;
            }
            else if (t.Baking)
            {
                t.Bake.Complete();
            }
            t.Baking = false;
            DestroyAll(t.Object, t.Mesh, t.OldObject, t.OldMesh);
            t.Object = t.OldObject = null;
            t.Mesh = t.OldMesh = null;
            t.HasCollider = false;
            t.BuildingsBuilt = false;
            withMesh.Remove(t);
        }

        void DropLamps(Tile t)
        {
            if (t.LampObject != null)
            {
                LampCount -= t.Lamps;
                LampVertices -= t.LampVertices;
                LampTiles--;
                UnityEngine.Object.Destroy(t.LampObject);
            }
            PoolCount -= t.Pools;
            if (t.LampMesh != null)
                UnityEngine.Object.Destroy(t.LampMesh);
            // What it put into the light map, DrawLights takes back out.
            if (t.LightMesh != null)
                erase.Add(new Erase
                {
                    Mesh = t.LightMesh, Matrix = Matrix4x4.identity, Pass = GroundLayer.PoolErase,
                    X0 = t.LightX0, Y0 = t.LightY0, X1 = t.LightX1, Y1 = t.LightY1,
                });
            if (t.GlowMesh != null)
                erase.Add(new Erase
                {
                    Mesh = t.GlowMesh, Matrix = t.Frame, Pass = GroundLayer.GlowErase, Stale = t.FramePlane != lightPlane,
                    X0 = t.GlowX0, Y0 = t.GlowY0, X1 = t.GlowX1, Y1 = t.GlowY1,
                });
            t.LampObject = null;
            t.LampMesh = null;
            t.LightMesh = null;
            t.GlowMesh = null;
            t.HasGlowExtent = false;
            t.Lights = null;
            t.Lamps = t.LampVertices = t.Pools = 0;
            t.LampsBuilt = false;
            lit.Remove(t);
        }

        /// <summary>Keeps `lit` (LitTiles) in step with the tile's light meshes.</summary>
        void UpdateLit(Tile t)
        {
            if (!t.Removed && HasLight(t))
                lit.Add(t);
            else
                lit.Remove(t);
        }

        void ReplaceRoads(Tile t, RibbonMeshData rd)
        {
            DropRoads(t);
            t.RoadsBuilt = true;
            if (rd == null || rd.Count == 0)
                return;
            t.Road = new GroundMesh(rd, TileFrame.Flat(t.X, t.Y), $"SolSatellite roads {t.X}/{t.Y}");
            roads.Add(t.Road);
            RoadVertices += t.Road.Vertices;
            RoadTiles++;
        }

        /// <summary>What the tile's roads drew stays in the road layer - they are still right
        /// there - unless erase (the tile is meshed anew without some) or roads are switched off.</summary>
        void DropRoads(Tile t, bool erase = false)
        {
            if (t.Road != null)
            {
                roads.Remove(t.Road, erase || !settings.RoadsEnabled);
                t.Road.Destroy();
                RoadVertices -= t.Road.Vertices;
                RoadTiles--;
            }
            t.Road = null;
            t.RoadsBuilt = false;
        }

        void MarkLightNew(Tile t)
        {
            if (!t.LightNew)
                lightsNew.Add(t);
            t.LightNew = true;
        }

        /// <summary>Where the tile's flat frame lies on the clipmap's plane (RoadLayer.PlaneMatrix),
        /// and the extent of its lit streets there, grown by the most the shader moves a vertex.</summary>
        void EnsureFrame(Tile t, Clipmap clipmap)
        {
            if (t.FramePlane == clipmap.PlaneVersion)
            {
                if (t.ExtentsStale)
                    Extents(t, clipmap);
                return;
            }
            t.FramePlane = clipmap.PlaneVersion;
            t.Frame = RoadLayer.PlaneMatrix(TileFrame.Flat(t.X, t.Y), clipmap.Plane);
            Extents(t, clipmap);
        }

        static void Extents(Tile t, Clipmap clipmap)
        {
            t.ExtentsStale = false;
            if (t.HasGlowExtent) // ribbon widths are in the extent; each level adds its own texels (GroundLayer.Pad)
                RoadLayer.Extent(t.Frame, t.GlowMinX, t.GlowMinY, t.GlowMaxX, t.GlowMaxY, 0.0,
                                 out t.GlowX0, out t.GlowY0, out t.GlowX1, out t.GlowY1);
        }

        // ------------------------------------------------------------------ night light map

        /// <summary>Pools of light that went away (tile dropped or rebuilt): still in the light
        /// map until DrawLights takes them back out.</summary>
        struct Erase
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public int Pass;
            public bool Stale; // drawn for an older plane: nothing to take out
            public double X0, Y0, X1, Y1;
        }

        /// <summary>Keeps the clipmap's night light map in step with the lamps, touching only
        /// what changed: new lamp tiles are added to every level they reach, dropped ones are
        /// subtracted again, and a level whose window moved gets only its new strip of ground
        /// drawn. A level is drawn whole only when it is new, reset, moved too far or back in
        /// use. Call after the clipmap's Update, outside rendering.</summary>
        public void DrawLights(Clipmap clipmap)
        {
            if (clipmap == null || !clipmap.Active || !LampsOn)
            {
                if (clipmap != null && clipmap.Active && lightsInMap)
                {
                    for (int k = 0; k < Clipmap.MaxLevels; k++)
                        if (clipmap.LevelReady(k))
                            clipmap.Lights.ClearLevel(k);
                    clipmap.Lights.End();
                    lightsInMap = false;
                }
                ForgetLights(); // drawn whole once lamps and levels are back
                return;
            }
            GroundLayer layer = clipmap.Lights;

            if (clipmap != lightClipmap || clipmap.PlaneVersion != lightPlane)
            {
                // New plane: every mesh's coordinates change.
                lightClipmap = clipmap;
                lightPlane = clipmap.PlaneVersion;
                poolsToMesh.Clear();
                foreach (Tile t in tiles.Values)
                {
                    if (t.LightMesh != null)
                        UnityEngine.Object.Destroy(t.LightMesh);
                    t.LightMesh = null;
                    UpdateLit(t);
                    if (t.Lights != null && t.Lights.Count > 0)
                        poolsToMesh.Add(t);
                    if (t.GlowMesh != null)
                        EnsureFrame(t, clipmap);
                }
                ForgetLights();
            }
            foreach (Tile t in poolsToMesh)
            {
                if (t.Removed || t.Lights == null || t.Lights.Count == 0 || t.LightMesh != null)
                    continue;
                BuildLightMesh(t, clipmap.Plane);
                MarkLightNew(t);
            }
            poolsToMesh.Clear();
            if (!lightsOn)
            {
                // By day the light map isn't shown: nothing is drawn into it (it was a big part of
                // the mod's time with lamps far out); night draws it whole, a level or so a frame.
                ForgetLights();
                return;
            }
            if (erase.Count > EraseLimit)
                ForgetLights(); // many tiles gone at once (cleared, rebuilt): drawing anew is cheaper
            foreach (Tile t in lightsNew)
                if (t.GlowMesh != null)
                    EnsureFrame(t, clipmap);

            bool began = false, whole = false;
            lightDraws = 0;
            for (int k = 0; k < Clipmap.MaxLevels; k++)
            {
                if (!clipmap.LevelReady(k))
                {
                    lightVersion[k] = -1;
                    continue;
                }
                int version = clipmap.LevelVersion(k);
                Vector4 window = clipmap.LevelWindow(k);
                if (lightVersion[k] == version)
                {
                    Changes(k, window);
                }
                else if (lightVersion[k] >= 0 && clipmap.MovedOnceSince(k, lightVersion[k], out Vector4 old) &&
                         GroundLayer.Intersect(old, window, out Vector4 kept))
                {
                    // The window moved: its new ground gets every lamp tile there; what changed
                    // meanwhile, only where the window already was.
                    began = true;
                    layer.ClearExposed(k, old);
                    int n = GroundLayer.Exposed(old, window, strips);
                    for (int i = 0; i < n; i++)
                        foreach (Tile t in LitTiles(clipmap, k, strips[i]))
                            DrawTileLights(layer, k, t, strips[i]);
                    Changes(k, kept);
                }
                else
                {
                    // Drawn whole, from every lit tile it reaches: for a wide level that's all of
                    // them, thousands of draws. Once the frame has drawn plenty, the others wait a
                    // frame (they keep what they show meanwhile) - one such level a frame at least.
                    if (whole && lightDraws >= LightDrawBudget)
                        continue;
                    whole = began = true;
                    layer.ClearLevel(k);
                    foreach (Tile t in LitTiles(clipmap, k, window))
                        DrawTileLights(layer, k, t, window);
                }
                lightVersion[k] = version;
            }
            foreach (Tile t in lightsNew)
                t.LightNew = false;
            lightsNew.Clear();
            foreach (Erase e in erase)
                UnityEngine.Object.Destroy(e.Mesh);
            erase.Clear();
            if (began)
            {
                layer.End();
                lightsInMap = true;
            }

            // Light gone and light new, within area of level k.
            void Changes(int k, Vector4 area)
            {
                foreach (Erase e in erase)
                    if (!e.Stale && layer.Draw(k, e.Mesh, e.Matrix, e.X0, e.Y0, e.X1, e.Y1, area, e.Pass))
                        began = true;
                foreach (Tile t in lightsNew)
                {
                    began = true;
                    DrawTileLights(layer, k, t, area);
                }
            }
        }

        /// <summary>A tile's pools and lit streets, within area of level k (Draw skips what
        /// doesn't reach it). Meshes are set to null when dropped: plain reference checks.</summary>
        void DrawTileLights(GroundLayer layer, int k, Tile t, Vector4 area)
        {
            if (!ReferenceEquals(t.LightMesh, null) &&
                layer.Draw(k, t.LightMesh, Matrix4x4.identity, t.LightX0, t.LightY0, t.LightX1, t.LightY1, area, GroundLayer.PoolPass))
                lightDraws++;
            if (!ReferenceEquals(t.GlowMesh, null) &&
                layer.Draw(k, t.GlowMesh, t.Frame, t.GlowX0, t.GlowY0, t.GlowX1, t.GlowY1, area, GroundLayer.GlowPass))
                lightDraws++;
        }

        static bool HasLight(Tile t) => !ReferenceEquals(t.LightMesh, null) || !ReferenceEquals(t.GlowMesh, null);

        /// <summary>Tiles whose light may fall in area of level k (absolute level coordinates).
        /// A small area - the fine levels, whose windows move every few metres the camera goes -
        /// is looked up by place among the tiles there; a large one takes every tile with light
        /// (`lit`: not all tiles, which with lamps far out are hundreds of thousands).</summary>
        List<Tile> LitTiles(Clipmap clipmap, int k, Vector4 area)
        {
            litNear.Clear();
            double width = clipmap.LevelWidth(k);
            TangentPlane plane = clipmap.Plane;
            if (plane != null && (area.z - area.x) * width < LookupSpan && (area.w - area.y) * width < LookupSpan)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                double lon0 = double.NaN;
                for (int c = 0; c < 4; c++)
                {
                    double px = ((c & 1) == 0 ? area.x : area.z) * width, py = ((c & 2) == 0 ? area.y : area.w) * width;
                    Geo.LatLon(plane.Unproject(px, py), out double lat, out double lon);
                    if (double.IsNaN(lon0))
                        lon0 = lon;
                    lon = lon0 + Math.IEEERemainder(lon - lon0, 360.0); // across the antimeridian too
                    double tx = OsmTile.TileX(lon), ty = OsmTile.TileY(lat);
                    minX = Math.Min(minX, tx);
                    maxX = Math.Max(maxX, tx);
                    minY = Math.Min(minY, ty);
                    maxY = Math.Max(maxY, ty);
                }
                // A tile's pools and lit streets reach a little beyond it: a tile more around.
                int x0 = (int)Math.Floor(minX) - 1, x1 = (int)Math.Floor(maxX) + 1;
                int y0 = (int)Math.Floor(minY) - 1, y1 = (int)Math.Floor(maxY) + 1;
                int n = 1 << OsmTile.Zoom;
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) < lit.Count)
                {
                    for (int y = Math.Max(0, y0); y <= Math.Min(n - 1, y1); y++)
                    {
                        for (int ux = x0; ux <= x1; ux++)
                        {
                            int x = ((ux % n) + n) % n;
                            if (tiles.TryGetValue(((long)x << 20) | (uint)y, out Tile t) && HasLight(t))
                                litNear.Add(t);
                        }
                    }
                    return litNear;
                }
            }
            foreach (Tile t in lit)
                litNear.Add(t);
            return litNear;
        }

        /// <summary>Every level is drawn whole next time; pools waiting to be erased are moot.</summary>
        void ForgetLights()
        {
            foreach (Erase e in erase)
                UnityEngine.Object.Destroy(e.Mesh);
            erase.Clear();
            foreach (Tile t in lightsNew)
                t.LightNew = false;
            lightsNew.Clear();
            for (int k = 0; k < Clipmap.MaxLevels; k++)
                lightVersion[k] = -1;
        }

        /// <summary>A quad per pool of light at its centre in plane metres (TileProject pass 2
        /// expands it to the pool's radius, or a texel of the level: GroundLayer.Pad covers that).</summary>
        void BuildLightMesh(Tile t, TangentPlane plane)
        {
            int n = t.Lights.Count;
            List<Vector3> vertices = poolVertices;
            List<Color32> colours = poolColours;
            List<Vector4> corners = poolCorners;
            List<int> triangles = poolTriangles;
            vertices.Clear();
            colours.Clear();
            corners.Clear();
            triangles.Clear();
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            double reach = 0.0; // the largest pool's radius
            foreach (GroundLight g in t.Lights)
            {
                reach = Math.Max(reach, g.Radius * worldScale);
                if (!plane.Project(Geo.Direction(g.Lat, g.Lon), out double x, out double y))
                    continue;
                int v = vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new Vector3((float)x, (float)y, 0f));
                    colours.Add(g.Colour);
                    corners.Add(new Vector4(k == 0 || k == 3 ? -1f : 1f, k < 2 ? -1f : 1f, g.Radius * (float)worldScale, 0f));
                }
                triangles.Add(v);
                triangles.Add(v + 1);
                triangles.Add(v + 2);
                triangles.Add(v);
                triangles.Add(v + 2);
                triangles.Add(v + 3);
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }
            if (vertices.Count == 0)
                return;
            Mesh mesh = MeshUpload.Streams($"SolSatellite light pools {t.X}/{t.Y}", vertices, colours, corners, null, triangles,
                                           new Bounds(Vector3.zero, Vector3.one * 1e7f)); // never culled; drawn by command buffer
            mesh.UploadMeshData(true); // only drawn: no copy in main memory
            t.LightMesh = mesh;
            t.LightX0 = x0 - reach;
            t.LightY0 = y0 - reach;
            t.LightX1 = x1 + reach;
            t.LightY1 = y1 + reach;
            UpdateLit(t);
        }

        void Remove(Tile t)
        {
            if (t.Removed)
                return;
            tiles.Remove(t.Key);
            t.Removed = true;
            if (t.Job == Job.Vector)
            {
                t.Request.Abort();
                t.Request.Dispose();
                t.Request = null;
                t.Job = Job.None;
                vectorRequests--;
            }
            else if (t.Job == Job.Read || t.Job == Job.VectorParse || t.Job == Job.SupplementParse || t.Job == Job.OverpassParse ||
                     t.Job == Job.OvertureParse || t.Job == Job.Mesh)
            {
                orphans.Add(t);
            }
            t.Overture?.Cancel();
            t.Overture = null;
            // An Overpass query in flight is cancelled by PollOverpass.
            DropBuildings(t);
            DropLamps(t);
            DropRoads(t);
            DropRoads3D(t);
            DropMask(t);
            if (t.Root != null)
                UnityEngine.Object.Destroy(t.Root);
            t.Root = null;
            t.Pending = null;
            BuildingMeshData.Recycle(t.ReadyMesh);
            LampMeshData.Recycle(t.ReadyLamps);
            RibbonMeshData.Recycle(t.ReadyRoads);
            RibbonMeshData.Recycle(t.ReadyMask);
            Road3DMeshData.Recycle(t.ReadyRoads3D);
            t.ReadyMesh = null;
            t.ReadyLamps = null;
            t.ReadyRoads = null;
            t.ReadyRoads3D = null;
            t.ReadyMask = null;
            t.ReadyToUpload = false;
        }

        void PollOrphans()
        {
            for (int i = orphans.Count - 1; i >= 0; i--)
            {
                Tile t = orphans[i];
                if (!t.WorkDone)
                    continue;
                workers--;
                t.WorkDone = false;
                t.WorkData = null;
                RecycleWork(t);
                orphans.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ OpenFreeMap

        /// <summary>The tile URL template changes with every weekly release: read it from the TileJSON.</summary>
        void UpdateTileJson(float now)
        {
            if (!settings.VectorTiles || vectorTemplate != null)
                return;
            if (tileJsonRequest == null)
            {
                if (now < tileJsonRetryAt)
                    return;
                tileJsonRequest = UnityWebRequest.Get(settings.VectorTileJson);
                tileJsonRequest.timeout = 20;
                tileJsonRequest.SetRequestHeader("User-Agent", UserAgent);
                tileJsonRequest.SendWebRequest();
                return;
            }
            if (!tileJsonRequest.isDone)
                return;
            try
            {
                if (tileJsonRequest.isNetworkError || tileJsonRequest.isHttpError)
                    throw new IOException(tileJsonRequest.isNetworkError ? tileJsonRequest.error : $"HTTP {tileJsonRequest.responseCode}");
                vectorTemplate = FirstTileUrl(tileJsonRequest.downloadHandler.data) ?? throw new InvalidDataException("no tile URL in TileJSON");
                tileJsonFailures = 0;
                vectorProblem = null;
            }
            catch (Exception e)
            {
                tileJsonFailures++;
                tileJsonRetryAt = now + (tileJsonFailures < 3 ? 10f : 300f);
                vectorProblem = e.Message;
                Debug.LogWarning($"[SolSatellite] OpenFreeMap TileJSON: {e.Message}");
            }
            finally
            {
                tileJsonRequest.Dispose();
                tileJsonRequest = null;
            }
        }

        static string FirstTileUrl(byte[] tileJson)
        {
            var j = new Json(tileJson);
            if (!j.BeginObject())
                return null;
            string url = null;
            do
            {
                if (j.Key() != "tiles")
                {
                    j.Skip();
                    continue;
                }
                if (j.BeginArray())
                {
                    do
                    {
                        string s = j.String();
                        url ??= s;
                    } while (j.Next(']'));
                }
            } while (j.Next('}'));
            return url;
        }

        void StartVector(Tile t)
        {
            string url = vectorTemplate.Replace("{z}", OsmTile.Zoom.ToString()).Replace("{x}", t.X.ToString()).Replace("{y}", t.Y.ToString());
            var req = UnityWebRequest.Get(url);
            req.timeout = 30;
            req.SetRequestHeader("User-Agent", UserAgent);
            TryAcceptGzip(req);
            req.SendWebRequest();
            t.Request = req;
            t.Job = Job.Vector;
            vectorRequests++;
            MarkBusy(t);
            Stage(t);
        }

        void FinishVector(Tile t, float now)
        {
            UnityWebRequest req = t.Request;
            t.Request = null;
            t.Job = Job.None;
            vectorRequests--;
            Unsettle(t);
            bool supplement = t.Supplementing;
            t.Supplementing = false;
            try
            {
                long code = req.responseCode;
                if (!req.isNetworkError && (code == 200 || code == 204 || code == 404))
                {
                    // No tile (204/404) means nothing there, like the open sea.
                    byte[] data = code == 200 ? req.downloadHandler.data : new byte[0];
                    VectorDownloaded++;
                    DownloadedBytes += data.Length;
                    vectorFailuresInRow = 0;
                    vectorProblem = null;
                    StartParse(t, supplement ? Job.SupplementParse : Job.VectorParse, data);
                    return;
                }
                vectorProblem = req.isNetworkError ? "нет связи: " + req.error : $"HTTP {code}";
                VectorFailures++;
                if (VectorFailures <= 5 || VectorFailures % 50 == 0)
                    Debug.LogWarning($"[SolSatellite] Vector tile {t.X}/{t.Y}: {(req.isNetworkError ? req.error : "HTTP " + code)} " +
                                     $"(attempt {t.VectorAttempts + 1}, {VectorFailures} failures this session)");
                if (++vectorFailuresInRow >= 10)
                {
                    // The whole service seems down: fetch the TileJSON again (maybe a new release).
                    vectorTemplate = null;
                    vectorFailuresInRow = 0;
                }
                if (++t.VectorAttempts >= MaxVectorAttempts)
                    t.VectorFailed = true; // Overpass will do this one
                else
                    t.VectorRetryAt = now + 5f * t.VectorAttempts;
            }
            finally
            {
                req.Dispose();
            }
        }

        static void TryAcceptGzip(UnityWebRequest req)
        {
            try
            {
                req.SetRequestHeader("Accept-Encoding", "gzip"); // replies are parsed gzipped or not
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ Overpass

        /// <summary>Nearest tile Overpass should fetch: for detail over vector data, or as the
        /// only source where vector tiles failed. Only within the building radius.</summary>
        Tile NextOverpassTile(float now)
        {
            // Only within the building radius: `ordered` goes by distance, so the far tens of
            // thousands (lamps) needn't be looked at every second.
            double reach = settings.BuildingRadiusKm * 1000.0 + KeepMargin;
            foreach (Tile t in ordered)
            {
                if (t.Distance > reach)
                    break;
                if (t.Job != Job.None || t.Pending != null || t.ReadyToUpload || t.OverpassFailed ||
                    !t.WantBuildings || now < t.OverpassRetryAt)
                    continue;
                bool primary = t.Shown == null && t.CacheChecked && t.VectorFailed;
                bool detail = settings.OverpassDetail && t.WantDetail && t.Shown == OsmSource.OpenFreeMap;
                if (primary || detail)
                    return t;
            }
            return null;
        }

        void StartOverpass(float now)
        {
            if (overpassTile != null || now < overpassWaitUntil || now < overpassSearchAt || !OverpassInTime)
                return;
            Tile t = NextOverpassTile(now);
            if (t == null)
            {
                overpassSearchAt = now + 1f; // nothing to ask for: not every frame through every tile
                return;
            }
            overpassTile = t;
            t.Job = Job.Overpass;
            overpassSlotFree = statusUnsupported;
            if (!overpassSlotFree)
                StartStatus();
        }

        string StatusUrl
        {
            get
            {
                string url = settings.OverpassUrl.TrimEnd('/');
                return url.EndsWith("/interpreter") ? url.Substring(0, url.Length - "interpreter".Length) + "status" : null;
            }
        }

        void StartStatus()
        {
            string url = StatusUrl;
            if (url == null)
            {
                statusUnsupported = overpassSlotFree = true;
                return;
            }
            overpassStatus = UnityWebRequest.Get(url);
            overpassStatus.timeout = 20;
            overpassStatus.SetRequestHeader("User-Agent", UserAgent);
            overpassStatus.SendWebRequest();
            overpassState = "спрашиваем сервер о свободном слоте";
        }

        void PollOverpass(float now)
        {
            if (overpassStatus != null)
            {
                if (!overpassStatus.isDone)
                    return;
                FinishStatus(now);
            }
            if (overpassQuery != null)
            {
                if (overpassTile.Removed && !overpassQuery.isDone)
                {
                    // Flown away from: the reply isn't wanted any more. The server may still be
                    // busy with it, so the next query waits for /api/status as usual.
                    overpassQuery.Abort();
                    overpassQuery.Dispose();
                    overpassQuery = null;
                    overpassTile.Job = Job.None;
                    overpassTile = null;
                    overpassState = null;
                    OverpassCancelled++;
                    return;
                }
                if (overpassQuery.isDone)
                    FinishQuery(now);
                return;
            }
            if (overpassTile == null)
                return;
            if (overpassTile.Removed)
            {
                overpassTile.Job = Job.None;
                overpassTile = null;
                return;
            }
            if (now < overpassWaitUntil)
                return;
            if (!overpassSlotFree)
            {
                if (overpassStatus == null)
                    StartStatus();
                return;
            }
            StartQuery(overpassTile, now);
        }

        static readonly Regex SlotsFree = new Regex(@"(\d+) slots? available now");
        static readonly Regex SlotIn = new Regex(@"in (\d+) seconds");

        /// <summary>Reads /api/status: "N slots available now", or one "Slot available after:
        /// ..., in S seconds." line per busy slot.</summary>
        void FinishStatus(float now)
        {
            UnityWebRequest req = overpassStatus;
            overpassStatus = null;
            try
            {
                if (req.isNetworkError)
                {
                    OverpassPause(now, 30f, "нет связи: " + req.error);
                    return;
                }
                if (req.responseCode != 200)
                {
                    statusUnsupported = overpassSlotFree = true; // a server without /api/status
                    return;
                }
                string text = req.downloadHandler.text ?? "";
                Match free = SlotsFree.Match(text);
                if (free.Success && int.Parse(free.Groups[1].Value) > 0 || text.IndexOf("Rate limit: 0", StringComparison.Ordinal) >= 0)
                {
                    overpassSlotFree = true;
                    return;
                }
                int wait = int.MaxValue;
                foreach (Match m in SlotIn.Matches(text))
                    wait = Math.Min(wait, int.Parse(m.Groups[1].Value));
                if (wait == int.MaxValue)
                    wait = 10; // all slots running queries: look again soon
                overpassWaitUntil = now + Mathf.Clamp(wait, 1, 300) + 1f;
                overpassState = $"ждём свободный слот ({wait + 1} с)";
            }
            finally
            {
                req.Dispose();
            }
        }

        void StartQuery(Tile t, float now)
        {
            byte[] form = Encoding.UTF8.GetBytes("data=" + Uri.EscapeDataString(OsmTile.Query(t.X, t.Y, t.Heavy, !t.HasOverture)));
            var req = new UnityWebRequest(settings.OverpassUrl, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(form) { contentType = "application/x-www-form-urlencoded" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = t.Heavy ? 200 : 90,
            };
            req.SetRequestHeader("User-Agent", UserAgent);
            TryAcceptGzip(req);
            req.SendWebRequest();
            overpassQuery = req;
            overpassStarted = now;
            overpassSlotFree = false; // next query asks /api/status again
            overpassState = null;
        }

        void FinishQuery(float now)
        {
            UnityWebRequest req = overpassQuery;
            Tile t = overpassTile;
            overpassQuery = null;
            overpassTile = null;
            try
            {
                long code = req.responseCode;
                if (!req.isNetworkError && code == 200)
                {
                    byte[] data = req.downloadHandler.data;
                    OverpassDownloaded++;
                    DownloadedBytes += data.Length;
                    overpassState = null;
                    overpassWaitUntil = now + 1f;
                    StartParse(t, Job.OverpassParse, data); // cached even if the tile is gone meanwhile
                    if (t.Removed)
                        orphans.Add(t);
                    return;
                }
                t.Job = Job.None;
                if (code == 429)
                {
                    // Our slots are used up after all: /api/status says for how long.
                    overpassWaitUntil = now + 2f;
                    overpassState = "сервер просит подождать (429)";
                    t.OverpassRetryAt = now;
                    return;
                }
                string why = req.isNetworkError ? "нет связи: " + req.error
                    : code == 504 || code == 503 ? $"сервер перегружен ({code})"
                    : $"HTTP {code}";
                // Busy servers clear up quickly: retry in seconds, not minutes.
                OverpassPause(now, code == 504 || code == 503 ? 10f : 30f, why);
                OverpassRetry(t, now, 20f);
            }
            finally
            {
                req.Dispose();
            }
        }

        void OverpassPause(float now, float seconds, string why)
        {
            overpassStrikes++;
            float pause = Mathf.Min(90f, seconds * Mathf.Min(overpassStrikes, 6)) + (float)jitter.NextDouble() * 5f;
            overpassWaitUntil = now + pause;
            overpassState = $"{why}, повтор через {pause:0} с";
            Debug.Log($"[SolSatellite] Overpass: {why}; next query in {pause:0} s");
        }

        void OverpassRetry(Tile t, float now, float delay)
        {
            t.Job = Job.None;
            if (++t.OverpassAttempts >= MaxOverpassAttempts)
                t.OverpassFailed = true; // keeps its vector data, if any; not again this session
            else
                t.OverpassRetryAt = now + delay * t.OverpassAttempts;
        }

        // ------------------------------------------------------------------ colliders

        void UpdateColliders()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            bool want = settings.BuildingColliders && vessel != null && vessel.mainBody == body;
            // The vessel in tile units, once (not per tile: thousands of them).
            double vx = 0.0, vy = 0.0, metersPerTile = 0.0;
            if (want)
            {
                vx = OsmTile.TileX(vessel.longitude);
                vy = OsmTile.TileY(vessel.latitude);
                metersPerTile = MetersPerTile(vessel.latitude);
            }
            // Only tiles with buildings (withMesh): going through all of them, tens of thousands
            // spread over memory, took 3 ms a frame.
            FinishRetired(false);
            colliderList.Clear();
            colliderList.AddRange(withMesh);
            int baking = 0;
            foreach (Tile t in colliderList)
                if (t.Baking)
                    baking++;
            foreach (Tile t in colliderList)
            {
                if (t.Baking && t.Bake.IsCompleted)
                {
                    t.Bake.Complete();
                    t.Baking = false;
                    baking--;
                    if (want && t.Object != null)
                    {
                        var collider = t.Object.AddComponent<MeshCollider>();
                        collider.sharedMaterial = physics ??= new PhysicMaterial("SolSatellite building")
                        {
                            staticFriction = 0.8f, dynamicFriction = 0.7f, bounciness = 0f,
                            frictionCombine = PhysicMaterialCombine.Maximum, bounceCombine = PhysicMaterialCombine.Multiply,
                        };
                        collider.sharedMesh = t.Mesh; // uses the data baked on the job thread
                        t.HasCollider = true;
                    }
                }
                double d = want && t.Mesh != null ? TileDistance(t, vx, vy, metersPerTile) : double.MaxValue;
                bool near = d < ColliderRadius;
                // A replaced build stays only as a collider, until the new one has its own.
                if (t.OldObject != null && (t.HasCollider || !near))
                {
                    UnityEngine.Object.Destroy(t.OldObject);
                    UnityEngine.Object.Destroy(t.OldMesh);
                    t.OldObject = null;
                    t.OldMesh = null;
                }
                if (t.Mesh == null)
                {
                    TrackColliders(t);
                    continue;
                }
                if (near && !t.HasCollider && !t.Baking && baking < 2)
                {
                    t.Bake = new BakeJob { MeshId = t.Mesh.GetInstanceID() }.Schedule();
                    JobHandle.ScheduleBatchedJobs();
                    t.Baking = true;
                    baking++;
                }
                else if (t.HasCollider && d > ColliderRadius + KeepMargin)
                {
                    UnityEngine.Object.Destroy(t.Object.GetComponent<MeshCollider>());
                    t.HasCollider = false;
                }
            }
        }

        /// <summary>The real relief changed in these places (DemTerrain): what stands there is
        /// put on the new ground.</summary>
        public void TerrainChanged(List<DemCap> caps)
        {
            frontier = 0; // stale tiles have something to do again
            foreach (Tile t in tiles.Values)
            {
                if (!t.HasCap)
                {
                    Vector3d c = Geo.Direction(OsmTile.LatOf(t.Y + 0.5), OsmTile.LonOf(t.X + 0.5));
                    Vector3d corner = Geo.Direction(OsmTile.LatOf(t.Y), OsmTile.LonOf(t.X));
                    // With the margin the height grid has around the tile.
                    t.Cap = new DemCap(c, 1.3 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(c, corner)))));
                    t.HasCap = true;
                }
                if (t.TerrainStale)
                    continue;
                foreach (DemCap cap in caps)
                {
                    if (t.Cap.Overlaps(cap))
                    {
                        t.TerrainStale = true;
                        break;
                    }
                }
            }
        }

        /// <summary>No OSM buildings or lamps on top of KSC (wherever KSCSwitcher put it) or
        /// other PQS cities such as Sol's tracking stations and Kerbal Konstructs' groups (the
        /// LC-39 pads). KSC is full size: its runway runs along its local x from -1325 to +1141 m,
        /// 497 m to its north, the pad at +447 (MechJeb's stock coordinates), so three circles
        /// along x cover it. One circle of 3 km took the real VAB and LC-39 away too, 1.5-2.5 km
        /// off once KSC stands on Merritt Island (2026-10-04).</summary>
        void RefreshExclusions()
        {
            nextExclusions = Time.realtimeSinceStartup + 60f;
            var list = new List<BuildingMesher.Exclusion>();
            void Add(Vector3 world, double radius)
            {
                Vector3d dir = Geo.Direction(body.GetLatitude(world), body.GetLongitude(world));
                list.Add(new BuildingMesher.Exclusion { X = dir.x, Y = dir.y, Z = dir.z, CosAngle = Math.Cos(radius / body.Radius) });
            }
            foreach (PQSCity city in body.pqsController.GetComponentsInChildren<PQSCity>(true))
            {
                if (city.name != "KSC")
                {
                    Add(city.transform.position, 500.0);
                    continue;
                }
                Transform t = city.transform;
                for (int i = -1; i <= 1; i++)
                    Add(t.position + t.right * (900f * i) + t.forward * 100f, 1100.0);
            }
            foreach (PQSCity2 city in body.pqsController.GetComponentsInChildren<PQSCity2>(true))
                Add(city.transform.position, 400.0);
            exclusions = list;
        }

        // ------------------------------------------------------------------ status

        public int TilesTotal => tiles.Count;

        /// <summary>The settings window's counts below come from a pass over the tiles a slice a
        /// frame while it asks for them: all of them at once, five times each GUI event, took
        /// most of the frame with a big radius (2026-10-02).</summary>
        void CountSome(float now)
        {
            if (now > statusWantedUntil)
            {
                countCursor = -1;
                return;
            }
            if (countCursor < 0)
            {
                counting = default;
                countCursor = 0;
            }
            int end = Math.Min(ordered.Count, countCursor + CountedPerFrame);
            bool detail = settings.OverpassDetail;
            for (int i = countCursor; i < end; i++)
            {
                Tile t = ordered[i];
                if (t.Removed)
                    continue;
                if ((t.BuildingsBuilt || !t.WantBuildings) && (t.LampsBuilt || !t.WantLamps) && (t.RoadsBuilt || !t.WantRoads) && t.Shown != null)
                    counting.Built++;
                if (t.Shown == OsmSource.Overpass)
                    counting.Detailed++;
                if (detail && t.WantDetail && !t.OverpassFailed && t.Shown == OsmSource.OpenFreeMap && t.WantBuildings)
                    counting.DetailWaiting++;
                if (t.Shown == null && t.VectorFailed && t.OverpassFailed)
                    counting.Failed++;
                if (t.HasOverture)
                    counting.Overture++;
            }
            countCursor = end;
            if (countCursor >= ordered.Count)
            {
                counted = counting;
                countCursor = -1;
            }
        }

        StatusCounts Counts
        {
            get
            {
                statusWantedUntil = Time.realtimeSinceStartup + 1f;
                return counted;
            }
        }

        /// <summary>Tiles showing what they should (their buildings and/or lamps).</summary>
        public int TilesBuilt => Counts.Built;

        public int TilesDetailed => Counts.Detailed;

        public int TilesDetailWaiting => Counts.DetailWaiting;

        public int TilesFailed => Counts.Failed;

        public int TilesOverture => Counts.Overture;

        public string OvertureStatus
        {
            get
            {
                if (!settings.OvertureEnabled)
                    return "выключен";
                return overture.Status + $"; скачано тайлов {overture.Downloaded} ({overture.DownloadedBytes / 1048576.0:0.0} МБ)" +
                       (overture.MissingTiles > 0 ? $", без зданий {overture.MissingTiles}" : "");
            }
        }

        public string VectorStatus
        {
            get
            {
                if (!settings.VectorTiles)
                    return "выключен";
                if (!VectorUsable)
                    return $"недоступен ({vectorProblem}), здания берутся из Overpass";
                if (vectorTemplate == null)
                    return vectorProblem != null ? $"нет связи ({vectorProblem}), пробуем снова" : "подключаемся";
                return (vectorProblem != null ? $"ошибка: {vectorProblem}" : "в норме") +
                       (vectorRequests > 0 ? $", загружается {vectorRequests}" : "");
            }
        }

        public string OverpassStatus
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (overpassQuery != null)
                    return $"скачивается тайл ({now - overpassStarted:0} с)";
                if (overpassState != null && (overpassTile != null || now < overpassWaitUntil))
                    return overpassState;
                if (!OverpassInTime)
                    return $"ждёт: летим быстро ({groundSpeed:0} м/с), тайл уйдёт раньше ответа";
                return "в норме";
            }
        }

        /// <summary>Drops every built tile; they come back from the disk cache.</summary>
        public void Clear()
        {
            foreach (Tile t in new List<Tile>(tiles.Values))
                Remove(t);
            ordered.Clear();
            lit.Clear();
            settledCount = 0;
            countCursor = -1;
            busy.Clear(); // all removed: workers still running are counted in orphans
            probeQueue.Clear();
            foreach (Tile t in staged)
                t.InStaged = false;
            staged.Clear();
            withMesh.Clear();
            frontier = 0;
        }

        /// <summary>What the pipeline got through since the last call, for the log; starts again.</summary>
        public string Throughput()
        {
            float now = Time.realtimeSinceStartup;
            float span = throughputSince > 0f ? now - throughputSince : 0f;
            throughputSince = now;
            var sb = new StringBuilder();
            int reads = 0;
            foreach (int r in readsBy)
                reads += r;
            sb.Append($"in {span:0} s: cache reads {reads}");
            if (reads > 0)
            {
                sb.Append(" (");
                bool first = true;
                for (int i = 0; i < readsBy.Length; i++)
                {
                    if (readsBy[i] == 0)
                        continue;
                    sb.Append(first ? "" : ", ").Append(ReadWhyNames[i]).Append(' ').Append(readsBy[i]);
                    first = false;
                }
                sb.Append(')');
            }
            sb.Append($", meshed {meshesDone}, uploaded {uploadsDone}, parts kept as they were {partsKept}; workers:");
            for (int j = 0; j < jobRuns.Length; j++)
            {
                int runs = Interlocked.Exchange(ref jobRuns[j], 0);
                long ticks = Interlocked.Exchange(ref jobTicks[j], 0);
                if (runs > 0)
                    sb.Append($" {(Job)j} {runs} x {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / runs:0.0} ms");
            }
            Array.Clear(readsBy, 0, readsBy.Length);
            meshesDone = uploadsDone = partsKept = 0;
            return sb.ToString();
        }

        /// <summary>Where the tiles are in the pipeline, for the log.</summary>
        public string PipelineStatus
        {
            get
            {
                int lookup = 0, reading = 0, download = 0, parsing = 0, heights = 0, toMesh = 0, meshing = 0, upload = 0,
                    overture = 0, overpass = 0, noData = 0, done = 0, other = 0;
                foreach (Tile t in ordered)
                {
                    if (t.Removed)
                        continue;
                    switch (t.Job)
                    {
                        case Job.Read: reading++; continue;
                        case Job.Vector: download++; continue;
                        case Job.Mesh: meshing++; continue;
                        case Job.Overpass: overpass++; continue;
                        case Job.None: break;
                        default: parsing++; continue;
                    }
                    bool wants = t.WantBuildings || t.WantLamps || t.WantRoads || t.WantRoads3D || t.WantMask;
                    if (t.ReadyToUpload)
                        upload++;
                    else if (t.Pending != null && (t.GridDone || !t.WantBuildings && !t.WantLamps && !t.WantRoads3D))
                        toMesh++;
                    else if (t.Pending != null)
                        heights++;
                    else if (t.Overture != null)
                        overture++;
                    else if (Settled(t) && t.Shown == null && wants)
                        noData++;
                    else if (Settled(t))
                        done++;
                    else if (t.Shown == null && !t.CacheChecked)
                        lookup++;
                    else if (t.Shown == null)
                        download++;
                    else
                        other++;
                }
                string problem = vectorProblem != null ? $", last error: {vectorProblem}" : "";
                return $"tiles done {done}, to look up on disk {lookup}, reading {reading}, to download/downloading {download}, " +
                       $"parsing {parsing}, waiting for heights {heights}, to mesh {toMesh}, meshing {meshing}, to upload {upload}, " +
                       $"waiting for Overture {overture}, Overpass {overpass}, no data {noData}, other {other}; " +
                       $"nearest unfinished #{frontier} of {ordered.Count}; on the way to a mesh {staged.Count}/{MaxStaged}; workers {workers}/{MaxWorkers}, " +
                       $"vector downloads {vectorRequests}/{MaxVectorRequests} ({VectorDownloaded} done, {VectorFailures} failed{problem})";
            }
        }

        public void Dispose()
        {
            Clear();
            FinishRetired(true);
            ForgetLights();
            foreach (UnityWebRequest req in new[] { overpassQuery, overpassStatus, tileJsonRequest })
            {
                if (req == null)
                    continue;
                req.Abort();
                req.Dispose();
            }
            overpassQuery = overpassStatus = tileJsonRequest = null;
            overture.Dispose();
            UnityEngine.Object.Destroy(material);
            if (lampMaterial != null)
                UnityEngine.Object.Destroy(lampMaterial);
            if (road3DMaterial != null)
                UnityEngine.Object.Destroy(road3DMaterial);
        }
    }
}
