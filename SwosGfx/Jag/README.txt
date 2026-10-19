Jaguar assets for SwosGfx -jag2amiga.

1. The cartridge image
----------------------
Put it here:

    Sensible Soccer International Edition (1995).jag      (2,097,152 bytes)

!Convert_Jag_to_Amiga.bat picks up the first *.jag / *.j64 / *.rom / *.bin it
finds in this folder. To use a ROM that lives somewhere else instead, pass its
path to the batch file:

    !Convert_Jag_to_Amiga.bat "D:\roms\Sensible Soccer International Edition (1995).jag"

or call SwosGfx directly:

    SwosGfx -dos -jag2amiga -jag-rom=<path> Amiga_Images_256_Jag

The ROM is not distributed with SwosGfx.

2. CJCTEAM1_CELEB.bmp / CJCTEAM2_CELEB.bmp / CJCTEAM3_CELEB.bmp
---------------------------------------------------------------
Hand-drawn goal celebration frames, one file per kit bank. The Jaguar release
has no celebration, so those six cells are blank in every Jaguar team sheet;
-jag2amiga stamps these over them before it reads the bank.

Each file is a 48x32 8bpp BMP: the block of bank pixels at (144,64), i.e. a 3x2
grid of 16x16 cells holding the six 12x15 sprites (the last one is 11x15). It
must use the Jaguar palette - JAG_GAME.act, written next to the converted banks.
A file in some other palette still works; its colours are matched to the nearest
Jaguar ones on the way in.

Those six cells are shared by Amiga sprites 251-256, 352-357 and 453-458, so one
set of six frames covers all three sprite ranges.

If a _CELEB.bmp is missing, that bank falls back to the DOS celebration sprites.

3. <bank>_PATCH.bmp / <bank>_PATCH_<x>_<y>.bmp
-----------------------------------------------
Art laid over the unpacked Jaguar sheet before -jag2amiga walks it, for sprites
the cartridge has not got - or has somewhere else, which is worse, because then
the walk lifts a real sprite out of the wrong slot.

  <bank>_PATCH.bmp            a whole 320x256 bank. Colour 0 is transparent.
  <bank>_PATCH_<x>_<y>.bmp    a crop of any size, pasted at that bank position.
                              It REPLACES its rectangle, zeros included.

The difference in what colour 0 means is deliberate. A whole-bank file would
otherwise have to redraw the bank to say "nothing here". A crop is the art at
that spot, and the sprite it covers is usually a different shape, so anything
showing through would be the old one's silhouette - which is exactly what
happened with the manager below until the crop was made to replace instead.

Cells a patch covers are then read from the sheet even where JagToAmiga's bank
table says the Jaguar has not got them. Use the Jaguar palette (JAG_GAME.act);
another palette is matched to the nearest Jaguar colours on the way in.

To find out what needs painting, run with -jag-template. Per bank it writes
<bank>_TODO.bmp - the DOS drawing of exactly the cells the Jaguar cannot supply,
already in the Jaguar palette - plus <bank>_DOS.bmp (the whole bank that way) and
<bank>_placements.csv (every cell's sprite index, source and rectangle).

CJCBENCH_PATCH_16_24.bmp / CJCBENCH_PATCH_256_24.bmp
  The white-shirted bench manager. The Amiga has two managers - COACH1 in a
  brown coat at sprites 1182-1187, COACH2 in a white shirt at 1189-1194 - and
  the Jaguar drew only the brown one, repeating it across COACH2's cells. SWOS
  picks the coach by team, so the top bench drew the wrong man. These two 48x24
  crops are the Amiga's own COACH2 art with its shadow (Amiga colour 8) moved
  onto the Jaguar's shadow index; the rest was already the first 16 colours on
  both releases. Sitting trio at (16,24), standing trio at (256,24).

CJCBENCH_PATCH_272_120.bmp
  The SELECT SUBSTITUTE arrow, sprite 1170. The Jaguar draws it pointing left;
  SWOS puts it at the top of the bench pointing right, at the substitutes. This
  9x8 crop is the Jaguar's own arrow mirrored, with its drop shadow recast
  down-right (shaded 62/61/60, as the Jaguar shades it) rather than mirrored.

Whatever you paint, stay off Amiga colours 10, 11, 14 and 15: those are the kit
slots and they follow the team. The manager sprites avoid them on purpose.
