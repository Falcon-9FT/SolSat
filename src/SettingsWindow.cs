// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KSP.UI.Screens;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>Toolbar button and settings window: one tab each for ground imagery,
    /// buildings, the real terrain, the disk cache and the load the mod puts on the game, in a
    /// fixed-size scrolling area.</summary>
    sealed class SettingsWindow : IDisposable
    {
        const int WindowId = 0x50153A7;
        const float Width = 440f;
        const float ContentHeight = 380f;
        const float LabelWidth = 230f;
        const string FieldPrefix = "SolSatNumber_";
        const string InputLock = "SolSatelliteTyping";
        static readonly string[] Tabs = { "Снимки", "Город", "Рельеф", "Кэш", "Нагрузка" };
        static readonly string[] EveModes = { "Как в Sol", "Ровное свечение", "Выключить" };
        const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW |
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.TRACKSTATION;

        readonly LiveColorCube owner;
        readonly StringBuilder sb = new StringBuilder();
        ApplicationLauncherButton button;
        Texture2D icon;
        GUIStyle header, text, muted, smallButton;
        bool visible, details, compact;
        int tab;
        Vector2 scroll;
        Rect rect = new Rect(260f, 120f, Width, 10f);
        float prefetchRadiusKm = 20f;
        string note;
        float noteUntil;
        readonly Dictionary<string, string> typed = new Dictionary<string, string>(); // number fields being edited
        bool inputLocked;
        DiskCache.Area confirmClear; // asked "sure?" about clearing this one

        public SettingsWindow(LiveColorCube owner)
        {
            this.owner = owner;
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
            if (ApplicationLauncher.Ready)
                AddButton();
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null)
                return;
            button = ApplicationLauncher.Instance.AddModApplication(
                () => visible = true, () => visible = false, null, null, null, null, Scenes, icon ??= MakeIcon());
        }

        void RemoveButton()
        {
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
            visible = false;
        }

        public void OnGUI()
        {
            if (!visible)
            {
                LockInput(false);
                return;
            }
            GUI.skin = HighLogic.Skin;
            if (header == null)
            {
                header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
                text = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
                muted = new GUIStyle(text);
                muted.normal.textColor = new Color(0.72f, 0.74f, 0.76f);
                smallButton = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = new RectOffset(6, 6, 2, 2) };
            }
            // While a mod profile is recorded the window shrinks to its status (DrawWindow).
            bool small = ModProfiler.Now != ModProfiler.Stage.Idle;
            if (small != compact)
            {
                compact = small;
                rect.height = 10f; // fits the new content
            }
            rect = GUILayout.Window(WindowId, rect, Draw, "SolSatellite", GUILayout.Width(Width));
            // While a number field has the keyboard, keys must not stage the rocket or fire action groups.
            if (Event.current.type == EventType.Repaint)
                LockInput(GUI.GetNameOfFocusedControl().StartsWith(FieldPrefix, StringComparison.Ordinal));
        }

        void LockInput(bool locked)
        {
            if (locked == inputLocked)
                return;
            inputLocked = locked;
            if (locked)
                InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, InputLock);
            else
                InputLockManager.RemoveControlLock(InputLock);
        }

        void Draw(int id)
        {
            // Unity calls this after OnGUI has returned, for every GUI event: timed here.
            Perf.Begin(Perf.Part.Window);
            try
            {
                DrawWindow();
            }
            finally
            {
                Perf.End(Perf.Part.Window);
            }
        }

        void DrawWindow()
        {
            if (compact)
            {
                // The full window is itself an OnGUI script of ~1 ms a frame (the "Нагрузка" tab
                // with every number): it would be in the profile it records.
                GUILayout.Label("Профиль по модам", header);
                GUILayout.Label(ModProfiler.Status, text);
                GUILayout.Label("Окно на время записи свёрнуто, чтобы само не попадать в замер.", muted);
                if (ModProfiler.Now != ModProfiler.Stage.Unpatching && GUILayout.Button("Остановить", GUILayout.Width(160f)))
                    ModProfiler.Stop();
                GUI.DragWindow();
                return;
            }
            int selected = GUILayout.Toolbar(tab, Tabs);
            if (selected != tab)
            {
                tab = selected;
                scroll = Vector2.zero;
            }
            GUILayout.Space(4f);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(ContentHeight));
            switch (tab)
            {
                case 0: DrawImagery(owner.Settings); break;
                case 1: DrawBuildings(owner.Settings); break;
                case 2: DrawTerrain(owner.Settings); break;
                case 3: DrawCache(owner.Settings); break;
                default: DrawLoad(owner.Settings); break;
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Сохранить настройки"))
            {
                try
                {
                    owner.Settings.Save();
                    Note("Сохранено в settings.ini");
                }
                catch (Exception e)
                {
                    Note("Не сохранилось: " + e.Message);
                }
            }
            if (GUILayout.Button("Закрыть", GUILayout.Width(100f)))
                button?.SetFalse();
            GUILayout.EndHorizontal();
            if (note != null && Time.realtimeSinceStartup < noteUntil)
                GUILayout.Label(note, muted);
            GUI.DragWindow();
        }

        void Note(string message)
        {
            note = message;
            noteUntil = Time.realtimeSinceStartup + 5f;
        }

        // ------------------------------------------------------------------ imagery

        void DrawImagery(Settings s)
        {
            TileStreamer streamer = owner.Streamer;
            Clipmap clipmap = owner.Clipmap;

            GUILayout.Label($"Источник: {s.SourceName}. Снимки у земли: {owner.StreamStatus}", text);
            s.StreamEnabled = GUILayout.Toggle(s.StreamEnabled, " Подгружать снимки у земли");
            if (streamer != null)
                streamer.Paused = GUILayout.Toggle(streamer.Paused, " Пауза загрузки из сети (кэш работает)");
            // Any numbers: if the server starts refusing, downloads pause by themselves and resume.
            s.StreamRequestsPerSecond = NumberRow("rps", "Запросов в секунду:", s.StreamRequestsPerSecond, 0.5f, float.MaxValue, "");
            s.StreamConnections = Mathf.RoundToInt(NumberRow("connections", "Соединений сразу:", s.StreamConnections, 1f, float.MaxValue, ""));
            s.StreamMaxZoom = (int)SliderRow(
                $"Детальность: z{s.StreamMaxZoom} (~{Geo.MercatorResZ0 / (1 << s.StreamMaxZoom):0.#} м/пикс)",
                s.StreamMaxZoom, 14f, Math.Min(20, s.SourceMaxZoom), true);
            // Zones: each level is as sharp as one zoom; more texels make every zone wider.
            s.LevelSize = Settings.ClampLevelTexels(Mathf.RoundToInt(NumberRow("texels", "Текселей на уровень:", s.LevelSize, 256f, 16384f, "")));
            GUILayout.Label($"  вдвое больше — зона каждого зума вдвое шире; видеопамять ~{Clipmap.TotalBytes(s.LevelSize) / 1048576} МБ " +
                            "(2048 — ~0,8 ГБ, 4096 — ~1,9 ГБ)", muted);
            s.DetailAltitude = NumberRow("detailalt", "Мелкие уровни до высоты:", s.DetailAltitude, 0.5f, float.MaxValue, "× ш.");
            GUILayout.Label("  уровень грузится, пока камера над землёй ниже стольких его ширин", muted);
            s.FinestLevelMeters = NumberRow("finest", "Самый мелкий уровень:", (float)s.FinestLevelMeters, 0f, float.MaxValue, "м");
            string finestNow = clipmap != null && clipmap.FinestLevelWidth > 0.0 ? $" (сейчас {clipmap.FinestLevelWidth:0} м)" : "";
            GUILayout.Label(s.FinestLevelMeters > 0.0
                ? $"  задан вручную{finestNow}; 0 — по детальности z{s.StreamMaxZoom}"
                : $"  0 — по детальности z{s.StreamMaxZoom}{finestNow}", muted);
            s.ImageryStrength = SliderRow($"Сила снимков: {s.ImageryStrength * 100f:0}%", s.ImageryStrength, 0f, 1f, false);
            s.OrbitDetail = GUILayout.Toggle(s.OrbitDetail, " Чётче из космоса: снимки поверх орбитальной текстуры (выше 130 км)");
            s.MemoryTiles = Mathf.RoundToInt(NumberRow("memtiles", "Тайлов в памяти:", s.MemoryTiles, 128f, float.MaxValue, ""));
            GUILayout.Label($"  до ~{s.MemoryTiles / 3} МБ видеопамяти", muted);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Перерисовать"))
                clipmap?.InvalidateAll();
            if (GUILayout.Button(details ? "Скрыть подробности" : "Подробнее"))
                details = !details;
            GUILayout.EndHorizontal();
            if (details)
            {
                if (clipmap != null && clipmap.Active)
                {
                    sb.Length = 0;
                    clipmap.Describe(sb);
                    if (sb.Length > 0)
                        GUILayout.Label("Уровни (ширина, детальность, тайлов готово):\n" + sb.ToString().TrimEnd(), muted);
                }
                if (streamer != null)
                {
                    GUILayout.Label($"Тайлы: нарисовано {clipmap?.DrawnTiles ?? 0}, скачано {streamer.Downloaded}, в очереди {streamer.Queued}, " +
                                    $"грузится {streamer.InFlight}, в памяти {streamer.Resident}, " +
                                    $"отменено {streamer.Cancelled} (остались позади)", muted);
                    GUILayout.Label("Сеть: " + streamer.NetworkStatus, muted);
                }
            }

            GUILayout.Space(6f);
            GUILayout.Label("Заранее скачать район вокруг камеры", header);
            prefetchRadiusKm = SliderRow($"Радиус: {prefetchRadiusKm:0} км", prefetchRadiusKm, 5f, 100f, true);
            if (streamer != null && streamer.Prefetching)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Скачано {streamer.PrefetchDone} из {streamer.PrefetchTotal}", text);
                if (GUILayout.Button("Отменить", GUILayout.Width(100f)))
                    streamer.CancelPrefetch();
                GUILayout.EndHorizontal();
            }
            else if (clipmap != null && clipmap.Active && streamer != null)
            {
                if (GUILayout.Button("Скачать"))
                    streamer.Prefetch(clipmap.AreaTiles(prefetchRadiusKm * 1000.0));
            }
            else
            {
                GUILayout.Label("Доступно, когда камера у Земли ниже 250 км", muted);
            }
        }

        // ------------------------------------------------------------------ buildings

        void DrawBuildings(Settings s)
        {
            if (owner.BuildingShaderMissing)
            {
                GUILayout.Label("Нет шейдера зданий: пересоберите Shaders (PluginData\\Source\\build_shaders.bat own)", text);
                return;
            }
            s.BuildingsEnabled = GUILayout.Toggle(s.BuildingsEnabled, " Здания вокруг камеры и аппарата");
            s.BuildingRadiusKm = NumberRow("buildings", "Радиус зданий:", s.BuildingRadiusKm, Settings.MinRadiusKm, float.MaxValue, "км");
            s.BuildingColliders = GUILayout.Toggle(s.BuildingColliders, " Твёрдые: в здания рядом с кораблём можно врезаться");
            s.LampsEnabled = GUILayout.Toggle(s.LampsEnabled, " Ночные фонари и освещённые улицы");
            s.LampRadiusKm = NumberRow("lamps", "Радиус фонарей:", s.LampRadiusKm, Settings.MinRadiusKm, float.MaxValue, "км");
            s.LampBrightness = SliderRow($"Яркость фонарей: {s.LampBrightness * 100f:0}%", s.LampBrightness, 0f, 3f, false);
            s.RoadsEnabled = GUILayout.Toggle(s.RoadsEnabled, " Дороги поверх снимков: асфальт и разметка");
            s.RoadRadiusKm = NumberRow("roads", "Радиус дорог:", s.RoadRadiusKm, Settings.MinRadiusKm, float.MaxValue, "км");
            s.RoadStrength = SliderRow($"Насколько дороги закрывают снимок: {s.RoadStrength * 100f:0}%", s.RoadStrength, 0f, 1f, false);
            s.Roads3D = GUILayout.Toggle(s.Roads3D, " 3D-дороги и ЖД рядом: асфальт, разметка, бордюры, тротуары, мосты, рельсы");
            s.Road3DRadiusKm = NumberRow("roads3d", "Радиус 3D-дорог:", s.Road3DRadiusKm, Settings.MinRadiusKm, float.MaxValue, "км");
            GUILayout.Label("Ночная подложка Sol (огни городов EVE) у земли, пока горят наши фонари:", text);
            s.EveCityLights = (Settings.EveCityLightsMode)GUILayout.Toolbar((int)s.EveCityLights, EveModes);
            s.VectorTiles = GUILayout.Toggle(s.VectorTiles, " Быстрая загрузка из OpenFreeMap");
            s.OverpassDetail = GUILayout.Toggle(s.OverpassDetail, " Догружать подробности из Overpass (типы зданий, фонари OSM)");
            s.OvertureEnabled = GUILayout.Toggle(s.OvertureEnabled, " Здания из Overture Maps: OSM со всеми тегами + контуры Microsoft и Google");
            s.OvertureConnections = (int)NumberRow("overtureConn", "Соединений Overture:", s.OvertureConnections, 1f, float.MaxValue, "");
            s.GuessRoofs = GUILayout.Toggle(s.GuessRoofs, " Скатные крыши у домов без тегов (где так строят)");
            s.ScatterMask = GUILayout.Toggle(s.ScatterMask, " Трава, деревья и камни Parallax по карте: не на дорогах, полосах, зданиях и воде, деревья — в лесах");
            GUILayout.Label("  " + ScatterMask.Status, muted);

            BuildingManager b = owner.Buildings;
            GUILayout.Space(6f);
            GUILayout.Label("Состояние", header);
            if (b == null)
            {
                GUILayout.Label(s.BuildingsEnabled || s.LampsEnabled || s.RoadsEnabled ? "Ждут полёта или космоцентра" : "Выключены", text);
                return;
            }
            if (b.AltitudeNote != null)
                GUILayout.Label(b.AltitudeNote, text);
            GUILayout.Label($"Тайлов готово {b.TilesBuilt} из {b.TilesTotal}, из них с подробностями {b.TilesDetailed}, " +
                            $"со зданиями Overture {b.TilesOverture}" +
                            (b.TilesFailed > 0 ? $", не загрузилось {b.TilesFailed}" : "") +
                            $". Зданий: {b.BuildingCount} (скатных крыш {b.PitchedCount}), фонарей: {b.LampCount}, тайлов с дорогами: {b.RoadTiles}" +
                            $", с 3D-дорогами: {b.Road3DTiles} ({b.Road3DVertices / 1e6:0.0} млн вершин)", text);
            GUILayout.Label("OpenFreeMap: " + b.VectorStatus, muted);
            int waiting = b.TilesDetailWaiting;
            GUILayout.Label("Overpass: " + b.OverpassStatus + (waiting > 0 ? $"; ждут подробностей {waiting} тайлов" : ""), muted);
            GUILayout.Label("Overture: " + b.OvertureStatus, muted);
            GUILayout.Label($"Скачано тайлов: {b.VectorDownloaded} быстрых, {b.OverpassDownloaded} подробных ({b.DownloadedBytes / 1048576.0:0.0} МБ)" +
                            (b.OverpassCancelled > 0 ? $"; отменено запросов Overpass: {b.OverpassCancelled} (улетели)" : ""), muted);
            if (GUILayout.Button("Перестроить здания"))
                b.Clear();
        }

        // ------------------------------------------------------------------ terrain

        void DrawTerrain(Settings s)
        {
            DemTerrain t = owner.Terrain;
            GUILayout.Label("Настоящий рельеф Земли из Mapterhorn: национальные карты высот (лидар 1–10 м в США, " +
                            "большей части Европы, Японии, Новой Зеландии, местами в Канаде и Австралии), " +
                            "в остальном мире Copernicus 30 м. Высоты делятся на 4, как у Sol; KSC и стартовые площадки — как в Sol.", text);
            bool was = s.TerrainEnabled;
            s.TerrainEnabled = GUILayout.Toggle(s.TerrainEnabled, " Настоящий рельеф");
            if (was != s.TerrainEnabled)
                Note(s.TerrainEnabled ? "Рельеф включён; если игра запускалась без него — со следующей сцены" : "Рельеф выключен: земля возвращается к рельефу Sol");
            if (t != null)
                t.Paused = GUILayout.Toggle(t.Paused, " Пауза загрузки из сети (кэш работает)");

            GUILayout.Space(4f);
            GUILayout.Label("Состояние", header);
            if (t == null)
            {
                GUILayout.Label("Не запущен (см. KSP.log)", text);
            }
            else
            {
                if (t.BaseLevel > 0)
                    GUILayout.Label($"Регион камеры: {t.RegionName}. Уровней сетки сейчас {t.LevelNow} (у Sol {t.BaseLevel}).", text);
                GUILayout.Label($"Тайлов в рельефе {t.ActiveCount}, в памяти {t.ResidentCount} (~{t.ResidentCount / 2} МБ), " +
                                $"грузится {t.LoadingCount}, ждут {t.QueuedCount}, скачано {t.Downloaded} ({t.DownloadedBytes / 1048576.0:0.0} МБ), " +
                                $"без данных {t.MissingCount}. Перестраивается кусков земли {t.StaleCount}, перестроено {t.Rebuilt}.", muted);
                if (t.Patches.Count > 0)
                    GUILayout.Label($"Рельеф выровнен другими модами (аэродромы KerbalAvia), участков: {t.Patches.Count}" +
                                    (t.PatchesWork ? "." : ", но сейчас не действует (рельеф не подключён)."), muted);
                GUILayout.Label("Сеть: " + t.NetworkStatus + (t.ProcessBytes > 0 ? $"; память игры {t.ProcessBytes / 1073741824.0:0.0} ГБ" : ""), muted);
                if (t.MemoryShort)
                    GUILayout.Label("Рельеф на паузе: игра заняла больше 80% оперативной памяти. Новые тайлы и перестройка " +
                                    "продолжатся, когда станет меньше 70%.", text);
                if (t.Holding)
                    GUILayout.Label("Под севшим аппаратом пока рельеф Sol: новый рельеф вокруг появится, когда он взлетит " +
                                    "(земля не должна двигаться под ним). В следующий раз здесь всё будет сразу из кэша.", text);
                GUILayout.Label(t.AdapterLoaded ? "BurstPQS: рельеф строится в его быстрых потоках." :
                                "BurstPQS не установлен или прослойка не загрузилась: рельеф строится медленнее.", muted);
            }

            GUILayout.Space(4f);
            GUILayout.Label("Регионы: уровни сетки / зум данных / детальность вдаль", header);
            foreach (DemRegion r in s.TerrainRegions)
                RegionRow(r);
            GUILayout.Label("Уровни — сколько уровней сетки добавить у камеры к уровням Sol (каждый вдвое мельче; " +
                            "новое число — со следующей сцены). Осторожно: трава и камни Parallax ставятся по штуке на треугольник, " +
                            "поэтому каждый уровень — вчетверо больше травы у камеры и её памяти (+4 съели 28 ГБ и уронили игру). " +
                            "Пока оставьте 0. Зум — самые мелкие тайлы высот (12 ≈ 20 м/пикс, 13 ≈ 10, 14 ≈ 5); мод сам не берёт " +
                            "мельче, чем показывает сетка (при уровнях Sol это ~9-й). Детальность — во сколько раз дальше мельчить " +
                            "сетку на неровной земле (1 — как у Sol); на равнинах всегда как у Sol.", muted);

            GUILayout.Space(4f);
            GUILayout.Label("Загрузка", header);
            s.TerrainConnections = Mathf.RoundToInt(NumberRow("demConn", "Соединений сразу:", s.TerrainConnections, 1f, float.MaxValue, ""));
            s.TerrainRequestsPerSecond = NumberRow("demRps", "Запросов в секунду:", s.TerrainRequestsPerSecond, 0.1f, float.MaxValue, "");
            s.TerrainMemoryTiles = Mathf.RoundToInt(NumberRow("demMem", "Тайлов в памяти:", s.TerrainMemoryTiles, 32f, float.MaxValue, ""));
            GUILayout.Label($"  до ~{s.TerrainMemoryTiles / 2} МБ оперативной памяти", muted);
            s.TerrainPrefetch = NumberRow("demPrefetch", "Запас загрузки:", s.TerrainPrefetch, 1f, float.MaxValue, "раз");
            s.TerrainMinZoom = Mathf.RoundToInt(NumberRow("demMinZoom", "Самый крупный зум:", s.TerrainMinZoom, 0f, 17f, ""));
            GUILayout.Label("Запас: во сколько раз дальше, чем нужно сетке, грузить тайлы заранее. Самый крупный зум задаёт, " +
                            "как далеко от камеры виден настоящий рельеф (7 — около 80 км), дальше — рельеф Sol.", muted);

            GUILayout.Space(4f);
            GUILayout.Label("Берег", header);
            float lift = NumberRow("demLift", "Суша над морем на:", s.TerrainLandLift, 0f, float.MaxValue, "м");
            float depth = NumberRow("demDepth", "Вода у берега глубже:", s.TerrainShoreDepth, 0f, float.MaxValue, "м");
            if (lift != s.TerrainLandLift || depth != s.TerrainShoreDepth)
            {
                s.TerrainLandLift = lift;
                s.TerrainShoreDepth = depth;
                t?.RebuildAll();
            }
            GUILayout.Label("Игровые метры (Sol Quarter: настоящие делить на 4). Если берег мерцает с морем — увеличьте.", muted);
            if (t != null && GUILayout.Button("Перестроить рельеф вокруг"))
                t.RebuildAll();
        }

        void RegionRow(DemRegion r)
        {
            var ci = CultureInfo.InvariantCulture;
            string kl = "region_" + r.Id + "_l", kz = "region_" + r.Id + "_z", kd = "region_" + r.Id + "_d";
            GUILayout.BeginHorizontal();
            GUILayout.Label(r.Name, text, GUILayout.Width(120f));
            if (!typed.TryGetValue(kl, out string levels))
                levels = r.Levels.ToString(ci);
            if (!typed.TryGetValue(kz, out string zoom))
                zoom = r.Zoom.ToString(ci);
            if (!typed.TryGetValue(kd, out string detail))
                detail = r.Detail.ToString("0.##", ci);
            GUI.SetNextControlName(FieldPrefix + kl);
            typed[kl] = GUILayout.TextField(levels, 3, GUILayout.Width(40f));
            GUI.SetNextControlName(FieldPrefix + kz);
            typed[kz] = GUILayout.TextField(zoom, 3, GUILayout.Width(40f));
            GUI.SetNextControlName(FieldPrefix + kd);
            typed[kd] = GUILayout.TextField(detail, 5, GUILayout.Width(50f));
            if (GUILayout.Button("Применить", GUILayout.Width(100f)))
            {
                bool ok = int.TryParse(typed[kl].Trim(), NumberStyles.Integer, ci, out int l) &
                          int.TryParse(typed[kz].Trim(), NumberStyles.Integer, ci, out int z) &
                          float.TryParse(typed[kd].Trim().Replace(',', '.'), NumberStyles.Float, ci, out float d);
                if (ok)
                {
                    r.Levels = Math.Max(0, l);
                    r.Zoom = Math.Max(0, Math.Min(17, z));
                    r.Detail = Math.Max(1f, d);
                    Note($"{r.Name}: +{r.Levels} уровней, зум {r.Zoom}, детальность {r.Detail.ToString("0.##", ci)}" +
                         (z > 17 ? " (у Mapterhorn зумы до 17)" : ""));
                    owner.Terrain?.RebuildAll();
                }
                else
                {
                    Note($"{r.Name}: нужны три числа");
                }
                typed.Remove(kl);
                typed.Remove(kz);
                typed.Remove(kd);
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ load

        void DrawLoad(Settings s)
        {
            double frame = Math.Max(Perf.FrameAvg, 0.1);
            GUILayout.Label($"Кадр: {Perf.FrameAvg:0.0} мс ({1000.0 / frame:0} к/с), худший за 2 с — {Perf.FrameMax:0} мс", text);
            GUILayout.Label($"Мод: {Perf.ModAvg:0.00} мс на кадр ({Perf.ModAvg / frame * 100.0:0}% кадра), худший кадр — {Perf.ModMax:0.0} мс", text);
            sb.Length = 0;
            int heaviest = -1;
            for (int i = 0; i < Perf.Count; i++)
            {
                sb.AppendLine($"{Perf.Names[i]}: {Perf.Avg[i]:0.00} мс, худший {Perf.Max[i]:0.0}");
                if (!Perf.Names[i].StartsWith(" ", StringComparison.Ordinal) && (heaviest < 0 || Perf.Avg[i] > Perf.Avg[heaviest]))
                    heaviest = i;
            }
            GUILayout.Label(sb.ToString().TrimEnd(), muted);
            if (Perf.FrameAvg > 20.0 && Perf.ModAvg < Perf.FrameAvg * 0.15)
                GUILayout.Label("Кадр долгий, но время уходит не на мод: тормозит видеокарта или другие моды. " +
                                "Частая причина — переполненная видеопамять (ниже — сколько её у мода).", text);
            else if (heaviest >= 0 && Perf.ModAvg >= Perf.FrameAvg * 0.15)
                GUILayout.Label($"Больше всего у мода занимает «{Perf.Names[heaviest].Trim()}».", text);

            DrawFrame();
            DrawModProfile();

            GUILayout.Space(6f);
            GUILayout.Label("Что загружено", header);
            long meshBytes = 0;
            BuildingManager b = owner.Buildings;
            if (b != null)
            {
                long buildingBytes = b.BuildingVertices * 66, lampBytes = b.LampVertices * 54, roadBytes = b.RoadVertices * 52;
                meshBytes = buildingBytes + lampBytes + roadBytes;
                GUILayout.Label($"Тайлов OSM: {b.TilesTotal}. Здания: {b.BuildingCount:N0} в {b.BuildingTiles} тайлах, " +
                                $"{b.BuildingVertices / 1e6:0.00} млн вершин (~{buildingBytes / 1048576} МБ); тени отбрасывают " +
                                $"{b.ShadowTiles} тайлов у камеры", text);
                GUILayout.Label($"Фонари: {b.LampCount:N0} в {b.LampTiles} тайлах (~{lampBytes / 1048576} МБ), пятен света на земле {b.PoolCount:N0}", text);
                GUILayout.Label($"Дороги: {b.RoadTiles} тайлов, {b.RoadVertices / 1e6:0.00} млн вершин (~{roadBytes / 1048576} МБ)", text);
            }
            TileStreamer st = owner.Streamer;
            long tileBytes = st?.ResidentBytes ?? 0;
            long clipBytes = owner.Clipmap?.Bytes ?? 0;
            if (st != null)
                GUILayout.Label($"Тайлов снимков в памяти: {st.Resident} (~{tileBytes / 1048576} МБ); уровни снимков, дорог и света ~{clipBytes / 1048576} МБ", text);
            long total = tileBytes + clipBytes + meshBytes;
            GUILayout.Label($"Видеопамяти у мода всего: ~{total / 1048576} МБ из {SystemInfo.graphicsMemorySize} МБ видеокарты", text);
            if (VideoMemory.Available)
            {
                GUILayout.Label($"Игра всего: {VideoMemory.Used / 1073741824.0:0.0} ГБ в видеокарте из {VideoMemory.Budget / 1073741824.0:0.0} ГБ, " +
                                $"что даёт Windows; в оперативной памяти {VideoMemory.Spilled / 1073741824.0:0.0} ГБ", text);
                if (VideoMemory.Short)
                    GUILayout.Label("Видеопамяти не хватает: часть текстур и мешей в оперативной памяти, видеокарта читает их " +
                                    "по шине и сильно тормозит. Мод сам держит меньше текстур тайлов; помогает и меньше " +
                                    "«Текселей на уровень» (4096 → 2048 — минус ~1,1 ГБ), закрыть браузер, а после " +
                                    "перелёта в другой город — перезапуск игры.", text);
            }
            if (tileBytes > 400L * 1048576)
                GUILayout.Label("Текстуры тайлов нужны только, чтобы дорисовывать снимки; с диска они читаются быстро. " +
                                "Если видеопамяти не хватает, уменьшите «Тайлов в памяти» на вкладке «Снимки» (1024 хватает).", muted);
            GUILayout.Label("Раз в 30 с эти же цифры пишутся в KSP.log (строки «[SolSatellite] perf»).", muted);
        }

        /// <summary>Recording which mods take the frame (ModProfiler).</summary>
        void DrawModProfile()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Профиль по модам", header);
            if (ModProfiler.Now == ModProfiler.Stage.Idle)
            {
                if (GUILayout.Button($"Записать {ModProfiler.RecordSeconds:0} с", GUILayout.Width(160f)))
                    ModProfiler.Start();
                GUILayout.Label("На время записи на методы кадра всех модов и KSP (Update, LateUpdate, FixedUpdate, OnGUI, " +
                                "камеры, модули деталей) ставятся таймеры, потом снимаются; пока ставятся и снимаются " +
                                "(секунды), игра подтормаживает. Отчёт — в PluginData\\Logs.", muted);
                if (ModProfiler.Summary != null)
                {
                    GUILayout.Label(ModProfiler.Summary, text);
                    GUILayout.Label("Полный отчёт: " + ModProfiler.ReportPath, muted);
                }
                return;
            }
            GUILayout.Label(ModProfiler.Status, text);
            if (ModProfiler.Now != ModProfiler.Stage.Unpatching && GUILayout.Button("Остановить", GUILayout.Width(160f)))
                ModProfiler.Stop();
        }

        /// <summary>Where the whole frame goes (FrameProfile), and what that points at.</summary>
        void DrawFrame()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Куда уходит весь кадр", header);
            if (!FrameProfile.Active)
            {
                GUILayout.Label("Замер не работает (см. KSP.log)", muted);
                return;
            }
            sb.Length = 0;
            for (int i = 0; i < FrameProfile.Count; i++)
            {
                sb.Append($"{FrameProfile.Names[i]}: {FrameProfile.Avg[i]:0.0} мс, худший {FrameProfile.Max[i]:0}");
                if (i == (int)FrameProfile.Phase.Physics)
                    sb.Append($" ({FrameProfile.StepsAvg:0.0} шага за кадр)");
                sb.Append('\n');
            }
            if (FrameProfile.GpuAvg >= 0.0)
                sb.Append($"Видеокарта сама: {FrameProfile.GpuAvg:0.0} мс на кадр\n");
            sb.Append(FrameProfile.MinuteCollections > 0
                ? $"Сборка мусора: {FrameProfile.MinuteCollections} за минуту" +
                  (FrameProfile.MinuteCollections > 1 ? $" (раз в {FrameProfile.MinuteInterval:0} с)" : "") +
                  $", кадр с ней {FrameProfile.MinuteGcFrameAvg:0} мс, худший {FrameProfile.MinuteGcFrameMax:0} мс\n"
                : "Сборок мусора за минуту не было\n");
            FrameProfile.Memory(out long managed, out long unity);
            sb.Append($"Память: управляемая (C#) {managed / 1073741824.0:0.0} ГБ" + (unity > 0 ? $", Unity {unity / 1073741824.0:0.0} ГБ" : "") +
                      $"; запас мешей мода {MeshPools.Held / 1048576.0:0} МБ\n");
            sb.Append($"Игра выделяет ~{FrameProfile.AllocRate:0} МБ/с (за минуту ~{FrameProfile.AllocRateMinute:0}); чем больше, тем чаще сборки");
            GUILayout.Label(sb.ToString(), muted);
            string hint;
            switch (FrameProfile.Heaviest)
            {
                case FrameProfile.Phase.Present:
                    hint = "Больше всего кадр ждёт видеокарту: она не успевает (часто — переполнена видеопамять; облака EVE, Scatterer, тени). Процессору тут делать нечего.";
                    break;
                case FrameProfile.Phase.Render:
                    hint = "Больше всего уходит на подготовку рендера: много объектов и вызовов отрисовки (детали, здания, деревья Parallax).";
                    break;
                case FrameProfile.Phase.Physics:
                    hint = "Больше всего — физика KSP: детали корабля, коллайдеры. Каждый медленный кадр добавляет ещё шагов физики.";
                    break;
                case FrameProfile.Phase.LateUpdate:
                    hint = Perf.ModAvg > FrameProfile.Avg[(int)FrameProfile.Phase.LateUpdate] * 0.5
                        ? "Больше всего — LateUpdate, и это в основном мод (разбивка выше)."
                        : "Больше всего — LateUpdate скриптов других модов (мод из этого — строка «Мод» выше).";
                    break;
                case FrameProfile.Phase.Update:
                    hint = "Больше всего — Update скриптов модов и KSP.";
                    break;
                default:
                    hint = "Больше всего — прочее (загрузка, ввод, сборка мусора).";
                    break;
            }
            GUILayout.Label(hint, text);
        }

        // ------------------------------------------------------------------ cache

        void DrawCache(Settings s)
        {
            DiskCache cache = owner.Cache;
            if (cache == null)
            {
                GUILayout.Label("Кэш недоступен (см. KSP.log)", text);
                return;
            }
            s.ImageryCacheGb = DrawCacheArea(cache, cache.Imagery, "Спутниковые снимки", "cacheImagery", s.ImageryCacheGb,
                "Тайлы до z10 включительно (из них собрана орбитальная текстура) не удаляются.");
            GUILayout.Space(6f);
            s.OsmCacheGb = DrawCacheArea(cache, cache.Osm, "Города: здания, дороги (OSM)", "cacheOsm", s.OsmCacheGb, null);
            GUILayout.Space(6f);
            s.TerrainCacheGb = DrawCacheArea(cache, cache.Terrain, "Рельеф (Mapterhorn)", "cacheTerrain", s.TerrainCacheGb, null);
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Проверить сейчас"))
            {
                cache.CheckSoon();
                confirmClear = null;
            }
            GUILayout.Label(cache.Busy ? "Идёт проверка..." : "", text);
            GUILayout.EndHorizontal();
            GUILayout.Label("Лимит 0 — без ограничения. Проверка раз в 10 минут; когда кэш больше лимита, " +
                            "удаляется то, что дольше всего не открывалось, пока не останется 90%.", muted);
        }

        float DrawCacheArea(DiskCache cache, DiskCache.Area area, string title, string key, float limit, string note)
        {
            GUILayout.Label(title, header);
            string used = area.Scanned
                ? $"Занято {Gb(area.Bytes)}" + (area.KeptBytes > 0 ? $", из них {Gb(area.KeptBytes)} не удаляются" : "")
                : "Считается...";
            GUILayout.Label(used, text);
            limit = NumberRow(key, "Лимит:", limit, 0f, float.MaxValue, "ГБ");
            if (note != null)
                GUILayout.Label(note, muted);
            if (area.Result != null)
                GUILayout.Label("Последняя очистка: " + area.Result, muted);
            if (confirmClear == area)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Удалить всё, что можно?", text);
                if (GUILayout.Button("Да", GUILayout.Width(60f)))
                {
                    cache.Clear(area);
                    confirmClear = null;
                }
                if (GUILayout.Button("Нет", GUILayout.Width(60f)))
                    confirmClear = null;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Очистить"))
            {
                confirmClear = area;
            }
            return limit;
        }

        static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} ГБ" : $"{bytes / 1048576.0:0} МБ";

        // ------------------------------------------------------------------ helpers

        /// <summary>A number typed in by hand, taken only when "Применить" is pressed; out-of-range
        /// values are clamped (and said so).</summary>
        float NumberRow(string key, string label, float value, float min, float max, string unit)
        {
            var ci = CultureInfo.InvariantCulture;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(LabelWidth - 60f));
            if (!typed.TryGetValue(key, out string entry))
                entry = value.ToString("0.#", ci);
            GUI.SetNextControlName(FieldPrefix + key);
            typed[key] = GUILayout.TextField(entry, 8, GUILayout.Width(70f));
            GUILayout.Label(unit, text, GUILayout.Width(28f));
            if (GUILayout.Button("Применить", GUILayout.Width(100f)))
            {
                string number = typed[key].Trim().Replace(',', '.');
                if (float.TryParse(number, NumberStyles.Float, ci, out float v) && !float.IsNaN(v))
                {
                    value = Mathf.Clamp(v, min, max);
                    Note(value == v
                        ? $"{label} {value.ToString("0.#", ci)} {unit}"
                        : $"{label} можно от {min.ToString("0.#", ci)} до {max.ToString("0.#", ci)} {unit}, поставлено {value.ToString("0.#", ci)}");
                }
                else
                {
                    Note($"«{typed[key]}» — не число");
                }
                typed.Remove(key); // show what was applied
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();
            return value;
        }

        float SliderRow(string label, float value, float min, float max, bool whole)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(LabelWidth));
            GUILayout.BeginVertical();
            GUILayout.Space(8f);
            float v = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            return whole ? Mathf.Round(v) : v;
        }

        static Texture2D MakeIcon()
        {
            // A small globe: ocean, a few land blotches, lit rim.
            const int n = 38;
            var tex = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var pixels = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x - 18.5f, dy = y - 18.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 c = new Color32(0, 0, 0, 0);
                    if (r < 15f)
                    {
                        bool land = Mathf.Sin(x * 0.45f) + Mathf.Cos(y * 0.38f + x * 0.21f) > 0.85f;
                        c = land ? new Color32(96, 150, 70, 255) : new Color32(38, 92, 170, 255);
                    }
                    else if (r < 16.5f)
                    {
                        c = new Color32(170, 210, 255, 255);
                    }
                    pixels[y * n + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        public void Dispose()
        {
            LockInput(false);
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            RemoveButton();
        }
    }
}
