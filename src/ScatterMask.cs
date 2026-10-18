// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parallax;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// A tile's scatter mask for the clipmap's mask layer (Clipmap.Mask), drawn like the road
    /// layer (TileProject's road pass keeps each channel's maximum, so overlaps and neighbouring
    /// tiles change nothing): r: no scatters at all (roads, railways, runways, aprons, buildings,
    /// water), g: forest, b: built-up 0.5 / airfield 1, a: map data here 0.5 / park 1. In the
    /// tile's flat frame at real size, like the road layer's ribbons. Thread-safe.
    /// </summary>
    static class MaskMesher
    {
        static readonly Color32 Known = new Color32(0, 0, 0, 128), Blocked = new Color32(255, 0, 0, 128),
            Forest = new Color32(0, 255, 0, 128), Park = new Color32(0, 0, 0, 255),
            BuiltUp = new Color32(0, 0, 128, 128), Airfield = new Color32(0, 0, 255, 128);
        const double Verge = 1.0;     // m beyond a road's edge kept clear too (real metres)
        const double RunwayHalf = 25.0, TaxiwayHalf = 12.0, OtherRoadHalf = 2.5, RailHalf = 3.0;

        public static RibbonMeshData Build(OsmTile tile)
        {
            TileFrame frame = TileFrame.Flat(tile);
            RibbonMeshData mesh = RibbonMeshData.Rent();
            var rings = new List<int[]>(4);
            // The tile itself: map data is here. Where it isn't, Parallax places scatters as it would.
            int s = (int)Math.Round(tile.South * 1e7), n = (int)Math.Round(tile.North * 1e7);
            int w = (int)Math.Round(tile.West * 1e7), e = (int)Math.Round(tile.East * 1e7);
            rings.Add(new[] { n, w, n, e, s, e, s, w });
            Fill(mesh, frame, rings, Known);
            foreach (OsmArea a in tile.Areas)
                Fill(mesh, frame, a.Rings, ColourOf(a.Kind));
            foreach (OsmBuilding b in tile.Buildings)
            {
                if (b.Rings.Count == 0)
                    continue;
                rings.Clear();
                rings.Add(b.Rings[0]); // courtyards stay blocked too
                Fill(mesh, frame, rings, Blocked);
            }
            double lon = OsmTile.LonOf(tile.X + 0.5);
            bool americas = lon < -30.0 && lon > -170.0;
            var runs = new List<List<double>>();
            foreach (OsmRoad r in tile.RoadList)
            {
                double half;
                if (RoadMesher.Railway(r.Kind, out double bed, out _))
                    half = Math.Max(bed * 0.5, RailHalf);
                else if (RoadMesher.Styled(r, americas, out RoadMesher.Style style))
                    half = style.Width * 0.5;
                else
                    half = OtherRoadHalf;
                Ribbons(mesh, tile, frame, r.Points, half + Verge, runs);
            }
            foreach (OsmRoad r in tile.AerowayLines)
                Ribbons(mesh, tile, frame, r.Points, (r.Kind == "runway" ? RunwayHalf : TaxiwayHalf) + Verge, runs);
            return mesh;
        }

        static Color32 ColourOf(AreaKind kind)
        {
            switch (kind)
            {
                case AreaKind.Forest: return Forest;
                case AreaKind.Park: return Park;
                case AreaKind.BuiltUp: return BuiltUp;
                case AreaKind.Airfield: return Airfield;
                default: return Blocked; // paved aeroways, water
            }
        }

        static void Ribbons(RibbonMeshData mesh, OsmTile tile, TileFrame frame, int[] points, double half, List<List<double>> runs)
        {
            if (points == null || points.Length < 4)
                return;
            runs.Clear();
            RibbonMeshData.Runs(tile, frame, points, 0.0, double.MaxValue, runs);
            foreach (List<double> line in runs)
                if (line.Count >= 4)
                    mesh.Add(line, 0.0, (float)half, Blocked);
        }

        /// <summary>A polygon (outer ring, then holes; lat,lon in 1e-7 degrees) as filled triangles.</summary>
        static void Fill(RibbonMeshData mesh, TileFrame frame, List<int[]> rings, Color32 colour)
        {
            if (rings.Count == 0 || rings[0] == null || rings[0].Length < 6)
                return;
            int count = 0;
            foreach (int[] ring in rings)
                if (ring != null)
                    count += ring.Length / 2;
            var xy = new double[count * 2];
            var holes = new List<int>();
            int v = 0;
            for (int r = 0; r < rings.Count; r++)
            {
                int[] ring = rings[r];
                if (ring == null || ring.Length < 6)
                    continue;
                if (r > 0)
                    holes.Add(v);
                for (int i = 0; i + 1 < ring.Length; i += 2)
                {
                    RibbonMeshData.Point(frame, ring[i] * 1e-7, ring[i + 1] * 1e-7, out double x, out double y);
                    xy[2 * v] = x;
                    xy[2 * v + 1] = y;
                    v++;
                }
            }
            var triangles = new List<int>();
            Earcut.Triangulate(xy, v, holes, triangles);
            if (triangles.Count >= 3)
                mesh.AddFill(xy, v, triangles, colour);
        }
    }

    /// <summary>
    /// Puts Parallax's scatters on Earth where the map says they belong (MaskMesher, drawn into
    /// the clipmap's mask layer): no grass, trees or rocks on roads, railways, runways and
    /// buildings or in water; trees in forests and only a few elsewhere; no trees or rocks on
    /// airfields and fewer in towns. Parallax isn't changed on disk:
    ///  * Earth's scatters get our build of Parallax's TerrainScatters compute shader (Harmony
    ///    postfix on Scatter.InitShader): its kernels 0-9 are Parallax's own, 10-15 the
    ///    distribution kernels that also read the mask;
    ///  * a postfix on ScatterData.GetDistributeKernel picks those for masked scatters, and a
    ///    prefix on ScatterData.Distribute hands them the mask with this moment's transforms
    ///    (planet-fixed, so it doesn't matter what the camera or KSP's floating origin did).
    /// The mask is applied when scatters are placed, not when drawn, so trees taken out get no
    /// colliders either. Where the map arrives after a quad's scatters were placed, the quad's
    /// scatters are placed again (as Parallax's ReinitializeScatters does, but with new
    /// colliders and paused scatters left paused), a few quads a frame.
    /// </summary>
    static class ScatterMask
    {
        const int MaskedKernels = 10;          // ours: Parallax's distribution kernel + 10
        const float SettleSeconds = 2.5f;      // changed tiles wait this long, so neighbours arriving together cost one placing
        const float QuadCooldown = 8f;         // a quad's scatters are placed again at most this often
        const int ReplacedPerFrame = 3;        // quads whose scatters are placed again, at most,
        const double ReplaceMs = 2.0;          // ... and while this frame has spent less than this on it (one at least)
        const int SearchSpan = 2;              // tiles around a quad's centre that count as touching it

        static readonly int MaskId = Shader.PropertyToID("_SolSatMask");
        static readonly int KindId = Shader.PropertyToID("_SolSatKind");
        static readonly int WorldToPlanetId = Shader.PropertyToID("_SolSatWorldToPlanet");
        static readonly int UpId = Shader.PropertyToID("_SolSatMaskUp");
        static readonly int EastId = Shader.PropertyToID("_SolSatMaskEast");
        static readonly int NorthId = Shader.PropertyToID("_SolSatMaskNorth");
        static readonly int LevelsId = Shader.PropertyToID("_SolSatMaskLevels");

        static ComputeShader compute;
        static readonly HashSet<ComputeShader> ours = new HashSet<ComputeShader>();
        static readonly Dictionary<Scatter, int> kinds = new Dictionary<Scatter, int>();
        static FieldInfo kernelField, collidersAddedField;
        static Settings settings;
        static Clipmap clipmap;
        static CelestialBody body;
        static Texture2DArray empty;
        static readonly Vector4[] levels = new Vector4[Clipmap.MaxLevels];
        static bool failed;
        static bool? wasOn;                    // the setting as last seen (none yet: nothing to undo)
        static readonly Dictionary<PQ, float> lastPlaced = new Dictionary<PQ, float>();

        // Tiles whose mask changed, with when; quads still to be placed again.
        static readonly Dictionary<long, float> changed = new Dictionary<long, float>();
        static readonly HashSet<long> settled = new HashSet<long>();
        static readonly List<long> settledKeys = new List<long>();
        static readonly Queue<PQ> replace = new Queue<PQ>();
        static readonly HashSet<PQ> queued = new HashSet<PQ>();
        static readonly List<ScatterData> active = new List<ScatterData>();
        static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        static bool everything;

        public static int Replaced { get; private set; }
        public static int Masked { get; private set; }
        public static string Problem { get; private set; }

        /// <summary>Our compute shader is in and the patches are on.</summary>
        public static bool Ready => compute != null && !failed;

        public static bool On => Ready && settings != null && settings.ScatterMask;

        public static void Setup(ComputeShader shader, Settings s)
        {
            settings = s;
            if (shader == null)
            {
                Problem = "нет шейдера раскладки в бандле SolSatellite";
                return;
            }
            try
            {
                // Our build must have Parallax's kernels where Parallax looks for them, and ours after.
                if (!shader.HasKernel("Distribute") || !shader.HasKernel("Evaluate") ||
                    shader.FindKernel("Distribute") != 0 || shader.FindKernel("Evaluate") != 6)
                {
                    Problem = "шейдер раскладки не совпадает с Parallax";
                    Debug.LogWarning("[SolSatellite] Scatter mask off: our TerrainScatters kernels don't match Parallax's");
                    return;
                }
                kernelField = AccessTools.Field(typeof(ScatterData), "distributeKernel");
                collidersAddedField = AccessTools.Field(typeof(ScatterData), "collidersAdded");
                MethodInfo init = AccessTools.Method(typeof(Scatter), "InitShader");
                MethodInfo pick = AccessTools.Method(typeof(ScatterData), "GetDistributeKernel");
                MethodInfo distribute = AccessTools.Method(typeof(ScatterData), "Distribute");
                if (kernelField == null || init == null || pick == null || distribute == null)
                {
                    Problem = "Parallax не такой, как ожидалось";
                    Debug.LogWarning("[SolSatellite] Scatter mask off: Parallax's scatter classes aren't what it expects");
                    return;
                }
                var harmony = new Harmony("SolSatellite.ScatterMask");
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(ScatterMask).GetMethod(nameof(AfterInitShader), flags)));
                harmony.Patch(pick, postfix: new HarmonyMethod(typeof(ScatterMask).GetMethod(nameof(AfterPickKernel), flags)));
                harmony.Patch(distribute, prefix: new HarmonyMethod(typeof(ScatterMask).GetMethod(nameof(BeforeDistribute), flags)));
                compute = shader;
                Debug.Log("[SolSatellite] Scatter mask ready: Earth's Parallax scatters follow the map");
            }
            catch (Exception e)
            {
                failed = true;
                Problem = e.Message;
                Debug.LogWarning("[SolSatellite] Scatter mask off: " + e.Message);
            }
        }

        /// <summary>Scatter kinds by name: 0 left alone (water plants), 1 ground cover, 2 tree, 3 rock.</summary>
        static int KindOf(Scatter s)
        {
            if (s == null)
                return 0;
            if (kinds.TryGetValue(s, out int kind))
                return kind;
            string n = (s.scatterName ?? "").ToLowerInvariant();
            if (n.Contains("kelp") || n.Contains("seaweed") || n.Contains("seagrass") || n.Contains("reed") || n.Contains("coral"))
                kind = 0;
            else if (n.Contains("tree") || n.Contains("baobab") || n.Contains("palm"))
                kind = 2;
            else if (n.Contains("rock") || n.Contains("cact") || n.Contains("boulder") || n.Contains("stone"))
                kind = 3;
            else
                kind = 1;
            kinds[s] = kind;
            return kind;
        }

        static bool OnEarth(Scatter s) =>
            ConfigLoader.parallaxScatterBodies != null &&
            ConfigLoader.parallaxScatterBodies.TryGetValue("Earth", out ParallaxScatterBody earth) &&
            earth.fastScatters != null && Array.IndexOf(earth.fastScatters, s) >= 0;

        // ------------------------------------------------------------------ Harmony patches

        static void AfterInitShader(Scatter __instance)
        {
            if (failed || compute == null)
                return;
            try
            {
                if (!OnEarth(__instance))
                    return;
                if (__instance.shader != null)
                    UnityEngine.Object.Destroy(__instance.shader);
                __instance.shader = UnityEngine.Object.Instantiate(compute);
                ours.RemoveWhere(c => c == null); // destroyed when their body unloaded
                ours.Add(__instance.shader);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void AfterPickKernel(ScatterData __instance, ref int __result)
        {
            if (failed || compute == null)
                return;
            try
            {
                if (Uses(__instance))
                    __result += MaskedKernels;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void BeforeDistribute(ScatterData __instance)
        {
            if (failed || compute == null)
                return;
            try
            {
                ComputeShader cs = __instance.scatter?.shader;
                if (cs == null || !ours.Contains(cs))
                    return;
                int kernel = (int)kernelField.GetValue(__instance);
                if (kernel < MaskedKernels)
                    return;
                SetParams(cs, kernel, KindOf(__instance.scatter));
                Masked++;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void Fail(Exception e)
        {
            if (failed)
                return;
            failed = true; // Parallax goes on as it would without us (our kernels 0-9 are its own)
            Problem = e.Message;
            Debug.LogException(e);
        }

        /// <summary>Whether this scatter's distribution reads the mask (our shader, a masked kind, the mask on).</summary>
        static bool Uses(ScatterData d) =>
            settings != null && settings.ScatterMask && d?.scatter != null && d.scatter.shader != null &&
            ours.Contains(d.scatter.shader) && KindOf(d.scatter) != 0 && clipmap?.Mask != null && clipmap.Plane != null;

        static void SetParams(ComputeShader cs, int kernel, int kind)
        {
            Texture mask = clipmap?.Mask?.Texture;
            TangentPlane plane = clipmap?.Plane;
            if (mask == null || plane == null || body == null || body.pqsController == null)
            {
                cs.SetTexture(kernel, MaskId, Empty);
                for (int k = 0; k < levels.Length; k++)
                    levels[k] = Vector4.zero;
                cs.SetVectorArray(LevelsId, levels); // no level: as Parallax would place them
                return;
            }
            cs.SetTexture(kernel, MaskId, mask);
            cs.SetInt(KindId, kind);
            cs.SetMatrix(WorldToPlanetId, body.pqsController.transform.worldToLocalMatrix);
            cs.SetVector(UpId, new Vector4((float)plane.Up.x, (float)plane.Up.y, (float)plane.Up.z, (float)plane.Radius));
            cs.SetVector(EastId, new Vector4((float)plane.East.x, (float)plane.East.y, (float)plane.East.z, 0f));
            cs.SetVector(NorthId, new Vector4((float)plane.North.x, (float)plane.North.y, (float)plane.North.z, 0f));
            clipmap.MaskLevelParams(levels);
            cs.SetVectorArray(LevelsId, levels);
        }

        static Texture2DArray Empty
        {
            get
            {
                if (empty == null)
                {
                    empty = new Texture2DArray(1, 1, Clipmap.MaxLevels, TextureFormat.RGBA32, false) { name = "SolSatellite empty scatter mask" };
                    var clear = new Color32[1];
                    for (int k = 0; k < Clipmap.MaxLevels; k++)
                        empty.SetPixels32(clear, k);
                    empty.Apply(false, true);
                }
                return empty;
            }
        }

        // ------------------------------------------------------------------ placing again

        /// <summary>Tile (z14) x, y's mask changed: the scatters on quads there are placed again shortly.</summary>
        public static void Changed(int x, int y)
        {
            if (!On)
                return;
            changed[((long)x << 20) | (uint)y] = Time.realtimeSinceStartup;
        }

        /// <summary>Call every frame after the mask layer is drawn.</summary>
        public static void Update(Clipmap map, CelestialBody earth)
        {
            clipmap = map;
            body = earth;
            if (!Ready)
                return;
            bool on = settings.ScatterMask;
            if (wasOn == null)
            {
                wasOn = on;
            }
            else if (on != wasOn.Value)
            {
                // Switched on or off: everything near is placed again, with the mask or without.
                wasOn = on;
                everything = true;
            }
            try
            {
                FindQuads();
                ReplaceSome();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void FindQuads()
        {
            float now = Time.realtimeSinceStartup;
            settled.Clear();
            settledKeys.Clear();
            foreach (KeyValuePair<long, float> kv in changed)
                if (now - kv.Value >= SettleSeconds)
                    settledKeys.Add(kv.Key);
            if (settledKeys.Count == 0 && !everything)
                return;
            foreach (long key in settledKeys)
            {
                changed.Remove(key);
                settled.Add(key);
            }
            Dictionary<PQ, ScatterSystemQuadData> quads = ScatterComponent.scatterQuadData;
            if (quads == null)
                return;
            int n = 1 << OsmTile.Zoom;
            foreach (KeyValuePair<PQ, ScatterSystemQuadData> kv in quads)
            {
                PQ quad = kv.Key;
                if (quad == null || queued.Contains(quad) || quad.sphereRoot == null || quad.sphereRoot != body?.pqsController)
                    continue;
                if (!everything)
                {
                    Geo.LatLon(quad.positionPlanetRelative.normalized, out double lat, out double lon);
                    int cx = (int)Math.Floor(OsmTile.TileX(lon)), cy = (int)Math.Floor(OsmTile.TileY(lat));
                    bool touched = false;
                    for (int dy = -SearchSpan; dy <= SearchSpan && !touched; dy++)
                        for (int dx = -SearchSpan; dx <= SearchSpan && !touched; dx++)
                            touched = settled.Contains(((long)(((cx + dx) % n + n) % n) << 20) | (uint)(cy + dy));
                    if (!touched)
                        continue;
                }
                replace.Enqueue(quad);
                queued.Add(quad);
            }
            everything = false;
        }

        /// <summary>Places a few queued quads' scatters again. Not through Parallax's
        /// ReinitializeScatters: its Cleanup leaves collidersAdded set, so the scatters placed
        /// again would get no colliders, and on paused scatters it would undo the pause. Each
        /// gets a new placing (ScatterGhosts.PlaceAgain), the old one drawn until it's ready.</summary>
        static void ReplaceSome()
        {
            Dictionary<PQ, ScatterSystemQuadData> quads = ScatterComponent.scatterQuadData;
            float now = Time.realtimeSinceStartup;
            if (lastPlaced.Count > 4096)
            {
                var old = new List<PQ>();
                foreach (KeyValuePair<PQ, float> kv in lastPlaced)
                    if (now - kv.Value >= QuadCooldown)
                        old.Add(kv.Key);
                foreach (PQ q in old)
                    lastPlaced.Remove(q);
            }
            int done = 0;
            bool worked = false;
            clock.Reset();
            clock.Start();
            for (int left = replace.Count; left > 0 && done < ReplacedPerFrame; left--)
            {
                if (worked && clock.Elapsed.TotalMilliseconds >= ReplaceMs)
                    break;
                PQ quad = replace.Dequeue();
                if (quads == null || quad == null || !quads.TryGetValue(quad, out ScatterSystemQuadData data) || data?.quadScatters == null)
                {
                    queued.Remove(quad);
                    continue;
                }
                if (lastPlaced.TryGetValue(quad, out float last) && now - last < QuadCooldown)
                {
                    replace.Enqueue(quad); // placed again only a moment ago: later
                    continue;
                }
                queued.Remove(quad);
                lastPlaced[quad] = now;
                worked = true;
                active.Clear();
                bool refreshed = false;
                foreach (ScatterData d in data.quadScatters)
                {
                    if (d?.scatter == null || KindOf(d.scatter) == 0)
                        continue;
                    bool colliders = collidersAddedField != null && collidersAddedField.GetValue(d) is bool b && b;
                    if (d.cleaned)
                    {
                        // Paused (out of range): placed anew when it comes back, with the mask.
                        // Colliders from its last placing go now, so it makes new ones then.
                        if (colliders)
                        {
                            d.Cleanup();
                            collidersAddedField.SetValue(d, false);
                        }
                        continue;
                    }
                    active.Add(d);
                }
                foreach (ScatterData d in active)
                {
                    if (!refreshed)
                    {
                        data.Reinitialize(); // the quad's planet normal and directions, as Parallax does first
                        refreshed = true;
                    }
                    // A new placing in its place; the old one stays drawn until it's ready (in
                    // place, the scatters blinked for the few frames Parallax waits for its count).
                    if (ScatterGhosts.PlaceAgain(data, d))
                        continue;
                    d.Cleanup();
                    collidersAddedField?.SetValue(d, false);
                    d.Start();
                }
                if (active.Count == 0)
                    continue;
                Replaced++;
                done++;
            }
            clock.Stop();
            active.Clear();
        }

        public static string Status =>
            !Ready ? (Problem ?? "выключено") :
            !settings.ScatterMask ? "выключено" :
            $"работает: раскладок по карте {Masked}, кусков разложено заново {Replaced}" + (replace.Count > 0 ? $", в очереди {replace.Count}" : "");
    }
}
