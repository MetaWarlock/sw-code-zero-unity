# Darkwing Duck (NES) — Enemy & Boss Behavior Catalog

Reverse-engineered from `prg.bin` (banks 0-7; bank N at file offset `N*0x4000`).
Generated 2026-10-11 by the enemy/boss research agent. Companion files:
`object_catalog.csv` (per placed type), `labels_enemies.json` (named addresses).
Related: `docs/player_mechanics.md` (player states, weapons), `docs/game_flow.md`, `FACTS.md`.

All addresses are CPU addresses. **Bank 6** ($8000-$BFFF when switched) holds the
enemy/gas behavior code; **bank 1** holds the boss code; both are dispatched from
the per-frame main loop `$C9AC` (bank 7) in the order: enemies → bosses → object update.

---

## 1. Dispatch architecture

### 1.1 Enemy dispatch — bank 6 `$8000` (`Enemy6_Dispatch`)

```
$99 = $40            ; gravity fraction for this frame (same value set by boss dispatch)
X = 1 .. 15          ; object slots (0 = player)
loop per slot:
  $04D0,X bit7 clear            -> skip (inactive)
  Y = $04E0,X (behavior id)
  id >= $47                     -> skip (bank-1 boss dispatch handles these)
  target = $825F[Y] | $82A7[Y]<<8   ; jump tables, bank 6
  push fake return $8027, JMP target
$8027: INC $4D ... next slot
```

**Full behavior table (bank 6, ids `$00-$46`)** — target addresses (verified from ROM bytes,
see `extracted/behavior_tables.json`):

| id | addr | id | addr | id | addr | id | addr | id | addr | id | addr | id | addr |
|----|------|----|------|----|------|----|------|----|------|----|------|----|------|
| 00 | 82EF | 0A | 8B80 | 14 | 92E6 | 1E | 9ABE | 28 | A8BB | 32 | B007 | 3C | AEBC |
| 01 | 82F0 | 0B | 8BDA | 15 | A41E | 1F | 8812 | 29 | 9E85 | 33 | AAD1 | 3D | B1D3 |
| 02 | 8385 | 0C | 8C34 | 16 | 90D3 | 20 | 9A03 | 2A | 9DBD | 34 | ADF2 | 3E | A28F |
| 03 | 841C | 0D | 8C34 | 17 | 9424 | 21 | 9F4C | 2B | A9BF | 35 | AC2C | 3F | 8A22 |
| 04 | 85BC | 0E | 8DAF | 18 | 950B | 22 | 9F4C | 2C | AA82 | 36 | A28F | 40 | B2FC |
| 05 | 86A2 | 0F | 8EA3 | 19 | 92E6 | 23 | 9BC9 | 2D | A53E | 37 | 965D | 41 | B2FC |
| 06 | 87E2 | 10 | 906A | 1A | 954B | 24 | 9C8A | 2E | A00A | 38 | A41E | 42 | B483 |
| 07 | 85FF | 11 | 90D3 | 1B | 95B8 | 25 | A6B4 | 2F | ABBE | 39 | A164 | 43 | B570 |
| 08 | 8837 | 12 | 9265 | 1C | 92E6 | 26 | A09C | 30 | AD5B | 3A | 9FCB | 44 | 82EF |
| 09 | 8A1A | 13 | 92E6 | 1D | 98EE | 27 | A7D9 | 31 | 883F | 3B | A631 | 45 | 82EF |
| —  | —    | —  | —    | —  | —    | —  | —    | —  | —    | —  | —    | 46 | 9822 |

Ids `$00`, `$44`, `$45` are `RTS` no-ops. Behaviors `$01-$04` + `$08` are the player's gas
weapons (already named in `labels_player.json`); the rest are enemies/debris/pickups.

### 1.2 Per-slot post-processing (bank 6 `$8032-$816C`, runs before each behavior)

1. `$803C` **hit-flash**: if `$04C0,X` (flash timer) != 0: toggle `$0400,X` bit `$08`
   (sprite visible bit) from bit 3 of the timer, `DEC $04C0,X`. `$04C0 = $0F` is set
   whenever an enemy takes a gas hit.
2. `$8066` `JSR $FAAE` — **player-projectile overlap** (see §6). If a projectile overlaps:
   - ids `$1E`/`$34` (falling spikes): only take hits while below the player top (custom check `$8079`).
   - `$8096` **weapon damage**: `SFX $1D` (hit), then `DEC $05A0,X` by weapon power
     taken from the *projectile's* anim number `$0420,Y` (Y = projectile slot `$0F`):
     | projectile anim | damage |
     |---|---|
     | `$13 $14 $16 $19 $1A` | 1 |
     | `$18` | 2 |
     | `$17 $29` | 3 |
     HP ≤ 0 → `$80F2` death. Otherwise `$04C0,X = $0F` (flash), spawn hit-puff
     (`$F872` = object anim `$14`, behavior 0), `JSR $F872`/`$F872`.
   - `$80F2` **death**: `$04D0,X = $80` (keep active, clear state), `$0590,X = $FF`,
     `$04E0,X = $05` (death-pop behavior), rise velocity `$0550,X = $44 / $0560,X = $03`,
     `SFX $1E` (enemy destroyed). Behavior `$05` ($86A2) then pops the corpse up, spawns
     1-2 debris (behavior `$0A`, `$8715`, velocities from RNG table `$87A2`/`$87C2`), falls off screen.
   - **Special deaths** `$81C0`: ids `{0F,17,1D,2A,30,32,34,42}` don't explode; when killed by
     the thunder/strong projectile (player anim `$14/$16`) they are *converted*: cleared, or
     re-tasked to behavior `$1F` (`$8812` "friendly", pops up and falls) — gas-lingers that
     touched them become pickups via `$08`.
3. `$813B` **enemy→player contact**: skipped while `$7C != 0` (player invuln timer) or
   `JSR $FB7B` says not touching. If touching: `SFX $15`, `$7C = $3C` (60 frames),
   `JSR $F9C2` (player hurt/knockback). Special anim ids (`$40 $4E $9F $7D $62 $55 $A6`)
   do a distance check (`$F928 |dx| < 8`) and can instead *bounce* the player
   (`SFX $1A`, retask enemy to behavior `$06`) — spiky armor enemies.

### 1.3 Boss dispatch — bank 1 `$8800` (`Boss1_Dispatch`)

Identical structure; dispatches ids `>= $47` through `$8836`/`$88B6` (bank 1) with fake
return `$8827`. Verified table (ids `$47-$7F`):

| id | addr | role | id | addr | role |
|----|------|------|----|------|------|
| 47 | 8936 | RTS | 5B | AC90 | Megavolt shot A |
| 48 | 8989 | hop-lift (fowl) | 5C | ACF2 | Megavolt shot B |
| 49 | A5F7 | **Bushroot** | 5D | 94E2 | orb-volley child |
| 4A | A12C | **Moliarty** | 5E | 9350 | orb-spawner core |
| 4B | A846 | forest teleport shooter | 5F | AF91 | Steelbeak minion A |
| 4C | A4BD | flamethrower 1/2 | 60 | AF91 | Steelbeak minion B |
| 4D | A4BD | flamethrower 1/2 | 61 | B1D4 | Steelbeak minion C |
| 4E | A4B3 | flamethrower 3 | 62 | AD6F | **Steelbeak** |
| 4F | B3F8 | **boss defeat pop** | 63 | 933D | orb spawner (forest) |
| 50 | 9736 | **Quackerjack core** | 64 | AD23 | Steelbeak flame |
| 51 | 9836 | **Quackerjack ball** | 65 | 8CE6 | spawner |
| 52 | 97E6 | Quackerjack child | 66 | 8937 | ball child walker |
| 53 | 9D4C | Wolfduck controller | 67 | 8CE6 | spawner |
| 54 | 9971 | **Wolfduck** | 68 | 8E7A | stalactite drop |
| 55 | 9BEC | Wolfduck child | 69 | 9694 | grabber verifier |
| 56 | 8CE6 | spawner | 6A/6B | 90C5 | tower riser |
| 57 | 9DA7 | **Liquidator** | 6C | 923E | swinger child |
| 58 | 9FDE | Liquidator geyser | 6D-6F | 8989 | hop-lift variants |
| 59 | A05A | Liquidator geyser 2 | 70-7D | 96C9 | **mission-complete door** |
| 5A | A977 | **Megavolt** | 7E | 9083 | door child |
| — | — | — | 7F | 8F71 | fowl droid (city `$27`) |

Ids `$80-$83` read garbage (`$6089/$BD89/$D089/$0489`) — table ends at `$7F`.

**Boss ids skip the enemy post-processing block** (`$8032` still runs for them: hit-flash,
projectile overlap and player contact apply, but the weapon-damage branch at `$8096` is
guarded by `LDA $05A0,X / BPL` — bosses carry `$05A0 = $80` in the type table, so gas
cannot kill them through the generic path; they use their own `$B39E`).

### 1.4 Boss shared framework (bank 1)

| routine | role |
|---|---|
| `$B39E` `BossTakeProjectileHit` | `$FAAE` overlap + same 1/2/3 damage rule as enemies, but damage accumulates in **`$EE`** (global). `SFX $1D`, hit puff `$F872`, sets `$ED = $1E` (30-frame invulnerability), `$EF` = projectile slot. Returns CS while `$ED != 0`. |
| `$B36A` `BossHitFlashToggle` | blinks boss sprite from `$ED` (same pattern as `$04C0`); **when `$EE >= $10` (16 damage) → `JMP $B315`**. |
| `$B315/$B317` `BossDefeat` | sweeps slots 15..1: kills every object except the boss itself and ids `$51` (Quackerjack ball), `$54` (Wolfduck), `$57` (Liquidator) which persist; sets this boss's behavior to `$4F`, clears timers, calls `$F607` (knockback), player state ← `$0F` (unless 6/7). |
| `$B3F8` `BossBeh4F` | defeat pop: rise `$0550=$44/$0560=$03`, `$0400=$D0`, `SFX $1E`, walks while falling; at `Y >= $E0` → player state `$15`. |
| `$3B` | global "boss death cinematic" timer; every boss routine checks it first and only runs `$F880` (restore player idle anim per world, table `$F893`) + `$F869` while it counts down. |
| `$B454`/`$B487` | clear arena edge metatile columns `$0600-$06FF` (open the boss room). |
| `$B4BA` | child-object projectile check: weak projectiles (anim `$14/$16`) are destroyed; others are *converted* to behavior `$1F` (`$B507` remaps `$0580` `{1,9,5}→$0A`, `{2,A,6}→$09`), `SFX $1A`. |
| `$FA4C` | find free slot (returns Y=$0F and C=1 if none). |
| `$FA84` | spawn object at slot Y with anim A. |
| `$FC29` | mark placement index `$0590,X` in the `$0340` "already spawned" bitmask (no respawn). |
| `$FA9A` | kill slot X. |

**Universal boss HP = 16 damage points** (`$EE >= $10`), i.e. 6 strong (3-dmg) / 9 medium /
17 weak gas hits, refreshed invulnerability 30 frames per hit.

---

## 2. The seven bosses

Boss identity ↔ behavior ↔ placed type ↔ mission (mission names from `levels.json`
order 0-6; name mapping corroborated by published guides — see sources at bottom).

### 2.1 Quackerjack — The Bridge (mission 0)

| object | type | behavior | addr | anim | HP |
|---|---|---|---|---|---|
| Quackerjack core | 73 ($49) | `$50` | `$9736` | `$92` | `$80` (via `$EE`) |
| Bouncing ball | 74 ($4A) | `$51` | `$9836` | `$99` | **0 — destroyable by one gas hit** |
| Ball child | spawned | `$52`/`$66` | `$97E6`/`$8937` | `$33` | 0 |

- **Core `$9736`**: state table `$9670/$9677`. State 0 `$967E`: init (`$0550=0 $0530=0
  $0560=1 $0540=1`, timer `$0480=8`, `$0490=$0A`), reads `$F93F` (24-sector direction to
  player), `AND #$08` → bumps `$04A0,X`. State `$96A6`: waits until `$F928 |dx| >= $60`
  (96 px), then `$05A0,X = 8`, next state. `$96B6/$96C2`: **teleports to the player's X**
  (`$04F0,X ← $04F0`) and re-arms `$0490 = $0A`; throws via `$97B0` (spawn child anim `$47`,
  behavior `$52`, param `$0A`, at own position) on the `$0490` cadence; `SFX $2A` every
  4th frame while active; anims `$A5` (idle) / `$A4` (attack, toggled by `$0490` bit 0).
- **Ball `$9836`**: `$3B`-gated; `JSR $B36A/$B39E` (can be destroyed — `$05A0 = 0` →
  generic death path); bounces with velocity pairs from RNG table `$9959/$9961/$9969`
  (`E5 += E4`, mask 7): `{8E/AB/06/FF, 06/FF}` — vertical vel ∈ {−82, −85, +6, …} × X vel
  {+6,−6,+2,−2}; jumps using `$F2D7` (vel `$28`), lands with probe offset `$40=$56,$41=2`
  + `$EDD2` snap; anims `$99` (roll) / `$9A` (land) / `$AB` (charge).
- **Door**: types 27/28 → behaviors `$70/$71` (§2.8).

### 2.2 Wolfduck — The City (mission 1)

| object | type | behavior | addr | anim |
|---|---|---|---|---|
| Wolfduck | 76 ($4C) | `$54` | `$9971` | `$6B` |
| Panic controller | 75 ($4B) | `$53` | `$9D4C` | `$6D` |
| Runner child | spawned | `$55` | `$9BEC` | — |

- **Wolfduck `$9971`**: state table `$9984/$9989` → `$998E` init (`$0530=$E6 $0540=3
  $0550=$8E $0560=6 $0480=$14`, `$B454`); `$99AE` walks (`$F200/$F254` speed 3) and jumps
  (`$F2D7` vel `$56`), turning at walls (`$04FF >= 4` → anim `$67`, `SFX $35` throw cue);
  anim frame `$0D` → `$1A = $8A` (screen effect trigger); frame `$08`+timer → `E6 += E7`
  RNG mask `$0F` → `$9A1F` table `{82,82,84,84,82,82,84,84,82,82,84,84,82,82,84,84}`
  chooses state `$82` (throw) or `$84` (charge) with anim `$6C`/`$68` — **the panic/charge
  cycle**; `$04A0,X = $3C` invuln timer between phases.
- **Controller `$9D4D`**: pure countdown (`$0480=$B4`, `$0490=$78`), re-arms `$0480=$B5`
  when expired — paces the fight.
- **Child `$9BEC`**: runs left/right (`$0490` bit0), hops `$F2D7` vel `$56`, throws at
  frame 1/timer 2 (`$F81D`, `JSR $9B93`), turns at `X = $E8/$18`; killed by `DEC $04AF`
  when it exits.
- **Door**: types 29 (`$72`) + 89 (`$73`).

### 2.3 The Liquidator — The Sewers (mission 2)

| object | type | behavior | addr | anim |
|---|---|---|---|---|
| Liquidator | 77 ($4D) | `$57` | `$9DA7` | `$4C` |
| Water geysers | spawned | `$58`/`$59` | `$9FDE`/`$A05A` | — |

- `$9DA7`: state table `$9DBA/$9DBF` → `$9DC4` init (`$0530=0 $0540=1`, `$B454`,
  `$0480=$0F`); walk cycle `$9E3A` (`$0490` timer, `$F24B/$F2A6 + $F4FD/$F51B`), turns at
  arena marks `X = $C0/$80/$40` → `$9F43`: `E6 += E7` RNG mask `$0F` →
  `$9F5F` walk-time `{00,30,00,30,…}` (0 or 48 frames), `$9F6F` anim `{41,42,41,42,…}`,
  `$9F7F` timer `{00,01,…}`; attack anims `$41/$42` (water blast), frame 2/timer 8 →
  anim `$44` + `$0570=6`; frames 3/4 → `SFX $37`; `$9EC0`: if `$04A0,X == 0` and frame
  ∈ {5,8,$0B} at timer 4 → `$9F8F` **spawn geyser** at player column (`$04F0/$0500` of
  player), Y `$F0`, anim `$45` (or `$46` when `$0480 != 0`), behavior `$59`/`$58`,
  `$0570=$44`, HP 1 — a water column that erupts from the floor.
- **Door**: types 90/91 → `$74/$75`.

### 2.4 Moliarty — The Tower (mission 3)

| object | type | behavior | addr | anim | HP |
|---|---|---|---|---|---|
| Moliarty | 71 ($47) | `$4A` | `$A12C` | `$48` | `$80` (via `$EE`) |
| Flamethrower 1 | 70 ($46) | `$4C` | `$A4BD` | `$56` | **13** (`$05A0=$0D`) |
| Flamethrower 2 | 84 ($54) | `$4E` | `$A4B3` | 0 | **13** |
| Flamethrower 3 | 85 ($55) | `$4D` | `$A4BD` | `$56` | **9** (`$A4D4`: slot `$0D` gets 9) |

- **Moliarty `$A12C`**: prologue `$A3F5` (`$B36A/$B39E`); state table `$A490/$A49A`.
  `$A148`: init — `$04BD=0`, `$B487`, `$04BC=1`, `$04BD=$0E`, `$04AC=8`, `$A414`.
  `$A166/$A170`: walks toward the post of flamethrower slot `$04AE` (`$A3D8` scans slots
  `$0C-$0E` for the lit one), steers with `$A296` against `$A4AD/$A4B0` X tables
  {`$20,$20,$E0,$E0,$88?`…}; at post → `$A2C9` (anim `$4B`, state `$87`, `$04B0=$78`).
  `$A318/$A1F8`: jump arcs (`$F2D7` vel `$06`) between floor levels; `$A33E`: **if he lands
  on a lit flamethrower slot** (`$A34D`: X == `$04AE` and its `$0480 != 0`) → douses it:
  `$0480[slot]=0`, `$05A0[slot]=$0D`, anim `$56`, `SFX $2C`. Throws bombs via `$A443`
  (spawn anim `$40`, **behavior `$01`** — the gas-travel projectile routine used as an
  enemy bomb, param `$0A`) whenever his jump timer `$0490` matches `$0480,X` (`$A1AC`).
- **Flamethrowers `$A4BD/$A4B3`**: `$A51E` round-robins `$04BC` (100-frame period);
  `$A54E` shoots a flame: spawn anim `$4D`, behavior `$0B` (8-dir mover), param `$13`,
  velocity from the `$F93F` 24-sector direction code via tables `$A5B7/$A5C7/$A5D7/$A5E7`
  (`$0530/$0540/$0550/$0560/$0580`), `SFX $29`. Damage them via the **generic enemy path**
  (their `$05A0` is 13/9 — positive!) → destroyed at HP < `$0A` (`$A503`: anim 0,
  `$0480=1`, `$05A0=$80`).
- **Door**: types 92/93 → `$76/$77`.

### 2.5 Bushroot — The Forest (mission 4)

| object | type | behavior | addr | anim |
|---|---|---|---|---|
| Bushroot | 79 ($4F) | `$49` | `$A5F7` | `$4F` |
| Teleport shooter add | 72 ($48) | `$4B` | `$A846` | 0 |
| Vine snappers | 35/36 | `$21`/`$22` | `$9F4C` | `$30` |
| Orb spawner | 104 ($68) | `$63` | `$933D` | `$1E` |

- **Bushroot `$A5F7`**: hit check `$A7D2` (saves `$EE`, `$B36A`, `$B39E`; a hit sets
  `$04B0,X = 1` unless `$04A0 == 2`). Hop loop `$A630`: anim `$50`, `DEC $04AE` (hop
  counter from 8), hops with `$F2D7` vel `$56`; on hops `$0490` ∈ {6,4,2} → `$A686`:
  RNG `$A7AB` (`E5 += E4 - $0490`, mask 3) table `$A83F {00,01,00,01}` → 50% chance to
  skip into the **ceiling hop** `$A6BB/$A6E1`: rises `$0550 += $40`/frame until above the
  ceiling table `$A843[$04A0]`, then probes up (offset `$40=$56`); on head-bonk → falls,
  `DEC $04A0`, `DEC $04D0` twice; at `$04A0 == 2` → `$A754`: `INC $04D0`, `$04B0=$80`,
  anim `$51`, sets globals `$048E=$88`, `$042E=$52` (**drops vines/branches objects**),
  then `$A777` counts `$04B0` down and returns to hopping. Walks between `X = $F0/$10`.
- **Teleport shooter `$A846`**: every `$0480` expiry (values `$6F/$41/$15` re-arm) spawns a
  flyer (anim `$4E`, behavior `$0B`, param `$0A`, `SFX $3A`) aimed by the `$F93F` sector
  tables `$A923/$A924/$A927…`; otherwise teleports: `E5 += E4` mask `$0F` → wait table
  `$A90B {00,01,02,03,…}`, position table `$A91B/$A91F` {`$20/$E0/$50/$98` × `$E0/$98`}.
- **Orb spawner `$933D`** (also behavior of type 104): when `|dx| < $18` and `|dy| < $10`
  (`$F928`/`$F919`), sets `$9E = $FF`; fires volleys `$93D6/$9440`: `E4 += E5 EOR $04F0`
  → `$E4/$E6`, mask 7 → `$946E {00,01,02,03,04,05,01,04}` (no immediate repeat via
  `$049F`), spawns from 6 fixed points `X {18,18,18,$E8,$E8,$E8}` `Y {$28,$58,$88,…}`
  (tables `$94D6/$94DC`), child anim `$3D`, **behavior `$5D`** (orb), param `$10`,
  max 5 alive (`$04AF`).
- **Door**: types 94/95 → `$78/$79`.

### 2.6 Megavolt — The Warehouse (mission 5)

| object | type | behavior | addr | anim |
|---|---|---|---|---|
| Megavolt | 78 ($4E) | `$5A` | `$A977` | `$57` |
| Shots | spawned | `$5B`/`$5C` | `$AC90`/`$ACF2` | — |
| Door-adjacent spawner | 103 ($67) | `$67` | `$8CE6` | `$33` |

- **Megavolt `$A977`**: init `$A97E` (`$0490=$1E`, `$B454`); states via `$A9BF/$A9C8`:
  `$A9D1` (anim frame 9/timer 2 → anim `$59`, next state), `$A9E8` **walks** between
  `X = $F0/$10` (speed 3), at `X = $DC/$24` → next state, anim `$58`, jump vel
  `$0550=$8E/$0560=6`; `$AA3E` rises (`$F5C9`); falling + floor probe (offset `$40=$56`)
  → `$EDD2` snap + `$AA69`: `E6 += E7` mask `$0F` → `$AA86 {00,FF,00,FF,…}` picks attack
  (`SFX $3B` when attack) and anim `$58`/`$5A`; `$AAA6` attack phase: `$0490 = $1E`
  re-arm, RNG `$AAEC {00,FF,FF,00,…}` → pattern A `$AC20` (anim `$5C`) or B `$AB9B`
  (anim `$5B`), `SFX $3C`, state `$86`; `$AF52`-style `$04A0` counter paces the barrage.
- **Shot `$AC90`**: falls with `$F55E`, at `Y = 0` copies the player's X (`$04F0/$0500 ←
  player`) and spawns an orb (anim `$5D`, **behavior `$5C`**, flags `$D8`).
- **Shot `$ACF2`**: rises 7/frame for `$1E` frames, then param `$0570=2`, flags `$D0`,
  `JMP $F539` (rise) — the aimed energy orb.
- **Door**: types 96/97 → `$7A/$7B`.

### 2.7 Steelbeak — Fowl Fortress (mission 6)

| object | type | behavior | addr | anim |
|---|---|---|---|---|
| Steelbeak | 80 ($50) | `$62` | `$AD6F` | `$60` |
| Minion A/B | 81/83 ($51/$53) | `$5F`/`$60` | `$AF91` | `$61` |
| Minion C | 82 ($52) | `$61` | `$B1D4` | `$3E` |
| Flame | spawned | `$64` | `$AD23` | — |
| Cage droid (city `$27`) | 39 ($27) | `$7F` | `$8F71` | `$A4` |

- **Steelbeak `$AD6F`**: state table `$AD82/$AD88` → `$AD8E` descend: falls (`$0560=0
  $0550=$70 $0530=$80 $0540=2`), if `$63 != 0` also `$F539`; lands at `Y=$70` (clears
  flag bit `$20`), **at `Y=$B0` → anim `$5F`, `$1A=$8B` (screen flash), `$0490=$1E`,
  `$04A0=$3C`, next state**. `$ADD6` walk (`$F24B/$F2A6`) between `X = $E0/$20` with RNG
  hop gate `$AF74` (`E5 += E4` mask `$0F`, table `$AF81 {00,FF,FF,00,…}`); `$AE22` frame
  8 → anim `$63` (wind-up); `$AE49` walks + `$B4BA`, at `Y=$B4` → next; `$AE8D` waits on
  `$0490`, then `SFX $3D`, anim `$66`, spawns **two flames** (`$AEC5`: anim `$64`,
  behavior `$0B`, param `$10`, offset `±$12`, speed from `E4 += E6` mask `$0F` table
  `$AF2D {02,03,07,04,02,02,06,03,…}`, `SFX $5F`), state `$86`; `$AF3D` waits `$04A0`
  (60) frames then returns to walk. Damage via `$B39E`/`$EE` (HP=$80).
- **Minions `$AF91`** (two placed instances, anim `$61`): state table at `$AFB6+`;
  simple patrollers that throw (children `$B1D4`).
- **Door**: types 98/99 → `$7C/$7D` — final door ends the game (script tables below).

### 2.8 Mission-complete doors — behaviors `$70-$7D` → `$96C9`

Placed types 27-29 and 89-99 (two per mission except city/bridge which have extras).
`$96C9`: while `state & $0F == 0`, waits for `JSR $FAAE` (a projectile to touch it —
i.e. you must shoot the door); then `$0400,X = $90`, `SFX $1C`. On the next state,
`JSR $FB7B` (player walk-in) → **mission complete**: `SFX $23`, player state `$50 = $14`
(20 = level complete), then loads from tables indexed by `id & $0F`:

| id | `$5B` ← `$970C` | `$9C` ← `$971A` | `$66` ← `$9728` |
|----|----|----|----|
| $70 | $0F | $10 | $FF |
| $71 | $11 | $12 | $F0 |
| $72 | $13 | $14 | $FF |
| $73 | $15 | $16 | $F0 |
| $74 | $17 | $18 | $FF |
| $75 | $19 | $1A | $F0 |
| $76 | $1B | $1C | $FF |
| $77 | $1D | $1E | $F0 |
| $78 | $1F | $20 | $FF |
| $79 | $21 | $22 | $F0 |
| $7A | $23 | $24 | $FF |
| $7B | $25 | $26 | $F0 |
| $7C | $27 | $28 | $FF |
| $7D | $29 | $2A | $F0 |

`$5B`/`$9C` = next-script/cell pointers (odd = checkpoint per player doc), `$66` = mission
flag (`$FF`/`$F0` alternate). Then `$FC29` + kill slot. Behavior `$7E` (`$9083`) is the
door's spawned child (mini door prop).

---

## 3. Object type catalog (placed types)

See **`object_catalog.csv`** for all 105 types. Data sources:

- **Spawn**: bank 4 `$8090` (spawner) → `$80FC` spawn routine. Placement arrays are five
  parallel lists (screenX, localX, screenY, localY, **type**) pointed to by `$72/$74/$76/$78/$7A`.
- **Type tables (bank 4)**: `$8AB3` — 105 records × 4 bytes `[flags, behaviorId, animId, param]`
  → loaded to `$0400,X` / `$04E0,X` / `$0420,X` / `$0570,X`; and `$8C57` — 105 × 4 bytes,
  first byte = starting HP → `$05A0,X`. Both tables are exactly 105 entries ($8AB3-$8C56,
  $8C57-$8E56) matching the 105 placed ids from `manifest.json`.
- `$0570,X` param: **low 5 bits = touch-class index** into the hitbox tables (§6); upper
  bits are reused as scratch by behaviors. `$0400` flags: bit7 = active/on-screen, bit6 =
  h-flip, bit4 = overlap-calc path, bit3 = hit-blink clear, bit2 = "projectiles pass
  through" (checked at `$FAE4`).
- **HP `$05A0` semantics**: `$80` (128) = gas-immune (bosses); 0 = dies to one hit;
  1-15 = normal enemies (flamethrowers 13/9, heavy shooter type 33 = 10, type 100 = 15).
- Type `$FF` (255) appears in placement arrays as the **terminator/empty marker**, not an object.
- Never placed but defined: types 20, 22, 55, 63, 100 (rows flagged `NEVER_PLACED` in the CSV).

Field notes for slots (verified): `$0410/$0430` anim frame/timer, `$0420` anim id,
`$0440/$0450` screen Y/X, `$0460` Y subpixel accumulator, `$0480/$0490/$04A0/$04B0`
behavior-local timers/counters (frequently double as HP), `$04C0` hit-flash timer,
`$04D0` bit7 active + low nibble state, `$04E0` behavior id, `$04F0/$0500` abs X (px/subpx),
`$0510/$0520` abs Y, `$0530/$0540` Y vel int/frac (up-positive), `$0550/$0560` Y vel
int/frac, `$0570` param/touch class, `$0580` direction (bit0 right, bit1 left, bits2-3 flip),
`$0590` placement index ($FF = not placed), `$05A0` HP.

---

## 4. RNG

**No single LFSR.** The generator is a set of four zero-page counters `$E4 $E5 $E6 $E7`
(RAM, zero at power-on) advanced *in place at each use site* with the canonical step
`state[i] += state[i-1] + carry`, then masked by the caller:

```
LDA $E4 / CLC-ish / ADC $E5 / STA $E5        ; E5 += E4+C   (most common; e.g. $8D2A, $94FA, $940D, $A03C, $A7AB, $A864, $AF74, $B119)
LDA $E4 / ADC $E6 / STA $E6                  ; E6 += E4+C   ($AF0F, $9613 variant)
LDA $E6 / ADC $E7 / STA $E7                  ; E7 += E6+C   ($AA69, $AAB7, $9EE9-region)
LDA $E4 / ADC $E5 / SBC $E6 / STA $E4 / EOR $E7 / STA $E6   ; mixed ($9613-961D, Quackerjack)
LDA $E4 / ADC $E5 / SBC $0490,X / STA $E5    ; perturbed ($A7AB, Bushroot)
LDA $E4 / ADC $E5 / EOR $04F0 / STA $E4 / STA $E6           ; ($9447, orb spawner)
INC $E6 / INC $E7                            ; cheap bump (bank 6 $BAD0)
```

All sites were found by scanning every bank for opcodes touching `$E4-$E7`
(see `Tools/DarkwingResearch` output in the run log): 8× `ADC E5` sites, 2× `ADC E7`,
1× `ADC E4`, 1× `ADC E6`, 1× `INC E6/E7` pair. **Seed**: RAM init (all zero); state is
therefore deterministic per run and advances only when one of these sites executes
(no per-frame tick found). Typical consumption: `AND #$03/$07/$0F/$1F` → table index
(drop tables, attack pattern choice, geyser columns, hop selection).

Call-site classes: boss attack pattern choice (Wolfduck `$99EE`, Megavolt `$AA69`,
Liquidator `$9F43`, Bushroot `$A686`, orb spawner `$9440`, Steelbeak `$AF74`), Moliarty
hop decisions, enemy flash timers (`$8D2A`, `$94FA`, `$9648`), death debris velocities
(`$8715`), flamethrower shot aim (uses `$F93F` sectors, not RNG).

---

## 5. Enemy ↔ player contact

### 5.1 Pairing rule `$F612`/`$F64A` (push-out, called from player state handlers)

```
Y = X EOR 1     ; the partner slot — with X = 0 (player) this is slot 1
```
Both slots must be active (`$04D0` bit7), on-screen (`$0400` bit7) and partner behavior
`$04E0,Y != 0`. `$F699` computes |dx| (16-bit; uses `$0400,X` bit4 to pick which slot is
the reference), `$F6DF` |dy|. Horizontal push applies when overlap < `$11` (17 px),
vertical when < `$18/$19` (24/25 px) — the returned value is the penetration depth used
to push the player out (clamped; see player_mechanics.md §contact). Called from player
walk (`$F20B`), crouch (`$D018/$D0B8`), air (`$D6B1`), hurt (`$D1AE`), transport (`$D59F`),
fall (`$F3D2`). With X=0 the practical pair is (player, slot 1); other even/odd pairs
`(2,3) (4,5)…` would only matter if the player occupied those slots, so in practice this
is the player-vs-one-enemy push.

### 5.2 Projectile → enemy damage `$FAAE`/`$FB08`

For projectile slots Y = 4..1: both active + on-screen, projectile behavior != `$1F`,
enemy behavior not in {0..4}, enemy on-screen, enemy `$0400,X` bit2 clear.
Then `$FB08`: `|dx| < XSize[$0570,X & $1F]` **and** `|dy| < YSize[...]` → CC = hit.

### 5.3 Enemy → player touch `$FB7B`

Guards: player state != `$07/$0F`, player active, enemy active + on-screen, enemy
behavior != `$07`, `$0570,X != 0` (touch class 0 = no touch). Y-half-height from
`$FBE9[class]` (reduced by 8 if player anim is climb `$06/$07`), X-half-width from
`$FC09[class]`. CC = touching → hurt path (`SFX $15`, `$7C=$3C`, `$F9C2`).

### 5.4 Hitbox tables (half-sizes, px, indexed by `$0570,X & $1F`)

```
$FB39 YSize (32): 0F 0F 0D 0F 07 0B 0F 13 07 0B 04 09 13 07 07 07 0B 13 13 0D 0D 0F 04 13 03 03 03 03 03 03 03 03
$FB59 XSize (32): 0E 0E 0A 12 0E 12 0E 06 06 0E 04 0E 0E 0A 06 0A 0A 0A 12 0A 0C 08 0C 03 0A 02 02 02 02 02 02 02
$FBE9 YHalf (32): 16 0C 12 16 0E 14 16 1C 16 12 0C 10 16 0E 14 0E 12 1A 1A 12 14 16 16 16 0E 0A 0A 0A 0A 0A 0A 0A
$FC09 XHalf (32): 16 0C 12 1A 0F 12 16 10 0C 16 10 1A 12 13 12 12 16 12 12 10 14 1A 16 10 0A 0A 0A 0A 0A 0A 0A 0A
```
(`$FB39/$FB59` are read by `$FB08` for projectile hits; `$FBE9/$FC09` by `$FB7B` for
player touches. Values are compared against absolute deltas, i.e. full hitbox = 2×entry.)

---

## 6. Sound effect ids used by enemy/boss code (via `$F84E`/`$F850`)

| id | context | id | context |
|----|---------|----|---------|
| $12 | hop-lift boss grabs player | $2A | Quackerjack core tick |
| $15 | player hurt by contact | $2B | Wolfduck child spawn |
| $17 | bonus pickup (energy) | $2C | Moliarty douses flamethrower |
| $18 | weapon pickup | $2E | Wolfduck/child phase |
| $19 | bonus pickup (points) | $2E | — |
| $1A | convert/bounce | $35 | Wolfduck throw cue |
| $1C | door shot | $37 | Liquidator water attack |
| $1D | gas hit on enemy/boss | $38 | death-pop child |
| $1E | enemy/boss destroyed | $39 | Moliarty throw |
| $23 | door entered / mission complete | $3A | teleport shooter fire |
| $24 | lob-bomb split | $3B | Megavolt attack pick |
| $27 | patroller HP tick | $3C | Megavolt barrage |
| $29 | flamethrower shot | $3D | Steelbeak wind-up |

---

## 7. Open questions

1. **Behaviors `$15`/`$38` (`$A41E` bank 6)** and the "state machine" family
   `$17-$2F` (`$9424`…`$AD5B`) are documented only at the dispatch/HP/child level;
   per-state timers need a second pass (the state tables and children they spawn are
   listed in `labels_enemies.json`).
2. Boss name ↔ behavior mapping rests on mission placement (strong: each mission's
   placement arrays contain exactly one HP=$80 core object + doors) plus published
   guides; the ROM itself has no name strings. Anim ids per boss are listed for
   sprite-level confirmation against `metasprite_frames.json`.
3. `$9728` door table alternates `$FF/$F0` — the meaning of the two values
   (final-mission vs normal?) is unverified.
4. Type 74 (Quackerjack ball, HP=0): dies through the *generic* path — including the
   `$81C0` special-death list check (id `$51` is not in it, so it explodes normally).
5. `$9E`/`$1A` global flags set by bosses (`$9E=$FF` orb spawner; `$1A=$8A/$8B` screen
   effect) — exact consumer (screen shake? palette flash in bank 5 update) not traced.
6. RNG `$E4-$E7` initial value is RAM-zero, but a mission reload may reseed via
   `Flow_ClearObjRam`-class routines — not verified.
7. Moliarty's third flamethrower HP of 9 comes from a slot-index check (`CPX #$0D`);
   TCRF notes a "broken flamethrower" in the final — likely this one; not confirmed dynamically.

## Sources for boss-mission mapping

- [Darkwing Duck Wiki — NES game (boss/stage assignments)](https://darkwingduck.fandom.com/wiki/Darkwing_Duck_(NES_game))
- [StrategyWiki — Darkwing Duck stage guide](https://strategywiki.org/wiki/Darkwing_Duck)
- [The Cutting Room Floor — Darkwing Duck prototype](https://tcrf.net/Proto:Darkwing_Duck_(NES))
