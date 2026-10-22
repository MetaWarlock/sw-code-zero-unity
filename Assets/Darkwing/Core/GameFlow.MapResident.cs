namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        private int _mapStage, _mapFlightCount;
        private bool _mapNewGame;
        private bool _mapChoiceWalkReturned;

        private void BeginResidentMap(bool newGame = true)
        {
            _mapNewGame = newGame;
            if (newGame) World.CompletedMask = 0;
            else if ((World.LastOutcome & 0x80) == 0) World.CompletedMask |= (byte)(1 << _clearedMission);
            SetPhase(Phase.MapCursor); World.CursorBubble = 0;
            _game.MapTier = World.CompletedCount;
            _game.LoadMapScreen(false); _game.SyncScenePlayer();
            if (newGame) World.CurrentWorld = 0;
            World.StreamId = 7;
            ApplyCompletedBubbles();
            _mapStage = 0; _game.ResidentProcess = StepResidentMap;
        }

        private void ApplyCompletedBubbles()
        {
            for (int i=0;i<3;i++)
                if (!BubbleAvailable(i)) EnemyBehaviors.SetAnim(_game.Objects.Slots[2+i], 0xBD); // $A6DB etc.
        }

        private void ParkCaret()
        {
            var caret = _game.Objects.Slots[1];
            caret.AnimTimer = caret.AnimFrame = 0;
        }

        private void MapTextTick(byte pressed)
        {
            bool waiting = _game.Text.WaitingForInput;
            StepText(_game.Text, pressed);
            if (_game.Text.WalkRequired)
            {
                if (waiting || _game.Text.WaitingForInput) ParkCaret();
                _game.ResidentWalk();
            }
        }

        private void SelectMapBubble()
        {
            int cursor = 2 + World.CursorBubble;
            for (int i = 2; i <= 4; i++)
                if (i != cursor) EnemyBehaviors.SetAnim(_game.Objects.Slots[i], 0x73);
            var s = _game.Objects.Slots[cursor];
            if (s.AnimNumber != 0xBB) EnemyBehaviors.SetAnim(s, 0xBB);
            World.CurrentWorld = World.StreamId = (byte)MissionForBubble(World.CursorBubble);
            ApplyCompletedBubbles();
            if (!BubbleAvailable(World.CursorBubble))
            { World.StreamId = 0x10; EnemyBehaviors.SetAnim(s, 0xC4); } // $A6E6/$A76E selected cleared bubble
            _game.MapCursorBubble = World.CursorBubble;
            _game.ResidentWalk();
            _mapChoiceWalkReturned = true; // resume at $A4F8 after this $F89A yield
        }

        private void StepResidentMap(NesButtons pad)
        {
            byte pressed = ResidentPad(pad);
            PhaseFrames++;
            var text = _game.Text;
            var flight = _game.Objects.Slots[5];
            switch (_mapStage)
            {
                case 0: // $A1DA
                    _game.ResidentWalk(); _mapStage = 1; return;
                case 1:
                    text.Start(7, true); _mapStage = 2; return;
                case 2: // $A1DD/$A300; its acknowledgement precedes the chooser's own yield
                    MapTextTick(pressed);
                    if (!text.Running) _mapStage = 3;
                    return;
                case 3: // resume $A448 after the $A1E4 yield
                    _mapChoiceWalkReturned = false; // entering $A448 directly, not through $A4F8
                    _mapStage = 4;
                    goto case 4;
                case 4:
                    if (_mapChoiceWalkReturned) ParkCaret(); // $A4F8 after the previous walk's yield
                    _mapChoiceWalkReturned = false;
                    if (pressed == 0x80 && World.StreamId != 7)
                    {
                        text.Start(World.StreamId, true); _mapStage = 5; return;
                    }
                    if ((pressed & 0x0F) == 1 || (pressed & 0x0F) == 2)
                    {
                        _game.PlaySfx(0x22);
                        if (World.CompletedCount != 6)
                            World.CursorBubble = (byte)((World.CursorBubble + ((pressed & 1) != 0 ? 1 : 2)) % 3);
                        return; // $A485/$A4A2 yield; the next iteration seeds the new bubble
                    }
                    if (pressed == 0x80) return;
                    SelectMapBubble();
                    return;
                case 5:
                    MapTextTick(pressed);
                    if (text.Running) return;
                    if (World.StreamId == 0x10)
                    { World.StreamId = 7; text.Start(7, true); _mapStage = 2; return; }
                    World.StreamId = 8; text.Start(8, true); _mapStage = 6;
                    return;
                case 6:
                    MapTextTick(pressed);
                    if (text.Running) return;
                    if (pressed != 0x80)
                    { World.StreamId = 7; text.Start(7, true); _mapStage = 2; return; }
                    _game.PlaySfx(0x3F);
                    for (int i = 2; i <= 4; i++)
                        if (i != 2 + World.CursorBubble) _game.Objects.Slots[i].Active = false;
                    EnemyBehaviors.SetAnim(flight, 0x3B);
                    flight.Active = true; flight.StateLow = 0x80; flight.Flags = 0x90;
                    flight.X = 0xFF; flight.Y = 0x70; flight.XVelFrac = 0; flight.XVelInt = 4;
                    _mapFlightCount = 0; _game.PlaySfx(0x21);
                    _game.ResidentWalk(); _mapStage = 7;
                    return;
                case 7: // $A24D-$A260
                    ParkCaret(); EnemyBehaviors.IntegrateXNeg(flight);
                    if (++_mapFlightCount < 64) { _game.ResidentWalk(); return; }
                    int w = World.CurrentWorld;
                    EnemyBehaviors.SetAnim(flight, _game.Bank2[0x22EB + w]);
                    flight.Active = true; flight.Flags = 0x90; flight.X = flight.Y = 0;
                    flight.YVelFrac = _game.Bank2[0x22DD + w];
                    flight.XVelFrac = _game.Bank2[0x22E4 + w];
                    flight.YVelInt = 0; flight.XVelInt = 0;
                    _game.Objects.Slots[6].T4B0 = _game.Bank2[0x22F2 + w];
                    _game.Objects.Slots[7].T4B0 = _game.Bank2[0x22F9 + w];
                    _game.ResidentWalk(); _mapStage = 8;
                    return;
                case 8: // $A2AD-$A2D7
                    ParkCaret();
                    if (pressed == 0)
                    {
                        EnemyBehaviors.IntegrateXPos(flight); EnemyBehaviors.IntegrateYRideSlot(flight);
                        if (_game.Objects.Slots[6].T4B0 != 0) _game.Objects.Slots[6].T4B0--;
                        else if (_game.Objects.Slots[7].T4B0 != 0) _game.Objects.Slots[7].T4B0--;
                        else { BeginResidentMission(); return; }
                        _game.ResidentWalk(); return;
                    }
                    BeginResidentMission(); return;
            }
        }

        private void BeginResidentMission()
        {
            if (!_mapNewGame)
            {
                _game.PlaySfx(0x23); _game.Player.WeaponActive = 0; _game.Objects.ClearSpawnMask();
                _missionLoadStage = -1; _game.ResidentProcess = StepResidentMissionLoad;
                return; // $D983 yield
            }
            _game.PlaySound(0xF1);
            _game.ClearObjectRamAndScroll(); _game.Oam.HideShadow();
            _game.ClearObjectPages();
            _game.FrameParity = 0; World.CompletedCount = 0;
            _game.Player.StateTimer = 0x3C;
            _game.BeginResidentFade(false); _missionLoadStage = 0;
            _game.ResidentProcess = StepResidentMissionLoad;
        }

        private int _missionLoadStage;
        private void StepResidentMissionLoad(NesButtons pad)
        {
            ResidentPad(pad);
            switch (_missionLoadStage)
            {
                case -1:
                    _game.Objects.ClearForSectionLoad(); _game.Oam.HideShadow();
                    _game.BeginResidentFade(false); _missionLoadStage = 0; return;
                case 0:
                    if (_game.TickResidentFade()) _missionLoadStage = 1; return;
                case 1:
                    _missionLoadStage = 2; return;
                case 2:
                    _game.BeginSceneLoad(_decoder, World.CurrentWorld); _missionLoadStage = 3; return;
                case 3:
                    if (!_game.TickSceneLoad()) return;
                    if (_mapNewGame) _game.InitializeNewGamePanel();
                    _game.WorldRunning = true; _game.MapScreen = _game.MapChooser = false;
                    _game.Oam.HudActive = true;
                    SetPhase(Phase.Mission); _game.ResidentProcess = null;
                    return;
            }
        }
    }
}
