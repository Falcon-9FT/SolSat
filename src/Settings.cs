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
    /// <summary>Reads PluginData/Tools/settings.ini, the file the solsat tool uses too.</summary>
    sealed class Settings
    {
        public const float MinRadiusKm = 0.1f; // loading radii are whatever the user types, above this

        /// <summary>What to do with EVE's city lights on the ground (Sol's night "underlay").</summary>
        public enum EveCityLightsMode { Sol, Smooth, Off }

        public string SourceName = "google";
        public string Url;
        public string Subdomains = "";
        public int SourceMaxZoom = 19;
        public string UserAgent = "SolSatellite/0.2 (personal KSP mod)";

        public bool StreamEnabled = true;
        public int StreamMaxZoom = 18;
        public float StreamRequestsPerSecond = 6f;
        public int StreamConnections = 4;
        public float ImageryStrength = 1f;
        public int MemoryTiles = 1024;
        public int LevelSize = 2048;          // texels per clipmap level side: twice as many, every zone twice as wide
        public int Levels = Clipmap.MaxLevels;
        public double FinestLevelMeters = 0.0; // width of the finest level (game m); 0: as max_zoom needs
        public float DetailAltitude = 10f;    // a level is used while the camera is lower over the ground than this times its width
        public bool OrbitDetail = true;     // clipmap imagery over the orbital cube, seen from orbit

        public bool BuildingsEnabled = true;
        public float BuildingRadiusKm = 4f;
        public bool BuildingColliders = true;
        public bool LampsEnabled = true;
        public float LampRadiusKm = 8f;
        public float LampBrightness = 1f;
        public bool RoadsEnabled = true;    // roads drawn over the imagery
        public float RoadRadiusKm = 10f;
        public float RoadStrength = 0.8f;   // how much the asphalt covers the imagery's own road
        public bool Roads3D = true;         // roads and railways as 3D geometry near the camera
        public float Road3DRadiusKm = 2.5f;
        public EveCityLightsMode EveCityLights = EveCityLightsMode.Smooth;
        public float ImageryCacheGb = 20f; // disk limits; 0 = no limit
        public float OsmCacheGb = 5f;
        public bool VectorTiles = true;
        public string VectorTileJson = "https://tiles.openfreemap.org/planet";
        public bool OverpassDetail = true;
        public string OverpassUrl = "https://overpass-api.de/api/interpreter";
        public bool OvertureEnabled = true;  // buildings from Overture Maps (OSM + Microsoft + Google footprints)
        public string OvertureUrl = "https://overturemaps-extras-us-west-2.s3.us-west-2.amazonaws.com";
        public int OvertureConnections = 4;
        public bool GuessRoofs = true;       // pitched roofs on untagged houses where that's usual
        public bool ScatterMask = true;      // Parallax's grass, trees and rocks only where the map says (ScatterMask.cs)

        // Real terrain (Dem.cs).
        public bool TerrainEnabled = true;
        public string TerrainSource = "mapterhorn";
        public string TerrainUrl = "https://tiles.mapterhorn.com/{z}/{x}/{y}.webp";
        public string TerrainUserAgent = "SolSatellite/0.3 (personal KSP terrain mod)";
        public int TerrainConnections = 4;
        public float TerrainRequestsPerSecond = 10f;
        public int TerrainMemoryTiles = 256;    // decoded tiles kept, 0.5 MB each
        public int TerrainMinZoom = 7;          // coarsest tiles: the real relief reaches this far out
        public float TerrainPrefetch = 4f;      // tiles loaded this many times farther out than the PQS needs them
        public float TerrainLandLift = 0.5f;    // m (game): land kept above the sea
        public float TerrainShoreDepth = 1f;    // m (game): water at least this deep
        public float TerrainCacheGb = 5f;
        public List<DemRegion> TerrainRegions = DemRegion.Defaults();

        string path;

        /// <summary>Texels per level side the GPU can have: 256 up to D3D11's 16384.</summary>
        public static int ClampLevelTexels(int texels) => Math.Max(256, Math.Min(16384, texels));

        public string TileUrl(int z, int x, int y)
        {
            string url = Url.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
            if (Subdomains.Length > 0)
                url = url.Replace("{s}", Subdomains[(x + y) % Subdomains.Length].ToString());
            return url;
        }

        public static Settings Load(string path)
        {
            var ini = ReadIni(path);
            var s = new Settings { path = path };
            s.SourceName = Get(ini, "general", "source", s.SourceName);
            string src = "source." + s.SourceName;
            s.Url = Get(ini, src, "url", null);
            s.Subdomains = Get(ini, src, "subdomains", "");
            s.SourceMaxZoom = GetInt(ini, src, "max_zoom", s.SourceMaxZoom);
            s.UserAgent = Get(ini, src, "user_agent", s.UserAgent);

            s.StreamEnabled = Get(ini, "stream", "enabled", "true").Trim().ToLowerInvariant() is "true" or "yes" or "1";
            s.StreamMaxZoom = Math.Min(GetInt(ini, "stream", "max_zoom", s.StreamMaxZoom), s.SourceMaxZoom);
            s.StreamRequestsPerSecond = (float)GetDouble(ini, "stream", "requests_per_second", s.StreamRequestsPerSecond);
            s.StreamConnections = GetInt(ini, "stream", "connections", s.StreamConnections);
            s.FinestLevelMeters = Math.Max(0.0, GetDouble(ini, "stream", "finest_level_m", s.FinestLevelMeters));
            s.LevelSize = ClampLevelTexels(GetInt(ini, "stream", "level_texels", s.LevelSize));
            s.DetailAltitude = Math.Max(0.5f, (float)GetDouble(ini, "stream", "detail_altitude", s.DetailAltitude));
            s.ImageryStrength = Mathf.Clamp01((float)GetDouble(ini, "stream", "strength", s.ImageryStrength));
            s.MemoryTiles = Math.Max(128, GetInt(ini, "stream", "memory_tiles", s.MemoryTiles));
            s.OrbitDetail = Flag(Get(ini, "stream", "orbit_detail", "true"));
            s.BuildingsEnabled = Flag(Get(ini, "osm", "buildings", "true"));
            s.BuildingRadiusKm = Math.Max((float)GetDouble(ini, "osm", "radius_km", s.BuildingRadiusKm), MinRadiusKm);
            s.BuildingColliders = Flag(Get(ini, "osm", "colliders", "true"));
            s.LampsEnabled = Flag(Get(ini, "osm", "lamps", "true"));
            s.LampRadiusKm = Math.Max((float)GetDouble(ini, "osm", "lamp_radius_km", s.LampRadiusKm), MinRadiusKm);
            s.LampBrightness = Mathf.Clamp((float)GetDouble(ini, "osm", "lamp_brightness", s.LampBrightness), 0f, 3f);
            s.RoadsEnabled = Flag(Get(ini, "osm", "roads", "true"));
            s.RoadRadiusKm = Math.Max((float)GetDouble(ini, "osm", "road_radius_km", s.RoadRadiusKm), MinRadiusKm);
            s.RoadStrength = Mathf.Clamp01((float)GetDouble(ini, "osm", "road_strength", s.RoadStrength));
            s.Roads3D = Flag(Get(ini, "osm", "roads_3d", "true"));
            s.Road3DRadiusKm = Math.Max((float)GetDouble(ini, "osm", "road_3d_radius_km", s.Road3DRadiusKm), MinRadiusKm);
            string eve = Get(ini, "osm", "eve_city_lights", "smooth").Trim().ToLowerInvariant();
            s.EveCityLights = eve == "sol" ? EveCityLightsMode.Sol : eve == "off" ? EveCityLightsMode.Off : EveCityLightsMode.Smooth;
            s.ImageryCacheGb = Math.Max(0f, (float)GetDouble(ini, "cache", "imagery_gb", s.ImageryCacheGb));
            s.OsmCacheGb = Math.Max(0f, (float)GetDouble(ini, "cache", "osm_gb", s.OsmCacheGb));
            s.VectorTiles = Flag(Get(ini, "osm", "vector_tiles", "true"));
            s.VectorTileJson = Get(ini, "osm", "vector_tilejson", s.VectorTileJson);
            s.OverpassDetail = Flag(Get(ini, "osm", "overpass_detail", "true"));
            s.OverpassUrl = Get(ini, "osm", "overpass_url", s.OverpassUrl);
            s.OvertureEnabled = Flag(Get(ini, "osm", "overture", "true"));
            s.OvertureUrl = Get(ini, "osm", "overture_url", s.OvertureUrl);
            s.OvertureConnections = Math.Max(1, GetInt(ini, "osm", "overture_connections", s.OvertureConnections));
            s.GuessRoofs = Flag(Get(ini, "osm", "guess_roofs", "true"));
            s.ScatterMask = Flag(Get(ini, "osm", "scatter_mask", "true"));
            s.TerrainEnabled = Flag(Get(ini, "terrain", "enabled", "true"));
            s.TerrainSource = Get(ini, "terrain", "source", s.TerrainSource);
            s.TerrainUrl = Get(ini, "terrain", "url", s.TerrainUrl);
            s.TerrainUserAgent = Get(ini, "terrain", "user_agent", s.TerrainUserAgent);
            s.TerrainConnections = Math.Max(1, GetInt(ini, "terrain", "connections", s.TerrainConnections));
            s.TerrainRequestsPerSecond = Math.Max(0.1f, (float)GetDouble(ini, "terrain", "requests_per_second", s.TerrainRequestsPerSecond));
            s.TerrainMemoryTiles = Math.Max(32, GetInt(ini, "terrain", "memory_tiles", s.TerrainMemoryTiles));
            s.TerrainMinZoom = Math.Max(0, Math.Min(17, GetInt(ini, "terrain", "min_zoom", s.TerrainMinZoom)));
            s.TerrainPrefetch = Math.Max(1f, (float)GetDouble(ini, "terrain", "prefetch", s.TerrainPrefetch));
            s.TerrainLandLift = Math.Max(0f, (float)GetDouble(ini, "terrain", "land_lift_m", s.TerrainLandLift));
            s.TerrainShoreDepth = Math.Max(0f, (float)GetDouble(ini, "terrain", "shore_depth_m", s.TerrainShoreDepth));
            s.TerrainCacheGb = Math.Max(0f, (float)GetDouble(ini, "cache", "terrain_gb", s.TerrainCacheGb));
            foreach (DemRegion r in s.TerrainRegions)
            {
                // region.<id> = levels zoom detail
                string[] parts = (Get(ini, "terrain", "region." + r.Id, "") ?? "").Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                var ci = CultureInfo.InvariantCulture;
                if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, ci, out int levels))
                    r.Levels = Math.Max(0, levels);
                if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, ci, out int zoom))
                    r.Zoom = Math.Max(0, Math.Min(17, zoom));
                if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, ci, out float detail))
                    r.Detail = Math.Max(1f, detail);
            }
            if (s.Url == null)
            {
                Debug.LogError($"[SolSatellite] No url for source '{s.SourceName}' in {path}; streaming disabled");
                s.StreamEnabled = false;
            }
            return s;
        }

        static bool Flag(string value) => value.Trim().ToLowerInvariant() is "true" or "yes" or "1";

        /// <summary>Writes the in-game adjustable values back into [stream] and [osm], keeping
        /// the rest of the file (comments included) as it is.</summary>
        public void Save()
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>(File.ReadAllLines(path));
            SetSection(lines, "stream", new Dictionary<string, string>
            {
                ["enabled"] = StreamEnabled ? "true" : "false",
                ["max_zoom"] = StreamMaxZoom.ToString(ci),
                ["requests_per_second"] = StreamRequestsPerSecond.ToString("0.#", ci),
                ["connections"] = StreamConnections.ToString(ci),
                ["strength"] = ImageryStrength.ToString("0.##", ci),
                ["memory_tiles"] = MemoryTiles.ToString(ci),
                ["orbit_detail"] = OrbitDetail ? "true" : "false",
                ["level_texels"] = LevelSize.ToString(ci),
                ["finest_level_m"] = FinestLevelMeters.ToString("0.##", ci),
                ["detail_altitude"] = DetailAltitude.ToString("0.##", ci),
            });
            SetSection(lines, "osm", new Dictionary<string, string>
            {
                ["buildings"] = BuildingsEnabled ? "true" : "false",
                ["radius_km"] = BuildingRadiusKm.ToString("0.#", ci),
                ["colliders"] = BuildingColliders ? "true" : "false",
                ["lamps"] = LampsEnabled ? "true" : "false",
                ["lamp_radius_km"] = LampRadiusKm.ToString("0.#", ci),
                ["lamp_brightness"] = LampBrightness.ToString("0.##", ci),
                ["roads"] = RoadsEnabled ? "true" : "false",
                ["road_radius_km"] = RoadRadiusKm.ToString("0.#", ci),
                ["road_strength"] = RoadStrength.ToString("0.##", ci),
                ["roads_3d"] = Roads3D ? "true" : "false",
                ["road_3d_radius_km"] = Road3DRadiusKm.ToString("0.#", ci),
                ["eve_city_lights"] = EveCityLights.ToString().ToLowerInvariant(),
                ["vector_tiles"] = VectorTiles ? "true" : "false",
                ["vector_tilejson"] = VectorTileJson,
                ["overpass_detail"] = OverpassDetail ? "true" : "false",
                ["overpass_url"] = OverpassUrl,
                ["overture"] = OvertureEnabled ? "true" : "false",
                ["overture_url"] = OvertureUrl,
                ["overture_connections"] = OvertureConnections.ToString(ci),
                ["guess_roofs"] = GuessRoofs ? "true" : "false",
                ["scatter_mask"] = ScatterMask ? "true" : "false",
            });
            var terrain = new Dictionary<string, string>
            {
                ["enabled"] = TerrainEnabled ? "true" : "false",
                ["source"] = TerrainSource,
                ["url"] = TerrainUrl,
                ["connections"] = TerrainConnections.ToString(ci),
                ["requests_per_second"] = TerrainRequestsPerSecond.ToString("0.#", ci),
                ["memory_tiles"] = TerrainMemoryTiles.ToString(ci),
                ["min_zoom"] = TerrainMinZoom.ToString(ci),
                ["prefetch"] = TerrainPrefetch.ToString("0.#", ci),
                ["land_lift_m"] = TerrainLandLift.ToString("0.##", ci),
                ["shore_depth_m"] = TerrainShoreDepth.ToString("0.##", ci),
            };
            foreach (DemRegion r in TerrainRegions)
                terrain["region." + r.Id] = $"{r.Levels.ToString(ci)} {r.Zoom.ToString(ci)} {r.Detail.ToString("0.##", ci)}";
            SetSection(lines, "terrain", terrain);
            SetSection(lines, "cache", new Dictionary<string, string>
            {
                ["imagery_gb"] = ImageryCacheGb.ToString("0.##", ci),
                ["osm_gb"] = OsmCacheGb.ToString("0.##", ci),
                ["terrain_gb"] = TerrainCacheGb.ToString("0.##", ci),
            });
            File.WriteAllLines(path, lines.ToArray());
        }

        static void SetSection(List<string> lines, string section, Dictionary<string, string> values)
        {
            int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                lines.Add("");
                lines.Add($"[{section}]");
                start = lines.Count - 1;
            }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
            if (end < 0)
                end = lines.Count;
            // Section ends before any trailing blank lines, so new keys go right after the last value.
            while (end > start + 1 && lines[end - 1].Trim().Length == 0)
                end--;
            foreach (var kv in values)
            {
                int at = lines.FindIndex(start + 1, end - start - 1, l =>
                {
                    string t = l.Trim();
                    int eq = t.IndexOf('=');
                    return t.Length > 0 && t[0] != ';' && t[0] != '#' && eq > 0 &&
                           t.Substring(0, eq).Trim().Equals(kv.Key, StringComparison.OrdinalIgnoreCase);
                });
                if (at >= 0)
                {
                    lines[at] = $"{kv.Key} = {kv.Value}";
                }
                else
                {
                    lines.Insert(end, $"{kv.Key} = {kv.Value}");
                    end++;
                }
            }
        }

        static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> section = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!result.TryGetValue(name, out section))
                        result[name] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0 || section == null)
                    continue;
                string value = line.Substring(eq + 1);
                // Inline comments need whitespace before them, as in Python's configparser.
                foreach (string marker in new[] { " ;", "\t;", " #", "\t#" })
                {
                    int c = value.IndexOf(marker, StringComparison.Ordinal);
                    if (c >= 0)
                        value = value.Substring(0, c);
                }
                section[line.Substring(0, eq).Trim()] = value.Trim();
            }
            return result;
        }

        static string Get(Dictionary<string, Dictionary<string, string>> ini, string section, string key, string fallback) =>
            ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;

        static int GetInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback) =>
            int.TryParse(Get(ini, section, key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        static double GetDouble(Dictionary<string, Dictionary<string, string>> ini, string section, string key, double fallback) =>
            double.TryParse(Get(ini, section, key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }
}
