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
    public sealed class GameFlow
    {
        public enum Phase
        {
            Title,          // $C792: logo + PRESS START, waits for Start
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
            _decoder = decoder;
            World = new WorldState(game.Bank7);
            Stage = Phase.Title;
            // Until the flow enters a mission, the bank-7 loop owns the screen: no player
            // dispatcher, no bank-6 task (the world the host preloaded stays frozen).
            _game.WorldRunning = false;
            _game.MissionCleared = false;
            // The $C792 title's own nametable is not exported yet (GAPS), so the screen
            // stays blank — rendering disabled, exactly the $C158 black state — until
            // LoadMapScreen re-enables it with the real map picture.
            _game.Ppu.WriteRegister(0x2001, 0x00);
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

        /// <summary>All seven missions done → the ending path ($D838 with $31 >= 6).</summary>
        public bool AllMissionsComplete => World.CompletedMask == 0x7F;

        /// <summary>
        /// One frame of flow logic. Pad bits are the NES strobe byte ($14):
        /// A=$80, B=$40, Select=$20, Start=$10, Up=$08, Down=$04, Left=$02, Right=$01
        /// (NesButtons.ToByte). Returns the phase the flow is in after this frame.
        /// </summary>
        public Phase Step(in NesButtons pad)
        {
            PhaseFrames++;
            byte held = pad.ToByte();
            byte pressed = (byte)(held & ~_prevHeld);
            _prevHeld = held;

            switch (Stage)
            {
                case Phase.Title:
                    if ((pressed & 0x10) != 0)      // $C7AD waits for Start → $C7CF (game_flow.md §"TITLE")
                    {
                        _game.PlaySfx(DarkwingGame.SfxPause);
                        SetPhase(Phase.Intro);                    // $C5F7: $31=$07 then the dialog
                        World.CurrentWorld = 0x07;                // $C5FC LDA #$07 / STA $31
                        _introStage = 0;
                        _game.Text?.Start(0x0E);                  // $C608/$C60F: $62=$0E, JSR $AA00
                    }
                    break;

                case Phase.Intro:
                    StepIntro(held);
                    break;

                case Phase.MapCursor:
                    StepBubbleChooser(pressed);
                    break;

                case Phase.MapBriefing:
                    if (!_promptOpen)
                    {
                        if (PhaseFrames >= PromptFrames) _promptOpen = true;
                    }
                    else if ((pressed & 0x90) != 0)   // $9947: A ($80) or Start ($10) → enter the world
                    {
                        World.LastOutcome = 0x00;     // $F8 still cleared by $DD57
                        SetPhase(Phase.Mission);
                        _game.LoadWorld(_decoder, World.CurrentWorld);
                        _game.PlayMusic(World.MusicId(World.CurrentWorld));
                    }
                    else if ((pressed & 0x40) != 0)   // $998C: B → $F8=$FF, back to the map
                    {
                        World.LastOutcome = 0xFF;
                        SetPhase(Phase.MapCursor);
                        _game.LoadMapScreen();
                    }
                    break;

                case Phase.Mission:
                    // The gameplay frame itself is stepped by the host; the flow only
                    // watches for the end-of-mission events raised by the world engines.
                    if (_game.MissionCleared) SetPhase(Phase.MissionCleared);
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
                        SetPhase(Phase.Mission);
                        _game.LoadWorld(_decoder, World.CurrentWorld);   // $D4FD JSR $C8B5
                        _game.PlayMusic(World.MusicId(World.CurrentWorld));
                    }
                    else if ((pressed & 0x40) != 0)            // $D4B3
                    {
                        World.LastOutcome = 0xFF;              // $998C STA $F8
                        World.StreamId = 0x07;                 // $D4CD LDA #$07 / STA $62
                        SetPhase(Phase.MapCursor);
                        _game.LoadMapScreen();                 // $D95D → $C8B5 with the map built
                        _game.PlaySound(0x01);                   // $D95F LDA #$01 / JSR $F84E
                        _game.PlaySfx(0x23);                   // $D974 LDA #$23 / JSR $F850
                    }
                    break;

                case Phase.MissionCleared:
                    if (PhaseFrames == 1)
                    {
                        int mission = World.CurrentWorld;
                        World.CompletedCount++;                 // $D95B INC $38
                        SetPhase(AllMissionsComplete ? Phase.Ending : Phase.MapCursor);
                        if (Stage == Phase.MapCursor)
                        {
                            _game.LoadMapScreen();
                            // $A1D0/$A5AB: counted at map init, only when $F8 bit7 is clear
                            if ((World.LastOutcome & 0x80) == 0) World.CompletedMask |= (byte)(1 << mission);
                        }
                    }
                    break;

                case Phase.Ending:
                    break;
            }
            return Stage;
        }

        private byte _prevHeld;
        private int _introStage;      // 0 = stream $0E, 1 = the $C612 delay, 2 = stream $0F
        private int _introDelay;      // $C612 LDX #$1E / JSR $FEFF

        /// <summary>$C5F7: the night-scene intro. The ROM drives the $AA00 coroutine twice —
        /// $62=$0E at $C608, a 30-frame $FEFF delay at $C612, then $62=$0F at $C617 — and only
        /// after that enters the $C61E flight loop ($F5A4/$F5DA in bank 5, which runs until the
        /// sprite counter $0510 reaches $5C). That flight animation is not ported yet, so the
        /// dialog hands straight over to the city map; the text itself is frame-exact (TextVerify).</summary>
        private void StepIntro(byte held)
        {
            var text = _game.Text;
            if (text == null)                       // the host did not wire prg_bank2
            {
                SetPhase(Phase.MapCursor);
                _game.LoadMapScreen();
                return;
            }
            switch (_introStage)
            {
                case 0:
                    if (!text.Running) { _introStage = 1; _introDelay = 30; }
                    else StepText(text, held);
                    break;
                case 1:
                    if (--_introDelay > 0) break;
                    _introStage = 2;
                    text.Start(0x0F);               // $C617 LDA #$0F / STA $62, $C61B JSR $AA00
                    break;
                case 2:
                    if (!text.Running)
                    {
                        SetPhase(Phase.MapCursor);  // $C61E flight loop: still open
                        _game.LoadMapScreen();
                    }
                    else StepText(text, held);
                    break;
            }
        }

        /// <summary>One $AA00 frame: the typewriter plus its $AA77 SFX $3F enqueue through $F850.</summary>
        private void StepText(TextEngine text, byte held)
        {
            text.Tick(PhaseFrames, held);
            if (text.SfxToPlay != 0) _game.PlaySfx(text.SfxToPlay);
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
        }

        private void SetPhase(Phase next)
        {
            Stage = next;
            PhaseFrames = 0;
            _promptOpen = false;
        }

        /// <summary>Host calls this on the frame the mission's final boss dies ($B39E path).</summary>
        public void OnMissionCleared() => _game.MissionCleared = true;
    }
}
