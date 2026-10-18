// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// Nested square textures around the camera (a clipmap) on one plane tangent to the planet.
    /// Level k is twice as wide as level k-1 with the same texel count, so each level is
    /// sharpest roughly as far out as it reaches. Level 0 is as narrow as the finest zoom
    /// streamed needs (its texels as sharp as that zoom's pixels): ~70 game m on Sol's Quarter
    /// Earth for z20 with 2048 texels; 16 levels then reach ~2300 km. A level is in use while
    /// the camera is less than DetailAltitude times its width above the ground (from orbit:
    /// levels about as wide as the camera is high).
    ///
    /// Web-map tiles are in real metres: on a smaller planet (Quarter: a quarter of the real
    /// Earth) a game metre shows four real ones, and a level's zoom is picked for that.
    ///
    /// Levels are toroidal: ground at absolute level coordinate a (plane metres / level width)
    /// sits at texel frac(a). When a level follows the camera, only the strips of newly exposed
    /// ground are cleared and drawn; everything else stays in place. The terrain shader falls
    /// back to coarser levels (and the orbital cube) wherever a level isn't drawn yet.
    ///
    /// Only tiles inside the windows are fetched: the streamer is told whenever that set
    /// changes and drops the rest. A level the camera crosses in under two seconds (time warp,
    /// re-entry) takes tiles from the disk cache only - downloads would come too late.
    ///
    /// Two more layers share the levels (GroundLayer): the night light map and the roads
    /// drawn over the imagery, both filled by BuildingManager from OSM data.
    /// </summary>
    sealed class Clipmap : IDisposable
    {
        public const int MaxLevels = 16;     // SOLSAT_LEVELS in Shaders/Terrain/SolSatellite.cginc
        /// <summary>Above this Sol draws Earth in scaled space: the clipmap feeds OrbitDetail.</summary>
        public const double OrbitAltitude = 130000.0;
        const double MaxAltitude = 1700000.0; // the local camera doesn't reach the ground beyond
        const int TileGrid = 8;              // a tile is drawn as TileGrid x TileGrid quads
        const int TilesCheckedPerLevel = 16;
        const int DrawsPerFrame = 48;
        const double ReanchorDistance = 1000000.0; // gnomonic scale is off by 2.5% there: fine, and resets stay rare in orbit
        const double LatitudeDrift = 0.25;   // level 0 sized for another latitude by this many zooms: anchored anew
        const double CrossingSeconds = 2.0;  // a level crossed faster than this downloads nothing
        const float MipsInterval = 0.1f;     // mip chains are rebuilt at most this often
        const double Recenter = 0.75;        // a level follows once the camera is this many steps (a quarter of its width) off its centre
        const double KeepActive = 1.15;      // a level in use stays so until the camera is this much higher than it takes to start
        public const int LightSize = 1024;   // night light map texels per level
        const int RoadSize = 2048;           // road layer texels per level at most (asphalt and paint)
        const int MaskSize = 1024;           // scatter mask texels per level (ScatterMask)

        static readonly int WindowId = Shader.PropertyToID("_Window");
        static readonly int OldWindowId = Shader.PropertyToID("_OldWindow");
        static readonly int RoadParamsId = Shader.PropertyToID("_SolSatRoadParams");
        static readonly int AxisEId = Shader.PropertyToID("_SolSatAxisE");
        static readonly int AxisNId = Shader.PropertyToID("_SolSatAxisN");
        static readonly int AxisUId = Shader.PropertyToID("_SolSatAxisU");
        static readonly int RefId = Shader.PropertyToID("_SolSatRef");
        static readonly int RefPlaneId = Shader.PropertyToID("_SolSatRefPlane");
        static readonly int LevelsId = Shader.PropertyToID("_SolSatLevels");
        static readonly int LevelFracId = Shader.PropertyToID("_SolSatLevelFrac");

        sealed class Level
        {
            public int Index;
            public double Width;
            public bool Active, Valid;
            public int Version; // bumped whenever the window moves or is reset (the light map follows)
            public int MovedAt;           // the version the window last moved to (not a reset)
            public Vector4 PreviousWindow; // ... and where it was before that move
            public double CenterX, CenterY; // plane metres, on a grid of Width / 4
            public int Zoom;
            public readonly List<long> Tiles = new List<long>(); // covering the window, nearest first
            public HashSet<long> Drawn = new HashSet<long>();

            /// <summary>The window in absolute level coordinates: (min x, min y, max x, max y).</summary>
            public Vector4 Window => new Vector4(
                (float)(CenterX / Width - 0.5), (float)(CenterY / Width - 0.5),
                (float)(CenterX / Width + 0.5), (float)(CenterY / Width + 0.5));
        }

        public readonly RenderTexture Texture;
        /// <summary>Night light on the ground (lamps' pools, lit streets), added by the terrain shader.</summary>
        public readonly GroundLayer Lights;
        /// <summary>Roads over the imagery, at the imagery's resolution; made when first wanted.</summary>
        public GroundLayer Roads { get; private set; }
        /// <summary>Where Parallax's scatters belong (ScatterMask, MaskMesher); made when first wanted.</summary>
        public GroundLayer Mask { get; private set; }
        readonly Material tileMaterial;
        readonly Mesh quad; // the whole level, for clearing (pass 1)
        bool mipsDirty;
        float nextMips;
        readonly Settings settings;
        readonly TileStreamer streamer;
        readonly CelestialBody body;
        readonly Level[] levels;
        readonly int size;          // texels per level side
        readonly int mips;          // mip levels of each
        readonly double worldScale; // planet radius / the real Earth's
        double finest = double.NaN; // level 0's width now (plane metres)
        readonly Vector4[] levelParams = new Vector4[MaxLevels];
        readonly Vector4[] levelFrac = new Vector4[MaxLevels];
        readonly double[] gridX = new double[(TileGrid + 1) * (TileGrid + 1)];
        readonly double[] gridY = new double[(TileGrid + 1) * (TileGrid + 1)];
        TangentPlane plane;
        static int planes;          // plane versions are never reused, not even by a new clipmap
        double camLat, camLon, camAlt;
        double ground;              // terrain height under the camera (m above sea level)
        float nextGround;
        Vector4 eastParam, northParam, upParam, refParam, refPlaneParam;

        // Tiles the windows cover, as last told to the streamer.
        readonly HashSet<long> needed = new HashSet<long>();
        bool neededSent;

        // Camera speed over the ground, plane metres per real second (downloads take real time).
        double speed, lastX, lastY;
        float lastTime;
        int lastPlane = -1;

        public bool Active { get; private set; }
        public int DrawnTiles { get; private set; }
        public double CameraAltitude => camAlt;
        /// <summary>The camera's height over the ground (sea level at sea), as levels are chosen by.</summary>
        public double CameraHeight => Math.Max(camAlt - ground, 1.0);
        public int LevelTexels => size;
        /// <summary>Level 0's width now (plane metres); 0 before the first update.</summary>
        public double FinestLevelWidth => double.IsNaN(finest) ? 0.0 : finest;
        public double CameraSpeed => speed;
        /// <summary>The plane levels live on; its coordinates change when PlaneVersion does.</summary>
        public TangentPlane Plane => plane;
        public int PlaneVersion { get; private set; }

        public Clipmap(Settings settings, TileStreamer streamer, CelestialBody body, Shader tileShader)
        {
            this.settings = settings;
            this.streamer = streamer;
            this.body = body;
            size = Settings.ClampLevelTexels(settings.LevelSize);
            mips = MipCount(size);
            worldScale = body.Radius / 6371000.0;
            if (Math.Abs(worldScale - 1.0) < 0.01)
                worldScale = 1.0;
            tileMaterial = new Material(tileShader);
            Texture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = MaxLevels,
                useMipMap = true,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat, // toroidal levels
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                name = "SolSatellite clipmap",
            };
            Texture.Create();
            levels = new Level[MaxLevels];
            for (int k = 0; k < MaxLevels; k++)
                levels[k] = new Level { Index = k }; // widths with the plane (SetWidths)
            quad = new Mesh { name = "SolSatellite level quad" };
            quad.SetVertices(new List<Vector3> { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f) });
            quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e7f);
            Lights = new GroundLayer("SolSatellite night lights", LightSize, this, tileMaterial, quad, GroundLayer.LightFormat());
        }

        /// <summary>Mip levels of a texture this many texels wide (down to 1x1).</summary>
        public static int MipCount(int texels)
        {
            int n = 1;
            while (texels > 1)
            {
                texels >>= 1;
                n++;
            }
            return n;
        }

        /// <summary>The road layer, made on first use (as much video memory as the imagery, up to 2048 texels).</summary>
        public GroundLayer EnsureRoads() => Roads ??= new GroundLayer("SolSatellite roads", Math.Min(size, RoadSize), this, tileMaterial, quad);

        /// <summary>The scatter mask layer, made on first use.</summary>
        public GroundLayer EnsureMask() => Mask ??= new GroundLayer("SolSatellite scatter mask", MaskSize, this, tileMaterial, quad);

        /// <summary>Each level's window for the scatter mask, in absolute plane coordinates:
        /// (centre / width, 1 / width, 1 if in use); zero for levels not in use.</summary>
        public void MaskLevelParams(Vector4[] into)
        {
            for (int k = 0; k < MaxLevels && k < into.Length; k++)
            {
                Level l = levels[k];
                into[k] = l.Active && l.Valid && l.Width > 0.0
                    ? new Vector4((float)(l.CenterX / l.Width), (float)(l.CenterY / l.Width), (float)(1.0 / l.Width), 1f)
                    : Vector4.zero;
            }
        }

        /// <summary>Video memory of the clipmap and its layers, with mips.</summary>
        public long Bytes => ImageryBytes(size) + Lights.Bytes + (Roads?.Bytes ?? 0) + (Mask?.Bytes ?? 0);

        /// <summary>Video memory the imagery levels take at this many texels (with mips).</summary>
        public static long ImageryBytes(int texels) => (long)texels * texels * MaxLevels * 4 * 4 / 3;

        /// <summary>Video memory of everything the clipmap makes at this many texels: imagery,
        /// road layer, night light map.</summary>
        public static long TotalBytes(int texels)
        {
            int roads = Math.Min(texels, RoadSize);
            return ImageryBytes(texels) + ImageryBytes(roads) + ImageryBytes(LightSize);
        }

        /// <summary>Level 0's width: what the user set, or as narrow as the finest zoom needs at
        /// this latitude - texels half a zoom sharper than its pixels, so ZoomFor picks it.</summary>
        double FinestWidth(double lat)
        {
            if (settings.FinestLevelMeters > 0.0)
                return settings.FinestLevelMeters;
            double pixel = Geo.MercatorResZ0 * Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.05) / Math.Pow(2.0, settings.StreamMaxZoom - 0.5);
            return pixel * worldScale * size;
        }

        void SetWidths(double width)
        {
            finest = width;
            for (int k = 0; k < MaxLevels; k++)
                levels[k].Width = width * (1L << k);
            InvalidateAll();
        }

        /// <summary>Tile zoom for a level: its texels in real metres (tiles are real-world ones).</summary>
        int ZoomOf(Level l, double lat) => Geo.ZoomFor(lat, l.Width / size / worldScale, settings.StreamMaxZoom);

        /// <summary>The camera moved so far north or south that level 0's zoom no longer fits it.</summary>
        bool Drifted(double lat) =>
            settings.FinestLevelMeters <= 0.0 && plane != null &&
            Math.Abs(Math.Log(Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.05) / Math.Max(Math.Cos(plane.Lat * Math.PI / 180.0), 0.05), 2.0)) > LatitudeDrift;

        public void InvalidateAll()
        {
            foreach (Level l in levels)
                l.Valid = false;
        }

        /// <summary>Follows the camera and draws tiles that have arrived. Call once per frame,
        /// outside camera rendering.</summary>
        public void Update(Focus camera)
        {
            if (!Texture.IsCreated() || !Lights.Texture.IsCreated() || Roads != null && !Roads.Texture.IsCreated() ||
                Mask != null && !Mask.Texture.IsCreated())
            {
                // Render textures are lost with the graphics device (e.g. alt-tab in fullscreen).
                Texture.Create();
                Lights.Texture.Create();
                Roads?.Texture.Create();
                Mask?.Texture.Create();
                InvalidateAll();
            }
            camLat = camera.Lat;
            camLon = camera.Lon;
            camAlt = camera.Alt;
            Active = settings.StreamEnabled && camAlt < MaxAltitude && Math.Abs(camLat) < Geo.MercatorMaxLat - 1.0;
            if (!Active)
            {
                // Out of range: nothing is left to fetch.
                if (needed.Count > 0)
                {
                    needed.Clear();
                    streamer.Retain(needed);
                }
                neededSent = false;
                speed = 0.0;
                lastPlane = -1;
                return;
            }

            long timed = System.Diagnostics.Stopwatch.GetTimestamp();
            if (plane == null || GroundDistance(camLat, camLon) > ReanchorDistance || Drifted(camLat))
            {
                plane = new TangentPlane(camLat, camLon, body.Radius);
                PlaneVersion = ++planes;
                InvalidateAll();
                Perf.Step(ref timed, "imagery levels anchored anew");
            }
            double width = FinestWidth(plane.Lat);
            if (!(Math.Abs(width - finest) <= width * 1e-9)) // also when not set yet (NaN)
                SetWidths(width);
            plane.Project(Geo.Direction(camLat, camLon), out double camX, out double camY);
            UpdateSpeed(camX, camY);
            bool orbit = camAlt > OrbitAltitude;
            timed = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateGround(orbit);
            Perf.Step(ref timed, "ground height under the camera");
            double height = CameraHeight;
            double widest = 2.5 * body.Radius; // wider levels would reach round the planet

            int budget = DrawsPerFrame;
            bool dirty = false;
            timed = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (Level l in levels)
            {
                // Near the ground a level is worth it while the camera is low enough over the
                // ground for it to show detail; from orbit only levels about as coarse as the
                // camera is high are.
                // With some slack both ways (KeepActive, Recenter): a camera hovering at a
                // threshold - or turning round a vessel - would otherwise switch a level on and
                // off or move its window back and forth, and the ground there between two
                // zooms' imagery every frame or two.
                bool wasActive = l.Active;
                double keep = wasActive ? KeepActive : 1.0;
                l.Active = l.Index < settings.Levels && l.Width <= widest &&
                           (orbit ? l.Width * keep >= camAlt * 0.5 : height < l.Width * settings.DetailAltitude * keep);
                if (l.Active != wasActive)
                    neededSent = false;
                if (!l.Active)
                    continue;
                double step = l.Width * 0.25;
                double cx = Math.Round(camX / step) * step;
                double cy = Math.Round(camY / step) * step;
                int zoom = ZoomOf(l, camLat);
                if (!l.Valid || zoom != l.Zoom)
                {
                    Reset(l, cx, cy, zoom);
                    dirty = true;
                    Perf.Step(ref timed, "imagery level reset", l.Index);
                }
                else if (Math.Abs(camX - l.CenterX) > step * Recenter || Math.Abs(camY - l.CenterY) > step * Recenter)
                {
                    Shift(l, cx, cy);
                    dirty = true;
                    Perf.Step(ref timed, "imagery level moved", l.Index);
                }

                bool download = speed * CrossingSeconds < l.Width;
                int looked = 0;
                foreach (long key in l.Tiles)
                {
                    if (l.Drawn.Contains(key))
                        continue;
                    int z = (int)(key >> 42), y = (int)((key >> 21) & 0x1FFFFF), x = (int)(key & 0x1FFFFF);
                    Texture2D tex = streamer.Get(z, x, y, download, out bool missing);
                    if (missing)
                    {
                        l.Drawn.Add(key); // nothing to draw; coarser levels cover it
                    }
                    else if (tex != null && budget > 0)
                    {
                        Perf.Begin(Perf.Part.TileDraw);
                        DrawTile(l, z, x, y, tex);
                        Perf.End(Perf.Part.TileDraw);
                        l.Drawn.Add(key);
                        budget--;
                        dirty = true;
                    }
                    if (++looked >= TilesCheckedPerLevel)
                        break;
                }
                Perf.Step(ref timed, "imagery tiles looked up for level", l.Index);
            }
            if (!neededSent)
            {
                // The windows moved: tiles no longer under any of them aren't fetched any more.
                needed.Clear();
                foreach (Level l in levels)
                    if (l.Active && l.Valid)
                        needed.UnionWith(l.Tiles);
                streamer.Retain(needed);
                neededSent = true;
                Perf.Step(ref timed, "imagery tiles still needed:", needed.Count);
            }
            // Mip chains cover all levels at once, so they are rebuilt a few times a second
            // rather than after every tile; far ground shows a new tile a moment later.
            float now = Time.realtimeSinceStartup;
            mipsDirty |= dirty;
            timed = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mipsDirty && now >= nextMips)
            {
                Perf.Begin(Perf.Part.TileDraw);
                Texture.GenerateMips();
                Perf.End(Perf.Part.TileDraw);
                mipsDirty = false;
                nextMips = now + MipsInterval;
                Perf.Step(ref timed, "imagery mip chains");
            }
            Lights.UpdateMips(now);
            Roads?.UpdateMips(now);
            streamer.Update();
        }

        /// <summary>The terrain height under the camera, a few times a second: levels go by the
        /// height over the ground, so a camera on a mountain gets the fine ones too.</summary>
        void UpdateGround(bool orbit)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextGround)
                return;
            nextGround = now + 0.25f;
            ground = 0.0;
            PQS pqs = body.pqsController;
            if (orbit || pqs == null)
                return;
            try
            {
                ground = Math.Max(0.0, pqs.GetSurfaceHeight(Geo.Direction(camLat, camLon)) - body.Radius);
            }
            catch (Exception)
            {
                ground = 0.0;
            }
        }

        void UpdateSpeed(double x, double y)
        {
            float now = Time.realtimeSinceStartup;
            float dt = now - lastTime;
            if (lastPlane == PlaneVersion && dt > 0f && dt < 1f)
            {
                double dx = x - lastX, dy = y - lastY;
                double v = Math.Sqrt(dx * dx + dy * dy) / dt;
                speed += (v - speed) * Math.Min(1.0, dt / 0.5); // averaged over about half a second
            }
            lastX = x;
            lastY = y;
            lastTime = now;
            lastPlane = PlaneVersion;
        }

        /// <summary>Computes this frame's shader parameters; call right before rendering, since
        /// the planet rotates and KSP shifts the world origin. Shaders measure plane positions
        /// from reference (the camera's world position): everything large - where the camera is
        /// on the plane, and in each level - is worked out here in double precision. False when
        /// nothing is to be shown.</summary>
        public bool PrepareShaderParams(Vector3d reference)
        {
            if (plane == null || !Active)
                return false;
            Vector3d anchor = body.GetWorldSurfacePosition(plane.Lat, plane.Lon, 0.0);
            Vector3d up = body.GetSurfaceNVector(plane.Lat, plane.Lon);
            const double eps = 1e-3;
            Vector3d north = (body.GetSurfaceNVector(plane.Lat + eps, plane.Lon) - body.GetSurfaceNVector(plane.Lat - eps, plane.Lon)).normalized;
            Vector3d east = (body.GetSurfaceNVector(plane.Lat, plane.Lon + eps) - body.GetSurfaceNVector(plane.Lat, plane.Lon - eps)).normalized;
            eastParam = new Vector4((float)east.x, (float)east.y, (float)east.z, 0f);
            northParam = new Vector4((float)north.x, (float)north.y, (float)north.z, 0f);
            upParam = new Vector4((float)up.x, (float)up.y, (float)up.z, 0f);
            // The reference point's gnomonic projection: plane position R * (v.E, v.N) / (R + v.U).
            Vector3d v = reference - anchor;
            double radius = plane.Radius;
            double along = Math.Max(radius + Vector3d.Dot(v, up), radius * 0.05);
            double qx = Vector3d.Dot(v, east) / along, qy = Vector3d.Dot(v, north) / along;
            double refX = radius * qx, refY = radius * qy;
            refParam = new Vector4((float)reference.x, (float)reference.y, (float)reference.z, (float)radius);
            refPlaneParam = new Vector4((float)qx, (float)qy, (float)along, 0f);
            bool any = false;
            for (int k = 0; k < MaxLevels; k++)
            {
                Level l = levels[k];
                bool on = l.Active && l.Valid;
                if (!on)
                {
                    levelParams[k] = levelFrac[k] = Vector4.zero;
                    continue;
                }
                double ax = refX / l.Width, ay = refY / l.Width;
                levelParams[k] = new Vector4((float)(ax - l.CenterX / l.Width), (float)(ay - l.CenterY / l.Width), (float)(1.0 / l.Width), 1f);
                levelFrac[k] = new Vector4((float)(ax - Math.Floor(ax)), (float)(ay - Math.Floor(ay)), 0f, 0f);
                any = true;
            }
            return any;
        }

        public void Apply(Material mat)
        {
            mat.SetTexture("_SolSatClip", Texture);
            mat.SetTexture("_SolSatLight", Lights.Texture);
            if (Roads != null)
                mat.SetTexture("_SolSatRoad", Roads.Texture);
            bool roads = settings.RoadsEnabled;
            mat.SetVector(RoadParamsId, new Vector4(Roads != null && roads ? settings.RoadStrength : 0f, 0f, 0f, 0f));
            mat.SetVector(AxisEId, eastParam);
            mat.SetVector(AxisNId, northParam);
            mat.SetVector(AxisUId, upParam);
            mat.SetVector(RefId, refParam);
            mat.SetVector(RefPlaneId, refPlaneParam);
            mat.SetVectorArray(LevelsId, levelParams);
            mat.SetVectorArray(LevelFracId, levelFrac);
        }

        /// <summary>One line per active level, for the settings window.</summary>
        public void Describe(StringBuilder sb)
        {
            foreach (Level l in levels)
            {
                if (!l.Active || !l.Valid)
                    continue;
                int drawn = 0;
                foreach (long key in l.Tiles)
                    if (l.Drawn.Contains(key))
                        drawn++;
                double texel = l.Width / size;
                string width = l.Width < 1000.0 ? $"{l.Width:0} м" : $"{l.Width / 1000.0:0.#} км";
                string sharp = texel < 1.0 ? $"{texel * 100.0:0.#} см" : $"{texel:0.#} м";
                sb.AppendLine($"  {width}, ~{sharp}/пикс, z{l.Zoom}: {drawn}/{l.Tiles.Count}");
            }
        }

        /// <summary>The levels in use, for the log: finest and coarsest, with their zooms.</summary>
        public string Summary
        {
            get
            {
                Level first = null, last = null;
                int count = 0, drawn = 0, total = 0;
                foreach (Level l in levels)
                {
                    if (!l.Active || !l.Valid)
                        continue;
                    first ??= l;
                    last = l;
                    count++;
                    total += l.Tiles.Count;
                    foreach (long key in l.Tiles)
                        if (l.Drawn.Contains(key))
                            drawn++;
                }
                if (first == null)
                    return "no levels";
                return $"{count} levels {first.Width:0} m (z{first.Zoom}) .. {last.Width / 1000.0:0} km (z{last.Zoom}), " +
                       $"{size} texels, {drawn}/{total} tiles drawn, camera {CameraHeight:0} m over the ground";
            }
        }

        /// <summary>Tiles the clipmap would use around the camera, out to radiusMeters, for
        /// downloading ahead of time.</summary>
        public List<long> AreaTiles(double radiusMeters)
        {
            var keys = new HashSet<long>();
            if (plane == null)
                return new List<long>();
            plane.Project(Geo.Direction(camLat, camLon), out double camX, out double camY);
            foreach (Level l in levels)
            {
                if (l.Index >= settings.Levels || !(l.Width > 0.0))
                    continue;
                int zoom = ZoomOf(l, camLat);
                double half = Math.Min(radiusMeters, l.Width);
                foreach (long key in TilesInRect(camX - half, camY - half, camX + half, camY + half, zoom))
                    keys.Add(key);
            }
            var list = new List<long>(keys);
            list.Sort((a, b) => (a >> 42).CompareTo(b >> 42)); // coarse zooms first
            return list;
        }

        void Reset(Level l, double cx, double cy, int zoom)
        {
            l.CenterX = cx;
            l.CenterY = cy;
            l.Zoom = zoom;
            l.Valid = true;
            l.Version++;
            l.Drawn.Clear();
            neededSent = false;
            // Every mip: until the chain is rebuilt (MipsInterval) the coarser mips - what far
            // and slanting ground samples - would still show what the level had before.
            RenderTexture previous = RenderTexture.active;
            for (int m = 0; m < mips; m++)
            {
                Graphics.SetRenderTarget(Texture, m, CubemapFace.Unknown, l.Index);
                GL.Clear(false, true, Color.clear);
            }
            RenderTexture.active = previous;
            RebuildTileList(l);
        }

        void Shift(Level l, double cx, double cy)
        {
            Vector4 oldWindow = l.Window;
            l.PreviousWindow = oldWindow;
            double half = l.Width * 0.5;
            // Tiles drawn completely inside the old window are still valid where the windows overlap.
            var keep = new HashSet<long>();
            foreach (long key in l.Drawn)
                if (TileInside(key, l.CenterX - half, l.CenterY - half, l.CenterX + half, l.CenterY + half))
                    keep.Add(key);
            l.Drawn = keep;
            l.CenterX = cx;
            l.CenterY = cy;
            l.Version++;
            l.MovedAt = l.Version;
            neededSent = false;

            // In every mip: the texels of newly exposed ground hold the ground that just left
            // the window on the other side (the level wraps round). Cleared in mip 0 only, the
            // coarser mips - all of the ground but the nearest - showed that other place there
            // until the chain was rebuilt (2026-10-03: "the image changes to another and back").
            RenderTexture previous = RenderTexture.active;
            tileMaterial.SetVector(WindowId, l.Window);
            tileMaterial.SetVector(OldWindowId, oldWindow);
            for (int m = 0; m < mips; m++)
            {
                Graphics.SetRenderTarget(Texture, m, CubemapFace.Unknown, l.Index);
                tileMaterial.SetPass(1);
                GL.Begin(GL.QUADS);
                GL.Vertex3(0f, 0f, 0f);
                GL.Vertex3(1f, 0f, 0f);
                GL.Vertex3(1f, 1f, 0f);
                GL.Vertex3(0f, 1f, 0f);
                GL.End();
            }
            RenderTexture.active = previous;
            RebuildTileList(l);
        }

        void RebuildTileList(Level l)
        {
            double half = l.Width * 0.5;
            l.Tiles.Clear();
            l.Tiles.AddRange(TilesInRect(l.CenterX - half, l.CenterY - half, l.CenterX + half, l.CenterY + half, l.Zoom));
        }

        /// <summary>Tiles of a zoom level covering a plane rectangle, nearest to the camera first.</summary>
        List<long> TilesInRect(double x0, double y0, double x1, double y1, int zoom)
        {
            // Range from points around the border, longitudes unwrapped around the anchor.
            double world = Geo.WorldPixels(zoom);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            const int samples = 16;
            for (int i = 0; i <= samples; i++)
            {
                double f = (double)i / samples;
                double px = x0 + (x1 - x0) * f, py = y0 + (y1 - y0) * f;
                foreach (var p in new[] { (px, y0), (px, y1), (x0, py), (x1, py) })
                {
                    Geo.LatLon(plane.Unproject(p.Item1, p.Item2), out double lat, out double lon);
                    lon = plane.Lon + WrapDegrees(lon - plane.Lon);
                    double mx = Geo.MercatorX(lon, world) / Geo.TileSize, my = Geo.MercatorY(lat, world) / Geo.TileSize;
                    minX = Math.Min(minX, mx);
                    maxX = Math.Max(maxX, mx);
                    minY = Math.Min(minY, my);
                    maxY = Math.Max(maxY, my);
                }
            }
            int n = 1 << zoom;
            double camMx = Geo.MercatorX(plane.Lon + WrapDegrees(camLon - plane.Lon), world) / Geo.TileSize;
            double camMy = Geo.MercatorY(camLat, world) / Geo.TileSize;
            var byDistance = new List<KeyValuePair<double, long>>();
            for (int ty = Math.Max(0, (int)Math.Floor(minY)); ty <= Math.Min(n - 1, (int)Math.Floor(maxY)); ty++)
            {
                for (int tx = (int)Math.Floor(minX); tx <= (int)Math.Floor(maxX); tx++)
                {
                    double dx = tx + 0.5 - camMx, dy = ty + 0.5 - camMy;
                    int x = ((tx % n) + n) % n;
                    byDistance.Add(new KeyValuePair<double, long>(dx * dx + dy * dy, Geo.TileKey(zoom, x, ty)));
                }
            }
            byDistance.Sort((a, b) => a.Key.CompareTo(b.Key));
            var result = new List<long>(byDistance.Count);
            foreach (var kv in byDistance)
                result.Add(kv.Value);
            return result;
        }

        /// <summary>Whether a tile lies entirely within a plane rectangle (checked on its outline).</summary>
        bool TileInside(long key, double x0, double y0, double x1, double y1)
        {
            int z = (int)(key >> 42), y = (int)((key >> 21) & 0x1FFFFF), x = (int)(key & 0x1FFFFF);
            double world = Geo.WorldPixels(z);
            for (int i = 0; i <= 2; i++)
            {
                for (int j = 0; j <= 2; j++)
                {
                    if (i == 1 && j == 1)
                        continue;
                    double lat = Geo.LatFromMercatorY((y + j * 0.5) * Geo.TileSize, world);
                    double lon = Geo.LonFromMercatorX((x + i * 0.5) * Geo.TileSize, world);
                    if (!plane.Project(Geo.Direction(lat, lon), out double px, out double py) ||
                        px < x0 || px > x1 || py < y0 || py > y1)
                        return false;
                }
            }
            return true;
        }

        void DrawTile(Level l, int z, int x, int y, Texture2D tex)
        {
            double world = Geo.WorldPixels(z);
            double inv = 1.0 / l.Width;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int j = 0; j <= TileGrid; j++)
            {
                double lat = Geo.LatFromMercatorY((y + (double)j / TileGrid) * Geo.TileSize, world);
                for (int i = 0; i <= TileGrid; i++)
                {
                    double lon = Geo.LonFromMercatorX((x + (double)i / TileGrid) * Geo.TileSize, world);
                    plane.Project(Geo.Direction(lat, lon), out double px, out double py);
                    int v = j * (TileGrid + 1) + i;
                    gridX[v] = px * inv;
                    gridY[v] = py * inv;
                    minX = Math.Min(minX, gridX[v]);
                    maxX = Math.Max(maxX, gridX[v]);
                    minY = Math.Min(minY, gridY[v]);
                    maxY = Math.Max(maxY, gridY[v]);
                }
            }

            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(Texture, 0, CubemapFace.Unknown, l.Index);
            tileMaterial.mainTexture = tex;
            tileMaterial.SetVector(WindowId, l.Window);
            tileMaterial.SetPass(0);
            // A tile that straddles the texture's wrap line is drawn once per side of it.
            for (int ox = (int)Math.Floor(minX); ox <= (int)Math.Floor(maxX); ox++)
            {
                for (int oy = (int)Math.Floor(minY); oy <= (int)Math.Floor(maxY); oy++)
                {
                    GL.Begin(GL.QUADS);
                    for (int j = 0; j < TileGrid; j++)
                    {
                        for (int i = 0; i < TileGrid; i++)
                        {
                            Vertex(i, j, ox, oy);
                            Vertex(i + 1, j, ox, oy);
                            Vertex(i + 1, j + 1, ox, oy);
                            Vertex(i, j + 1, ox, oy);
                        }
                    }
                    GL.End();
                }
            }
            RenderTexture.active = previous;
            DrawnTiles++;
        }

        void Vertex(int i, int j, int ox, int oy)
        {
            int v = j * (TileGrid + 1) + i;
            // Texture v runs bottom-up in Unity, while tile rows (and Mercator y) run top-down.
            GL.TexCoord2((float)i / TileGrid, 1f - (float)j / TileGrid);
            GL.MultiTexCoord2(1, (float)gridX[v], (float)gridY[v]);
            GL.Vertex3((float)(gridX[v] - ox), (float)(gridY[v] - oy), 0f);
        }

        double GroundDistance(double lat, double lon)
        {
            double cosAngle = Vector3d.Dot(Geo.Direction(lat, lon), plane.Up);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosAngle))) * plane.Radius;
        }

        static double WrapDegrees(double d)
        {
            d %= 360.0;
            if (d > 180.0)
                d -= 360.0;
            else if (d < -180.0)
                d += 360.0;
            return d;
        }

        // ------------------------------------------------------------------ night light map

        public bool LevelReady(int k) => levels[k].Active && levels[k].Valid;

        /// <summary>The window of the coarsest level in use, plane metres (centre, half width).</summary>
        public bool CoarsestWindow(out double cx, out double cy, out double half)
        {
            for (int k = MaxLevels - 1; k >= 0; k--)
            {
                Level l = levels[k];
                if (!l.Active || !l.Valid)
                    continue;
                cx = l.CenterX;
                cy = l.CenterY;
                half = l.Width * 0.5;
                return true;
            }
            cx = cy = half = 0.0;
            return false;
        }
        public int LevelVersion(int k) => levels[k].Version;
        public double LevelWidth(int k) => levels[k].Width;

        /// <summary>Level k's window in absolute level coordinates: (min x, min y, max x, max y).</summary>
        public Vector4 LevelWindow(int k) => levels[k].Window;

        /// <summary>Whether level k's only change since version was a single move of its window,
        /// and where the window was before it.</summary>
        public bool MovedOnceSince(int k, int version, out Vector4 oldWindow)
        {
            Level l = levels[k];
            oldWindow = l.PreviousWindow;
            return l.Version == version + 1 && l.MovedAt == l.Version;
        }

        public void Dispose()
        {
            Texture.Release();
            Lights.Dispose();
            Roads?.Dispose();
            Mask?.Dispose();
            UnityEngine.Object.Destroy(quad);
            UnityEngine.Object.Destroy(Texture);
            UnityEngine.Object.Destroy(tileMaterial);
        }
    }
}
