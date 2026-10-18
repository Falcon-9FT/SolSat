// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SolSatellite
{
    /// <summary>
    /// Reads z14 vector tiles in the OpenMapTiles schema (as served by OpenFreeMap) into an
    /// OsmTile: buildings from the "building" layer (render_height, render_min_height, colour;
    /// outlines marked hide_3d are left out, their building:part pieces are there instead),
    /// roads from "transportation", and for the scatter mask the areas of "landcover",
    /// "landuse", "water" and "aeroway" (with runway and taxiway lines). Polygons are clipped
    /// to the tile square, so a building cut by a tile border becomes two pieces that meet exactly.
    /// </summary>
    static class Mvt
    {
        static readonly HashSet<string> RoadClasses = new HashSet<string>
        {
            "motorway", "trunk", "primary", "secondary", "tertiary", "minor", "service", "busway",
        };

        public static OsmTile Parse(int x, int y, byte[] data)
        {
            int length = data.Length;
            if (length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                data = Gunzip(data, out length);
            var tile = new OsmTile
            {
                X = x, Y = y, Source = OsmSource.OpenFreeMap, FetchedUtcTicks = DateTime.UtcNow.Ticks, HasAeroway = true, HasRail = true, HasBridges = true,
                HasAreas = true,
            };
            Scratch s = Scratch.Get();
            var pb = new Pbf(data, 0, length);
            while (pb.Next())
            {
                if (pb.Field != 3 || pb.Wire != 2)
                {
                    pb.Skip();
                    continue;
                }
                Pbf layer = pb.Message();
                // House numbers, labels, POIs...: not even decoded (in a city, most of the tile).
                switch (LayerName(layer))
                {
                    case "building": case "transportation": case "landcover": case "landuse": case "water": case "aeroway":
                        ReadLayer(layer, tile, s);
                        break;
                }
            }
            return tile;
        }

        /// <summary>
        /// What decoding a tile needs, kept per thread from tile to tile: a city's vector tile
        /// made ~18 MB of garbage decoding (a feature object, a tag dictionary and point lists
        /// for each of tens of thousands of features, most of them in layers nothing reads),
        /// for 0.5-0.75 MB of tile (2026-10-02).
        /// </summary>
        sealed class Scratch
        {
            public readonly Layer Layer = new Layer();
            public readonly Feature Feature = new Feature();
            /// <summary>Rings of the feature being read (Rings): lists kept for the next feature.</summary>
            public readonly List<List<double>> Rings = new List<List<double>>();
            readonly List<List<double>> ringPool = new List<List<double>>();
            int ringsUsed;
            public readonly List<double> ClipA = new List<double>(), ClipB = new List<double>();
            public readonly List<int> LatLon = new List<int>();
            public readonly List<OsmBuilding> Footprints = new List<OsmBuilding>();
            // ParseOverture's: outlines, parts, outlines by id, outlines that gave way to parts.
            public readonly List<(OsmBuilding b, ulong id)> Outlines = new List<(OsmBuilding b, ulong id)>();
            public readonly List<(OsmBuilding b, ulong parent)> Parts = new List<(OsmBuilding b, ulong parent)>();
            public readonly Dictionary<ulong, OsmBuilding> Parents = new Dictionary<ulong, OsmBuilding>();
            public readonly HashSet<ulong> Replaced = new HashSet<ulong>();

            public void ClearOverture()
            {
                Outlines.Clear();
                Parts.Clear();
                Parents.Clear();
                Replaced.Clear();
            }

            [ThreadStatic] static Scratch current;

            public static Scratch Get() => current ??= new Scratch();

            public void ClearRings()
            {
                Rings.Clear();
                ringsUsed = 0;
            }

            public List<double> NewRing()
            {
                if (ringsUsed == ringPool.Count)
                    ringPool.Add(new List<double>());
                List<double> r = ringPool[ringsUsed++];
                r.Clear();
                Rings.Add(r);
                return r;
            }
        }

        /// <summary>A layer's value: a number, a flag, or a string left in the tile's bytes until
        /// asked for (Layer.String) - Overture's layers hold a UUID for every building, which is
        /// only ever hashed (HashOf). Boxed numbers and every string decoded made ~3 MB of
        /// garbage for a city tile of Overture's (2026-10-03).</summary>
        struct Value
        {
            public byte Kind;          // 0 none, 1 string, 2 number, 3 flag
            public double Number;      // a number; a flag 1 or 0
            public int Start, Length;  // a string's UTF-8 bytes in Layer.Bytes
        }

        sealed class Layer
        {
            public string Name = "";
            public int Extent = 4096;
            public readonly List<string> Keys = new List<string>();
            public readonly List<Value> Values = new List<Value>();
            public readonly List<Pbf> Features = new List<Pbf>();
            public byte[] Bytes;                      // the tile's: where Values' strings are
            string[] strings = new string[0];         // decoded so far, by value index

            /// <summary>The layer of a tile's layer message: keys and values decoded, features found.</summary>
            public Layer Read(Pbf pb)
            {
                Name = "";
                Extent = 4096;
                Keys.Clear();
                Values.Clear();
                Features.Clear();
                Bytes = pb.Bytes;
                while (pb.Next())
                {
                    switch (pb.Field)
                    {
                        case 1: Name = pb.String(); break;
                        case 2: Features.Add(pb.Message()); break; // decoded once keys/values are known
                        case 3: Keys.Add(pb.String()); break;
                        case 4: Values.Add(ReadValue(pb.Message())); break;
                        case 5: Extent = (int)pb.Varint(); break;
                        default: pb.Skip(); break;
                    }
                }
                if (strings.Length < Values.Count)
                    strings = new string[Math.Max(Values.Count, strings.Length * 2)];
                else
                    Array.Clear(strings, 0, Values.Count);
                return this;
            }

            /// <summary>Value v as a string (decoded once), null if it isn't one.</summary>
            public string String(int v)
            {
                Value value = Values[v];
                if (value.Kind != 1)
                    return null;
                return strings[v] ??= Encoding.UTF8.GetString(Bytes, value.Start, value.Length);
            }
        }

        /// <summary>A layer message's name, the rest left undecoded.</summary>
        static string LayerName(Pbf pb)
        {
            while (pb.Next())
            {
                if (pb.Field == 1 && pb.Wire == 2)
                    return pb.String();
                pb.Skip();
            }
            return "";
        }

        static void ReadLayer(Pbf pb, OsmTile tile, Scratch s)
        {
            Layer layer = s.Layer.Read(pb);
            if (layer.Name == "building")
                ReadBuildings(layer, tile, s);
            else if (layer.Name == "transportation")
                foreach (Pbf f in layer.Features)
                    ReadRoad(f, layer, tile, s);
            else if (layer.Name == "landcover" || layer.Name == "landuse" || layer.Name == "water" || layer.Name == "aeroway")
                ReadAreas(layer, tile, s);
        }

        /// <summary>What an area is for the scatter mask; null: nothing it cares about.</summary>
        static AreaKind? AreaKindOf(string layer, string cls, string sub)
        {
            switch (layer)
            {
                case "landcover":
                    if (cls == "wood")
                        return AreaKind.Forest;
                    if (cls == "grass" && (sub == "park" || sub == "garden" || sub == "village_green" || sub == "recreation_ground"))
                        return AreaKind.Park;
                    return null;
                case "landuse":
                    switch (cls)
                    {
                        case "residential": case "commercial": case "industrial": case "retail": case "railway":
                        case "military": case "quarry": case "garages": case "school": case "university": case "college":
                        case "hospital": case "kindergarten": case "stadium": case "bus_station":
                            return AreaKind.BuiltUp;
                        case "cemetery": case "zoo": case "theme_park":
                            return AreaKind.Park;
                    }
                    return null;
                case "water":
                    return AreaKind.Water;
                case "aeroway":
                    switch (cls)
                    {
                        case "aerodrome": case "heliport":
                            return AreaKind.Airfield;
                        case "runway": case "taxiway": case "apron": case "helipad": case "stopway":
                            return AreaKind.Paved;
                    }
                    return null;
            }
            return null;
        }

        /// <summary>Areas of a layer (outer rings with their holes, cut to the tile), and from
        /// "aeroway" the runway and taxiway centre lines.</summary>
        static void ReadAreas(Layer layer, OsmTile tile, Scratch s)
        {
            foreach (Pbf pb in layer.Features)
            {
                Feature f = ReadFeature(pb, layer, s.Feature);
                string cls = Text(f, "class"), sub = Text(f, "subclass");
                if (f.Type == 2 && layer.Name == "aeroway" && (cls == "runway" || cls == "taxiway"))
                {
                    foreach (List<double> line in Rings(f.Geometry, false, s))
                        if (line.Count >= 4)
                            tile.AerowayLines.Add(new OsmRoad { Id = (long)f.Id, Kind = cls, Points = ToLatLon(line, tile, layer.Extent, s) });
                    continue;
                }
                if (f.Type != 3)
                    continue;
                AreaKind? kind = AreaKindOf(layer.Name, cls, sub);
                if (kind == null)
                    continue;
                OsmArea current = null;
                foreach (List<double> ring in Rings(f.Geometry, true, s))
                {
                    double area = SignedArea(ring);
                    if (area == 0.0)
                        continue;
                    List<double> clipped = Clip(ring, layer.Extent, s);
                    if (clipped.Count < 6)
                    {
                        if (area > 0)
                            current = null; // an outer ring outside the tile: its holes too
                        continue;
                    }
                    if (area > 0)
                    {
                        current = new OsmArea { Kind = kind.Value };
                        current.Rings.Add(ToLatLon(clipped, tile, layer.Extent, s));
                        tile.Areas.Add(current);
                    }
                    else if (current != null)
                    {
                        current.Rings.Add(ToLatLon(clipped, tile, layer.Extent, s));
                    }
                }
            }
        }

        static Value ReadValue(Pbf pb)
        {
            var v = new Value();
            while (pb.Next())
            {
                switch (pb.Field)
                {
                    case 1: v.Kind = 1; pb.StringAt(out v.Start, out v.Length); break;
                    case 2: v.Kind = 2; v.Number = pb.Float(); break;
                    case 3: v.Kind = 2; v.Number = pb.Double(); break;
                    case 4: v.Kind = 2; v.Number = (long)pb.Varint(); break;
                    case 5: v.Kind = 2; v.Number = pb.Varint(); break;
                    case 6: v.Kind = 2; v.Number = Pbf.ZigZag(pb.Varint()); break;
                    case 7: v.Kind = 3; v.Number = pb.Varint() != 0 ? 1.0 : 0.0; break;
                    default: pb.Skip(); break;
                }
            }
            return v;
        }

        sealed class Feature
        {
            public ulong Id;
            public int Type;
            public Layer Layer;
            public readonly Dictionary<string, int> Tags = new Dictionary<string, int>(); // key -> index in Layer.Values
            public readonly List<uint> Geometry = new List<uint>();
        }

        /// <summary>A feature, read into `f` (whatever it held before is gone).</summary>
        static Feature ReadFeature(Pbf pb, Layer layer, Feature f)
        {
            f.Id = 0UL;
            f.Type = 0;
            f.Layer = layer;
            f.Tags.Clear();
            f.Geometry.Clear();
            while (pb.Next())
            {
                switch (pb.Field)
                {
                    case 1:
                        f.Id = pb.Varint();
                        break;
                    case 2:
                        Pbf tags = pb.Packed();
                        while (!tags.End)
                        {
                            int k = (int)tags.Varint(), v = (int)tags.Varint();
                            if (k < layer.Keys.Count && v < layer.Values.Count)
                                f.Tags[layer.Keys[k]] = v;
                        }
                        break;
                    case 3:
                        f.Type = (int)pb.Varint();
                        break;
                    case 4:
                        Pbf geometry = pb.Packed();
                        while (!geometry.End)
                            f.Geometry.Add((uint)geometry.Varint());
                        break;
                    default:
                        pb.Skip();
                        break;
                }
            }
            return f;
        }

        static bool Get(Feature f, string key, out Value v)
        {
            if (f.Tags.TryGetValue(key, out int i))
            {
                v = f.Layer.Values[i];
                return true;
            }
            v = default;
            return false;
        }

        static double Number(Feature f, string key, double fallback) => Get(f, key, out Value v) && v.Kind == 2 ? v.Number : fallback;

        static string Text(Feature f, string key) => f.Tags.TryGetValue(key, out int i) ? f.Layer.String(i) : null;

        static float Single(Feature f, string key) => Get(f, key, out Value v) && v.Kind == 2 ? (float)v.Number : float.NaN;

        static bool Flag(Feature f, string key) => Get(f, key, out Value v) && v.Kind == 3 && v.Number != 0.0;

        /// <summary>A number above 0 or a true flag (ramp=1 or ramp=true).</summary>
        static bool Truthy(Feature f, string key) =>
            Get(f, key, out Value v) && (v.Kind == 2 && v.Number > 0.0 || v.Kind == 3 && v.Number != 0.0);

        /// <summary>Hash of a string value, from its bytes without decoding it: for ASCII (UUIDs)
        /// the same as Hash of the string; anything else is decoded. 0 if it isn't a string.</summary>
        static ulong HashOf(Feature f, string key)
        {
            if (!f.Tags.TryGetValue(key, out int i))
                return 0UL;
            Value v = f.Layer.Values[i];
            if (v.Kind != 1)
                return 0UL;
            byte[] b = f.Layer.Bytes;
            ulong h = 14695981039346656037UL;
            for (int k = v.Start, end = v.Start + v.Length; k < end; k++)
            {
                byte c = b[k];
                if (c >= 0x80)
                    return Hash(f.Layer.String(i));
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        static void ReadBuildings(Layer layer, OsmTile tile, Scratch s)
        {
            double buffer = Buffer(layer, s);
            foreach (Pbf pb in layer.Features)
            {
                Feature f = ReadFeature(pb, layer, s.Feature);
                if (f.Type != 3)
                    continue;
                List<List<double>> rings = Rings(f.Geometry, true, s);
                if (Flag(f, "hide_3d"))
                    continue;
                double height = Number(f, "render_height", double.NaN);
                double minHeight = Number(f, "render_min_height", 0.0);
                // OpenMapTiles puts 5 m on every building without height or levels: leave those
                // to the mesher's guess from the footprint, as for untagged Overpass buildings.
                float tagged = double.IsNaN(height) || (height <= 5.0 && minHeight <= 0.0) ? float.NaN : (float)height;
                uint colour = Text(f, "colour") is string c ? OsmTile.ParseColour(c) : 0;
                foreach (OsmBuilding b in Footprints(rings, tile, layer.Extent, buffer, f.Id, s))
                {
                    b.Height = tagged;
                    b.MinHeight = minHeight > 0.0 ? (float)minHeight : float.NaN;
                    b.WallColor = colour;
                    tile.Buildings.Add(b);
                }
            }
        }

        /// <summary>How far outside the tile square a layer's polygons reach: the margin the
        /// tiles were cut with (64 of 4096 units for both OpenFreeMap and Overture, ~1.5%).
        /// A pass of its own over the features, so they needn't all be kept.</summary>
        static double Buffer(Layer layer, Scratch s)
        {
            double over = 0.0;
            foreach (Pbf pb in layer.Features)
            {
                Feature f = ReadFeature(pb, layer, s.Feature);
                if (f.Type != 3)
                    continue;
                foreach (List<double> ring in Rings(f.Geometry, true, s))
                    for (int i = 0; i < ring.Count; i++)
                        over = Math.Max(over, Math.Max(-ring[i], ring[i] - layer.Extent));
            }
            return Math.Max(over, 64.0 * layer.Extent / 4096.0);
        }

        /// <summary>
        /// Buildings of a polygon feature: one per outer ring, its holes attached, clipped to the
        /// tile square (a building across the border becomes two pieces that meet exactly). A
        /// clipped one also keeps its whole outer ring, unless that reaches the tile's margin
        /// (it is bigger than the tile shows: Cut), and so does one with a courtyard.
        /// </summary>
        static List<OsmBuilding> Footprints(List<List<double>> rings, OsmTile tile, int extent, double buffer, ulong id, Scratch s)
        {
            List<OsmBuilding> result = s.Footprints;
            result.Clear();
            OsmBuilding current = null;
            bool clippedCurrent = false;
            int part = 0;
            foreach (List<double> ring in rings)
            {
                double area = SignedArea(ring);
                if (area == 0.0)
                    continue;
                // Exterior rings have positive area in tile coordinates (y down), holes negative.
                List<double> clipped = Clip(ring, extent, s);
                bool wasClipped = !ReferenceEquals(clipped, ring);
                if (area > 0)
                {
                    current = null;
                    if (clipped.Count < 6)
                        continue;
                    current = OsmBuilding.Rent();
                    current.Id = (long)(id * 16 + (ulong)part++);
                    current.Type = "";
                    current.Rings.Add(ToLatLon(clipped, tile, extent, s));
                    clippedCurrent = wasClipped;
                    if (wasClipped)
                    {
                        if (TouchesMargin(ring, extent, buffer))
                            current.Cut = true;
                        else
                            current.Whole = ToLatLon(ring, tile, extent, s);
                    }
                    result.Add(current);
                }
                else if (current != null)
                {
                    if (clippedCurrent)
                        current.Cut = true; // the hole may be in the other piece only
                    if (clipped.Count >= 6)
                        current.Rings.Add(ToLatLon(clipped, tile, extent, s));
                }
            }
            return result;
        }

        static bool TouchesMargin(List<double> ring, int extent, double buffer)
        {
            double low = -buffer + 0.5, high = extent + buffer - 0.5;
            for (int i = 0; i < ring.Count; i++)
                if (ring[i] <= low || ring[i] >= high)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ Overture Maps

        /// <summary>
        /// Buildings of an Overture Maps tile (the "building" and "building_part" layers of its
        /// buildings PMTiles, Overture.cs): OpenStreetMap's with their tags as Overture keeps them
        /// - height, floors, roof shape, height, direction and orientation, facade and roof
        /// material and colour, class - and footprints from Microsoft's and Google's building
        /// detection where OSM has none. An outline whose parts are in the tile gives way to
        /// them, as with OSM's Simple 3D Buildings.
        /// </summary>
        public static OsmTile ParseOverture(int x, int y, byte[] data)
        {
            int length = data.Length;
            if (length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                data = Gunzip(data, out length);
            var tile = new OsmTile { X = x, Y = y, Source = OsmSource.OpenFreeMap, FetchedUtcTicks = DateTime.UtcNow.Ticks, HasOverture = true };
            Scratch s = Scratch.Get();
            s.ClearOverture();
            List<(OsmBuilding b, ulong id)> outlines = s.Outlines;
            List<(OsmBuilding b, ulong parent)> parts = s.Parts;
            Dictionary<ulong, OsmBuilding> parents = s.Parents; // outlines by id, for their parts
            var pb = new Pbf(data, 0, length);
            while (pb.Next())
            {
                if (pb.Field != 3 || pb.Wire != 2)
                {
                    pb.Skip();
                    continue;
                }
                Pbf lp = pb.Message();
                string name = LayerName(lp);
                bool isPart = name == "building_part";
                if (!isPart && name != "building")
                    continue;
                Layer layer = s.Layer.Read(lp);
                double buffer = Buffer(layer, s);
                foreach (Pbf fp in layer.Features)
                {
                    Feature f = ReadFeature(fp, layer, s.Feature);
                    if (f.Type != 3)
                        continue;
                    List<List<double>> rings = Rings(f.Geometry, true, s);
                    if (Flag(f, "is_underground"))
                        continue;
                    ulong own = HashOf(f, "id");
                    ulong id = own ^ f.Id;
                    ulong parent = isPart ? HashOf(f, "building_id") : 0UL;
                    foreach (OsmBuilding b in Footprints(rings, tile, layer.Extent, buffer, id, s))
                    {
                        b.Part = isPart;
                        b.Type = Text(f, "class") ?? TypeOf(Text(f, "subtype")) ?? "";
                        b.Height = Single(f, "height");
                        b.MinHeight = Single(f, "min_height");
                        b.Levels = Single(f, "num_floors");
                        b.MinLevel = Single(f, "min_floor");
                        b.WallColor = Text(f, "facade_color") is string wc ? OsmTile.ParseColour(wc) : 0;
                        b.RoofColor = Text(f, "roof_color") is string rc ? OsmTile.ParseColour(rc) : 0;
                        b.RoofShape = Text(f, "roof_shape");
                        b.RoofHeight = Single(f, "roof_height");
                        b.RoofDirection = Single(f, "roof_direction");
                        b.RoofOrientation = OsmTile.Orientation(Text(f, "roof_orientation"));
                        b.WallMaterial = Text(f, "facade_material");
                        b.RoofMaterial = Text(f, "roof_material");
                        if (isPart)
                        {
                            parts.Add((b, parent));
                        }
                        else
                        {
                            outlines.Add((b, Flag(f, "has_parts") ? own : 0UL));
                            if (own != 0UL)
                                parents[own] = b;
                        }
                    }
                }
            }
            HashSet<ulong> replaced = s.Replaced;
            tile.Buildings.Capacity = Math.Max(tile.Buildings.Capacity, parts.Count + outlines.Count);
            foreach ((OsmBuilding b, ulong parent) in parts)
            {
                // What a part doesn't say, its building does (its kind, materials, colours).
                if (parent != 0UL && parents.TryGetValue(parent, out OsmBuilding p))
                {
                    replaced.Add(parent);
                    if (b.Type.Length == 0)
                        b.Type = p.Type;
                    b.WallMaterial ??= p.WallMaterial;
                    b.RoofMaterial ??= p.RoofMaterial;
                    if (b.WallColor == 0)
                        b.WallColor = p.WallColor;
                    if (b.RoofColor == 0)
                        b.RoofColor = p.RoofColor;
                }
                tile.Buildings.Add(b);
            }
            foreach ((OsmBuilding b, ulong id) in outlines)
                if (id == 0UL || !replaced.Contains(id))
                    tile.Buildings.Add(b);
            s.ClearOverture(); // the buildings are the tile's now
            return tile;
        }

        /// <summary>A building=* value standing for Overture's broader subtype.</summary>
        static string TypeOf(string subtype)
        {
            switch (subtype)
            {
                case "residential": return "residential";
                case "commercial": return "commercial";
                case "industrial": return "industrial";
                case "education": return "school";
                case "medical": return "hospital";
                case "religious": return "religious";
                case "civic": return "civic";
                case "transportation": return "transportation";
                case "agricultural": return "farm_auxiliary";
                case "outbuilding": return "shed";
                case "service": return "service";
                case "military": return "military";
                default: return null;
            }
        }

        /// <summary>FNV-1a of a string id (Overture's are UUIDs); 0 for none.</summary>
        static ulong Hash(string s)
        {
            if (s == null)
                return 0UL;
            ulong h = 14695981039346656037UL;
            foreach (char c in s)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        static void ReadRoad(Pbf pb, Layer layer, OsmTile tile, Scratch s)
        {
            Feature f = ReadFeature(pb, layer, s.Feature);
            string kind = f.Type == 2 ? Text(f, "class") : null;
            if (kind == null)
                return;
            string brunnel = Text(f, "brunnel");
            if (brunnel == "tunnel")
                return;
            bool bridge = brunnel == "bridge";
            if (kind == "rail" || kind == "transit")
            {
                // Railways: the kind is in subclass (rail, narrow_gauge, preserved, light_rail, subway, tram, ...).
                string sub = Text(f, "subclass");
                string service = Text(f, "service");
                string rail = OsmTile.RailwayKind(sub ?? kind, service);
                if (rail == null)
                    return;
                foreach (List<double> line in Rings(f.Geometry, false, s))
                {
                    if (line.Count < 4)
                        continue;
                    int[] points = ToLatLon(line, tile, layer.Extent, s);
                    tile.RoadList.Add(new OsmRoad { Id = (long)f.Id, Kind = rail, Points = points });
                    if (bridge)
                        tile.Bridges.Add(points);
                }
                return;
            }
            if (!RoadClasses.Contains(kind))
                return;
            if (Text(f, "surface") == "unpaved")
                return; // dirt roads: the imagery shows them as they are
            // Slip roads come as their road's class with ramp=1; OSM calls them *_link.
            if (Truthy(f, "ramp") && kind != "minor" && kind != "service" && kind != "busway")
                kind += "_link";
            foreach (List<double> line in Rings(f.Geometry, false, s))
            {
                if (line.Count < 4)
                    continue;
                int[] points = ToLatLon(line, tile, layer.Extent, s);
                tile.RoadList.Add(new OsmRoad { Id = (long)f.Id, Kind = kind, Points = points });
                if (bridge)
                    tile.Bridges.Add(points);
            }
        }

        /// <summary>Decodes geometry commands into point lists (flat x,y in tile units):
        /// polygon rings (closing point not repeated) or line strings. The lists are the
        /// scratch's, good until the next call.</summary>
        static List<List<double>> Rings(List<uint> g, bool polygon, Scratch s)
        {
            s.ClearRings();
            List<List<double>> result = s.Rings;
            List<double> current = null;
            int x = 0, y = 0;
            for (int i = 0; i < g.Count;)
            {
                uint command = g[i++];
                int id = (int)(command & 7), count = (int)(command >> 3);
                if (id == 1 || id == 2)
                {
                    for (int k = 0; k < count && i + 1 < g.Count; k++)
                    {
                        x += (int)Pbf.ZigZag(g[i++]);
                        y += (int)Pbf.ZigZag(g[i++]);
                        if (id == 1)
                            current = s.NewRing();
                        current?.Add(x);
                        current?.Add(y);
                    }
                }
                else if (id == 7)
                {
                    current = null; // ClosePath: rings are implicitly closed
                }
                else
                {
                    break; // unknown command: the rest can't be trusted
                }
            }
            if (polygon)
                result.RemoveAll(r => r.Count < 6);
            return result;
        }

        static double SignedArea(List<double> r)
        {
            double sum = 0.0;
            int n = r.Count / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
                sum += r[2 * j] * r[2 * i + 1] - r[2 * i] * r[2 * j + 1];
            return 0.5 * sum;
        }

        /// <summary>Sutherland-Hodgman clip of a ring to the tile square [0, extent]: the ring
        /// itself if it is inside, else one of the scratch's clip lists (good until the next call).</summary>
        static List<double> Clip(List<double> ring, int extent, Scratch s)
        {
            bool inside = true;
            for (int i = 0; i < ring.Count && inside; i++)
                inside = ring[i] >= 0 && ring[i] <= extent;
            if (inside)
                return ring;
            List<double> r = ring;
            for (int edge = 0; edge < 4 && r.Count >= 6; edge++)
                r = ClipEdge(r, edge, extent, edge % 2 == 0 ? s.ClipA : s.ClipB);
            return r;
        }

        static List<double> ClipEdge(List<double> r, int edge, double extent, List<double> output)
        {
            // edge 0: x >= 0, 1: x <= extent, 2: y >= 0, 3: y <= extent
            int axis = edge / 2;
            double limit = edge % 2 == 0 ? 0.0 : extent;
            bool keepAbove = edge % 2 == 0;
            bool In(double v) => keepAbove ? v >= limit : v <= limit;
            output.Clear();
            int n = r.Count / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + n - 1) % n;
                double px = r[2 * j], py = r[2 * j + 1], cx = r[2 * i], cy = r[2 * i + 1];
                double pv = axis == 0 ? px : py, cv = axis == 0 ? cx : cy;
                bool pin = In(pv), cin = In(cv);
                if (cin != pin)
                {
                    double t = (limit - pv) / (cv - pv);
                    output.Add(px + (cx - px) * t);
                    output.Add(py + (cy - py) * t);
                }
                if (cin)
                {
                    output.Add(cx);
                    output.Add(cy);
                }
            }
            return output;
        }

        static int[] ToLatLon(List<double> points, OsmTile tile, int extent, Scratch s)
        {
            double n = 1 << OsmTile.Zoom;
            List<int> result = s.LatLon;
            result.Clear();
            for (int i = 0; i < points.Count; i += 2)
            {
                double gx = (tile.X + points[i] / extent) / n, gy = (tile.Y + points[i + 1] / extent) / n;
                int lat = (int)Math.Round(Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * gy))) * 180.0 / Math.PI * 1e7);
                int lon = (int)Math.Round((gx * 360.0 - 180.0) * 1e7);
                int k = result.Count;
                if (k >= 2 && result[k - 2] == lat && result[k - 1] == lon)
                    continue; // clipping and rounding can repeat a point
                result.Add(lat);
                result.Add(lon);
            }
            return result.ToArray();
        }

        [ThreadStatic] static byte[] inflated;
        const int MaxKeptInflated = 16 << 20;

        /// <summary>A gzipped tile unpacked into this thread's buffer, kept for the next tile
        /// (a growing stream copied out made ~11 MB of garbage per Overture city tile,
        /// 2026-10-03): the first `length` bytes are the tile, good until the next call.</summary>
        static byte[] Gunzip(byte[] data, out int length)
        {
            byte[] buffer = inflated ?? new byte[Math.Max(1 << 20, data.Length * 4)];
            int n = 0;
            using (var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                while (true)
                {
                    if (n == buffer.Length)
                        Array.Resize(ref buffer, buffer.Length * 2);
                    int read = input.Read(buffer, n, buffer.Length - n);
                    if (read <= 0)
                        break;
                    n += read;
                }
            }
            inflated = buffer.Length <= MaxKeptInflated ? buffer : null;
            length = n;
            return buffer;
        }

        /// <summary>Protocol buffer reader over part of a byte array. A value (not an object
        /// for every feature, tag list and geometry); methods called on a copy move only the copy.</summary>
        struct Pbf
        {
            readonly byte[] b;
            int p;
            readonly int end;
            public int Field, Wire;

            public Pbf(byte[] bytes, int start, int stop)
            {
                b = bytes;
                p = start;
                end = stop;
                Field = Wire = 0;
            }

            public bool End => p >= end;

            public bool Next()
            {
                if (p >= end)
                    return false;
                ulong key = Varint();
                Field = (int)(key >> 3);
                Wire = (int)(key & 7);
                return true;
            }

            public ulong Varint()
            {
                ulong result = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    if (p >= end)
                        throw new InvalidDataException("vector tile: truncated varint");
                    byte c = b[p++];
                    result |= (ulong)(c & 0x7F) << shift;
                    if (c < 0x80)
                        return result;
                }
                throw new InvalidDataException("vector tile: bad varint");
            }

            public static long ZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

            int Length()
            {
                int len = (int)Varint();
                if (len < 0 || p + len > end)
                    throw new InvalidDataException("vector tile: bad length");
                return len;
            }

            public Pbf Message()
            {
                int len = Length();
                var sub = new Pbf(b, p, p + len);
                p += len;
                return sub;
            }

            public Pbf Packed() => Message();

            public byte[] Bytes => b;

            /// <summary>Where a string field's bytes are, without decoding it.</summary>
            public void StringAt(out int start, out int length)
            {
                length = Length();
                start = p;
                p += length;
            }

            public string String()
            {
                int len = Length();
                string s = Encoding.UTF8.GetString(b, p, len);
                p += len;
                return s;
            }

            public float Float()
            {
                float v = BitConverter.ToSingle(b, p);
                p += 4;
                return v;
            }

            public double Double()
            {
                double v = BitConverter.ToDouble(b, p);
                p += 8;
                return v;
            }

            public void Skip()
            {
                switch (Wire)
                {
                    case 0: Varint(); break;
                    case 1: p += 8; break;
                    case 2: p += Length(); break;
                    case 5: p += 4; break;
                    default: throw new InvalidDataException($"vector tile: wire type {Wire}");
                }
            }
        }
    }
}
