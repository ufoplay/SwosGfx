using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SwosGfx
{
    /// <summary>
    /// Tiled map I/O in the JSON map format:
    /// https://doc.mapeditor.org/en/stable/reference/json-map-format/
    ///
    /// SWOS only ever needs the simplest shape Tiled supports: one orthogonal tile
    /// layer, one embedded tileset backed by one external image, and tile data as a
    /// plain array of GIDs. Keys are emitted in the same order (and with the same
    /// field set) Tiled itself writes, so a round-trip through the editor produces a
    /// near-identical file.
    ///
    /// Reading also accepts the XML .tmx files SwosGfx used to write, so existing
    /// maps keep working; writing is always JSON (.tmj). The format is detected from
    /// the content rather than the extension, so a .json map loads just as well.
    /// </summary>
    public static class TiledMap
    {
        /// <summary>Tiled's own extension for a JSON map (.tmj; .tsj is the tileset one).</summary>
        public const string Extension = ".tmj";

        private const string FormatVersion = "1.10";
        private const string TiledVersion = "1.10.2";
        private const string LayerName = "Tile Layer 1";
        private const string TilesetName = "tiles";

        /// <summary>
        /// A single-layer, single-tileset orthogonal map - everything SwosGfx reads or
        /// writes. <see cref="Gids"/> is row-major, <see cref="Width"/> * <see cref="Height"/>
        /// entries, 1-based (0 = no tile).
        /// </summary>
        public sealed class MapData
        {
            public int Width;
            public int Height;
            public int TileWidth;
            public int TileHeight;

            public int TileCount;
            public int Columns;

            /// <summary>Tileset image filename, relative to the map file.</summary>
            public string ImageSource = "";
            public int ImageWidth;
            public int ImageHeight;

            public int[] Gids = Array.Empty<int>();

            /// <summary>
            /// Extra layers, written after the one in <see cref="Gids"/>. Empty for a
            /// plain single-layer map, which is what every existing caller writes.
            /// </summary>
            public List<LayerData> ExtraLayers = new List<LayerData>();

            /// <summary>Custom properties on the FIRST layer (the one from Gids).</summary>
            public List<LayerProperty> Properties = new List<LayerProperty>();

            /// <summary>Name of the first layer; Tiled's default when left null.</summary>
            public string? FirstLayerName;
        }

        /// <summary>One custom property, in Tiled's own {name,type,value} shape.</summary>
        public sealed class LayerProperty
        {
            public string Name = "";
            public string Type = "int";     // int | string | bool | float
            public string Value = "0";      // already in JSON form

            public static LayerProperty Int(string name, int v) =>
                new LayerProperty { Name = name, Type = "int", Value = v.ToString(CultureInfo.InvariantCulture) };
            public static LayerProperty Text(string name, string v) =>
                new LayerProperty { Name = name, Type = "string", Value = Str(v) };
        }

        /// <summary>An additional tile layer: same dimensions as the map.</summary>
        public sealed class LayerData
        {
            public string Name = "";
            public int[] Gids = Array.Empty<int>();
            public bool Visible = true;
            public List<LayerProperty> Properties = new List<LayerProperty>();
        }

        // ------------------------------------------------------------------
        // Write (JSON)
        // ------------------------------------------------------------------

        public static void Write(string path, MapData map)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (map == null) throw new ArgumentNullException(nameof(map));

            int cells = map.Width * map.Height;
            if (map.Gids.Length != cells)
                throw new ArgumentException($"Expected {cells} tile IDs for a {map.Width}x{map.Height} map, got {map.Gids.Length}.", nameof(map));

            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

            var sb = new StringBuilder();

            sb.Append("{ \"compressionlevel\":-1,\n");
            sb.Append(" \"height\":").Append(Num(map.Height)).Append(",\n");
            sb.Append(" \"infinite\":false,\n");
            sb.Append(" \"layers\":[\n");

            // The map's own layer is first -- TiledMap.ReadJson and AmigaTools both
            // take the FIRST tile layer, so a multi-layer map still round-trips as
            // the pitch it always was.
            AppendLayer(sb, map.FirstLayerName ?? LayerName, map.Gids, true,
                        map.Properties, 1, map.Width, map.Height,
                        map.ExtraLayers.Count == 0);

            for (int li = 0; li < map.ExtraLayers.Count; li++)
            {
                LayerData extra = map.ExtraLayers[li];
                if (extra.Gids.Length != cells)
                    throw new ArgumentException($"Layer '{extra.Name}' has {extra.Gids.Length} tile IDs, expected {cells}.", nameof(map));
                AppendLayer(sb, extra.Name, extra.Gids, extra.Visible, extra.Properties,
                            li + 2, map.Width, map.Height,
                            li == map.ExtraLayers.Count - 1);
            }

            sb.Append(" \"nextlayerid\":").Append(Num(map.ExtraLayers.Count + 2)).Append(",\n");
            sb.Append(" \"nextobjectid\":1,\n");
            sb.Append(" \"orientation\":\"orthogonal\",\n");
            sb.Append(" \"renderorder\":\"right-down\",\n");
            sb.Append(" \"tiledversion\":").Append(Str(TiledVersion)).Append(",\n");
            sb.Append(" \"tileheight\":").Append(Num(map.TileHeight)).Append(",\n");
            sb.Append(" \"tilesets\":[\n");
            sb.Append("        {\n");
            sb.Append("         \"columns\":").Append(Num(map.Columns)).Append(",\n");
            sb.Append("         \"firstgid\":1,\n");
            sb.Append("         \"image\":").Append(Str(map.ImageSource)).Append(",\n");
            sb.Append("         \"imageheight\":").Append(Num(map.ImageHeight)).Append(",\n");
            sb.Append("         \"imagewidth\":").Append(Num(map.ImageWidth)).Append(",\n");
            sb.Append("         \"margin\":0,\n");
            sb.Append("         \"name\":").Append(Str(TilesetName)).Append(",\n");
            sb.Append("         \"spacing\":0,\n");
            sb.Append("         \"tilecount\":").Append(Num(map.TileCount)).Append(",\n");
            sb.Append("         \"tileheight\":").Append(Num(map.TileHeight)).Append(",\n");
            sb.Append("         \"tilewidth\":").Append(Num(map.TileWidth)).Append("\n");
            sb.Append("        }],\n");
            sb.Append(" \"tilewidth\":").Append(Num(map.TileWidth)).Append(",\n");
            sb.Append(" \"type\":\"map\",\n");
            sb.Append(" \"version\":").Append(Str(FormatVersion)).Append(",\n");
            sb.Append(" \"width\":").Append(Num(map.Width)).Append("\n");
            sb.Append("}\n");

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(fullPath, sb.ToString(), encoding);
        }

        /// <summary>
        /// One layer object, in Tiled's own key order (alphabetical, which is what its
        /// writer produces, so a file we write and a file Tiled writes diff cleanly).
        /// </summary>
        private static void AppendLayer(StringBuilder sb, string name, int[] gids, bool visible,
                                        List<LayerProperty> properties, int id,
                                        int width, int height, bool last)
        {
            int cells = width * height;

            sb.Append("        {\n");

            // One map row per line - a 42x55 pitch is 2310 GIDs, and Tiled reads it
            // back just the same whether it is wrapped or not.
            sb.Append("         \"data\":[");
            for (int y = 0; y < height; y++)
            {
                if (y > 0) sb.Append("\n                ");

                for (int x = 0; x < width; x++)
                {
                    sb.Append(Num(gids[y * width + x]));
                    if (y * width + x < cells - 1) sb.Append(", ");
                }
            }
            sb.Append("],\n");

            sb.Append("         \"height\":").Append(Num(height)).Append(",\n");
            sb.Append("         \"id\":").Append(Num(id)).Append(",\n");
            sb.Append("         \"name\":").Append(Str(name)).Append(",\n");
            sb.Append("         \"opacity\":1,\n");

            if (properties != null && properties.Count > 0)
            {
                sb.Append("         \"properties\":[\n");
                for (int i = 0; i < properties.Count; i++)
                {
                    LayerProperty pr = properties[i];
                    sb.Append("                {\"name\":").Append(Str(pr.Name))
                      .Append(",\"type\":").Append(Str(pr.Type))
                      .Append(",\"value\":").Append(pr.Value).Append("}");
                    sb.Append(i == properties.Count - 1 ? "\n" : ",\n");
                }
                sb.Append("         ],\n");
            }

            sb.Append("         \"type\":\"tilelayer\",\n");
            sb.Append("         \"visible\":").Append(visible ? "true" : "false").Append(",\n");
            sb.Append("         \"width\":").Append(Num(width)).Append(",\n");
            sb.Append("         \"x\":0,\n");
            sb.Append("         \"y\":0\n");
            sb.Append(last ? "        }],\n" : "        },\n");
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Str(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Read (JSON, or the .tmx SwosGfx used to write)
        // ------------------------------------------------------------------

        /// <summary>
        /// Load a Tiled map. The format is taken from the content, not the extension,
        /// so a JSON map named .tmx (or vice versa) still loads.
        /// </summary>
        public static MapData Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            string fullPath = Path.GetFullPath(path);
            string text = File.ReadAllText(fullPath);

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                return c == '<' ? ReadTmx(text) : ReadJson(text);
            }

            throw new InvalidDataException($"Tiled map '{Path.GetFileName(fullPath)}' is empty.");
        }

        private static MapData ReadJson(string text)
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            using var doc = JsonDocument.Parse(text, options);
            JsonElement root = doc.RootElement;

            var map = new MapData
            {
                Width = Int(root, "width"),
                Height = Int(root, "height"),
                TileWidth = Int(root, "tilewidth"),
                TileHeight = Int(root, "tileheight")
            };

            string orientation = OptionalString(root, "orientation") ?? "orthogonal";
            if (!orientation.Equals("orthogonal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Tiled map: orientation must be 'orthogonal', not '{orientation}'.");

            if (root.TryGetProperty("infinite", out var infinite) &&
                infinite.ValueKind == JsonValueKind.True)
                throw new InvalidDataException("Tiled map: infinite maps are not supported.");

            // --- tileset (the first embedded one) ---
            JsonElement tileset = FirstOfKind(root, "tilesets", null, "tileset");

            if (tileset.TryGetProperty("source", out _))
                throw new InvalidDataException("Tiled map: external tilesets (\"source\") are not supported; save the tileset inside the map.");

            map.TileCount = Int(tileset, "tilecount");
            map.Columns = Int(tileset, "columns");
            map.ImageSource = String(tileset, "image");
            map.ImageWidth = Int(tileset, "imagewidth");
            map.ImageHeight = Int(tileset, "imageheight");

            int firstGid = tileset.TryGetProperty("firstgid", out var fg) ? fg.GetInt32() : 1;
            if (firstGid != 1)
                throw new InvalidDataException($"Tiled map: tileset firstgid must be 1, not {firstGid}.");

            // --- layer (the first tile layer) ---
            JsonElement layer = FirstOfKind(root, "layers", "tilelayer", "tile layer");

            if (!layer.TryGetProperty("data", out var data))
                throw new InvalidDataException("Tiled map: tile layer has no \"data\".");

            if (data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Tiled map: tile layer \"data\" must be an array of GIDs; re-save with \"Tile Layer Format: CSV\".");

            int cells = map.Width * map.Height;
            var gids = new int[cells];
            int i = 0;

            foreach (JsonElement cell in data.EnumerateArray())
            {
                if (i >= cells)
                    throw new InvalidDataException($"Tiled map: expected {cells} tile IDs, got more.");

                // GIDs carry the flip flags in the top bits; SWOS has no flipped tiles,
                // but mask them off rather than reading a nonsense tile index.
                gids[i++] = (int)(cell.GetUInt32() & 0x0FFFFFFFu);
            }

            if (i != cells)
                throw new InvalidDataException($"Tiled map: expected {cells} tile IDs, got {i}.");

            map.Gids = gids;
            return map;
        }

        /// <summary>
        /// First array entry whose "type" matches <paramref name="type"/> (or the first
        /// entry when <paramref name="type"/> is null).
        /// </summary>
        private static JsonElement FirstOfKind(JsonElement root, string arrayName, string? type, string what)
        {
            if (root.TryGetProperty(arrayName, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in array.EnumerateArray())
                {
                    if (type == null)
                        return entry;

                    if (entry.TryGetProperty("type", out var t) &&
                        t.ValueKind == JsonValueKind.String &&
                        string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }

            throw new InvalidDataException($"Tiled map: no {what} found in \"{arrayName}\".");
        }

        private static int Int(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException($"Tiled map: missing or non-numeric \"{name}\".");

            return value.GetInt32();
        }

        private static string String(JsonElement element, string name)
        {
            return OptionalString(element, name)
                ?? throw new InvalidDataException($"Tiled map: missing or non-string \"{name}\".");
        }

        private static string? OptionalString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                return null;

            return value.GetString();
        }

        /// <summary>Legacy XML .tmx reader, kept so maps written before the JSON switch still load.</summary>
        private static MapData ReadTmx(string text)
        {
            var doc = XDocument.Parse(text);

            var mapElem = doc.Element("map")
                ?? throw new InvalidDataException("TMX: missing <map> root element.");

            var map = new MapData
            {
                Width = Attr(mapElem, "width"),
                Height = Attr(mapElem, "height"),
                TileWidth = Attr(mapElem, "tilewidth"),
                TileHeight = Attr(mapElem, "tileheight")
            };

            var tilesetElem = mapElem.Element("tileset")
                ?? throw new InvalidDataException("TMX: missing <tileset> element.");

            map.TileCount = Attr(tilesetElem, "tilecount");
            map.Columns = Attr(tilesetElem, "columns");

            var imageElem = tilesetElem.Element("image")
                ?? throw new InvalidDataException("TMX: missing <image> inside <tileset>.");

            map.ImageSource = (string?)imageElem.Attribute("source")
                ?? throw new InvalidDataException("TMX: <image> missing 'source' attribute.");
            map.ImageWidth = Attr(imageElem, "width");
            map.ImageHeight = Attr(imageElem, "height");

            var layerElem = mapElem.Element("layer")
                ?? throw new InvalidDataException("TMX: missing <layer> element.");
            var dataElem = layerElem.Element("data")
                ?? throw new InvalidDataException("TMX: missing <data> inside <layer>.");

            string encoding = (string?)dataElem.Attribute("encoding") ?? "";
            if (!encoding.Equals("csv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("TMX <data> encoding must be 'csv'.");

            string[] tokens = (dataElem.Value ?? "").Split(
                new[] { ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            int cells = map.Width * map.Height;
            if (tokens.Length != cells)
                throw new InvalidDataException($"TMX: expected {cells} tile IDs, got {tokens.Length}.");

            var gids = new int[cells];
            for (int i = 0; i < tokens.Length; i++)
                gids[i] = (int)(uint.Parse(tokens[i], CultureInfo.InvariantCulture) & 0x0FFFFFFFu);

            map.Gids = gids;
            return map;
        }

        private static int Attr(XElement element, string name)
        {
            var attribute = element.Attribute(name)
                ?? throw new InvalidDataException($"TMX: <{element.Name.LocalName}> missing '{name}' attribute.");

            return (int)attribute;
        }
    }
}
