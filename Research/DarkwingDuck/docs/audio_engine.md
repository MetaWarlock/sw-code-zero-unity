# Darkwing Duck (NES) — Audio Engine Reverse-Engineering

Status: 2026-10-11. Static disassembly (bank 3 fixed-window $8000-$BFFF, bank 7 fixed $C000-$FFFF)
+ dynamic probes (`reference/probe_audio_m1`, `reference/probe_audio_sfx`, 500-600 frames from
`reference/m1_gameplay.mss`). Confidence markers: [V] verified dynamically, [S] static only,
[?] hypothesis / open.

## 1. Where the driver lives

- The whole sound engine is **PRG bank 3** (file offset 0xC000-0xFFFF), executed through the
  MMC1 switchable window **$8000-$BFFF**. All APU writes funnel through one store helper
  (`Audio_WriteReg` $80EC, callback PC $80FD).
- Bank 7 (fixed $C000-$FFFF) only hosts the **service plumbing**: NMI hook, PRG-switch queue
  processors, the enqueue routine and one reset-time init.
- Bank 7 `$C21E` is **not audio** (OAM shadow clear, mislabeled in `labels.json`); `$C276/$C270`
  = palette fade, `$C838` = RAM clear. Ignore them.
- DMC is **unused**: no writes to $4010-$4013 anywhere in PRG; the only `STA $4015` is
  `Audio_EnableAPU` $821A with `#$0F` (pulses+tri+noise, DMC bit clear). Noise channel uses
  the standard LFSR mode. [V]

## 2. Execution model / frame timing [V]

Per frame exactly **one driver tick**, from one of two paths:

1. **NMI path** (bank7 $C000): if no PRG switch is pending (`$F5 == 0`) the NMI itself maps
   bank 3 and calls the driver: `$C0C7..$C0F6` → `Audio_QueueProcess` ($C0DC) →
   `Audio_Task_Queued` ($8003) once per queued task (A = task id) → `Audio_Tick` ($8000) →
   restore PRG from `$F1`.
2. **Game-side path**: game code that switched PRG via `SwitchPRGBank` ($FF55) sets `$F5`;
   the NMI then only increments `$F6` and skips audio; the game finishes the switch with
   `SwitchPRG_ProcessQueue` ($FF6E) which maps bank 3, drains the same queue, calls $8003/$8000
   and restores PRG to `$F4`. Net effect: audio never runs inside a half-switched frame.

The tick (`Audio_Tick` $8000 → $806C):
- `$C0` bit0 set = driver halted → RTS.
- If SFX stream pointer `$D1:$D0` != 0 → `SFX_ParseTick` ($8252) (SFX state machine, below).
- Tempo accumulator: `$C7:$C8 += $C9:$CA` (8.8 fixed point; `$C7` = whole tempo ticks this
  frame, `$C8` fraction). Observed song 1 tempo word = $0133 (ticks ≈ 1.2/frame); default
  after music start = $0199. [V: $C8 increments by $33/frame, carry toggles $C7 1↔2]
- Channel loop X=3..0 (X=3 pulse1, 2 pulse2, 1 triangle, 0 noise):
  - rotate `$CF` right; if bit set → `SFX_ChannelTick` ($82DE) for that channel and **set
    `$CF` bit7** ("channel occupied by SFX this frame"); `$CF` is restored after the loop
    (PHA/PLA at $8087/$80A9), so bit7 is a per-frame transient flag.
  - if `$C0` bit1 clear (music not paused) → `Music_ChannelTick` ($8393).
- Register writes happen inside the envelope routine ($86BA → $8797-$889D) every tick for
  every active channel (dedup only on `$4003` via `$077C-$077F`).

## 3. Command interface (game → driver) [V]

- **Enqueue**: `Audio_Enqueue` ($F84E, bank 7): A = task id, X = preserved. Pushes A into ring
  `$DC[8]` at write index `$DA` if slot != $88, advances `$DA`. `$D9` = copy of last id pushed.
  Caller pattern everywhere: `LDA #id ; LDX xx ; JSR $F84E`.
- **Queue**: slots `$DC-$E3`, `$88` = empty. Consumed by NMI/$FF6E at read index `$DB`: slot
  != $88 → run it (JSR $8003 in bank 3, A = id), overwrite with $88, advance `$DB`, repeat;
  marker → only the tick runs.
- **Task dispatch** (`Audio_Task` $8003 → $80FE → $8106), A = id:
  - `A >= $F0` → special engine commands, dispatch table $81B4 (A&7):
    | id | target | meaning |
    |----|--------|---------|
    | $F0 | $81C5 | stop SFX (silence group-A channels, $4015=$0F, sweep reset) |
    | $F1 | $81C8 | full engine reset (also clears D0/D1/D7/D8, $CE-$CF) |
    | $F2 | $81E4 | silence/clear SFX state (same core as $F0) |
    | $F3 | $821E | pause music (set $C0 bit1) |
    | $F4 | $8226 | resume music (clear $C0 bit1) |
    | $F5 | $8234 | tempo ramp helper (uses $C3 param) |
    | $F6 | $822D | set tempo from $C3 (writes $C9/$CA via $84F1-style path) |
    | $F7 | $8234 | same as $F5 |
  - `A < $F0` → id is folded mod $40 (`A -= $40` while >= $40) and indexes the 64-entry
    **sound pointer table** at **$8A43** (pairs stored hi,lo). Entry = 0 → ignore.

## 4. Sound pointer table $8A43-$8AC2 [V]

64 entries; byte at $8A40 = $40 (fold ceiling). Entries 0-15 (13 = null) = **music**, 16-63
(31 = null) = **SFX**. First stream byte disambiguates: 0 = music header, else = SFX priority.

### Music entries (16)

| id | header | size* | static trigger sites (bank $addr = LDA #id;JSR $F84E) |
|----|--------|-------|--------------------------------------------------------|
| 0 | $8C0B | $477 | bank7 $D8C9 (master script) — title/theme |
| 1 | $9082 | $109 | bank7 $C7E2, $D95F — **mission music (playing in m1 probe)** |
| 2 | $918B | $0BB | bank7 $DA45 |
| 3-11 | $9246,$92E0,$9662,$989F,$9B85,$9ED5,$A155,$A3DB,$A76F | — | no static LDA# site (computed/unused) |
| 12 | $AA1B | $085 | — |
| 13 | null | — | — |
| 14 | $AAA0 | $254 | — |
| 15 | $ACF4 | $220 | — |

\* size = distance to next entry (approx; streams are interleaved per channel).
Observed music ids queued statically: $00, $01, $02, $0C (bank7 $D40B/$D4A9). [V: song 1 =
mission 1 gameplay music; channel pointers $90AB/$910C/$915F inside song-1 ranges; noise ptr 0]

### SFX entries (16-63)

ptr / size / first-byte = priority (7-bit; compared against running max `$CE`; higher or equal
wins; bit7 = additional flag, see §7).

| id | ptr | size | pri | id | ptr | size | pri |
|----|-----|------|-----|----|-----|------|-----|
| 16 | $AF14 | $12 | 04 | 40 | $B193 | $11 | 07 |
| 17 | $AF26 | $13 | 05 | 41 | $B1A4 | $0E | 07 |
| 18 | $AF39 | $0C | 05 | 42 | $B1B2 | $13 | 06 |
| 19 | $AF45 | $0C | 05 | 43 | $B1C5 | $18 | 07 |
| 20 | $AF51 | $17 | 05 | 44 | $B1DD | $1C | 07 |
| 21 | $AF68 | $17 | 09 | 45 | $B1F9 | $15 | 07 |
| 22 | $AF7F | $0C | 15 | 46 | $B20E | $21 | 06 |
| 23 | $AF8B | $1B | 07 | 47 | $B22F | $12 | 07 |
| 24 | $AFA6 | $25 | 07 | 48 | $B241 | $0B | 07 |
| 25 | $AFCB | $64 | 07 | 49 | $B24C | $89 | 06 |
| 26 | $B02F | $15 | 07 | 50 | $B2D5 | $1D | 07 |
| 27 | $B044 | $12 | 05 | 51 | $B2F2 | $22 | 07 |
| 28 | $B056 | $2F | 14 | 52 | $B314 | $10 | 07 |
| 29 | $B085 | $13 | 08 | 53 | $B324 | $24 | 08 |
| 30 | $B098 | $0C | 08 | 54 | $B348 | $10 | 07 |
| 31 | null | — | — | 55 | $B358 | $11 | 07 |
| 32 | $B0A4 | $20 | 14 | 56 | $B369 | $12 | 07 |
| 33 | $B0C4 | $17 | 14 | 57 | $B37B | $16 | 07 |
| 34 | $B0DB | $2A | 05 | 58 | $B391 | $0B | 07 |
| 35 | $B105 | $15 | 05 | 59 | $B39C | $1C | 08 |
| 36 | $B11A | $0A | 05 | 60 | $B3B8 | $0B | 07 |
| 37 | $B124 | $3D | 15 | 61 | $B3C3 | $0B | 07 |
| 38 | $B161 | $0F | 07 | 62 | $B3CE | $0F | 07 |
| 39 | $B170 | $23 | 04 | 63 | $B3DD | $4C | 05 |

Sizes are distances to the next entry (last entry bounded by ~$B429).

### Live-confirmed SFX semantics (probe_audio_sfx, mission 1)

| id | trigger | semantics |
|----|---------|-----------|
| $21 | bank2 `$A241` / A on map | mission launch: pulse1+pulse2 parallel pitch slide up + noise burst, ~100 frames [V]. NOT a gameplay jump: bank7 jump `$CBB7` has no sound call; `gold_jump` a@20-22 leaves `$D9=04`, `$D0/$D1=0` |
| $3F | B button in m1 | punch/whiff: noise hit, ~30 frames [V] |
| $04 | bank6/7 | climb/grab rattle (noise) [V context] |

### Static trigger sites (110 sites of `LDA #id; JSR $F84E`)

Bank 1: $89FC/$8B5C/$8C3E/$911B/$A2E5? (=$12 x4), $8E69/$8162/$CBAC/$CD03/$D147/$D272/$D746/$F489 ($15),
$8FAE ($2A), $928E/$92BA/$A47F/$A4BB ($2E), $96DF/$CA35 ($1C), $96EA/$8D20/$D974 ($23),
$99DA ($35), $9C1E ($36), $9EB0 ($37), $A034/$A0AD ($38), $A314 ($39), $A36E/$9865 ($2C),
$A555/$9E27/$B092 ($29), $A8A0 ($3A), $AA7C/$AB1D ($3B), $AAC8/$AADA ($3C), $AE16 ($3E),
$AEA8 ($3D), $B202/$B3D4 ($1D), $B429 ($1E), $B4F2/$81A8/$8219 ($1A).
Bank 2: $996A/$999C ($18), $9A02 ($20), $9BDE/$A470/$A48D ($22), $9C8D ($19), $A210/$A369/$AA77 ($3F),
$A243 ($21), $A2D9 ($F1).
Bank 6: $809D ($1D), $8138 ($1E), $8162 ($15), $81A8/$8219 ($1A), $83D7 ($24), $8867/$888B/$88AF ($18),
$88DB/$89D8/$8A41/$8A4F ($17), $8AF6 ($19), $8D20 ($23), $9125/$A2E5 ($12), $9290 ($26),
$940C ($27), $95EE ($32), $9865 ($2C), $9979 ($2B), $9E27/$B092 ($29), $A47F/$A4BB ($2E),
$A5BB ($2D), $A6F7 ($33), $A86E ($2F), $ACCC ($30), $AE08 ($31), $B5A7 ($34).
Bank 7: $C63E/$CE14/$D532/$D6FD ($10), $C9D5 ($1B), $CA35 ($1C), $CB07/$D3B1 ($25), $CBAC/$CD03/
$D147/$D272/$D746/$F489 ($15), $C7E2/$D95F ($01), $C997/$D9B4 ($F2), $C59F/$C794/$D4BE/$D941 ($F0),
$D36E ($16), $D40B/$D4A9 ($0C), $D602/$D735/$D7C8/$F392/$F452/$F46B ($12), $D8C9 ($00),
$DA45 ($02), $DDAB/$CEE9/$E3BF (computed id — see open questions).
Bank 5 (gameplay engine) has **no direct sites** — its SFX ids must be computed (A loaded from
variables) or routed via banks 6/7.

## 5. Music stream format [S, partially V]

Song header (first byte 0): `[00] [p1.hi p1.lo] [p2.hi p2.lo] [tri.hi tri.lo] [noise.hi noise.lo]`
— four big-endian pointers, one per channel (order pulse1, pulse2, triangle, noise). A null
pointer = channel unused (song 1 has no noise stream). On music start ($816F): tempo default
$0199, channel state cleared, pointers stored, APU enabled ($4015=$0F), sweeps reset.

Per-channel stream (parsed by `Music_ChannelTick` $8393 → note loop $83CD, pointer in
$0750/0754+chan, advanced by `ReadByte` $8592 → `ReadByteAt` $803A):
- **Channel init block** (first bytes): flag byte; for each set bit 0-3 one parameter byte:
  bit0 → instrument (1-based, handler $866F), bit1 → duty ($86AD), bit2 → volume ($865A),
  bit3 → slide/vibrato depth ($86A7). Then bit semantics: `$8333+` arms first note.
- **Event loop**: bytes >= $20 = note events; bytes < $20 = commands via table $84A6
  (24 entries, $00-$17; commands >= 4 take one parameter byte):
  | cmd | handler | meaning |
  |-----|---------|---------|
  | 0-3 | $84D9/$84DD/$84E1/$84E8 | note articulation flags into $0730,X ($20/$40/$10/$08) |
  | 4 | $8575 | OR $0730,X with param |
  | 5 | $84F1 | set tempo: word = (param<<8)\|next byte → $C9:$CA |
  | 6 | $84FF | note-length unit → $073C,X (multiplies durations) |
  | 7 | $865A | duty/volume nibble |
  | 8 | $866F | instrument change |
  | 9 | $8505 | control nibble (duty) into $0730,X bits 6-7 |
  | A | $8510 | transpose (global $CB) |
  | B | $8515 | transpose (channel $0734,X) |
  | C | $86A1 | vibrato rate → $0714,X |
  | D | $86A7 | vibrato depth → $0718,X |
  | E-$11 / $12-$15 | $851B/$851F/$8523/$8527 | envelope/volume modifiers (duplicated pairs) |
  | $16 | $855A | ? |
  | $17 | $8580 | end of channel stream: clear pointer, silence channel |
- **Note event** (byte >= $20) [? bit roles]: high bits select an octave/multiplier row
  (tables $8915/$891C = 2,4,8..128 and 3,6,c..c0; combined 1.5x when $0730 bit4), the value
  is accumulated into the pitch accumulator $0738+chan and final pitch = $8923[$0730&F] +
  acc + $CB + $0734,X (index into period table, capped $5F). Low 5 bits: 0 = note off
  ($85A3), else duration code; duration = $073C,X × multiplier → note frames $0740+chan;
  note counters decrement by $C7 (tempo ticks) per frame [V: music continues seamlessly].

## 6. SFX stream format [S, partially V]

`[priority]` then tick blocks parsed by `SFX_ParseTick` $8252 (stream pointer $D1:$D0, global):
- flags byte: bit7 = chain another task (id stored in $D7 during lookup, re-enters $8118) or
  stop engine (via $D7 bit0 → $81C8); bit6 = **loop point**: two bytes = new $D1:$D0
  (big-endian); bit5/bit4/bit3/bit2 = optional bytes → $D6-update, $D4, $D2; then one length
  byte $D3 (always); duration product $D3×$D4; final byte = **channel mask → $CF**
  (bit0=pulse1, bit1=pulse2, bit2=tri, bit3=noise) [V: jump = $0B = pulse1+pulse2+noise].

## 7. Channel state, arbitration, instruments [V unless noted]

Two identical 20-field per-channel state groups (fields stride 4, channel k at base+4k... the
code uses X=0-3 for group A and X=0x28-0x2B for group B on the same opcodes):

- **Group A = SFX** ($0700-$074F): $0700 instrument, $0704 flags (bits0-2 envelope phase,
  bit3 instrument-loaded, bit5 note-on, bit7 slide), $070C control byte (duty/halt/const/vol),
  $0710 envelope timer, $0714 vibrato rate, $0718 slide amount, $071C slide target, $0720/24
  period lo/hi, $0728/2C stream ptr lo/hi, $0730 note flags, $0734 transpose, $0738 pitch acc,
  $073C length unit, $0740 duration, $0744 per-duty volume decay, $077C-$077F last $4003
  (shared, dedup).
- **Group B = music** ($0728-$077F accessed with X|0x28): same layout shifted by $28 bytes
  per field ($0728 instrument, $072C flags, $0734 control, $0738 env timer, $0748/4C period,
  $0750/54 stream ptr, $0758 note flags, $0760 pitch acc, $0768 duration, ...). [V: values
  match live music state; $0728-2B = instruments 0,9,9,9 in song 1]

**Arbitration** (the mechanism that lets SFX steal channels and give them back):
1. SFX tick block sets `$CF` = channels used by current SFX.
2. In the tick loop, a channel with `$CF` bit set runs the SFX tick (writes APU) and sets
   `$CF` bit7.
3. The music tick's envelope path for that channel sees bit7 (`$8724: LDA $CF; BMI $88A0`)
   and jumps to $88A0 which **skips all register writes** — music parsing/timing continues,
   output is muted only for stolen channels.
4. SFX start silences the stolen channels ($4000/Y=$30) and resets sweeps ($4001/$4005=$08)
   [V: observed at steal frame].
5. When the SFX stream ends ($8580 or mask→0) `$CF` clears; music resumes writing registers
   on the next tick — current note state was preserved, so the hand-back is seamless [V:
   music envelopes resume unchanged after 100-frame jump SFX].
6. SFX-vs-SFX: 7-bit priority vs running max `$CE`; equal-or-higher replaces. `$D5/$D6/$D7`
  hold chain/slide bookkeeping [partially mapped].

**Instruments**: table $8AC3, 41 entries x 8 bytes (region ends exactly at song-0 header
$8C0B). Envelope = 4-phase state machine ($86BA, phases from $0704/X&7, dispatch table at
$86C6): attack ($86D1, instr[0] + duration table), decay ($86E6, instr[1]/instr[2]), sustain/
release ($8702, instr[3]), volume applied to control byte. Also per instrument: [4] used at
$88FA (post-note), [5] vibrato depth ($87B8), [6] gate ($876D), [7] noise period bits ORed
($883F). SFX noise uses instrument 41 ($29). [field roles S]

**Tables**:
- $8933: note duration table, 29 monotonic entries (0,1,2,...,$0c,$0e,$0f,$10,...,$7e).
- $8915/$891C/$8923: octave/multiplier tables (2,4,8,16,32,64,128 / 3,6,12,...,192 /
  0,12,24,...,84).
- $8953: vibrato LUT (00 07 0e 15 1c 23 2a 31), also used as octave-fold thresholds ($8849).
- $8959-$8A18: **note→period table**, 96 entries x 2 bytes (lo,hi) indexed by final pitch
  index; portamento compares $0720/24 against it; hi byte 6-bit (+2 flag bits in $0724).
  Raw values start $312A $375C $369C $E7?? ... — encoding (12.4 fixed period + octave folding
  via $8953) is [?]; exact mapping needs one focused probe (log $0720/$0724 per note index).

## 8. RAM summary (zero page $C0-$E3, page 7 $0700-$07FF)

$C0 halt/pause flags (bit0 halt, bit1 music paused) · $C1-$C6 scratch (mult/ptr) · $C7:$C8
tempo accumulator · $C9:$CA tempo increment · $CB global transpose · $CC:$CD tempo/cmd
scratch · $CE SFX priority max · $CF SFX channel mask (rotating, bit7 = busy-this-frame) ·
$D1:$D0 SFX stream ptr · $D2-$D5 SFX parse state · $D6/$D7 chain state · $D8-$DB misc/$DA
queue write idx · $DB queue read idx · $DC-$E3 queue ring ($88 empty) · $D9 last task id.
Page 7: group A/B as in §7.

## 9. Open questions

1. Note-event bit layout (octave vs duration roles of bits 5-7 / 0-4) — needs one probe
   logging $8959 index vs $4002/$4003 per note.
2. Exact period-table encoding ($8959) incl. octave folding.
3. The 3 computed-id trigger sites (bank7 $DDAB/$CEE9/$E3BF) and bank-5 routing — would name
   remaining SFX/music semantics; ids never statically referenced: music 3-11,13-15 ($03-$0B,
   $0D-$0F), SFX $11,$13,$14,$1F,$28.
4. D5/D6/D7 exact roles in SFX chaining; loop-point byte order details.
5. Whether the $F5/F6 handshake can ever skip audio ticks in practice (frame-drop check).

## 10. WAV export tool plan (not implemented)

Feasible pure-Python replayer: (a) parse song/SFX streams with the §5/§6 rules; (b) emulate
per-frame the small register model: pulses (duty table, 11-bit period, length/linear ignored —
use envelopes), triangle (16-step at 11-bit period), noise (LFSR, period index, short/long
mode from bit7), constant vs envelope volume from $070C/$0734 group fields and instrument
envelope phases; (c) mix 4 channels, apply frame tempo accumulator; (d) write WAV. Ground
truth for validation exists: `reference/det_a/audio.wav` (boot) and per-frame APU write logs
from `probe_audio_m1`/`probe_audio_sfx`. Simplest robust alternative: offlineMesen WAV dump
per song (queue task id, run N frames) — no engine code needed.

## 11. Porting status (2026-10-11) — bank-3 driver transcribed to C#

**Deliverables**: `Assets/Darkwing/Audio/AudioSequencer.cs` (the transcription; engine-free,
drives `Apu.Write` in the original order, `Tick()` = one driver tick per game frame,
`RenderFrame()` = tick + 29780/29781 alternating CPU cycles sampled at 48 kHz through a fixed
rational resampler 16000/596591 — drift-free; `SaveState`/`LoadState` schema-tagged).
Test harness: `Tools/DarkwingResearch/AudioPortRender.cs` (editor `run_script`: renders song 1
600 frames → generated WAV + per-frame state CSV; use `RenderTo` with a temporary output directory),
`Tools/DarkwingResearch/AudioPortSfxCompare.cs` (SFX state dump), and
`Tools/DarkwingResearch/compare_audio_port.py` (structural comparison vs the Mesen probes).

### 11.1 Проверки

Актуальные числа — в [audio_port_comparison.json](../audio_port_comparison.json); команды генерации временных CSV/WAV — в [RESEARCH_HANDOFF](../../../Docs/DarkwingPort/RESEARCH_HANDOFF_RU.md). Постоянные копии экспортов не требуются.

Проверка 2026-10-18: song1 — 600 кадров, 479210 samples при 48 kHz; longest contiguous (interval,period) runs: pulse1 12, pulse2 17, triangle 18. Старое утверждение «100% переходов» было ошибкой сравнения списка порта с самим собой. После исправления присутствие переходов в original capture: **33/51, 44/59, 26/34**; порядок/кратность эта метрика не проверяет. Исходные старты различны: порт от начала песни, оригинал — mid-song snapshot. Envelope correlation **0,4367** при подобранном сдвиге **−87 кадров** не доказывает сигнал или абсолютный тайминг.

Технические наблюдения при переносе: tempo default $0199, song1 cmd05→$0133, $C8 += $33/frame с $C7 1↔2. SFX $3F: len $05, speed $56, D5=(D3·D4)>>8+1; mask $08/noise, instrument $29, volume $0F→control $3F; $400E=$84 = ((note−1)^$0F)|instr[7], envelope $F0/$90/$6D/$4A/$27, stop $FF→банк3 $81C8. Арбитраж: $CF steal, музыка продолжает parse со skip записи через $88A0. Это описание исследованных/портированных веток; сквозная покадровая приёмка не закрыта. Legacy SFX alignment port1=probe88 даёт 1091/1160 значений за 40f; начальное состояние/фаза требуют проверки (формат проб — FACTS, раздел SFX $3F).

### 11.2 Corrections to the static analysis above (found while porting)

1. **§5 "Channel init block" for music is wrong.** Music streams are pure event streams from
   byte 0 — song 1's pulse1 starts with cmd $05 (tempo $0133), which is where the observed
   tempo comes from. The init-flag block parser ($830A) belongs to the SFX tick-block format
   only (entered from $82DE when $C0 bit0 is set).
2. **§6 SFX tick-block flag bits are bit0/bit1/bit2, not bit6/5/4.** The parser tests bit7
   via ASL (chain/stop), then LSRs $C4 testing **bit0 = loop point** (2 big-endian bytes →
   new $D1:$D0), **bit1 = $D4** (speed), **bit2 = $D2** (pitch offset). Verified against the
   punch SFX stream `05 | 02 56 05 08 | 05 28 0F | 0C` → D4=$56, len=$05, mask=$08 (noise),
   init: instrument $29 (handler $866F, param+1), volume $0F ($865A), note $0C.
3. **Music channel pointers live at $0750/$0754 + c** and music start/clear/silence loops use
   raw X (3..0) against those group-B addresses; the field layout is per-FIELD 4-byte arrays
   ($0700+field+X, group B = same offsets with X|=$28), not per-channel interleaved.
4. **$C0 bit0 (halt) is cleared at the end of every tick** by the `LSR $C0 / ASL $C0` pair at
   $80AC-$80AE (ASL shifts in 0 — bit0 does not survive). $82CB and the $80FE task bracket
   set it mid-tick only; probes show $C0 = 0 at every frame end. Bit1 = music pause.
5. **Note-event bit layout (resolves open question §9.1)**: with carry-in 1 from the routing
   CMP, the four ROLs at $83DB compute `(b >> 5) & 7` — bits 7-5 pick the multiplier row
   (index-1 into $8915 = 2·2ⁿ when $0730 bit5/4 set, $891C = 3·2ⁿ otherwise, ×1.5 when
   $0730 bit3, index −1 reads $8914 = $60); bits 4-0 are the duration code whose value-1 is
   ALSO the pitch index ($8923[flags&$0F] + code−1 + $CB + $0734,X; noise: (code−1)^$0F).
   $0738 (the multiplier) is a hold counter in tempo ticks; $0740 (unit×hold >> 8, min 1) is
   the APU gate.
6. **Period table $8959 is little-endian words** (lo at $8959+2i → $0720, hi at $8959+2i+1 →
   $0724; resolves half of open question §9.2). The $8849 fold: find the largest Y with
   hi ≥ $8953[Y]; N = Y+hi; shift the 16-bit pair [((N&7)+7) : lo] right by 7−(N>>3); the
   final $4003 value = pair>>8 (dedup'd), $4002 = pair&$FF. Entry 61 (pitch 60) → $06A etc.
7. **Command table $84A6 has 25 entries ($00-$18)** — cmd $18 = the duty handler $86AD
   (shared with the SFX init block). Table base = the JSR $8023 push quirk (return−1), entries
   (lo,hi) at base+2k+1/base+2k+2. Same quirk fixes the $81B4 task table: $F5→$822D (fade
   down), $F6→$8234 (fade set), $F7→$824A (D8 = −$C3) — the §3 table had these shifted.
8. **Volume clamp mechanism** ($87A1): BIT $C4 with $C4 = $10 (always — $8797 reloads it even
   after the gate path) tests the control byte's $30 sentinel bit4; a borrowed subtraction
   clears it → volume clamps to control & $F0. Confirmed against the probe: no control write
   ever exceeds the stream's volume nibble.
9. **Song unused channels**: song 1's "noise" pointer $918A points at a `$17` (stream end)
   byte — the channel self-terminates on tick 1 (the doc's "noise ptr 0" was the
   post-termination value).
10. **$85AE envelope restart**: music triangle → phase 2 with timer 1; SFX triangle → phase 2
    with timer = (D3 × control)>>8 + 1; others → phase 0, timer 0. The $8702 sustain zeroes
    the timer for channel index 1 every tick (triangle = linear-counter "volume" $FF/0).

### 11.3 Deviations / open items in the port

- The $803A fixed-bank read path (pointer hi ≥ $C0) is implemented as a logical bank-7 read;
  the original's MMC1 window dance is not reproduced (path is dead for all game audio data).
- Reads through a cleared stream pointer (address < $8000) return 0; the original would read
  mirrored RAM. Not reachable with valid streams.
- SFX block loop-point `b1 == D6` handling reproduces the D6 collapse; the `b2 == 0`
  fall-through (whose PLP would pop a return address in the original) is implemented as the
  D6-restore path — unreachable with valid ROM data (loop-pointer hi bytes are $80-$BF).
- Queue plumbing ($DA/$DB/$DC-$E3, Audio_Enqueue $F84E) is replaced by synchronous
  `Task(id)` calls; the fields are kept in SaveState for completeness.
- Open: the probe's re-triggered punch SFX shows group-A flags bit3 clear where the port
  (fresh trigger) has it set — bit3 only gates GateCheck's decay-accumulator reset, which is
  unreachable in the noise path, so the audio is identical; exact trigger-history cause not
  chased. SFX-vs-SFX priority equal-replace and the D6 loop-counter semantics beyond the
  punch SFX remain exercised only by construction.
