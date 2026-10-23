// Darkwing Duck (NES) audio engine — faithful C# transcription of the PRG bank 3
// sound driver ($8000-$BFFF window), for the native Unity port.
//
// Provenance: Research/DarkwingDuck/docs/audio_engine.md and the disassembly under
// Research/DarkwingDuck/disasm/bank3_*.txt. Every method cites the CPU address(es)
// it transcribes. The sequencer drives Darkwing.Audio.Apu exactly like the original
// drives the 2A03: identical $4000-$4015 write sequences per frame.
//
// Execution model (verified against Mesen probes):
//   - one Tick() per game frame == one driver tick ($806C, reached via the bank-7 NMI)
//   - tasks (PlayMusic/PlaySfx/...) == Audio_Task $8003 with A = id
//   - engine-free: no UnityEngine types, deterministic, step-locked, serializable.
//
// Channel state model: the original keeps two identical per-channel field groups in
// page 7, each FIELD being a 4-byte array indexed by channel:
//   group A (SFX)   fields at $0700 + 4*field + X, X = 0..3
//   group B (music) same offsets with X |= $28 (base $0728)
// This file keeps a flat byte[0x800] page-7 image and addresses it exactly like the
// 6502 does (_p7[X + 4*field]), so any field overlap present in the original is
// reproduced bit-exactly.

using System;

namespace Darkwing.Audio
{
    public sealed partial class AudioSequencer
    {
        // ------------------------------------------------------------------ //
        // ROM data
        // ------------------------------------------------------------------ //

        /// <summary>PRG bank 3 (16 KB). CPU $8000-$BFFF == bytes[addr - 0x8000].</summary>
        private readonly byte[] _rom3;

        /// <summary>Optional fixed bank (16 KB). CPU $C000-$FFFF == bytes[addr - 0xC000].
        /// Only used by the $803A high-pointer read path (never taken by game data —
        /// all audio streams live in bank 3); kept for faithfulness.</summary>
        private readonly byte[] _rom7;

        private readonly Apu _apu;

        // ------------------------------------------------------------------ //
        // Zero page ($C0-$E3)
        // ------------------------------------------------------------------ //

        private byte _c0;                   // bit0 halt (cleared at tick end, $80AC), bit1 music pause
        private byte _c1, _c2;              // stream/header pointer scratch ($C1 lo, $C2 hi)
        private byte _c3, _c4;              // command param / multiply operand / clamp mask
        private byte _c5, _c6;              // instrument record pointer ($C5 lo, $C6 hi; base $8AC3)
        private byte _c7, _c8;              // $C7:$C8 tempo accumulator (whole . fraction)
        private byte _c9, _ca;              // $C9:$CA tempo increment (8.8)
        private byte _cb;                   // global transpose
        private byte _cc, _cd;              // fade rate ($CC bit7 = down-direction) / fade level
        private byte _ce;                   // SFX priority max (7-bit)
        private byte _cf;                   // SFX channel mask (bit7 = occupied-this-frame transient)
        private byte _d0, _d1;              // SFX stream pointer ($D0 lo, $D1 hi)
        private byte _d2, _d3, _d4, _d5;    // SFX parse state: pitch offset, block len, speed, duration
        private byte _d6, _d7, _d8;         // chain/loop bookkeeping, chain id*2, slide step
        private byte _d9;                   // last queued task id (game plumbing; unused here)
        private readonly byte[] _queue = new byte[8];  // $DC-$E3 ring ($88 = empty; game plumbing)
        private byte _queueWrite;           // $DA
        private byte _queueRead;            // $DB

        // ------------------------------------------------------------------ //
        // Page 7 ($0700-$07FF) — flat image, addressed exactly like the asm
        // ------------------------------------------------------------------ //

        private readonly byte[] _p7 = new byte[0x800];

        // Field indices (each field = 4 bytes, one per channel; byte offset = X + 4*field)
        private const int F_Instrument  = 0x00;  // $0700 (1-based instrument number)
        private const int F_Flags       = 0x04;  // $0704: bits0-2 env phase, bit3 instr-loaded,
                                                 //   bit5 note-on/slide-active, bit6 vib-sign, bit7 slide
        private const int F_DecayAcc    = 0x08;  // $0708 volume-decay / vibrato phase accumulator
        private const int F_Control     = 0x0C;  // $070C duty|halt|const|volume control byte
        private const int F_EnvTimer    = 0x10;  // $0710 envelope timer (attack counts up to $F0)
        private const int F_VibRate     = 0x14;  // $0714 vibrato rate
        private const int F_SlideAmt    = 0x18;  // $0718 slide amount / vibrato depth (bit7 = down)
        private const int F_SlideTarget = 0x1C;  // $071C slide target pitch index
        private const int F_PeriodLo    = 0x20;  // $0720 current period lo
        private const int F_PeriodHi    = 0x24;  // $0724 current period hi
        private const int F_StreamLo    = 0x28;  // $0728 stream pointer lo
        private const int F_StreamHi    = 0x2C;  // $072C stream pointer hi
        private const int F_NoteFlags   = 0x30;  // $0730 articulation/control flags
        private const int F_Transpose   = 0x34;  // $0734 per-channel transpose
        private const int F_PitchHold   = 0x38;  // $0738 note hold counter (tempo ticks)
        private const int F_LengthUnit  = 0x3C;  // $073C note length unit
        private const int F_Duration    = 0x40;  // $0740 APU gate duration (frames)
        private const int F_VolDecay    = 0x44;  // $0744 per-duty volume-decay counters (4 slots x 4 ch)

        // The F_* constants are already BYTE offsets from $0700 (matching the asm's
        // absolute addressing $0700+field+X), so the index is simply X + field.
        private byte R(int x, int field) => _p7[x + field];
        private void W(int x, int field, byte v) => _p7[x + field] = v;

        // $077C-$077F: last value written to $4003 per APU channel (write dedup)
        private byte LastPeriodHi(int x) => _p7[0x7C + (x & 3)];
        private void SetLastPeriodHi(int x, byte v) => _p7[0x7C + (x & 3)] = v;

        // ------------------------------------------------------------------ //
        // Frame audio clock + resampler
        // ------------------------------------------------------------------ //

        /// <summary>NTSC CPU clock (Hz).</summary>
        public const int CpuHz = Apu.CpuHz;              // 1789773
        /// <summary>Output sample rate (Hz) of the built-in resampler.</summary>
        public const int OutputHz = 48000;

        // Fixed rational resampler: emit one output sample every CpuHz/OutputHz CPU
        // cycles. 1789773/48000 = 596591/16000 (gcd 3 removed): per CPU cycle the
        // accumulator advances by 16000 and fires every 596591 — drift-free forever.
        private const long ResampleStep = 16000;
        private const long ResampleMod = 596591;
        private long _resampleAcc;

        // NTSC frame length = 29780.5 CPU cycles, realized as an alternating
        // 29780/29781 pattern via a half-cycle accumulator (+59561 per frame,
        // emit floor(acc/2) cycles, keep the remainder). Documented deviation: none —
        // this is the standard NTSC frame cadence.
        private const long HalfCyclesPerFrame = 59561;   // 2 * 29780.5
        private long _halfAccum;
        private bool _frameFlip;                         // toggles every frame (documentation aid)

        // ------------------------------------------------------------------ //
        // Construction
        // ------------------------------------------------------------------ //

        public AudioSequencer(byte[] bank3, byte[] bank7 = null, Apu apu = null)
        {
            _rom3 = bank3 ?? throw new ArgumentNullException(nameof(bank3));
            if (_rom3.Length != 0x4000)
                throw new ArgumentException("bank3 must be exactly 16 KB (one PRG bank)", nameof(bank3));
            _rom7 = bank7;
            _apu = apu ?? new Apu();
        }

        /// <summary>The APU this sequencer drives (same instance across frames).</summary>
        public Apu Apu => _apu;

        // ------------------------------------------------------------------ //
        // Public task API ($8003 Audio_Task)
        // ------------------------------------------------------------------ //

        /// <summary>Raw engine task, mirrors Audio_Enqueue + dispatch ($8003). Music ids
        /// 0-15 (sound table entries whose first stream byte is 0), SFX ids 16-63,
        /// special engine ids $F0-$F7 (see SpecialTask).</summary>
        public void Task(int id) => TaskEntry((byte)id);

        public void PlayMusic(int id)
        {
            if (id < 0 || id > 15) throw new ArgumentOutOfRangeException(nameof(id), "music ids are 0-15");
            TaskEntry((byte)id);
        }

        public void PlaySfx(int id)
        {
            if (id < 16 || id > 63) throw new ArgumentOutOfRangeException(nameof(id), "SFX ids are 16-63");
            TaskEntry((byte)id);
        }

        /// <summary>Task $F1 — full engine reset ($81C8).</summary>
        public void StopAll() => TaskEntry(0xF1);

        /// <summary>Task $F0 — stop: clear music streams + full reset ($81C5).</summary>
        public void StopMusicAndReset() => TaskEntry(0xF0);

        /// <summary>Task $F3 / $F4 — pause/resume music ($821E/$8226).</summary>
        public void PauseMusic() => TaskEntry(0xF3);
        public void ResumeMusic() => TaskEntry(0xF4);

        /// <summary>Tasks $F5-$F7 take a byte param; the original passes it in Y at
        /// dispatch ($81AE: STY $C3). Set it here before calling Task($F5/$F6/$F7).</summary>
        public void SetTaskParam(byte c3) => _c3 = c3;

        // ------------------------------------------------------------------ //
        // Per-frame tick: $806C (Audio_Tick)
        // ------------------------------------------------------------------ //

        /// <summary>Advance the sequencer one game frame (the NMI tick $8000→$806C).</summary>
        public void Tick()
        {
            int before = _executionCycles;
            TimingSupported = true;
            TickCore();
            LastTickCycles = _executionCycles - before;
        }

        private void TickCore()
        {
            Cost(5);
            if ((_c0 & 0x01) != 0)
            {
                Cost(9);
                return;                                        // $806C-$806F: halted → RTS
            }

            Cost(8);
            if (_d0 != 0 || _d1 != 0)                          // $8071-$8077
            {
                Cost(8);
                SfxParseTick();                                //   JSR $8252
            }
            else Cost(3);

            // $807A-$8085: tempo accumulator $C7:$C8 += $C9:$CA (8.8; $C7 = whole ticks)
            int sum = _c8 + _ca;
            _c8 = (byte)sum;
            _c7 = (byte)(_c9 + (sum >> 8));                    // LDA $C9; ADC #$00

            // $8087-$80A7: channel loop X = 3..0 (3 pulse1, 2 pulse2, 1 triangle, 0 noise)
            byte cfSave = _cf;                                 // $8089: PHA
            Cost(27);
            for (int x = 3; x >= 0; x--)
            {
                bool sfxBit = (_cf & 0x01) != 0;
                Cost(sfxBit ? 21 : 8);
                _cf = (byte)(_cf >> 1);                        // $808C: LSR $CF
                if (sfxBit)                                    // $808E: BCC skip
                {
                    _cf |= 0x80;                               // $8090-$8094: occupied this frame
                    SfxChannelTick(x);                         // $8096: JSR $82DE
                }
                if ((_c0 & 0x02) == 0)                         // $8099-$809D: music not paused
                {
                    Cost(24);
                    MusicChannelTick(x);                       // $80A1: JSR $8393
                }
                else Cost(8);
                Cost(x == 0 ? 4 : 5);
            }
            _cf = cfSave;                                      // $80A9-$80AA: PLA; STA $CF

            // $80AC-$80AE: LSR $C0; ASL $C0 — clears the halt bit (bit0) at tick end.
            // (Probe-verified: $82CB/$80FE set bit0 mid-tick; $C0 reads 0 at every frame end.)
            _c0 &= 0xFE;

            // $80B0-$80D5: music fade accumulator $CD:$C0 += ($CC & $7F) << 4
            int rate = _cc & 0x7F;
            Cost(22);
            if (rate != 0)                                     // $80B4: BEQ $80D7
            {
                Cost(70);
                int addLo = (rate << 4) & 0xFF;
                int addHi = (rate >> 4) & 0xFF;                // $80BC-$80C1: 4x (ASL A; ROL $C1)
                int s = _c0 + addLo;                           // $80C3-$80C5: ADC $C0; STA $C0
                _c0 = (byte)s;
                int sh = addHi + _cd + (s >> 8);               // $80C7-$80C9: LDA $C1; ADC $CD
                if (sh > 0xFF) { Cost(13); _cc = (byte)(_cc & 0x80); _cd = 0xFF; }   // $80CB-$80D3: done
                else { Cost(4); _cd = (byte)sh; }                                        // $80D5
            }
            else Cost(3);
            Cost(6);
        }

        // ------------------------------------------------------------------ //
        // Frame audio clock: one sequencer Tick + APU stepping + 48 kHz resampling
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Step CPU/APU cycles for one frame and write resampled output into
        /// <paramref name="dest"/>. The sequencer tick itself is driven by the NMI
        /// (<see cref="Tick"/>), so a host that already ran the frame must call this
        /// samples-only entry point.
        /// </summary>
        public int RenderSamples(float[] dest, int offset)
        {
            _halfAccum += HalfCyclesPerFrame;
            int cycles = (int)(_halfAccum / 2);
            _halfAccum -= (long)cycles * 2;
            _frameFlip = !_frameFlip;
            int n = 0;
            for (int i = 0; i < cycles; i++)
            {
                _resampleAcc += ResampleStep;
                if (_resampleAcc >= ResampleMod)
                {
                    _resampleAcc -= ResampleMod;
                    dest[offset + n++] = _apu.CurrentOutput();
                }
                _apu.StepCpu();
            }
            return n;
        }

        /// <summary>
        /// Run one frame of audio: one sequencer Tick(), then 29780/29781 alternating
        /// CPU cycles of APU stepping, sampling <see cref="Apu.CurrentOutput"/> at exactly
        /// 48 kHz through the fixed rational resampler. Writes into
        /// <paramref name="dest"/> at <paramref name="offset"/> and returns the sample
        /// count (~798.68/frame — alternating 798/799; provide room for 800 floats).
        /// </summary>
        public int RenderFrame(float[] dest, int offset)
        {
            Tick();
            return RenderSamples(dest, offset);
        }

        /// <summary>Allocating variant of <see cref="RenderFrame(float[],int)"/>.</summary>
        public float[] RenderFrame()
        {
            var buf = new float[(int)((29781L * ResampleStep) / ResampleMod) + 2];
            int n = RenderFrame(buf, 0);
            if (n == buf.Length) return buf;
            var res = new float[n];
            Array.Copy(buf, res, n);
            return res;
        }

        // ------------------------------------------------------------------ //
        // Task interface: $8003 Audio_Task → $80FE/$8106
        // ------------------------------------------------------------------ //

        private void TaskEntry(byte id)
        {
            int before = _executionCycles;
            TimingSupported = true;
            Cost(22);
            _c0++;                                             // $80FE: INC $C0 (halt during task)
            TaskDispatch(id);
            _c0--;                                             // $8103: DEC $C0
            LastTaskCycles = _executionCycles - before;
        }

        private void TaskDispatch(byte a)
        {
            if (a >= 0xF0) { Cost(68); SpecialTask(a); return; }         // $8106-$810A
            Cost(12);

            // $810D-$8116: fold id mod $40 ($8A40 = $40)
            while (a >= _rom3[0x0A40]) { Cost(15); a -= _rom3[0x0A40]; }

            // $8118-$8121: 64-entry (hi,lo) sound pointer table at $8A43
            int x = a << 1;
            byte lo = _rom3[0x0A44 + x];
            byte hi = _rom3[0x0A43 + x];
            Cost(16 + ((0x44 + x) > 255 ? 1 : 0) + ((0x43 + x) > 255 ? 1 : 0));
            if ((lo | hi) == 0) { Cost(7); return; }                        // null entry → ignore
            Cost(14 + ((0x43 + x) > 255 ? 1 : 0));

            // $8123-$812A: read the first stream byte (Y=lo, A=hi) via $803A,
            // which leaves $C1:$C2 = the stream address.
            _c1 = lo;
            _c2 = hi;
            byte first = ReadByteAt(_c1, _c2);
            if (first == 0) { Cost(1); MusicStart(); return; }          // music header
            SfxLoad(first, (byte)x);                           // SFX: first byte = priority
        }

        /// <summary>Music start $816F-$81AC. Header: [00][p1.hi p1.lo][p2.hi p2.lo]
        /// [tri.hi tri.lo][noise.hi noise.lo] — big-endian, channel order X=3,2,1,0 =
        /// pulse1, pulse2, triangle, noise; null pointer = unused channel.</summary>
        private void MusicStart()
        {
            Cost(865);
            _c9 = 0x01;                                        // $816F-$8175: default tempo $0199
            _ca = 0x99;
            _c8 = 0x00;
            _cb = 0x00;                                        // $8179: global transpose
            _cc = 0x00; _cd = 0x00;                            // $817B-$817D: fade off
            for (int i = 0x53; i >= 0; i--)                    // $817F-$8185: clear $0728-$077B
                _p7[0x28 + i] = 0x00;

            for (int x = 3; x >= 0; x--)                       // $8187-$81AA (raw X → group-B regs)
            {
                Cost(48 + (x == 0 ? 7 : 5));
                AdvancePtr();                                  // $8189-$818D
                byte hi = ReadByteAt(_c1, _c2);
                W(0x28 + x, F_StreamHi, hi);                   // $8196: STA $0754,X
                AdvancePtr();                                  // $8199-$819D
                byte lo = ReadByteAt(_c1, _c2);
                W(0x28 + x, F_StreamLo, lo);                   // $81A6: STA $0750,X
            }
            SilenceMasked();                                   // $81AC-$81AD: BMI $81F1
        }

        private void AdvancePtr()                              // $8189-$818D / $8199-$819D
        {
            Cost(_c1 == 255 ? 5 : 1);
            if (++_c1 == 0) _c2++;
        }

        /// <summary>SFX load $812C-$816C. first = priority byte (bit7 = chain marker).</summary>
        private void SfxLoad(byte first, byte x)
        {
            Cost(14);
            byte y = 0x00;                                     // $812C: LDY #$00
            _c4 = first;                                       // $812F
            if ((byte)(first & 0x7F) < _ce) { Cost(7); return; }            // $8131-$8135: lower → ignore
            Cost(5);
            Cost((first & 0x7F) != _ce ? 1 : (_d6 & 0x80) == 0 ? 6 : (_c4 & 0x80) != 0 ? 11 : 13);
            _ce = (byte)(first & 0x7F);                        // $8137
            if (_ce == 0)                                      // $8139: BNE skip
            {
                if ((_d6 & 0x80) != 0 && (_c4 & 0x80) == 0)
                    _d7 = y;                                   // $8143: cancel pending chain
            }
            Cost(15);
            _d6 = y;                                           // $8145: STY $D6
            bool bit7 = (_c4 & 0x80) != 0;                     // $8147: ASL $C4
            _c4 = (byte)(_c4 << 1);
            _d6 = bit7 ? (byte)0x80 : (byte)0x00;              // $8149: ROR $D6 (D6 was 0)
            if (bit7) { Cost(3); TimingSupported = false; _d7 = x; }                                 // $814B-$814D: chain source = id*2
            else Cost(1);

            Cost(_c1 == 255 ? 26 : 22);
            if (++_c1 == 0) _c2++;                             // $814F-$8151: skip priority byte
            _d0 = _c1; _d1 = _c2;                              // $8153-$815B
            _d2 = y; _d3 = y; _d4 = y; _d5 = y;                // $815D-$8164
            for (int i = 0x27; i >= 0; i--)                    // $8166-$816C: clear group A state
                _p7[i] = y;
            Cost(419);
        }

        /// <summary>Special engine tasks $F0-$F7 (dispatch table $81B4, decoded (lo,hi)
        /// pairs at $81B5+2k): F0→$81C5, F1→$81C8, F2→$81E4, F3→$821E, F4→$8226,
        /// F5→$822D, F6→$8234, F7→$824A. Params arrive in $C3 ($81AE: STY $C3).</summary>
        private void SpecialTask(byte a)
        {
            switch (a & 7)
            {
                case 0:                                        // $81C5: JSR $81E4, then $81C8
                    Cost(6);
                    ClearMusicStreams();
                    goto case 1;
                case 1:                                        // $81C8-$81E3: full engine reset
                    Cost(_cf != 0 ? 44 : 29);
                    _ce = 0; _d0 = 0; _d1 = 0; _d7 = 0; _d8 = 0;
                    if (_cf != 0)
                    {
                        _cf ^= 0x0F;
                        SilenceMasked();                       // $81DC: JSR $81F1
                        _cf = 0;                               // $81DF-$81E1
                    }
                    break;
                case 2:                                        // $81E4-$81EF: clear music streams
                    ClearMusicStreams();
                    break;
                case 3:                                        // $821E-$8224: pause music
                    Cost(11);
                    _c0 |= 0x02;
                    SilenceMasked();                           // $8224: BNE $81F1 (always)
                    break;
                case 4:                                        // $8226-$822C: resume music
                    Cost(14);
                    _c0 &= 0xFD;
                    break;
                case 5:                                        // $822D-$8233: fade down
                {
                    Cost((byte)(_c3 << 1) == 0 ? 8 : 14);
                    byte r = (byte)(_c3 << 1);                 // ASL $C3 (stores the shift!)
                    _c3 = r;
                    if (r != 0) _c3 = (byte)((r >> 1) | 0x80); // SEC; ROR $C3
                    goto case 6;
                }
                case 6:                                        // $8234-$8249: set fade rate/level
                    Cost(_c3 == 0 ? 24 : _cd == 255 ? 34 : 33);
                    _c0 &= 0x0F;                               // $8236-$8238
                    _cc = _c3;                                 // $823A-$823C
                    if (_c3 != 0)                              // $823E
                        _cd = (_cd == 0xFF) ? (byte)0x00 : (byte)0xFF;  // $8240-$8247
                    break;
                case 7:                                        // $824A-$8251: D8 = -$C3
                    Cost(16);
                    _d8 = (byte)(0 - _c3);
                    break;
            }
        }

        private void ClearMusicStreams()                           // $81E4-$81EF (raw X → $0750/X)
        {
            Cost(63);
            for (int x = 3; x >= 0; x--)
            {
                W(0x28 + x, F_StreamHi, 0);                    // $81E8: STA $0754,X
                W(0x28 + x, F_StreamLo, 0);                    // $81EB: STA $0750,X
            }
            SilenceMasked();
        }

        /// <summary>$81F1-$821D: silence every channel NOT in $CF, invalidate their $4003
        /// dedup if a music stream is active, reset sweeps, enable the APU.</summary>
        private void SilenceMasked()
        {
            Cost(37);
            byte save = _cf;                                   // $81F3: PHA
            for (int x = 3; x >= 0; x--)
            {
                bool bit = (_cf & 0x01) != 0;
                Cost(bit ? 9 : 13);
                _cf = (byte)(_cf >> 1);                        // $81F6: LSR $CF
                if (bit) { Cost(x == 0 ? 4 : 6); continue; }                             // $81F8: BCS $820A (keep)
                SilenceChannel(x);                             // $81FA: JSR $80D8
                bool stream = (R(0x28 + x, F_StreamHi) | R(0x28 + x, F_StreamLo)) != 0;
                Cost(stream ? 17 : 11);
                if (stream)  // $81FD-$8203 ($0750/X)
                    SetLastPeriodHi(x, 0xFF);                  // $8205-$8207
                Cost(x == 0 ? 4 : 6);
            }
            _cf = save;                                        // $820D-$820E: PLA; STA $CF
            _apu.Write(0x4001, 0x08);                          // $8210-$8215: sweeps off
            _apu.Write(0x4005, 0x08);
            _apu.Write(0x4015, 0x0F);                          // $8218-$821A: enable
        }

        /// <summary>$81D4-$81E3: silence the complement of $CF (newly-stolen channels)
        /// and clear $CF. Caller restores the mask afterwards ($82DA).</summary>
        private void SilenceComplementAndClear()
        {
            Cost(_cf == 0 ? 12 : 27);
            if (_cf == 0) return;                              // $81D6: BEQ $81E3
            _cf ^= 0x0F;                                       // $81D8-$81DA
            SilenceMasked();                                   // $81DC: JSR $81F1
            _cf = 0;                                           // $81DF-$81E1
        }

        /// <summary>$80D8-$80EB: silence one channel — $4000,Y = $30 (halt+const+vol 0);
        /// the triangle (Y == 8) gets 0 (linear reload 0).</summary>
        private void SilenceChannel(int x)
        {
            Cost((x & 3) == 1 ? 31 : 30);
            int y = (3 - (x & 3)) << 2;                        // $80D9-$80DF
            byte v = 0x30;                                     // $80E0: LDA #$30
            if (y == 8) v = 0x00;                              // $80E2-$80E6: CPY #$08
            _apu.Write(0x4000 + y, v);                         // $80E8-$80E9
        }

        /// <summary>$80EC-$80FD: register write helper. subReg: 0 = $4000 family,
        /// 2 = period lo, 3 = period hi/length. Target = $4000 + ((3-(X&amp;3))&lt;&lt;2) + subReg.</summary>
        private void WriteReg(int x, int subReg, byte value)
        {
            Cost(36);
            int y = ((3 - (x & 3)) << 2) | subReg;             // $80ED-$80F8
            _apu.Write(0x4000 + y, value);                     // $80FA-$80FB
        }

        // ------------------------------------------------------------------ //
        // SFX tick-block parser: $8252 (SFX_ParseTick) — once per tick
        // ------------------------------------------------------------------ //

        private void SfxParseTick()
        {
            Cost(5);
            if (_d3 != 0)                                      // $8252-$825A
            {
                Cost(16);
                _d3 = (byte)(_d3 - 1);
                _d5 = (byte)(_d5 - 1);
                return;
            }
            Cost(1);
            ParseBlocks();                                     // $825B
        }

        /// <summary>$825B-$82DD: parse one tick block from the global SFX stream.
        /// Block flags: bit7 = chain/stop (ASL test), bit0 = loop point (2 big-endian
        /// bytes → new $D1:$D0), bit1 = optional $D4 (speed), bit2 = optional $D2
        /// (pitch offset); then the length byte ($D3) and the channel mask ($CF).
        /// Bit roles verified against the original punch SFX ($3F): [02 56 05 08]
        /// → D4=$56, len=$05, mask=$08 (noise).</summary>
        private void ParseBlocks()
        {
            while (true)
            {
                Cost(13);
                byte flags = SfxReadByte();                    // $825B: JSR $8386 (Y returns 0)
                _c4 = flags;                                   // $825E

                if ((flags & 0x80) != 0)                       // $8260-$8261: bit7 chain/stop
                {
                    Cost(10);
                    _ce = 0;                                   // $8263: STY $CE (Y = 0)
                    if ((_d7 & 0x01) != 0)                     // $8265-$8268
                    {
                        TimingSupported = false;
                        TaskDispatch((byte)(_d7 >> 1));        // $826A: JSR $8118 (chain next)
                        continue;                              // $826D: JMP $825B
                    }
                    Cost(4);
                    SpecialTask(0xF1);                         // $8270: JMP $81C8 (stop)
                    return;
                }
                Cost(1);

                if ((flags & 0x01) != 0)                       // $8273-$8275: bit0 = loop point
                {                                              // (after the ASL bit7 chain check,
                    Cost(13);
                                                               //  the LSR chain tests bits 0,1,2)
                    byte b1 = (byte)(SfxReadByte() << 1);       // $8277-$827A
                    Cost(b1 == 0 ? 5 : 4);
                    if (b1 == 0) TimingSupported = false;
                    bool finished = false;
                    if (b1 != 0)                               // $827A-$827B: ASL A; BEQ $8289
                    {
                        byte savedBit7 = (byte)(_d6 & 0x80);   // $827D: ASL $D6 (old bit7 → carry)
                        _d6 = (byte)(_d6 << 1);
                        if (b1 == _d6)                         // $8280-$8282: CMP $D6 (b1*2 vs D6*2)
                        {
                            Cost(25);
                            // $8296-$8299: TYA; PLP; ROR A; STA $D6 — D6 collapses to its
                            // old bit7 (low bits lost — verbatim).
                            _d6 = savedBit7;
                            finished = true;
                        }
                        else
                        {
                            Cost(27);
                            _d6 = (byte)((_d6 >> 1) | savedBit7);  // $8284-$8285: PLP; ROR $D6
                            _d6 = (byte)(_d6 + 1);                 // $8287: INC $D6
                        }
                    }
                    if (!finished)
                    {
                        Cost(20);
                        byte b2 = SfxReadByte();               // $8289-$828C
                        byte b3 = SfxReadByte();               // $828D
                        _d0 = b3; _d1 = b2;                    // $8290-$8292: big-endian new ptr
                        if (b2 != 0) { Cost(3); continue; }
                        Cost(2);
                        TimingSupported = false;
                    }
                    int d0 = _d0 + 2;                          // $829B-$82A5: D0 += 2
                    Cost(d0 > 255 ? 17 : 13);
                    _d0 = (byte)d0;
                    if (d0 > 0xFF) _d1++;
                }

                Cost((flags & 1) == 0 ? 8 : 0);
                Cost((flags & 2) == 0 ? 8 : 16);
                if ((flags & 0x02) != 0)                       // $82A6-$82AD: bit1 → $D4
                    _d4 = SfxReadByte();
                Cost((flags & 4) == 0 ? 8 : 16);
                if ((flags & 0x04) != 0)                       // $82AF-$82B6: bit2 → $D2
                    _d2 = SfxReadByte();

                byte len = SfxReadByte();                      // $82B8-$82BB: length byte
                Cost(24);
                _d3 = len;
                _c1 = len;                                     // $82BD
                _c4 = _d4;                                     // $82BF-$82C1
                Multiply();                                    // $82C3: $D3 × $D4 → $C1:$C2
                Cost(27);
                _d5 = (byte)(_c1 + 1);                         // $82C6-$82C9: block frames
                _c0++;                                         // $82CB: INC $C0 (parse mode)
                byte mask = SfxReadByte();                     // $82CD: channel mask
                if (mask != _cf)                               // $82D1-$82D3
                {
                    Cost(9);
                    _cf = (byte)(mask ^ _cf);
                    SilenceComplementAndClear();               // $82D7: JSR $81D4
                }
                else Cost(1);
                Cost(13);
                _cf = mask;                                    // $82DA-$82DB: PLA; STA $CF
                return;                                        // $82DD
            }
        }

        // ------------------------------------------------------------------ //
        // SFX channel tick: $82DE (SFX_ChannelTick), X = 0..3
        // ------------------------------------------------------------------ //

        private void SfxChannelTick(int x)
        {
            Cost(R(x, F_Instrument) != 0 ? 12 : 7);
            if (R(x, F_Instrument) != 0)                       // $82DE-$82E3
                SetInstrumentPtr(x);                           //   JSR $8684 ($C5:$C6 = record)

            if ((_c0 & 0x01) != 0)                             // $82E6-$82E9: halt → parse mode
            {
                Cost(9);
                SfxInitNote(x);                                //   $830A
                return;
            }

            Cost(13);
            Envelope(x);                                       // $82EB: JSR $86BA

            Cost(_d3 == 0 ? 12 : 5);
            if (_d3 == 0) return;                              // $82EE-$82F0
            if (x == 1)                                        // $82F2-$82F4: triangle timer
            {
                Cost(12);
                int t = _p7[x + F_EnvTimer] - 1;               // $82FB: DEC $0710,X
                _p7[x + F_EnvTimer] = (byte)t;
                if (t != 0) { Cost(10); return; }                            // $82FE: BNE $82FA
                Cost(2);
            }
            else if (_d5 != 0) { Cost(15); return; }                         // $82F6-$82F8
            else Cost(11);

            Cost(8);
            if ((R(x, F_Flags) & 0x04) != 0) { Cost(8); return; }           // $8300-$8305: finished bit
            Cost(3);
            NoteOff(x);                                        // $8307: JMP $85A3
        }

        /// <summary>$830A-$8383: SFX init + note parse (entered with $C0 bit0 set). Reads an
        /// init-flag byte; each set bit 0-4 consumes one param byte dispatched through
        /// table $8328 (bit0 instrument $866F, bit1 duty $86AD, bit2 volume $865A,
        /// bit3 slide/vibrato depth $86A7, bit4 vibrato rate $86A1, bit5 $8620); then one
        /// note byte (0 = end of block: silence channel, phase 4).</summary>
        private void SfxInitNote(int x)
        {
            Cost(11);
            byte handlerIdx = 0;                               // $830A-$830C
            byte flagsLeft = SfxReadByte();                    // $830E
            while (true)                                       // $8311-$8324
            {
                bool bit = (flagsLeft & 0x01) != 0;            // $8311: LSR A
                flagsLeft = (byte)(flagsLeft >> 1);
                if (bit)
                {
                    Cost(85);
                    _c3 = SfxReadByte();                       // $8315-$8318: param
                    switch (handlerIdx)                        // table $8328 (lo,hi at $8329+2k)
                    {
                        case 0: InstrumentChange(x); break;    // $866F
                        case 1: SetDuty(x); break;             // $86AD
                        case 2: SetVolume(x); break;           // $865A
                        case 3: Cost(14); W(x, F_SlideAmt, _c3); break;  // $86A7
                        case 4: Cost(14); W(x, F_VibRate, _c3); break;   // $86A1
                        case 5: TimingSupported = false; W(x, F_Flags, (byte)(_c3 & 0xDF)); break;  // $8620 (unused by data)
                        default: TimingSupported = false; break;                        // entries 6/7: ROM garbage, unreachable
                    }
                }
                else Cost(5);
                Cost(flagsLeft == 0 ? 3 : 10);
                if (flagsLeft == 0) break;                     // $8320: BEQ $8333
                handlerIdx++;                                  // $8322: INC $C4
            }

            byte note = SfxReadByte();                         // $8333-$8336 (JSR $8386; TAY)
            Cost(note == 0 ? 10 : 11);
            if (note == 0)                                     // $8337-$8346: end of block
            {
                Cost(24);
                W(x, F_EnvTimer, 0);                           // $8339
                W(x, F_Flags, (byte)((R(x, F_Flags) & 0xF8) | 0x04));  // $833C-$8343: phase 4
                SilenceChannel(x);                             // $8346: JMP $80D8
                return;
            }
            Cost(31);

            W(x, F_Flags, (byte)(R(x, F_Flags) | 0x20));       // $8349-$834E: note-on flag
            bool slideUp = (R(x, F_SlideAmt) & 0x80) != 0;     // $8351-$8354: ASL → carry = bit7
            Cost(slideUp ? 0 : 1);
            W(x, F_SlideTarget, (byte)(slideUp ? 0x54 : 0x0A));// $8355-$835B
            byte y = note;
            if (note >= 0x80)                                  // $835E: TYA; BPL $836B
            {
                Cost(x == 1 ? 13 : 8);
                if (x == 1) EnvRestart(x, ref y);              // $8361-$8365: triangle only
                GateCheck(x);                                  // $8368: JMP $8644
                return;                                        // (JMP — no return to init flow)
            }
            Cost(7);
            EnvRestart(x, ref y);                              // $836B: JSR $85AE
            Cost(13);
            SetLastPeriodHi(x, 0xFF);                          // $836E-$8370: invalidate dedup
            y--;                                               // $8373: DEY (note-1)
            if (x == 0)                                        // $8374-$837C: noise channel
            {
                Cost(10);
                _c3 = 0;                                       // $8377: STA $C3 (A = X = 0)
                SetPeriodHiLo(x, (byte)(y ^ 0x0F), _c3);       // $8379-$837C: JMP $8636
                return;                                        // (JMP — no return to init flow)
            }
            Cost(11);
            SetPeriodFromIndex(x, (byte)((y + _d2) & 0xFF));   // $837F-$8383: pitch = note-1 + $D2
            return;                                            // (JMP $85DE — no return to init flow)
        }

        // ------------------------------------------------------------------ //
        // Music channel tick: $8393 (Music_ChannelTick), X = 0..3 (uses X|=$28)
        // ------------------------------------------------------------------ //

        private void MusicChannelTick(int x)
        {
            Cost(16);
            int m = x | 0x28;                                  // $8393-$8396

            if ((R(m, F_StreamLo) | R(m, F_StreamHi)) == 0)    // $8397-$839D: unused channel
            {
                Cost(7);
                return;
            }

            Cost(6);
            if (R(m, F_PitchHold) == 0)                        // $839F-$83A2
            {
                Cost(1);
                FetchEvents(m);                                // $83CD (envelope skipped this tick)
                return;
            }

            if (R(m, F_Instrument) != 0)                     // $83A4-$83AC
            {
                Cost(18);
                SetInstrumentPtr(m);                         // $83A9: JSR $8684
                Envelope(m);                                 // $83AC: JSR $86BA
            }
            else Cost(7);

            // $83AF-$83BC: gate duration $0740 -= $C7 (0 or borrow → note off)
            int d = R(m, F_Duration) - _c7;                  // SEC; SBC $C7
            W(m, F_Duration, (byte)d);
            Cost(d == 0 ? 17 : d < 0 ? 18 : 19);
            if (d == 0 || d < 0)                             // $83B8: BEQ / $83BA: BCS
            {
                Cost(6);
                NoteOff(m);                                  // $83BC: JSR $85A3
            }

            // $83BF-$83CA: hold counter $0738 -= $C7 (0 or borrow → fetch next events)
            int h = R(m, F_PitchHold) - _c7;
            W(m, F_PitchHold, (byte)h);
            Cost(h == 0 ? 17 : h < 0 ? 19 : 24);
            if (h == 0 || h < 0)                             // $83C8: BEQ / $83CA: BCC
                FetchEvents(m);
        }

        /// <summary>$83CD-$83D7 event loop: bytes &lt; $20 = commands (dispatch $8497),
        /// bytes >= $20 = note events. Stream end ($17) unwinds straight to the tick loop.</summary>
        private void FetchEvents(int m)
        {
            while (true)
            {
                byte b = ReadStreamByte(m);                    // $83CD: JSR $8592
                Cost(b >= 0x20 ? 11 : 16);
                if (b >= 0x20) { NoteEvent(m, b); return; }    // $83D2: BCS $83DA
                if (MusicCommand(m, b)) return;                // $83D4: JSR $8497; $17 → unwind
                Cost(3);
            }
        }

        // ------------------------------------------------------------------ //
        // Note events: $83DA-$8496
        // ------------------------------------------------------------------ //

        private void NoteEvent(int m, byte b)
        {
            Cost(27);
            // $83DA-$83E2: PHA; ROL A x4; AND #$07; TAY; DEY. With carry-in 1 (the
            // CMP #$20 that routed here set it) the four ROLs compute (b >> 5) & 7,
            // so the multiplier index = (b >> 5 & 7) - 1 (index -1 reads $8914 = $60).
            int idx = ((b >> 5) & 7) - 1;

            byte nf = R(m, F_NoteFlags);                       // $83E3
            int mult;
            if ((nf & 0x20) != 0)                              // $83E6-$83E8: bit5 set → 2^n table
            {
                Cost(8 + (idx < 0 ? 1 : 0));
                mult = _rom3[0x0915 + idx];                    // $83EA: LDA $8915,Y
            }
            else
            {
                Cost(((nf & 0x10) != 0 ? 11 : 13) + (idx < 0 ? 1 : 0));
                mult = _rom3[0x091C + idx];                    // $83F1: LDA $891C,Y (3·2^n table)
                if ((nf & 0x10) != 0)                          // $83F4: BCC not taken (bit4 = dotted)
                {
                    Cost(24);
                    _c3 = (byte)mult;                          // $83F6
                    nf &= 0xEF;                                // $83F8-$83FD: clear bit4
                    W(m, F_NoteFlags, nf);
                    mult = (mult >> 1) + mult;                 // $8400-$8404: 1.5× (LSR; ADC)
                }
            }

            int hold = (mult + R(m, F_PitchHold)) & 0xFF;    // $8406-$840A: CLC; ADC $0738,X
            Cost(21);
            W(m, F_PitchHold, (byte)hold);

            int code = b & 0x1F;                             // $840E-$840F: low 5 bits
            if (code == 0)                                   // $8411-$8416: note off
            {
                Cost(22);
                NoteOff(m);                                  // $8413: JSR $85A3
                W(m, F_Duration, 0xFF);                      // $8491-$8493 (JMP $8491: no flag rotation)
                return;
            }
            Cost(23);

            // $8419-$842A: gate frames = high byte of LengthUnit × hold
            byte y = (byte)hold;                             // $841A: STY $C4
            _c4 = y;
            _c1 = R(m, F_LengthUnit);                        // $841C-$841F
            Multiply();                                      // $8421: JSR $8006
            Cost(_c1 != 0 ? 22 : 23);
            W(m, F_Duration, _c1 != 0 ? _c1 : (byte)0x01);   // $8424-$842A (min 1)

            y = (byte)(code - 1);                            // $842D-$842F: PLA; TAY; DEY

            if ((R(m, F_NoteFlags) & 0x80) != 0)             // $8430-$8433: legato/slide bit
            {
                Cost(6);
                if (R(m, F_SlideAmt) != 0)                   // $8435-$8438: slide active → keep
                {
                    Cost(1);
                    SetPitch(m, y);                          // $8454→$8466/$8459 (no restart)
                }
                else
                {
                    Cost(9);
                    GateCheck(m);                            // $843A: JSR $8644
                    PostNoteFlags(m);                        // $843D: JMP $847E
                }
                return;
            }

            Cost(12);
            EnvRestart(m, ref y);                            // $8440: JSR $85AE
            if ((_cf & 0x80) == 0)                           // $8443-$8445: not stolen
            {
                Cost(19);
                SetLastPeriodHi(m, 0xFF);                    // $8447-$844F: invalidate dedup
            }
            else Cost(1);
            SetPitch(m, y);                                  // $8454-$847B
        }

        /// <summary>$8454-$847B: pitch store — noise uses the inverted duration code,
        /// other channels $8923[flags&amp;$0F] + (code-1) + $CB + $0734,X via $85DE.</summary>
        private void SetPitch(int m, byte y)
        {
            Cost(6);
            if ((m & 3) == 0)                                // $8454-$8457
            {
                Cost(18);
                _c3 = 0;                                     // $8459: STA $C3 (A = 0)
                SetPeriodHiLo(m, (byte)((y & 0x0F) ^ 0x0F), _c3);  // $845B-$8460
            }
            else                                             // $8466-$847B
            {
                Cost(38);
                int pitch = _rom3[0x0923 + (R(m, F_NoteFlags) & 0x0F)]   // $846E: LDA $8923,Y
                          + y + _cb + R(m, F_Transpose);     // $8471-$847B
                SetPeriodFromIndex(m, (byte)pitch);
            }
            PostNoteFlags(m);                                // $847E
        }

        /// <summary>$847E-$8496: rotate $0730 bit6 → bit7 (legato marker for the NEXT note);
        /// when the new bit7 is set the gate duration is forced to $FF.</summary>
        private void PostNoteFlags(int m)
        {
            Cost(27);
            byte t = R(m, F_NoteFlags);                        // $847E-$8481
            byte nf = (byte)((t & 0x7F) | ((t & 0x40) << 1));  // $8482-$848C
            W(m, F_NoteFlags, nf);
            if ((nf & 0x80) != 0)                              // $848F: BPL not taken
            {
                Cost(13);
                W(m, F_Duration, 0xFF);                        // $8491-$8493
            }
            else Cost(7);
        }

        // ------------------------------------------------------------------ //
        // Music command dispatch: $8497 + table $84A6 (25 entries $00-$18)
        // ------------------------------------------------------------------ //

        /// <summary>Dispatch one music command. Returns true when the channel stream
        /// ended ($8580 pops both loop returns — unwinds to the tick loop).</summary>
        private bool MusicCommand(int m, byte cmd)
        {
            Cost(cmd >= 0x04 ? 75 : 61);
            if (cmd >= 0x04)                                   // $8497-$84A2
            {
                _c4 = cmd;
                _c3 = ReadStreamByte(m);                       // $849D: param byte
                cmd = (byte)_c4;
            }

            switch (cmd)                                       // table $84A6: JSR $8023 computed
            {                                                  // jump; entries (lo,hi) at $84A7+2k
                case 0x00:                                     // $84D9: EOR #$20 (toggle)
                    Cost(20);
                    W(m, F_NoteFlags, (byte)(R(m, F_NoteFlags) ^ 0x20)); break;
                case 0x01:                                     // $84DD: EOR #$40 (toggle)
                    Cost(20);
                    W(m, F_NoteFlags, (byte)(R(m, F_NoteFlags) ^ 0x40)); break;
                case 0x02:                                     // $84E1: ORA #$10 (set)
                    Cost(20);
                    W(m, F_NoteFlags, (byte)(R(m, F_NoteFlags) | 0x10)); break;
                case 0x03:                                     // $84E8: EOR #$08 (toggle)
                    Cost(17);
                    W(m, F_NoteFlags, (byte)(R(m, F_NoteFlags) ^ 0x08)); break;
                case 0x04:                                     // $8575: OR param into $0730
                    Cost(20);
                    W(m, F_NoteFlags, (byte)((R(m, F_NoteFlags) & 0x97) | _c3)); break;
                case 0x05:                                     // $84F1: tempo = param:$next (big-endian)
                {
                    Cost(26);
                    _c8 = 0x00;                                // $84F1-$84F3
                    byte lo = ReadStreamByte(m);               // $84F5: JSR $8592
                    _ca = lo;                                  // $84FA
                    _c9 = _c3;                                 // $84FC: STY $C9
                    break;
                }
                case 0x06:                                     // $84FF: note length unit
                    Cost(14);
                    W(m, F_LengthUnit, _c3); break;
                case 0x07:                                     // $865A: duty/volume nibble
                    SetVolume(m); break;
                case 0x08:                                     // $866F: instrument change
                    InstrumentChange(m); break;
                case 0x09:                                     // $8505: control nibble → $0730 bits 0-2
                    Cost(20);
                    W(m, F_NoteFlags, (byte)((R(m, F_NoteFlags) & 0xF8) | _c3)); break;
                case 0x0A:                                     // $8510: global transpose
                    Cost(12);
                    _cb = _c3; break;
                case 0x0B:                                     // $8515: channel transpose
                    Cost(14);
                    W(m, F_Transpose, _c3); break;
                case 0x0C:                                     // $86A1: vibrato rate
                    Cost(14);
                    W(m, F_VibRate, _c3); break;
                case 0x0D:                                     // $86A7: vibrato depth / slide amount
                    Cost(14);
                    W(m, F_SlideAmt, _c3); break;
                case 0x0E: VolumeDecay(m, 0x00, false); break; // $851B
                case 0x0F: VolumeDecay(m, 0x04, false); break; // $851F
                case 0x10: VolumeDecay(m, 0x08, false); break; // $8523
                case 0x11: VolumeDecay(m, 0x0C, false); break; // $8527
                case 0x12: VolumeDecay(m, 0x00, true); break;  // $851B (second copy)
                case 0x13: VolumeDecay(m, 0x04, true); break;  // $851F
                case 0x14: VolumeDecay(m, 0x08, true); break;  // $8523
                case 0x15: VolumeDecay(m, 0x0C, true); break;  // $8527
                case 0x16:                                     // $855A: set stream ptr [param=hi][lo]
                {
                    Cost(25);
                    byte lo = ReadStreamByte(m);               // $855A-$855D
                    W(m, F_StreamLo, lo);
                    W(m, F_StreamHi, _c3);                     // $8560-$8562
                    break;
                }
                case 0x17:                                     // $8580-$8591: end of channel stream
                {
                    Cost((_cf & 0x80) != 0 ? 32 : 28);
                    W(m, F_StreamLo, 0);                       // $8584
                    W(m, F_StreamHi, 0);                       // $8587
                    if ((_cf & 0x80) == 0)                     // $858A-$858C
                        SilenceChannel(m);                     // $858E: JMP $80D8
                    return true;                               // PLA;PLA;RTS → unwind
                }
                case 0x18:                                     // $86AD: duty
                    SetDuty(m); break;
                default:
                    TimingSupported = false;
                    break;
            }
            return false;
        }

        /// <summary>$851B-$8574: per-duty volume-decay counters ($0744 + X + 4*slot; note
        /// this region overlaps $0750+ for slot 3 exactly as in the original). The
        /// $0E-$11 variants reload from the param on wraparound; the $12-$15 variants
        /// fire when the counter reaches 1 (the decrement itself is discarded).</summary>
        private void VolumeDecay(int m, int slot, bool second)
        {
            if (slot != 0) TimingSupported = false;
            Cost(slot == 0x0C ? 21 : 24);
            Cost(second ? 1 : 0);
            int addr = 0x44 + m + slot;                        // $8529-$852F: Y = X + slot
            if (!second)                                       // cmds $0E-$11 ($8530-$8545)
            {
                int v = _p7[addr] - 1;
                Cost(v < 0 ? 13 : 11);
                if (v < 0) v = _c3;                            // $853C-$853E: borrow → param
                _p7[addr] = (byte)v;
                Cost(v == 0 ? 8 : 10);
                if (v == 0) { AdvanceStreamPtr(m); return; }   // $8543: BEQ $8566 (skip pair)
            }
            else                                               // cmds $12-$15 ($8547-$8552)
            {
                int v = _p7[addr] - 1;
                Cost(v != 0 ? 11 : 41);
                if (v != 0) { AdvanceStreamPtr(m); return; }   // $854D: BNE $8566 (decrement discarded)
                _p7[addr] = 0;                                 // $854F
                W(m, F_NoteFlags, (byte)((R(m, F_NoteFlags) & 0x97) | _c3));  // $8552: JSR $8575
            }
            Cost(34);
            byte hi = ReadStreamByte(m);                       // $8555-$8558: STA $C3
            _c3 = hi;
            byte lo = ReadStreamByte(m);                       // $855A-$855D
            W(m, F_StreamLo, lo);
            W(m, F_StreamHi, _c3);                             // $8560-$8562
        }

        /// <summary>$8566-$8574: stream pointer += 2 (16-bit).</summary>
        private void AdvanceStreamPtr(int m)
        {
            int lo = R(m, F_StreamLo) + 2;
            Cost(lo > 255 ? 28 : 22);
            W(m, F_StreamLo, (byte)lo);
            if (lo > 0xFF) W(m, F_StreamHi, (byte)(R(m, F_StreamHi) + 1));
        }

        // ------------------------------------------------------------------ //
        // Stream / period helpers
        // ------------------------------------------------------------------ //

        /// <summary>$8592-$85A0: read one byte from the channel stream ($0728/$072C,X)
        /// and advance the pointer.</summary>
        private byte ReadStreamByte(int m)
        {
            byte lo = R(m, F_StreamLo);
            Cost(lo == 255 ? 27 : 21);
            byte hi = R(m, F_StreamHi);
            byte v = ReadByteAt(lo, hi);
            if (++lo == 0) hi++;
            W(m, F_StreamLo, lo);
            W(m, F_StreamHi, hi);
            return v;
        }

        /// <summary>$8386-$8390: read one byte from the global SFX stream ($D1:$D0).</summary>
        private byte SfxReadByte()
        {
            Cost(_d0 == 255 ? 21 : 17);
            byte v = ReadByteAt(_d0, _d1);
            if (++_d0 == 0) _d1++;
            return v;
        }

        /// <summary>$803A-$806B: read the byte at (lo, hi). Pointers with hi &lt; $C0 read
        /// bank 3; hi >= $C0 reads the fixed bank (the original performs an MMC1 window
        /// dance at $8047-$806B; the logical fixed-bank read is equivalent. The path is
        /// never taken by any game audio data). Reads below $8000 (a cleared pointer)
        /// yield 0 where the original would read zero-page RAM.</summary>
        private byte ReadByteAt(byte lo, byte hi)
        {
            Cost(23);
            int addr = (hi << 8) | lo;
            if (hi < 0xC0)
                return addr < 0x8000 ? (byte)0 : _rom3[addr - 0x8000];
            TimingSupported = false;
            if (_rom7 == null || addr < 0xC000) return 0;
            return _rom7[addr - 0xC000];
        }

        /// <summary>$85DE-$863D: set the channel period from the pitch index in
        /// <paramref name="index"/>, including the music slide-target logic.</summary>
        private void SetPeriodFromIndex(int m, byte index)
        {
            Cost(index >= 0x60 ? 18 : 17);
            _c3 = index;
            int idx = index;
            if (idx >= 0x60) idx = 0x5F;                       // $85DE-$85E2: cap at $5F
            idx++;                                             // $85E6: INC $C3
            _c3 = (byte)idx;

            if (m >= 0x28)                                     // $85E8-$85EA: music only
            {
                Cost(6);
                int target = R(m, F_SlideTarget);              // $85EC
                if (target != 0)                               // $85EF: BEQ $861D
                {
                    Cost(5);
                    if (target == _c3)                         // $85F1-$85F3
                    {
                        Cost(6);
                        if ((R(m, F_NoteFlags) & 0x80) != 0)   // $85F5-$85FA: BMI $8644
                        {
                            Cost(3);
                            GateCheck(m);                      // legato: keep current period
                            return;
                        }
                        Cost(2);
                    }
                    else if (R(m, F_SlideAmt) != 0)            // $85FC-$85FF
                    {
                        Cost(40 + (target >= _c3 ? 5 : 7));
                        bool up = target >= _c3;               // carry from CMP $C3 ($85F1)
                        byte amt = R(m, F_SlideAmt);
                        amt = up ? (byte)(amt & 0x7F) : (byte)(amt | 0x80);  // $8601-$8609
                        W(m, F_SlideAmt, amt);
                        W(m, F_Flags, (byte)(R(m, F_Flags) | 0x20));         // $860C-$8611
                        _c3 = (byte)target;                    // $8616-$8619: STY $C3 (old target)
                        W(m, F_SlideTarget, (byte)idx);        // $8627: new target = A (= idx)
                        StorePeriod(m);                        // $862A (period of the OLD target)
                        return;
                    }
                    else Cost(8);
                }
                else Cost(2);
                Cost(19);
                W(m, F_Flags, (byte)(R(m, F_Flags) & 0xDF));   // $861D-$8622: clear slide-active
                W(m, F_SlideTarget, (byte)_c3);                // $8625-$8627: target = new index
            }
            else Cost(2);
            StorePeriod(m);                                    // $862A
        }

        /// <summary>$862A-$863D: period = little-endian word at $8959 + 2*$C3;
        /// stored as $0724,X = hi byte, $0720,X = lo byte.</summary>
        private void StorePeriod(int m)
        {
            Cost(32);
            int y = _c3 << 1;                                  // $862A-$862C: ASL $C3
            byte lo = _rom3[0x0959 + y];                       // $862E
            byte hi = _rom3[0x095A + y];                       // $8633
            Cost((0x59 + y > 255 ? 1 : 0) + (0x5A + y > 255 ? 1 : 0));
            W(m, F_PeriodHi, hi);                              // $8636
            W(m, F_PeriodLo, lo);                              // $863B
            Cost(9 + InstrumentPageCross(4) + ((Instr(4) & 0x80) != 0 ? 1 : 0));
            GateCheck(m, (Instr(4) & 0x80) != 0);
        }

        /// <summary>$8636-$863D: $0724,X = hi; $0720,X = $C3 (used by the noise paths).</summary>
        private void SetPeriodHiLo(int m, byte hi, byte lo)
        {
            Cost(22 + InstrumentPageCross(4) + ((Instr(4) & 0x80) != 0 ? 1 : 0));
            W(m, F_PeriodHi, hi);
            W(m, F_PeriodLo, lo);
            GateCheck(m, (Instr(4) & 0x80) != 0);
        }

        /// <summary>$8644-$8659: if the instrument-reloaded flag (bit3) is set, reset the
        /// decay accumulator and clear flags bits 3, 4 and 6.</summary>
        private void GateCheck(int m, bool force = false)
        {
            if (force) Cost(24);
            else Cost((R(m, F_Flags) & 8) == 0 ? 14 : 33);
            if (!force && (R(m, F_Flags) & 0x08) == 0) return; // $8644-$864B
            W(m, F_DecayAcc, 0);                               // $864C-$864E
            W(m, F_Flags, (byte)(R(m, F_Flags) & 0x37));       // $8651-$8656
        }

        /// <summary>$85A3-$85AD: note off — envelope phase = 3 (release).</summary>
        private void NoteOff(int m)
        {
            Cost(19);
            W(m, F_Flags, (byte)((R(m, F_Flags) & 0xF8) | 0x03));
        }

        /// <summary>$85AE-$85DD: envelope restart on note-on. Y (the duration code-1)
        /// flows through untouched. Triangle channels jump straight to phase 2:
        /// music triangle gets timer 1; SFX triangle gets timer = high byte of
        /// ($D3 × control) + 1; everything else keeps phase 0 with timer 0.</summary>
        private void EnvRestart(int m, ref byte y)
        {
            Cost(41);
            byte saved = y;                                    // $85AE-$85AF: TYA; PHA
            W(m, F_Flags, (byte)(R(m, F_Flags) & 0xF8));       // $85B2-$85B7: phase = 0
            if (m == 0x29)                                     // $85BA-$85BC: music triangle
            {
                Cost(17);
                y = 1;                                         // $85B0: LDY #$00; $85D0: INY
            }
            else if (m == 0x01)                                // $85BE-$85C0: SFX triangle
            {
                Cost(42);
                _c1 = _d3;                                     // $85C2-$85C4
                _c4 = R(m, F_Control);
                Multiply();                                    // $85CB: JSR $8006
                y = _c1;                                       // $85CE: LDY $C1
                y++;                                           // $85D0: INY
            }
            else
            {
                Cost(5);
                W(m, F_EnvTimer, 0);                           // $85D7-$85D8: TYA (Y=0); STA
                y = saved;                                     // $85DB-$85DC: PLA; TAY
                return;
            }
            byte f = (byte)(R(m, F_Flags) + 1);                // $85D1: INC $0704,X
            W(m, F_Flags, f);
            W(m, F_Flags, (byte)(f + 1));                      // $85D4: INC $0704,X → phase 2
            W(m, F_EnvTimer, y);                               // $85D7-$85D8
            y = saved;                                         // $85DB-$85DC: PLA; TAY
        }

        // ------------------------------------------------------------------ //
        // Instrument / volume / duty handlers
        // ------------------------------------------------------------------ //

        /// <summary>$866F-$86A0: instrument change (param+1, 1-based; reloads $C5:$C6).</summary>
        private void InstrumentChange(int m)
        {
            Cost(14);
            byte instr = (byte)(_c3 + 1);                      // $866F-$8671: INC $C3
            if (instr == R(m, F_Instrument)) { Cost(7); return; }           // $8673-$8676
            Cost(18);
            W(m, F_Instrument, instr);                         // $8678
            W(m, F_Flags, (byte)(R(m, F_Flags) | 0x08));       // $867C-$8681: instr-loaded flag
            SetInstrumentPtr(m);                               // $8684 (fall-through)
        }

        /// <summary>$8684-$86A0: $C5:$C6 = $8AC3 + (instrument-1)*8 (absolute address;
        /// the asm adds the offset to the stored base pointer $8A41:$8A42 = $8A:$C3).</summary>
        private void SetInstrumentPtr(int m)
        {
            Cost(55);
            int addr = 0x8AC3 + (((R(m, F_Instrument) - 1) & 0xFF) * 8);
            _c5 = (byte)(addr & 0xFF);
            _c6 = (byte)(addr >> 8);
        }

        /// <summary>($C5),Y — instrument record byte i (record base $8AC3).</summary>
        private byte Instr(int i) => _rom3[((_c6 << 8) | _c5) - 0x8000 + i];

        /// <summary>$865A-$866E: duty/volume command (cmd $07 / init bit2). The SFX
        /// triangle (X == 1) stores the param directly when nonzero; other channels
        /// merge into the control byte with halt+constant-volume ($30) set.</summary>
        private void SetVolume(int m)
        {
            Cost(m == 1 ? (_c3 != 0 ? 21 : 31) : 27);
            if (m == 0x01 && _c3 != 0)                         // $865A-$8660
            {
                W(m, F_Control, _c3);                          // $866B
                return;
            }
            W(m, F_Control, (byte)((R(m, F_Control) & 0xC0) | _c3 | 0x30));  // $8662-$866B
        }

        /// <summary>$86AD-$86B9: duty command (cmd $18 / init bit1):
        /// control = (control &amp; $0F) | param | $30.</summary>
        private void SetDuty(int m)
        {
            Cost(22);
            W(m, F_Control, (byte)((R(m, F_Control) & 0x0F) | _c3 | 0x30));
        }

        // ------------------------------------------------------------------ //
        // Envelope + register writes: $86BA-$8914
        // ------------------------------------------------------------------ //

        private void Envelope(int m)
        {
            Cost(69);
            _c4 = R(m, F_EnvTimer);                            // $86BA-$86BD
            int phase = R(m, F_Flags) & 0x07;                  // $86BF-$86C2 (table $86C6)

            switch (phase)                                     // 0→$86D1, 1→$86E6, 2→$8720, 3→$8702, ≥4→$8914
            {
                case 0:                                        // $86D1-$86E4: attack
                {
                    int t = _c4 + DurTable(Instr(0));          // $86D6-$86D9: instr[0] duration
                    Cost(t > 255 ? 39 : t >= 240 ? 42 : 31);
                    Cost(InstrumentPageCross(0));
                    if (t > 0xFF || t >= 0xF0)                 // $86DC: BCS / $86DE-$86E0: CMP #$F0
                    {
                        _c4 = 0xF0;                            // $86E2
                        W(m, F_Flags, (byte)(R(m, F_Flags) + 1));  // $871A: INC phase
                    }
                    else
                        _c4 = (byte)t;                         // $871D (no phase change)
                    break;
                }
                case 1:                                        // $86E6-$86FF: decay
                {
                    int d = Instr(1);                          // instr[1]
                    Cost(9 + InstrumentPageCross(1));
                    if (d == 0)                                // $86EA: BEQ $86FB
                    {
                        Cost(23 + InstrumentPageCross(2));
                        _c4 = Instr(2);
                        W(m, F_Flags, (byte)(R(m, F_Flags) + 1));
                        break;
                    }
                    int t = _c4 - DurTable(d);                 // $86EC-$86F3
                    Cost(13);
                    if (t < 0) Cost(23 + InstrumentPageCross(2));
                    else Cost(t < Instr(2) ? 31 + 2 * InstrumentPageCross(2) : 16 + InstrumentPageCross(2));
                    if (t < 0 || t < Instr(2))                 // $86F3 BCC / $86F7-$86F9 CMP instr[2]
                    {
                        _c4 = Instr(2);                        // $86FB-$86FD
                        W(m, F_Flags, (byte)(R(m, F_Flags) + 1));
                    }
                    else
                        _c4 = (byte)t;                         // $871D (stay in decay)
                    break;
                }
                case 2:
                    break;
                case 3:                                        // $8702-$871D: release
                {
                    if ((m & 3) == 1)                          // $8702-$8707: triangle → 0
                    {
                        Cost(23);
                        _c4 = 0;                               // $8718
                        W(m, F_Flags, (byte)(R(m, F_Flags) + 1));
                        break;
                    }
                    int rel = Instr(3);                        // $8709-$870D: instr[3]
                    Cost(17 + InstrumentPageCross(3));
                    if (rel == 0) { Cost(1); break; }                       // $870D: BEQ $8720 (unchanged)
                    int t = _c4 - DurTable(rel);               // $870F-$8716
                    Cost(t >= 0 ? 19 : 27);
                    if (t >= 0) _c4 = (byte)t;                 // $8716: BCS $871D
                    else
                    {
                        _c4 = 0;                               // $8718
                        W(m, F_Flags, (byte)(R(m, F_Flags) + 1));  // $871A
                    }
                    break;
                }
                default:
                    Cost(6);
                    return;                                    // $8914: phases 4-7 idle (no writes)
            }
            W(m, F_EnvTimer, (byte)_c4);                       // $871D: STA $0710,X

            // ---------------- $8720+: volume + period register writes ----------------
            if (m >= 0x28 && (_cf & 0x80) != 0)                // $8720-$8728: music + stolen
            {
                Cost(12);
                StolenTail(m);                                 // $88A0: skip register writes
                return;
            }
            Cost(m >= 0x28 ? 10 : 5);

            byte? fadeVal = null;
            if (m >= 0x28)                                     // $872B-$8735: music fade check
            {
                int fv = _cd;
                if ((_cc & 0x80) == 0) fv = (~_cd) & 0xFF;     // $8731: EOR #$FF (up-fade level)
                if (fv != 0xFF) fadeVal = (byte)fv;            // $8733-$8735: CMP #$FF → inactive
                Cost(((_cc & 128) != 0 ? 13 : 14) + (fv != 255 ? 1 : 0));
            }
            VolumeAndRegisters(m, fadeVal);                    // $8737-$87AC: control write
            WritePeriodRegisters(m);                           // $87AF-$889D: $4002/$4003
            StolenTail(m);                                     // $88A0-$8914: shared tail
        }

        /// <summary>$8933 duration-table lookup (29 monotonic entries at $8933-$894F;
        /// indices $1D-$1F continue into $8950-$8952 = $7F/$FE/$FF exactly like the
        /// original's table overrun, which instruments $01/$08 etc. rely on).</summary>
        private int DurTable(int i) => _rom3[0x0933 + (i & 0xFF)];

        /// <summary>$8737-$87AC: volume computation and the control-register write.</summary>
        private void VolumeAndRegisters(int m, byte? fadeVal)
        {
            // $8737-$873E: triangle special case (linear counter as its "volume")
            if ((m & 3) == 1)
            {
                Cost(fadeVal.HasValue ? 25 : 11);
                if (m == 0x29 && fadeVal.HasValue)             // $8740-$8750: music tri + fade
                {
                    _c4 = fadeVal.Value;                       // $8744
                    _c1 = R(m, F_Duration);                    // $8746-$8749
                    Multiply();                                // $874B: duration × fade
                    if (_c1 == 0)                              // $874E-$8750: product hi == 0
                    {
                        Cost(9);
                        WriteReg(m, 0, 0);                     // $87AA (A = 0)
                        return;
                    }
                }
                Cost(R(m, F_EnvTimer) == 0 ? 15 : 19);
                if (R(m, F_EnvTimer) == 0)                     // $8752-$8755
                    WriteReg(m, 0, 0x00);                      // $87AA (A = 0)
                else
                    WriteReg(m, 0, 0xFF);                      // $8757-$8759: linear reload $7F
                return;
            }

            // $875B-$8760: volume base = min(fade, timer) when fading, else timer
            byte a;
            if (fadeVal.HasValue)
            {
                a = fadeVal.Value;
                Cost(a >= R(m, F_EnvTimer) ? 15 : 12);
                if (a >= R(m, F_EnvTimer)) a = R(m, F_EnvTimer);   // $875E: BCC keeps fade
            }
            else
            {
                Cost(13);
                a = R(m, F_EnvTimer);                          // $8760
            }

            byte c3 = (byte)(((a >> 4) & 0x0F) ^ 0x0F);        // $8763-$8769: >>4, EOR #$0F
            _c3 = c3;

            int gate = Instr(6);                               // ($C5),Y = 6
            Cost(24 + InstrumentPageCross(6));
            if (gate >= 0x05)                                  // $876D-$8771
            {
                _c4 = (byte)gate;                              // $8773 (multiply operand)
                int acc = R(m, F_DecayAcc);                    // $8775
                int v = (R(m, F_Flags) & 0x40) != 0 ? (~acc & 0xFF) : acc;  // $8778-$8781: 2×ASL → carry = bit6
                Cost((R(m, F_Flags) & 0x40) != 0 ? 23 : 22);
                if (v != 0)                                    // $8782
                {
                    Cost(9);
                    _c1 = (byte)v;
                    Multiply();                                // $8786: acc × gate
                    int reduction = _c1 >> 2;
                    if (reduction >= 16)
                    {
                        Cost(26);
                        WriteReg(m, 0, (byte)(R(m, F_Control) & 0xF0));
                        return;
                    }
                    Cost(16 + (reduction >= _c3 ? 3 : 1));
                    if (reduction >= _c3)
                        _c3 = (byte)reduction;
                }
                else Cost(1);
            }
            else Cost(1);

            // $8797-$87A8: volume = control - C3. BIT $C4 with C4 = $10 ($8797 reloads it
            // unconditionally, even after the gate path): the control byte's $30 sentinel
            // bit4 detects the borrow; borrowed results clamp to control & $F0 (volume 0).
            byte result = (byte)(R(m, F_Control) - _c3);       // SEC; SBC $C3
            Cost((result & 0x10) != 0 ? 28 : 33);
            if ((result & 0x10) != 0)                          // $87A1-$87A3: BIT $C4; BNE $87AA
                WriteReg(m, 0, result);                        // $87AA
            else
                WriteReg(m, 0, (byte)(R(m, F_Control) & 0xF0));  // $87A5-$87A8: volume 0
        }

        /// <summary>$87AF-$889D: vibrato/portamento period adjustment, the $8849 octave
        /// fold, then the $4002 write and the dedup'd $4003 write.</summary>
        private void WritePeriodRegisters(int m)
        {
            Cost(LastPeriodHi(m) >= 128 ? 14 : 12);
            byte lo, hi;
            if (!VibratoAdjust(m, out lo, out hi))             // $87B8-$8809
            {
                Cost(11);
                lo = R(m, F_PeriodLo);                         // $880C-$880F
                hi = R(m, F_PeriodHi);                         // $8811: LDY $0724,X
            }
            Cost(m >= 0x28 ? 5 : 4);

            // $8814-$8835: SFX pitch slide ($D6 bit7 = armed, $D8 = step)
            if (m < 0x28 && (_d6 & 0x80) != 0 && _d8 != 0)
            {
                Cost(47);
                _c4 = _d8;                                     // $8820
                _c1 = hi;                                      // $8822
                byte loSave = lo;                              // $8824-$8826: PHA
                Multiply();                                    // $8827: hi × $D8
                int sum = loSave + _c2;                        // $882A-$882E: + product lo
                lo = (byte)sum;
                hi = (byte)(((sum >> 8) & 1) + _c1);           // $8830-$8834: 0 + hi + carry
            }
            else if (m < 0x28) Cost((_d6 & 0x80) == 0 ? 6 : 11);

            // $8835-$8847: noise channels — raw period index OR instr[7]
            if ((m & 3) == 0)
            {
                Cost(28 + InstrumentPageCross(7));
                byte p = (byte)((hi & 0x0F) | Instr(7));       // $883A-$883F
                Cost(11);
                WriteReg(m, 2, p);                             // $8884: $400E
                WritePeriodLo(m, 0x00);                        // $888B-$889D with C1 = 0: $400F = 8
                return;
            }
            Cost(7);

            // $8849-$886E: octave fold. Threshold table $8953 ($00..$31) selects an
            // octave band from the stored hi byte; the combined value (Y+hi) splits
            // into shift count ((Y+hi)&$38 → 7-((Y+hi)>>3) right shifts) and the
            // final hi nibble (((Y+hi)&7)+7 shifted along).
            int yOct = 8;                                      // $884A: LDY #$08
            Cost(4);
            do { yOct--; Cost(8); if (hi < _rom3[0x0953 + yOct]) Cost(1); } while (hi < _rom3[0x0953 + yOct]);  // $884C-$8850
            int n = (yOct + hi) & 0xFF;                        // $8854-$8858: TYA; CLC; ADC $C1
            int c1 = ((n & 7) + 7) & 0xFF;                     // $8859-$885E
            int shifts = (((n & 0x38) ^ 0x38) & 0xFF) >> 3;    // $8860-$886E
            Cost(29 + (shifts == 0 ? 1 : shifts * 17 - 1));
            int pair = (c1 << 8) | lo;
            for (int i = 0; i < shifts; i++) pair >>= 1;       // $8867-$8869: LSR $C1; ROR $C2

            // $8870-$8883: vibrato rate adds to the folded 16-bit value (signed)
            int rate = R(m, F_VibRate);                        // $8872
            Cost(8);
            if (rate != 0)
            {
                Cost((rate & 128) != 0 ? 20 : 19);
                int yCarry = (rate & 0x80) != 0 ? -1 : 0;      // $8877-$8879: BPL skip; DEY
                int lo2 = (pair & 0xFF) + rate;                // $887A-$887D: CLC; ADC $C2
                int hi2 = ((pair >> 8) & 0xFF) + yCarry + (lo2 >> 8);  // $887F-$8882
                pair = ((hi2 & 0xFF) << 8) | (lo2 & 0xFF);
            }
            else Cost(1);

            Cost(11);
            WriteReg(m, 2, (byte)(pair & 0xFF));               // $8884-$8888: $4002
            WritePeriodLo(m, (byte)(pair >> 8));               // $888B-$889D: dedup'd $4003
        }

        /// <summary>$888B-$889D: $4003 write with dedup on the stored hi byte
        /// ($077C-$077F). Skipped entirely when unchanged; bit 3 (length reload)
        /// is OR'd in ($8899: ORA #$08).</summary>
        private void WritePeriodLo(int m, byte c1)
        {
            Cost(15);
            if (c1 == LastPeriodHi(m)) { Cost(1); return; }                 // $8891-$8894: BEQ $88A0
            Cost(15);
            SetLastPeriodHi(m, c1);                            // $8896
            WriteReg(m, 3, (byte)(c1 | 0x08));                 // $8899-$889D
        }

        /// <summary>$87B8-$8814: vibrato/portamento adjustment of the period.
        /// Returns true with (lo, hi) = adjusted period; false → caller reloads
        /// the stored $0720/$0724 verbatim. Faithfully models $C2 mutation: the
        /// add path stores its (possibly rejected) result into $C2 before the
        /// hi-byte test, so the hi==0 fall-through subtracts the ADDED lo.</summary>
        private bool VibratoAdjust(int m, out byte outLo, out byte outHi)
        {
            outLo = 0; outHi = 0;
            if (LastPeriodHi(m) >= 128) return false;
            int depth = Instr(5);                              // $87B8-$87BC: instr[5]
            if (depth == 0) { Cost(11 + InstrumentPageCross(5)); return false; }                      // $87BC: BEQ $880C

            int acc = R(m, F_DecayAcc);                        // $87C0
            int v = (R(m, F_Flags) & 0x40) != 0 ? (~acc & 0xFF) : acc;  // $87C3-$87CB: 2×ASL → carry = bit6
            Cost(((R(m, F_Flags) & 0x40) != 0 ? 32 : 31) + InstrumentPageCross(5));
            if (v == 0) { Cost(2); return false; }                          // $87CD-$87CE: BEQ $880C

            _c4 = (byte)depth;                                 // $87BE
            _c1 = (byte)v;                                     // $87CF
            Multiply();                                        // $87D1: v × depth → $C1:$C2
            Cost(47);
            int prod = ((_c1 << 8) | _c2) & 0xFFFF;
            prod >>= 4;                                        // $87D4-$87E1: 4x (LSR A; ROR $C2)
            if (prod == 0) { Cost(2); return false; }                       // $87E2-$87E5: TAY; ORA $C2; BEQ
            Cost(6);
            byte dHi = (byte)(prod >> 8);                      // (Y register holds this)
            byte dLo = (byte)prod;

            if ((R(m, F_Flags) & 0x80) == 0)                   // $87E7-$87E9: bit7 clear → add
            {
                Cost(20);
                int sum = dLo + R(m, F_PeriodLo);              // $87EC-$87F2: CLC; ADC $0720,X → $C2
                _c2 = (byte)sum;
                int hi2 = (dHi + R(m, F_PeriodHi) + (sum >> 8)) & 0xFF;  // $87F4-$87F7: TYA; ADC $0724,X
                if (hi2 != 0)                                  // $87F8: BNE $8809
                {
                    Cost(7);
                    outLo = _c2; outHi = (byte)hi2;
                    return true;                               // $8809-$880B: TAY; BNE $8814
                }
                // hi == 0 → fall into the subtract path with $C2 = the ADDED lo (verbatim)
                int diff = R(m, F_PeriodLo) - _c2;             // $87FA-$8800: SEC; LDA $0720,X; SBC $C2
                int borrow = diff < 0 ? 1 : 0;
                int hi3 = (R(m, F_PeriodHi) - dHi - borrow) & 0xFF;  // $8802-$8807: STY $C1; SBC $C1
                Cost(26 + (hi3 != 0 ? 1 : 0));
                outLo = (byte)diff; outHi = (byte)hi3;
                return hi3 != 0;                               // $8809-$880B: TAY; BNE $8814
            }
            {                                                  // $87FA-$8807: SEC; SBC (direct)
                Cost(1);
                int diff = R(m, F_PeriodLo) - dLo;
                int borrow = diff < 0 ? 1 : 0;
                int hi2 = (R(m, F_PeriodHi) - dHi - borrow) & 0xFF;
                Cost(26 + (hi2 != 0 ? 1 : 0));
                outLo = (byte)diff; outHi = (byte)hi2;
                return hi2 != 0;                               // $8809-$880B
            }
        }

        /// <summary>$88A0-$8914: shared tail. For stolen music channels this runs INSTEAD
        /// of the register writes (music timing continues, output muted); for every
        /// channel it applies the slide-to-target adjustment and the per-duty
        /// volume-decay accumulator.</summary>
        private void StolenTail(int m)
        {
            Cost(8);
            if ((R(m, F_Flags) & 0x20) == 0)                   // $88A0-$88A5: note-on?
            {
                Cost(1);
                DecayAccTail(m);                               // $88FA
                return;
            }
            int amt = R(m, F_SlideAmt);                        // $88A7
            Cost(6);
            if (amt == 0)                                      // $88AA: BEQ $88F2
            {
                Cost(12);
                W(m, F_Flags, (byte)(R(m, F_Flags) & 0xDF));   // $88F2-$88F7: clear note-on
                DecayAccTail(m);
                return;
            }

            bool down = (amt & 0x80) != 0;                     // $88AE: ASL → carry = bit7
            Cost(down ? 17 : 10);
            int delta = (amt << 1) & 0xFF;                     // $88AE: ASL A
            int y = 0;                                         // $88AC: LDY #$00
            if (down)
            {
                delta = ((~delta) + 1) & 0xFF;                 // $88B2-$88B6: EOR #$FF; CLC; ADC #$01
                y = -1;                                        // $88B7: DEY
            }
            int sum = delta + R(m, F_PeriodLo);                // $88B8-$88BC: CLC; ADC $0720,X
            int lo = sum & 0xFF;
            int hi = (R(m, F_PeriodHi) + y + (sum >> 8)) & 0xFF;   // $88BF-$88C3: TYA; ADC $0724,X
            W(m, F_PeriodLo, (byte)lo);
            W(m, F_PeriodHi, (byte)hi);

            // $88C6-$88E1: compare against table[slideTarget] and snap when crossed
            int t2 = (R(m, F_SlideTarget) << 1) & 0xFF;        // $88C6-$88CA: ASL A; TAY
            int tLo = _rom3[0x0959 + t2];
            int tHi = _rom3[0x095A + t2];
            Cost(62 + (0x59 + t2 > 255 ? 1 : 0) + (0x5A + t2 > 255 ? 1 : 0));
            int diffLo = lo - tLo;                             // $88CB-$88CF: SEC; SBC $8959,Y
            int borrow = diffLo < 0 ? 1 : 0;
            int hiCmp = (hi & 0x3F) - tHi - borrow;            // $88D2-$88D7: AND #$3F; SBC $895A,Y
            int carryOut = ((hi & 0x3F) >= tHi + borrow) ? 1 : 0;
            int a = (0xFF + carryOut) & 0xFF;                  // $88DA-$88DC: LDA #$FF; ADC #$00
            a = (a + (down ? 1 : 0)) & 0xFF;                   // $88DE-$88E0: PLP; ADC #$00
            if (a != 0) { Cost(1); DecayAccTail(m); return; }           // $88E1: BNE $88FA (keep sliding)
            Cost(4);
            if (m == 0) { Cost(1); DecayAccTail(m); return; }           // $88E3-$88E4: TXA; BEQ (full X == 0)
            Cost(29);
            W(m, F_PeriodLo, (byte)tLo);                       // $88E6-$88EF: snap to target
            W(m, F_PeriodHi, (byte)tHi);
            W(m, F_Flags, (byte)(R(m, F_Flags) & 0xDF));       // $88F2-$88F7: clear note-on
            DecayAccTail(m);
        }

        /// <summary>$88FA-$8914: per-duty volume-decay accumulator — instr[4]&amp;$7F added
        /// to $0708,X; overflow carries into flags bit6 (the vibrato/gate sign).</summary>
        private void DecayAccTail(int m)
        {
            Cost(11 + InstrumentPageCross(4));
            int rate = Instr(4) & 0x7F;                        // $88FA-$88FE
            if (rate == 0) { Cost(7); return; }                             // $8900: BEQ $8914
            int acc = rate + R(m, F_DecayAcc);                 // $8902-$8906
            W(m, F_DecayAcc, (byte)acc);
            Cost(acc > 255 ? 32 : 20);
            if (acc > 0xFF)                                    // $8909: BCC $8914
                W(m, F_Flags, (byte)(R(m, F_Flags) + 0x40));   // $890B-$8911: CLC; ADC #$40
        }

        // ------------------------------------------------------------------ //
        // $8006-$8022: 8-bit multiply — $C1 × $C4 → $C1:$C2 ($C1 = high byte)
        // ------------------------------------------------------------------ //

        private void Multiply()
        {
            Cost(156);
            int result = 0;                                    // $8006-$8008: $C1:$C2 = $C1:0
            byte mult = _c1, mand = _c4;
            for (int i = 0; i < 8; i++)
            {
                result <<= 1;                                  // ASL $C2; ROL $C1
                if ((mult & 0x80) != 0) { Cost(18); result += mand; }        // $8010-$801D: BCC skip; ADC $C4
                mult = (byte)(mult << 1);
            }
            _c1 = (byte)(result >> 8);
            _c2 = (byte)(result & 0xFF);
        }

        // ------------------------------------------------------------------ //
        // State serialization (schema-tagged, deterministic)
        // ------------------------------------------------------------------ //

        private const ushort StateSchema = 1;
        private const uint StateTag = 0x534E5744;              // 'DWNS'

        /// <summary>Capture the full sequencer state. The Apu state is NOT included —
        /// serialize it separately if a bit-exact audio resume is required.</summary>
        public byte[] SaveState()
        {
            var ms = new System.IO.MemoryStream(0x900);
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(StateTag);
                w.Write(StateSchema);
                w.Write(_c0); w.Write(_c1); w.Write(_c2); w.Write(_c3); w.Write(_c4);
                w.Write(_c5); w.Write(_c6); w.Write(_c7); w.Write(_c8); w.Write(_c9);
                w.Write(_ca); w.Write(_cb); w.Write(_cc); w.Write(_cd); w.Write(_ce);
                w.Write(_cf); w.Write(_d0); w.Write(_d1); w.Write(_d2); w.Write(_d3);
                w.Write(_d4); w.Write(_d5); w.Write(_d6); w.Write(_d7); w.Write(_d8);
                w.Write(_d9);
                w.Write(_queueWrite); w.Write(_queueRead);
                for (int i = 0; i < 8; i++) w.Write(_queue[i]);
                w.Write((ushort)0x0800);
                for (int i = 0; i < 0x800; i++) w.Write(_p7[i]);
                w.Write(_halfAccum);
                w.Write(_resampleAcc);
                w.Write(_frameFlip);
                return ms.ToArray();
            }
        }

        /// <summary>Restore a state produced by <see cref="SaveState"/>. Returns false
        /// (leaving state untouched) on tag/schema/size mismatch.</summary>
        public bool LoadState(byte[] data)
        {
            byte[] savedP7 = new byte[0x800];
            Array.Copy(_p7, savedP7, 0x800);
            try
            {
                using (var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data)))
                {
                    if (r.ReadUInt32() != StateTag) return false;
                    if (r.ReadUInt16() != StateSchema) return false;
                    _c0 = r.ReadByte(); _c1 = r.ReadByte(); _c2 = r.ReadByte(); _c3 = r.ReadByte(); _c4 = r.ReadByte();
                    _c5 = r.ReadByte(); _c6 = r.ReadByte(); _c7 = r.ReadByte(); _c8 = r.ReadByte(); _c9 = r.ReadByte();
                    _ca = r.ReadByte(); _cb = r.ReadByte(); _cc = r.ReadByte(); _cd = r.ReadByte(); _ce = r.ReadByte();
                    _cf = r.ReadByte(); _d0 = r.ReadByte(); _d1 = r.ReadByte(); _d2 = r.ReadByte(); _d3 = r.ReadByte();
                    _d4 = r.ReadByte(); _d5 = r.ReadByte(); _d6 = r.ReadByte(); _d7 = r.ReadByte(); _d8 = r.ReadByte();
                    _d9 = r.ReadByte();
                    _queueWrite = r.ReadByte(); _queueRead = r.ReadByte();
                    for (int i = 0; i < 8; i++) _queue[i] = r.ReadByte();
                    if (r.ReadUInt16() != 0x0800) return false;
                    for (int i = 0; i < 0x800; i++) _p7[i] = r.ReadByte();
                    _halfAccum = r.ReadInt64();
                    _resampleAcc = r.ReadInt64();
                    _frameFlip = r.ReadBoolean();
                    return true;
                }
            }
            catch (Exception)
            {
                Array.Copy(savedP7, _p7, 0x800);
                return false;
            }
        }
    }
}
