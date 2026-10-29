#!/usr/bin/env python3
"""
Darkwing Duck Unity Port - GDD Sprite & Tilemap Exporter
Extracts player sprite CHR tiles and renders complete animation frame strips
into Art/Docs/GameDesignDocument/sprites/ for documentation and art references.
"""

import argparse
import hashlib
import json
import sys
from pathlib import Path
from PIL import Image

# 2C02 NES RGB color palette (matching NesPalette.cs / Mesen default)
NES_PALETTE = [
    (0x66,0x66,0x66),(0x00,0x2A,0x88),(0x14,0x12,0xA7),(0x3B,0x00,0xA4),(0x5C,0x00,0x7E),(0x6E,0x00,0x40),(0x6C,0x06,0x00),(0x56,0x1D,0x00),
    (0x33,0x35,0x00),(0x0B,0x48,0x00),(0x00,0x52,0x00),(0x00,0x4F,0x08),(0x00,0x40,0x4D),(0x00,0x00,0x00),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0xAD,0xAD,0xAD),(0x15,0x5F,0xD9),(0x42,0x40,0xFF),(0x75,0x27,0xFE),(0xA0,0x1A,0xCC),(0xB7,0x1E,0x7B),(0xB5,0x31,0x20),(0x99,0x4E,0x00),
    (0x6B,0x6D,0x00),(0x38,0x87,0x00),(0x0C,0x93,0x00),(0x00,0x8F,0x32),(0x00,0x7C,0x8D),(0x00,0x00,0x00),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0xFF,0xFE,0xFF),(0x64,0xB0,0xFF),(0x92,0x90,0xFF),(0xC6,0x76,0xFF),(0xF3,0x6A,0xFF),(0xFE,0x6E,0xCC),(0xFE,0x81,0x70),(0xEA,0x9E,0x22),
    (0xBC,0xBE,0x00),(0x88,0xD8,0x00),(0x5C,0xE4,0x30),(0x45,0xE0,0x82),(0x48,0xCD,0xDE),(0x4F,0x4F,0x4F),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0xFF,0xFE,0xFF),(0xC0,0xDF,0xFF),(0xD3,0xD2,0xFF),(0xE8,0xC8,0xFF),(0xFB,0xC2,0xFF),(0xFE,0xC4,0xEA),(0xFE,0xCC,0xC5),(0xF7,0xD8,0xA5),
    (0xE4,0xE5,0x94),(0xCF,0xEF,0x96),(0xBD,0xF4,0xAB),(0xB3,0xF3,0xCC),(0xB5,0xEB,0xF2),(0xB8,0xB8,0xB8),(0x00,0x00,0x00),(0x00,0x00,0x00),
]

# Player sprite sub-palettes in NES VRAM ($3F10-$3F1F)
SPRITE_PALS = [
    [None, NES_PALETTE[0x0F], NES_PALETTE[0x30], NES_PALETTE[0x27]], # Pal 0: Black, White, Orange
    [None, NES_PALETTE[0x0F], NES_PALETTE[0x27], NES_PALETTE[0x13]], # Pal 1: Black, Orange, Purple
    [None, NES_PALETTE[0x0F], NES_PALETTE[0x27], NES_PALETTE[0x15]], # Pal 2: Black, Orange, Magenta
    [None, NES_PALETTE[0x0F], NES_PALETTE[0x37], NES_PALETTE[0x07]], # Pal 3: Black, Yellow, Brown
]

CELL_SIZE = 48
ORIGIN_X = 24
ORIGIN_Y = 24
CHR_BANK = 14  # $8E & $1F; 4 KiB CHR bank, not bank 25.

ROOT_DIR = Path(__file__).resolve().parent.parent.parent
CHRS_PATH = ROOT_DIR / "Research" / "DarkwingDuck" / "extracted" / "chr" / "tiles_index2.bin"
OUTPUT_DIR = Path(__file__).resolve().parent / "sprites"
sys.path.insert(0, str(ROOT_DIR / "Tools" / "DarkwingResearch"))
from extract_rom import EXPECTED_SHA256, decode_tile

# The same animation ID addresses different entries in the two ROM engines.
# $FA29's special hits occur in sections $08/$0C, whose start cells are $76/$B5
# (bank-4 walk via $CA8F/$CAC5). CHR and palettes come from those descriptors.
SPECIAL_SECTIONS = {0x1C: 0x0C, 0x1D: 0x08}

# Target animations list with their designated file names and descriptions
ANIMATIONS_TO_EXPORT = [
    (0x01, "anim_01_idle.png", "Idle / Stand (Базовая стойка)"),
    (0x03, "anim_03_walk.png", "Walk Cycle (Бег / Ходьба)"),
    (0x04, "anim_04_look_up_deflect.png", "Look Up & Cape Deflect (Взгляд вверх и плащ)"),
    (0x05, "anim_05_stand_fire.png", "Stand Fire (Стрельба стоя)"),
    (0x06, "anim_06_crouch.png", "Crouch / Duck (Приседание)"),
    (0x07, "anim_07_crouch_fire.png", "Crouch Fire (Стрельба из приседа)"),
    (0x08, "anim_08_stage_drop.png", "Stage Intro Drop (Десант на уровень)"),
    (0x09, "anim_09_hurt.png", "Hurt / Knockback (Урон на земле)"),
    (0x0B, "anim_0b_air_stun.png", "Air Stun / Bonk (Оглушение в воздухе)"),
    (0x0C, "anim_0c_hook_hang.png", "Hook Hang Idle (Висение на крюке)"),
    (0x0D, "anim_0d_shoot_up.png", "Shoot Up / Hang Shoot (Стрельба вверх)"),
    (0x0E, "anim_0e_jump_fall.png", "Jump Fall / Apex (Падение в прыжке)"),
    (0x0F, "anim_0f_jump_rise.png", "Jump Rise (Взлёт в прыжке)"),
    (0x11, "anim_11_air_steer.png", "Air Steer / Arc (Воздушное руление)"),
    (0x12, "anim_12_hook_reach.png", "Hook Grab / Reach (Зацеп за крюк)"),
    (0x1C, "anim_1c_hazard_shock.png", "Special Hurt ($6D=$0C, hazard anim $5D)"),
    (0x1D, "anim_1d_hazard_slime.png", "Special Hurt ($6D=$08, hazard anim $4D)"),
    (0x2B, "anim_2b_victory_card.png", "Mission Intro Card (Начало миссии)"),
    (0x2D, "anim_2d_level_clear_walk.png", "Mission Clear (Завершение миссии)"),
]


def load_resources():
    rom = (ROOT_DIR / "Darkwing Duck (USA).nes").read_bytes()
    if hashlib.sha256(rom).hexdigest() != EXPECTED_SHA256:
        raise ValueError("ROM identity differs from the researched revision")
    tiles_data = CHRS_PATH.read_bytes()
    expected = b"".join(decode_tile(rom[i:i+16]) for i in range(0x20010, 0x40010, 16))
    if tiles_data != expected:
        raise ValueError("Decoded CHR export differs from the original ROM")
    banks = {i: rom[0x10+i*0x4000:0x10+(i+1)*0x4000] for i in (4, 5, 7)}
    return tiles_data, banks


def pointer(bank, low, high, index):
    cpu = bank[low-0x8000+index] | (bank[high-0x8000+index] << 8)
    if not 0x8000 <= cpu < 0xC000:
        raise ValueError(f"Export requires a static ROM frame, got ${cpu:04X}")
    return cpu - 0x8000


def animation_context(anim_id, banks):
    section = SPECIAL_SECTIONS.get(anim_id)
    prg = 4 if section is not None else 5
    bank = banks[prg]
    if section is None:
        chr_bank, palette_index = CHR_BANK, banks[7][0xDF22-0xC000]
    else:
        chr_bank = banks[7][0xE515-0xC000+section] & 0x1F
        palette_index = banks[7][0xE593-0xC000+section]
    pal = banks[7][0xC4FD-0xC000:0xC505-0xC000]
    off = 0xC505-0xC000 + ((palette_index << 3) & 0xFF)
    pal += banks[7][off:off+8]
    palettes = [[None] + [NES_PALETTE[c & 0x3F] for c in pal[i+1:i+4]] for i in range(0, 16, 4)]
    alo, ahi, flo, fhi, clo, chi = ((0xB222, 0xB292, 0xA325, 0xA3C5, 0xAF27, 0xAFAF)
                                  if prg == 4 else (0xB429, 0xB4F0, 0x9B0B, 0x9C45, 0xAE53, 0xAF23))
    entry = pointer(bank, alo, ahi, anim_id)
    ids = list(bank[entry+2:entry+3+(bank[entry] & 0x7F)])
    frames = []
    for fid in ids:
        high = 256 if prg == 5 and bank[entry] & 0x80 else 0
        part = pointer(bank, flo, fhi, fid+high)
        # $80F2-$80FE/$818F: direction chooses a coordinate table AND XORs tile H-flips.
        # Cutscene frames are authored for the right-facing $D0 context. Their alternate
        # coordinate entry is not necessarily a useful mirrored artwork pose.
        coord = pointer(bank, clo, chi, bank[part+1]+1)
        parts = []
        for j in range(bank[part]+1):
            y, x = bank[coord+2*j:coord+2*j+2]
            parts.append({"tile": bank[part+2+2*j], "attribute": bank[part+3+2*j] ^ 0x40,
                          "x": (x+128) % 256-128, "y": (y+128) % 256-128})
        frames.append({"id": fid+high, "parts": parts})
    return {"id": anim_id, "prgBank": prg, "chrBank4KiB": chr_bank, "section": section,
            "facingRight": True, "animationCpu": entry+0x8000, "durationRaw": bank[entry+1],
            "frameIds": [f["id"] for f in frames], "paletteIndices": list(pal)}, frames, palettes


def render_single_frame(frame_id, frames, tiles_data, chr_bank=CHR_BANK, sprite_pals=SPRITE_PALS):
    """Renders a single metasprite frame onto a fixed CELL_SIZE x CELL_SIZE RGBA canvas."""
    img = Image.new("RGBA", (CELL_SIZE, CELL_SIZE), (0, 0, 0, 0))
    parts = frames[frame_id]["parts"]
    pixels = img.load()
    
    # Lower OAM indices win opaque overlaps on the NES.
    for p in reversed(parts):
        tile_idx = p["tile"]
        attr = p["attribute"]
        pal_idx = attr & 3
        flip_h = bool(attr & 0x40)
        flip_v = bool(attr & 0x80)
        
        tile_offset = (chr_bank * 256 + tile_idx) * 64
        tile_bytes = tiles_data[tile_offset : tile_offset + 64]
        
        px_base = ORIGIN_X + p["x"]
        py_base = ORIGIN_Y + p["y"]
        
        for ty in range(8):
            sy = 7 - ty if flip_v else ty
            for tx in range(8):
                sx = 7 - tx if flip_h else tx
                ci = tile_bytes[ty * 8 + tx]
                if ci != 0:
                    c = sprite_pals[pal_idx][ci]
                    dest_x = px_base + sx
                    dest_y = py_base + sy
                    if not (0 <= dest_x < CELL_SIZE and 0 <= dest_y < CELL_SIZE):
                        raise ValueError(f"Frame {frame_id} exceeds the fixed origin canvas")
                    pixels[dest_x, dest_y] = (c[0], c[1], c[2], 255)
                        
    return img


def export_chr_tilemap(tiles_data, out_path, chr_bank=CHR_BANK):
    """Exports the full 256-tile bank (16x16 tiles of 8x8 px) scaled 2x to 256x256 px."""
    img = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
    pixels = img.load()
    
    # Render with Darkwing's primary coat palette (Pal 1)
    pal = SPRITE_PALS[1]
    
    for t in range(256):
        tile_x = (t % 16) * 8
        tile_y = (t // 16) * 8
        tile_offset = (chr_bank * 256 + t) * 64
        tile_bytes = tiles_data[tile_offset : tile_offset + 64]
        
        for ty in range(8):
            for tx in range(8):
                ci = tile_bytes[ty * 8 + tx]
                if ci != 0:
                    c = pal[ci]
                    pixels[tile_x + tx, tile_y + ty] = (c[0], c[1], c[2], 255)
                else:
                    # Subtle checkerboard for transparent pixels in tilemap preview
                    bg_col = (20, 26, 36, 255) if ((tile_x + tx) // 4 + (tile_y + ty) // 4) % 2 == 0 else (28, 36, 48, 255)
                    pixels[tile_x + tx, tile_y + ty] = bg_col

    # Scale 2x with nearest neighbor for sharp pixel art display
    scaled = img.resize((256, 256), Image.NEAREST)
    scaled.save(out_path)
    print(f"Exported CHR tilemap: {out_path.name} (256x256 px)")


def export_animation_strips(tiles_data, banks):
    """Exports horizontal sprite strips for all configured player animations."""
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    
    # 1. Export CHR tilemap
    export_chr_tilemap(tiles_data, OUTPUT_DIR / "player_tileset_chr.png")
    
    unique_frames = []
    unique_keys = set()
    manifest = {"romSha256": EXPECTED_SHA256, "cellSize": CELL_SIZE, "origin": [ORIGIN_X, ORIGIN_Y],
                "scope": "Static ROM artwork with explicit table/CHR/direction context; not runtime acceptance", "animations": []}
    
    for anim_id, file_name, label in ANIMATIONS_TO_EXPORT:
        context, frames, palettes = animation_context(anim_id, banks)
        context["file"] = file_name
        manifest["animations"].append(context)
        n_frames = len(frames)
        
        # Keep track of unique frames for the master atlas
        for idx, frame in enumerate(frames):
            key = (context["prgBank"], context["chrBank4KiB"], frame["id"], tuple(context["paletteIndices"]))
            if key not in unique_keys:
                unique_keys.add(key)
                unique_frames.append(render_single_frame(idx, frames, tiles_data, context["chrBank4KiB"], palettes))
                
        # Create horizontal strip
        strip_w = n_frames * CELL_SIZE
        strip_h = CELL_SIZE
        strip_img = Image.new("RGBA", (strip_w, strip_h), (0, 0, 0, 0))
        
        for idx in range(n_frames):
            fr_img = render_single_frame(idx, frames, tiles_data, context["chrBank4KiB"], palettes)
            strip_img.paste(fr_img, (idx * CELL_SIZE, 0))
            
        out_file = OUTPUT_DIR / file_name
        strip_img.save(out_file)
        print(f"Exported animation {anim_id:02X} ({label}): {file_name} [{n_frames} frames, {strip_w}x{strip_h} px]")
        
    # 2. Export Master Atlas of all unique player poses
    n_unique = len(unique_frames)
    cols = 8
    rows = (n_unique + cols - 1) // cols
    atlas_w = cols * CELL_SIZE
    atlas_h = rows * CELL_SIZE
    atlas_img = Image.new("RGBA", (atlas_w, atlas_h), (0, 0, 0, 0))
    
    for idx, fr_img in enumerate(unique_frames):
        c = idx % cols
        r = idx // cols
        atlas_img.paste(fr_img, (c * CELL_SIZE, r * CELL_SIZE))
        
    atlas_file = OUTPUT_DIR / "player_full_atlas.png"
    atlas_img.save(atlas_file)
    (OUTPUT_DIR / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(f"Exported Master Atlas: {atlas_file.name} [{n_unique} unique poses, {cols}x{rows} grid, {atlas_w}x{atlas_h} px]")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-native", type=Path, help="Verify existing PNGs against PlayerAnimationContextContract RGBA outputs")
    args = parser.parse_args()
    if args.check_native:
        verify_native_exports(args.check_native)
        return
    print("Loading ROM extracted data...")
    tiles_data, banks = load_resources()
    print("Exporting player sprites and animation strips...")
    export_animation_strips(tiles_data, banks)
    print("Finished! All files written to Art/Docs/GameDesignDocument/sprites/")


def compare_rgba(actual, expected):
    if len(actual) != len(expected):
        raise ValueError(f"RGBA length mismatch: {len(actual)} != {len(expected)}")
    if actual != expected:
        first = next(i for i, (a, b) in enumerate(zip(actual, expected)) if a != b)
        raise ValueError(f"RGBA byte mismatch at {first}: {actual[first]} != {expected[first]}")


def verify_native_exports(native_dir):
    frames = 0
    required = {f"anim_{anim:02X}.rgba" for anim, _, _ in ANIMATIONS_TO_EXPORT}
    if {p.name for p in native_dir.glob("anim_*.rgba")} != required:
        raise ValueError("Missing or extra native animation data")
    for anim, filename, _ in ANIMATIONS_TO_EXPORT:
        with Image.open(OUTPUT_DIR / filename) as img:
            if img.height != CELL_SIZE or img.width % CELL_SIZE:
                raise ValueError(f"Invalid sprite strip dimensions: {filename}")
            compare_rgba(img.convert("RGBA").tobytes(), (native_dir / f"anim_{anim:02X}.rgba").read_bytes())
            frames += img.width // CELL_SIZE
    print(f"PASS {len(required)} strips / {frames} frames: exact native OAM/PPU RGBA")


if __name__ == "__main__":
    main()
