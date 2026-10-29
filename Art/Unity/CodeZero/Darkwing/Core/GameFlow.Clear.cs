namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        private int _clearStage, _clearWait;
        private byte _clearedMission;

        private void BeginResidentClear()
        {
            if (World.CurrentWorld >= 6) { SetPhase(Phase.Ending); return; }
            _clearedMission = World.CurrentWorld;
            SetPhase(Phase.MissionCleared);
            _clearStage = 0;
            _game.ResidentProcess = StepResidentClear;
        }

        // Bank 7 $D838/$D8D1 and bank 2 $9A00/$9B00. Each return is a native
        // $FF06 boundary; no input is used while the clear process owns the stack.
        private void StepResidentClear(NesButtons pad)
        {
            ResidentPad(pad); PhaseFrames++;
            var p = _game.Player; var o = _game.Objects; var s0 = o.Slots[0];
            switch (_clearStage)
            {
                case 0:
                    World.CurrentWorld = 8;
                    _game.BeginResidentFade(false); _clearStage = 1; return;
                case 1:
                    if (_game.TickResidentFade())
                    { o.ClearForSectionLoad(); _game.Oam.HideShadow(); _clearStage = 2; }
                    return;
                case 2: // $C251 yield returns into the extra $DD5A yield
                    _clearStage = 3; return;
                case 3:
                    _game.BeginSceneLoad(_decoder, 8); _game.Oam.HudActive = false;
                    _clearStage = 4; return;
                case 4:
                    if (!_game.TickSceneLoad()) return;
                    _game.Oam.HideShadow(); World.CurrentWorld = _clearedMission;
                    _game.RestoreResidentWorld(_clearedMission);
                    _clearWait = 120; _clearStage = 5; return; // $D8E5 FEFF
                case 5:
                    if (--_clearWait != 0) return;
                    SeedClearFlight(); _game.ResidentWalk(); _clearStage = 6; return;
                case 6: // $9A80 after the preceding $F89A yield
                    EnemyBehaviors.IntegrateXNeg(o.Slots[1]);
                    EnemyBehaviors.IntegrateXPos(o.Slots[2]);
                    if ((byte)o.Slots[1].X >= 0x88) { _game.ResidentWalk(); return; }
                    o.Slots[1].XVelFrac = 0x80; o.Slots[1].XVelInt = 2;
                    _clearStage = 7; return; // $D8F2 yields before the first player move
                case 7:
                    EnemyBehaviors.IntegrateXNeg(s0);
                    EnemyBehaviors.IntegrateXNeg(o.Slots[1]);
                    EnemyBehaviors.IntegrateXPos(o.Slots[2]);
                    _game.SyncScenePlayer(); p.XFrac = s0.XFrac;
                    p.HorizontalScroll(_game.TileMap.GridByte, _game.TileMap.FlagByte);
                    p.PrevPlayerX = p.X;
                    _game.TileMap.SetScroll(p.ScrollX);
                    _game.ResidentWalk();
                    if (p.ScrollX != 0x100) return;
                    SeedClearScore(); _game.Oam.ReservedCursor = 0x98;
                    _game.ResidentWalk(); // $9B75 in the same call, after $D91B's walk
                    _clearStage = 14; return; // $9B75's F89A yield has not returned yet
                case 14:
                    _clearWait = 60; _clearStage = 8; return; // resume $9B78, first FEFF yield
                case 8:
                    if (--_clearWait != 0) return;
                    CountClearPoint(); return;
                case 9:
                    if (--_clearWait != 0) return;
                    if (_game.Oam.ReadShadowByte(0x6D) != 0x70)
                        _game.Oam.WriteShadowByte(0x6D, (byte)(_game.Oam.ReadShadowByte(0x6D) - 1));
                    else if ((s0.StateLow & 15) == 0)
                    {
                        s0.StateLow++;
                        _game.Oam.WriteShadowByte(0x6D, 0x79); _game.Oam.WriteShadowByte(0x69, 0x74);
                    }
                    else if (_game.Oam.ReadShadowByte(0x69) != 0x70)
                    {
                        _game.Oam.WriteShadowByte(0x6D, 0x79);
                        _game.Oam.WriteShadowByte(0x69, (byte)(_game.Oam.ReadShadowByte(0x69) - 1));
                    }
                    if (_game.Oam.ReadShadowByte(0x69) != 0x70 || _game.Oam.ReadShadowByte(0x6D) != 0x70)
                    { CountClearPoint(); return; }
                    _clearWait = 120; _clearStage = 10; return;
                case 10:
                    if (--_clearWait != 0) return;
                    _game.BeginResidentFade(false); _clearStage = 11; return;
                case 11:
                    if (_game.TickResidentFade()) _clearStage = 12;
                    return;
                case 12: // $D939 return, then $D93C-$D969
                    _game.RenderOff(); _game.PlaySound(0xF0);
                    _game.FillVramPages(0x20, 0, 0); _game.ClearObjectRamAndScroll();
                    _game.Oam.ReservedCursor = 0; _game.Oam.HideShadow();
                    World.StreamId = 7; World.CompletedCount++; _game.PlaySound(1);
                    _clearStage = 13; return;
                case 13:
                    BeginResidentMap(false);
                    return;
            }
        }

        private void SeedClearFlight()
        {
            _game.PlaySfx(0x20);
            var p = _game.Player; var o = _game.Objects;
            var a = o.Slots[1]; var b = o.Slots[2]; var s = o.Slots[0];
            EnemyBehaviors.SetAnim(a, 0x2F); EnemyBehaviors.SetAnim(b, 0xAD);
            a.Active = b.Active = true; a.StateLow = b.StateLow = 0x80;
            a.Flags = 0x90; b.Flags = 0x80;
            a.X = 0x2FF; a.Y = 0xA4; b.X = 0x200; b.Y = 0xB7;
            p.ScrollX = 0x200; s.X = (ushort)(0x200 | (byte)s.X);
            s.XFrac = p.XFrac; // $0470 is shared RAM, retained from the mission
            p.PrevPlayerX = (ushort)(0x200 | (byte)p.PrevPlayerX);
            a.XVelFrac = s.XVelFrac = 0x80; a.XVelInt = s.XVelInt = 1;
            b.XVelFrac = 0; b.XVelInt = 5;
            _game.SyncScenePlayer(); _game.LoadClearFlightPalette();
        }

        private void SeedClearScore()
        {
            var oam = _game.Oam;
            for (int i = 0; i < 4; i++)
            {
                int at = 0x68 + i * 4;
                oam.WriteShadowByte(at, 0x27); oam.WriteShadowByte(at + 1, i == 0 ? (byte)0x75 : (byte)0x70);
                oam.WriteShadowByte(at + 2, 0); oam.WriteShadowByte(at + 3, (byte)(0xA8 + i * 8));
            }
            DrawClearTotal();
        }

        private void DrawClearTotal()
        {
            bool significant = false;
            for (int i = 0; i < 7; i++)
            {
                byte d = _game.ClearScoreDigit(i);
                // $9D53 deliberately tests $05CE again before copying $05D0.
                byte tested = i == 6 ? _game.ClearScoreDigit(5) : d;
                significant |= tested != 0x70;
                int at = 0x78 + i * 4;
                _game.Oam.WriteShadowByte(at, 0x47);
                _game.Oam.WriteShadowByte(at + 1, significant ? d : (byte)0);
                _game.Oam.WriteShadowByte(at + 2, 0);
                _game.Oam.WriteShadowByte(at + 3, (byte)(0x90 + i * 8));
            }
        }

        private void CountClearPoint()
        {
            // $9BDC's original carry chain skips $05C6 on overflow from $05C8.
            var digits = _game.Player.ScoreDigits; bool overflow = true;
            foreach (int i in new[] { 4, 3, 2, 0 })
            {
                if (digits[i] < 0x79) { digits[i]++; overflow = false; break; }
                digits[i] = 0x70;
            }
            if (overflow) for (int i = 0; i < 5; i++) digits[i] = 0x79;
            _game.PlaySfx(0x22); DrawClearTotal(); AwardClearLife();
            _clearWait = 3; _clearStage = 9;
        }

        private void AwardClearLife()
        {
            var p = _game.Player; int mode = p.ScoreLifeState & 0xF0;
            if (mode == 0x10)
            {
                if ((p.ScoreDigits[2] & 15) < 1) return;
                p.ScoreLifeState += 0x13;
            }
            else if (mode == 0x20)
            {
                if ((p.ScoreDigits[2] & 15) != (p.ScoreLifeState & 15)) return;
                p.ScoreLifeState = (byte)(0x20 + ((p.ScoreLifeState & 15) + 3) % 10);
            }
            else
            {
                if ((p.ScoreDigits[3] & 15) < 5) return;
                p.ScoreLifeState += 0x10;
            }
            _game.PlaySfx(0x19);
            int right = (p.EnergyColRight & 15) + 1;
            if (right < 10) p.EnergyColRight = (byte)(0x70 + right);
            else if (p.EnergyColLeft != 0x79)
            {
                p.EnergyColRight = 0x70;
                int left = (p.EnergyColLeft & 15) + 1;
                if (left < 9) p.EnergyColLeft = (byte)(0x70 + left);
                else p.EnergyColRight = 0x79; // $9CB7-$9CC4, including its original cap
            }
        }
    }
}
