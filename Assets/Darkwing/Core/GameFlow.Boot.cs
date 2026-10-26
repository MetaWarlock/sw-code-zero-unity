namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        private byte _attractLow, _attractHigh;

        private void ExitResidentIntro()
        {
            _game.PlaySound(0xF0);
            _game.BeginResidentFade(false);
            _introStage = 13;
        }

        // $C675/$C68E/$C6A7: the panel's RTS falls into JSR $C95C, STA $31 and JSR $DD57 in the
        // same frame, and $DD57 opens with the $C276 fade-out's first ramp.
        private void BeginIntroPanelLoad(byte world)
        {
            _game.ClearObjectRamAndScroll();
            World.CurrentWorld = world;
            _game.BeginResidentFade(false); _introStage = 21;
        }

        private byte ResidentPad(NesButtons pad)
        {
            byte held = pad.ToByte();
            byte pressed = (byte)(held & ~_prevHeld);
            _prevHeld = held;
            return pressed;
        }

        // Bank 7 $C601-$C672. Each case resumes at an actual $FF06 boundary.
        private void StepResidentIntro(NesButtons pad)
        {
            MeasureResetIntroTiming();
            StepResidentIntroBody(pad);
            CompleteResetBodyTiming();
        }

        private void StepResidentIntroBody(NesButtons pad)
        {
            byte pressed = ResidentPad(pad);
            PhaseFrames++;
            var text = _game.Text;
            var s = _game.Objects.Slots[0];
            switch (_introStage)
            {
                case 0:
                    if (--_introDelay > 0) return;
                    World.StreamId = 0x0E; text.Start(0x0E); _introStage = 1;
                    return;
                case 1:
                    StepText(text, pressed); _game.ResidentWalk();
                    if (!text.Running) { _introStage = 2; _introDelay = 31; } // $AB57 must first RTS from its yield
                    return;
                case 2:
                    if (--_introDelay > 0) return;
                    World.StreamId = 0x0F; text.Start(0x0F); _introStage = 3;
                    return;
                case 3:
                    StepText(text, pressed);
                    if (!text.Running) { _game.SeedIntroFlight(); _introStage = 4; }
                    _game.ResidentWalk();
                    return;
                case 4: // $AB57 returns, then $C61E itself yields once more
                    _introStage = 5;
                    return;
                case 5:
                    _game.StepIntroFlight();
                    RecordResetIntroFlightWalk();
                    if ((s.Y & 0xFF) < 0x5C) return;
                    _game.PlaySfx(0x10); _game.Oam.PrimeShadowCursor(4); _game.Oam.HideShadow();
                    EnemyBehaviors.SetAnim(s, 0x2B);
                    _game.Oam.MissionCardHold = true;
                    _game.ResidentWalk(); // $9609 starts its own $F89A before yielding
                    RecordResetIntroCardWalk();
                    _introStage = 6;
                    return;
                case 6: // $960C tests the walk that just returned from its yield
                    if (s.AnimFrame == 0x0F) _game.Oam.BlitMissionCard(_game.Bank2, 7);
                    if (s.AnimFrame == 0x11 || (s.AnimFrame == 0x0F && s.AnimTimer == 4))
                    { _introDelay = 120; _introStage = 7; return; } // $9630 + $C650, 60 each
                    _game.ResidentWalk();
                    RecordResetIntroCardWalk();
                    return;
                case 7:
                    if (--_introDelay > 0) return;
                    _game.Oam.MissionCardHold = false; _game.Oam.PrimeShadowCursor(4); _game.Oam.HideShadow();
                    _game.ClearObjectRamAndScroll(); World.CurrentWorld = 9;
                    _game.BeginResidentFade(false); _introStage = 8;
                    return;
                case 8:
                    if (_game.TickResidentFade()) _introStage = 9;
                    return;
                case 9: // $C251's extra yield
                    _introStage = 10;
                    return;
                case 10:
                    MeasureResetStaging();
                    _game.BeginSceneLoadStreaming(_decoder,World.CurrentWorld);
                    if(BeginResetStreamingTiming())RunLoaderStreamFrame();
                    else _game.FallbackSceneLoad();
                    _introStage = 11;
                    return;
                case 11:
                    if(LoaderStreaming) { RunLoaderStreamFrame(); return; }
                    if (!_game.TickSceneLoad()) return;
                    _game.IntroPanel8F00Seed(); _game.ResidentWalk(); _introStage = 12;
                    return;
                case 12:
                    if (_game.IntroPanel8F00Step(pressed)) return;
                    // A button returns from the first panel directly into $C792.
                    // The timed panel chain falls into the next scene loader in the same frame.
                    if (pressed == 0) { BeginIntroPanelLoad(0x0E); return; }
                    ExitResidentIntro();
                    return;
                case 13:
                    if (_game.TickResidentFade()) _introStage = 15;
                    return;
                case 14:
                    _introStage = 15;
                    return;
                case 15:
                    _game.RenderOff(); _game.FillVramPages(0x20, 0, 0);
                    _game.ClearObjectRamAndScroll(); _game.Oam.HideShadow();
                    _introStage = 16;
                    return;
                case 16:
                    _game.LoadTitleScreen(false); _game.SyncScenePlayer(); _game.ResidentWalk();
                    SetPhase(Phase.Title); _game.ResidentProcess = StepResidentTitle;
                    return;
                case 21:
                    if (_game.TickResidentFade()) _introStage = 22;
                    return;
                case 22:
                    _introStage = 23; return;
                case 23:
                    _game.BeginSceneLoad(_decoder, World.CurrentWorld); _introStage = 24; return;
                case 24:
                    if (!_game.TickSceneLoad()) return;
                    if (World.CurrentWorld == 0x0C)
                    {
                        _game.BeginIntroPanel9000Settle(); _introStage = 26;
                    }
                    else if (World.CurrentWorld == 0x0A)
                    {
                        _game.IntroEntryA000(); _game.SyncScenePlayer();
                        _introStage = 29;
                    }
                    else { _game.IntroPanel9100Seed(); _game.ResidentWalk(); _introStage = 25; } // $9155
                    return;
                case 25:
                    if (_game.IntroPanel9100Step(pressed)) return;
                    if (pressed != 0) { ExitResidentIntro(); return; }
                    BeginIntroPanelLoad(0x0C);
                    return;
                case 26:
                    if (pressed != 0) { _game.IntroPanel9000SettleExit(); ExitResidentIntro(); return; }
                    if (!_game.StepIntroPanel9000Settle()) return;
                    _introDelay = 10; _introStage = 27;
                    return;
                case 27:
                    if (--_introDelay > 0) return;
                    _game.IntroPanel9000Seed(); _game.ResidentWalk(); _introStage = 28; // $9079…$907D
                    return;
                case 28:
                    if (_game.IntroPanel9000Step(pressed)) return;
                    if (pressed != 0) { ExitResidentIntro(); return; }
                    BeginIntroPanelLoad(0x0A);
                    return;
                case 29:
                    if ((pressed & 0x10) != 0) { ExitResidentIntro(); return; }
                    _game.StepIntroEntryFlight();
                    if (_game.Player.ScrollX != 0x100) return;
                    _game.BeginResidentFade(false); _introStage = 30;
                    return;
                case 30:
                    if (_game.TickResidentFade()) _introStage = 31;
                    return;
                case 31:
                    _game.RenderOff(); _game.FillVramPages(0x20, 0, 0);
                    _game.ClearObjectRamAndScroll(); _game.Oam.HideShadow(); _introStage = 32;
                    return;
                case 32:
                    _game.LoadIntroAttractTitle(); _game.ResidentWalk();
                    _attractLow = _attractHigh = 0; _introStage = 33;
                    return;
                case 33: // resumes at $C733 after the $C730 yield; the $C721 walk ends the frame
                    if ((s.StateLow & 15) == 0)
                    {
                        if (_game.Player.StateTimer != 0) _game.Player.StateTimer--;
                        else
                        {
                            EnemyBehaviors.IntegrateXNeg(s);
                            if ((s.X & 0xFF) < 0x28)
                            {
                                _game.RevealIntroAttractBackground(); s.StateLow++;
                            }
                        }
                    }
                    else
                    {
                        s.AnimFrame = s.AnimTimer = 0;
                        _game.Objects.Slots[1].Flags = 0x80;
                        EnemyBehaviors.IntegrateXPos(_game.Objects.Slots[1]);
                        if (_attractHigh == 6 && _attractLow == 0xA0)
                        {
                            _game.BeginResidentFade(false); _introStage = 34; return;
                        }
                        _attractLow++;
                        if (_attractLow == 0xFF) _attractHigh++;
                    }
                    _game.SyncScenePlayer();
                    if ((pressed & 0x10) != 0) { ExitResidentIntro(); return; }   // $C78C…$C790 → $C792
                    _game.ResidentWalk();                                            // $C721…$C72D, $C730 JSR $FF06
                    return;
                case 34:
                    if (!_game.TickResidentFade()) return;
                    _game.BeginPowerOn(); SetPhase(Phase.Reset);
                    PowerOn = null;
                    ResetClock = null;
                    _resetStage = 0; _resetWait = 4;
                    _game.ResidentProcess = StepReset;
                    return;
            }
        }

        private void StepResidentTitle(NesButtons pad)
        {
            byte pressed = ResidentPad(pad);
            PhaseFrames++;
            EnemyBehaviors.IntegrateXPos(_game.Objects.Slots[1]);
            _game.Objects.Slots[1].Flags = 0x80;
            if ((pressed & 0x10) == 0) { _game.ResidentWalk(); return; }
            // The dialog's $C986 fade is resident; no title animation runs during it.
            SetPhase(Phase.NewGameDialog); World.CompletedCount = 0;
            _game.TitleScreen = false;
            _game.BeginResidentFade(false); _dialogStage = 0;
            _game.ResidentProcess = StepResidentDialog;
        }

        private int _dialogStage;
        private void StepResidentDialog(NesButtons pad)
        {
            byte pressed = ResidentPad(pad);
            PhaseFrames++;
            switch (_dialogStage)
            {
                case 0:
                    if (_game.TickResidentFade()) _dialogStage = 2;
                    return;
                case 1:
                    _dialogStage = 2; return;
                case 2:
                    _game.RenderOff(); _game.ClearObjectRamAndScroll(); _game.Oam.HideShadow();
                    _game.FillVramPages(0x20, 2, 0); _dialogStage = 3; return;
                case 3:
                    _game.RunBank2Task(4); _game.EndingScene9E00();
                    _game.SyncScenePlayer();
                    World.StreamId = 0x0A;
                    _dialogStage = 30; return;
                case 30: // $9E61's engine/yield precedes $9E64's $AA00 clear/yield
                    _game.ResidentWalk(); _dialogStage = 31; return;
                case 31:
                    _game.Text.Start(0x0A); _dialogStage = 4; return;
                case 4:
                    StepText(_game.Text, pressed);
                    if (_game.Text.WalkRequired) _game.ResidentWalk();
                    if (_game.Text.Running) return;
                    World.StreamId = 0x0D; _game.Text.Start(0x0D); _dialogStage = 5;
                    return;
                case 5:
                    StepText(_game.Text, pressed);
                    if (_game.Text.WalkRequired) _game.ResidentWalk();
                    if (_game.Text.Running) return;
                    _game.PlaySound(1); _game.BeginResidentFade(false); _dialogStage = 6;
                    return;
                case 6:
                    if (_game.TickResidentFade()) _dialogStage = 8; return;
                case 7:
                    _dialogStage = 8; return;
                case 8:
                    _game.RenderOff(); _game.FillVramPages(0x20, 0, 0);
                    _game.ClearObjectRamAndScroll(); _game.Oam.HideShadow();
                    _dialogStage = 9; return;
                case 9:
                    BeginResidentMap();
                    return;
            }
        }
    }
}
