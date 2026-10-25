namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        public byte NmiCounter;
        public void RestoreResidentWorld(byte id)
        { _worldId = id; Player.WorldId = Player.AreaId = id; }
        public void LoadClearFlightPalette()
        {
            for (int i=0;i<16;i++) _paletteSrc[16+i]=_palMirror[16+i]=Bank2[0x1A9C+i];
            _palDirty=0xFF;
        }
        public byte ClearScoreDigit(int index)
        { EnsurePauseBlock(); return index<5 ? Player.ScoreDigits[index] : _pauseBlock[2+index*2]; }
        private int _sectionFadeStage;
        private byte _sectionRequested;

        public void InitializeNewGamePanel()
        {
            // $C820/$C92A copy the status template after the first world loader returns.
            EnsurePauseBlock();
            for(int i=0;i<_pauseBlock.Length;i++) _pauseBlock[i]=Bank7[0x936+i];
            Player.ScoreUnits=0; Player.ScoreLifeState=0;
            Player.EnergyColLeft=Bank7[0x958]; Player.EnergyColRight=Bank7[0x95A];
            Player.ContinueCount=Player.WeaponActive=Player.WeaponSelected=Player.PendingMissionFlag=0;
        }

        private void ToggleSpecialWeapon()
        {
            var p = Player;
            PlaySfx(0x1B);
            p.GasToggle ^= 1;
            if (p.GasToggle != 0)
            {
                p.WeaponActive = p.WeaponSelected; p.GasActive++;
                p.CommitSavedGasMeter();
            }
            else if (p.GasActive == 1)
            {
                p.GasEnergyLo = p.ActiveGasEnergyLo; p.GasEnergyHi = p.ActiveGasEnergyHi;
                if (p.WeaponSelected == 2)
                    // $CAE7-$CAFB releases active Thunder bolts into their fall.
                    // It writes only slots 2/3's state; slot 4 and inactive slots stay intact.
                    for (int i=2;i<=3;i++)
                        if (Objects.Slots[i].Active) Objects.Slots[i].StateLow=0x83;
                p.WeaponActive = p.GasActive = p.ActiveGasEnergyHi = p.ActiveGasEnergyLo = 0;
            }
        }

        private void BeginSectionTransition(byte sectionId)
        {
            // $E388 saves X across the resident $C248 fade before writing any descriptor.
            _sectionRequested = sectionId;
            _sectionFadeStage = 1;
            PublishToOamOnce();
            BeginResidentFade(false);
        }
        private void SeedHudSlot()
        {
            Hud.Reset();
            RestoreHudSlot();
        }
        private void RestoreHudSlot()
        {
            var s = Objects.Slots[1];
            s.Active = true; s.StateLow = s.Flags = 0x80;
            EnemyBehaviors.SetAnim(s, 0x1B);
            s.Behavior = s.BehaviorParam = 0;
            s.X = (ushort)((Player.X & 0xFF00) | 0x18);
            s.Y = (ushort)((Player.Y & 0xFF00) | 0x1C);
        }
        public void ClearObjectPages()
        {
            // $C838 clears the complete $0400-$05FF pages, including inactive-slot leftovers.
            for (int i = 0; i < Objects.Slots.Length; i++) Objects.Slots[i] = new ObjectSystem.Slot();
            Player.X = Player.Y = 0; Player.XFrac = Player.YFrac = 0;
            Player.XVelInt = Player.XVelFrac = Player.YVelFrac = Player.Flags = 0;
            Player.YVelInt = 0;
        }
        public void BeginPowerOn()
        {
            WorldRunning = TitleScreen = MapScreen = EndingScreen = false;
            Player.X = Player.Y = 0;
            Player.XVelInt = Player.XVelFrac = Player.Flags = Player.FacingRight = 0;
            Player.ScrollYRow = Player.RowStride = 0;
            Player.GravityFrac = 0;
            Player.LevelActive = 0;
            Ppu.WriteRegister(0x2000, 0);
            Ppu.WriteRegister(0x2001, 0);
            Ppu.Chr0Bank = 0; Ppu.Chr1Bank = 0x10;
            Oam.HudActive = false;
            Text.DeferWrites = true;
        }

        public void ResetMemoryAndVideo()
        {
            ResetPowerOnMemory();
            ResetPowerOnVideo();
        }

        internal void ResetPowerOnMemory()
        {
            TileMap = null;
            // $FE26-$FE3F clear RAM; $FE69/$FE72/$FE7B hide OAM and fill both NTs.
            for (int i = 0; i < Objects.Slots.Length; i++)
            {
                var s = Objects.Slots[i];
                s.Active = false; s.StateLow = s.Flags = s.AnimNumber = s.Behavior = 0;
                s.X = s.Y = 0;
            }
            _nmiMaskRequest = 0x1E;
        }

        internal void ResetPowerOnVideo()
        {
            for (int i = 0; i < 256; i++) Ppu.Oam[i] = (byte)(i % 4 == 0 ? 0xF8 : 0);
            Oam.ResetShadow();
            FillVramPages(0x20, 1, 0); FillVramPages(0x24, 1, 0);
        }

        public void BeginResidentFade(bool fadeIn)
        {
            _fadeLevel = fadeIn ? (byte)0x40 : (byte)0;
            _fadeDelta = fadeIn ? (byte)0xF0 : (byte)0x10;
            _fadeExitAt50 = !fadeIn; _fadeFrame = 0;
            StepFade();
        }
        public bool TickResidentFade() => StepFade();

        public void LoadCopyrightScreen()
        {
            RenderOff();
            ClearObjectRamAndScroll();
            FrameParity = 0;
            FillVramPages(0x20, 0, 0);
            RunBank2Task(0);
            for (int i = 0; i < 16; i++)
                _paletteSrc[i] = _palMirror[i] = Bank2[0xD15 + i];
            Player.ChrLatch1 = 0x99;
            SetPaletteUploadFlag(0x99);
            RenderOn();
        }

        private int _sceneLoadStage;
        private bool _sceneLoadStreaming, _loaderExited;
        private int _streamPassRemaining, _streamPassCharged, _streamPassIncrementAt;
        private bool _streamPassLast, _streamPassIncrementPending;

        /// <summary>True once the $DE86 loop reached $0F and the $DDCB yield is due.</summary>
        public bool LoaderExited => _loaderExited;

        /// <summary>$DD5D-$DE86 with the staging loop left to the caller. The reset clock
        /// streams the loop pass by pass, so the prelude must not paint the first bands.</summary>
        public void BeginSceneLoadStreaming(RoomDecoder decoder, byte worldId)
        {
            _worldDecoder = decoder; _worldId = worldId;
            WorldPrelude(false);
            _sceneLoadStage = 0;
            _sceneLoadStreaming = true;
            _streamPassRemaining = _streamPassCharged = 0;
            _streamPassLast = _streamPassIncrementPending = false;
            _loaderExited = false;
        }

        /// <summary>Leaves the $DE86 loop to the caller's $DDCB yield when the clock could
        /// not stream it and the old per-frame approximation finishes the staging.</summary>
        public void FallbackSceneLoad()
        {
            _sceneLoadStreaming = false;
            _sceneLoadStage = 0;
        }

        /// <summary>Charges the $DE86/$E698 passes one frame's remaining CPU time can hold and
        /// returns the cycles consumed. The pass paints its band and stores $25 near the end of
        /// $E698 ($E790), so a pass that does not fit leaves the old cursor visible to the NMI;
        /// the store is applied when the charged cycles cross that point. The uncharged
        /// remainder of the same pass is carried into the next frame, where the RTI resumes it.
        /// The pass that reaches $0F is six cycles cheaper: $DE8F does not branch back to a
        /// fresh $DE86 head. The final pass starts like every other one: the NMI may interrupt
        /// it too, and only the $DE93 tail must not cross the edge.</summary>
        public int StreamSceneLoad(int budgetCycles)
        {
            if (!_sceneLoadStreaming) throw new System.InvalidOperationException("Reset loader is not streaming");
            if (budgetCycles < 0) throw new System.ArgumentOutOfRangeException(nameof(budgetCycles));
            int used = 0;
            while (true)
            {
                if (_streamPassRemaining == 0)
                {
                    if (_loaderExited || budgetCycles - used <= 0) break;
                    int cost = TileMap.MeasureStagingPassCycles(Player.StageCursor, Player.CellIndexNext,
                        Player.StageStep, Player.StageBoundary, out int incrementAt);
                    bool last = (byte)(Player.StageCursor + Player.StageStep) == Player.StageTarget;
                    if (last) cost -= 6;
                    _streamPassLast = last;
                    _streamPassRemaining = cost;
                    _streamPassCharged = 0;
                    _streamPassIncrementAt = incrementAt;
                    _streamPassIncrementPending = true;
                    Player.StageBandStep();
                }
                int room = budgetCycles - used;
                int charge = _streamPassRemaining <= room ? _streamPassRemaining : room;
                _streamPassCharged += charge;
                if (_streamPassIncrementPending && _streamPassCharged >= _streamPassIncrementAt)
                {
                    _streamPassIncrementPending = false;
                    Player.StageCursorStep();
                }
                used += charge;
                if (charge == _streamPassRemaining)
                {
                    _streamPassRemaining = 0;
                    if (_streamPassLast) { _loaderExited = true; _streamPassLast = false; }
                    continue;
                }
                _streamPassRemaining -= charge;
                break;
            }
            return used;
        }

        /// <summary>After $DDCB: the next frame's spawn runs through the existing
        /// $DDCE/$DE00/$DE03 path.</summary>
        public void FinishSceneLoadStream()
        {
            _sceneLoadStreaming = false;
            _sceneLoadStage = 3;
        }

        /// <summary>The bank-5 $8000 walk called by the resident $F89A service.</summary>
        public void ResidentWalk()
        {
            FrameParity++;
            Oam.RunFrame(FrameParity);
        }

        public void SyncScenePlayer()
        {
            var s = Objects.Slots[0];
            Player.X = s.X; Player.Y = s.Y; Player.Flags = s.Flags;
        }

        public void SeedIntroFlight()
        {
            var s = Objects.Slots[0];
            EnemyBehaviors.SetAnim(s, 0x2C);
            s.Active = true; s.StateLow = 0x80; s.Flags = 0xD0;
            s.X = 0x80; s.Y = 0; s.YVelFrac = 0xAB; s.YVelInt = -1;
            Player.GravityFrac = 0x40;
            SyncScenePlayer();
        }

        public void StepIntroFlight()
        {
            var s = Objects.Slots[0];
            EnemyBehaviors.IntegrateYSub(s);
            EnemyBehaviors.ApplyGravity(s, Player.GravityFrac);
            SyncScenePlayer();
            ResidentWalk();
        }

        public void BeginIntroPanel9000Settle()
        {
            Objects.Slots[0].YVelInt = 2;
            Objects.Slots[0].YVelFrac = 0;
        }

        public bool StepIntroPanel9000Settle()
        {
            var reference = Objects.Slots[0];
            EnemyBehaviors.IntegrateYRideSlot(reference);
            int distance = (reference.Y & 0xFF) - Player.ScrollY;
            if (distance < 0) distance -= 0x10;
            Player.ScrollSceneDown((byte)distance);
            return Player.ScrollYRow == 1 && Player.ScrollY == 0;
        }

        public void StepIntroEntryFlight()
        {
            EnemyBehaviors.IntegrateXNeg(Objects.Slots[0]);
            SyncScenePlayer();
            Player.HorizontalScroll(TileMap.GridByte, TileMap.FlagByte);
            if ((Player.ScrollX >> 8) == 1 && (Player.ScrollX & 0xFF) < 0x91)
                EnemyBehaviors.IntegrateXPos(Objects.Slots[1]);
            ResidentWalk();
            TileMap.CameraY = Player.ScrollY;
            TileMap.SetScroll(Player.ScrollX);
        }

        public void LoadIntroAttractTitle()
        {
            RunBank2Task(1);
            for (int index = 0; index < 16; index++)
            {
                _paletteSrc[index] = _palMirror[index] = 0x0F;
                _paletteSrc[16 + index] = _palMirror[16 + index] = Bank2[0xD77 + index];
            }
            SetChrAndUpload(0x9F, 0x99, 0x99);
            var logo = Objects.Slots[0];
            EnemyBehaviors.SetAnim(logo, 0xAC);
            logo.Active = true; logo.StateLow = 0x80;
            logo.X = 0xC8; logo.Y = 0x9B;
            logo.XVelInt = 1; logo.XVelFrac = 0xB3;
            TitleSlot(1, 0xBC, 0, 0xDC);
            Objects.Slots[1].Flags = 0xA4;
            Objects.Slots[1].XVelInt = 1; Objects.Slots[1].XVelFrac = 0;
            Player.StateTimer = 0xB4;
            RenderOn();
            SyncScenePlayer();
        }

        public void RevealIntroAttractBackground()
        {
            var palette = new byte[16];
            Sections.ReadBgPaletteByIndex(0x0D, palette);
            SetPaletteSource(palette, 0);
            SetPaletteUploadFlag(0xFF);
        }
        public void BeginSceneLoad(RoomDecoder decoder, byte worldId)
        {
            _worldDecoder = decoder; _worldId = worldId;
            WorldPrelude();
            _sceneLoadStage = 0;
        }
        public bool TickSceneLoad()
        {
            if (_sceneLoadStage == 0)
            {
                if (!Player.StageSectionPass()) return false;
                _sceneLoadStage = 3;
                return false; // $DDCB JSR $FF06, before $DDCE reseeds slot 0
            }
            if (_sceneLoadStage == 3)
            {
                TileMap.CameraY = Player.ScrollY;
                TileMap.SetScroll(Player.ScrollX);
                WorldSpawn();
                Player.EntryFreezeFrames = 0;
                _sceneLoadStage = 1;
                return false;
            }
            if (_sceneLoadStage == 1) // $DE03 JSR $FF06, then $C254 walk and $C270
            {
                FrameParity++;
                Oam.PublishToOam(); Oam.RunFrame(FrameParity);
                BeginResidentFade(true);
                _sceneLoadStage = 2;
                return false;
            }
            return TickResidentFade();
        }
    }
}
