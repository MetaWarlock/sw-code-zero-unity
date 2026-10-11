# Darkwing Duck (NES) — Player Mechanics (static reverse, addresses + constants)

Scope: player (object slot 0) movement, physics, collisions, hook/hang, gas gun, damage, animations.
All CPU addresses are as executed in game (fixed bank = PRG bank 7 at $C000-$FFFF; switchable
$8000-$BFFF = bank noted per label). File offset = `bank*0x4000 + (addr - base)`, base $8000 for
banks 0-6, $C000 for bank 7.

## 0. Corrections to previously "established" context

- The NMI bank-queue task (`NMI $C0F6: JSR $8000`) runs the **audio driver in PRG bank 3**
  (bank3 $8000 = `JMP $806C`; $8393 = note processing, $8684/$86BA/$8023 = tempo/envelope
  helpers, $80EC = `STA $4000,Y` APU write). The earlier claim that bank5 $8393/$8684 are
  "per-object animation advance / sprite composition" is **wrong** — those are audio.
- Player animation + OAM composition **is** bank5 $8000-$81F7 (`$802F` per-slot:
  culling $802F-$80C6, player invuln blink $80E1-$80F1, anim advance $8100-$8141, OAM
  composition $8143-$81F7). It runs every frame from the master loop.
- Bank 4 $9800 is a near-copy of bank5 $8000 (same object update; differs only in JSR
  targets $982F and anim tables $B222/$B292 instead of $B429/$B4F0). Only screens whose
  level-script byte is $34/$56/$76/$97/$B5/$98 use the bank4 copy (`$CAC5`); all others
  use bank5 (`$CAAF`). Bank4 $9800 was never executed in the 120-frame mission-1 trace.
- Bank 2 is not only menus: the **frame task loop** at bank2 $A400 runs the briefing/level
  intro (loops until A/B/Left/Right pressed, `$A414: AND #$C3`), and the fixed-bank master
  gameplay loop is `$C9AC`.

## 1. Per-frame flow (master process, fixed bank)

`$C9AC Player_MainLoop` (loops via `$CAE4 JMP $C9AC`, yields each frame with `$FF06`):

1. `$C9AC`: `$65` negative → skip to `$CAD8` (level inactive).
2. `$C9B3` **Select** (edge `$14` bit5): if `$50` not in {09,12,15,07} and `$61 != 0`:
   SFX $1B (`$F850`), `$98 ^= 1` (gas gun on/off). Activate (`$CA07`): `$53 = $54` (weapon
   index), `INC $5F`, save `$61→$05C0`, `$60→$05BE`. Deactivate (`$C9E0`): restore
   `$05C0→$61`, `$05BE→$60`, clear `$53/$5F/$05BE/$05C0`; if `$54==2` also `$CAE7`
   (or `$04D2/$04D3 |= $83`).
3. `$CA17` **Start** (edge bit4): same state guards → SFX $1C, bank4 `$8F00` (pause).
4. `$CA40 JSR $CAFC Player_DeathCheck`: if slot0 active (`$04D0` bit7) →
   `$CB41 Player_StateDispatch`; else energy/HUD drain `$CB01-$CB3E` (see §7) then `$D41B`.
5. `$CB41 Player_StateDispatch`: X=0; **every frame** re-arms walk velocity
   `$0530=$80, $0540=$01` (`$CB43-$CB4C`); dispatches `JMP ($CB5F+Y)($CB75+Y)`, `Y = $50 & $1F`
   (player state machine id, see §2).
6. `$CA59 JSR $DA70 Player_CameraFollow`: horizontal dead-zone follow; screen X of player
   `$00 = $04F0-$FC`; scroll right (`$DC19`, `$2F=2`) while `$00 >= $10` (and `$00 < $88`),
   scroll left (`$DBCE`, `$2F=1`) while `$00 >= $89`/`< $F1`; after scrolling the player is
   pushed 16 px (`$DABD-$DACC`, `$DAEE-$DAFA`). Skipped while `$50` in {07,08,12}.
7. `$DAFD Player_ScriptCheckpointHook`: reads level script byte `($34),Y` (Y=`$22` = current
   screen column): byte $C1-$CF odd → **checkpoint** `$6D = byte`; low nibble == 1 → hook
   column (see §5); nibble == 2 → vertical scroll snap `$DB3F-$DB6C`.
8. `$CA5C`: `$6E/$6F = $04F0/$0500` (player prev X, used by collision clamps).
9. bank4 `$8090`: level object spawner (layout pointers `$72/$74/$76/$78`, occupancy
   `$0340`, spawns into slots 15..4, sets `$04E0` behavior/$0420 anim/$05A0 etc.).
10. `$E388` if `$5B != 0` (door/level-event loader, §5); `$EDF1` (area palette/CHR by `$31`);
    `$EFBE` (palette animation).
11. bank6 `$8000` = enemy behavior dispatch (slots 1-15, ids < $47, table `$825F/$82A7`);
    also sets `$99 = $40`. bank1 `$8800` = boss dispatch (ids >= $47, table `$8836/$88B6`);
    also `$99 = $40`. **`$99` = gravity fraction = $40 every frame** (§3).
12. bank5 `$8000` (or bank4 `$9800`): object anim/cull/OAM update (§0).
13. `$CAD8`: `$C2DE`, yield `$FF06`.

Input: read on every process resume at `$C162` (labelled Slot0_ResumeHook; called from
`$FEC7 Process_ResumeRunning` → so exactly once per frame per process). `$16` = held,
`$14` = pressed edge; bits 7=A, 6=B, 5=Select, 4=Start, 3=Up, 2=Down, 1=Left, 0=Right
(verified from read order at `$C16D-$C180`).

## 2. Player state machine (`$50`, dispatch `$CB41`, table `$CB5F/$CB75`)

| $50 | handler | meaning |
|----|----|----|
| 00 | `$CB8B` | stand / walk / fire / crouch / look-up dispatcher on ground |
| 01 | `$CD0E` | shared jump/air physics (also re-entered from ground states) |
| 02 | `$CFD7` | crouch (Down held) |
| 03 | `$D16E` | look up (Up held) |
| 04 | `$D1A4` | hurt, airborne (anim $0C→$0B); also 13 ($0D) shares it |
| 05 | `$D282` | stunned (anim 8, timer `$51`) |
| 06 | `$D29A` | hurt, ground/knockback flight |
| 07 | `$D353` | death throes (launch up, pause, fall, energy drain) |
| 08 | `$D41B` | death handler (respawn at checkpoint `$6D` / game over) |
| 09 | `$D517` | walking into a door (bank2 `$9600` transition) |
| 0A | `$D590` | forced hop (crouch+A), uncontrollable ≈17 frames |
| 0B | `$D626` | full airborne control (also 16 `$10` = variant with `$43` flag) |
| 0C | `$D7F9` | hanging on hook/peg |
| 0D | `$D1A4` | hurt variant (exits via `$D23F` to `$10`) |
| 0E | `$D1A4` | hurt, recovering (exits when `$F64A` overlap clears) |
| 0F | `$D837` | no-op (RTS) |
| 10 | `$D626` | airborne variant |
| 11 | `$D838` | area cutscene (briefing / mission-complete, sets `$65=$FF` at end) |
| 12 | `$D517` | post-transition walk-out (also used by death respawn) |
| 13 | `$D994` | transport-tube drop cutscene (`$51==$73` → anim 8) |
| 14 | `$D9B2` | level-complete jingle (bank2 `$9E00`/`$AA00`) |
| 15 | `$DA04` | grabbed/carried by end-of-level enemy (anim $2D, then state $11) |

Table entries beyond index 21 run into data; `$50` never exceeds $15 in code.

### Ground state 0 ($CB8B)

```
$CB8B JSR $CD0E            ; run shared vertical/jump physics first
$CB8E LDA $50; BEQ ..      ; if still state 0:
$CB93 LDA $10; CMP #$04    ; contact result == 4 (enemy or spike, §4/§6)
$CB99 LDA $7C; BNE ..      ; and not invulnerable
  → $CB9D: $50=6, $51=$0F, anim 9 ($FA78), SFX $15, $7C=$3C, JSR $F9C2 (§6)
$CBB7 LDA $14 AND #$80     ; A pressed → jump:
  $0550=$E5, $0560=$04 (impulse), $50=1, JMP $CD0E
$CBCE LDA $16 AND #$03     ; Left/Right held → $CC53 walk:
  B pressed → fire while walking ($CF3A gate, anim 5, $CE39) and RTS (no move this frame)
  anim 5 (fire) → wait frame 2/timer 4 ($CBF6)
  else anim 3 (walk) if not already; walk-cycle wrap: frame 4+timer 6 → frame 1 timer 0 ($CC84)
  frame 0 + timer<4 → footstep dust coords $0530=$48, $0540=$00 ($CCAA)
  steer ($CCB4): $52 = $16&3; bit0 set (Right) → JSR $F200 (A=$00,Y=$02); bit0 clear →
  $CCDF: JSR $F254 (A=$03,Y=$02); blocked if $9E negative; special-case checkpoint $6D==$0E
  with $0400 == $50/$10 (hanging flags) → no move
  after move: $CCEA damage check identical to $CB93
$CBD7 LDA $16 AND #$0F     ; Up only → anim 4, $50=3 ($CC3A)
                           ; Down → anim 6, $50=2, $0490=0 ($CC44)
$CBE6/$CC16 LDA $14 AND #$40 ; B pressed → fire:
  skip if anim==4; $CF3A can-fire gate; set anim 5 if not firing; JSR $CE39 (spawn)
$CBEF no fire input: if anim 5 finished (frame 2, timer 4) or anim==1 → anim 1 (stand), $5A=0
```

### Crouch (state 2, $CFD7)

- Down released → `$D0AD: $50=0` (stand).
- anim 7 (crouch-fire) frame 2/timer 4 → back to anim 6 (`$D08C`).
- B pressed with frame==1 → crouch fire: `$CF3A`, anim 7, `$CE39` (`$CFFB-$D00E`).
- A pressed (`$D00F`): `$F64A` enemy-overlap check, then integrate + probe down:
  `$10==0` → stay; `$10==1/2` → `$D06F: $50=$0A` (forced hop, anim $12, `$F607` vel cut,
  `$0480/$5A/$9D=0`); `$10>=3` → if `$10==8` set `$0400 |= $20` (hook flag), else
  `$EDD2` (snap up) + `$F607`.
- Down pressed edge (`$D0B2`→`$D11C`): `$0490=$FF`; floor probe: `$10==0` → fall (`$F2D7`);
  `$10==2` → **drop-through**: `$D166: $50=1`, `$F607` (velocity = -0.33 down) — press-Down
  descent through the tile; `$10==4` → spike damage; `$10==8` → set `$0400` bit5.

### Look up (state 3, $D16E)

Up held; frame pinning at 2; A pressed → normal jump impulse (`$D18B-$D199`, `$56=1` →
up-reaching jump anim $0D). Up released → `$50=0`.

### Forced hop (state $0A, $D590)

Integrates down (`$F5A4`/`$F5DA` + probes). `$9D` counter: reaches $11 (17) → state $0E hurt
(anim $0B, `$5A=0`). `$5A` counter (gated by `$43`): reaches $11 → state 4 hurt, anim $0B,
**SFX $12**. `$0480` counter: reaches $12 (18) → `$50=1` (airborne). No steering input.

## 3. Physics — exact constants

**Coordinate/velocity format**: positions are 16.8 fixed point:
X = `$04F0` (int lo) / `$0500` (hi) with fraction accumulator `$0470`; Y = `$0510`/`$0520`
with fraction accumulator `$0460` (slot 0). **X grows rightward; Y grows downward** (screen
Y = `$0510 - $FA` mod $F0, `$8049-$8054`). Velocities are signed 8.8: integer byte + fraction
byte (e.g. `$0540` = X vel int, `$0530` = X vel frac; `$0560` = Y vel "up-int", `$0550` = frac).
Vertical integration **subtracts** the velocity from Y ($F5A4 adds when falling, i.e. negative
velocity), so positive `$0560` = upward motion.

| Constant | Addr (set) | Value | Meaning |
|---|---|---|---|
| Walk speed | `$CB43-$CB4C`, re-armed every frame | `$0540=$01, $0530=$80` | **1.5 px/frame** horizontal, both directions ($F4FD adds, $F51B subtracts). Instant start/stop — no acceleration, no friction anywhere in player code (no ice-specific branch found). |
| Jump impulse | `$CBBD-$CBC5` (also `$D18B`, ground/look-up) | `$0560=$04, $0550=$E5` | **4.8945 px/frame upward** (= 4 + 229/256) |
| Gravity | `$F5DA` (`SEC SBC $99`), `$99=$40` set every frame by bank6 `$8000` and bank1 `$8800` | **0.25 px/frame² downward** | velocity -= 1/4 px/frame each frame |
| Terminal fall | `$F5EB-$F5F8` | `$0560=$F8, $0550=$00` | **max fall 8 px/frame** |
| Variable jump | `$CD47-$CD54`: A released (held check `$16` bit7) while `$0560 < 5` → `$F5FC` | `$0560=$FF, $0550=$AB` | velocity set to **-0.332 px/frame** (downward) = ascent cancelled. If `$0560 >= 5` (external launches) the release does not cut. |
| Jump-cut also used | `$F607` (slot 0 fixed form of `$F5FC`) | same values | used on landings, head bonks, hurt exits |
| Knockback drift | `$F9C2`/`$D2DE` | `$F607` value | hurt launch starts as slow fall; steering inverted during flight (§6) |
| Hang drift | `$DB8F-$DB96` | `$0550=$80, $0560=$00` | 0.5 px/frame upward while hanging (peg ride) |
| Air control | state 11 `$D78D/$D795` and state 1 `$CD18` | same `$0530/$0540` | **full air steering at 1.5 px/frame** (same as ground) |

Jump height ≈ v²/2g = 4.8945²/0.5 ≈ **48 px (~3 tiles)**, apex ≈ 20 frames after takeoff.
Short hop: releasing A immediately → rise of ≈ (4.89² − 0.33²)/(2·0.25) frames-worth… precisely
the cut zeroes ascent within 1 frame, minimum hop ≈ 13-14 px.

Turnaround: none — direction changes are instantaneous (both integrators share the same
velocity registers; the facing flag `$0400` bit6 flips immediately, `$F24B`/`$F2A6`).

## 4. Tile collisions

**Probe engine**: `$EB52 Player_TileProbeScan` — samples `Y=$40 … $40+$41` probe points;
each point = `$EBE8`: X = `$04F0/$0500 + TileProbe_OffsetXTable[$ED19]` idx, Y =
`$0510/$0520 + TileProbe_OffsetYTable[$ECAD]` idx; converts to an index into the decompressed
metatile map at **`$0600`** (2 bytes per 16×16 cell column/row pair: address =
`2*((Y>>5 &7)<<3 | (X>>5 &7) | (X>>8 &1)<<6) + ((X>>4|Y>>4)&1)`, `$EC6D-$EC90`) and returns
either `metatile >> 4` (the **type nibble**, when mask table `$ECA9[idx] == 0`) or
`metatile & $0F` (sub-tile solidity mask, when mask = $0F). `$10` = max result over the
points (player: no early exit; other slots exit at `$10 >= 2`).

Probe sets (index into `$ED19`/`$ECAD`, count = `$41+1`):

| caller | $40 | points | X offsets | Y offsets | purpose |
|---|---|---|---|---|---|
| `$F200`/`$F254` via `$EB52` | 0/3 | 0-2 / 3-5 | +12 / -12 | -11,0,+11 | side walls (3 pts: top/center/bottom) |
| falling (`$F2D7`, `$F3A3`) | 6 | 6-8 | -11,0,+11 | +12,+12,+12 | floor (left/center/right foot 12 px below center) |
| rising (`$F3C4`) | 9 | 9-11 | -11,0,+11 | -12,-12,-12 | ceiling/head |
| state 11 hook probe | $3A | 58-60 | (see tables) | ≈ -20,-20,-3 | hook/peg above head |

**Type nibble semantics (measured from consumption code):**

| $10 | effect | evidence |
|---|---|---|
| 0 | empty | `$F32E BCC` keep falling |
| 1 | one-way/special: rising passes through (`$D6EE-$D6F9`); falling with vel >= `$FD` (-3) lands (`$F4EC`, `$D6FB`); on ground probes lands only if probe Y lo nibble >= 8 (`$F332`) | one-way platform |
| 2 | solid ground: landing (`$F360`, `$D6DC`→`$D7B7`), crouch drop-through | main floor |
| 3 | solid: land on top when falling (`$D711`), wall when horizontal (`$F36D`: `$10>=3` → stop) | walls/blocks |
| 4 | **damage** (same path as enemy contact) | `$D134-$D151`, `$D73C`, `$F440` → state 6 hurt + SFX $15 |
| 5 | **door/level object**: `$EBC8` reads level-object map `($34),Y & $1F → $5B` **and `$6D`** | `$5B` triggers `$E388` section load next frame (`$CA6E-$CA72`) |
| 6 | cleared to 0 (no collision) | `$EBE3` |
| 7 | sets `$0400` bit5 ($20) = "on hook tile" this frame; bit5 makes the vertical integrator treat the player as grounded (`$CCEA`/`$CDF5-$CE21`) | grab ledges/pegs from below |
| 8-F | slope/half-tile family: `attr & $0F >= 8` checked at `$F332`; `$10==8` sets bit5 | partial solidity |

**Resolution/snapping** (all against the last sampled probe coordinate `$06`=X, `$07`=Y):
`$ED85` snap left edge (X -= X&0F), `$ED9D` snap right edge (X += 16-(X&0F)), `$EDB3` snap
down onto tile top (Y += 16-(Y&0F)), `$EDD2` snap up (Y -= Y&0F). Horizontal move flow
(`$F200` right): apply velocity `$F4FD` (+1.5) → enemy-overlap pushback `$F612` (penetration
capped at 4, position pushed out; if pushed past `$6E/$6F` previous X → re-probe other side
`$F29A` with `$40=3`) → tile probe; `$10 >= 3` → `$ED85` snap back. Landing flow
(`$F2D7` falling branch): integrate → enemy-overlap vertical pushback `$F64A`/`$F49C` →
gravity `$F5DA` → floor probe; `$10==2` (or 1 with the nibble rule, or >=8) → `$5C=1`,
re-probe with `$40=$3A`, snap `$EDB3`, return carry=1 → caller `$CE08`: `$50=0`, `$56=0`,
`$5C=0`, `$9E=0`, SFX `$10`.

## 5. Hook (peg) grab / hang / ride

- The level **script byte** `($34),Y` per screen column gates everything: low nibble == 1 =
  hook column; byte $C1-$CF (odd) = checkpoint id → `$6D` (`$DB27`).
- **Grab** `$DB6D Player_HookGrabCheck` (called from `$DAFD` after camera follow, only when
  scroll X `$FC == 0` and script nibble == 1): if `$50 != $0C` and player screen Y
  (`$0510-$FA`) >= `$E8` (232) → `$DB8B`: `$50=$0C` (hang), `$0550=$80/$0560=$00`
  (0.5 px/frame up), **all object slots 15..1 cleared** (`$DB99-$DBB1`), script byte $90 →
  `$0400 |= $20`. So Darkwing catches the peg as he descends past the bottom of the screen —
  there is no "Up near hook" test; it is position + script-column based.
- **Hang** state 12 `$D7F9`: integrates `$F539` (0.5/frame rise); anim frame pinning (6→f1,
  $0D→f1, 4→f2). The camera scroll routines carry the player (script byte $90 sets bit5
  again every frame). Release: when scroll Y `$FA == 0` → `$50=1` (fall).
- Grabbing also happens from airborne state 11: `$D6C4` probe with `$40=$3A` (hook points
  above head) feeding `$10` into the same land/grab decisions; and type-7/8 tiles set
  `$0400` bit5 which the jump physics treat as ground (`$CE08`).

## 6. Damage, knockback, death

**Contact detection** `$F612` (horizontal) / `$F64A` (vertical): compares the player (slot 0)
against its paired slot (X^1) when both active and enemy behavior `$04E0 != 0`; computes
overlap via `$F699`/`$F6DF`; result clamped to `$10 = min(pen,4)`. `$10 == 4` = full overlap
→ damage. Spikes produce the same `$10==4` from tile probes — identical reaction.

**Hurt** (fresh hit, `$7C==0`): `$50=6`, `$51=$0F`, anim 9 (`$FA29` picks anim $1D/$1C in
areas `$31==$08` with enemy anim $4D / `$31==$0C` with $5D), SFX $15, `$7C=$3C` (**60
invincibility frames**), then `$F9C2 Player_HurtKnockback`:
- saves `$58 = $50` (state to restore), forces `$50 = 6`;
- writes the flash codes into the dynamic RAM metasprite template `$05B0` block:
  hit1 → `$05B4=$82`, hit2 → `$05B8=$83`, hit3 → `$05B6=$83`, hit4 → `$05B2=$82` **and
  death**: `$7C=0, $50=7, $04D0=$80, anim 9` (`$FA1E`). **4 hits = death** (the HP counter is
  hidden inside the RAM sprite template bytes `$05B2-$05B8`).
- Invuln blink: bank5 `$80E1-$80F1` decrements `$7C` per frame and skips OAM composition when
  `($95 & 4) == 0` (4 frames invisible / 4 visible).

**Knockback flight** (state 6 `$D2C3`): horizontal velocity registers re-set to 0.5 px/frame,
first frame `$F5FC` (fall -0.33), companion slot 5 spawned (template $0A) at player X, Y-$14;
per frame: vertical integrate `$F2D7` (gravity applies), **inverted steering** — Right held
(bit0=1) → `$F254` (moves left, faces right), Left → `$F200` — i.e. the player drifts away
from the held direction while `$52` was latched at hit time; `$51` counts $0F → `$50=0`.
State 4 (air hurt) lets you steer facing only, fire (anim $0C), or press A to jump out
(anim $0D if Up / $11), Down to drop.

**Death paths**:
1. 4-hit ladder (above) → state 7.
2. Pits/falling out: death throes state 7 `$D353` can be entered with `$66` (death flag set
   by level scripts) — `$CB41` runs when `$04D0` bit7 clear via `$CAFC`'s `$66` branch.
3. Gas energy exhausted: `$CB0E-$CB3B` / state 7 `$D3D9-$D407` drain HUD meters
   `$05E6` then `$05E4` (tile codes $70..$79); both empty → `INC $64` → `$D41B`.

**Death sequence** state 7 `$D353`: launch up (`$0560=2`), `$0490=$0F` frames of rise,
`$0480=$3C` pause (at $1E plays SFX $25 and sets `$9B`), then falls with gravity; energy
drain; → state 8.

**State 8 `$D41B`**: if `$64 != 0` → game over cinematic (`$D46C`, bank2 `$8D00`/`$9900`,
continue counter `$5D`, then `$D95D` level restart). Else if `$6D == 0` → reset to level
entry (`$C887` spawn, `$50=$12`, CHR `$D589[$31]`). Else respawn at checkpoint: `$66` clears,
`$5D` counts deaths, `$E388` with X=`$6D & $1F` reloads the section (respawn position
`$04F0=$30, $0510=$06`, anim $2C, `$0400=$D0`, state `$12`).

**Lives/continues**: `$64` = death flag accumulated per life; `$5D` = deaths at checkpoints;
game over → continue screen → level restart (no separate lives counter found; continues are
implicit via `$5D`).

## 7. Gas gun (weapon B)

Fire request (`$CC16`, `$CC53`, `$CD2C` in air, `$CFFB` crouch, `$D1FA` hurt, `$D79C` air):
`$CF3A Player_CanFire` — counts active slots 4..1 whose anim matches the weapon's set
(`$CFCF[$53]` base: w0=$13; w1={16,18,14}; w2={14,17,29}; w3={14,19,1A}); blocked if count >=
`$CFD3[$53]` = **max active [3, 1, 2, 1]** for weapons [0,1,2,3].

`$CE39 Player_FireProjectile`:
- `$53==0` → Y=0; else `$CEF2 Player_EnergyGate` (returns Y=4 = blocked when active-meter
  `$05BE:$05C0` is below the weapon threshold: w1/w3 need `$05C0 >= $72` when `$05BE==$70`,
  w2 needs `$05C0 >= $73`).
- Slot preference `$CF29[Y] = [04, 02, 03, 02, FF]` (search downward for inactive slot).
- Spawn X = player X + offset table (16-bit): `$CF21/$CF22` = **+15 / -15 px** (weapons 0/1),
  `$CF25/$CF26` = **+22 / -22 px** (weapons 2/3); side selected by `$52 & 2` (bit1=Left).
- Spawn Y = player Y - 3, or +8 if player anim 7 (looking up), or same if anim $0E/$0F.
- `$0580,X` = `$52` (direction), behavior `$04E0,X = $CF2E[$53] = [1,2,3,4]`, template
  `$FA6C($CF32[$53] = [13,16,14,14])`, SFX `$F850($CF36[$53] = [11,13,11,14])`,
  `$0570,X = 1`.

Projectile behaviors (bank 6, dispatched via `$825F/$82A7`):

| $53 | behavior | motion | on hit | spawn cost (active meter `$8982` + saved `$890B`) |
|---|---|---|---|---|
| 0 normal gas | 1 `$82F0` | horizontal **4 px/frame** ($0540=4, $0530=0), **6** once anim $18; no gravity | wall → anim $14 burst, cloud remains | none at spawn |
| 1 | 2 `$8385` | lob: X **0.5 px/frame**, Y **3 px/frame up** + gravity → arc | floor → anim $14, spawns 2 behavior-1 clouds (dirs 2,1), SFX $24 | `$FE` = 1 + 1 |
| 2 | 3 `$841C` | waits, then X **3.6 px/frame** ($0540=3, $0530=$99); sticks to wall (`$0570=$84`), proximity check to player, timer `$0480` $FF→$78 → anim $29 lightning falls ($F5C9) | — | `$FD` = 2 + 2 |
| 3 | 4 `$85BC` | no per-frame motion; after anim frame 4/timer 2 → `$8634` spawns 2 behavior-1 clouds (dirs 1,2), object dies | timed | `$FE` = 1 + 1 |

Manual says Heavy/Thunder cost 2 and Arrow 3; the code's per-shot active-meter drains are
1 / 2 / 1 and the fire gates are 2 / 3 / 2. The visible meter is two-digit: `$05BE` = high
digit ($70-$79 = 0-9), `$05C0` = low digit ($70-$7A = 0-10) → 0-99 units; pickups add `$0A`
(10 units) via `$88FF` and set `$54` (1/2/3) + HUD icon `$05BA = $7A/$7B/$7C`. Lingering gas
clouds (behavior 8 `$8837`, anims $22-$28) drain the meter every frame via `$8957`
(1 unit/frame, 10/frame for anim $24) — so the manual numbers likely count spawn + lingering
drain; exact name→index mapping (which of $54=1/2/3 is Heavy/Thunder/Arrow) is **unverified**
(needs CHR/HUD icon decode or the pause menu in bank4 `$8F00`).

Energy HUD drain while idle: `$05E6` (right column) decrements each frame; at `$6F` refill
from `$05E4` (left column) with `$05E6=$79`; both at `$70` → death (§6). (This is the
"energy = timer" bar; refills are pickups only.)

## 8. Player animation numbers ($FA78 sets $0420; table bank5 $B429/$B4F0)

Entry format: `[lastFrameIdx(+bit7 flags), duration, frameId0, x, frameId1, x, ...]`
(frameId indexes the metasprite frame tables `$9B0B/$9C45`, or `$9C0B/$9D45` when bit7 of
byte0 set). Durations are frames per step.

| anim | entry | frames × dur | role (from code) |
|---|---|---|---|
| 01 | $AFF3 | 1 × 8 | stand |
| 03 | $AFF9 | 5 × 6 (cycle wraps 1-4, `$CC84`) | walk |
| 04 | $B000 | 3 × 4 | look up / reach up (also hang pose) |
| 05 | $B005 | 3 × 4 | fire (standing) |
| 06 | $B00A | 2 × 4 | crouch |
| 07 | $B00E | 3 × 4 | crouch-fire |
| 08 | $B013 | 2 × 8 | stunned/squash (state 5, cutscene 19) |
| 09 | $B017 | 1 × 8 | hurt (fresh hit) / death |
| 0B | $B01F | 1 × 8 | hurt falling / recover |
| 0C | $B022 | 3 × 4 | hurt flying (air) |
| 0D | $B027 | 4 × 4 | jump reaching up (Up held / hook grab, loops 1-3) |
| 0E | $B02D | 2 × 4 | fire in air, rising |
| 0F | $B031 | 2 × 4 | fire in air, falling |
| 11 | $B038 | 1 × 8 | jump (neutral) |
| 12 | $B03B | 1 × 8 | forced-hop / transition / grabbed |
| 13 | $B03E | 3 × 4 | normal gas projectile |
| 14 | $B043 | 6 × 2 | projectile burst / stuck |
| 16 | $B050 | 2 × 4 | weapon-1 projectile |
| 17 | $B054 | 1 × 8 | thunder stuck bolt |
| 18 | $B057 | 3 × 4 | full traveling gas cloud |
| 19 | $B05C | 2 × 4 | weapon-3 projectile |
| 1A | $B060 | 2 × 4 | weapon-3 projectile 2 |
| 29 | $B097 | 2 × 6 | lightning strike |
| 2C | — | (state $12 spawn pose) | materialize at checkpoint |
| 2D | $B0B6 | 44 × 4 | carried by end-of-level enemy |

Frame ids observed (low byte of each pair): stand=01, walk=02/05/04/04/06, up=02/07/04,
fire=02/08/08→09/0A, crouch=09/0A→0B, crouchfire=0B/0B/0B, stun=0C/0D→08, hurt=0E,
hurtfall=11, hurtfly=12/13/13→14, jumpup=14/17/18/17→19, airfire=19/1A→1A, jump=15,
hop=16.

## 9. Key RAM (player)

| addr | meaning |
|---|---|
| `$50` | player state machine id (§2) |
| `$51` | state timer (stun/knockback $0F; cutscene counters) |
| `$52` | latched direction bits (`$16&3`) |
| `$53` | active weapon index 0-3 (copy of `$54` while gas on) |
| `$54` | selected weapon index (set by pickups) |
| `$55` | vertical phase scratch (0 while falling) |
| `$56` | air sub-state: 1 = Up held (reach pose), 2 = fired in air |
| `$58` | state saved across hurt |
| `$5A,$5B,$5C,$43` | counters/flags: hop timer, pending level event id, landed flag, hook variant flag |
| `$5D` | deaths/continue counter |
| `$5F` | gas active (1) |
| `$60/$61` | saved gas energy (hi/lo digit) |
| `$64` | death flag-count → game over |
| `$65` | level active (negative = inactive) |
| `$66` | death flag (set by level scripts) |
| `$67/$68` | frame counters (level timers) |
| `$6D` | checkpoint id (script $C1-$CF) / door target from type-5 tile |
| `$6E/$6F` | player previous X (collision clamp reference) |
| `$70/$71` | spawner cursors; `$7C` | invincibility frames ($3C = 60) |
| `$9B` | energy-warning latch; `$9D/$9E` | hop counter / wall-blocked flag |
| `$98` | gas gun on/off toggle |
| `$99` | gravity fraction, $40 every frame |
| `$0470/$0530/$0540` | X fraction / X vel frac / X vel int (slot 0) |
| `$0460/$0550/$0560` | Y fraction / Y vel frac / Y vel int (slot 0) |
| `$04F0/$0500, $0510/$0520` | player abs X, abs Y (16-bit) |
| `$0400` | slot-0 flags: $80 active, $40 facing right, $20 on-hook-tile this frame, $10/$04 misc, bit5 cleared+set by tile probes each frame |
| `$0410/$0420/$0430` | anim frame idx / anim number / anim timer |
| `$05B0-$05BF` | dynamic RAM metasprite template 31 (also hides the 4-hit HP ladder $82/$83 codes) |
| `$05BE/$05C0` | active gas energy hi/lo digit |
| `$05E4/$05E6` | energy HUD columns (tile codes $70-$79) |

## 10. Open questions / unresolved

1. Weapon name ↔ index mapping ($54 = 1/2/3 vs Heavy/Thunder/Arrow) — needs HUD icon CHR
   decode ($05BA tiles $7A/$7B/$7C) or bank4 $8F00 pause menu.
2. Manual cost mismatch: code = spawn drain 1/2/1 (+ gate 2/3/2) + lingering drain 1/frame
   (10/frame for anim $24); manual says 2/2/3. Unit semantics of the 2-digit meter vs the
   manual's "shots" not fully reconciled.
3. Who spawns behavior-8 lingering clouds (the drain path `$8837→$89D6`) — behavior id 8 not
   seen in `$CF2E`; likely set by an anim-end hook or the burst routines' second spawn.
4. Arrow flight: behavior 4 shows no velocity integration — the visible projectile motion for
   weapon 3 is still unexplained (possibly a different behavior gets set after spawn, or the
   anim-driven template moves it).
5. Crouch drop-through ($D166) sets state 1 with downward velocity — exact interplay with the
   type-2 landing rule (why it doesn't re-land next frame) not traced to the end.
6. Ice friction: no player-code branch found for low-friction surfaces; if present it lives in
   the level scripts or bank4 level code.
7. `$10 == 4` damage threshold: overlap math ($F699/$F6DF) gives penetration 1-17 clamped to
   4; the exact pixel overlap that reaches 4 (i.e. the real hitbox sizes) not yet tabulated
   per enemy.
8. MMC1 note: all PRG switches observed as 5 writes to `$FFF0` (register 4) — nonstandard
   wiring (CHR regs at $BFFF/$DFFF); irrelevant to mechanics but worth documenting in the
   mapper notes.
