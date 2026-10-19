using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwosGfx
{
    /// <summary>
    /// Jaguar -> Amiga bank reconstruction.
    ///
    /// The Atari Jaguar release (Sensible Soccer International Edition, 1995) ships
    /// its in-game artwork as nine LZ-packed "*.lzj" blobs, each unpacking to
    /// exactly 81920 bytes = one 320x256 8bpp sheet. Six of those sheets are the
    /// SWOS/Sensible gameplay sprite banks, laid out at the SAME pixel coordinates
    /// the Amiga image-command scripts read from - the Jaguar port reused the Amiga
    /// bank layout and simply redrew the art at 256 colours.
    ///
    /// So this class is DosToAmiga with a different pixel source: it walks the same
    /// image command streams (AmigaScripts.MenuColorTextScript) and, for every draw
    /// opcode, copies the rectangle straight out of the matching Jaguar sheet. Any
    /// placement the Jaguar sheet does not hold falls back to the DOS sprite, so the
    /// result is always a complete bank.
    ///
    /// Bank mapping (verified by comparing every script placement's pixel mask
    /// against the original Amiga banks - see BankTable below):
    ///
    ///     Amiga bank   Jaguar stream   placements matched
    ///     CJCGRAFS     $894 7C0        26 / 64   (scoreboard font + clock text)
    ///     CJCTEAM1     $8AC 828       285 / 303
    ///     CJCTEAM2     $8AF B58       285 / 303
    ///     CJCTEAM3     $8B2 FE0       285 / 303
    ///     CJCTEAMG     $8A0 2E8       232 / 232  (goalkeepers - exact)
    ///     CJCBITS      $899 3D8        23 / 30
    ///     CJCBENCH     $89D 0B0        51 / 61
    ///     SOCCER_S     -                        (no Jaguar equivalent)
    ///     CHARSET / MENUS / MENUS2 -            (Jaguar has its own menu system)
    ///
    /// The celebration frames are the one gap: Amiga sprites 251-256, 352-357 and
    /// 453-458 (three team-slot ranges sharing six 12x15 cells at (144,64) to
    /// (176,80)) are blank in all three Jaguar team sheets - the Jaguar game has no
    /// goal celebration. They are supplied by hand-drawn Jaguar-style art in
    /// Jag/&lt;bank&gt;_CELEB.bmp, stamped onto the sheet before the script walk so it
    /// flows through the normal placement path. Without those files the cells fall
    /// back to the DOS sprites, as placeholders.
    ///
    /// Palette: the Jaguar keeps a 256-entry CRY palette per screen and copies it
    /// into the hardware CLUT at $F00400. The gameplay one lives at file offset
    /// $6FA42. Banks are written with that palette; DOS fallback sprites are
    /// remapped from the DOS Game palette into the nearest Jaguar colours, so each
    /// bank stays a single 256-colour image.
    /// </summary>
    public static class JagToAmiga
    {
        // All atlas banks are 320x256, same as the Amiga originals.
        private const int BankWidth = 320;
        private const int BankHeight = 256;

        // DOS sprite index = Amiga index + 114 for gameplay sprites (>= 227).
        private const int DosToAmigaOffset = 114;

        // First Amiga sprite index of the team1-slot player block.
        private const int TeamBlockBaseIndex = 227;
        private const int TeamSpriteCount = 303;

        // Load-opcode word offsets inside MenuColorTextScript (see DosToAmiga).
        private const int LoadCjcgrafs = 0;
        private const int LoadTeam1Slot = 405;
        private const int LoadCjcteamg = 3418;
        private const int LoadCjcbits = 4547;
        private const int LoadSoccerS = 4694;
        private const int LoadCjcbench = 4889;

        // Cartridge addresses (mapped at $800000) of the packed gameplay sheets.
        private const int StreamCjcgrafs = 0x8947C0;
        private const int StreamCjcbits = 0x8993D8;
        private const int StreamCjcbench = 0x89D0B0;
        private const int StreamCjcteamg = 0x8A02E8;
        private const int StreamCjcteam1 = 0x8AC828;
        private const int StreamCjcteam2 = 0x8AFB58;
        private const int StreamCjcteam3 = 0x8B2FE0;

        // File offset of the 256-entry in-game CRY palette ($1A8E2 in DRAM).
        private const int GamePaletteOffset = 0x6FA42;

        // Jag/<bank>_CELEB.bmp: the block of bank pixels holding the six celebration
        // cells - a 3x2 grid of 16x16 cells at (144,64), the sprites themselves being
        // 12x15 (the last 11x15) in the top-left corner of each cell. The whole block
        // is blank in every Jaguar team sheet, so it is stamped over wholesale.
        private const string CelebrationSuffix = "_CELEB.bmp";

        // Jag/<bank>_PATCH.bmp and Jag/<bank>_PATCH_<x>_<y>.bmp: hand-drawn art laid
        // over the Jaguar sheet. See ApplyBankPatch.
        private const string BankPatchSuffix = "_PATCH.bmp";
        private const string BankPatchPrefix = "_PATCH_";
        private const int CelebrationX = 144;
        private const int CelebrationY = 64;
        private const int CelebrationWidth = 48;
        private const int CelebrationHeight = 32;

        /// <summary>
        /// Which Amiga sprite indices a Jaguar sheet actually holds at the Amiga
        /// coordinates. Anything outside these rules is taken from the DOS sprites.
        /// </summary>
        private sealed class JagBank
        {
            public string Name = "";
            public int LoadOffset;
            public int StreamOffset;

            /// <summary>If set, ONLY these inclusive Amiga sprite ranges come from the Jaguar sheet.</summary>
            public (int Lo, int Hi)[]? Only;

            /// <summary>Inclusive Amiga sprite ranges the Jaguar sheet uses for something else.</summary>
            public (int Lo, int Hi)[]? Except;

            public bool UsesJaguar(int amigaIndex)
            {
                if (Only != null)
                    return Only.Any(r => amigaIndex >= r.Lo && amigaIndex <= r.Hi);

                if (Except != null && Except.Any(r => amigaIndex >= r.Lo && amigaIndex <= r.Hi))
                    return false;

                return true;
            }
        }

        // Derived by scoring every script placement's non-zero pixel mask in the
        // Jaguar sheet against the same rectangle in the original Amiga bank. The
        // exclusions below are the places where the Jaguar artists reused free bank
        // space for something else entirely.
        private static readonly JagBank[] BankTable =
        {
            new JagBank
            {
                Name = "CJCGRAFS", LoadOffset = LoadCjcgrafs, StreamOffset = StreamCjcgrafs,
                // Only the scoreboard font, the two cards and the "N mins." captions
                // survive; the rest of the sheet is a Jaguar-only alphabet, chevron
                // strip and kit icons sitting on top of the SWOS advertising boards.
                // (137 scores well on shape - both cells are solid - but the Jaguar
                // one is a slice of its "HALF-TIME" banner, not the blue block.)
                Only = new[] { (139, 140), (173, 192), (195, 195), (214, 216) }
            },
            new JagBank { Name = "CJCTEAM1", LoadOffset = LoadTeam1Slot, StreamOffset = StreamCjcteam1 },
            new JagBank { Name = "CJCTEAM2", LoadOffset = LoadTeam1Slot, StreamOffset = StreamCjcteam2 },
            new JagBank { Name = "CJCTEAM3", LoadOffset = LoadTeam1Slot, StreamOffset = StreamCjcteam3 },
            new JagBank { Name = "CJCTEAMG", LoadOffset = LoadCjcteamg, StreamOffset = StreamCjcteamg },
            new JagBank
            {
                Name = "CJCBITS", LoadOffset = LoadCjcbits, StreamOffset = StreamCjcbits,
                // 1093 is blank on the Amiga; the Jaguar sheet has unrelated pixels there.
                // 1090 the Jaguar has not got: 13 pixels on the Amiga, a 3-pixel green
                // smudge in the Jaguar sheet.
                Except = new[] { (1090, 1090), (1093, 1093) }
            },
            new JagBank
            {
                Name = "CJCBENCH", LoadOffset = LoadCjcbench, StreamOffset = StreamCjcbench,
                // 1171-1173 are blank on the Amiga; the Jaguar sheet parks two tall
                // advertising posts across those cells.
                //
                // 1188-1195: the Jaguar release has only ONE manager. The Amiga has two
                // - COACH1 in a brown coat at 1182-1187, COACH2 in a white shirt at
                // 1189-1194 - and the Jaguar repeats its brown one across COACH2's six
                // cells and into the blanks either side. The bench code picks the coach
                // by team (`teamNumber != 1` adds 7 to the sprite, bench_subs.c), so the
                // top bench drew the brown manager and the white-shirted one was missing
                // from the tier entirely. Taken from the DOS sprites until someone
                // redraws them: -jag-template writes the cells out to paint over.
                Except = new[] { (1171, 1173), (1188, 1195) }
            },
        };

        // Banks with no Jaguar source at all: rebuilt from the DOS graphics exactly
        // as -dos2amiga does, so the output folder is a complete drop-in set.
        private static readonly (string Name, int LoadOffset)[] DosOnlyGameplayBanks =
        {
            ("SOCCER_S", LoadSoccerS),
        };

        /// <summary>
        /// Rebuild the gameplay banks from the Jaguar cartridge in
        /// <paramref name="romPath"/>, using the DOS graphics in
        /// <paramref name="dosDir"/> for everything the Jaguar release does not
        /// have, and write them as 8bpp BMPs into <paramref name="outDir"/>.
        /// <paramref name="jagDir"/> holds the hand-drawn &lt;bank&gt;_CELEB.bmp patches.
        /// </summary>
        /// <paramref name="template"/> additionally writes, per bank, the DOS
        /// artwork for every placement in the Jaguar palette
        /// (&lt;bank&gt;_DOS.bmp), the subset of it the Jaguar sheet could not
        /// supply (&lt;bank&gt;_TODO.bmp, everything else left blank) and the
        /// placement list both were built from (&lt;bank&gt;_placements.csv).
        /// That TODO sheet is the redraw list: an artist repaints those cells in
        /// the Jaguar palette and they come back in as &lt;bank&gt;_CELEB.bmp-style
        /// patches. <paramref name="dumpSheets"/> writes the raw unpacked Jaguar
        /// sheet before any placement walk.
        public static int ConvertBanks(string dosDir, string romPath, string jagDir, string outDir,
                                       bool template = false, bool dumpSheets = false)
        {
            if (dosDir == null) throw new ArgumentNullException(nameof(dosDir));
            if (romPath == null) throw new ArgumentNullException(nameof(romPath));
            if (jagDir == null) throw new ArgumentNullException(nameof(jagDir));
            if (outDir == null) throw new ArgumentNullException(nameof(outDir));

            Directory.CreateDirectory(outDir);

            var rom = JagRom.Load(romPath);
            uint[] jagPalette = rom.ReadCryPalette(GamePaletteOffset, 256);

            var gameplay = DosToAmiga.Interpret(AmigaScripts.MenuColorTextScript);
            var intro = DosToAmiga.Interpret(AmigaScripts.IntroScript);
            var menu = DosToAmiga.Interpret(AmigaScripts.MenuBgScript);

            // Global DOS sprite model (SCORE/GOAL1/BENCH/CHARSET ranges).
            var model = DosSprite.Load(dosDir);

            // The three player-kit files, indexed by ordinal (shared layout).
            var teamDat = new Dictionary<string, DosSprite.Sprite[]>
            {
                ["CJCTEAM1"] = DosSprite.LoadSpritesFromDat(Path.Combine(dosDir, "TEAM1.DAT"), TeamSpriteCount),
                ["CJCTEAM2"] = DosSprite.LoadSpritesFromDat(Path.Combine(dosDir, "TEAM2.DAT"), TeamSpriteCount),
                ["CJCTEAM3"] = DosSprite.LoadSpritesFromDat(Path.Combine(dosDir, "TEAM3.DAT"), TeamSpriteCount),
            };

            // DOS fallback sprites arrive in the DOS Game palette; fold them onto the
            // nearest Jaguar colours so each bank stays a single 256-colour image.
            byte[] dosToJag = BuildRemap(DosPalette.Game, jagPalette);

            bool wantSheets = dumpSheets ||
                              Environment.GetEnvironmentVariable("SWOSGFX_DUMP_JAG_SHEETS") != null;
            bool wantTemplate = template ||
                                Environment.GetEnvironmentVariable("SWOSGFX_JAG_TEMPLATE") != null;

            int banks = 0;

            foreach (var bank in BankTable)
            {
                if (!gameplay.TryGetValue(bank.LoadOffset, out var places))
                {
                    Console.Error.WriteLine($"Warning: bank '{bank.Name}' has no load block at offset {bank.LoadOffset}.");
                    continue;
                }

                byte[] sheet = rom.UnpackSheet(bank.StreamOffset, BankWidth * BankHeight);
                teamDat.TryGetValue(bank.Name, out var team);

                // -jag-dump-sheets: the RAW Jaguar sheet, before any placement walk or
                // DOS fallback. This is the only way to see what the Jaguar release
                // actually holds and where -- which is what you need when a placement
                // comes out wrong and you have to tell "the Jaguar has not got it" from
                // "the Jaguar has it somewhere else".
                if (wantSheets)
                {
                    string rawPath = System.IO.Path.Combine(outDir, bank.Name + "_JAGSHEET.bmp");
                    DosToAmiga.WriteBankBmp(rawPath, sheet, jagPalette);
                    Console.WriteLine($"  raw Jaguar sheet -> {rawPath}");
                }

                // Fill in the celebration cells the Jaguar release never had, so the
                // placement loop below reads them like any other Jaguar sprite.
                bool patched = ApplyCelebrationPatch(sheet, jagDir, bank.Name, jagPalette);
                bool[]? patchMask = ApplyBankPatch(sheet, jagDir, bank.Name, jagPalette);

                var buf = new byte[BankWidth * BankHeight];
                var dosBuf = wantTemplate ? new byte[BankWidth * BankHeight] : null;
                var todoBuf = wantTemplate ? new byte[BankWidth * BankHeight] : null;
                var csv = wantTemplate ? new System.Text.StringBuilder("index,source,x,y,w,h\n") : null;
                int fromJag = 0, fromDos = 0, empty = 0;

                foreach (var p in places)
                {
                    DosSprite.Sprite? spr = team != null
                        ? SpriteForTeamSlot(team, p.AmigaIndex)
                        : SpriteForGlobalSlot(model, p.AmigaIndex);

                    byte[]? px = null;
                    int sw = 0, sh = 0;

                    if (spr != null)
                    {
                        px = DosSprite.DecodeSpriteChunky(spr, out sw, out sh);
                        if (sw <= 0 || sh <= 0) px = null;
                    }

                    // The sprite table is shared across platforms, so the DOS sprite's
                    // own size is the window to lift out of the Jaguar sheet. Without a
                    // DOS sprite, fall back to the script's extract window.
                    int w = px != null ? Math.Min(sw, p.WinW) : p.WinW;
                    int h = px != null ? Math.Min(sh, p.WinH) : p.WinH;

                    // A cell the patch paints is taken from the sheet whatever the bank
                    // table says - that is the whole point of painting it.
                    bool tookJag = (bank.UsesJaguar(p.AmigaIndex) ||
                                    PatchCovers(patchMask, p.X0, p.Y0, w, h)) &&
                                   CopyFromSheet(buf, sheet, p.X0, p.Y0, w, h);
                    if (tookJag) fromJag++;

                    // The DOS template is drawn for EVERY placement, so it is the whole
                    // original bank in the Jaguar palette; the TODO sheet is only the
                    // cells the Jaguar sheet did not supply.
                    if (px != null && dosBuf != null)
                        BlitRemapped(dosBuf, px, sw, sh, p, dosToJag);

                    if (!tookJag)
                    {
                        if (px == null) empty++;
                        else
                        {
                            BlitRemapped(buf, px, sw, sh, p, dosToJag);
                            if (todoBuf != null) BlitRemapped(todoBuf, px, sw, sh, p, dosToJag);
                            fromDos++;
                        }
                    }

                    csv?.Append($"{p.AmigaIndex},{(tookJag ? "jag" : px == null ? "blank" : "dos")}," +
                                $"{p.X0},{p.Y0},{w},{h}\n");
                }

                DosToAmiga.WriteBankBmp(Path.Combine(outDir, bank.Name + ".bmp"), buf, jagPalette);

                if (wantTemplate)
                {
                    DosToAmiga.WriteBankBmp(Path.Combine(outDir, bank.Name + "_DOS.bmp"), dosBuf!, jagPalette);
                    DosToAmiga.WriteBankBmp(Path.Combine(outDir, bank.Name + "_TODO.bmp"), todoBuf!, jagPalette);
                    File.WriteAllText(Path.Combine(outDir, bank.Name + "_placements.csv"), csv!.ToString());
                }
                banks++;

                Console.WriteLine($"  {bank.Name,-8} {fromJag,3} Jaguar / {fromDos,3} DOS" +
                                  (empty > 0 ? $" / {empty} blank" : "") +
                                  (patched ? "   (+ celebration frames)" : ""));
            }

            // Banks with no Jaguar counterpart - straight DOS reconstruction, so the
            // output folder is a complete drop-in replacement for Amiga_Images_256.
            foreach (var (name, loadOffset) in DosOnlyGameplayBanks)
                banks += DosToAmiga.RenderGlobalBank(gameplay, loadOffset, model, outDir, name,
                                                     rsOffset: name == "SOCCER_S",
                                                     DosToAmiga.MapMode.GameplayPlus114, DosPalette.Game);

            banks += DosToAmiga.RenderGlobalBank(intro, 0, model, outDir, "CHARSET", rsOffset: false, DosToAmiga.MapMode.MenuIdentity, DosPalette.Menu);
            banks += DosToAmiga.RenderGlobalBank(menu, 0, model, outDir, "MENUS", rsOffset: false, DosToAmiga.MapMode.MenuIdentity, DosPalette.Menu);
            banks += DosToAmiga.RenderGlobalBank(menu, 392, model, outDir, "MENUS2", rsOffset: false, DosToAmiga.MapMode.MenuIdentity, DosPalette.Menu);

            // Keep the Jaguar CLUT next to the banks - the gameplay banks are in it,
            // so whatever consumes them later needs it too.
            AmigaPalette.PaletteToPalette(outDir, "JAG_GAME", jagPalette, PaletteFormat.Act);

            Console.WriteLine($"Jaguar -> Amiga: wrote {banks} banks to {outDir}");

            return 0;
        }

        // ------------------------------------------------------------------
        // Pixel sources
        // ------------------------------------------------------------------

        private static DosSprite.Sprite? SpriteForGlobalSlot(DosSprite model, int amigaIndex)
        {
            int dosIdx = amigaIndex + DosToAmigaOffset;

            return (dosIdx >= 0 && dosIdx < DosSprite.NumSprites) ? model[dosIdx] : null;
        }

        private static DosSprite.Sprite? SpriteForTeamSlot(DosSprite.Sprite[] team, int amigaIndex)
        {
            int ordinal = amigaIndex - TeamBlockBaseIndex;

            return (ordinal >= 0 && ordinal < team.Length) ? team[ordinal] : null;
        }

        /// <summary>
        /// Copy a sprite rectangle out of a Jaguar sheet. Returns false (and leaves the
        /// bank untouched) when the sheet has nothing there - the Jaguar release is
        /// missing that sprite, so the caller falls back to the DOS graphics.
        /// </summary>
        private static bool CopyFromSheet(byte[] buf, byte[] sheet, int x0, int y0, int w, int h)
        {
            int written = 0;

            for (int y = 0; y < h; y++)
            {
                int by = y0 + y;
                if (by < 0 || by >= BankHeight) continue;

                int srcRow = by * BankWidth;
                int dstRow = by * BankWidth;

                for (int x = 0; x < w; x++)
                {
                    int bx = x0 + x;
                    if (bx < 0 || bx >= BankWidth) continue;

                    byte v = sheet[srcRow + bx];
                    if (v == 0) continue;          // index 0 = transparent, same as SWOS

                    buf[dstRow + bx] = v;
                    written++;
                }
            }

            return written > 0;
        }

        /// <summary>DosToAmiga.Blit, with the DOS palette indices folded onto the Jaguar palette.</summary>
        private static void BlitRemapped(byte[] buf, byte[] px, int sw, int sh, DosToAmiga.Placement p, byte[] remap)
        {
            int limW = Math.Min(sw, p.WinW);
            int limH = Math.Min(sh, p.WinH);

            for (int y = 0; y < limH; y++)
            {
                int by = p.Y0 + y;
                if (by < 0 || by >= BankHeight) continue;

                int srcRow = y * sw;
                int dstRow = by * BankWidth;

                for (int x = 0; x < limW; x++)
                {
                    byte v = px[srcRow + x];
                    if (v == 0) continue;          // index 0 = transparent

                    int bx = p.X0 + x;
                    if (bx < 0 || bx >= BankWidth) continue;

                    buf[dstRow + bx] = remap[v];
                }
            }
        }

        /// <summary>
        /// Stamp Jag/&lt;bank&gt;_CELEB.bmp over the blank celebration cells of a Jaguar
        /// team sheet. The art is expected in the Jaguar palette - it is drawn against
        /// JAG_GAME.act - but a file in some other palette is folded onto the nearest
        /// Jaguar colours rather than rejected. Returns false when there is no patch
        /// for this bank, leaving the cells blank so the DOS sprites fill them.
        /// </summary>
        /// <summary>
        /// Hand-drawn art laid over the unpacked Jaguar sheet before the placement
        /// walk, from Jag/&lt;bank&gt;_PATCH.bmp (a whole 320x256 bank) and/or
        /// Jag/&lt;bank&gt;_PATCH_&lt;x&gt;_&lt;y&gt;.bmp (any size, pasted at that bank
        /// position - the coordinates are in the name so a crop carries its own
        /// placement and no table in here has to know about it).
        ///
        /// They differ in what colour 0 means, and it matters. In the whole-bank
        /// file it is transparent, or the file would have to redraw the bank. A
        /// positioned crop REPLACES its rectangle outright, zeros included: it is
        /// the art there now, and the sprite it is covering is usually a different
        /// shape, so anything left showing through is the old one's silhouette.
        /// (Measured: painting only the non-zero pixels of the white-shirted
        /// manager left 11-38 pixels of the Jaguar's brown one around each of the
        /// six frames.)
        ///
        /// Cells a patch paints are taken from the sheet even where the bank table
        /// says the Jaguar has not got them. This is how art the Jaguar release
        /// never had is put in: run with -jag-template, open &lt;bank&gt;_TODO.bmp -
        /// the DOS drawing of exactly those cells, already in the Jaguar palette -
        /// repaint it, and save the whole sheet or just the crops.
        /// </summary>
        /// <returns>The coverage mask, or null when nothing was painted.</returns>
        private static bool[]? ApplyBankPatch(byte[] sheet, string jagDir, string bankName, uint[] jagPalette)
        {
            if (!Directory.Exists(jagDir))
                return null;

            var parts = new List<(string Path, int X, int Y)>();

            string whole = Path.Combine(jagDir, bankName + BankPatchSuffix);
            if (File.Exists(whole))
                parts.Add((whole, 0, 0));

            // <bank>_PATCH_<x>_<y>.bmp, in a stable order so two overlapping crops
            // always resolve the same way.
            foreach (string path in Directory.GetFiles(jagDir, bankName + BankPatchPrefix + "*.bmp")
                                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string tail = Path.GetFileNameWithoutExtension(path)
                                  .Substring(bankName.Length + BankPatchPrefix.Length);
                string[] bits = tail.Split('_');

                if (bits.Length != 2 ||
                    !int.TryParse(bits[0], out int px) || !int.TryParse(bits[1], out int py))
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(path)}': a positioned patch is named " +
                        $"{bankName}{BankPatchPrefix}<x>_<y>.bmp, e.g. {bankName}{BankPatchPrefix}16_24.bmp.");

                parts.Add((path, px, py));
            }

            if (parts.Count == 0)
                return null;

            var mask = new bool[BankWidth * BankHeight];
            int painted = 0;

            foreach (var (path, ox, oy) in parts)
            {
                byte[,] indices = AmigaTools.Bmp8Helper.Read8bppBmpToIndices(path, out uint[] palette, out int w, out int h);

                if (ox == 0 && oy == 0 && parts.Count == 1 && (w != BankWidth || h != BankHeight) &&
                    string.Equals(path, whole, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(path)}' is {w}x{h}, expected a whole {BankWidth}x{BankHeight} bank. " +
                        $"A crop is named {bankName}{BankPatchPrefix}<x>_<y>.bmp instead.");

                if (ox < 0 || oy < 0 || ox + w > BankWidth || oy + h > BankHeight)
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(path)}' is {w}x{h} at ({ox},{oy}), which runs outside the " +
                        $"{BankWidth}x{BankHeight} bank.");

                byte[]? remap = SamePalette(palette, jagPalette) ? null : BuildRemap(palette, jagPalette);

                if (remap != null)
                    Console.WriteLine($"  {bankName,-8} '{Path.GetFileName(path)}' is not in the Jaguar palette - remapping.");

                bool replace = !string.Equals(path, whole, StringComparison.OrdinalIgnoreCase);
                int here = 0;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte v = indices[y, x];
                        if (v == 0 && !replace) continue;

                        int o = (oy + y) * BankWidth + ox + x;
                        sheet[o] = v != 0 && remap != null ? remap[v] : v;
                        mask[o] = true;
                        if (v != 0) here++;
                    }
                }

                Console.WriteLine($"  {bankName,-8} '{Path.GetFileName(path)}' " +
                                  (replace ? $"replaced {w}x{h} at ({ox},{oy}), {here} pixels drawn."
                                           : $"painted {here} pixels."));
                painted += replace ? w * h : here;
            }

            return painted > 0 ? mask : null;
        }

        /// <summary>Does the patch paint anything inside this placement's window?</summary>
        private static bool PatchCovers(bool[]? mask, int x0, int y0, int w, int h)
        {
            if (mask == null) return false;

            for (int y = Math.Max(0, y0); y < y0 + h && y < BankHeight; y++)
                for (int x = Math.Max(0, x0); x < x0 + w && x < BankWidth; x++)
                    if (mask[y * BankWidth + x]) return true;

            return false;
        }

        private static bool ApplyCelebrationPatch(byte[] sheet, string jagDir, string bankName, uint[] jagPalette)
        {
            string path = Path.Combine(jagDir, bankName + CelebrationSuffix);

            if (!File.Exists(path))
                return false;

            byte[,] indices = AmigaTools.Bmp8Helper.Read8bppBmpToIndices(path, out uint[] palette, out int w, out int h);

            if (w != CelebrationWidth || h != CelebrationHeight)
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' is {w}x{h}, expected {CelebrationWidth}x{CelebrationHeight} " +
                    $"(the bank block at ({CelebrationX},{CelebrationY})).");

            // Identity map when the file already carries the Jaguar palette, which is
            // the normal case; only a mismatched palette costs a nearest-colour search.
            byte[]? remap = SamePalette(palette, jagPalette) ? null : BuildRemap(palette, jagPalette);

            if (remap != null)
                Console.WriteLine($"  {bankName,-8} '{Path.GetFileName(path)}' is not in the Jaguar palette - remapping.");

            for (int y = 0; y < h; y++)
            {
                int by = CelebrationY + y;
                if (by < 0 || by >= BankHeight) continue;

                for (int x = 0; x < w; x++)
                {
                    int bx = CelebrationX + x;
                    if (bx < 0 || bx >= BankWidth) continue;

                    byte v = indices[y, x];
                    sheet[by * BankWidth + bx] = remap != null ? remap[v] : v;
                }
            }

            return true;
        }

        private static bool SamePalette(uint[] a, uint[] b)
        {
            int n = Math.Min(a.Length, b.Length);

            for (int i = 0; i < n; i++)
            {
                // Compare RGB only - the alpha byte is not meaningful here.
                if ((a[i] & 0x00FFFFFF) != (b[i] & 0x00FFFFFF))
                    return false;
            }

            return true;
        }

        /// <summary>Nearest-colour map from one 256-entry ARGB palette onto another.</summary>
        private static byte[] BuildRemap(uint[] from, uint[] to)
        {
            var map = new byte[256];

            for (int i = 1; i < 256; i++)
            {
                uint argb = i < from.Length ? from[i] : 0u;
                AmigaPalette.GetNearestColor(argb, to, out int nearest);
                map[i] = (byte)nearest;
            }

            map[0] = 0; // transparent stays transparent
            return map;
        }
    }

    /// <summary>
    /// Atari Jaguar cartridge image: address mapping, the "lzj" depacker and the
    /// CRY palette reader. A C# port of the parts of jagfx (jagfx/compression.py,
    /// jagfx/cry.py, jagfx/rom.py) that JagToAmiga needs, so SwosGfx can read a
    /// cartridge without any external tooling.
    /// </summary>
    internal sealed class JagRom
    {
        /// <summary>Cartridge images are mapped at $800000, so CPU addresses resolve through this.</summary>
        public const int CartridgeBase = 0x800000;

        private readonly byte[] _data;

        private JagRom(byte[] data) => _data = data;

        public static JagRom Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Jaguar ROM not found: {path}", path);

            return new JagRom(File.ReadAllBytes(path));
        }

        /// <summary>Resolve a CPU address (or a plain file offset) to a file offset.</summary>
        public int ToFileOffset(int address)
        {
            int offset = address >= CartridgeBase ? address - CartridgeBase : address;

            if (offset < 0 || offset >= _data.Length)
                throw new ArgumentOutOfRangeException(nameof(address), $"Address ${address:X6} is outside the {_data.Length} byte ROM.");

            return offset;
        }

        /// <summary>
        /// Unpack one of the game's packed graphics sheets and check it came out at
        /// the expected size (320x256 8bpp = 81920 bytes for every gameplay bank).
        /// </summary>
        public byte[] UnpackSheet(int address, int expectedSize)
        {
            byte[] sheet = LzBits(_data, ToFileOffset(address), expectedSize);

            if (sheet.Length != expectedSize)
                throw new InvalidDataException(
                    $"Jaguar stream at ${address:X6} unpacked to {sheet.Length} bytes, expected {expectedSize}.");

            return sheet;
        }

        /// <summary>
        /// Read <paramref name="count"/> 16-bit CRY entries into an ARGB palette.
        /// A CRY word is chroma in the high byte (indexing the hue tables below) and
        /// intensity in the low byte, which scales it.
        /// </summary>
        public uint[] ReadCryPalette(int fileOffset, int count)
        {
            var palette = new uint[count];

            for (int i = 0; i < count; i++)
            {
                int o = fileOffset + i * 2;
                if (o + 1 >= _data.Length)
                    throw new InvalidDataException($"CLUT at ${fileOffset:X} runs past the end of the ROM.");

                int chroma = _data[o];
                int intensity = _data[o + 1];

                byte r = (byte)(CryR[chroma] * intensity / 255);
                byte g = (byte)(CryG[chroma] * intensity / 255);
                byte b = (byte)(CryB[chroma] * intensity / 255);

                palette[i] = AmigaPalette.PackArgb(0xFF, r, g, b);
            }

            return palette;
        }

        // ------------------------------------------------------------------
        // "lz-bits" - the depacker Sensible's .lzj files use
        // ------------------------------------------------------------------

        /// <summary>
        /// Bit-oriented LZSS with an 8 KB ring buffer, MSB first:
        ///
        ///     1 &lt;8 bits&gt;                 emit that literal byte
        ///     0 &lt;13-bit offset&gt; &lt;4 bits&gt; copy (len + 3) bytes from ring[offset]
        ///     0 &lt;13 zero bits&gt;           end of stream
        ///
        /// Everything emitted is mirrored into the ring as it is produced and the
        /// offset is an ABSOLUTE index into the ring, not a back-distance - reading it
        /// as a distance consumes the bitstream identically and still terminates in
        /// the right place, it just copies the wrong bytes. The write position starts
        /// at 1.
        /// </summary>
        private static byte[] LzBits(byte[] src, int start, int limit)
        {
            const int Window = 8192;
            const int WindowMask = Window - 1;
            const int OffsetBits = 13;
            const int LengthBits = 4;
            const int MinMatch = 3;

            var ring = new byte[Window];
            var output = new byte[limit];

            int outLen = 0;
            int wpos = 1;

            long bitPos = (long)start * 8;
            long bitEnd = (long)src.Length * 8;

            int ReadBits(int count)
            {
                int value = 0;

                for (int i = 0; i < count; i++)
                {
                    int byteIndex = (int)(bitPos >> 3);
                    int bit = (src[byteIndex] >> (7 - (int)(bitPos & 7))) & 1;
                    value = (value << 1) | bit;
                    bitPos++;
                }

                return value;
            }

            while (outLen < limit && bitPos + 1 + 8 <= bitEnd)
            {
                if (ReadBits(1) == 1)
                {
                    byte literal = (byte)ReadBits(8);

                    output[outLen++] = literal;
                    ring[wpos] = literal;
                    wpos = (wpos + 1) & WindowMask;
                    continue;
                }

                if (bitPos + OffsetBits + LengthBits > bitEnd)
                    break;

                int offset = ReadBits(OffsetBits);
                int length = ReadBits(LengthBits) + MinMatch;

                if (offset == 0)
                    break;                                  // end of stream

                for (int i = 0; i < length && outLen < limit; i++)
                {
                    byte c = ring[offset];

                    output[outLen++] = c;
                    ring[wpos] = c;

                    wpos = (wpos + 1) & WindowMask;
                    offset = (offset + 1) & WindowMask;
                }
            }

            if (outLen == limit)
                return output;

            var trimmed = new byte[outLen];
            Buffer.BlockCopy(output, 0, trimmed, 0, outLen);
            return trimmed;
        }

        // ------------------------------------------------------------------
        // CRY chroma tables (the standard Atari tables, as published in the
        // Jaguar developer docs and used verbatim by JagView and jagfx).
        // ------------------------------------------------------------------

        private static readonly byte[] CryR =
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x13, 0x00,
            0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x40, 0x2B, 0x15, 0x00,
            0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x5F, 0x47, 0x2F, 0x17, 0x00,
            0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x68, 0x4E, 0x34, 0x1A, 0x00,
            0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xAA, 0x8D, 0x71, 0x55, 0x38, 0x1C, 0x00,
            0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0x99, 0x7A, 0x5B, 0x3D, 0x1E, 0x00,
            0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xE6, 0xC5, 0xA4, 0x83, 0x62, 0x41, 0x20, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF7, 0xD6, 0xB5, 0x94, 0x73, 0x52, 0x31, 0x11,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEB, 0xCC, 0xAD, 0x8F, 0x70, 0x51, 0x33,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xE3, 0xC6, 0xAA, 0x8D, 0x71, 0x55,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF9, 0xDF, 0xC5, 0xAB, 0x91, 0x77,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF8, 0xE0, 0xC8, 0xB1, 0x99,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFC, 0xE6, 0xD0, 0xBB,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF0, 0xDD,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        };

        private static readonly byte[] CryG =
        {
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
            0x00, 0x13, 0x26, 0x39, 0x4D, 0x60, 0x73, 0x86, 0x9A, 0xAD, 0xC0, 0xD3, 0xE7, 0xFA, 0xFF, 0xFF,
            0x00, 0x15, 0x2B, 0x40, 0x56, 0x6B, 0x81, 0x96, 0xAC, 0xC1, 0xD7, 0xEC, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x17, 0x2F, 0x47, 0x5F, 0x77, 0x8E, 0xA6, 0xBE, 0xD6, 0xEE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1A, 0x34, 0x4E, 0x68, 0x82, 0x9C, 0xB6, 0xD0, 0xEA, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1C, 0x38, 0x55, 0x71, 0x8D, 0xAA, 0xC6, 0xE2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1E, 0x3D, 0x5B, 0x85, 0x99, 0xB7, 0xD6, 0xF4, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x20, 0x41, 0x62, 0x83, 0xA4, 0xC5, 0xE6, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x20, 0x41, 0x62, 0x83, 0xA4, 0xC5, 0xE6, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1E, 0x3D, 0x5B, 0x85, 0x99, 0xB7, 0xD6, 0xF4, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1C, 0x38, 0x55, 0x71, 0x8D, 0xAA, 0xC6, 0xE2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x1A, 0x34, 0x4E, 0x68, 0x82, 0x9C, 0xB6, 0xD0, 0xEA, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x17, 0x2F, 0x47, 0x5F, 0x77, 0x8E, 0xA6, 0xBE, 0xD6, 0xEE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x15, 0x2B, 0x40, 0x56, 0x6B, 0x81, 0x96, 0xAC, 0xC1, 0xD7, 0xEC, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x13, 0x26, 0x39, 0x4D, 0x60, 0x73, 0x86, 0x9A, 0xAD, 0xC0, 0xD3, 0xE7, 0xFA, 0xFF, 0xFF,
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        };

        private static readonly byte[] CryB =
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF0, 0xDD,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFC, 0xE6, 0xD0, 0xBB,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF8, 0xE0, 0xC8, 0xB1, 0x99,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF9, 0xDF, 0xC5, 0xAB, 0x91, 0x77,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xE3, 0xC6, 0xAA, 0x8D, 0x71, 0x55,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEB, 0xCC, 0xAD, 0x8F, 0x70, 0x51, 0x33,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF7, 0xD6, 0xB5, 0x94, 0x73, 0x52, 0x31, 0x11,
            0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xED, 0xE6, 0xC5, 0xA4, 0x83, 0x62, 0x41, 0x20, 0x00,
            0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0xCB, 0x99, 0x7A, 0x5B, 0x3D, 0x1E, 0x00,
            0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xA9, 0xAA, 0x8D, 0x71, 0x55, 0x38, 0x1C, 0x00,
            0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x87, 0x68, 0x4E, 0x34, 0x1A, 0x00,
            0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x5F, 0x47, 0x2F, 0x17, 0x00,
            0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x44, 0x40, 0x2B, 0x15, 0x00,
            0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x13, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

    }
}
