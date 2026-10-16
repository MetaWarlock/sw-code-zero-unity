namespace Darkwing.Core
{
    /// <summary>
    /// Bank 4 <c>$8F00…$9025</c> — the pause / status screen — plus its bank-7 entry
    /// <c>$CA17…$CA3D</c>. It is a partial half of <see cref="DarkwingGame"/> because the
    /// phase needs the $0300 palette mirror, $18 and the frame tail that live there.
    ///
    /// The phase is a blocking <c>JSR $8F00</c> in ROM terms, so in the port's
    /// cooperative model it OWNS the frame: $CAFC (the player state table) and the $CA43
    /// tail — scroll, spawner, zone, palette script, HUD refresh, enemy tasks — do not run
    /// while it is active. Only the bank-5 engine ($F89A → JSR $8000) and the two plate
    /// machines tick. That is the same process-owns-the-frame rule shift 42 proved for the
    /// $9605 intro-card phase.
    ///
    /// Panel = object slots 0…5 re-installed from the bank-4 tables. Their $0400 byte is
    /// zeroed, so bit 4 is clear and the cull takes the $80C7 absolute-copy path: $0450/$0440
    /// become $04F0/$0510 verbatim, which is what makes the panel land in SCREEN space and
    /// why the plates' X/Y high bytes (untouched by the install) do not matter.
    /// Slot 1 carries anim $2E, whose bank-5 frame pointer is the RAM block $05C2 — that is
    /// the panel body itself, and why <see cref="OamDispatcher.MapRamPartBlock"/> exists.
    ///
    /// Deliberately not modelled: the <c>$1A=$9F</c> / <c>$1A=$038E|$80</c> CHR-latch writes.
    /// The NMI's $FF21 only flushes a request while rendering is on, and rendering is off for
    /// the whole phase ($FE AND #$F7), so the round trip applies nothing new — the port's
    /// applied CHR banks are already the level's, which is what the panel draws from.
    /// </summary>
    public sealed partial class DarkwingGame
    {
        private const int BlockLen = 38;                 // $C92A: LDY #$25 → $05C2…$05E7
        private readonly byte[] _pauseBlock = new byte[BlockLen];
        private readonly byte[] _pauseSaved = new byte[36];      // $0360…$0383, six stride-6 arrays
        private readonly byte[] _pauseSavedTail = new byte[10];  // $0384…$038D ← $04D6…$04DF
        private readonly byte[] _pausePalSaved = new byte[16];   // $038F…$039E ← $0310…$031F
        private byte _pauseSavedInvuln;                          // $8F00 PHA / $9022 PLA of $7C
        private bool _pauseBlockInit;
        private bool _pauseActive;
        private bool _pausePlatesArmed;

        /// <summary>Set while the $8F00 loop owns the process.</summary>
        public bool Paused => _pauseActive;

        /// <summary>The live $05C2…$05E7 panel block, exposed for the frame-exact harness.</summary>
        public byte[] PauseBlockForTest => _pauseBlock;

        /// <summary>$CA22…$CA2F: the four states that may not open the status screen
        /// ($07 death, $09 mission seed, $12 section exit, $15). The second $12 compare is dead.</summary>
        private static bool PauseGuardAllows(byte state)
            => state != 0x07 && state != 0x09 && state != 0x12 && state != 0x15;

        /// <summary>$C92A, reached from $C820 (title init) and $D47F (game over). The ROM runs it
        /// long before any mission, so the block is seeded once: $05E6 inside it is the death
        /// countdown, live gameplay state that a re-copy on every pause would wrongly reset.</summary>
        private void EnsurePauseBlock()
        {
            if (_pauseBlockInit || Bank7 == null) return;
            for (int i = 0; i < BlockLen; i++) _pauseBlock[i] = Bank7[0x0936 + i];   // $C936…$C95B (Bank7 = $C000–$FFFF)
            Oam?.MapRamPartBlock(0x05C2, _pauseBlock);
            _pauseBlockInit = true;
        }

        /// <summary>$904E: the weapon name is seven tile immediates inside four branch bodies,
        /// not a data table. $54 = 0 blanks all seven ($9061); anything else RTS-es at $9060 and
        /// leaves the block untouched.</summary>
        private static readonly byte[][] WeaponNames =
        {
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },   // $9061
            new byte[] { 0xCD, 0xA0, 0x7B, 0xCE, 0xC9, 0x00, 0x00 },   // $9079
            new byte[] { 0x7B, 0xC8, 0xC8, 0x70, 0xD1, 0x00, 0x00 },   // $909B
            new byte[] { 0x7C, 0xCD, 0xCF, 0x8C, 0xD0, 0xA0, 0xC8 },   // $90BD
        };

        private void PauseWeaponName(byte weapon)
        {
            if (weapon >= WeaponNames.Length) return;               // $9060 RTS
            byte[] name = WeaponNames[weapon];
            for (int i = 0; i < 7; i++) _pauseBlock[0x10 + i * 2] = name[i];   // $05D2…$05DE
        }

        /// <summary>$CA30…$CA3D then $8F00…$8FB5: SFX $1C, PRG→4, save the six slots and the
        /// state bytes of slots 6…15, install the plates, swap the palette, blank the bg.</summary>
        private void PauseEnter()
        {
            PlaySfx(SfxPause);
            EnsurePauseBlock();
            var b4 = Bank4;
            var slots = Objects.Slots;

            _pauseSavedInvuln = Player.InvulnFrames;     // $8F00/$8F02: LDA $7C / PHA
            _pauseBlock[0x1E] = Player.GasEnergyHi;      // $8F03/$8F05: $60 → $05E0
            _pauseBlock[0x20] = Player.GasEnergyLo;      // $8F08/$8F0A: $61 → $05E2
            PauseWeaponName(Player.WeaponSelected);      // $8F0D JSR $904E

            for (int x = 5; x >= 0; x--)                 // $8F10 LDX #$05 … $8F7F DEX / BPL
            {
                var s = slots[x];
                _pauseSaved[x] = s.StateLow;             // $8F12 → $0360,X
                _pauseSaved[x + 6] = s.Flags;            // $8F18 → $0366,X
                _pauseSaved[x + 12] = s.AnimNumber;      // $8F1E → $036C,X
                _pauseSaved[x + 18] = (byte)s.Y;         // $8F24 → $0372,X ($0510 = Y low)
                _pauseSaved[x + 24] = (byte)s.X;         // $8F2A → $0378,X ($04F0 = X low)
                _pauseSaved[x + 30] = s.AnimFrame;       // $8F30 → $037E,X
                s.StateLow = b4[0x1026 + x];             // $8F36 ← $9026 ($80 × 6)
                s.Active = (s.StateLow & 0x80) != 0;
                s.AnimNumber = b4[0x102C + x];           // $8F3C ← $902C ($36 $2E $31 $00 $3F $3F)
                if (x == 3)                              // $8F42…$8F64: the $54 override
                {
                    switch (Player.WeaponSelected)
                    {
                        case 1: s.AnimNumber = 0x27; break;
                        case 2: s.AnimNumber = 0x26; break;
                        case 3: s.AnimNumber = 0x28; break;
                    }
                }
                s.AnimTimer = 0;                         // $8F67/$8F69
                s.AnimFrame = 0;                         // $8F6C
                s.Flags = 0;                             // $8F6F (bit 4 clear → screen-space copy)
                s.Y = (ushort)((s.Y & 0xFF00) | b4[0x1032 + x]);   // $8F72 ← $9032
                s.X = (ushort)((s.X & 0xFF00) | b4[0x1038 + x]);   // $8F78 ← $9038
            }

            Player.InvulnFrames = 0;                     // $8F81/$8F83: LDX #$00, STX $7C
            for (int k = 0; k < 10; k++)                 // $8F83…$8F93: $04D6…$04DF = slots 6…15
            {
                var s = slots[6 + k];
                _pauseSavedTail[k] = s.StateLow;         // → $0384,X
                s.StateLow = 0;
                s.Active = false;
            }

            for (int i = 0; i < 16; i++)                 // $8F9A…$8FA9
            {
                _pausePalSaved[i] = _palMirror[0x10 + i];
                _palMirror[0x10 + i] = b4[0x103E + i];   // ← $903E
            }
            _palDirty = 0xFF;                            // $8FAB STX $18, X = $FF off the DEX loop
            Ppu.Mask = (byte)(Ppu.Mask & 0xF7);          // $8FB1…$8FB5: background off
            Oam.HudActive = false;                       // slot 1 now composes the panel block
            _pauseActive = true;
            _pausePlatesArmed = false;
        }

        /// <summary>$8FB7…$8FC8: $97=$04 (<see cref="OamDispatcher.RunFrame"/> primes the same
        /// cursor), the two plate machines, JSR $F89A, and the render of the composed frame.
        /// The plate service is armed (skipped) on the entering frame, so gold_pause_m1 f20 holds
        /// the install values $54/$AC and only f21 shows $58/$A8.
        ///
        /// Ordering inside the frame is fixed by the OAM/RAM phase of the capture: the plate parts
        /// compose at offset (−4,−4) from the PREVIOUS frame's positions — gold_pause_m1 f22 has
        /// $04F4=$5C/$04F5=$A4 yet emits D4,CA,03,$54 / 24,CA,03,$A4, i.e. $58−4 and $A8−4. So the
        /// step must land before the walk that the NEXT PublishToOam flushes, while the RAM index
        /// sequence (one step per frame, first at f21) stays as measured.</summary>
        private void PauseFrame()
        {
            StepFrameParity();              // $8000 INC $95, once per frame
            PublishToOamOnce();
            if (_pausePlatesArmed) SlidePlates();   // $8FBE / $8FC1
            else _pausePlatesArmed = true;
            Oam.RunFrame(FrameParity);        // $F89A
            Ppu.Render(Framebuffer);
            Frame++;
        }

        /// <summary>
        /// $90E1 and $9148: five stages each, held in the low nibble of the plate's $04D0 byte
        /// and dispatched through $90F4/$90F9 and $915B/$9160. <c>INC</c> keeps bit 7, so a plate
        /// stays active through the whole cycle and stage 4 parks it back at $80 — the two plates
        /// swap places (slot 5 slides left then down, slot 4 right then up) and stay parked.
        /// </summary>
        private void SlidePlates()
        {
            var p5 = Objects.Slots[5];                     // $90E1, $04F5/$0515
            switch (p5.StateLow & 0x0F)
            {
                case 0: StepLow(p5, true, -4); if ((byte)p5.X < 0x58) p5.StateLow++; break;    // $90FE
                case 1: StepLow(p5, false, +4); if ((byte)p5.Y >= 0xD8) p5.StateLow++; break;  // $910F
                case 2: StepLow(p5, true, +4); if ((byte)p5.X >= 0xAC) p5.StateLow++; break;   // $9120
                case 3: StepLow(p5, false, -4); if ((byte)p5.Y < 0x30) p5.StateLow++; break;   // $9131
                default: p5.StateLow = 0x80; break;                                           // $9142
            }
            var p4 = Objects.Slots[4];                     // $9148, $04F4/$0514
            switch (p4.StateLow & 0x0F)
            {
                case 0: StepLow(p4, true, +4); if ((byte)p4.X >= 0xAC) p4.StateLow++; break;   // $9165
                case 1: StepLow(p4, false, -4); if ((byte)p4.Y < 0x30) p4.StateLow++; break;   // $9176
                case 2: StepLow(p4, true, -4); if ((byte)p4.X < 0x58) p4.StateLow++; break;    // $9187
                case 3: StepLow(p4, false, +4); if ((byte)p4.Y >= 0xD8) p4.StateLow++; break;  // $9198
                default: p4.StateLow = 0x80; break;                                            // $91A9
            }
        }

        /// <summary>Every handler is <c>LDA / SEC/SBC #$04 or CLC/ADC #$04 / STA</c> on a single
        /// coordinate byte, so the step moves the LOW byte of X ($04F0) or Y ($0510) by 4 with an
        /// 8-bit wrap and never carries into the page byte.</summary>
        private static void StepLow(ObjectSystem.Slot s, bool onX, int delta)
        {
            if (onX) s.X = (ushort)((s.X & 0xFF00) | (byte)(s.X + delta));
            else s.Y = (ushort)((s.Y & 0xFF00) | (byte)(s.Y + delta));
        }

        /// <summary>$8FCD…$9025: the six slots come back field by field ($0430 is zeroed rather
        /// than saved), then slots 6…15's state bytes, then the palette, $18 and the bg bit.</summary>
        private void PauseRestore()
        {
            var slots = Objects.Slots;
            for (int x = 5; x >= 0; x--)
            {
                var s = slots[x];
                s.StateLow = _pauseSaved[x];                  // $8FCF ← $0360,X
                s.Active = (s.StateLow & 0x80) != 0;
                s.Flags = _pauseSaved[x + 6];                 // $8FD5 ← $0366,X
                s.AnimNumber = _pauseSaved[x + 12];           // $8FDB ← $036C,X
                s.Y = (ushort)((s.Y & 0xFF00) | _pauseSaved[x + 18]);    // $8FE1 ← $0372,X
                s.X = (ushort)((s.X & 0xFF00) | _pauseSaved[x + 24]);    // $8FE7 ← $0378,X
                s.AnimFrame = _pauseSaved[x + 30];            // $8FED ← $037E,X
                s.AnimTimer = 0;                              // $8FF3
            }
            for (int k = 0; k < 10; k++)                      // $8FFD…$9006
            {
                var s = slots[6 + k];
                s.StateLow = _pauseSavedTail[k];
                s.Active = (s.StateLow & 0x80) != 0;
            }
            for (int i = 0; i < 16; i++) _palMirror[0x10 + i] = _pausePalSaved[i];   // $900A ← $038F
            _palDirty = 0xFF;                                 // $9013 STX $18
            Ppu.Mask = (byte)(Ppu.Mask | 0x08);               // $901C…$9020: background back on
            Player.InvulnFrames = _pauseSavedInvuln;          // $9022/$9023: PLA / STA $7C
            Oam.HudActive = true;
            _pauseActive = false;
        }
    }
}
