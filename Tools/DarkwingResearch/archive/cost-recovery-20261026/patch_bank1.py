p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\ActorTailTiming.cs'
s = open(p, encoding='utf-8').read()
old = '''        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported)
        {
            unsupported = null;'''
new = '''        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported)
            => MeasureBank1Script(objects, out unsupported, null, null, null, 0);

        /// <summary>The bank-1 $8800 pass. The fire spitter ($7F) and its shot ($7E) need the player,
        /// the probe backend, the sound ring and $95 (the engine has not ticked it yet this frame).</summary>
        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported, PlayerCore player, ITileMap tiles,
            ActorEventTiming events, byte parity)
        {
            unsupported = null;'''
assert old in s
s = s.replace(old, new)
old = '''                    if (slot.Behavior < 0x47) cycles += 3;
                    else
                    {
                        if (slot.Behavior < 0x70 || slot.Behavior > 0x7D || (slot.StateLow & 15) != 0)'''
new = '''                    if (slot.Behavior < 0x47) cycles += 3;
                    else if (slot.Behavior == 0x7E || slot.Behavior == 0x7F)
                    {
                        cycles += 2 + 4 + PageCross(0x8836, slot.Behavior) + 3 + 4 + PageCross(0x88B6, slot.Behavior) + 3 + 2 + 3 + 2 + 3 + 5;
                        if (player == null || tiles == null || events == null) { unsupported = "bank1 7E/7F owners absent"; return -1; }
                        try
                        {
                            cycles += slot.Behavior == 0x7E ? FireShot(slot, player, tiles)
                                : FireSpitter(objects, slot, player, tiles, events, parity);
                        }
                        catch (NotSupportedException exception) { unsupported = exception.Message; return -1; }
                    }
                    else
                    {
                        if (slot.Behavior < 0x70 || slot.Behavior > 0x7D || (slot.StateLow & 15) != 0)'''
assert old in s
s = s.replace(old, new)
old = '''        public static int MeasureBank1AndTail('''
new = '''        private const int AnimSet = 6 + 5 + 2 + 5 + 5 + 6;           // JSR $FA6C
        private const int ClearSlot = 2 + 5 + 5 + 5 + 5 + 2 + 5 + 6;  // $FA9A

        /// <summary>$9083 shot: state 0 arms 1.00 X / 1.6E Y, then X moves by $0580 bit 0 and $F2D7
        /// (points $28/$29) rises or falls; any class bursts it into anim $14 and clears the behaviour.</summary>
        private static int FireShot(ObjectSystem.Slot slot, PlayerCore player, ITileMap tiles)
        {
            var motion = ActorMotion.From(slot);
            byte xVelInt = slot.XVelInt, xVelFrac = slot.XVelFrac;
            int cycles = 4 + 2;
            if ((slot.StateLow & 15) == 0)
            {
                cycles += 2 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 7;
                xVelFrac = 0; xVelInt = 1; motion.YVelFrac = 0x6E; motion.YVelInt = 1;
            }
            else cycles += 3;
            bool right = (slot.Direction & 1) != 0;
            cycles += 4 + 2 + (right ? 2 : 3) + 6 + ActorMotionTiming.IntegrateX(ref motion, xVelInt, xVelFrac, !right) + (right ? 3 : 0);
            cycles += 2 + 2 + 6 + ActorMotionTiming.MoveY(ref motion, player.GravityFrac, 0x28, 1, player, tiles, out _) + 3;
            if (motion.ProbeClass == 0) return cycles + 3 + 6;
            return cycles + 2 + 2 + AnimSet + 2 + 5 + 6;
        }

        /// <summary>$8F71 fire spitter: state 0 arms the drift and dies on cell $27 in scroll mode 2;
        /// SFX $2A when $95&amp;3=0; anim $A5 fires a $7E shot on frame $10 timer 2 and turns to $A4 on
        /// $15; anim $A4 drifts diagonally and flips its vertical sense on a $3D timer.</summary>
        private static int FireSpitter(ObjectSystem objects, ObjectSystem.Slot slot, PlayerCore player, ITileMap tiles,
            ActorEventTiming events, byte parity)
        {
            int cycles = 4 + 2;
            byte t490 = slot.T490, t480 = slot.T480;
            var motion = ActorMotion.From(slot);
            byte xVelInt = slot.XVelInt, xVelFrac = slot.XVelFrac;
            if ((slot.StateLow & 15) == 0)
            {
                cycles += 2 + 5 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 7 + 2 + 5;
                t490 = 0; xVelInt = 0; xVelFrac = 0x92; motion.YVelFrac = 0x62; motion.YVelInt = 1; t480 = 0x3C;
                var map = tiles as LevelTileMap;
                if (map == null) throw new NotSupportedException("8F97 cell owner absent");
                cycles += 3 + 5 + PageCross(map.World.GridAddress, player.CellIndex) + 2;
                if (player.CellGridByte == 0x27)
                {
                    cycles += 2 + 3 + 2;
                    if (player.ScrollMode == 2) return cycles + 2 + 3 + ClearSlot;
                }
                cycles += 3;
            }
            else cycles += 3;
            cycles += 3 + 2;
            if ((parity & 3) != 0) cycles += 3;
            else cycles += 2 + 2 + 6 + events.Enqueue(0x2A);
            byte anim = slot.AnimNumber;
            cycles += 4 + 2;
            if (anim != 0xA5)
            {
                cycles += 2 + 2;
                if (anim == 0xA4)
                {
                    cycles += 3 + 4;
                    if (t480 != 0) cycles += 3;
                    else { cycles += 2 + 2 + 5 + 4 + 2 + 5 + 2 + AnimSet; t480 = 0x3D; t490 ^= 1; }
                    cycles += 7 + 4 + 2;
                    bool right = (slot.Direction & 1) != 0;
                    bool up = (t490 & 1) != 0;
                    cycles += (right ? 2 : 3) + 6 + ActorMotionTiming.IntegrateX(ref motion, xVelInt, xVelFrac, !right) + 6 + 4 + 2 + 5 + 6
                        + 4 + 2;
                    if (right) cycles += up ? 3 : 2;
                    else cycles += up ? 3 : 2;
                    cycles += 3 + (up ? YSubtract(motion) : YAdd(motion));
                    return cycles;
                }
                cycles += 2 + 2 + AnimSet;                       // BEQ not taken, LDA #$A5, JSR $FA6C → $8FC1
                anim = 0xA5;
            }
            else cycles += 3;
            // $8FC1 with the $A5 pose: the anim frame/timer belong to the slot (a fresh $FA6C zeroes them).
            byte frame = slot.AnimNumber == 0xA5 ? slot.AnimFrame : (byte)0;
            byte timer = slot.AnimNumber == 0xA5 ? slot.AnimTimer : (byte)0;
            cycles += 4 + 2;
            if (slot.Flags == 0x50) return cycles + 3 + 6;
            cycles += 2 + 2;
            if (slot.Flags == 0x10) return cycles + 3 + 6;
            cycles += 2 + 4 + 2;
            byte compared;
            if (frame != 0x10) { cycles += 3; compared = frame; }
            else
            {
                cycles += 2 + 4 + 2;
                if (timer == 2)
                {
                    cycles += 2 + 4 + 3 + 6 + FaceTowardPlayer(slot, player, out byte facing) + 6
                        + SpawnShot(objects, slot, facing) + 3 + 5 + 6;
                    return cycles;
                }
                cycles += 3; compared = timer;
            }
            cycles += 2;
            if (compared != 0x15) return cycles + 3 + 6;
            return cycles + 2 + 2 + AnimSet + 6;
        }

        /// <summary>JSR $F81D body: $F7EE faces the player, $F808 copies the facing into $0400.</summary>
        private static int FaceTowardPlayer(ObjectSystem.Slot slot, PlayerCore player, out byte facing)
        {
            bool playerRight = slot.X < player.X;
            facing = playerRight ? (byte)1 : (byte)2;
            int direction = 2 + 5 + 4 + 2 + 4 + 4 + 4 + (playerRight ? 3 : 2 + 2 + 5) + 6;
            int flags = 4 + 2 + 2 + 2 + 2 + 3 + 4 + 2 + 3 + 5 + 6;
            return 6 + direction + 6 + flags + 6;
        }

        /// <summary>$9039: a $7E shot 12 px ahead and 4 px lower, in the first free slot 15..5.</summary>
        private static int SpawnShot(ObjectSystem objects, ObjectSystem.Slot slot, byte facing)
        {
            int cycles = 6 + 2;
            int free = -1;
            for (int index = 15; index >= 5; index--)
            {
                cycles += 4;
                if (!objects.Slots[index].Active) { cycles += 3 + 2 + 6; free = index; break; }
                cycles += 2 + 2 + 2 + (index > 5 ? 3 : 2);
            }
            if (free < 0) return cycles + 2 + 6 + 3 + 6;
            return cycles + 2 + 3 + 4 + 5 + 2 + 2 + 4 + 2 + 4 + 3 + 4 + 4 + 3 + 5 + 4 + 5
                + 4 + 2 + 2 + 5 + 4 + 5 + 2 + 6 + (5 + 2 + 5 + 5 + 2 + 5 + 2 + 5 + 6) + 2 + 5 + 2 + 5 + 6;
        }

        private static int YAdd(ActorMotion motion)
        {
            int carry = (motion.YFrac + motion.YVelFrac) >> 8;
            int low = ((motion.Y & 0xFF) + (byte)motion.YVelInt + carry) & 0xFF;
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + (low < 0xF0 ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
        }

        private static int YSubtract(ActorMotion motion)
        {
            int fraction = motion.YFrac - motion.YVelFrac;
            int low = (motion.Y & 0xFF) - (byte)motion.YVelInt - (fraction < 0 ? 1 : 0);
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + (low < 0 ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        public static int MeasureBank1AndTail('''
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\EnemyDispatcher.Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''            LastBank1ScriptCycles = ActorTailTiming.MeasureBank1Script(Objects, out string reason);'''
new = '''            LastBank1ScriptCycles = ActorTailTiming.MeasureBank1Script(Objects, out string reason, Player, Tiles,
                _nativeActorEvents, FrameParityForTiming?.Invoke() ?? 0);'''
assert old in s
s = s.replace(old, new)
s = s.replace('''        public ActorEventTiming ActorTimingEvents;''', '''        public ActorEventTiming ActorTimingEvents;
        /// <summary>$95 as bank 1 reads it (the bank-5 engine ticks it later in the pass).</summary>
        public Func<byte> FrameParityForTiming;''')
open(p, 'w', encoding='utf-8').write(s)
p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\DarkwingGame.cs'
s = open(p, encoding='utf-8').read()
old = '''                        ReadHookLatch = () => Player.HookLatchForTiming
                    };'''
new = '''                        ReadHookLatch = () => Player.HookLatchForTiming
                    };
                    Enemies.FrameParityForTiming = () => FrameParity;'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
