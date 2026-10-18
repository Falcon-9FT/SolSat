// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Spaceports on the real relief (2026-10-04). Around KSC and the launch sites the real
    /// terrain used to keep Sol's own (Dem's places kept as Sol has them): on Merritt Island that
    /// is half lagoon ("KSC partly in water"), and under the Kerbal Konstructs pads of
    /// CapeCanaveral/ and Baikonur/ - snapped to the ground at one point - the steppe rose
    /// through their ground plates here and dropped away there. Now the real relief goes on
    /// up to them and a terrain patch (TerrainPatches' store, as KerbalAvia grades airfields)
    /// lays the ground just under their lowest ground: flat on their own plane, out to Full
    /// beyond their outline - Sol's Earth has a terrain vertex every ~87 m, a triangle reaching
    /// past the edge mustn't lift the ground into them - and back to the relief over Fade.
    ///
    /// The pads: SOLSAT_SITE_GROUND nodes (CapeCanaveral/LC39.cfg, Baikonur/Baikonur.cfg) give
    /// the group centre, the lowest ground's height above the sea and the outline; the groups sit
    /// at a fixed height above the sea (SeaLevelAsReference), so ground and patch never chase
    /// each other. KSC: its ground is measured with rays down onto its own colliders the first
    /// time it is near (cached in PluginData/Cache/site_grounds.txt) and the patch follows KSC
    /// wherever KSCSwitcher puts it.
    /// </summary>
    static class SiteGrounds
    {
        const double Below = 0.3;    // game m: the terrain this far under the lowest ground
        const double Full = 90.0;    // game m past the outline still level
        const double Fade = 150.0;   // game m back to the relief
        const double Step = 15.0;    // patch grid spacing, game m
        const double Reach = 3000.0; // KSCSwitcher's launch sites this close to a levelled site keep nothing of Sol's

        sealed class Site
        {
            public string Name, Group;
            public double Lat, Lon, Floor;
            public List<Vector2d> Outline;
            public Vector3d Dir;
            public bool Set;
        }

        static readonly List<Site> sites = new List<Site>();
        static readonly HashSet<string> groups = new HashSet<string>();
        static bool loaded;
        static string cacheFile;

        // KSC
        static bool kscWanted;
        static double kscX0 = -1475, kscX1 = 1290, kscZ0 = -600, kscZ1 = 650; // its own frame (x along the runway), game m
        static double kscGround = double.NaN; // its ground above its origin, measured
        static bool kscSet;
        static Vector3d kscDir;               // PQS-local, where its patch was laid
        static Vector3d kscRight;
        static double kscRadius;
        static float nextKsc;
        static int kscTries;
        static PQSCity ksc;

        /// <summary>Main thread, every frame from the start: reads the sites once the game
        /// database is there and lays their patches; KSC's when it is placed and measured.</summary>
        public static void Update(string modDir)
        {
            DemTerrain terrain = DemTerrain.Instance;
            if (terrain == null)
                return;
            if (!loaded)
            {
                if (GameDatabase.Instance == null || !GameDatabase.Instance.IsReady())
                    return;
                CelestialBody earth = FlightGlobals.GetBodyByName("Earth");
                if (earth == null)
                    return;
                loaded = true;
                cacheFile = Path.Combine(modDir, "PluginData", "Cache", "site_grounds.txt");
                Load();
                ReadCache();
                foreach (Site s in sites)
                    LaySite(terrain, s, earth.Radius);
            }
            if (kscWanted && HighLogic.LoadedScene == GameScenes.FLIGHT || kscWanted && HighLogic.LoadedScene == GameScenes.SPACECENTER)
                UpdateKsc(terrain);
        }

        /// <summary>Dem: a PQS city whose ground is levelled here, not kept as Sol has it.</summary>
        public static bool Levelled(PQSCity city)
        {
            if (city == null)
                return false;
            if (city.name == "KSC")
                return kscSet;
            foreach (Site s in sites)
                if (s.Set && s.Group == city.name)
                    return true;
            return false;
        }

        /// <summary>Dem: a launch site (KSCSwitcher's) at a place levelled here.</summary>
        public static bool NearLevelled(Vector3d dir, double radius)
        {
            double cos = Math.Cos(Reach / radius);
            foreach (Site s in sites)
                if (s.Set && Vector3d.Dot(dir, s.Dir) >= cos)
                    return true;
            return kscSet && Vector3d.Dot(dir, kscDir) >= cos;
        }

        static void Load()
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (ConfigNode n in GameDatabase.Instance.GetConfigNodes("SOLSAT_SITE_GROUND"))
            {
                try
                {
                    if (n.HasValue("ksc") && bool.TryParse(n.GetValue("ksc"), out bool k) && k)
                    {
                        kscWanted = true;
                        string[] r = n.GetValue("rect")?.Split(',');
                        if (r != null && r.Length == 4)
                        {
                            kscX0 = double.Parse(r[0], ci);
                            kscX1 = double.Parse(r[1], ci);
                            kscZ0 = double.Parse(r[2], ci);
                            kscZ1 = double.Parse(r[3], ci);
                        }
                        continue;
                    }
                    var s = new Site
                    {
                        Name = n.GetValue("name"),
                        Group = n.GetValue("group"),
                        Lat = double.Parse(n.GetValue("latitude"), ci),
                        Lon = double.Parse(n.GetValue("longitude"), ci),
                        Floor = double.Parse(n.GetValue("floor"), ci),
                        Outline = new List<Vector2d>(),
                    };
                    foreach (string p in n.GetValues("point"))
                    {
                        string[] xy = p.Split(',');
                        s.Outline.Add(new Vector2d(double.Parse(xy[0], ci), double.Parse(xy[1], ci)));
                    }
                    if (s.Outline.Count < 3 || string.IsNullOrEmpty(s.Group))
                        throw new FormatException("an outline of 3 points or more and a group are needed");
                    s.Dir = Geo.Direction(s.Lat, s.Lon);
                    sites.Add(s);
                    groups.Add(s.Group);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SolSatellite] SOLSAT_SITE_GROUND {n.GetValue("name")}: {e.Message}");
                }
            }
            Debug.Log($"[SolSatellite] Site grounds: {sites.Count} sites{(kscWanted ? " and KSC" : "")}");
        }

        static void LaySite(DemTerrain terrain, Site s, double radius)
        {
            Vector3d up = s.Dir;
            double lon = s.Lon * Math.PI / 180.0;
            var east = new Vector3d(-Math.Sin(lon), 0.0, Math.Cos(lon)).normalized;
            Vector3d north = Vector3d.Cross(up, east).normalized;
            if (Vector3d.Dot(north, new Vector3d(0, 1, 0)) < 0.0)
                north = -north;
            s.Set = Lay(terrain, "SolSatellite:site:" + s.Name, up, east, north, radius + s.Floor, s.Outline);
            Debug.Log($"[SolSatellite] Site ground {s.Name}: {(s.Set ? "levelled" : "NOT levelled")} {s.Floor:0.0} m above the sea, outline {s.Outline.Count} points");
        }

        /// <summary>A patch: the plane at planeRadius (its points' own heights, the plane being flat)
        /// less Below, inside the outline grown by Full, back to the relief over Fade.</summary>
        static bool Lay(DemTerrain terrain, string key, Vector3d up, Vector3d east, Vector3d north, double planeRadius, List<Vector2d> outline)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (Vector2d p in outline)
            {
                minX = Math.Min(minX, p.x);
                maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y);
                maxY = Math.Max(maxY, p.y);
            }
            double pad = Full + Fade + Step;
            double x0 = minX - pad, y0 = minY - pad;
            int nx = (int)Math.Ceiling((maxX - minX + 2 * pad) / Step) + 1;
            int ny = (int)Math.Ceiling((maxY - minY + 2 * pad) / Step) + 1;
            var heights = new float[nx * ny];
            var weights = new float[nx * ny];
            for (int j = 0; j < ny; j++)
            {
                double y = y0 + j * Step;
                for (int i = 0; i < nx; i++)
                {
                    double x = x0 + i * Step;
                    int k = j * nx + i;
                    heights[k] = (float)(Math.Sqrt(planeRadius * planeRadius + x * x + y * y) - planeRadius - Below);
                    double d = Distance(outline, x, y) - Full;
                    double w = d <= 0.0 ? 1.0 : d >= Fade ? 0.0 : 1.0 - d / Fade;
                    weights[k] = (float)(w * w * (3.0 - 2.0 * w));
                }
            }
            double[] frame = { up.x, up.y, up.z, east.x, east.y, east.z, north.x, north.y, north.z };
            return terrain.Patches.Set(key, frame, planeRadius, x0, y0, Step, nx, ny, heights, weights);
        }

        /// <summary>Distance from (x, y) to the polygon, 0 inside.</summary>
        static double Distance(List<Vector2d> poly, double x, double y)
        {
            bool inside = false;
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2d a = poly[i], b = poly[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
                double dx = b.x - a.x, dy = b.y - a.y;
                double len = dx * dx + dy * dy;
                double t = len > 0.0 ? Math.Max(0.0, Math.Min(1.0, ((x - a.x) * dx + (y - a.y) * dy) / len)) : 0.0;
                double ex = a.x + t * dx - x, ey = a.y + t * dy - y;
                best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
            }
            return inside ? 0.0 : best;
        }

        // ------------------------------------------------------------------ KSC

        static void UpdateKsc(DemTerrain terrain)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextKsc)
                return;
            nextKsc = now + 2f;
            CelestialBody earth = FlightGlobals.GetBodyByName("Earth");
            if (earth == null || earth.pqsController == null)
                return;
            if (ksc == null || ksc.sphere != earth.pqsController)
            {
                ksc = null;
                foreach (PQSCity c in earth.pqsController.GetComponentsInChildren<PQSCity>(true))
                    if (c.name == "KSC")
                        ksc = c;
                if (ksc == null)
                    return;
            }
            Transform pqsT = earth.pqsController.transform;
            Vector3d local = pqsT.InverseTransformPoint(ksc.transform.position);
            double r = local.magnitude;
            if (r < earth.Radius * 0.5)
                return; // not placed yet
            Vector3d up = local / r;
            Vector3d right = ((Vector3d)pqsT.InverseTransformDirection(ksc.transform.right)).normalized;
            if (double.IsNaN(kscGround))
            {
                if (!Measure(earth))
                    return;
            }
            bool moved = !kscSet || (up - kscDir).magnitude * r > 1.0 || Math.Abs(r - kscRadius) > 0.2 || (right - kscRight).magnitude > 1e-4;
            if (!moved)
                return;
            Vector3d forward = Vector3d.Cross(up, right).normalized; // KSC's own +z (north at the Cape)
            if (Vector3d.Dot(forward, (Vector3d)pqsT.InverseTransformDirection(ksc.transform.forward)) < 0.0)
                forward = -forward;
            var outline = new List<Vector2d>
            {
                new Vector2d(kscX0, kscZ0), new Vector2d(kscX1, kscZ0), new Vector2d(kscX1, kscZ1), new Vector2d(kscX0, kscZ1),
            };
            bool was = kscSet;
            kscSet = Lay(terrain, "SolSatellite:site:KSC", up, right, forward, r + kscGround, outline);
            kscDir = up;
            kscRight = right;
            kscRadius = r;
            Geo.LatLon(up, out double lat, out double lon);
            double native = terrain.NativeRadius(up) - earth.Radius;
            Debug.Log($"[SolSatellite] Site ground KSC at {lat:0.0000} {lon:0.0000}: {(kscSet ? "levelled" : "NOT levelled")}, ground " +
                      $"{r - earth.Radius + kscGround:0.00} m above the sea ({kscGround:0.00} above its origin), relief there without it {native:0.00} m");
            if (kscSet != was)
                terrain.RefreshExclusionsSoon();
        }

        /// <summary>KSC's ground above its origin: rays down over it, the lowest level many of
        /// them stop on its own colliders (aprons, runway, roads - not the roofs).</summary>
        static bool Measure(CelestialBody earth)
        {
            if (kscTries > 30)
                return false;
            Camera cam = Camera.main;
            if (cam == null || (cam.transform.position - ksc.transform.position).magnitude > 8000f)
                return false;
            kscTries++;
            Transform t = ksc.transform;
            Vector3 up = (t.position - earth.position).normalized;
            var heights = new List<float>();
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            for (float x = -1800f; x <= 1600f; x += 40f)
            {
                for (float z = -1000f; z <= 1000f; z += 40f)
                {
                    Vector3 from = t.position + t.right * x + t.forward * z + up * 400f;
                    RaycastHit[] hits = Physics.RaycastAll(from, -up, 800f, ~0, QueryTriggerInteraction.Ignore);
                    foreach (RaycastHit h in hits)
                    {
                        if (h.collider == null || !h.collider.transform.IsChildOf(t))
                            continue;
                        heights.Add(Vector3.Dot(h.point - t.position, up));
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minZ = Math.Min(minZ, z);
                        maxZ = Math.Max(maxZ, z);
                    }
                }
            }
            if (heights.Count < 200)
            {
                Debug.Log($"[SolSatellite] Site ground KSC: {heights.Count} rays on its colliders, not yet (try {kscTries})");
                return false;
            }
            // 0.25 m levels; the lowest one with a good share of the hits (the runway alone is most of them)
            heights.Sort();
            float lo = heights[0];
            var bins = new Dictionary<int, int>();
            int most = 0;
            foreach (float h in heights)
            {
                int b = (int)Math.Floor((h - lo) / 0.25f);
                bins.TryGetValue(b, out int c);
                bins[b] = ++c;
                most = Math.Max(most, c);
            }
            int level = int.MaxValue;
            foreach (KeyValuePair<int, int> kv in bins)
                if (kv.Value * 3 >= most && kv.Key < level)
                    level = kv.Key;
            kscGround = lo + level * 0.25f;
            kscX0 = Math.Min(kscX0, minX - 40.0);
            kscX1 = Math.Max(kscX1, maxX + 40.0);
            kscZ0 = Math.Min(kscZ0, minZ - 40.0);
            kscZ1 = Math.Max(kscZ1, maxZ + 40.0);
            Debug.Log($"[SolSatellite] Site ground KSC measured: {heights.Count} hits, levels {heights[heights.Count / 10]:0.00} / " +
                      $"{heights[heights.Count / 2]:0.00} / {heights[heights.Count * 9 / 10]:0.00} m above its origin (10/50/90 %), ground at " +
                      $"{kscGround:0.00}; its colliders reach x {minX:0}..{maxX:0}, z {minZ:0}..{maxZ:0} m");
            WriteCache();
            return true;
        }

        static void ReadCache()
        {
            try
            {
                if (!File.Exists(cacheFile))
                    return;
                var ci = CultureInfo.InvariantCulture;
                foreach (string line in File.ReadAllLines(cacheFile))
                {
                    string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 6 && f[0] == "ksc_ground")
                    {
                        kscGround = double.Parse(f[1], ci);
                        kscX0 = Math.Min(kscX0, double.Parse(f[2], ci));
                        kscX1 = Math.Max(kscX1, double.Parse(f[3], ci));
                        kscZ0 = Math.Min(kscZ0, double.Parse(f[4], ci));
                        kscZ1 = Math.Max(kscZ1, double.Parse(f[5], ci));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] site_grounds.txt: " + e.Message);
            }
        }

        static void WriteCache()
        {
            try
            {
                var ci = CultureInfo.InvariantCulture;
                File.WriteAllText(cacheFile, "# KSC's ground above its origin (game m) and its extent in its own frame; delete to measure again\n" +
                    string.Format(ci, "ksc_ground {0:0.###} {1:0} {2:0} {3:0} {4:0}\n", kscGround, kscX0, kscX1, kscZ0, kscZ1));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] site_grounds.txt: " + e.Message);
            }
        }
    }
}
