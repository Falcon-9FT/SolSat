// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// SolSatellite: swaps Earth's orbital color cubemap while the game is running.
//
// "solsat live" (PluginData/Tools) writes a new cube file every time more imagery has
// arrived and names it in PluginData/Textures/current.txt. This addon polls that file and
// hands the new cube to Parallax:
//  * it points Parallax's _ColorCube path at the new file, so Parallax's own next load of
//    Earth's scaled textures uses it;
//  * if those textures are loaded right now, it loads the new cube asynchronously and puts
//    it in place of Parallax's handle. Parallax's unload/reload keeps working and only one
//    copy of the cube stays in memory.
// It never touches terrain textures (ParallaxScaledBody.Unload would unload them too).
//
// It also colours Earth's ground (with the SolSatellite build of Parallax's terrain shader):
//  * everywhere from that same cube, and
//  * near the camera from a clipmap of web-map tiles streamed while playing (Clipmap.cs),
// refreshing the transforms every frame since the planet rotates and KSP keeps moving the
// world origin.
//
// Buildings and lamps are loaded around two points (Focus.cs): the camera, and the
// vessel being flown, so what is near the vessel stays while the camera zooms out. In map
// view the camera's point follows the vessel, since that is where the camera will be back.

using System;
using System.Collections;
using System.IO;
using KSPTextureLoader;
using Parallax;
using UnityEngine;

namespace SolSatellite
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class LiveColorCube : MonoBehaviour
    {
        const string BodyName = "Earth";
        const string Property = "_ColorCube";
        const string TexturesPath = "SolSatellite/PluginData/Textures/";
        const int LocalSceneryLayer = 15;

        // Set in Awake: KSPUtil.ApplicationRootPath is Application.dataPath, which Unity forbids while it
        // constructs the component (static initializers run then) - a development player throws, and the
        // whole mod was gone (2026-10-17, KSP in dev mode).
        static string GameData, ModDir, PointerFile;

        static readonly int SatCubeId = Shader.PropertyToID("_SolSatCube");
        static readonly int SatWorldToPlanetId = Shader.PropertyToID("_SolSatWorldToPlanet");
        static readonly int SatParamsId = Shader.PropertyToID("_SolSatParams");
        static readonly int SatNightId = Shader.PropertyToID("_SolSatNight");
        const float StreetLight = 0.8f; // light map strength on the terrain at 100% lamp brightness

        string applied;
        string loadingPath;
        TextureHandle<Texture> loading;
        int lastTerrainFrame = -1;

        Settings settings;
        Shader tileShader, buildingShader, lampShader, orbitShader, road3DShader, heightShader;
        OrbitDetail orbit;
        HeightField heightField; // the terrain as drawn, for the 3D roads
        Texture anisoCube; // the orbital cube whose filtering was set
        DiskCache diskCache;
        TileStreamer streamer;
        int tileCap = int.MaxValue;   // tile textures held, while the card is short of memory (UpdateTileCap)
        float nextCapChange;
        Clipmap clipmap;
        BuildingManager buildings;
        DemTerrain terrain;   // real relief laid into Earth's PQS
        readonly RoadLayer roads = new RoadLayer(c => c.EnsureRoads()); // OSM roads over the imagery
        readonly RoadLayer scatterMaskLayer = new RoadLayer(c => c.EnsureMask()); // where Parallax's scatters belong
        ComputeShader scatterCompute; // our build of Parallax's TerrainScatters (ScatterMask)
        SettingsWindow window;
        Vector3d cameraWorld;
        readonly Material[] terrainMaterials = new Material[6]; // Parallax's, this frame
        Focus cameraFocus;    // where the terrain camera was when it last rendered
        Focus vesselAtCamera; // ... and where the vessel was then
        CelestialBody earth;
        int cameraFrame = -10;
        string cameraName;
        float nextStatusLog, nextPerfLog;
        bool sceneLoaded = true; // created in the main menu, which is loaded by then
        float sceneSettledAt;

        internal Settings Settings => settings;
        internal TileStreamer Streamer => streamer;
        internal Clipmap Clipmap => clipmap;
        internal BuildingManager Buildings => buildings;
        internal DiskCache Cache => diskCache;
        internal DemTerrain Terrain => terrain;
        internal bool BuildingShaderMissing => buildingShader == null;

        /// <summary>The terrain camera rendered last frame (not in map view, menus, ...). In map
        /// view some other camera (Scatterer's, EVE's) may render the terrain layer from far away:
        /// taking its position for the player's would unload everything around the vessel.</summary>
        bool TerrainVisible => !MapView.MapIsEnabled && Time.frameCount - cameraFrame <= 1;

        CelestialBody Earth => earth != null ? earth : earth = FlightGlobals.GetBodyByName(BodyName);

        /// <summary>No scene switch pending and the new scene has had a moment to settle: only
        /// then may we ask the PQS for heights (Kopernicus may load its maps on demand, which
        /// isn't allowed during a switch).</summary>
        bool SceneSettled =>
            sceneLoaded && Time.realtimeSinceStartup >= sceneSettledAt &&
            (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ready);

        /// <summary>Set when another mod draws Earth's ground (the window says so at the top).</summary>
        internal string Conflict { get; private set; }
        bool mirageEarth; // Mirage draws Earth: its imagery and relief stay, ours aren't loaded

        /// <summary>Mirage puts its own imagery and relief (GPU-tessellated) on Earth: ours would lie
        /// under it. Its PQSMods are on Earth's PQS from the game's loading on.</summary>
        static string MirageOnEarth()
        {
            CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null || body.pqsController == null)
                return null;
            foreach (PQSMod mod in body.pqsController.GetComponentsInChildren<PQSMod>(true))
                if (mod.GetType().Name.StartsWith("PQSMod_Mirage", StringComparison.Ordinal))
                    return mod.GetType().Name;
            return null;
        }

        internal string StreamStatus
        {
            get
            {
                if (mirageEarth)
                    return "не грузятся: снимки и рельеф Земли рисует Mirage";
                if (settings == null || !settings.StreamEnabled)
                    return "выключены";
                if (tileShader == null)
                    return "не работают: нет шейдера SolSatellite (см. KSP.log)";
                if (!TerrainScene)
                    return "ждут полёта или космоцентра";
                if (clipmap == null || !cameraFocus.Valid)
                    return "ждут камеру";
                if (!clipmap.Active)
                    return $"не нужны на такой высоте ({clipmap.CameraAltitude / 1000.0:0} км)";
                string where = TerrainVisible ? "высота камеры" : "карта открыта, камера вернётся на";
                string fast = clipmap.CameraSpeed > 1000.0 ? $"; скорость {clipmap.CameraSpeed / 1000.0:0.#} км/с — мелкие уровни только из кэша" : "";
                return $"работают, {where} {clipmap.CameraAltitude:0} м{fast}";
            }
        }

        void Awake()
        {
            GameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
            ModDir = Path.Combine(GameData, "SolSatellite");
            PointerFile = Path.Combine(GameData, TexturesPath + "current.txt");
            DontDestroyOnLoad(gameObject);
            FrameProfile.Install(); // where the whole frame goes (Нагрузка tab, log)
            Camera.onPreCull += OnCameraPreCull;
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneLoaded);
            try
            {
                settings = Settings.Load(Path.Combine(ModDir, "PluginData", "Tools", "settings.ini"));
                // "solsat disable" renames the patch; then the terrain shader isn't ours either.
                if (!File.Exists(Path.Combine(ModDir, "SolSatellite.cfg")))
                    settings.StreamEnabled = false;
                string mirageMod = MirageOnEarth();
                if (mirageMod != null)
                {
                    mirageEarth = true;
                    Conflict = "Найден мод Mirage: снимки и рельеф Земли рисует он, SolSat свои не грузит. От SolSat " +
                               "остаются здания, фонари и объёмные дороги. Чтобы вернуть снимки и рельеф SolSat, удалите Mirage.";
                    Debug.LogWarning($"[SolSatellite] Mirage draws Earth ({mirageMod} on its PQS): its imagery and relief stay; " +
                                     "this mod loads neither and adds buildings, lamps and 3D roads only");
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(Path.Combine(ModDir, "Shaders", "SolSatellite-windows.unity3d"));
                if (bundle != null)
                    foreach (ComputeShader cs in bundle.LoadAllAssets<ComputeShader>())
                        if (cs.name == "TerrainScatters")
                            scatterCompute = cs;
                if (bundle != null)
                    foreach (Shader shader in bundle.LoadAllAssets<Shader>())
                    {
                        if (shader.name == "SolSatellite/TileProject")
                            tileShader = shader;
                        else if (shader.name == "SolSatellite/Building")
                            buildingShader = shader;
                        else if (shader.name == "SolSatellite/Lamp")
                            lampShader = shader;
                        else if (shader.name == "SolSatellite/OrbitDetail")
                            orbitShader = shader;
                        else if (shader.name == "SolSatellite/Road3D")
                            road3DShader = shader;
                        else if (shader.name == "SolSatellite/HeightCapture")
                            heightShader = shader;
                        else if (shader.name == "SolSatellite/PlanetCityLight")
                            ScaledCityLights.Setup(shader);
                    }
                if (tileShader == null)
                    Debug.LogError("[SolSatellite] TileProject shader not found; ground imagery streaming disabled");
                if (buildingShader == null)
                    Debug.LogError("[SolSatellite] Building shader not found; OSM buildings disabled");
                if (lampShader == null)
                    Debug.LogError("[SolSatellite] Lamp shader not found; street lamps disabled");
                diskCache = new DiskCache(Path.Combine(ModDir, "PluginData", "Cache"));
                window = new SettingsWindow(this);
                try
                {
                    ParallaxCollisionFix.Apply(); // Parallax's scatter collider race (crashes)
                    ScatterGhosts.Apply(); // scatters placed anew don't blink meanwhile
                    ParallaxDecalFix.Apply(); // stale entries of Parallax's launch site decals ("Multiple MapDecal...")
                    ScatterMask.Setup(scatterCompute, settings); // Parallax's scatters where the map says
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SolSatellite] Parallax patches unavailable (Harmony missing?): " + e.Message);
                }
                try
                {
                    // Before the space centre starts Earth's PQS: the mod has to be there by then.
                    // Not under Mirage: buildings then stand on its relief (the PQS heights).
                    if (!mirageEarth)
                    {
                        terrain = new DemTerrain(settings, Path.Combine(ModDir, "PluginData", "Cache", "dem"));
                        terrain.Changed += caps => buildings?.TerrainChanged(caps); // what stands on the ground follows it
                        terrain.Attach();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void OnGUI()
        {
            window?.OnGUI(); // the window times its own drawing (Perf.Part.Window)
        }

        void OnDestroy()
        {
            ModProfiler.Abort();
            FrameProfile.Uninstall();
            Camera.onPreCull -= OnCameraPreCull;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneLoaded);
            window?.Dispose();
            EveCityLights.Restore();
            buildings?.Dispose();
            terrain?.Dispose();
            heightField?.Dispose();
            orbit?.Dispose();
            clipmap?.Dispose();
            streamer?.Dispose();
        }

        void OnSceneLoadRequested(GameScenes scene)
        {
            sceneLoaded = false;
            cameraFrame = -10; // whatever camera rendered last belongs to the old scene
            cameraFocus = vesselAtCamera = default;
            try
            {
                ModProfiler.Stop("смена сцены"); // a recording under way: its report now, while the old scene is still there
                ScatterGhosts.RetireAll();
                terrain?.OnSceneSwitch();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void OnSceneLoaded(GameScenes scene)
        {
            sceneLoaded = true;
            sceneSettledAt = Time.realtimeSinceStartup + 2f;
        }

        static bool TerrainScene => HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.SPACECENTER;

        void LateUpdate()
        {
            ModProfiler.Update(); // a mod profile being recorded (Нагрузка tab)
            if (settings == null)
                return;
            Perf.NextFrame();
            Perf.Begin(Perf.Part.Other);
            diskCache?.Update(settings);
            ScatterGhosts.Update();
            VideoMemory.Update();
            UpdateTileCap();
            try
            {
                SiteGrounds.Update(ModDir); // ground under KSC and the pads placed here, from the main menu on
                ScaledCityLights.Update();  // EVE's city lights from orbit over Parallax's raised land
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            Perf.End(Perf.Part.Other);
            if (!TerrainScene)
                return;
            LogPerf();
            CelestialBody body = Earth;
            if (body == null || !SceneSettled || !cameraFocus.Valid)
            {
                streamer?.Update(); // requests in flight finish; nothing new is asked for
                return;
            }
            // Map view and the like: the terrain camera is away, and comes back where it was
            // around the vessel. Nothing near it unloads meanwhile, and nothing is fetched for
            // places the vessel has left.
            Focus vessel = Focus.OfVessel(body);
            Focus camera = TerrainVisible ? cameraFocus : Focus.Follow(cameraFocus, vesselAtCamera, vessel, body.Radius);
            if (terrain != null)
            {
                Perf.Begin(Perf.Part.Terrain);
                try
                {
                    terrain.Update(camera, vessel);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                Perf.End(Perf.Part.Terrain);
            }
            if (TerrainVisible)
            {
                Perf.Begin(Perf.Part.Other);
                try
                {
                    EveCityLights.Update(settings, camera.Alt);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                Perf.End(Perf.Part.Other);
            }
            Perf.Begin(Perf.Part.City);
            if (buildingShader != null && (settings.BuildingsEnabled || settings.LampsEnabled || settings.RoadsEnabled))
            {
                try
                {
                    buildings ??= new BuildingManager(body, settings, buildingShader, lampShader, road3DShader,
                                                      Path.Combine(ModDir, "PluginData", "Cache", "osm"), roads, scatterMaskLayer);
                    buildings.Update(camera, vessel);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    settings.BuildingsEnabled = settings.LampsEnabled = false; // don't spam the log every frame
                }
            }
            else
            {
                buildings?.Update(camera, vessel); // unloads what was built
            }
            Perf.End(Perf.Part.City);
            if (heightShader != null && road3DShader != null)
            {
                // The terrain under the 3D roads, as drawn (only while the terrain camera is on).
                Perf.Begin(Perf.Part.City);
                Perf.Begin(Perf.Part.RoadHeights);
                heightField ??= new HeightField(body, heightShader);
                if (TerrainVisible || !settings.Roads3D)
                    heightField.Update(camera, Math.Min(settings.Road3DRadiusKm * 1000.0, 3000.0), settings.Roads3D && buildings != null); // further out roads keep their own heights
                Perf.End(Perf.Part.RoadHeights);
                Perf.End(Perf.Part.City);
            }
            if (!settings.StreamEnabled || tileShader == null || mirageEarth)
            {
                buildings?.DrawLights(null); // no light map: lets go of what waited to be erased from it
                return;
            }
            try
            {
                if (clipmap != null && clipmap.LevelTexels != Settings.ClampLevelTexels(settings.LevelSize))
                {
                    // Texels per level changed in the settings window: new textures (the tiles stay loaded).
                    clipmap.Dispose();
                    clipmap = null;
                }
                if (clipmap == null)
                {
                    streamer ??= new TileStreamer(settings, Path.Combine(ModDir, "PluginData", "Cache", "tiles"));
                    clipmap = new Clipmap(settings, streamer, body, tileShader);
                    Debug.Log($"[SolSatellite] Streaming ground imagery from '{settings.SourceName}' up to zoom {settings.StreamMaxZoom}, " +
                              $"{clipmap.LevelTexels} texels per level (~{clipmap.Bytes / 1048576} MB of video memory so far)");
                }
                Perf.Begin(Perf.Part.Imagery);
                clipmap.Update(camera);
                if (orbitShader != null)
                {
                    orbit ??= new OrbitDetail(body, orbitShader);
                    orbit.Update(clipmap, settings.OrbitDetail && TerrainVisible);
                }
                Perf.End(Perf.Part.Imagery);
                Perf.Begin(Perf.Part.City);
                Perf.Begin(Perf.Part.LightMap);
                buildings?.DrawLights(clipmap); // streets lit at night, in the terrain
                Perf.End(Perf.Part.LightMap);
                Perf.Begin(Perf.Part.Roads);
                // Roads over the imagery.
                roads.Draw(clipmap, settings.RoadsEnabled);
                Perf.End(Perf.Part.Roads);
                // Where Parallax's scatters belong; quads whose map arrived get theirs placed again.
                Perf.Begin(Perf.Part.Scatters);
                scatterMaskLayer.Draw(clipmap, settings.ScatterMask && ScatterMask.Ready);
                ScatterMask.Update(clipmap, body);
                Perf.End(Perf.Part.Scatters);
                Perf.End(Perf.Part.City);
                if (Time.realtimeSinceStartup > nextStatusLog && clipmap.Active)
                {
                    nextStatusLog = Time.realtimeSinceStartup + 30f;
                    Debug.Log($"[SolSatellite] clipmap: {clipmap.DrawnTiles} tiles drawn, {streamer.Downloaded} downloaded, {streamer.Queued} queued, " +
                              $"{streamer.InFlight} loading, {streamer.Cancelled} cancelled (left behind); {clipmap.Summary}");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                settings.StreamEnabled = false; // don't spam the log every frame
            }
        }

        /// <summary>Tile textures are only kept to draw tiles into the clipmap again (they come back
        /// from the disk cache): while the card is short of memory (VideoMemory.Short) fewer are
        /// held, a third less every 5 s down to 256; with room again, more, up to "Тайлов в памяти".
        /// 2026-10-03, Vnukovo: 4 GB of the game's textures and meshes in main memory, 6 frames a second.</summary>
        void UpdateTileCap()
        {
            if (streamer == null || !VideoMemory.Available)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < nextCapChange)
                return;
            int was = tileCap;
            if (VideoMemory.Short)
            {
                tileCap = Math.Max(256, Math.Min(tileCap, Math.Max(streamer.Resident, 256)) * 2 / 3);
                nextCapChange = now + 5f;
            }
            else if (tileCap != int.MaxValue && VideoMemory.Used < VideoMemory.Budget * 0.8 && VideoMemory.Spilled < 512L << 20)
            {
                tileCap = tileCap * 5 / 4 + 64;
                if (tileCap >= settings.MemoryTiles)
                    tileCap = int.MaxValue;
                nextCapChange = now + 10f;
            }
            streamer.ResidentCap = tileCap;
            if (tileCap != was)
                Debug.Log($"[SolSatellite] Video memory: {VideoMemory.Summary}; tile textures held at most: " +
                          (tileCap == int.MaxValue ? $"{settings.MemoryTiles} (as set)" : tileCap.ToString()));
        }

        /// <summary>Every 30 s: where the time goes and how much is loaded, for finding lag.</summary>
        void LogPerf()
        {
            if (Time.realtimeSinceStartup < nextPerfLog)
                return;
            nextPerfLog = Time.realtimeSinceStartup + 30f;
            string city = buildings == null ? "" :
                $"; osm tiles {buildings.TilesTotal}, buildings {buildings.BuildingCount} in {buildings.BuildingTiles} tiles " +
                $"({buildings.BuildingVertices / 1e6:0.00} M vertices, shadows from {buildings.ShadowTiles}), " +
                $"lamps {buildings.LampCount} in {buildings.LampTiles} tiles, pools {buildings.PoolCount}";
            string imagery = streamer == null ? "" : $"; tile textures {streamer.Resident} (~{streamer.ResidentBytes / 1048576} MB)";
            Debug.Log("[SolSatellite] perf: " + Perf.Summary() + city + imagery);
            Debug.Log("[SolSatellite] frame: " + FrameProfile.Summary());
            if (VideoMemory.Available)
                Debug.Log("[SolSatellite] video memory: " + VideoMemory.Summary + (VideoMemory.Short ? " - SHORT, the card slows down" : "") +
                          (tileCap != int.MaxValue ? $"; tile textures held at most {tileCap}" : ""));
            if (buildings != null)
            {
                Debug.Log("[SolSatellite] loading around: " + buildings.Coverage);
                Debug.Log("[SolSatellite] osm pipeline: " + buildings.PipelineStatus);
                Debug.Log("[SolSatellite] osm throughput " + buildings.Throughput());
                Debug.Log("[SolSatellite] scatter mask: " + ScatterMask.Status +
                          (ParallaxCollisionFix.Active ? $"; Parallax collision fix on ({ParallaxCollisionFix.Repairs} repairs)" : "") +
                          (ScatterGhosts.Active ? $"; placed anew without blinking {ScatterGhosts.Bridged} (waiting {ScatterGhosts.Count})" : "") +
                          (ParallaxDecalFix.Active ? $"; decal table entries set right {ParallaxDecalFix.Repairs}" : ""));
            }
        }

        /// <summary>The camera the player looks through. Other cameras draw the terrain too -
        /// reflection probes, hull cameras, mods' helper cameras rendering into textures from
        /// wherever - and taking one of them for the player's would load the city elsewhere.
        /// In flight that is KSP's flight camera; elsewhere the first on-screen game camera.</summary>
        static bool PlayerCamera(Camera cam)
        {
            if (HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
                return cam == FlightCamera.fetch.mainCamera;
            return cam.cameraType == CameraType.Game && cam.targetTexture == null;
        }

        void OnCameraPreCull(Camera cam)
        {
            if ((cam.cullingMask & (1 << LocalSceneryLayer)) == 0 || Time.frameCount == lastTerrainFrame || MapView.MapIsEnabled ||
                !PlayerCamera(cam))
                return;
            lastTerrainFrame = Time.frameCount;
            if (cam.name != cameraName)
            {
                cameraName = cam.name;
                Debug.Log($"[SolSatellite] Following camera '{cam.name}'");
            }
            cameraWorld = cam.transform.position;
            cameraFrame = Time.frameCount;
            try
            {
                CelestialBody body = Earth;
                if (body != null)
                {
                    cameraFocus = Focus.At(body, cameraWorld);
                    vesselAtCamera = Focus.OfVessel(body);
                }
                Perf.Begin(Perf.Part.Materials);
                UpdateTerrainMaterials();
                Perf.End(Perf.Part.Materials);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void UpdateTerrainMaterials()
        {
            if (!ConfigLoader.parallaxTerrainBodies.TryGetValue(BodyName, out ParallaxTerrainBody terrain))
                return;
            CelestialBody body = Earth;
            if (body == null || body.pqsController == null)
                return;

            Texture cube = null;
            // HasProperty: without the baked cube (no cube patch) Earth's scaled shader has no _ColorCube, and
            // GetTexture would log an error every frame.
            if (ConfigLoader.parallaxScaledBodies.TryGetValue(BodyName, out ParallaxScaledBody scaled) && scaled.Loaded &&
                scaled.scaledMaterial.HasProperty(Property))
                cube = scaled.scaledMaterial.GetTexture(Property);
            if (cube != null && cube != anisoCube)
            {
                // Seen from orbit the ground is mostly at a slant: without anisotropic filtering
                // the cube goes to mush towards the horizon.
                anisoCube = cube;
                cube.anisoLevel = 9;
                if (QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable)
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
                Debug.Log($"[SolSatellite] Orbital cube {cube.width}x{cube.height}, mips {cube.mipmapCount}; texture quality limit {QualitySettings.masterTextureLimit}");
            }
            Matrix4x4 worldToPlanet = body.pqsController.transform.worldToLocalMatrix;
            bool clipOn = clipmap != null && TerrainScene && settings.StreamEnabled && clipmap.PrepareShaderParams(cameraWorld);
            Vector4 parameters = new Vector4(cube != null ? 1f : 0f, settings?.ImageryStrength ?? 1f, clipOn ? 1f : 0f, 0f);
            double sun = Sky.SunElevation(body, cameraWorld);
            // Streets lit by the lamps' pools (the terrain's night light map).
            float lampRadius = settings.LampRadiusKm * 1000f;
            Vector4 night = new Vector4(settings.LampsEnabled ? Sky.Lights(sun) : 0f, settings.LampBrightness * StreetLight,
                                        lampRadius, 1f / Mathf.Max(0.15f * lampRadius, 50f));
            Shader.SetGlobalVector(SatNightId, night); // buildings: the lamps light their walls

            ParallaxMaterials m = terrain.parallaxMaterials;
            terrainMaterials[0] = m.parallaxLow;
            terrainMaterials[1] = m.parallaxMid;
            terrainMaterials[2] = m.parallaxHigh;
            terrainMaterials[3] = m.parallaxLowMid;
            terrainMaterials[4] = m.parallaxMidHigh;
            terrainMaterials[5] = m.parallaxFull;
            foreach (Material mat in terrainMaterials)
            {
                if (mat == null)
                    continue;
                if (cube != null)
                    mat.SetTexture(SatCubeId, cube);
                mat.SetMatrix(SatWorldToPlanetId, worldToPlanet);
                mat.SetVector(SatParamsId, parameters);
                mat.SetVector(SatNightId, night);
                if (clipOn)
                    clipmap.Apply(mat);
            }
            if (heightField != null)
            {
                Perf.Begin(Perf.Part.RoadHeights);
                heightField.RenderPending();
                Perf.End(Perf.Part.RoadHeights);
                heightField.Apply();
            }
            buildings?.UpdateMaterial(clipmap, clipOn, settings.ImageryStrength, cameraWorld);
            if (orbit != null && clipOn)
                orbit.UpdateMaterial(clipmap, cube, worldToPlanet);
        }

        IEnumerator Start()
        {
            // Realtime, so it keeps working while the game is paused.
            var wait = new WaitForSecondsRealtime(3f);
            while (true)
            {
                try
                {
                    Poll();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                yield return wait;
            }
        }

        void Poll()
        {
            if (!ConfigLoader.parallaxScaledBodies.TryGetValue(BodyName, out ParallaxScaledBody body))
                return;
            var textures = body.scaledMaterialParams.shaderProperties.shaderTextures;
            // Only act while the SolSatellite patch has put Earth on the cubemap shader.
            if (!textures.TryGetValue(Property, out string current))
                return;

            if (loading != null)
            {
                Finish(body);
                return;
            }

            string relative = ReadPointer();
            if (relative == null || relative == applied)
                return;
            string path = TexturesPath + relative;
            if (!File.Exists(Path.Combine(GameData, path)))
                return;
            if (path == current)
            {
                applied = relative;
                return;
            }

            textures[Property] = path;
            if (!body.Loaded || body.IsLoading)
            {
                // Earth's scaled textures aren't in memory; Parallax will load the new path itself.
                applied = relative;
                Debug.Log($"[SolSatellite] Earth imagery set to {relative}");
                return;
            }
            loading = TextureLoader.LoadTexture<Texture>(path, new TextureLoadOptions { Linear = false, Unreadable = true });
            loadingPath = relative;
        }

        void Finish(ParallaxScaledBody body)
        {
            if (!loading.IsComplete)
                return;
            TextureHandle<Texture> handle = loading;
            loading = null;
            applied = loadingPath;

            if (handle.IsError)
            {
                Debug.LogError($"[SolSatellite] Could not load {applied}");
                handle.Dispose();
                return;
            }
            if (!body.Loaded || body.IsLoading)
            {
                // Parallax unloaded or is reloading Earth meanwhile and will use the new path.
                handle.Dispose();
                return;
            }
            body.scaledMaterial.SetTexture(Property, handle.GetTexture());
            if (body.loadedTextures.TryGetValue(Property, out TextureHandle old))
                old.Dispose();
            body.loadedTextures[Property] = handle;
            Debug.Log($"[SolSatellite] Earth imagery updated to {applied}");
        }

        static string ReadPointer()
        {
            try
            {
                return File.Exists(PointerFile) ? File.ReadAllText(PointerFile).Trim() : null;
            }
            catch (IOException)
            {
                return null; // being rewritten right now; try again on the next poll
            }
        }
    }
}
