namespace Darkwing.Core
{
    /// <summary>
    /// The master-process flow (bank 7 $C595/$C9AC/$D41B) as a phase machine:
    /// title → city-map bubble chooser → briefing/continue prompt → mission →
    /// map return (cleared: $D8D1 tail INC $38 + $A100/$A5AB set $69) or the
    /// ending branch ($D838 with $31 >= 6).
    ///
    /// Progression rules are the ROM's, not invented:
    ///   $A416  gate: `LDA $14 / AND #$C3 / BEQ` — the chooser only reacts to A($80),
    ///          B($40), Left($02), Right($01); Select/Start/Up/Down are ignored.
    ///   $A44C  A alone (`CMP #$80`) is a no-op (it re-enters the queue loop).
    ///   $A452  `LDA $14 / AND #$0F`: $01 → $A46E Right (SFX $22, then `$38 != 6`
    ///          INC $6C, wrap 3 → 0), $02 → $A48B Left (SFX $22, then `$38 != 6`
    ///          DEC $6C, wrap -1 → 2), any other masked value → $A45C confirm, which
    ///          dispatches the bubble handler through the $A4A8/$A4AB pointer tables.
    ///          The ROM re-reads held $14 every frame (level-triggered auto-repeat);
    ///          the port acts on fresh presses so scripted pad scripts stay deterministic.
    ///   $A4B0  bubble 0: $38 &lt; 3 → $31=0 (bridge), $38 &lt; 6 → $31=3 (tower),
    ///          $38 == 6 → $31=6 (FOWL);
    ///   $A503  bubble 1: $38 &lt; 3 → $31=1 (city), else $31=4 (forest);
    ///   $A53A  bubble 2: $38 &lt; 3 → $31=2 (sewers), else $31=5 (warehouse);
    ///   $9947  the briefing/continue prompt (bank 2) accepts A($80) or Start($10) =
    ///          enter the world ($995E flight → $D4E9 loads $31) and B($40) = $998C
    ///          (`LDA #$FF / STA $F8`) → go back to the map (no tier change);
    ///   $A1D0  at map init, $38 != 0 → $A5AB: with $F8 bit7 clear (the world was
    ///          cleared, since only $998C sets bit7) $69 |= 1 &lt;&lt; $31.
    /// $38 is a counter 0..6 (it is compared with 3 and 6), not a tier index.
    /// </summary>
    public sealed partial class GameFlow
    {
        public enum Phase
        {
            Reset,          // $FE00 → $C595: power-on, fade and copyright hold
            Title,          // $C792: logo + PRESS START, waits for Start
            NewGameDialog, // $C7CF: task 4, $9E00, streams $0A/$0D
            Intro,          // $C5F7: night scene + the $AA00 dialog streams $0E and $0F
            MapCursor,      // $C9AC map loop, $A448 bubble chooser
            MapBriefing,    // $D46C tail + $9900 prompt (flight arrived at the node)
            Mission,        // $31 world running on the bank-5 engine
            MissionCleared, // $D838/$D8D1: cutscene, INC $38, map rebuild
            GameOver,       // $D46C card + $9900 prompt: A=$D4E9 continue, B=$D4B3 stage select
            Ending,         // $D838 with $31 >= 6: post-final cutscene
        }

        public const int BubbleCount = 3;
        public const int PromptFrames = 240;      // $C970 fade + $9900 settle before input is accepted

        public readonly WorldState World;
        public Phase Stage;
        public int PhaseFrames;                   // frames spent in the current phase

        private readonly DarkwingGame _game;
        private readonly RoomDecoder _decoder;
        private bool _promptOpen;                 // $9900 has taken over the pad

        public GameFlow(DarkwingGame game, RoomDecoder decoder)
        {
            _game = game;
            _game.Flow = this;
            _decoder = decoder;
            World = new WorldState(game.Bank7);
            Stage = Phase.Reset;
            // Until the flow enters a mission, the bank-7 loop owns the screen: no player
            // dispatcher, no bank-6 task (the world the host preloaded stays frozen).
            _game.WorldRunning = false;
            _game.MissionCleared = false;
            // The $C792 title is bank 7's own seed: bank-2 task 1 streams the silhouette/stripes
            // picture and $9500/$9200 stage the palettes, CHR and the logo/PRESS START sprite slots.
            // LoadTitleScreen does exactly that, so the boot screen is the real title, not a black
            // frame waiting for LoadMapScreen.
            _game.BeginPowerOn();
            BeginPowerOnTiming();
            _game.ResidentProcess = StepReset;
        }

        /// <summary>Which mission the bubble under the cursor starts right now ($38 rules).</summary>
        public int MissionForBubble(int bubble)
        {
            byte count = World.CompletedCount;
            switch (bubble)
            {
                case 0: return count < 3 ? 0 : count < 6 ? 3 : 6;   // $A4B0/$A4DC/$A4EB
                case 1: return count < 3 ? 1 : 4;                   // $A503/$A531
                case 2: return count < 3 ? 2 : 5;                   // $A53A/$A568
                default: return -1;
            }
        }

        /// <summary>A bubble is dead once its mission is in the completed mask ($69).</summary>
        public bool BubbleAvailable(int bubble)
        {
            int m = MissionForBubble(bubble);
            return m >= 0 && (World.CompletedMask & (1 << m)) == 0;
        }

        /// <summary>
        /// One frame of flow logic. Pad bits are the NES strobe byte ($14):
        /// A=$80, B=$40, Select=$20, Start=$10, Up=$08, Down=$04, Left=$02, Right=$01
        /// (NesButtons.ToByte). Returns the phase the flow is in after this frame.
        /// </summary>
        public Phase Step(in NesButtons pad)
        {
            if (_game.ResidentProcess != null) return Stage; // the resident process runs after the NMI
            PhaseFrames++;
            byte held = pad.ToByte();
            byte pressed = (byte)(held & ~_prevHeld);
            _prevHeld = held;

            switch (Stage)
            {
                case Phase.Title:
                    // $C7AD-$C7CC is the title's Start loop. The $51=$F0 timer belongs
                    // to the RESET prologue, before $C5F7, not to this title. Sending an
                    // idle title through LoadWorld(7) after 240 frames corrupted its image.
                    if ((pressed & 0x10) != 0)
                    {
                        _game.TitleScreen = false;
                        _game.PlaySfx(DarkwingGame.SfxPause);
                        World.CompletedCount = 0;
                        SetPhase(Phase.NewGameDialog);
                        _game.ClearObjectRamAndScroll();
                        _game.FillVramPages(0x20, 0x02, 0x00);
                        _game.RunBank2Task(4);
                        _game.EndingScene9E00();
                        _game.Text.Start(0x0A);
                        World.StreamId = 0x0A;
                    }
                    break;

                case Phase.NewGameDialog:
                    if (_game.Text.Running) StepText(_game.Text, held);
                    else if (World.StreamId == 0x0A)
                    {
                        World.StreamId = 0x0D;
                        _game.Text.Start(0x0D);
                    }
                    else EnterMap();
                    break;

                case Phase.Intro:
                    StepIntro(held);
                    break;

                case Phase.MapCursor:
                    if (_game.Text != null && _game.Text.Running) StepText(_game.Text, held);
                    if (_game.Text == null || !_game.Text.Running) StepBubbleChooser(pressed);
                    _game.MapCursorBubble = World.CursorBubble;   // $6C feeds the $A4AE/$51D/$554 seed
                    break;

                case Phase.MapBriefing:
                    if (_game.Text != null && _game.Text.Running)
                        StepText(_game.Text, held);
                    if (_game.Text != null && _game.Text.Running) break;
                    if (!_promptOpen)
                    {
                        World.StreamId = 8;
                        _game.Text.Start(8, map: true); // YES A BUTTON / NO B BUTTON
                        _promptOpen = true;
                    }
                    else if ((pressed & 0x90) != 0)
                    {
                        World.LastOutcome = 0x00;
                        SetPhase(Phase.Mission);
                        _game.LoadWorld(_decoder, World.CurrentWorld);
                    }
                    else if ((pressed & 0x40) != 0)   // $998C: B → $F8=$FF, back to the map
                    {
                        World.LastOutcome = 0xFF;
                        EnterMap();
                    }
                    break;

                case Phase.Mission:
                    // The gameplay frame itself is stepped by the host; the flow only
                    // watches for the end-of-mission events raised by the world engines.
                    if (_game.Player.State == 0x11) BeginResidentClear(); // native $D838 dispatch
                    else if (_game.MissionCleared) SetPhase(Phase.MissionCleared);
                    else if (_game.Player.GameOverRequest)     // $D41D with $64 != 0
                    {
                        _game.Player.GameOverRequest = false;
                        SetPhase(Phase.GameOver);
                        _game.LoadGameOverScreen();            // $D46C-$D4A9
                        _game.PlaySound(0x0C);                   // $D4A7 LDA #$0C / JSR $F84E
                    }
                    break;

                case Phase.GameOver:
                    // $D4AC JSR $9900 then $D4AF LDA $F8 / BPL $D4E9. A/Start leave $F8 clear
                    // and take $D4E9 = CONTINUE (reload world $31, which still holds the
                    // mission id); B reaches $998C with $F8=$FF and falls into $D4B3 = STAGE
                    // SELECT. gold_gameover_continue (A @ f300) ends on the mission,
                    // gold_gameover_select (B @ f300) on the city map — both from the
                    // original ROM, so the pairing is evidence, not a guess.
                    if ((pressed & 0x90) != 0)                 // $D4E9
                    {
                        _game.PlaySound(0xF0);                   // $D4BE LDA #$F0 / JSR $F84E
                        World.LastOutcome = 0x00;
                        _game.Player.CheckpointId = 0;            // $D4EC/$D4F4 STA $6D (and $C8C2 again)
                        SetPhase(Phase.Mission);
                        _game.LoadWorld(_decoder, World.CurrentWorld);   // $D4FD JSR $C8B5
                    }
                    else if ((pressed & 0x40) != 0)            // $D4B3
                    {
                        World.LastOutcome = 0xFF;              // $998C STA $F8
                        World.StreamId = 0x07;                 // $D4CD LDA #$07 / STA $62
                        _game.Player.CheckpointId = 0;         // $D4D8-$D4E4 zero block, before $D95D
                        EnterMap();                            // $D95D → $C8B5 with the map built
                        _game.PlaySound(0x01);                   // $D95F LDA #$01 / JSR $F84E
                        _game.PlaySfx(0x23);                   // $D974 LDA #$23 / JSR $F850
                    }
                    break;

                case Phase.MissionCleared:
                    if (PhaseFrames == 1)
                    {
                        int mission = World.CurrentWorld;
                        // $D838 LDA $31 / CMP #$06 / BCS $D841 — the ending branch is gated on the
                        // world id just cleared, not on the $69 mask. $D95B INC $38 and the $A100
                        // map rebuild live in the $D8D1 tail, so the ending path takes neither.
                        if (mission >= 6) { SetPhase(Phase.Ending); break; }
                        World.CompletedCount++;                 // $D95B INC $38
                        EnterMap();
                        // $A1D0/$A5AB: counted at map init, only when $F8 bit7 is clear
                        if ((World.LastOutcome & 0x80) == 0) World.CompletedMask |= (byte)(1 << mission);
                    }
                    break;

                case Phase.Ending:
                    StepEnding(held);
                    break;
            }
            return Stage;
        }

        private byte _prevHeld;
        private int _resetStage, _resetWait = 4;

        private void StepReset(NesButtons pad)
        {
            StepResetBody(pad);
            CompleteResetBodyTiming();
        }

        private void StepResetBody(NesButtons pad)
        {
            _prevHeld = pad.ToByte();
            switch (_resetStage)
            {
                case 0:
                    if (PowerOn == null)
                    {
                        if (--_resetWait != 0) return;
                        _game.ResetMemoryAndVideo();
                        _resetStage = 1;
                        return;
                    }
                    StepPowerOnTiming();
                    return;
                case 1:
                    _game.Ppu.WriteRegister(0x2000, 0x90);
                    _game.PlaySound(0xF0);
                    _resetStage = 2;
                    return;
                case 2: // $C5A5-$C5AD
                    EnemyBehaviors.ResetRng();
                    _game.Player.StateTimer = 0xF0;
                    _game.BeginResidentFade(false);
                    _resetStage = 3;
                    return;
                case 3:
                    if (_game.TickResidentFade()) _resetStage = 4; // $C5B0 extra yield
                    return;
                case 4: // $C5B3-$C5E5
                    _game.LoadCopyrightScreen();
                    _game.Player.StateTimer--;
                    _resetStage = 5;
                    return;
                case 5: // $C5E8-$C5F4: Start is deliberately not read in this loop
                    if (_game.Player.StateTimer != 0) { _game.Player.StateTimer--; return; }
                    _game.ClearObjectRamAndScroll();              // $C5F7 JSR $C95C, before $DD57
                    World.CurrentWorld = 7;                       // $C5FA LDA #$07 / STA $31
                    _game.BeginResidentFade(false);               // $C5FE JSR $DD57 -> $C248 -> $C276
                    _resetStage = 6;
                    return;
                case 6: // $C2AA fade frames; the exit pass also runs $C24B/$C24E before $C251
                    if (_game.TickResidentFade())
                    {
                        _game.Objects.ClearForSectionLoad();      // $C24B JSR $C235
                        _game.Oam.HideShadowFromCursor();         // $C24E JSR $C21E at the live $97
                        _resetStage = 7;
                    }
                    return;
                case 7:
                    _resetStage = 9; // $DD5A JSR $FF06 is a separate yield, then $DD5D
                    return;
                case 9:
                    _game.BeginSceneLoad(_decoder, World.CurrentWorld);   // $DD5D-$DE24 staging
                    _resetStage = 8;
                    return;
                case 8:
                    if (!_game.TickSceneLoad()) return;
                    SetPhase(Phase.Intro);
                    _introStage = 0; _introDelay = 30;
                    _game.ResidentProcess = StepResidentIntro;
                    return;
            }
        }
        private int _introStage;      // 0 = $C601 hold, 1 = stream $0E, 2 = $C612 hold, 3 = stream $0F
        private int _introDelay;      // a $FEFF count: $C601/$C612 both LDX #$1E / JSR $FEFF

        /// <summary>$C5F7-$C61B: the night-scene intro. The ROM holds 30 frames ($C601), streams
        /// $62=$0E through $AA00, holds another 30 ($C612) and streams $62=$0F ($C617). After that
        /// comes the $C61E flight loop ($F5A4/$F5DA in bank 5, until $0510 reaches $5C), SFX $10,
        /// the $9600 card and $C650's 60-frame $FEFF — and only then the three story panels
        /// ($C66B $8F00, $C684 $9100, $C69D $9000) and $C6B6 $A000. The panels are ported on
        /// DarkwingGame (IntroPanel8F00Seed/Step, …9100…, …9000Seed/Step, IntroEntryA000); the
        /// flight loop is not, so this phase still hands over to the city map at stage 3.</summary>
        private void StepIntro(byte held)
        {
            var text = _game.Text;
            if (text == null)                       // the host did not wire prg_bank2
            {
                EnterMap();
                return;
            }
            switch (_introStage)
            {
                case 0:                                       // $C601 LDX #$1E / JSR $FEFF
                    if (--_introDelay > 0) break;
                    _introStage = 1;
                    text.Start(0x0E);                          // $C608/$C60F: $62=#$0E, JSR $AA00
                    World.StreamId = 0x0E;
                    break;
                case 1:
                    if (!text.Running) { _introStage = 2; _introDelay = 30; }   // $C612
                    else StepText(text, held);
                    break;
                case 2:
                    if (--_introDelay > 0) break;
                    _introStage = 3;
                    text.Start(0x0F);                          // $C617 LDA #$0F / STA $62, $C61B JSR $AA00
                    World.StreamId = 0x0F;
                    break;
                case 3:
                    if (!text.Running)
                        EnterMap();                             // $C61E flight loop: still open
                    else StepText(text, held);
                    break;
            }
        }

        /// <summary>One $AA00 frame: the typewriter plus its $AA77 SFX $3F enqueue through $F850.</summary>
        private void StepText(TextEngine text, byte held)
        {
            text.Tick(_game.ResidentProcess != null ? _game.NmiCounter : PhaseFrames, held);
            if (text.SfxToPlay != 0) _game.PlaySfx(text.SfxToPlay);
        }

        private int _endStage;          // $D841/$D86F/$D8A5 blocks, then the $D8D0 RTS
        private int _endDelay;          // a $FEFF count: $D89D = $1E (30), $9280 = $F0 (240)

        /// <summary>
        /// $D841-$D8D0, the ending reached by $D838 <code>LDA $31 / CMP #$06 / BCS $D841</code>.
        /// Three blocks, each with the same prologue ($C970 fade/clear, $97=$04 + $C21E, the four
        /// scroll mirrors zeroed, a $C1BA nametable fill) and a bank-2 body:
        ///   block 1  $D85B $C1BA($20,$00,$00), $62=$11, $FF55(#$02), $8000 with X=$05
        ///            (records at $89BE), $A900 (palette $A94C/$A95C, slot 1 anim $56 at
        ///            ($7C,$5B), $1B=$9E/$1A=$8C) then its $AA00 dialog;
        ///   block 2  $D883 $C1BA($20,$02,$00), $62=$09, X=$04 (records at $87C8), $9E00
        ///            (palette $9E68/$9E78, slots 1-2 anims $AF/$B0, $1B=$9B/$1A=$84), its dialog,
        ///            then $D89D LDX #$1E / JSR $FEFF (30 frames) and $D8A2 JSR $C95C;
        ///   block 3  $D8B6 $31=$0B, $D8BA JSR $DD57, $D8C2 JSR $9280 — which opens with
        ///            LDX #$F0 / JSR $FEFF (240 frames), then seeds slots 1-5 ($2F/$AE/$C6/$C5),
        ///            the sprite palette $93B8 and $18=$FF. $D8CD LDA #$FF / STA $65 is the
        ///            game-completed flag; its readers are not ported yet (GAPS G69).
        /// Every address above is in disasm/flat_bank7.txt:2998-3059 and flat_bank2.txt, and all
        /// palette/record/CHR bytes are read from prg_bank2.bytes at run time, never hardcoded.
        /// There is no ending capture, so this is verified as instruction-for-instruction parity
        /// plus a rendered screen, not frame-exactly against the original ROM.
        /// </summary>
        private void StepEnding(byte held)
        {
            var text = _game.Text;
            switch (_endStage)
            {
                case 0:                                     // block 1 ($D841-$D86C)
                    _game.EndingFadeOutAndClear();           // $D841 JSR $C970
                    _game.RunBank2Task(5);                   // $D867 LDX #$05 / JSR $8000
                    _game.EndingSceneA900();                 // $D86C JSR $A900 (sans its $AA00)
                    World.StreamId = 0x11;                   // $D860 LDA #$11 / STA $62
                    if (text != null) text.Start(0x11);       // $D86C's tail: JSR $AA00
                    _endStage = 1;
                    break;
                case 1:                                     // resident inside $AA00
                    if (text == null || !text.Running) { _endStage = 2; break; }
                    StepText(text, held);
                    break;
                case 2:                                     // block 2 ($D86F-$D89A)
                    _game.EndingFadeOutAndClear();           // $D86F JSR $C970
                    _game.FillVramPages(0x20, 0x02, 0x00);   // $D883-$D889
                    _game.RunBank2Task(4);                   // $D897 LDX #$04 / JSR $8000
                    _game.EndingScene9E00();                 // $D89A JSR $9E00 (sans its $AA00)
                    World.StreamId = 0x09;                   // $D88E LDA #$09 / STA $62
                    if (text != null) text.Start(0x09);
                    _endStage = 3;
                    break;
                case 3:
                    if (text == null || !text.Running)       // $D89D LDX #$1E / JSR $FEFF
                    {
                        _endStage = 4;
                        _endDelay = 30;
                        _game.EndingYieldOnly = true;
                    }
                    else StepText(text, held);
                    break;
                case 4:
                    if (--_endDelay > 0) break;
                    _game.EndingYieldOnly = false;
                    _game.ClearObjectRamAndScroll();         // $D8A2 JSR $C95C
                    World.CurrentWorld = 0x0B;               // $D8B6 LDA #$0B / STA $31
                    _game.LoadWorld(_decoder, 0x0B);         // $D8BA JSR $DD57
                    _game.WorldRunning = false;              // $9280's own LDX #$F0 hold is
                    _game.EndingScreen = true;               // resident: ProcessYield only
                    _game.EndingYieldOnly = true;
                    _endDelay = 240;                         // $9282/$9280 JSR $FEFF
                    _endStage = 5;
                    break;
                case 5:
                    if (--_endDelay > 0) break;
                    _game.EndingYieldOnly = false;
                    _game.EndingScene9280();                 // $D8C2 JSR $9280's seeds
                    _game.PlaySound(0x00);                   // $D8C9 JSR $F84E with A=$00
                    World.GameCompletedFlag = 0xFF;          // $D8CD LDA #$FF / STA $65
                    _game.WorldRunning = true;               // $D8D0 RTS → the main loop's engine
                    _game.EndingScreen = false;
                    _endStage = 6;
                    break;
                case 6:
                    // Post-RTS: scene $0B runs on the world engine. $65's title-screen readers and
                    // the $F51B flight inside $9280's $04D0&$0F==2 branch are not ported (G69/task 6).
                    break;
            }
        }

        /// <summary>$A416/$A448/$A452: A($80) alone is a no-op, Right($01)/Left($02)
        /// move the bubble cursor (SFX $22, $38==6 freezes the cursor but still beeps),
        /// any other $C3-masked press confirms the bubble under $6C.</summary>
        private void StepBubbleChooser(byte pressed)
        {
            if ((pressed & 0xC3) == 0) return;              // $A416 AND #$C3 / BEQ
            if (pressed == 0x80) return;                    // $A44C CMP #$80 → re-queue, no action
            switch (pressed & 0x0F)                         // $A452 AND #$0F
            {
                case 0x01:                                   // $A46E Right: SFX then INC $6C
                    _game.PlaySfx(0x22);
                    if (World.CompletedCount != 6)
                        World.CursorBubble = (byte)((World.CursorBubble + 1) % BubbleCount);
                    return;
                case 0x02:                                   // $A48B Left: SFX then DEC $6C
                    _game.PlaySfx(0x22);
                    if (World.CompletedCount != 6)
                        World.CursorBubble = (byte)((World.CursorBubble + BubbleCount - 1) % BubbleCount);
                    return;
                default: break;                              // $A45C confirm
            }
            int mission = MissionForBubble(World.CursorBubble);
            if (mission < 0) return;
            World.CurrentWorld = (byte)mission;
            World.StreamId = (byte)mission;         // $62 = the briefing text stream for this node
            World.LastOutcome = 0x00;               // $DE66 clears $F8 on the next world load
            SetPhase(Phase.MapBriefing);
            // $A43F → $A402: the map loop becomes the idle queue, which still parks the caret
            // ($A404) but never re-seeds the bubbles ($A4AE/$A503/$A53A are chooser-only).
            _game.MapChooser = false;
            _game.Text.Start(World.StreamId, map: true);
        }

        /// <summary>Entering the city map: $6C restarts at bubble 0 ($A193/$A1C8 LDA #$00 /
        /// STA $6C) and $38 picks which of the two bubble layouts $A100 seeds, so both are
        /// handed to the screen before it fills its slots.</summary>
        private void EnterMap()
        {
            SetPhase(Phase.MapCursor);
            World.CursorBubble = 0;
            _game.MapTier = World.CompletedCount;
            _game.LoadMapScreen();
            World.StreamId = 7;
            _game.Text?.Start(7, map: true);
        }

        private void SetPhase(Phase next)
        {
            Stage = next;
            PhaseFrames = 0;
            _promptOpen = false;
            if (next == Phase.Ending) { _endStage = 0; _endDelay = 0; }
        }

        /// <summary>Host calls this on the frame the mission's final boss dies ($B39E path).</summary>
        public void OnMissionCleared() => _game.MissionCleared = true;

        /// <summary>Which $D841/$D86F/$D8A5 step the ending body is on (0-6, 6 = after the
        /// $D8D0 RTS) and how many $FEFF frames are left in it — exposed for FlowVerify.</summary>
        public int EndingStage => _endStage;
        public int EndingDelay => _endDelay;
    }
}
