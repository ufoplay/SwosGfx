using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static RncProPack.RncProcessor;

namespace SwosGfx
{
    /// <summary>
    /// Types of pitch in SWOS - controls palette tweaks.
    /// Must match original enum order: FROZEN, MUDDY, WET, SOFT, NORMAL, DRY, HARD.
    /// </summary>
    public enum PitchType
    {
        Frozen = 0,
        Muddy = 1,
        Wet = 2,
        Soft = 3,
        Normal = 4,
        Dry = 5,
        Hard = 6
    }

    /// <summary>
    /// Options for the Tiled pitch export. The names mirror the equivalent Tiled2Bin
    /// "-slice" switches so the two tools produce interchangeable maps.
    /// </summary>
    public sealed class PitchTiledOptions
    {
        /// <summary>
        /// Reserve tile id 0 for an all-index-0 tile (Tiled2Bin -insertblanktile).
        /// With this set the map never contains gid 0 ("no tile"): empty cells point
        /// at the blank tile instead, so the map round-trips through Tiled and back
        /// to a SWOS .MAP without holes.
        /// </summary>
        public bool InsertBlankTile;

        /// <summary>Tileset image width in pixels (Tiled2Bin -tilesetwidth). Default 256 = 16 tiles across.</summary>
        public int TilesetWidth = 256;

        /// <summary>
        /// Put the crowd on its own pair of layers ("crowd-sit" / "crowd-stand")
        /// instead of leaving it in the pitch layer -- the v2 pitch format
        /// (docs/PITCH_FORMAT_V2.md in SWOS United). Default on.
        /// </summary>
        public bool SplitCrowdLayers = true;

        /// <summary>
        /// Which pattern indices are fans. 1..22 is the engine's own rule
        /// (MAX_FAN_TILE_INDEX) and holds for the Amiga maps and DOS pitches 1-5.
        /// DOS pitch6 orders its patterns differently -- override with -fanrange=a-b.
        /// </summary>
        public int FirstFanPattern = DosPitch.DefaultFirstFanPattern;
        public int LastFanPattern = DosPitch.DefaultLastFanPattern;

        /// <summary>Set when -fanrange was given: use the range, skip the derivation.</summary>
        public bool FanRangeExplicit;

        /// <summary>
        /// -derivefans: infer the fan patterns from the pitch instead of using the
        /// engine's 1..22. Needed for DOS pitch6, over-selects on the others.
        /// </summary>
        public bool DeriveFans;
    }

    /// <summary>
    /// Minimal abstraction for accessing pitch pattern data.
    /// Have your DosPitchPattern class implement this.
    /// </summary>
    public interface IDosPitchPatternSource
    {
        /// <summary>Maximum number of pitches (usually 6).</summary>
        int MaxPitch { get; }

        /// <summary>Number of pointer entries for the given pitch (num_ptrs in C).</summary>
        int GetPointerCount(int pitchIndex);

        /// <summary>
        /// Return the pattern index (0..maxpatterns-1) for the given pointer entry.
        /// This corresponds to (ptrs[pointerIndex] - data) / 256 in the original C.
        /// </summary>
        int GetPatternIndexForPointer(int pitchIndex, int pointerIndex);

        /// <summary>
        /// Returns a span over the 16x16 (256-byte) pattern with given pattern index.
        /// </summary>
        ReadOnlySpan<byte> GetPatternBytes(int pitchIndex, int patternIndex);
    }

    public sealed class DosPitch
    {
        public const int Width = 672;  // PITCH_W
        public const int Height = 880; // PITCH_H

        public const int PatternSize = 16;
        public const int Columns = 42; // 672 / 16
        public const int Rows = 55;

        /// <summary>
        /// The engine's fan tiles: pitch pattern indices 1..22 (MAX_FAN_TILE_INDEX in
        /// SWOS United's include/display.h), in consecutive odd/even pairs -- odd is
        /// sitting, even is the same fans standing.
        /// </summary>
        public const int DefaultFirstFanPattern = 1;
        public const int DefaultLastFanPattern = 22;

        /// <summary>The two stands: the engine only ever animates fans here.</summary>
        public const int FanPointerTableRow = 0;
        public const int LastTopStandRow = 4;
        public const int FirstBottomStandRow = 50;    // 880 / 16

        private readonly IDosPitchPatternSource _patterns;

        public DosPitch(IDosPitchPatternSource patterns)
        {
            _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        }

        /// <summary>
        /// Build the full pitch image as palette indices (top-down int[,] [y,x]) + palette (ARGB uint[]).
        /// </summary>
        public (byte[,] Indices, uint[] Palette) BuildPitchImage(int pitchIndex, PitchType pitchType)
        {
            if (pitchIndex < 0 || pitchIndex >= _patterns.MaxPitch)
                throw new ArgumentOutOfRangeException(nameof(pitchIndex));

            uint[] palette = DosPalette.GetPitchPalette(pitchType);

            // Top-down indices: [y,x]
            var indices = new byte[Height, Width];

            int pointerCount = _patterns.GetPointerCount(pitchIndex);

            for (int tileY = 0; tileY < Rows; tileY++)
            {
                for (int tileX = 0; tileX < Columns; tileX++)
                {
                    int pointerIndex = Columns * tileY + tileX;

                    // Guard against short pointer tables
                    if (pointerIndex >= pointerCount)
                        continue;

                    int patternIndex = _patterns.GetPatternIndexForPointer(pitchIndex, pointerIndex);
                    ReadOnlySpan<byte> pattern = _patterns.GetPatternBytes(pitchIndex, patternIndex);

                    BlitPattern(pattern, indices, tileX * PatternSize, tileY * PatternSize);
                }
            }

            return (indices, palette);
        }

        public void SavePitchAsBmp(int pitchIndex, PitchType pitchType, string path, int colorCount = 256)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            var (indices, palette) = BuildPitchImage(pitchIndex, pitchType);
            Write8bppIndexedBmp(path, Width, Height, indices, palette, colorCount);
        }

        public void SavePitchAsTiled(int pitchIndex, PitchType pitchType, string path, int colorCount = 256, PitchTiledOptions? options = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (pitchIndex < 0 || pitchIndex >= _patterns.MaxPitch)
                throw new ArgumentOutOfRangeException(nameof(pitchIndex));

            options ??= new PitchTiledOptions();

            int tilesetColumns = Math.Max(1, options.TilesetWidth / PatternSize);

            string fullMapPath = Path.GetFullPath(path);
            string mapDir = Path.GetDirectoryName(fullMapPath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(fullMapPath);
            string tilesBmpPath = Path.Combine(mapDir, baseName + ".bmp");

            uint[] palette = DosPalette.GetPitchPalette(pitchType);
            int pointerCount = _patterns.GetPointerCount(pitchIndex);

            // Tiles are deduplicated on pixel content (Tiled2Bin -slice -norepeat),
            // not on pattern index: several pointer entries can resolve to distinct
            // pattern slots that hold identical pixels.
            var tileToId = new Dictionary<byte[], int>(TilePixelComparer.Instance);
            var tilePixels = new List<byte[]>(); // each is 16*16 bytes (indices)

            // GIDs for the map layer, row-major (1-based tile IDs; 0 = empty)
            var gidMap = new int[Rows * Columns];

            // The crowd, split onto its own pair of layers (docs/PITCH_FORMAT_V2.md).
            // The engine's fans are pitch PATTERN indices 1..22 in consecutive
            // odd/even pairs -- odd sitting, even standing -- and it animates a cell
            // by stepping its pointer one tile either way. That +-1 does NOT survive
            // this exporter: tiles are deduplicated on pixel content, so the tile
            // after a sitting fan in the sheet is whatever happened to appear next.
            // Hence two layers holding the two states of the same cells, which is
            // also what a pitch author can see and edit.
            var gidCrowdSit = new int[Rows * Columns];
            var gidCrowdStand = new int[Rows * Columns];
            int crowdCells = 0;

            // -insertblanktile: tile id 0 is an all-index-0 tile, so cells with no
            // pattern (a short pointer table) reference it instead of leaving a hole.
            int blankGid = 0;

            if (options.InsertBlankTile)
            {
                var blankTile = new byte[PatternSize * PatternSize];
                tileToId[blankTile] = 0;
                tilePixels.Add(blankTile);
                blankGid = 1;
            }

            // Which patterns are fans, derived from this pitch rather than assumed.
            // The engine hard-codes 1..22 (MAX_FAN_TILE_INDEX) and that is right for
            // the Amiga maps and DOS pitches 1-5 -- but DOS pitch6 gives its two
            // stands DIFFERENT tiles (top 0..9, bottom 202..210), so no single range
            // can describe it. The rule that does: a fan pattern appears ONLY in the
            // stand bands and pairs off odd/even with another such pattern, which is
            // exactly the sit/stand pairing the engine animates.
            HashSet<int> fanPatterns = options.SplitCrowdLayers
                ? FanPatterns(pitchIndex, pointerCount, options)
                : new HashSet<int>();

            for (int tileY = 0; tileY < Rows; tileY++)
            {
                for (int tileX = 0; tileX < Columns; tileX++)
                {
                    int pointerIndex = Columns * tileY + tileX;

                    if (pointerIndex >= pointerCount)
                    {
                        gidMap[pointerIndex] = blankGid;
                        continue;
                    }

                    int patternIndex = _patterns.GetPatternIndexForPointer(pitchIndex, pointerIndex);

                    ReadOnlySpan<byte> pattern = _patterns.GetPatternBytes(pitchIndex, patternIndex);
                    if (pattern.Length != PatternSize * PatternSize)
                        throw new InvalidOperationException("Pattern must be 16x16 (256 bytes).");

                    var tileData = new byte[PatternSize * PatternSize];
                    pattern.CopyTo(tileData);

                    if (!tileToId.TryGetValue(tileData, out int tileId))
                    {
                        tileId = tilePixels.Count;
                        tilePixels.Add(tileData);
                        tileToId[tileData] = tileId;
                    }

                    // Classify on the PATTERN index, never on the tile id: the two
                    // line up by accident on pitch1-5 and are off by one on pitch6,
                    // where -insertblanktile takes id 0.
                    // ... and only in the stands. The fan range is an AMIGA fact:
                    // DOS pitch6 lays its patterns out differently and uses 3, 4 and
                    // 10..21 as ordinary grass, so an index test alone would scatter
                    // "crowd" across the middle of the field. The bands are where the
                    // engine looks for fans anyway (it picks by camera Y).
                    // Row 0 is NOT scenery and NOT spectators: it is the fan POINTER
                    // TABLE. The engine aliases slots 0..20 as fan group A and 21..41
                    // as fan group B (FAN_PTR_TABLE_OFFSET/4 = 21, src/pitch.c step 3)
                    // and walks those two groups to recolour the fans into the two
                    // teams' kits -- which is why it holds one of every fan pattern
                    // including the standing halves that nothing else references. The
                    // camera clamps at y=16 (move_camera.c: newIntY < 0x10), exactly
                    // one tile row, so it is never drawn either. v2 says which tiles
                    // are whose with layers, so the row keeps its tiles for the old
                    // loader but stays off the crowd layers.
                    bool inStandBand = (tileY > FanPointerTableRow && tileY <= LastTopStandRow)
                                       || tileY >= FirstBottomStandRow;

                    if (inStandBand && fanPatterns.Contains(patternIndex))
                    {
                        int sitPattern = (patternIndex & 1) != 0 ? patternIndex : patternIndex - 1;
                        gidCrowdSit[pointerIndex] = TileIdForPattern(pitchIndex, sitPattern, tileToId, tilePixels) + 1;
                        gidCrowdStand[pointerIndex] = TileIdForPattern(pitchIndex, sitPattern + 1, tileToId, tilePixels) + 1;
                        gidMap[pointerIndex] = 0;      // the crowd layer draws this cell
                        crowdCells++;
                        continue;
                    }

                    gidMap[pointerIndex] = tileId + 1;
                }
            }

            // A pitch whose stands are mostly NOT in the fan range is telling us the
            // range is wrong for it (DOS pitch6). Say so rather than shipping a crowd
            // layer with holes in it -- a silent wrong answer here would not surface
            // until the ground half-animated in a match.
            if (options.SplitCrowdLayers)
            {
                int standCells = 0;
                for (int ty = 0; ty < Rows; ty++)
                    if (ty <= LastTopStandRow || ty >= FirstBottomStandRow)
                        standCells += Columns;
                if (crowdCells * 2 < standCells)
                    Console.Error.WriteLine(
                        $"  warning: pitch{pitchIndex + 1}: only {crowdCells} of {standCells} stand cells " +
                        $"are fan patterns {options.FirstFanPattern}..{options.LastFanPattern} - " +
                        "the fan range looks wrong for this pitch (-fanrange=a-b)");
            }

            int tileCount = tilePixels.Count;
            int tilesetRows = (tileCount + tilesetColumns - 1) / tilesetColumns;

            int tilesetWidth = tilesetColumns * PatternSize;
            int tilesetHeight = tilesetRows * PatternSize;

            // Top-down tileset indices: [y,x]
            var tilesetIndices = new byte[tilesetHeight, tilesetWidth];

            for (int tileId = 0; tileId < tileCount; tileId++)
            {
                byte[] tileData = tilePixels[tileId];
                int col = tileId % tilesetColumns;
                int row = tileId / tilesetColumns;

                int dstX0 = col * PatternSize;
                int dstY0 = row * PatternSize;

                for (int y = 0; y < PatternSize; y++)
                {
                    for (int x = 0; x < PatternSize; x++)
                    {
                        int srcIndex = y * PatternSize + x;
                        tilesetIndices[dstY0 + y, dstX0 + x] = tileData[srcIndex];
                    }
                }
            }

            // Write tileset BMP
            Write8bppIndexedBmp(tilesBmpPath, tilesetWidth, tilesetHeight, tilesetIndices, palette, colorCount);

            // Write the Tiled JSON map
            TiledMap.Write(fullMapPath, new TiledMap.MapData
            {
                Width = Columns,
                Height = Rows,
                TileWidth = PatternSize,
                TileHeight = PatternSize,
                TileCount = tileCount,
                Columns = tilesetColumns,
                ImageSource = Path.GetFileName(tilesBmpPath),
                ImageWidth = tilesetWidth,
                ImageHeight = tilesetHeight,
                Gids = gidMap,
                FirstLayerName = crowdCells > 0 ? "pitch" : null,
                Properties = crowdCells > 0 ? PitchLayerProperties() : new List<TiledMap.LayerProperty>(),
                ExtraLayers = crowdCells > 0 ? CrowdLayers(gidCrowdSit, gidCrowdStand)
                                             : new List<TiledMap.LayerData>()
            });
        }

        // ---------------------------------------------------------------------
        // Internal helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// The fan patterns of one pitch: those used only in the stand bands, that
        /// pair off odd/even with another such pattern (odd sitting, even standing).
        /// An explicit -fanrange overrides the derivation.
        /// </summary>
        private HashSet<int> FanPatterns(int pitchIndex, int pointerCount, PitchTiledOptions options)
        {
            var result = new HashSet<int>();

            // The ENGINE's rule by default, not a clever one of our own: fans are
            // pattern indices 1..22 (MAX_FAN_TILE_INDEX), which is what the crowd
            // animation actually tests, so a default export reproduces exactly the
            // crowd the game animates today.
            //
            // -derivefans instead infers them: patterns used only in the stand bands
            // that pair off odd/even. That reproduces 1..22 on pitches 1-5 AND finds
            // DOS pitch6's two disjoint sets (top 1,2,5..8; bottom 203..210) -- but it
            // also picks up the hoarding and barrier rows the engine never touches, so
            // it is opt-in until someone decides what pitch6's crowd should be.
            if (!options.DeriveFans)
            {
                for (int p = options.FirstFanPattern; p <= options.LastFanPattern; p++)
                    result.Add(p);
                return result;
            }

            if (options.FanRangeExplicit)
            {
                for (int p = options.FirstFanPattern; p <= options.LastFanPattern; p++)
                    result.Add(p);
                return result;
            }

            var inStands = new HashSet<int>();
            var inField = new HashSet<int>();
            for (int pointerIndex = 0; pointerIndex < pointerCount && pointerIndex < Rows * Columns; pointerIndex++)
            {
                int row = pointerIndex / Columns;
                int pattern = _patterns.GetPatternIndexForPointer(pitchIndex, pointerIndex);
                if (row <= LastTopStandRow || row >= FirstBottomStandRow) inStands.Add(pattern);
                else inField.Add(pattern);
            }
            inStands.ExceptWith(inField);

            foreach (int p in inStands)
                if ((p & 1) != 0 && inStands.Contains(p + 1))
                {
                    result.Add(p);        // sitting
                    result.Add(p + 1);    // ... and the same fans standing
                }

            return result;
        }

        /// <summary>
        /// The tile id for a pattern index, adding its pixels to the sheet if nothing
        /// has used them yet. The standing half of a fan pair is often not referenced
        /// by the map at all -- the pitch ships with the whole ground seated -- so it
        /// has to be brought in deliberately or the crowd would have nothing to stand
        /// up into.
        /// </summary>
        private int TileIdForPattern(int pitchIndex, int patternIndex,
                                     Dictionary<byte[], int> tileToId, List<byte[]> tilePixels)
        {
            ReadOnlySpan<byte> pattern = _patterns.GetPatternBytes(pitchIndex, patternIndex);
            if (pattern.Length != PatternSize * PatternSize)
                throw new InvalidOperationException("Pattern must be 16x16 (256 bytes).");

            var tileData = new byte[PatternSize * PatternSize];
            pattern.CopyTo(tileData);

            if (!tileToId.TryGetValue(tileData, out int tileId))
            {
                tileId = tilePixels.Count;
                tilePixels.Add(tileData);
                tileToId[tileData] = tileId;
            }
            return tileId;
        }

        private static List<TiledMap.LayerProperty> PitchLayerProperties() =>
            new List<TiledMap.LayerProperty>
            {
                TiledMap.LayerProperty.Text("swos:role", "pitch"),
                TiledMap.LayerProperty.Int("swos:z", -100),
            };

        /// <summary>
        /// The two crowd layers. "crowd-stand" ships hidden: it is the other half of
        /// every fan cell, which the engine swaps in as the ground stands up, and
        /// showing both at once in an editor would just cover the seated one.
        /// </summary>
        private static List<TiledMap.LayerData> CrowdLayers(int[] sit, int[] stand) =>
            new List<TiledMap.LayerData>
            {
                new TiledMap.LayerData
                {
                    Name = "crowd-sit", Gids = sit, Visible = true,
                    Properties = new List<TiledMap.LayerProperty>
                    {
                        TiledMap.LayerProperty.Text("swos:role", "crowd-sit"),
                        TiledMap.LayerProperty.Int("swos:z", -90),
                    }
                },
                new TiledMap.LayerData
                {
                    Name = "crowd-stand", Gids = stand, Visible = false,
                    Properties = new List<TiledMap.LayerProperty>
                    {
                        TiledMap.LayerProperty.Text("swos:role", "crowd-stand"),
                        TiledMap.LayerProperty.Int("swos:z", -90),
                    }
                },
            };

        /// <summary>Exact (not hash-only) equality over 16x16 tile pixels, for tile deduplication.</summary>
        private sealed class TilePixelComparer : IEqualityComparer<byte[]>
        {
            public static readonly TilePixelComparer Instance = new TilePixelComparer();

            public bool Equals(byte[]? a, byte[]? b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.Length != b.Length) return false;

                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;

                return true;
            }

            public int GetHashCode(byte[] tile)
            {
                // FNV-1a
                uint hash = 2166136261u;

                for (int i = 0; i < tile.Length; i++)
                {
                    hash ^= tile[i];
                    hash *= 16777619u;
                }

                return unchecked((int)hash);
            }
        }

        private static void BlitPattern(ReadOnlySpan<byte> pattern, byte[,] destIndices, int destX, int destY)
        {
            if (pattern.Length != PatternSize * PatternSize)
                throw new ArgumentException("Pattern must be 16x16 (256 bytes).", nameof(pattern));

            for (int y = 0; y < PatternSize; y++)
            {
                int srcOffset = y * PatternSize;
                int dy = destY + y;

                for (int x = 0; x < PatternSize; x++)
                {
                    int dx = destX + x;
                    destIndices[dy, dx] = pattern[srcOffset + x];
                }
            }
        }

        /// <summary>
        /// Write an 8bpp indexed BMP from top-down indices [y,x] and an ARGB palette (uint[]).
        /// </summary>
        private static void Write8bppIndexedBmp(
            string path,
            int width,
            int height,
            byte[,] indices,
            uint[] palette,
            int colorCount)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (indices.GetLength(0) != height || indices.GetLength(1) != width)
                throw new ArgumentException("indices dimensions do not match width/height.", nameof(indices));
            if (palette.Length < 256)
                throw new ArgumentException("Palette must have at least 256 entries (ARGB).", nameof(palette));

            AmigaPalette.QuantizeBmp(ref indices, ref palette, colorCount);

            // BMP rows are padded to 4 bytes.
            int stride = (width + 3) & ~3;
            int pixelBytes = stride * height;

            // Build bottom-up byte buffer.
            var pixelData = new byte[pixelBytes];

            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y;       // bottom-up
                int dstRow = y * stride;

                for (int x = 0; x < width; x++)
                {
                    int idx = indices[srcY, x];
                    if ((uint)idx > 255u)
                        throw new InvalidDataException($"Pixel index {idx} at ({x},{srcY}) is not in 0..255.");

                    pixelData[dstRow + x] = (byte)idx;
                }

                // padding bytes already zero
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            // --- BITMAPFILEHEADER (14 bytes) ---
            bw.Write((ushort)0x4D42); // 'BM'

            int headerSize = 14 + 40 + 256 * 4;
            int fileSize = headerSize + pixelBytes;

            bw.Write(fileSize);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write(headerSize);

            // --- BITMAPINFOHEADER (40 bytes) ---
            bw.Write(40);                 // biSize
            bw.Write(width);              // biWidth
            bw.Write(height);             // biHeight (positive -> bottom-up)
            bw.Write((ushort)1);          // biPlanes
            bw.Write((ushort)8);          // biBitCount
            bw.Write(0);                  // biCompression = BI_RGB
            bw.Write(pixelBytes);         // biSizeImage (including padding)
            bw.Write(0);                  // biXPelsPerMeter
            bw.Write(0);                  // biYPelsPerMeter
            bw.Write(256);                // biClrUsed (keep 256-entry table)
            bw.Write(0);                  // biClrImportant

            // --- Color table: 256 x (B,G,R,0) ---
            const uint FallbackMagenta = 0xFFFF00FF;

            for (int i = 0; i < 256; i++)
            {
                uint argb = (i < palette.Length) ? palette[i] : FallbackMagenta;
                AmigaPalette.UnpackRgb(argb, out byte r, out byte g, out byte b);

                bw.Write(b);
                bw.Write(g);
                bw.Write(r);
                bw.Write((byte)0);
            }

            // --- Pixel data ---
            bw.Write(pixelData);
        }
    }
}
