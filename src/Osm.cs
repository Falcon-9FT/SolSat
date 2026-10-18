// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SolSatellite
{
    /// <summary>A building outline or building part, with its OSM tags not interpreted yet
    /// (BuildingMesher does that), so the cache stays valid when the heuristics change.</summary>
    sealed class OsmBuilding
    {
        public long Id;
        public bool Part;
        public string Type = "yes"; // value of building=* or building:part=*
        public float Height = float.NaN, MinHeight = float.NaN;
        public float Levels = float.NaN, MinLevel = float.NaN, RoofLevels = float.NaN;
        public uint WallColor, RoofColor; // 0xRRGGBB00 | 0xFF when tagged, 0 when not
        public string RoofShape;          // roof:shape ("gabled", "hipped", ...), null when not tagged
        public float RoofHeight = float.NaN;    // roof:height (m)
        public float RoofDirection = float.NaN; // roof:direction: degrees from north the slope faces
        public byte RoofOrientation;      // roof:orientation: 0 not tagged, 1 along, 2 across
        public string WallMaterial, RoofMaterial; // building:material, roof:material
        /// <summary>Outer ring first, then holes; each flat lat,lon pairs in 1e-7 degrees,
        /// without the repeated closing point.</summary>
        public readonly List<int[]> Rings = new List<int[]>();
        /// <summary>Vector tiles cut buildings at the tile's border: Rings are then this tile's
        /// piece and Whole the building's whole outline (as far as the tile's margin shows it),
        /// which roofs and the ground level are worked out on, so the pieces meet. Null when
        /// Rings are the whole building.</summary>
        public int[] Whole;
        /// <summary>A piece of something bigger than the tile's margin shows: no pitched roof,
        /// the neighbouring tile couldn't match it.</summary>
        public bool Cut;

        // Buildings of tiles done with (meshed) are kept for the next tiles read (Rent, Return),
        // their outline arrays too (IntPool): reading a city tile again for another detail band
        // made ~11 000 buildings, their ring lists and arrays anew - 2.6 MB of garbage a time,
        // dozens of times a second flying over Bangkok (2026-10-03).
        const int MaxPooled = 65536;
        static readonly Stack<OsmBuilding> pool = new Stack<OsmBuilding>();

        /// <summary>A building as new (from the pool, or new).</summary>
        public static OsmBuilding Rent()
        {
            lock (pool)
            {
                if (pool.Count > 0)
                    return pool.Pop();
            }
            return new OsmBuilding();
        }

        /// <summary>A tile's buildings, nothing else referring to them any more (its meshes are
        /// made), back to the pool with their outline arrays; the list is emptied.</summary>
        public static void Return(List<OsmBuilding> buildings)
        {
            int room;
            lock (pool)
                room = MaxPooled - pool.Count;
            int n = Math.Min(room, buildings.Count);
            for (int i = 0; i < n; i++)
                buildings[i].Reset();
            lock (pool)
            {
                for (int i = 0; i < n && pool.Count < MaxPooled; i++)
                    pool.Push(buildings[i]);
            }
            buildings.Clear();
        }

        void Reset()
        {
            foreach (int[] ring in Rings)
                IntPool.Return(ring);
            if (Whole != null)
                IntPool.Return(Whole);
            Id = 0;
            Part = false;
            Type = "yes";
            Height = MinHeight = float.NaN;
            Levels = MinLevel = RoofLevels = float.NaN;
            WallColor = RoofColor = 0;
            RoofShape = null;
            RoofHeight = RoofDirection = float.NaN;
            RoofOrientation = 0;
            WallMaterial = RoofMaterial = null;
            Rings.Clear();
            Whole = null;
            Cut = false;
        }
    }

    /// <summary>Int arrays of buildings' outlines kept for the next tile (OsmBuilding.Return),
    /// by exact length (the meshers go by an array's length).</summary>
    static class IntPool
    {
        const int MaxLength = 256;        // ints; longer outlines are rare and made anew
        const int MaxPerLength = 32768;
        static readonly Stack<int[]>[] byLength = new Stack<int[]>[MaxLength + 1];

        public static int[] Rent(int n)
        {
            if (n <= MaxLength)
            {
                Stack<int[]> s = byLength[n];
                if (s != null)
                {
                    lock (s)
                    {
                        if (s.Count > 0)
                            return s.Pop();
                    }
                }
            }
            return new int[n];
        }

        public static void Return(int[] a)
        {
            int n = a.Length;
            if (n < 2 || n > MaxLength)
                return;
            Stack<int[]> s = byLength[n];
            if (s == null)
            {
                lock (byLength)
                    s = byLength[n] ??= new Stack<int[]>();
            }
            lock (s)
            {
                if (s.Count < MaxPerLength)
                    s.Push(a);
            }
        }
    }

    /// <summary>Overpass has every tag (building types, roof colours, lamps, lit roads);
    /// OpenFreeMap's vector tiles only heights and colours, but come quickly.</summary>
    enum OsmSource : byte { Overpass = 0, OpenFreeMap = 1 }

    sealed class OsmRoad
    {
        public long Id;
        public string Kind = ""; // highway=*
        public byte Lit;         // 0 unknown, 1 lit=yes, 2 lit=no
        public byte Lanes;
        public int[] Points;     // flat lat,lon pairs in 1e-7 degrees
    }

    /// <summary>What the ground of an area is, as far as Parallax's scatters (grass, trees,
    /// rocks) care (ScatterMask.cs): from OpenMapTiles' landcover, landuse, water and aeroway
    /// layers.</summary>
    enum AreaKind : byte { Forest = 1, Park = 2, BuiltUp = 3, Airfield = 4, Paved = 5, Water = 6 }

    sealed class OsmArea
    {
        public AreaKind Kind;
        /// <summary>Outer ring first, then its holes; flat lat,lon pairs in 1e-7 degrees, cut to the tile.</summary>
        public readonly List<int[]> Rings = new List<int[]>();
    }

    /// <summary>
    /// Buildings, roads and street lamps of one z14 Web Mercator tile from OpenStreetMap
    /// (via the Overpass API or OpenFreeMap's vector tiles), cached in a compact binary file.
    /// From Overpass, a building belongs to the tile holding the centre of its bounding box,
    /// so neighbouring tiles never both build it; vector tiles come clipped to the tile.
    /// </summary>
    sealed class OsmTile
    {
        public const int Zoom = 14;
        const int Magic = 0x4D534F53; // "SOSM"
        // 2 added Source (version 1 files are all from Overpass), 3 taxiways and aprons, 4 railways,
        // 5 bridges, 6 roof shapes and materials, whole outlines of cut buildings, Overture buildings,
        // 7 areas (forests, towns, water, airfields) and runway/taxiway lines for the scatter mask.
        const int FormatVersion = 7;

        const string Roads = "motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|pedestrian|" +
                             "motorway_link|trunk_link|primary_link|secondary_link|tertiary_link";
        const string Railways = "rail|light_rail|narrow_gauge|subway|tram|preserved";

        public int X, Y;
        public OsmSource Source;
        public long FetchedUtcTicks;
        public readonly List<OsmBuilding> Buildings = new List<OsmBuilding>();
        public readonly List<OsmRoad> RoadList = new List<OsmRoad>(); // roads and railways (IsRailway)
        public readonly List<int> Lamps = new List<int>(); // lat,lon pairs in 1e-7 degrees
        /// <summary>Only kept so cache files keep their layout: aprons (and taxiways, Kind
        /// "taxiway" in RoadList) of older files; nothing fills or reads them any more.</summary>
        public readonly List<int[]> Aprons = new List<int[]>();
        public bool HasAeroway;
        /// <summary>False for tiles cached before railways were kept.</summary>
        public bool HasRail;
        /// <summary>Where roads and railways run on bridges: their lines (lat,lon pairs in 1e-7
        /// degrees), whatever the source; the 3D roads lift what lies along them.</summary>
        public readonly List<int[]> Bridges = new List<int[]>();
        /// <summary>False for tiles cached before bridges were kept.</summary>
        public bool HasBridges;
        /// <summary>Forests, parks, towns, airfields, paved aprons and water (vector tiles only).</summary>
        public readonly List<OsmArea> Areas = new List<OsmArea>();
        /// <summary>Runway and taxiway centre lines (Kind "runway", "taxiway"), for the scatter mask only.</summary>
        public readonly List<OsmRoad> AerowayLines = new List<OsmRoad>();
        /// <summary>False for tiles cached before areas were kept, and for Overpass-only data.</summary>
        public bool HasAreas;
        /// <summary>Overture Maps was asked for the tile's buildings (Overture.cs): the buildings
        /// are Overture's - OpenStreetMap's with every tag, Microsoft's and Google's footprints
        /// where OSM has none - unless it had no tile here. OSM data coming later keeps them.</summary>
        public bool HasOverture;

        /// <summary>Railway kinds as RoadList keeps them: "rail", "light_rail", "narrow_gauge",
        /// "subway" (where it runs in the open), "tram", and "rail_siding" for yard, siding and
        /// spur tracks of any of them.</summary>
        public static bool IsRailway(string kind) =>
            kind == "rail" || kind == "rail_siding" || kind == "light_rail" || kind == "narrow_gauge" || kind == "subway" || kind == "tram";

        public static double LonOf(double x) => x / (1 << Zoom) * 360.0 - 180.0;
        public static double LatOf(double y) => Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / (1 << Zoom)))) * 180.0 / Math.PI;
        public static double TileX(double lon) => (lon + 180.0) / 360.0 * (1 << Zoom);

        public static double TileY(double lat)
        {
            double phi = Math.Max(-85.0511287798, Math.Min(85.0511287798, lat)) * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(phi) + 1.0 / Math.Cos(phi)) / Math.PI) * 0.5 * (1 << Zoom);
        }

        public double South => LatOf(Y + 1);
        public double North => LatOf(Y);
        public double West => LonOf(X);
        public double East => LonOf(X + 1);

        /// <summary>Overpass query for a tile. The server turns queries away when busy, the
        /// more likely the more time and memory they declare: ask for little first, and for
        /// the defaults only after running out ("heavy"). Without buildings (the tile has
        /// Overture's already) the query is a fraction of the work for the server.</summary>
        public static string Query(int x, int y, bool heavy, bool buildings = true)
        {
            var ci = CultureInfo.InvariantCulture;
            string bbox = string.Format(ci, "{0:0.0000000},{1:0.0000000},{2:0.0000000},{3:0.0000000}",
                LatOf(y + 1), LonOf(x), LatOf(y), LonOf(x + 1));
            string limits = heavy ? "[timeout:180][maxsize:536870912]" : "[timeout:60][maxsize:134217728]";
            // Ways and nodes as "tags geom" (no node id lists: ~20% less); relations need the full
            // "geom" form, the only one that includes their members' geometry.
            string query = $"[out:json]{limits};(" +
                           (buildings ? $"way[\"building\"]({bbox});way[\"building:part\"]({bbox});" : "") +
                           $"way[\"highway\"~\"^({Roads})$\"]({bbox});" +
                           $"way[\"railway\"~\"^({Railways})$\"]({bbox});" +
                           $"node[\"highway\"=\"street_lamp\"]({bbox});" +
                           ");out tags geom qt;";
            if (buildings)
                query += "(" +
                         $"relation[\"type\"=\"multipolygon\"][\"building\"]({bbox});" +
                         $"relation[\"type\"=\"multipolygon\"][\"building:part\"]({bbox});" +
                         ");out geom qt;";
            return query;
        }

        // ---------------------------------------------------------------- Overpass reply

        sealed class Element
        {
            public char Type; // 'n', 'w', 'r'
            public long Id;
            public int Lat, Lon; // nodes
            public List<int> Geometry; // ways: flat lat,lon
            public List<Member> Members; // relations
            public Dictionary<string, string> Tags;

            public string Tag(string key) => Tags != null && Tags.TryGetValue(key, out string v) ? v : null;
        }

        sealed class Member
        {
            public string Role = "";
            public List<int> Geometry;
        }

        /// <summary>Parses an Overpass JSON reply (possibly gzipped). Throws when the reply is
        /// not what we asked for, including Overpass' own "runtime error" remarks.</summary>
        public static OsmTile Parse(int x, int y, byte[] data)
        {
            if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                data = Gunzip(data);
            var elements = new List<Element>();
            string remark = null;
            var j = new Json(data);
            if (j.BeginObject())
            {
                do
                {
                    string key = j.Key();
                    if (key == "elements")
                    {
                        if (j.BeginArray())
                            do elements.Add(ReadElement(j)); while (j.Next(']'));
                    }
                    else if (key == "remark")
                    {
                        remark = j.String();
                    }
                    else
                    {
                        j.Skip();
                    }
                } while (j.Next('}'));
            }
            if (remark != null && remark.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException("Overpass: " + remark);

            var tile = new OsmTile { X = x, Y = y, FetchedUtcTicks = DateTime.UtcNow.Ticks, HasAeroway = true, HasRail = true, HasBridges = true };
            var outlines = new List<OsmBuilding>();
            var parts = new List<OsmBuilding>();
            foreach (Element e in elements)
            {
                if (e.Type == 'n')
                {
                    if (e.Tag("highway") == "street_lamp")
                    {
                        tile.Lamps.Add(e.Lat);
                        tile.Lamps.Add(e.Lon);
                    }
                    continue;
                }
                string building = e.Tag("building");
                string part = e.Tag("building:part");
                bool isOutline = building != null && building != "no";
                bool isPart = !isOutline && part != null && part != "no";
                if (isOutline || isPart)
                {
                    if (e.Tag("location") == "underground")
                        continue;
                    foreach (OsmBuilding b in Shapes(e))
                    {
                        b.Part = isPart;
                        b.Type = isOutline ? building : part;
                        ReadTags(b, e);
                        (isPart ? parts : outlines).Add(b);
                    }
                    continue;
                }
                string tunnel = e.Tag("tunnel");
                bool underground = tunnel != null && tunnel != "no";
                string bridge = e.Tag("bridge");
                bool onBridge = bridge != null && bridge != "no";
                string railway = e.Tag("railway");
                if (e.Type == 'w' && railway != null && e.Geometry != null && e.Geometry.Count >= 4)
                {
                    string kind = RailwayKind(railway, e.Tag("service"));
                    if (kind != null && !underground)
                    {
                        tile.RoadList.Add(new OsmRoad { Id = e.Id, Kind = kind, Points = e.Geometry.ToArray() });
                        if (onBridge)
                            tile.Bridges.Add(e.Geometry.ToArray());
                    }
                    continue;
                }
                string highway = e.Tag("highway");
                if (e.Type == 'w' && highway != null && e.Geometry != null && e.Geometry.Count >= 4)
                {
                    if (underground)
                        continue; // tunnels: nothing of them shows
                    if (onBridge)
                        tile.Bridges.Add(e.Geometry.ToArray());
                    string lit = e.Tag("lit");
                    tile.RoadList.Add(new OsmRoad
                    {
                        Id = e.Id,
                        Kind = highway,
                        Lit = (byte)(lit == null ? 0 : lit == "no" ? 2 : 1),
                        Lanes = (byte)Math.Max(0, Math.Min(255, (int)ParseNumber(e.Tag("lanes"), 0f))),
                        Points = e.Geometry.ToArray(),
                    });
                }
            }

            // Parts get drawn instead of the outline they belong to (Simple 3D Buildings); the
            // outline goes when any part's centre lies inside it. Parts from the whole reply
            // count, even ones that belong to a neighbouring tile.
            var partCentres = new List<(double lat, double lon)>(parts.Count);
            foreach (OsmBuilding p in parts)
            {
                Centre(p.Rings[0], out double lat, out double lon);
                partCentres.Add((lat, lon));
            }
            foreach (OsmBuilding b in outlines)
            {
                if (!InThisTile(tile, b))
                    continue;
                if (partCentres.Count > 0 && HasPartInside(b.Rings[0], partCentres))
                    continue;
                tile.Buildings.Add(b);
            }
            foreach (OsmBuilding p in parts)
                if (InThisTile(tile, p))
                    tile.Buildings.Add(p);
            return tile;
        }

        static Element ReadElement(Json j)
        {
            var e = new Element();
            if (!j.BeginObject())
                return e;
            do
            {
                switch (j.Key())
                {
                    case "type":
                        string t = j.String();
                        e.Type = t == "node" ? 'n' : t == "way" ? 'w' : t == "relation" ? 'r' : '?';
                        break;
                    case "id":
                        e.Id = j.Long();
                        break;
                    case "lat":
                        e.Lat = (int)j.Fixed(7);
                        break;
                    case "lon":
                        e.Lon = (int)j.Fixed(7);
                        break;
                    case "geometry":
                        e.Geometry = ReadGeometry(j);
                        break;
                    case "members":
                        e.Members = new List<Member>();
                        if (j.BeginArray())
                            do e.Members.Add(ReadMember(j)); while (j.Next(']'));
                        break;
                    case "tags":
                        e.Tags = new Dictionary<string, string>();
                        if (j.BeginObject())
                            do
                            {
                                string k = j.Key();
                                e.Tags[k] = j.String();
                            } while (j.Next('}'));
                        break;
                    default:
                        j.Skip();
                        break;
                }
            } while (j.Next('}'));
            return e;
        }

        static Member ReadMember(Json j)
        {
            var m = new Member();
            if (!j.BeginObject())
                return m;
            do
            {
                switch (j.Key())
                {
                    case "role":
                        m.Role = j.String();
                        break;
                    case "geometry":
                        m.Geometry = ReadGeometry(j);
                        break;
                    default:
                        j.Skip();
                        break;
                }
            } while (j.Next('}'));
            return m;
        }

        static List<int> ReadGeometry(Json j)
        {
            var points = new List<int>();
            if (!j.BeginArray())
                return points;
            do
            {
                if (j.TryNull())
                    continue; // node outside the requested area (not with plain "out geom")
                int lat = 0, lon = 0;
                if (j.BeginObject())
                    do
                    {
                        string k = j.Key();
                        if (k == "lat")
                            lat = (int)j.Fixed(7);
                        else if (k == "lon")
                            lon = (int)j.Fixed(7);
                        else
                            j.Skip();
                    } while (j.Next('}'));
                points.Add(lat);
                points.Add(lon);
            } while (j.Next(']'));
            return points;
        }

        /// <summary>Closed shapes of a way or multipolygon: one per outer ring, holes attached.</summary>
        static IEnumerable<OsmBuilding> Shapes(Element e)
        {
            if (e.Type == 'w')
            {
                int[] ring = CloseRing(e.Geometry);
                if (ring != null)
                {
                    var b = new OsmBuilding { Id = e.Id };
                    b.Rings.Add(ring);
                    yield return b;
                }
                yield break;
            }
            if (e.Type != 'r' || e.Members == null || e.Tag("type") != "multipolygon")
                yield break;

            var outerWays = new List<List<long>>();
            var innerWays = new List<List<long>>();
            foreach (Member m in e.Members)
            {
                if (m.Geometry == null || m.Geometry.Count < 4)
                    continue;
                var way = new List<long>(m.Geometry.Count / 2);
                for (int i = 0; i < m.Geometry.Count; i += 2)
                    way.Add(Pack(m.Geometry[i], m.Geometry[i + 1]));
                (m.Role == "inner" ? innerWays : outerWays).Add(way);
            }
            List<int[]> outers = AssembleRings(outerWays);
            List<int[]> inners = AssembleRings(innerWays);
            int n = 0;
            foreach (int[] outer in outers)
            {
                // The id only seeds colour, height and lit windows; consecutive ones will do.
                var b = new OsmBuilding { Id = e.Id + n++ };
                b.Rings.Add(outer);
                foreach (int[] inner in inners)
                    if (PointInRing(outer, inner[0] * 1e-7, inner[1] * 1e-7))
                        b.Rings.Add(inner);
                yield return b;
            }
        }

        static long Pack(int lat, int lon) => ((long)lat << 32) | (uint)lon;

        /// <summary>Joins way segments end to end into closed rings; drops what doesn't close.</summary>
        static List<int[]> AssembleRings(List<List<long>> ways)
        {
            var rings = new List<int[]>();
            var open = new List<List<long>>();
            foreach (List<long> w in ways)
            {
                if (w[0] == w[w.Count - 1])
                    AddRing(rings, w);
                else
                    open.Add(w);
            }
            while (open.Count > 0)
            {
                var ring = new List<long>(open[open.Count - 1]);
                open.RemoveAt(open.Count - 1);
                bool progress = true;
                while (ring[0] != ring[ring.Count - 1] && progress)
                {
                    progress = false;
                    long end = ring[ring.Count - 1];
                    for (int i = 0; i < open.Count; i++)
                    {
                        List<long> s = open[i];
                        if (s[0] == end)
                        {
                            for (int k = 1; k < s.Count; k++)
                                ring.Add(s[k]);
                        }
                        else if (s[s.Count - 1] == end)
                        {
                            for (int k = s.Count - 2; k >= 0; k--)
                                ring.Add(s[k]);
                        }
                        else
                        {
                            continue;
                        }
                        open.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
                if (ring[0] == ring[ring.Count - 1])
                    AddRing(rings, ring);
            }
            return rings;
        }

        static void AddRing(List<int[]> rings, List<long> closed)
        {
            var flat = new List<int>(closed.Count * 2);
            for (int i = 0; i < closed.Count; i++)
            {
                flat.Add((int)(closed[i] >> 32));
                flat.Add((int)closed[i]);
            }
            int[] ring = CloseRing(flat);
            if (ring != null)
                rings.Add(ring);
        }

        /// <summary>A closed way's points without the closing repeat and consecutive duplicates;
        /// null unless it is a closed ring of at least three distinct points.</summary>
        static int[] CloseRing(List<int> g)
        {
            if (g == null || g.Count < 8 || g[0] != g[g.Count - 2] || g[1] != g[g.Count - 1])
                return null;
            var r = new List<int>(g.Count);
            for (int i = 0; i < g.Count - 2; i += 2)
            {
                int n = r.Count;
                if (n >= 2 && r[n - 2] == g[i] && r[n - 1] == g[i + 1])
                    continue;
                r.Add(g[i]);
                r.Add(g[i + 1]);
            }
            while (r.Count >= 4 && r[r.Count - 2] == r[0] && r[r.Count - 1] == r[1])
                r.RemoveRange(r.Count - 2, 2);
            return r.Count >= 6 ? r.ToArray() : null;
        }

        static void Centre(int[] ring, out double lat, out double lon)
        {
            int minLat = int.MaxValue, maxLat = int.MinValue, minLon = int.MaxValue, maxLon = int.MinValue;
            for (int i = 0; i < ring.Length; i += 2)
            {
                minLat = Math.Min(minLat, ring[i]);
                maxLat = Math.Max(maxLat, ring[i]);
                minLon = Math.Min(minLon, ring[i + 1]);
                maxLon = Math.Max(maxLon, ring[i + 1]);
            }
            lat = ((long)minLat + maxLat) * 0.5e-7;
            lon = ((long)minLon + maxLon) * 0.5e-7;
        }

        static bool InThisTile(OsmTile tile, OsmBuilding b)
        {
            Centre(b.Rings[0], out double lat, out double lon);
            return (int)Math.Floor(TileX(lon)) == tile.X && (int)Math.Floor(TileY(lat)) == tile.Y;
        }

        static bool HasPartInside(int[] ring, List<(double lat, double lon)> centres)
        {
            double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
            for (int i = 0; i < ring.Length; i += 2)
            {
                minLat = Math.Min(minLat, ring[i] * 1e-7);
                maxLat = Math.Max(maxLat, ring[i] * 1e-7);
                minLon = Math.Min(minLon, ring[i + 1] * 1e-7);
                maxLon = Math.Max(maxLon, ring[i + 1] * 1e-7);
            }
            foreach (var c in centres)
                if (c.lat >= minLat && c.lat <= maxLat && c.lon >= minLon && c.lon <= maxLon && PointInRing(ring, c.lat, c.lon))
                    return true;
            return false;
        }

        public static bool PointInRing(int[] ring, double lat, double lon)
        {
            bool inside = false;
            int n = ring.Length / 2;
            for (int i = 0, k = n - 1; i < n; k = i++)
            {
                double yi = ring[i * 2] * 1e-7, xi = ring[i * 2 + 1] * 1e-7;
                double yk = ring[k * 2] * 1e-7, xk = ring[k * 2 + 1] * 1e-7;
                if ((yi > lat) != (yk > lat) && lon < (xk - xi) * (lat - yi) / (yk - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        static void ReadTags(OsmBuilding b, Element e)
        {
            b.Height = ParseLength(e.Tag("height"));
            b.MinHeight = ParseLength(e.Tag("min_height"));
            b.Levels = ParseNumber(e.Tag("building:levels"), float.NaN);
            b.MinLevel = ParseNumber(e.Tag("building:min_level"), float.NaN);
            b.RoofLevels = ParseNumber(e.Tag("roof:levels"), float.NaN);
            b.WallColor = ParseColour(e.Tag("building:colour") ?? e.Tag("colour"));
            b.RoofColor = ParseColour(e.Tag("roof:colour"));
            b.RoofShape = e.Tag("roof:shape") ?? e.Tag("building:roof:shape");
            b.RoofHeight = ParseLength(e.Tag("roof:height"));
            b.RoofDirection = ParseDirection(e.Tag("roof:direction"));
            b.RoofOrientation = Orientation(e.Tag("roof:orientation"));
            b.WallMaterial = e.Tag("building:material") ?? e.Tag("building:facade:material");
            b.RoofMaterial = e.Tag("roof:material");
        }

        public static byte Orientation(string s) => s == "along" ? (byte)1 : s == "across" ? (byte)2 : (byte)0;

        static readonly string[] Compass = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

        /// <summary>A direction in degrees from north: "135", "SE"; NaN if neither.</summary>
        public static float ParseDirection(string s)
        {
            if (string.IsNullOrEmpty(s))
                return float.NaN;
            s = s.Trim().ToUpperInvariant();
            int i = Array.IndexOf(Compass, s);
            if (i >= 0)
                return i * 22.5f;
            float v = ParseNumber(s, float.NaN);
            return float.IsNaN(v) ? v : ((v % 360f) + 360f) % 360f;
        }

        // ---------------------------------------------------------------- tag values

        /// <summary>Leading number of a tag value ("3", "2.5", "4;5"); fallback if none.</summary>
        public static float ParseNumber(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            int end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.' || s[end] == ',' || (end == 0 && s[end] == '-')))
                end++;
            string num = s.Substring(0, end).Replace(',', '.');
            return float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        /// <summary>A length in metres: "12", "12.5 m", "40'", "40 ft", "40'6\"".</summary>
        public static float ParseLength(string s)
        {
            if (string.IsNullOrEmpty(s))
                return float.NaN;
            s = s.Trim();
            float v = ParseNumber(s, float.NaN);
            if (float.IsNaN(v))
                return v;
            int feet = s.IndexOf('\'');
            if (feet >= 0)
            {
                float inches = ParseNumber(s.Substring(feet + 1).Trim(), 0f);
                return (v + inches / 12f) * 0.3048f;
            }
            if (s.EndsWith("ft", StringComparison.OrdinalIgnoreCase) || s.EndsWith("feet", StringComparison.OrdinalIgnoreCase))
                return v * 0.3048f;
            return v;
        }

        static readonly Dictionary<string, uint> Colours = new Dictionary<string, uint>
        {
            ["white"] = 0xFFFFFF, ["black"] = 0x303030, ["gray"] = 0x808080, ["grey"] = 0x808080,
            ["lightgray"] = 0xD3D3D3, ["lightgrey"] = 0xD3D3D3, ["darkgray"] = 0x707070, ["darkgrey"] = 0x707070,
            ["silver"] = 0xC0C0C0, ["red"] = 0xB04030, ["darkred"] = 0x8B2020, ["maroon"] = 0x802020,
            ["brown"] = 0x8B5A3C, ["beige"] = 0xE8DFC8, ["tan"] = 0xD2B48C, ["wheat"] = 0xF5DEB3,
            ["ivory"] = 0xFFFFF0, ["cream"] = 0xFFF8DC, ["yellow"] = 0xE8D080, ["lightyellow"] = 0xFFF5C0,
            ["khaki"] = 0xF0E68C, ["orange"] = 0xE0904A, ["pink"] = 0xF0C0C0, ["salmon"] = 0xE89080,
            ["blue"] = 0x5070A0, ["lightblue"] = 0xADC8E0, ["navy"] = 0x303C70, ["green"] = 0x608050,
            ["darkgreen"] = 0x305030, ["lightgreen"] = 0xB0D0A0, ["olive"] = 0x808050, ["teal"] = 0x408080,
            ["sandybrown"] = 0xF4A460, ["peru"] = 0xCD853F, ["sienna"] = 0xA0522D, ["chocolate"] = 0xD2691E,
            ["burlywood"] = 0xDEB887, ["gold"] = 0xE0C060, ["lavender"] = 0xE6E6FA, ["purple"] = 0x806080,
            ["terracotta"] = 0xC0634A, ["brick"] = 0xA85A45, ["sand"] = 0xD8C8A0, ["concrete"] = 0xB8B8B0,
        };

        /// <summary>"#rrggbb", "#rgb" or a common colour name, as 0xRRGGBBFF; 0 if unknown.</summary>
        public static uint ParseColour(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            s = s.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
            uint rgb;
            if (s.StartsWith("#"))
            {
                string hex = s.Substring(1);
                if (hex.Length == 3)
                    hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
                if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                    return 0;
            }
            else if (!Colours.TryGetValue(s, out rgb))
            {
                return 0;
            }
            return (rgb << 8) | 0xFF;
        }

        static byte[] Gunzip(byte[] data)
        {
            using (var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var output = new MemoryStream(data.Length * 6))
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        // ---------------------------------------------------------------- cache file

        /// <summary>RoadList kind of an OSM railway=* (preserved lines are ordinary railways
        /// to look at); service tracks (yard, siding, spur) of any kind are "rail_siding".</summary>
        public static string RailwayKind(string railway, string service)
        {
            string kind = railway == "preserved" ? "rail" : railway;
            if (!IsRailway(kind))
                return null;
            return service != null && kind != "tram" && kind != "subway" ? "rail_siding" : kind;
        }

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".part" + System.Threading.Thread.CurrentThread.ManagedThreadId;
            using (var w = new BinaryWriter(new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(X);
                w.Write(Y);
                w.Write(FetchedUtcTicks);
                w.Write((byte)Source);
                w.Write(Buildings.Count);
                foreach (OsmBuilding b in Buildings)
                {
                    w.Write(b.Id);
                    w.Write(b.Part);
                    w.Write(b.Type ?? "");
                    w.Write(b.Height);
                    w.Write(b.MinHeight);
                    w.Write(b.Levels);
                    w.Write(b.MinLevel);
                    w.Write(b.RoofLevels);
                    w.Write(b.WallColor);
                    w.Write(b.RoofColor);
                    w.Write(b.Rings.Count);
                    foreach (int[] ring in b.Rings)
                        WriteInts(w, ring);
                    w.Write(b.RoofShape ?? "");
                    w.Write(b.RoofHeight);
                    w.Write(b.RoofDirection);
                    w.Write(b.RoofOrientation);
                    w.Write(b.WallMaterial ?? "");
                    w.Write(b.RoofMaterial ?? "");
                    WriteInts(w, b.Whole ?? NoInts);
                    w.Write(b.Cut);
                }
                w.Write(RoadList.Count);
                foreach (OsmRoad r in RoadList)
                {
                    w.Write(r.Id);
                    w.Write(r.Kind ?? "");
                    w.Write(r.Lit);
                    w.Write(r.Lanes);
                    WriteInts(w, r.Points);
                }
                WriteInts(w, Lamps.ToArray());
                w.Write(Aprons.Count);
                foreach (int[] ring in Aprons)
                    WriteInts(w, ring);
                w.Write(HasRail);
                w.Write(Bridges.Count);
                foreach (int[] line in Bridges)
                    WriteInts(w, line);
                w.Write(HasBridges);
                w.Write(HasOverture);
                w.Write(HasAreas);
                w.Write(Areas.Count);
                foreach (OsmArea a in Areas)
                {
                    w.Write((byte)a.Kind);
                    w.Write(a.Rings.Count);
                    foreach (int[] ring in a.Rings)
                        WriteInts(w, ring);
                }
                w.Write(AerowayLines.Count);
                foreach (OsmRoad r in AerowayLines)
                {
                    w.Write(r.Kind ?? "");
                    WriteInts(w, r.Points);
                }
            }
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Reads a cache file; null if it is from another format version.</summary>
        public static OsmTile Read(string path)
        {
            CacheReader r = CacheReader.Open(path);
            try
            {
                if (r.Int32() != Magic)
                    return null;
                int version = r.Int32();
                if (version < 1 || version > FormatVersion)
                    return null;
                var t = new OsmTile { X = r.Int32(), Y = r.Int32(), FetchedUtcTicks = r.Int64() };
                t.Source = version >= 2 ? (OsmSource)r.Byte() : OsmSource.Overpass;
                int buildings = r.Count(16);
                t.Buildings.Capacity = buildings;
                for (int i = 0; i < buildings; i++)
                {
                    OsmBuilding b = OsmBuilding.Rent();
                    b.Id = r.Int64();
                    b.Part = r.Bool();
                    b.Type = r.String();
                    b.Height = r.Single();
                    b.MinHeight = r.Single();
                    b.Levels = r.Single();
                    b.MinLevel = r.Single();
                    b.RoofLevels = r.Single();
                    b.WallColor = r.UInt32();
                    b.RoofColor = r.UInt32();
                    int rings = r.Count(4);
                    if (b.Rings.Capacity < rings)
                        b.Rings.Capacity = rings;
                    for (int k = 0; k < rings; k++)
                        b.Rings.Add(r.Ints(true));
                    if (version >= 6)
                    {
                        b.RoofShape = NullIfEmpty(r.String());
                        b.RoofHeight = r.Single();
                        b.RoofDirection = r.Single();
                        b.RoofOrientation = r.Byte();
                        b.WallMaterial = NullIfEmpty(r.String());
                        b.RoofMaterial = NullIfEmpty(r.String());
                        int[] whole = r.Ints(true);
                        b.Whole = whole.Length > 0 ? whole : null;
                        b.Cut = r.Bool();
                    }
                    t.Buildings.Add(b);
                }
                int roads = r.Count(14);
                t.RoadList.Capacity = roads;
                for (int i = 0; i < roads; i++)
                    t.RoadList.Add(new OsmRoad { Id = r.Int64(), Kind = r.String(), Lit = r.Byte(), Lanes = r.Byte(), Points = r.Ints() });
                r.AddInts(t.Lamps);
                if (version >= 3)
                {
                    int aprons = r.Count(4);
                    for (int i = 0; i < aprons; i++)
                        t.Aprons.Add(r.Ints());
                    t.HasAeroway = true;
                }
                if (version >= 4)
                    t.HasRail = r.Bool();
                if (version >= 5)
                {
                    int bridges = r.Count(4);
                    for (int i = 0; i < bridges; i++)
                        t.Bridges.Add(r.Ints());
                    t.HasBridges = r.Bool();
                }
                if (version >= 6)
                    t.HasOverture = r.Bool();
                if (version >= 7)
                {
                    t.HasAreas = r.Bool();
                    int areas = r.Count(5);
                    t.Areas.Capacity = areas;
                    for (int i = 0; i < areas; i++)
                    {
                        var a = new OsmArea { Kind = (AreaKind)r.Byte() };
                        int rings = r.Count(4);
                        a.Rings.Capacity = rings;
                        for (int k = 0; k < rings; k++)
                            a.Rings.Add(r.Ints());
                        t.Areas.Add(a);
                    }
                    int lines = r.Count(5);
                    for (int i = 0; i < lines; i++)
                        t.AerowayLines.Add(new OsmRoad { Kind = r.String(), Points = r.Ints() });
                }
                return t;
            }
            finally
            {
                r.Close();
            }
        }

        static string NullIfEmpty(string s) => s.Length == 0 ? null : s;

        static readonly int[] NoInts = new int[0];

        static void WriteInts(BinaryWriter w, int[] values)
        {
            w.Write(values.Length);
            foreach (int v in values)
                w.Write(v);
        }

    }

    /// <summary>
    /// Reads an OSM cache file whole into a kept buffer and decodes it from there, in the
    /// layout BinaryWriter wrote (little-endian, strings UTF-8 after a 7-bit length). The tags'
    /// short strings (building types, road kinds, roof shapes, materials) come from a table kept
    /// per thread instead of anew for every building. Until 2026-10-02 every read made a 64 KB
    /// stream buffer and a string per tag: ~0.1 MB of garbage for a country tile, 8 MB for a
    /// city tile, and tiles are read again whenever their detail changes.
    /// </summary>
    sealed class CacheReader
    {
        const int MaxKept = 16;            // buffers kept for the next file
        const int KeepBytes = 4 << 20;     // ... unless bigger than this
        const int MaxInterned = 4096;      // strings per thread's table

        static readonly Stack<CacheReader> free = new Stack<CacheReader>();
        [ThreadStatic] static Dictionary<ulong, string> interned;

        byte[] buffer = new byte[1 << 16];
        int pos, length;
        Dictionary<ulong, string> strings;

        public static CacheReader Open(string path)
        {
            CacheReader r = null;
            lock (free)
            {
                if (free.Count > 0)
                    r = free.Pop();
            }
            r ??= new CacheReader();
            try
            {
                // No buffer of FileStream's own: the file goes straight into ours.
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
                {
                    long size = f.Length;
                    if (size > int.MaxValue / 2)
                        throw new InvalidDataException("OSM cache file too big");
                    if (r.buffer.Length < size)
                        r.buffer = new byte[Math.Max((int)size, Math.Min(r.buffer.Length * 2, int.MaxValue / 2))];
                    int n = 0;
                    while (n < size)
                    {
                        int got = f.Read(r.buffer, n, (int)size - n);
                        if (got <= 0)
                            break;
                        n += got;
                    }
                    r.length = n;
                    r.pos = 0;
                }
            }
            catch
            {
                r.Close();
                throw;
            }
            r.strings = interned ??= new Dictionary<ulong, string>();
            return r;
        }

        /// <summary>Done: the buffer is kept for the next file.</summary>
        public void Close()
        {
            strings = null;
            if (buffer.Length > KeepBytes)
                buffer = new byte[1 << 16];
            lock (free)
            {
                if (free.Count < MaxKept)
                    free.Push(this);
            }
        }

        void Need(int n)
        {
            if (n < 0 || pos + n > length)
                throw new EndOfStreamException("OSM cache file cut short");
        }

        public byte Byte()
        {
            Need(1);
            return buffer[pos++];
        }

        public bool Bool() => Byte() != 0;

        public int Int32()
        {
            Need(4);
            int v = BitConverter.ToInt32(buffer, pos);
            pos += 4;
            return v;
        }

        public uint UInt32() => unchecked((uint)Int32());

        public long Int64()
        {
            Need(8);
            long v = BitConverter.ToInt64(buffer, pos);
            pos += 8;
            return v;
        }

        public float Single()
        {
            Need(4);
            float v = BitConverter.ToSingle(buffer, pos);
            pos += 4;
            return v;
        }

        /// <summary>A count of things of at least `minBytes` each: no more than the rest of the file holds.</summary>
        public int Count(int minBytes)
        {
            int n = Int32();
            if (n < 0 || (long)n * minBytes > length - pos)
                throw new InvalidDataException("corrupt OSM cache file");
            return n;
        }

        /// <summary>An int array; pooled: from IntPool (buildings' outlines, which go back to it).</summary>
        public int[] Ints(bool pooled = false)
        {
            int n = Count(4);
            if (n == 0)
                return NoInts;
            int[] values = pooled ? IntPool.Rent(n) : new int[n];
            System.Buffer.BlockCopy(buffer, pos, values, 0, n * 4);
            pos += n * 4;
            return values;
        }

        /// <summary>An int array, added to a list instead of made into an array.</summary>
        public void AddInts(List<int> into)
        {
            int n = Count(4);
            if (into.Capacity < into.Count + n)
                into.Capacity = into.Count + n;
            for (int i = 0; i < n; i++)
            {
                into.Add(BitConverter.ToInt32(buffer, pos));
                pos += 4;
            }
        }

        static readonly int[] NoInts = new int[0];

        public string String()
        {
            int len = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (shift > 28)
                    throw new InvalidDataException("corrupt OSM cache file");
                byte b = Byte();
                len |= (b & 0x7F) << shift;
                if (b < 0x80)
                    break;
            }
            Need(len);
            if (len == 0)
                return "";
            string s = len <= 48 ? Interned(len) : null;
            if (s == null)
                s = Encoding.UTF8.GetString(buffer, pos, len);
            pos += len;
            return s;
        }

        /// <summary>The ASCII string at pos from this thread's table (added if new); null for
        /// other text, which is decoded as it comes.</summary>
        string Interned(int len)
        {
            ulong h = 14695981039346656037UL ^ (ulong)len;
            for (int i = 0; i < len; i++)
            {
                byte c = buffer[pos + i];
                if (c >= 0x80)
                    return null;
                h = (h ^ c) * 1099511628211UL;
            }
            if (strings.TryGetValue(h, out string s) && Same(s, len))
                return s;
            s = Encoding.ASCII.GetString(buffer, pos, len);
            if (strings.Count >= MaxInterned)
                strings.Clear();
            strings[h] = s;
            return s;
        }

        bool Same(string s, int len)
        {
            if (s.Length != len)
                return false;
            for (int i = 0; i < len; i++)
                if (s[i] != buffer[pos + i])
                    return false;
            return true;
        }
    }

    /// <summary>Minimal pull parser over UTF-8 JSON bytes, enough for Overpass replies.</summary>
    sealed class Json
    {
        readonly byte[] s;
        int p;

        public Json(byte[] data)
        {
            s = data;
            if (s.Length >= 3 && s[0] == 0xEF && s[1] == 0xBB && s[2] == 0xBF)
                p = 3;
        }

        InvalidDataException Error(string what) => new InvalidDataException($"JSON: {what} at byte {p}");

        byte Peek()
        {
            while (p < s.Length)
            {
                byte c = s[p];
                if (c != ' ' && c != '\n' && c != '\r' && c != '\t')
                    return c;
                p++;
            }
            throw Error("unexpected end");
        }

        void Expect(char c)
        {
            if (Peek() != c)
                throw Error($"expected '{c}'");
            p++;
        }

        bool Maybe(char c)
        {
            if (Peek() != c)
                return false;
            p++;
            return true;
        }

        /// <summary>Consumes '{'; false if the object is empty (then '}' is consumed too).</summary>
        public bool BeginObject()
        {
            Expect('{');
            return !Maybe('}');
        }

        public bool BeginArray()
        {
            Expect('[');
            return !Maybe(']');
        }

        /// <summary>After a member or item: true if another follows, false at the closing bracket.</summary>
        public bool Next(char close)
        {
            if (Maybe(','))
                return true;
            Expect(close);
            return false;
        }

        public string Key()
        {
            string k = String();
            Expect(':');
            return k;
        }

        public bool TryNull()
        {
            if (Peek() != 'n')
                return false;
            p += 4;
            return true;
        }

        public string String()
        {
            Expect('"');
            int start = p;
            bool escaped = false;
            while (true)
            {
                if (p >= s.Length)
                    throw Error("unterminated string");
                byte c = s[p];
                if (c == '"')
                    break;
                if (c == '\\')
                {
                    escaped = true;
                    p += 2;
                    continue;
                }
                p++;
            }
            string result = escaped ? Unescape(start, p) : Encoding.UTF8.GetString(s, start, p - start);
            p++;
            return result;
        }

        string Unescape(int start, int end)
        {
            var sb = new StringBuilder(end - start);
            int run = start;
            for (int i = start; i < end; i++)
            {
                if (s[i] != '\\')
                    continue;
                sb.Append(Encoding.UTF8.GetString(s, run, i - run));
                char e = (char)s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(Encoding.ASCII.GetString(s, i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
                i++;
                run = i + 1;
            }
            sb.Append(Encoding.UTF8.GetString(s, run, end - run));
            return sb.ToString();
        }

        public long Long() => Fixed(0);

        /// <summary>A decimal number times 10^digits, rounded, without going through floating
        /// point (so coordinates come out exactly as OSM stores them).</summary>
        public long Fixed(int digits)
        {
            Peek();
            bool negative = false;
            if (s[p] == '-')
            {
                negative = true;
                p++;
            }
            long mantissa = 0;
            int decimals = -1;
            int exponent = 0;
            bool roundUp = false;
            for (; p < s.Length; p++)
            {
                byte c = s[p];
                if (c >= '0' && c <= '9')
                {
                    if (decimals >= 0 && decimals >= digits)
                    {
                        if (decimals == digits)
                            roundUp = c >= '5';
                        decimals++;
                        continue;
                    }
                    mantissa = mantissa * 10 + (c - '0');
                    if (decimals >= 0)
                        decimals++;
                }
                else if (c == '.')
                {
                    decimals = 0;
                }
                else if (c == 'e' || c == 'E')
                {
                    p++;
                    bool negExp = false;
                    if (s[p] == '-' || s[p] == '+')
                        negExp = s[p++] == '-';
                    for (; p < s.Length && s[p] >= '0' && s[p] <= '9'; p++)
                        exponent = exponent * 10 + (s[p] - '0');
                    if (negExp)
                        exponent = -exponent;
                    break;
                }
                else
                {
                    break;
                }
            }
            int kept = Math.Max(decimals, 0) > digits ? digits : Math.Max(decimals, 0);
            int scale = digits - kept + exponent;
            if (roundUp)
                mantissa++;
            for (; scale > 0; scale--)
                mantissa *= 10;
            for (; scale < 0; scale++)
                mantissa /= 10;
            return negative ? -mantissa : mantissa;
        }

        public void Skip()
        {
            byte c = Peek();
            if (c == '{')
            {
                if (BeginObject())
                    do
                    {
                        Key();
                        Skip();
                    } while (Next('}'));
            }
            else if (c == '[')
            {
                if (BeginArray())
                    do Skip(); while (Next(']'));
            }
            else if (c == '"')
            {
                String();
            }
            else
            {
                while (p < s.Length)
                {
                    c = s[p];
                    if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\n' || c == '\r' || c == '\t')
                        break;
                    p++;
                }
            }
        }
    }
}
