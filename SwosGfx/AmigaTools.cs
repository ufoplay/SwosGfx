using System;
using System.Collections.Generic;
using System.IO;
using static SwosGfx.AmigaPalette;

namespace SwosGfx
{
    public sealed class AmigaTools
    {
        // Pitch constants
        private const int PitchTilesX = 42;
        private const int PitchTilesY = 55;
        private const int TileSize = 16;
        private const int PitchWidth = PitchTilesX * TileSize;   // 672
        private const int PitchHeight = PitchTilesY * TileSize;  // 880

        // MAP layout constants
        private const int MapHeaderSize = PitchTilesX * PitchTilesY * 4; // 42*55*4 = 9240
        private const int SpriteRows = 16;
        private const int BytesPerPlanePerRow16 = TileSize / 8; // 16px / 8 = 2 bytes per plane per row

        // Default number of columns for tileset image in pitch-map-to-tiled
        private const int TileSheetColumns = 16;

        // Tile data structures (equivalent to TMapping / TPitchBitmapItem)
        private struct Mapping
        {
            public byte B1, B2, B3, B4;
        }

        private sealed class PitchBitmapItem
        {
            public readonly byte[,] Bits = new byte[TileSize, TileSize]; // [y,x]
        }

        // ------------------------------------------------------------------
        // Planar helpers for 16-pixel rows (1-8 bitplanes)
        // ------------------------------------------------------------------

        // Encode 16 pixels (indices 0..255) into planar bytes for N bitplanes.
        // For 16 pixels, each plane = 2 bytes, so total = bitplanes * 2.
        private static void EncodeAmigaRow16(byte[] inputPixels16, int bitplanes, byte[] outputRowBytes)
        {
            if (inputPixels16 == null) throw new ArgumentNullException(nameof(inputPixels16));
            if (outputRowBytes == null) throw new ArgumentNullException(nameof(outputRowBytes));
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            const int width = TileSize; // 16
            int bytesPerPlanePerRow = BytesPerPlanePerRow16; // 2

            if (inputPixels16.Length != width)
                throw new ArgumentException($"inputPixels16 must be length {width}.");
            if (outputRowBytes.Length != bytesPerPlanePerRow * bitplanes)
                throw new ArgumentException($"outputRowBytes must be length {bytesPerPlanePerRow * bitplanes} for {bitplanes} bitplanes.");

            int maxIndex = (1 << bitplanes) - 1;
            int outIndex = 0;

            for (int plane = 0; plane < bitplanes; plane++)
            {
                for (int block = 0; block < bytesPerPlanePerRow; block++)
                {
                    byte value = 0;

                    // Each byte packs 8 pixels for this plane
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int x = block * 8 + bit;     // pixel index 0..15
                        int pix = inputPixels16[x];  // full index (0..255)

                        if (pix > maxIndex)
                            throw new InvalidOperationException(
                                $"Pixel index {pix} exceeds maximum {maxIndex} for {bitplanes} bitplanes.");

                        int bitVal = (pix >> plane) & 1; // plane 0 = LSB

                        // MSB first: left-shift then OR in the new bit
                        value = (byte)((value << 1) | bitVal);
                    }

                    outputRowBytes[outIndex++] = value;
                }
            }
        }

        // Decode planar bytes for a 16-pixel row (1-8 bitplanes) to indices 0..255.
        private static void DecodeAmigaRow16(byte[] inputRowBytes, int bitplanes, byte[] outputPixels16)
        {
            if (inputRowBytes == null) throw new ArgumentNullException(nameof(inputRowBytes));
            if (outputPixels16 == null) throw new ArgumentNullException(nameof(outputPixels16));
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            const int width = TileSize; // 16
            int bytesPerPlanePerRow = BytesPerPlanePerRow16; // 2

            if (inputRowBytes.Length != bytesPerPlanePerRow * bitplanes)
                throw new ArgumentException($"inputRowBytes must be length {bytesPerPlanePerRow * bitplanes} for {bitplanes} bitplanes.");
            if (outputPixels16.Length != width)
                throw new ArgumentException($"outputPixels16 must be length {width}.");

            for (int x = 0; x < width; x++)
            {
                int pixelIndex = 0;

                int byteIndexInPlane = x / 8;      // 0 or 1
                int bitIndexInByte = 7 - (x % 8);  // MSB first

                for (int plane = 0; plane < bitplanes; plane++)
                {
                    int planeBase = plane * bytesPerPlanePerRow;
                    byte b = inputRowBytes[planeBase + byteIndexInPlane];
                    int bit = (b >> bitIndexInByte) & 1;
                    pixelIndex |= (bit << plane);
                }

                outputPixels16[x] = (byte)pixelIndex;
            }
        }

        // ------------------------------------------------------------------
        // BMP helper: 4/8bpp indexed, no System.Drawing
        // ------------------------------------------------------------------

        internal static class Bmp8Helper
        {
            private const ushort BmpMagic = 0x4D42; // 'BM'
            private const int FileHeaderSize = 14;
            private const int InfoHeaderSize = 40;
            private const int PaletteEntries = 256;
            private const int PaletteBytes = PaletteEntries * 4; // BGRA

            /// <summary>
            /// Read an indexed BMP (4bpp or 8bpp) and return indices[y,x] + ARGB palette[256].
            /// For 4bpp, only the first 16 entries are typically used; the rest are zeroed.
            /// </summary>
            public static byte[,] Read8bppBmpToIndices(
                string path,
                out uint[] paletteArgb,
                out int width,
                out int height)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                // BITMAPFILEHEADER
                ushort magic = br.ReadUInt16();
                if (magic != BmpMagic)
                    throw new InvalidDataException("Not a BMP file (missing 'BM').");

                uint fileSize = br.ReadUInt32();
                br.ReadUInt16(); // reserved1
                br.ReadUInt16(); // reserved2
                uint pixelOffset = br.ReadUInt32();

                // BITMAPINFOHEADER
                uint infoSize = br.ReadUInt32();
                if (infoSize < InfoHeaderSize)
                    throw new InvalidDataException("Unsupported BMP info header size.");

                width = br.ReadInt32();
                height = br.ReadInt32();
                ushort planes = br.ReadUInt16();
                ushort bitCount = br.ReadUInt16();
                uint compression = br.ReadUInt32();
                uint sizeImage = br.ReadUInt32();
                int xPels = br.ReadInt32();
                int yPels = br.ReadInt32();
                uint clrUsed = br.ReadUInt32();
                uint clrImportant = br.ReadUInt32();

                if (bitCount != 4 && bitCount != 8)
                    throw new InvalidDataException("Only 4bpp or 8bpp indexed BMPs are supported.");
                if (compression != 0)
                    throw new InvalidDataException("Compressed BMPs are not supported.");
                if (planes != 1)
                    throw new InvalidDataException("Invalid planes count in BMP (expected 1).");

                bool bottomUp = height > 0;
                if (!bottomUp)
                    throw new InvalidDataException("Top-down BMPs (negative height) are not supported here.");

                int heightAbs = height;
                int bitsPerPixel = bitCount;

                // Determine how many palette entries are actually stored
                int paletteCount;
                if (clrUsed != 0)
                    paletteCount = (int)Math.Min(clrUsed, (uint)PaletteEntries);
                else
                    paletteCount = (bitCount == 4) ? 16 : 256;

                // Palette
                paletteArgb = new uint[PaletteEntries];

                // Seek to palette (immediately after BITMAPFILEHEADER + INFOHEADER)
                int paletteOffset = FileHeaderSize + (int)infoSize;
                fs.Seek(paletteOffset, SeekOrigin.Begin);

                for (int i = 0; i < paletteCount; i++)
                {
                    byte b = br.ReadByte();
                    byte g = br.ReadByte();
                    byte r = br.ReadByte();
                    byte a = br.ReadByte(); // reserved (ignored)

                    paletteArgb[i] = (uint)((r << 16) | (g << 8) | b);
                }

                // Any unused palette slots are left as 0
                for (int i = paletteCount; i < PaletteEntries; i++)
                    paletteArgb[i] = 0;

                // Seek to pixel data
                fs.Seek(pixelOffset, SeekOrigin.Begin);

                // General formula: rows are DWORD-aligned
                int rowSize = ((width * bitsPerPixel + 31) / 32) * 4;
                byte[] row = new byte[rowSize];
                byte[,] indices = new byte[heightAbs, width];

                for (int rowIndex = 0; rowIndex < heightAbs; rowIndex++)
                {
                    int read = br.Read(row, 0, rowSize);
                    if (read != rowSize)
                        throw new EndOfStreamException("Unexpected end of BMP file reading pixel data.");

                    int destY = heightAbs - 1 - rowIndex; // convert bottom-up to top-down

                    if (bitsPerPixel == 8)
                    {
                        // one byte per pixel
                        for (int x = 0; x < width; x++)
                            indices[destY, x] = row[x];
                    }
                    else // 4bpp
                    {
                        // two pixels per byte (high nibble = left pixel)
                        for (int x = 0; x < width; x++)
                        {
                            int byteIndex = x / 2;
                            byte packed = row[byteIndex];
                            byte idx = (x % 2 == 0)
                                ? (byte)((packed >> 4) & 0x0F)
                                : (byte)(packed & 0x0F);

                            indices[destY, x] = idx;
                        }
                    }
                }

                return indices;
            }

            /// <summary>
            /// Write indices[y,x] + ARGB palette[256] as an 8bpp BMP.
            /// </summary>
            public static void Write8bppBmpFromIndices(
                string path,
                byte[,] indices,
                uint[] palette,
                uint[]? newPalette = null,
                int colorCount = 16)
            {
                if (indices == null) throw new ArgumentNullException(nameof(indices));
                if (palette == null) throw new ArgumentNullException(nameof(palette));

                if (newPalette != null)
                    AmigaPalette.QuantizeBmp(ref indices, ref palette, newPalette);
                else
                    AmigaPalette.QuantizeBmp(ref indices, ref palette, colorCount);

                int height = indices.GetLength(0);
                int width = indices.GetLength(1);

                const int bitsPerPixel = 8;
                int rowSize = ((width * bitsPerPixel + 31) / 32) * 4; // 8bpp rows, DWORD-aligned
                int imageSize = rowSize * height;
                int pixelOffset = FileHeaderSize + InfoHeaderSize + PaletteBytes;
                int fileSize = pixelOffset + imageSize;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var bw = new BinaryWriter(fs);

                // BITMAPFILEHEADER
                bw.Write(BmpMagic);              // bfType
                bw.Write(fileSize);              // bfSize
                bw.Write((ushort)0);             // bfReserved1
                bw.Write((ushort)0);             // bfReserved2
                bw.Write(pixelOffset);           // bfOffBits

                // BITMAPINFOHEADER
                bw.Write(InfoHeaderSize);        // biSize
                bw.Write(width);                 // biWidth
                bw.Write(height);                // biHeight (positive = bottom-up)
                bw.Write((ushort)1);             // biPlanes
                bw.Write((ushort)8);             // biBitCount
                bw.Write(0u);                    // biCompression = BI_RGB
                bw.Write(imageSize);             // biSizeImage
                bw.Write(0);                     // biXPelsPerMeter
                bw.Write(0);                     // biYPelsPerMeter
                bw.Write((uint)PaletteEntries);  // biClrUsed
                bw.Write(0u);                    // biClrImportant

                // Palette (BGRA)
                for (int i = 0; i < PaletteEntries; i++)
                {
                    uint argb = i < palette.Length ? palette[i] : 0;
                    byte r = (byte)((argb >> 16) & 0xFF);
                    byte g = (byte)((argb >> 8) & 0xFF);
                    byte b = (byte)(argb & 0xFF);

                    bw.Write(b);
                    bw.Write(g);
                    bw.Write(r);
                    bw.Write((byte)0); // reserved
                }

                // Pixels (bottom-up)
                byte[] rowBuf = new byte[rowSize];

                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                        rowBuf[x] = indices[y, x];

                    // zero padding
                    for (int p = width; p < rowSize; p++)
                        rowBuf[p] = 0;

                    bw.Write(rowBuf);
                }
            }
        }

        /// <summary>
        /// Load an indexed BMP and return its index buffer [y,x] and palette.
        /// </summary>
        private static byte[,] LoadIndexedImageIndices(
            string path,
            out uint[] palette,
            out int width,
            out int height)
        {
            return Bmp8Helper.Read8bppBmpToIndices(path, out palette, out width, out height);
        }

        // ------------------------------------------------------------------
        // Helpers for MAP <-> sprites+mapping (variable bitplanes)
        // ------------------------------------------------------------------

        private static void ReadPitchMap(
            string mapFilePath,
            int bitplanes,
            out Mapping[,] mapping,
            out List<PitchBitmapItem> sprites)
        {
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            byte[] data = AmigaRncHelper.ReadAllBytes(mapFilePath);

            if (data.Length < MapHeaderSize)
                throw new InvalidOperationException($"MAP file too small. Length={data.Length}, expected at least {MapHeaderSize}.");

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            // 1) Header = mapping
            mapping = new Mapping[PitchTilesY, PitchTilesX];
            for (int y = 0; y < PitchTilesY; y++)
            {
                for (int x = 0; x < PitchTilesX; x++)
                {
                    mapping[y, x].B1 = br.ReadByte();
                    mapping[y, x].B2 = br.ReadByte();
                    mapping[y, x].B3 = br.ReadByte();
                    mapping[y, x].B4 = br.ReadByte();
                }
            }

            long bytesRead = ms.Position;
            int remaining = data.Length - (int)bytesRead;
            if (remaining < 0)
                throw new InvalidOperationException("MAP parsing error: negative remaining bytes.");

            int spriteRowBytes = BytesPerPlanePerRow16 * bitplanes; // 2 * bitplanes
            int spriteSizeBytes = SpriteRows * spriteRowBytes;      // 16 rows

            if (remaining % spriteSizeBytes != 0)
            {
                // The bit depth is not stored in the file, so a wrong -bitplanes is the
                // usual cause. Say which depths the data would actually fit.
                var fits = new List<int>();
                for (int planes = 1; planes <= 8; planes++)
                {
                    if (remaining % (SpriteRows * BytesPerPlanePerRow16 * planes) == 0)
                        fits.Add(planes);
                }

                string hint = fits.Count > 0
                    ? $" The sprite data divides evenly at -bitplanes={string.Join(" or ", fits)}."
                    : string.Empty;

                throw new InvalidOperationException(
                    $"MAP sprite data size ({remaining} bytes) is not a multiple of spriteSizeBytes={spriteSizeBytes} " +
                    $"for {bitplanes} bitplanes.{hint}");
            }

            int spriteCount = remaining / spriteSizeBytes;
            sprites = new List<PitchBitmapItem>(spriteCount);

            var rowBytes = new byte[spriteRowBytes];
            var rowPixels = new byte[TileSize];

            for (int sIndex = 0; sIndex < spriteCount; sIndex++)
            {
                var tile = new PitchBitmapItem();

                for (int row = 0; row < SpriteRows; row++)
                {
                    int read = br.Read(rowBytes, 0, spriteRowBytes);
                    if (read != spriteRowBytes)
                        throw new EndOfStreamException("Unexpected end of sprite data in MAP.");

                    DecodeAmigaRow16(rowBytes, bitplanes, rowPixels);

                    for (int col = 0; col < TileSize; col++)
                        tile.Bits[row, col] = rowPixels[col];
                }

                sprites.Add(tile);
            }
        }

        private static bool SameTile(PitchBitmapItem a, PitchBitmapItem b)
        {
            for (int j = 0; j < TileSize; j++)
                for (int i = 0; i < TileSize; i++)
                    if (a.Bits[j, i] != b.Bits[j, i])
                        return false;
            return true;
        }

        private static void BuildSpritesAndMapping(
            PitchBitmapItem[,] tileMatrix,
            int bitplanes,
            int maxTiles,
            out List<PitchBitmapItem> sprites,
            out int[,] tileIndices,
            out Mapping[,] mapping)
        {
            int rows = tileMatrix.GetLength(0);
            int cols = tileMatrix.GetLength(1);

            sprites = new List<PitchBitmapItem>();
            tileIndices = new int[rows, cols];

            // Deduplicate tiles into sprite list
            for (int ty = 0; ty < rows; ty++)
            {
                for (int tx = 0; tx < cols; tx++)
                {
                    var tile = tileMatrix[ty, tx];
                    int foundIndex = -1;

                    for (int i = 0; i < sprites.Count; i++)
                    {
                        if (SameTile(tile, sprites[i]))
                        {
                            foundIndex = i;
                            break;
                        }
                    }

                    if (foundIndex == -1)
                    {
                        foundIndex = sprites.Count;
                        sprites.Add(tile);
                    }

                    tileIndices[ty, tx] = foundIndex;
                }
            }

            int spriteCount = sprites.Count;
            if (maxTiles > 0 && spriteCount > maxTiles)
            {
                Console.WriteLine($"WARNING: Tile count {spriteCount} exceeds limit {maxTiles}.");
            }

            int tileHeight = 16;                   // 16x16 pitch tiles
            int bytesPerTile = tileHeight * bitplanes * 2;  // 32*bitplanes

            mapping = new Mapping[rows, cols];
            for (int ty = 0; ty < rows; ty++)
            {
                for (int tx = 0; tx < cols; tx++)
                {
                    int tileIndex = tileIndices[ty, tx];

                    // If 0 means "no tile", leave it as 0 so the loader keeps it null.
                    uint offset = (tileIndex == 0) ? 0u : (uint)(tileIndex * bytesPerTile);

                    mapping[ty, tx].B1 = (byte)(offset >> 24);
                    mapping[ty, tx].B2 = (byte)(offset >> 16);
                    mapping[ty, tx].B3 = (byte)(offset >> 8);
                    mapping[ty, tx].B4 = (byte)(offset);
                }
            }
        }

        private static void WritePitchMap(
            string outputMapPath,
            Mapping[,] mapping,
            List<PitchBitmapItem> sprites,
            int bitplanes)
        {
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            int rows = mapping.GetLength(0);
            int cols = mapping.GetLength(1);

            // mapping header
            for (int ty = 0; ty < rows; ty++)
            {
                for (int tx = 0; tx < cols; tx++)
                {
                    var m = mapping[ty, tx];
                    bw.Write(m.B1);
                    bw.Write(m.B2);
                    bw.Write(m.B3);
                    bw.Write(m.B4);
                }
            }

            int spriteRowBytes = BytesPerPlanePerRow16 * bitplanes;
            var rowPixels = new byte[TileSize];
            var rowBytes = new byte[spriteRowBytes];

            // sprites: 16 rows * (bitplanes * 2 bytes) each
            for (int k = 0; k < sprites.Count; k++)
            {
                var tile = sprites[k];

                for (int row = 0; row < TileSize; row++)
                {
                    for (int col = 0; col < TileSize; col++)
                        rowPixels[col] = tile.Bits[row, col];

                    EncodeAmigaRow16(rowPixels, bitplanes, rowBytes);
                    bw.Write(rowBytes);
                }
            }

            bw.Flush();
            byte[] mapBytes = ms.ToArray();

            // RNC-aware write (uses global setting inside AmigaRncHelper)
            AmigaRncHelper.WriteAllBytes(outputMapPath, mapBytes);
        }

        // ------------------------------------------------------------------
        // 1. PITCH MAP → FULL PITCH BMP (MAP-only, variable bitplanes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuild a full 42x55-tile pitch bitmap from a SWOS pitch MAP file.
        ///
        /// Tiles are read directly from the MAP's planar sprite data:
        /// - Header: 42x55x4 bytes (mapping)
        /// - Sprites: N * 16 * (bitplanes * 2) bytes (16x16 pixels each)
        ///
        /// overridePalette:
        ///   - if null, a default SWOS palette is used (SwosPalettes.Base).
        ///   - otherwise, the given palette is used for the output BMP.
        ///
        /// bitplanes:
        ///   - number of bitplanes used in the MAP sprite data (1-8).
        /// </summary>
        public void ConvertPitchMapToBmp(
            string mapFilePath,
            uint[]? overridePalette,
            string outputBmpPath,
            uint[]? newPalette = null,
            int bitplanes = 4,
            int colorCount = 16)
        {
            // 1) Read mapping + sprites from MAP (RNC-aware, variable bitplanes)
            ReadPitchMap(mapFilePath, bitplanes, out var mapping, out var sprites);

            // 2) Palette: override or default
            uint[] palette = overridePalette ?? (bitplanes > 4 ? DosPalette.Game : AmigaPalette.PaletteFromAmiga12(AmigaPalette.Game));

            // 3) Build full pitch index buffer
            var fullIndices = new byte[PitchHeight, PitchWidth];
            int offsetStride = DetectPitchOffsetStride(mapping, bitplanes, sprites.Count);

            for (int y = 0; y < PitchTilesY; y++)
            {
                for (int x = 0; x < PitchTilesX; x++)
                {
                    int tileIndex = DecodePitchTileIndex(mapping[y, x], offsetStride);

                    if ((uint)tileIndex >= (uint)sprites.Count)
                        throw new InvalidOperationException(
                            $"Mapping refers to tileIndex {tileIndex} but sprites.Count={sprites.Count}.");

                    var tile = sprites[tileIndex];

                    int dstX0 = x * TileSize;
                    int dstY0 = y * TileSize;

                    for (int j = 0; j < TileSize; j++)
                    {
                        for (int i = 0; i < TileSize; i++)
                        {
                            fullIndices[dstY0 + j, dstX0 + i] = tile.Bits[j, i];
                        }
                    }
                }
            }

            // 4) Write indexed BMP from indices + palette
            Bmp8Helper.Write8bppBmpFromIndices(outputBmpPath, fullIndices, palette, newPalette, colorCount);
        }

        // ------------------------------------------------------------------
        // 2. PITCH MAP → Tiled JSON + tiles BMP (MAP-only, variable bitplanes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Convert a SWOS pitch MAP file into:
        ///   - a tileset BMP (MyPitch.bmp, same directory as the map)
        ///   - a Tiled JSON map with one layer that references those tiles.
        ///
        /// Tiles are laid out in a tileset image with 16 columns of 16x16 tiles.
        /// Tile IDs in Tiled are 1-based and directly correspond to sprite indices + 1.
        ///
        /// bitplanes:
        ///   - number of bitplanes used in the MAP sprite data (1-8).
        /// </summary>
        public void ConvertPitchMapToTiled(
            string mapFilePath,
            uint[] palette,
            string dstMapPath,
            uint[]? newPalette = null,
            int bitplanes = 4,
            int colorCount = 16)
        {
            // 1) Read mapping + sprites from MAP
            ReadPitchMap(mapFilePath, bitplanes, out var mapping, out var sprites);

            // 2) Build tileset image (sprites -> grid 16x16 tiles)
            int tileCount = sprites.Count;
            int destCols = TileSheetColumns;
            int destRows = (tileCount + destCols - 1) / destCols;

            int imgWidth = destCols * TileSize;
            int imgHeight = destRows * TileSize;

            var tilesetIndices = new byte[imgHeight, imgWidth];

            for (int sIndex = 0; sIndex < tileCount; sIndex++)
            {
                int col = sIndex % destCols;
                int row = sIndex / destCols;

                var tile = sprites[sIndex];
                int dstX0 = col * TileSize;
                int dstY0 = row * TileSize;

                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        tilesetIndices[dstY0 + y, dstX0 + x] = tile.Bits[y, x];
                    }
                }
            }

            string mapFullPath = Path.GetFullPath(dstMapPath);
            string mapDir = Path.GetDirectoryName(mapFullPath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(mapFullPath);
            string tilesBmpPath = Path.Combine(mapDir, baseName + ".bmp");

            // 3) Write tiles BMP
            Bmp8Helper.Write8bppBmpFromIndices(tilesBmpPath, tilesetIndices, palette, newPalette, colorCount);

            // 4) Write the Tiled JSON map
            var gids = new int[PitchTilesY * PitchTilesX];
            int offsetStride = DetectPitchOffsetStride(mapping, bitplanes, tileCount);

            for (int y = 0; y < PitchTilesY; y++)
            {
                for (int x = 0; x < PitchTilesX; x++)
                {
                    int tileIndex = DecodePitchTileIndex(mapping[y, x], offsetStride);

                    if ((uint)tileIndex >= (uint)tileCount)
                        throw new InvalidOperationException(
                            $"Mapping refers to tileIndex {tileIndex} but the MAP holds {tileCount} tiles.");

                    gids[y * PitchTilesX + x] = tileIndex + 1; // Tiled uses 1-based IDs; 0 is "empty"
                }
            }

            TiledMap.Write(mapFullPath, new TiledMap.MapData
            {
                Width = PitchTilesX,
                Height = PitchTilesY,
                TileWidth = TileSize,
                TileHeight = TileSize,
                TileCount = tileCount,
                Columns = destCols,
                ImageSource = Path.GetFileName(tilesBmpPath),
                ImageWidth = imgWidth,
                ImageHeight = imgHeight,
                Gids = gids
            });
        }

        // ------------------------------------------------------------------
        // 3. Tiled map + tiles BMP → PITCH MAP (variable bitplanes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Convert a Tiled map (plus its associated tiles BMP) back into a SWOS MAP
        /// file. JSON maps are what SwosGfx writes; the older XML .tmx is also read.
        ///
        /// Requirements:
        /// - a single embedded tileset with a single image.
        /// - tilewidth/tileheight are 16x16.
        /// - map width/height is 42x55.
        /// - Tile IDs (gid) are >0 (no empty tiles) and reference a tileset image
        ///   that contains all tile graphics.
        ///
        /// bitplanes:
        ///   - number of bitplanes to use when encoding the MAP (1-8).
        ///     4 = original ECS/OCS 16-color; 8 = full 256-color AGA.
        /// </summary>
        public void ConvertTiledToPitchMap(
            string tiledMapPath,
            string outputMapPath,
            int maxTiles = 284,
            int bitplanes = 4)
        {
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            string fullMapPath = Path.GetFullPath(tiledMapPath);
            TiledMap.MapData tiled = TiledMap.Read(fullMapPath);

            if (tiled.TileWidth != TileSize || tiled.TileHeight != TileSize)
                throw new InvalidOperationException($"Tiled map tile size must be {TileSize}x{TileSize}.");
            if (tiled.Width != PitchTilesX || tiled.Height != PitchTilesY)
                throw new InvalidOperationException($"Tiled map dimensions must be {PitchTilesX}x{PitchTilesY}.");

            int tileCount = tiled.TileCount;
            int columns = tiled.Columns;
            int imageWidth = tiled.ImageWidth;
            int imageHeight = tiled.ImageHeight;

            string mapDir = Path.GetDirectoryName(fullMapPath) ?? ".";
            string tilesBmpPath = Path.Combine(mapDir, tiled.ImageSource);

            int[,] gids = new int[tiled.Height, tiled.Width];
            for (int i = 0; i < tiled.Gids.Length; i++)
                gids[i / tiled.Width, i % tiled.Width] = tiled.Gids[i];

            // 1) Read tileset image
            uint[] tsPal;
            byte[,] tsIndices = LoadIndexedImageIndices(
                tilesBmpPath,
                out tsPal,
                out int tsWidth,
                out int tsheight);

            if (tsWidth != imageWidth || tsheight != imageHeight)
                throw new InvalidOperationException("Tiled map: tileset image dimensions do not match the map's image size.");

            int tileSheetCols = columns;
            int tileSheetRows = imageHeight / TileSize;

            // 2) Build tile library: one PitchBitmapItem per tile in tileset
            var tileLibrary = new PitchBitmapItem[tileCount];

            for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
            {
                int col = tileIndex % tileSheetCols;
                int row = tileIndex / tileSheetCols;
                if (row >= tileSheetRows)
                    throw new InvalidOperationException("Tiled map: tileset image is too small for the declared tilecount/columns.");

                var tile = new PitchBitmapItem();
                int srcX0 = col * TileSize;
                int srcY0 = row * TileSize;

                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        tile.Bits[y, x] = tsIndices[srcY0 + y, srcX0 + x];
                    }
                }

                tileLibrary[tileIndex] = tile;
            }

            // 3) Build tile matrix for the pitch (using tileLibrary + gids)
            var tileMatrix = new PitchBitmapItem[PitchTilesY, PitchTilesX];

            for (int ty = 0; ty < PitchTilesY; ty++)
            {
                for (int tx = 0; tx < PitchTilesX; tx++)
                {
                    int gid = gids[ty, tx];
                    if (gid <= 0)
                        throw new InvalidOperationException("Tiled map: tile GID 0 (empty) encountered; SWOS MAP requires all tiles present.");

                    int tileIndex = gid - 1; // 1-based gid → 0-based
                    if (tileIndex < 0 || tileIndex >= tileCount)
                        throw new InvalidOperationException($"Tiled map: tile GID {gid} references out-of-range tile index {tileIndex}.");

                    tileMatrix[ty, tx] = tileLibrary[tileIndex];
                }
            }

            // 4) Deduplicate tiles + build mapping, then write MAP
            BuildSpritesAndMapping(tileMatrix, bitplanes, maxTiles, out var sprites, out var tileIndices, out var mapping);
            WritePitchMap(outputMapPath, mapping, sprites, bitplanes);
        }

        // ------------------------------------------------------------------
        // 4. FULL PITCH BMP → PITCH MAP RAW (variable bitplanes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Convert a full pitch indexed BMP (42x55 tiles = 672x880) to a SWOS MAP file.
        /// This builds both the mapping and the planar sprite data.
        ///
        /// fullPitchBmpPath MUST be an indexed BMP whose indices are already correct.
        /// Palette is read but not used for the planar encoding.
        ///
        /// bitplanes:
        ///   - number of bitplanes to use in the MAP sprite data (1-8).
        /// </summary>
        public void ConvertFullPitchBmpToMap(
            string fullPitchBmpPath,
            string outputMapPath,
            int maxTiles = 284,
            int bitplanes = 4)
        {
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            // 1) Load full pitch indices (ignore image palette; trust indices)
            uint[] imgPal;
            byte[,] indexBuffer = LoadIndexedImageIndices(
                fullPitchBmpPath,
                out imgPal,
                out int imgWidth,
                out int imgHeight);

            // 2) Slice into tile matrix (55 x 42 tiles of 16x16)
            var tileMatrix = new PitchBitmapItem[PitchTilesY, PitchTilesX];

            for (int ty = 0; ty < PitchTilesY; ty++)
            {
                for (int tx = 0; tx < PitchTilesX; tx++)
                {
                    var tile = new PitchBitmapItem();
                    for (int j = 0; j < TileSize; j++)
                    {
                        for (int i = 0; i < TileSize; i++)
                        {
                            tile.Bits[j, i] = indexBuffer[ty * TileSize + j, tx * TileSize + i];
                        }
                    }
                    tileMatrix[ty, tx] = tile;
                }
            }

            // 3) Deduplicate tiles and build mapping
            BuildSpritesAndMapping(tileMatrix, bitplanes, maxTiles, out var sprites, out var tileIndices, out var mapping);

            // 4) Write MAP with the requested bitplane depth
            WritePitchMap(outputMapPath, mapping, sprites, bitplanes);
        }

        // ------------------------------------------------------------------
        // 5. GENERIC RAW ↔ BMP (variable bitplanes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Convert an indexed BMP into planar Amiga RAW (1-8 bitplanes).
        /// Equivalent to DC_MakeAmigaRaw / DC_MakeAmigaRaw_New, generalized.
        ///
        /// The BMP MUST be indexed; palette[] is not used for extraction
        /// (indices are taken directly) but is kept for API symmetry.
        /// </summary>
        /// <summary>
        /// Pass <paramref name="bitplanes"/> = 0 to auto-select the depth from the
        /// image's highest palette index: 4 bitplanes if it only uses indices 0-15,
        /// otherwise 8. This avoids storing empty upper planes for 16-colour art.
        /// </summary>
        public void ConvertBmpToRaw(
            string inputBmpPath,
            uint[] palette,
            string outputRawPath,
            int bitplanes = 4)
        {
            bool autoBitplanes = bitplanes <= 0;

            if (!autoBitplanes && (bitplanes < 1 || bitplanes > 8))
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            uint[] imgPal;
            byte[,] buffer = LoadIndexedImageIndices(
                inputBmpPath,
                out imgPal,
                out int imgWidth,
                out int imgHeight);

            if (autoBitplanes)
            {
                int highest = 0;
                int rows = buffer.GetLength(0), cols = buffer.GetLength(1);
                for (int yy = 0; yy < rows; yy++)
                    for (int xx = 0; xx < cols; xx++)
                        if (buffer[yy, xx] > highest) highest = buffer[yy, xx];

                bitplanes = highest <= 15 ? 4 : 8;
                Console.WriteLine($"  {Path.GetFileName(outputRawPath)}: max colour index {highest} -> {bitplanes} bitplanes");
            }

            int bytesPerPlanePerRow = imgWidth / 8;
            int maxIndex = (1 << bitplanes) - 1;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            for (int y = 0; y < imgHeight; y++)
            {
                for (int plane = 0; plane < bitplanes; plane++)
                {
                    for (int block = 0; block < bytesPerPlanePerRow; block++)
                    {
                        byte value = 0;

                        for (int bit = 0; bit < 8; bit++)
                        {
                            int x = block * 8 + bit;     // pixel x in [0..width-1]
                            int pix = buffer[y, x];      // full index (0..255)

                            if (pix > maxIndex)
                                throw new InvalidOperationException(
                                    $"Pixel index {pix} exceeds maximum {maxIndex} for {bitplanes} bitplanes.");

                            int bitVal = (pix >> plane) & 1; // plane 0 = LSB

                            value = (byte)((value << 1) | bitVal);
                        }

                        bw.Write(value);
                    }
                }
            }

            bw.Flush();
            byte[] rawBytes = ms.ToArray();

            // RNC-aware write
            AmigaRncHelper.WriteAllBytes(outputRawPath, rawBytes);
        }

        /// <summary>
        /// Convert planar Amiga RAW (1-8 bitplanes) back to an indexed BMP using the given palette.
        /// This is the inverse of ConvertBmpToRaw.
        ///
        /// RAW is read via AmigaRncHelper, so RNC-packed RAW is supported transparently.
        /// </summary>
        public void ConvertRawToBmp(
            string inputRawPath,
            uint[] palette,
            string outputBmpPath,
            uint[]? newPalette = null,
            int bitplanes = 4,
            int colorCount = 16)
        {
            if (bitplanes < 1 || bitplanes > 8)
                throw new ArgumentOutOfRangeException(nameof(bitplanes), "Bitplanes must be between 1 and 8.");

            byte[] raw = AmigaRncHelper.ReadAllBytes(inputRawPath);

            (int width, int height) = DetectRawDimensions(raw.Length, bitplanes);

            int bytesPerPlanePerRow = width / 8;
            int bytesPerRow = bytesPerPlanePerRow * bitplanes;
            int expectedLength = bytesPerRow * height;

            if (raw.Length < expectedLength)
                throw new InvalidOperationException(
                    $"RAW file too small. Expected at least {expectedLength} bytes, got {raw.Length}.");

            var buffer = new byte[height, width];

            // Decode bitplanes -> index buffer
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * bytesPerRow;

                for (int x = 0; x < width; x++)
                {
                    int pixelIndex = 0;

                    int byteIndexInPlane = x / 8;
                    int bitIndexInByte = 7 - (x % 8); // MSB = first pixel

                    for (int plane = 0; plane < bitplanes; plane++)
                    {
                        int planeBase = rowBase + plane * bytesPerPlanePerRow;
                        byte b = raw[planeBase + byteIndexInPlane];
                        int bit = (b >> bitIndexInByte) & 1;

                        pixelIndex |= (bit << plane); // plane0=LSB
                    }

                    buffer[y, x] = (byte)pixelIndex;
                }
            }

            Bmp8Helper.Write8bppBmpFromIndices(outputBmpPath, buffer, palette, newPalette, colorCount);
        }

        static (int width, int height) DetectRawDimensions(int byteCount, int bitplanes)
        {
            if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
            if (bitplanes <= 0) throw new ArgumentOutOfRangeException(nameof(bitplanes));

            // Supported raw formats
            Span<(int w, int h)> candidates = stackalloc (int w, int h)[]
            {
                (320, 256),
                (352, 272),
            };

            (int w, int h)? match = null;

            foreach (var (w, h) in candidates)
            {
                if ((w & 7) != 0)
                    continue; // sanity: must be divisible by 8 for planar bytes-per-row

                long bytesPerPlanePerRow = w / 8;
                long bytesPerRow = bytesPerPlanePerRow * bitplanes;
                long expectedLength = bytesPerRow * h;

                if (expectedLength == byteCount)
                {
                    if (match != null)
                        throw new InvalidOperationException(
                            $"Ambiguous raw size: {byteCount} bytes matches multiple candidate dimensions for {bitplanes} bitplanes.");

                    match = (w, h);
                }
            }

            if (match != null)
                return match.Value;

            // Build a useful error message (shows what would have matched)
            long exp320 = ((320L / 8) * bitplanes) * 256L;
            long exp352 = ((352L / 8) * bitplanes) * 272L;

            throw new InvalidOperationException(
                $"Unknown raw format: {byteCount} bytes with {bitplanes} bitplanes. " +
                $"Expected {exp320} bytes for 320x256, or {exp352} bytes for 352x272.");
        }

        /// <summary>Big-endian 32-bit tile reference stored in one mapping cell.</summary>
        static uint ReadPitchMappingOffset(Mapping m)
        {
            return ((uint)m.B1 << 24) |
                   ((uint)m.B2 << 16) |
                   ((uint)m.B3 << 8) |
                   (uint)m.B4;
        }

        /// <summary>
        /// Work out what unit a pitch MAP's mapping offsets are counted in.
        ///
        /// Each cell holds a 32-bit value identifying its tile. WritePitchMap emits a
        /// true byte offset into the tile stream - tileIndex * 16 * bitplanes * 2 - but
        /// AGA pitches also exist (including ones earlier SwosGfx builds produced) whose
        /// offsets keep the original OCS scaling of a fixed 128 bytes per tile, the
        /// 4-bitplane tile size, whatever the real depth is. At 4 bitplanes the two are
        /// the same number, which is why it went unnoticed.
        ///
        /// Both are unambiguous once you check them against the file: a stride only fits
        /// if every offset is a multiple of it AND lands on a tile that exists. So try
        /// the byte stride first and fall back to the legacy one.
        /// </summary>
        static int DetectPitchOffsetStride(Mapping[,] mapping, int bitplanes, int spriteCount)
        {
            int byteStride = TileSize * bitplanes * 2;   // what WritePitchMap emits
            const int LegacyStride = TileSize * 4 * 2;   // 128 - fixed 4-bitplane tile size

            if (PitchOffsetsFitStride(mapping, byteStride, spriteCount))
                return byteStride;

            if (LegacyStride != byteStride && PitchOffsetsFitStride(mapping, LegacyStride, spriteCount))
                return LegacyStride;

            // Neither fits; return the expected one so the per-cell decode below reports
            // the offending offset rather than silently picking something wrong.
            return byteStride;
        }

        static bool PitchOffsetsFitStride(Mapping[,] mapping, int stride, int spriteCount)
        {
            if (stride <= 0)
                return false;

            int rows = mapping.GetLength(0);
            int cols = mapping.GetLength(1);

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    uint offset = ReadPitchMappingOffset(mapping[y, x]);

                    if ((offset % (uint)stride) != 0)
                        return false;

                    if (offset / (uint)stride >= (uint)spriteCount)
                        return false;
                }
            }

            return true;
        }

        static int DecodePitchTileIndex(Mapping m, int offsetStride)
        {
            uint offset = ReadPitchMappingOffset(m);

            if (offsetStride <= 0)
                throw new ArgumentOutOfRangeException(nameof(offsetStride));

            if ((offset % (uint)offsetStride) != 0)
                throw new InvalidOperationException(
                    $"Bad pitch mapping offset 0x{offset:X8} (not divisible by the tile stride of {offsetStride} bytes).");

            return (int)(offset / (uint)offsetStride);
        }

    }
}
