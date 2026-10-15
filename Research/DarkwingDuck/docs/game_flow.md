# Darkwing Duck (NES) — Game Flow State Machine

Reverse-engineered game-flow doc for the Unity port. Companion: `labels_flow.json`.
Evidence: static disassembly (`disasm/flat_bank*.txt`, `disasm/bank7_*.txt`) + emulator probes
(`Tools/DarkwingResearch/runs/flow_boot`, `flow_confirm`, `flow_yes`, `flow_gameplay`, `flow_death`).

## 0. Architecture recap

- One cooperative master process (entry `$C595`, bank 7 fixed at `$C000-$FFFF`) runs the whole game
  as a linear script with `JSR $FF06` yields. `CreateProcess $FED7` is used exactly once (boot).
- Every "screen" in the game (title, cutscenes, city map, missions, interiors) is a **world**:
  a 17-wide **layout grid** of tile/scene bytes + per-tile behaviors, ticked by a per-bank engine.
- `$31` = **current world/scene id**. Ids 0-6 = the 7 missions; ids 7+ = cutscene/dialog scenes.
- `$32/$33` = pointer to the current world's layout grid. `$22/$23` = index of the player's cell
  inside that grid (also used as a scratch pointer by dialogs).
- `LDA ($32),Y / LDY $22` reads "the tile under the player"; in the city map grid (bank 1)
  special tile values drive the scene-mode dispatch at `$CA8F`:
  `$14` → bank 5 gameplay engine, `$34/$56/$76/$97/$B5/$98` → bank 4 `$9800` interior engine,
  anything else → bank 5 `$8000` (map/default engine).
- NMI `$C000`: OAM DMA from `$0200`, palette upload from `$0300` when `$18!=0`, VRAM queue
  (`$C119`, flags `$1C/$1D`), scroll writes `$2005 = $FC (X), $FA (Y)`, CHR switch from
  `$1A/$1B` (`$1A&$1F`→CHR0 sprites, `$1B&$1F`→CHR1 bg) via `SetCHR0 $FF21`/`SetCHR1 $FF3B`,
  bank-task queue (`$DC[8]`, marker `$88`, idx `$DB`) processed at PRG=3.
- All 40 `JSR $FF55` (SwitchPRG) call sites live in bank 7; other banks request via bank-7 helpers.

## 1. Boot & attract flow (master process `$C595`, bank 7)

| Step | Routine (bank 7 unless noted) | Screen (verified frame ≈) | Notes |
|---|---|---|---|
| 1 | `$C595` init, `$C276` fade-to-black, audio `$C21E`, `$C838` clear `$0400-$05FF`, palette `$C1BA` | black | `$51=$F0` delay |
| 2 | `$C95C` clear objects/scroll; `$31=$07; JSR $DD57` | copyright "©DISNEY PRODUCED BY CAPCOM…" (f≈50-250) | `DialogScene(7)` |
| 3 | `$62=$0E; JSR $AA00` (bank 2); sleep 30; `$62=$0F; JSR $AA00` | "I am the terror that flaps in the night!" intro monologue (f≈250-750) | `$AA00` = VRAM text-stream player (`$ABAA/$ABBD` per `$62`); `$62` = text stream id |
| 4 | loop `yield; $F5A4(0); $F5DA; bank5 $8000` until `$0510 >= $5C` | intro continues | object-0 Y gate |
| 5 | `bank2 $9600` (sound `$2B`, wait anim), sleep 60 | — | |
| 6 | dialogs `DialogScene(9)`,`(0E)`,`(0C)`,`(0A)` with bank2 object scenes `$8F00/$9100/$9000/$A000` | thunderbike ride (f≈800-1100), plane at night (f≈1200-1400), DW close-up (f≈1500-2000) | any button skips; Start (`$14&$10`) → jump to step 9 |
| 7 | loop `yield; $F51B(0); $DA70; bank5 $8000` until scroll X `$FD==1 && $FC==0` | bike ride scrolls exactly one screen ($100 px) | |
| 8 | fade `$C970`; `bank2: $8000(X=1); $8D30; $9200; $C158` render on; `$51=$B4` wait; idle counter `$67/$68` | DW + Launchpad rooftop scene | if `$67==$A0 && $68==6` (~29 s) → `JMP $FFE0` soft reset (attract loop) |
| 9 | wait `$14&$10` (Start) at `$C78C` → `$C792` | | |
| 10 | `$C792`: fade; `bank2 $9500` + `$9200`; wait Start at `$C7AD` | TITLE screen: logo + PRESS START (f≈2100+) | Start → `$C7CF` |
| 11 | `$C7CF`: `$C986` clear; `$62=$0A; bank2 $9E00; $62=$0D; $AA00` | mission-briefing cutscene (Launchpad + DW) | |
| 12 | `bank2: $8000(X=2); $8E00`; `$31=$00; $A100`; `$C8B5` (=bank4 `$8000` + `DialogScene($31)`) | map intro dialog | then `JMP $C9AC` = city map loop |

`$DD57` = `DialogScene/LoadWorld(A via $31)`. Per-id descriptor tables (bank 7, indexed by `$31`):

| Table | Meaning |
|---|---|
| `$DF50/$DF60` | layout grid pointer lo/hi (id0=`$DF90` bridge, id1=`$DFD4` city, id2=`$E018` sewers, id3=`$E05C` tower, id4=`$E0D4` forest, id5=`$E10D` warehouse, id6=`$E146` FOWL; id 31 = `$83E1` in bank 1 = the city map grid) |
| `$DEE6`/`$DEF5` | CHR0 sprite / CHR1 bg bank request (written to `$1A/$1B`; NMI masks `&$1F`) |
| `$DEC8` | initial scroll Y (`$FB`) + obj0 Y |
| `$DEB9` | player start cell index in grid (`$22/$23`; mission 1 = `$33` = row 3 col 0) |
| `$DF04` | music/sound id (`$C2F9`) |
| `$DF22` | sprite palette set (`$C319` = copy 16 bytes from `$C4FD/$C505[id*8]`) |
| `$DEAA/$DED7/$DEE6...$DF41` | per-scene small params (`$36`,`$2C`,`$1A`,`$D9`,`$2D`,`$3D`) |
| `$E388` | same structure for map sub-scenes (tables `$E497+`), entered when `$5B!=0` |

Special-casing inside `$DD57`: `$31 < 7` → world init (`$04D0=$80,$0400=$D0`, `$50=9`, `$F5FC`);
`$31 == 8/10` → `$0400=$D8`; ends with `JMP $C254` (bank-5 tick + palette fade-in `$C270`).

## 2. City map loop (`$C9AC` … `$CAD8`, bank 7)

Per frame (`$C9AC`):
1. `$65` bit7 set → spin at `$CAD8` (yield loop) until map rebuild done (rebuild = `$D95D`).
2. **Select** (`$14&$20`) on states not in {7,9,$12,$15} and `$61!=0`: sound `$1B`; toggle `$98`;
   help-bubble select logic (`$5F`, `$05BE/$05C0`, `$54==2 → $CAE7`).
3. **Start** (`$14&$10`) on enterable state: sound `$1C`; `bank4 $8F00` (save map objects to
   `$0360+` buffers, set briefing objects from tables `$9026/$902C`) → opens confirm dialog.
4. `$CAFC`: map cursor/water anim (`$05E4/$05E6` RAM-frame wave), then
   `LDX $50; AND #$1F; JMP ($CB5F/$CB75)` — **map state jump table** (state `$50`):
   0:`$CB8B` 1:`$CD0E` 2:`$CFD7` 3:`$D16E` 4:`$D1A4` 5:`$D282` 6:`$D39A` 7:`$D453` 8:`$D51B`
   9:`$D517` A:`$D690` B:`$D726` C:`$D7F9` D/E:`$D8A4` F:`$D837` 10:`$D826` 11:`$D838`
   12:`$D817` 13:`$D894` 14:`$D9B2`… (low confidence on entries ≥7; see labels).
   - `$CD0E` = "standing": Left/Right → walk (`$CCBA`), Up → `$56=1`, B → `$CF3A` check →
     `$CE39` spawn HELP bubble readout; `$50` updated by tile/walk handlers.
5. `$DA70` update obj0; `bank4 $8090` (scroll/VRAM row feeder); `$5B!=0 → $E388` (load map
   sub-scene); `bank6 $8000`, `bank1 $8800` (map object behaviors via table `$8836/$88B6`, behaviors ≥ `$47`).
6. `$CA8F` scene-mode dispatch by tile under player (see §0) → bank 5 tick (map/gameplay) or bank 4 `$9800` tick (interiors).
7. `$CAD8`: `$C2DE`; `$9A` frame counter; yield; loop.

**Pending-load processor `$D41B`** (called from `$CAFC` tail `$CB3E`):
- `$6D!=0` → `X=$6D&$1F; JSR $E388` (load map sub-scene: HELP text, interiors).
- `$64!=0` → `$D46C` **GAME OVER card** (fade `$C276`, `$C838/$C92A`, `$C1BA` clears `$2000-$23FF`,
  bank2 task 3 draws its screen records, `$8D00` stages the palette, `$9900` waits for input).
  Not a map rebuild — the map rebuild is `$D95D` (corrected 2026-10-16, evidence below).
- else → `JSR $DD57` with `$31` = **world id 0-6 → mission load!** After load, `$1A=$D589[$31]`.

### Mission selection (bank 2 `$A400-$A607`, the YES/NO + tier chooser)

- `$38` = **missions-completed / tier counter** (0 boot; `INC $38` at `$D95B` on each mission-complete return).
- `$6C` = cursor over the 3 current-tier HELP bubbles; Left/Right move it (`$A46E/$A48B`).
- Confirm (B) → jump table `$A4A8/$A4AB` → handler per bubble:
  - bubble 0 `$A4B0`: `$38<3 → $31=0` (bridge); `$38<6 → $31=3` (tower); `$38==6 → $31=6` (FOWL).
  - bubble 1 `$A503`: `$38<3 → $31=1` (city); else `$31=4` (forest).
  - bubble 2 `$A53A`: `$38<3 → $31=2` (sewers); else `$31=5` (warehouse).
  (each handler spawns flight/briefing objects ids `$73`, `$BB` via `$FA6C`, calls `$A6A5`).
- `$A571`: hide HELP-bubble objects `$04D2-$04D4` for the chosen mission slot.
- `$A5AB`: if `$F8` bit7 (just completed) set completed-bit: mission id → `$69 |= bit(id)`.

**YES A / NO B confirm dialog**: text drawn via `$AA00` stream; verified behavior:
mission help text → A → "YES A BUTTON / NO B BUTTON" → A = fly to node.
Exact YES/NO input handler address not yet pinned (open question Q3).

### Mission entry sequence (verified frames, `flow_yes`)

YES → DW flies across map screen (bank-4 engine, f≈430-730) → loader switches PRG to bank
`set` (= `$31`, 0-6) to read metatiles (f≈732) → NMI applies CHR (`$1A=0E|80?`, `$1B=10..16`)
→ PRG→bank 5 gameplay (f≈743) → level fades in with intro sign (f≈870).

### Mission → level data mapping (deliverable 2)

| $31 | name (levels.json) | layout grid (bank 7) | tile/metatile bank (PRG) | CHR1 bg (`$DEF5&1F`) | CHR0 spr | start cell ($DEB9) | music (`$DF04`) |
|---|---|---|---|---|---|---|---|
| 0 | bridge | `$DF90` (17x4) | bank 0 (`$36F0` big / `$3AF0` small) | 16 (`$90`) | 14 (`$8E`) | `$33` | `$00` |
| 1 | city | `$DFD4` | bank 1 | 17 (`$91`) | 14 | `$33` | `$01` |
| 2 | sewers | `$E018` | bank 2 | 18 (`$92`) | 14 | `$00` | `$04` |
| 3 | tower | `$E05C` (10x12) | bank 3 | 19 (`$93`) | 14 | `$6E` | `$05` |
| 4 | forest | `$E0D4` (19x3) | bank 4 | 20 (`$94`) | 14 | `$00` | `$07` |
| 5 | warehouse | `$E10D` (19x3) | bank 5 | 21 (`$95`) | 14 | `$00` | `$0C` |
| 6 | fowl_fortress | `$E146` (19x3) | bank 6 | 22 (`$96`) | 14 | `$00` | `$09` |

levels.json offsets are file offsets (include 16-byte iNES header): `layout_offset-0x10` = PRG offset.
All 7 grids + scroll tables live consecutively in fixed bank 7 (`$DF90-$E352+`), so the engine reads
them without banking. Sprite CHR is bank 14 for all missions; bg CHR = `16 + 2*set`.
Cutscene scene ids (7-14) use their own grids (`$E17F-$E18A`) and CHRs (bg 24/28/29/30, spr 15/31/0).

## 3. Gameplay (world ids 0-6, bank 5 engine)

- Frame: map-loop path `$CAAF` (or dedicated loops) → `bank5 $8000` = object tick: alternating
  even/odd frame order (`$95` parity), 16 object slots stride `$10`
  (`$0400,X` flags, `$0420,X` anim id, `$04E0,X` behavior, `$04F0/$0500,$0510/$0520` abs X/Y).
- Object behaviors are **bytecode scripts**: pointer tables bank 5 `$9B0B/$9C0B` (+hi `$9D45`),
  rendered by OAM builder `$8150` into shadow OAM `$0200` (bank 5 `$AE53/$AF23` metasprite tables).
- Bank task queue runs per-frame side ticks in banks 4/6/1 (seen in NMI logs during gameplay).
- **HUD**: top-left heart (health), drawn in nametable; verified pause screen shows
  "POINTS 000000 / GAS / GAS ENERGY: 03".
- **Pause**: Start during gameplay → bank 4 routine (`$8F83` region; entered via bank-4 task):
  saves objects (`$0360+`) & sprite palettes (`$038F`), `$1A=$9F` (CHR0=31 status font),
  `$FE&=$F7` (hide sprites), loop {bank5 tick, draw status screen `$90E1`, `$9148`, wait Start};
  resume path `$8FCD` restores everything (`$1A=$F2|$80`, `$FE|=$08`).
- Health (`$42/$43=03,03` at spawn) and gas energy (`03`) live in low RAM; exact score/lives
  addresses not yet pinned (open question Q4). Select cycles gas weapons (standard DW behavior;
  not yet located — Q5).

## 4. Mission complete → map return (bank 7 `$D49C`)

Outcome flag `$F8` (bit7 set = mission completed, cleared = died):
- **Completed** (`$F8<0`): `$D4B3`: fade, render off, palette, `$C95C`, `$62=$07`, audio;
  clears `$5D/$54/$60/$61/$6B/$6D`; `JMP $D95D` → map rebuild; `INC $38` (tier counter);
  `bank2 $9900` cutscene (`$D4AC`). At map, `$A5AB` sets `$69` bit for the finished mission.
- **Died** (`$F8>=0`): `$D4E9`: `$C235`, obj0 off, yield, `$97=4`, `JSR $C8B5` → `bank4 $8000` +
  `DialogScene($31)` — **reloads the same world from its start grid cell** (no mid-level
  checkpoint found; checkpoints = level start. Open question Q6 covers mid-level flags).
- After return, master continues in map loop `$C9AC` (`$65=$FF` set by `$D8D0` path keeps the
  loop spinning while the map rebuilds).

### GAME OVER card + prompt (`$D46C` … `$D4AF`, ported 2026-10-16)

Reached from `$D41D` when the death counter `$64` is non-zero (`$CB27` INCs it only once both HUD
energy columns `$05E6`/`$05E4` are empty). Sequence: `$C276` fade → `$FF06` yield → `$C14E` →
`$97=$04` → `$C21E` → `$C838` (clear `$0400-$05FF`) → `$C92A` (38 B from `$C936` → `$05C2`) → zero
`$FC/$FA/$FD/$FB/$95` → `$C1BA` with `A=$20` (`$2000 &= $FE`, clear 1024 tiles + 64 attributes at
`$23C0`) → `$C21E` → `LDA #$02 JSR $FF55` (PRG bank 2) → `LDX #$03 JSR $8000` (draws bank-2 screen
records) → `JSR $8D00` (stages palette) → `LDA #$0C JSR $F84E` → `JSR $9900` → `$D4AF LDA $F8`.

Card content, read straight out of the ROM (`prg_bank2.bytes`, ROM SHA-256 `1271dce8…abb0a00`):
records at `$8789`, `[addrHi][addrLo][len]` + tiles, ended by `addrHi & $80` —
`$20AE`/`$20EE` = "GAME"/"OVER" (rows 5,7 col 14), `$2165`/`$21A5` = "CONTINUE     A BUTTON"/
"STAGE SELECT B BUTTON" (rows 11,13 col 5); font tile = `$DA + letter − 'A'`, space `$03`;
BG palette = 16 B at `$8D15`. MMC1 latches chr0=`$1F`/chr1=`$19` (gold `chr_map.csv`), `$2000=$90`
⇒ BG pattern table `$1000` = 4K CHR bank 25. No sprites.

Branch (both directions captured from the original ROM):
- **A/Start** → `$F8` stays clear → `$D4E9` = **CONTINUE**: `$C235`, `$0400=0`, `$04D0=0`, `$6D=0`,
  yield, `$97=$04`, `JSR $C8B5` (= bank `$04` + `JSR $DD57` on world `$31`, which still holds the
  mission id) → same mission restarts; `$DD57` zeroes `$64` at `$DE4C`.
- **B** → `$998C` sets `$F8=$FF` → falls into `$D4B3` = **STAGE SELECT**: `$C276`, yield, `$C14E`,
  `LDA #$F0 JSR $F84E`, `$C1BA`, `$C95C`, `$62=$07`, `$97=$04`, `$C21E`, zero
  `$5D/$54/$60/$61/$6B/$6D`, `JMP $D95D` (map rebuild; `INC $38` at `$D95B` is jumped past, so the
  tier counter does NOT advance).
- Evidence: `reference/gold_gameover` (600 f card), `gold_gameover_continue` (`0:a@300-303`,
  ends in the mission), `gold_gameover_select` (`0:b@300-303`, ends on the city map). Port check:
  `AgentScripts/GameOverVerify.cs` → nametable `$2000-$23FF` 0 mismatches vs `vram_frames.bin` f300,
  BG palette 0/16, and the rendered frame is **0/61440 differing pixels** vs `frame_000300.png`.
- Open: the exact `$9900` input gate (how many frames the card ignores pad) was not measured —
  the capture seeded with A at f266 produced no `ram_frames.bin`, so the port accepts input from
  the first GameOver frame (GAPS G27-note).

## 5. Progression & completion flags

- `$69` = **completed-missions bitmask**, bit N = mission N done (bank 2 `$A5AB-$A607`).
- `$38` = missions completed count / tier index (0 → choose from {0,1,2}; 3 → {3,4,5}; 6 → {6}).
- `$F8` = last mission outcome (bit7 = success).
- `$50` = map state (position/zone); states {7,9,$12,$15} reject Start/Select (dialogs, FOWL gate).
- `$65` bit7 = "map needs rebuild" latch; `$5B` = pending map sub-scene id; `$64` = pending map rebuild request.
- `$6D` = pending map sub-scene load (`$E388`).

## 6. Ending (final mission 6 = fowl_fortress)

Static evidence only (not probed yet — open question Q7):
- After mission 6 completes, tier `$38==6`; map state 0x16 handler `$D9B2`: if `$66==$FF` →
  `$62=$0B; bank2 $9E00; $62=$12; bank2 $AA00` (ending text/cutscene stream 0x12), else `$62=$0C`.
- Map state 0x11 handler `$D838` (`$31>=6`): fade; `bank2 $8000(X=5); $A900` (palette+objects id
  `$56`, CHR `$1A=$8C/$1B=$9E`) — post-final cutscene; then `bank2 $9E00`, `DialogScene($0B)`,
  `bank2 $9280`, `$65=$FF` (map rebuild).
- `$AA00` stream id `$12` and grid `$E18A` (scene id 13/14: CHR0=`$80`→0, CHR1=`$9D`→29) are the
  likely ending/credits screens. No ASCII "CREDITS" text exists in PRG (text is CHR tiles drawn
  from VRAM streams); the ending is the `$AA00($12)`/scene-13/14 sequence. Exact order TBD.

## 7. Open questions

- Q1: full map-state jump table entries ≥ index 7 (hi-table `$CB75` alignment needs a scripted
  disassembly pass from `$CB8B`/`$CD0E` etc.).
- Q2: map node → `$38`-tier bubble plumbing: which handler writes `$38`/opens the bubble chooser
  (bank 2 `$A400` loop is invoked from the dialog advance path; caller not yet traced).
- Q3: YES/NO confirm-dialog input handler address (bank 2 region `$A400-$A6A4`, exact routine TBD).
- Q4: score/lives RAM addresses (score shown on pause as POINTS; lives counter not yet located).
- Q5: Select-button gas-weapon cycling routine (expected bank 5/6 player behavior).
- Q6: whether any mid-level checkpoints exist (retry path `$D4E9` reloads from level start; the
  spring/sign objects at level start may just be decoration).
- Q7: ending/credits exact sequence & screens (needs a playthrough probe of mission 6 completion).
- Q8: `$D589` per-mission values (`$80,$83,$81,$84,$86,$80,$81`) — CHR0 with flag bits (semantics TBD).
- Q9: energy/health decrement sites (`$42/$43` start at 03) and the heart HUD update routine.
