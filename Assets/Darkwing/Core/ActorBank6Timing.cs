using System;

namespace Darkwing.Core
{
    public static class ActorBank6Timing
    {
        private static readonly byte[] CorpseSwapAnims = { 0xA0, 0x7E, 0x7F, 0x69, 0x6B, 0x6A, 0x72 };
        private static readonly byte[] BayonetPhases = { 9,10,11,12,13,14,15,1,2,3,4,5,6,7 };
        private static readonly byte[] BayonetX = { 0,0,0x61,0,0xB5,0,0xEC,0,0,1,0xEC,0,0xB5,0,0x61,0,0,0,0x9F,0xFF,0x4B,0xFF,0x14,0xFF,0,0xFF,0x14,0xFF,0x4B,0xFF,0x9F,0xFF };
        public static int DispatchEnvelope(ObjectSystem.Slot slot)
        {
            if (!slot.Active) return 4 + 3;
            if (slot.Behavior >= 0x47) return 4 + 2 + 4 + 2 + 3;
            return 4 + 2 + 4 + 2 + 2 + 4 + PageCross(0x825F, slot.Behavior) + 3
                + 4 + PageCross(0x82A7, slot.Behavior) + 3 + 2 + 3 + 2 + 3 + 5;
        }

        public static int PostProcessNoHit(ObjectSystem objects, PlayerCore player, ObjectSystem.Slot target, ActorEventTiming events = null)
        {
            int cycles = 4 + 2;
            if ((target.BehaviorParam & 0x40) == 0) cycles += 2 + 3;
            else
            {
                cycles += 3 + 4;
                if (target.HitFlash != 0)
                {
                    cycles += 2 + 2 + 2 + 2;
                    cycles += (target.HitFlash & 4) != 0 ? 3 + 4 + 2 + 5 : 2 + 4 + 2 + 5 + 3;
                    cycles += 7 + 3 + 3;
                }
                else
                {
                    cycles += 3 + 4 + 2 + 5 + 6;
                    bool projectileHit = false;
                    int projectileIndex = -1;
                    if (!target.Active || target.Behavior <= 4)
                        cycles += ProjectileTargetEarlyNoHit(objects, target);
                    else
                        cycles += ActorTailTiming.ProjectileNoHit(objects, target, out projectileHit, out projectileIndex);
                    if (projectileHit)
                    {
                        if (events == null) throw new NotSupportedException("8069 projectile hit context");
                        return cycles + events.ProjectileDamageTail(objects, player, target, projectileIndex);
                    }
                    cycles += 3 + 3;
                }
            }
            return cycles + ActorEventTiming.ContactTail(objects, player, target);
        }

        public static int ProjectileTargetEarlyNoHit(ObjectSystem objects, ObjectSystem.Slot target)
        {
            if (target.Active || (target.StateLow & 127) != 0)
                throw new NotSupportedException("FAAE nonzero-target early gate");
            int cycles = 2 + 3;
            for (int slotIndex = 4; slotIndex >= 2; slotIndex--)
            {
                var projectile = objects.Slots[slotIndex];
                cycles += 3 + 4;
                if (!projectile.Active) cycles += 3;
                else
                {
                    cycles += 2 + 4;
                    if ((projectile.Flags & 0x80) == 0) cycles += 3;
                    else
                    {
                        cycles += 2 + 4 + 2;
                        cycles += projectile.Behavior == 0x1F ? 3 : 2 + 4 + 3;
                    }
                }
                cycles += 5 + 3 + 2 + (slotIndex == 2 ? 2 : 4);
            }
            return cycles + 2 + 6;
        }

        public static int Behavior(ObjectSystem.Slot target, int slotIndex, PlayerCore player, ITileMap tiles, ActorEventTiming events = null)
        {
            if (target.Behavior == 0 || target.Behavior == 0x44 || target.Behavior == 0x45) return 6;
            if (target.Behavior == 0x0F && (target.StateLow & 2) == 0)
            {
                int cycles = 4 + 2;
                bool initialize = (target.StateLow & 15) == 0;
                cycles += initialize ? 2 + 5 + 2 + 5 + 2 + 5 + 7 : 3;
                cycles += 4 + 2 + 2 + 4;
                int timer = initialize ? 0x3C : target.T480;
                if (timer != 0)
                {
                    cycles += 3 + 7 + 6 + DistanceX(target, player) + 2;
                    int delta = player.X - target.X;
                    int distance = delta >= 0 ? (byte)delta : (byte)-delta;
                    if (distance >= 0x48) return cycles + 2 + 6;
                    return cycles + 3 + 2 + 6 + 23 + 2 + 5 + 6;
                }
                cycles += 2 + 4 + 2;
                if (target.AnimNumber == 0x5C)
                {
                    cycles += 3 + 4 + 2;
                    if (target.AnimFrame != 1) return cycles + 3 + 6;
                    cycles += 2 + 4 + 2;
                    return cycles + (target.AnimTimer != 0x10 ? 3 : 2 + 2 + 6 + 23) + 6;
                }
                cycles += 2 + 2;
                if (target.AnimNumber != 0x5D) return cycles + 2 + 2 + 6 + 23 + 2 + 5 + 6;
                cycles += 3 + 4;
                int low = (byte)target.X;
                if (target.T490 == 0)
                {
                    cycles += 2 + 4 + 2 + 2 + 5;
                    low = (byte)(low - 2);
                    cycles += low != 0 ? 3 : 2 + 4 + 2 + 3;
                    cycles += 7;
                }
                else
                {
                    cycles += 3 + 4 + 2;
                    if (target.T490 != 4) cycles += 3 + 7;
                    else
                    {
                        cycles += 2 + 2 + 5 + 4 + 2 + 2 + 5;
                        low = (byte)(low + 2);
                        cycles += low != 0 ? 3 : 2 + 7;
                    }
                }
                cycles += 4 + 2;
                if (target.AnimFrame != 3) return cycles + 3 + 6;
                cycles += 2 + 4 + 2;
                if (target.AnimTimer != 0x10) return cycles + 3 + 6;
                throw new NotSupportedException("8F33 heavy charge spawn");
            }
            if (target.Behavior == 0x01)
            {
                if (slotIndex < 2 || player.State == 0x0C) throw new NotSupportedException("gas player slot or state0C");
                int cycles = 4 + 2;
                bool initialize = (target.StateLow & 15) == 0;
                byte velocity = initialize ? (byte)(target.AnimNumber == 0x18 ? 6 : 4) : target.XVelInt;
                byte fraction = initialize ? (byte)0 : target.XVelFrac;
                if (initialize)
                    cycles += 2 + 5 + 4 + 2 + (target.AnimNumber == 0x18 ? 4 + 2 : 2 + 2 + 3) + 5 + 7;
                else cycles += 4;
                cycles += 4 + 2;
                if (target.AnimNumber == 0x14) return cycles + 3 + 6;
                cycles += 2 + 2 + 4 + 2;
                bool left = (target.Direction & 1) == 0;
                cycles += left ? 3 : 2;
                cycles += 3 + 2;
                if (player.ScrollState == 4)
                {
                    cycles += 2 + 4 + 2;
                    bool edge = left ? (byte)target.X < 0x10 : (byte)target.X >= 0xF0;
                    cycles += edge ? 2 : 3;
                    if (edge) return cycles + 3 + IntegrateXValues(target, left, velocity, fraction);
                }
                else cycles += 3;
                int screenX = (target.X - player.ScrollX) & 255;
                cycles += 4 + 2 + 3 + 2;
                if (!left)
                {
                    if (screenX >= 0xFC) return cycles + 2 + 3 + 35;
                    cycles += 3 + 2 + 6;
                }
                else
                {
                    if (screenX < 8)
                    {
                        cycles += 2 + 4 + 2;
                        if ((byte)player.X >= 4) return cycles + 2 + 3 + 35;
                        return cycles + 3 + 3 + IntegrateXValues(target, true, velocity, fraction);
                    }
                    cycles += 3 + 2 + 6;
                }
                var motion = ActorMotion.From(target);
                cycles += ActorMotionTiming.MoveX(ref motion, velocity, fraction, left, left ? 15 : 12, 1, player, tiles);
                if (!left) cycles += 3;                                    // JMP $836B
                // $836B: a wall class ≥3 bursts the shot ($FA6C anim $14) or clears a gas cloud ($18).
                cycles += 3 + 2;
                byte wall = motion.ProbeClass;
                if (wall < 3) return cycles + 3 + 6;
                cycles += 2 + 2;
                if (wall == 7) return cycles + 3 + 6;
                cycles += 2 + 4 + 2;
                if (target.AnimNumber == 0x18) return cycles + 3 + 3 + ClearSlotCycles;
                return cycles + 2 + 2 + 6 + 23 + 6;
            }
            if (target.Behavior == 0x05)
            {
                // $86A2 corpse: state 0 spawns debris unless the anim is one of five; $86C9 swaps
                // seven anims for their falling pose ($FA6C) before the common $86F8 tail.
                int cycles = 4 + 2;
                if ((target.StateLow & 15) != 0) cycles += 3;
                else
                {
                    cycles += 2 + 7 + 4;
                    bool skipDebris = false;
                    foreach (int animation in new[] { 0x96,0x61,0x45,0x46,0x56 })
                    {
                        cycles += 2;
                        if (target.AnimNumber == animation) { cycles += 3; skipDebris = true; break; }
                        cycles += 2;
                    }
                    if (!skipDebris) cycles += 6 + ActorEventTiming.DeathDebris(player.Objects, target);
                }
                cycles += 6 + 18 + 4;
                int swap = Array.IndexOf(CorpseSwapAnims, target.AnimNumber);
                byte animationAfter = target.AnimNumber;
                if (swap < 0) cycles += CorpseSwapAnims.Length * (2 + 2) + 3;
                else
                {
                    cycles += swap * (2 + 2) + 2 + 3 + 2 + (swap <= 2 ? 3 : 0) + 6 + 23;
                    animationAfter = swap == 0 ? (byte)0xA2 : swap <= 2 ? (byte)0x44 : (byte)0xBA;
                }
                if (animationAfter == 0x5F || (target.StateLow & 2) != 0) throw new NotSupportedException("86F8 corpse turn");
                cycles += 4 + 2 + 2 + 4 + 2 + 3 + 3 + 4 + 2;
                bool left = (target.Direction & 1) != 0;
                cycles += left ? 2 + 6 + IntegrateX(target, true) + 3 : 3 + 6 + IntegrateX(target, false);
                return cycles + 6 + IntegrateAndDecayY(target, player.GravityFrac) + 6;
            }
            if (target.Behavior == 0x08 || target.Behavior == 0x09)
                return PickupTest(target, slotIndex, player, events, target.Behavior == 0x09);
            if (target.Behavior == 0x2A) return Behavior2A(target, player, events);
            if (target.Behavior == 0x0B) return Behavior0B(target);
            if (target.Behavior == 0x21 || target.Behavior == 0x22) return Behavior21(target, player, tiles);
            if (target.Behavior == 0x3A)
            {
                // $9FCB: state 0 arms the walk-in velocities and flips $0580, then every state tests
                // $FB7B; a miss returns through BCS $A009, which crosses the $9F/$A0 page.
                int cycles = 4 + 2;
                cycles += (target.StateLow & 15) == 0 ? 2 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 4 + 2 + 5 + 7 : 3;
                int touch = TouchNoHit(player.Objects, player, target, out bool hit);
                // Contact: behaviour 5, $50=$13, $FA78 anim 9 (20), $51=$78, $F607 jump cut (18).
                if (hit) return cycles + 6 + touch + 2 + 2 + 5 + 2 + 3 + 2 + 6 + 20 + 2 + 3 + 6 + 18 + 6;
                return cycles + 6 + touch + 4 + 6;
            }
            if (target.Behavior == 0x1F)
            {
                bool left = target.Direction == 0x0A;
                int own = 4 + 2;
                if (target.Direction == 9) own += 3 + 6 + 3 + 3;
                else own += 2 + 2 + (left ? 3 + 6 + 3 : 2 + 3 + 6 + 3 + 3);
                return own + IntegrateX(target, left) + IntegrateYSubtract(target);
            }
            if (target.Behavior == 0x0A) return Behavior0A(target, slotIndex, player, tiles, events);
            if (target.Behavior == 0x0E && (target.StateLow & 15) == 0)
            {
                int deltaY = (byte)player.Y - (byte)target.Y;
                int magnitudeY = deltaY >= 0 ? deltaY : -deltaY;
                int distanceCycles = 4 + 2 + 4 + (deltaY >= 0 ? 3 : 2 + 2 + 2 + 2) + 6;
                if (magnitudeY < 8)
                {
                    int faceDirection = 2 + 5 + 4 + 2 + 4 + 4 + 4 + (target.X < player.X ? 3 : 2 + 2 + 5) + 6;
                    int faceFlags = 4 + 2 + 2 + 2 + 2 + 3 + 4 + 2 + 3 + 5 + 6;
                    int face = 6 + faceDirection + 6 + faceFlags + 6;
                    return 4 + 2 + 2 + 6 + distanceCycles + 2 + 2 + 2 + 6 + 23 + 2 + 5 + 6 + face + 7 + 6;
                }
                return 4 + 2 + 2 + 6 + distanceCycles + 2 + 3 + 6;
            }
            if (target.Behavior == 0x0E && (target.StateLow & 15) != 0)
            {
                int cycles = 4 + 2 + 3 + 4;
                byte velocity = target.XVelInt, fraction = target.XVelFrac;
                if (target.T480 != 0) cycles += 4;
                else
                {
                    if (target.T490 >= BayonetPhases.Length) throw new NotSupportedException("8DD3 phase outside table");
                    cycles += 2 + 4 + 4 + 2 + 2 + 4 + 5 + 4 + 5 + 4 + 5 + 4 + 5 + 4;
                    int tableIndex = BayonetPhases[target.T490] * 2;
                    short tableVelocity = (short)(BayonetX[tableIndex] | BayonetX[tableIndex + 1] << 8);
                    cycles += tableVelocity >= 0 ? 3 : 2 + 4 + 2 + 2 + 2 + 5 + 4 + 2 + 2 + 5;
                    int magnitude = Math.Abs((int)tableVelocity);
                    velocity = (byte)(magnitude >> 8); fraction = (byte)magnitude;
                    cycles += 7 + 4 + 2 + (target.T490 == 13 ? 2 + 2 + 5 : 3);
                    cycles += 3 + 2 + (player.AreaId == 5 ? 2 + 2 + 3 : 3 + 2 + 3) + 5;
                }
                cycles += 7 + 4 + 2 + 4 + 5 + 4 + 4 + 5 + 4 + 2;
                bool left = (target.Direction & 2) != 0;
                if (left) cycles += 3 + 6 + IntegrateXValues(target, true, velocity, fraction);
                else
                {
                    cycles += 2 + 6 + IntegrateXValues(target, false, velocity, fraction);
                    bool carry = target.X + velocity + ((target.XFrac + fraction) >> 8) > 65535;
                    cycles += carry ? 3 : 2 + 3;
                }
                return cycles + 6;
            }
            if (target.Behavior == 0x0C && (target.StateLow & 2) == 0
                && (target.T480 != 0 || (target.StateLow & 15) == 0) && target.AnimNumber != 0x43 && target.Health != 0)
            {
                if (slotIndex < 2 || player.State == 0x0C) throw new NotSupportedException("F200 push or EB52 state0C");
                bool initialize = (target.StateLow & 15) == 0;
                int velocityFraction = initialize ? 0 : target.XVelFrac;
                int velocity = initialize ? 1 : target.XVelInt;
                bool left = (target.Direction & 1) == 0;
                int fraction = left ? target.XFrac - velocityFraction : target.XFrac + velocityFraction;
                int nextX = (target.X + (left ? -velocity - (fraction < 0 ? 1 : 0) : velocity + (fraction >> 8))) & 65535;
                int integrate = IntegrateXValues(target, left, (byte)velocity, (byte)velocityFraction);
                int probe = new ActorProbeTiming(player, tiles).MeasureEmptyProbe(nextX, target.Y, left ? 3 : 0);
                int walk = 3 + 3 + 6 + integrate + 2 + 3 + 6 + probe + 3 + 2 + 3 + 4 + 2 + 5 + 6;
                int deltaX = player.X - nextX;
                int magnitudeX = deltaX >= 0 ? (byte)deltaX : (byte)-deltaX;
                if (magnitudeX < 0x32) throw new NotSupportedException("8C89 thug distance activation");
                int distance = 4 + 2 + 4 + 3 + 4 + 4 + 4 + (deltaX >= 0 ? 3 : 2 + 2 + 2 + 2) + 6;
                int own = 4 + 2 + (initialize ? 2 + 5 + 2 + 5 + 7 + 2 + 5 + 6 + 23 + 5 : 3)
                    + 4 + 2 + 2 + 4 + 2 + 4 + 2 + 2 + 7 + 2 + 4 + 2 + 2
                    + 2 + 6 + 3 + 4 + 2 + 6 + 2 + 3 + 6;
                return own + walk + distance - (left ? 2 : 0);
            }
            if (target.Behavior == 0x0C && (target.StateLow & 2) == 0 && (target.StateLow & 15) != 0)
            {
                int cycles = 4 + 2 + 3 + 4 + 2 + 2 + 4 + (target.T480 == 0 ? 3 : 2);
                cycles += 4 + 2 + (target.AnimNumber == 0x43 ? 3 : 2);
                if (target.AnimNumber != 0x43)
                {
                    if (target.T480 != 0) throw new NotSupportedException("8C64 unpriced moving thug");
                    return cycles + 2 + 6 + 23 + 2 + 5 + 6;
                }
                cycles += 7 + 4;
                if (target.T480 == 1) return cycles + 2 + 2 + 6 + 23 + 2 + 5 + 4 + 2 + 5 + 6;
                cycles += 3 + 4;
                if (target.Health == 0) return cycles + 3 + 6 + FaceApply(target, player) + 7 + 6;
                cycles += 2 + 6 + DistanceX(target, player) + 2;
                int delta = player.X - target.X;
                int magnitude = delta >= 0 ? (byte)delta : (byte)-delta;
                if (magnitude >= 0x32) return cycles + 3 + 6;
                return cycles + 2 + 6 + FaceApply(target, player) + 7 + 6;
            }
            if (target.Behavior == 0x0C && (target.StateLow & 2) != 0)
            {
                if (slotIndex < 2 || target.YVelInt >= 0 || player.State == 0x0C)
                    throw new NotSupportedException("8CBD falling thug slot or vertical branch");
                if (events?.ReadHookLatch == null || events.ReadHookLatch() != 0)
                    throw new NotSupportedException("F322 falling thug hook latch");
                int fractionY = target.YFrac - target.YVelFrac;
                int lowY = (byte)((byte)target.Y - (byte)target.YVelInt - (fractionY < 0 ? 1 : 0));
                int pageY = target.Y >> 8;
                if (lowY >= 0xF0) { lowY = (lowY + 16) & 255; pageY++; }
                int nextY = ((pageY & 255) << 8) | lowY;
                int floorCycles = 3 + 3 + 4 + 3 + 4 + 3 + 4 + 3 + 6 + IntegrateFallingY(target)
                    + 2 + 4 + 6 + DecayY(target, player.GravityFrac) + 3 + 2 + 6;
                var floorProbe = new ActorProbeTiming(player, tiles);
                floorCycles += floorProbe.MeasureProbe(target.X, nextY, 6, 3, out byte floor);
                floorCycles += 2 + 3 + 2;
                bool landed = floor != 0 && (floor != 1 || (floorProbe.LastPointY & 15) < 8);
                if (floor == 0) floorCycles += 3 + 6;
                else
                {
                    floorCycles += 2 + (floor != 1 ? 3 : 2 + 3 + 2 + 2 + (landed ? 2 : 3));
                    if (!landed) floorCycles += 2 + 6;
                    else
                    {
                        int snap = 3 + 2 + 3 + 4 + 2 + 3 + 5
                            + (lowY >= (floorProbe.LastPointY & 15) ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
                        floorCycles += 2 + 3 + 6 + snap + 6 + 20 + 2 + 6;
                        lowY -= floorProbe.LastPointY & 15;
                        if (lowY < 0) { lowY += 240; pageY--; }
                        nextY = ((pageY & 255) << 8) | lowY;
                    }
                }
                int cycles = 4 + 2 + 3 + 4 + 2 + 3 + 2 + 2 + 6 + floorCycles + 3;
                if (landed)
                    cycles += 2 + 4 + 2 + (target.AnimNumber == 0x41 ? 3 : 2 + 2 + 6 + 23 + 3);
                else cycles += 3 + 2 + 6 + 23;
                bool left = (target.Direction & 1) == 0;
                int fractionX = left ? target.XFrac - target.XVelFrac : target.XFrac + target.XVelFrac;
                int nextX = (target.X + (left ? -target.XVelInt - (fractionX < 0 ? 1 : 0)
                    : target.XVelInt + (fractionX >> 8))) & 65535;
                int wallCycles = new ActorProbeTiming(player, tiles).MeasureEmptyProbe(nextX, nextY, left ? 3 : 0);
                int walk = 3 + 3 + 6 + IntegrateXValues(target, left, target.XVelInt, target.XVelFrac)
                    + 2 + 3 + 6 + wallCycles + 3 + 2 + 3 + 4 + 2 + 5 + 6;
                cycles += 2 + 4 + 2 + (left ? 3 + 2 + 6 : 2 + 2 + 6 + 3) + walk;
                cycles += 4 + (landed ? 2 + 3 + 2 + 4 : 4);
                return cycles + 4 + 2 + 3 + 6;
            }
            throw new NotSupportedException("bank6 behavior " + target.Behavior.ToString("X2") + " state " + target.StateLow.ToString("X2"));
        }

        private static readonly byte[] ShotDirections = { 0x01, 0x02, 0x08, 0x04, 0x09, 0x05, 0x0A, 0x06 };

        /// <summary>$8BDA straight shot: the $0580 direction picks the facing ($F24B/$F2A6) and the
        /// X ($F4FD/$F51B) and Y ($F55E up/$F539 down) integrators; the last one is a JMP.</summary>
        private static int Behavior0B(ObjectSystem.Slot target)
        {
            int index = Array.IndexOf(ShotDirections, target.Direction);
            int cycles = 4;
            if (index < 0) return cycles + ShotDirections.Length * (2 + 2) + 6;
            cycles += index * (2 + 2) + 2 + Branch(0x8BDF + 4 * index, index == 0 ? 0x8BFE : 0x8C04, true);
            const int Facing = 6 + 4 + 2 + 5 + 6;
            bool horizontal = (target.Direction & 3) != 0, left = (target.Direction & 2) != 0;
            bool vertical = (target.Direction & 0x0C) != 0, down = (target.Direction & 4) != 0;
            if (horizontal) cycles += Facing + (vertical ? 6 : 3) + IntegrateX(target, left);
            if (vertical) cycles += 3 + (down ? IntegrateYAdd(target) : IntegrateYSubtract(target));
            return cycles;
        }

        /// <summary>$F539: Y += velocity, skipping the $F0..$FF rows of each 240-row page.</summary>
        private static int IntegrateYAdd(ObjectSystem.Slot target)
        {
            int carry = (target.YFrac + target.YVelFrac) >> 8;
            int low = ((byte)target.Y + (byte)target.YVelInt + carry) & 0xFF;
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + (low < 0xF0 ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
        }

        /// <summary>$F919: |player Y − actor Y| low bytes.</summary>
        private static int DistanceYBody(ObjectSystem.Slot target, PlayerCore player, out byte distance)
        {
            int delta = (byte)player.Y - (byte)target.Y;
            distance = delta >= 0 ? (byte)delta : (byte)-delta;
            return 4 + 2 + 4 + (delta >= 0 ? 3 : 2 + 2 + 2 + 2) + 6;
        }

        /// <summary>$9F4C walker (21) / mirrored walker (22): state 0 wakes inside a 16×23 box; later
        /// states fall with $F2D7, toggle touch class $95 by the vertical distance and walk with
        /// $F200/$F254, turning on any wall class.</summary>
        private static int Behavior21(ObjectSystem.Slot target, PlayerCore player, ITileMap tiles)
        {
            int cycles = 4 + 2;
            bool initial = (target.StateLow & 15) == 0;
            cycles += Branch(0x9F51, 0x9F75, !initial);
            if (initial)
            {
                cycles += 5 + 2 + 5 + 6 + DistanceXBody(target, player, out byte dx) + 2 + Branch(0x9F60, 0x9F71, dx >= 0x10);
                if (dx < 0x10)
                {
                    cycles += 6 + DistanceYBody(target, player, out byte dy) + 2 + Branch(0x9F67, 0x9F71, dy >= 0x17);
                    if (dy < 0x17) cycles += 7 + 2 + 5;
                }
                return cycles + 6 + AnimRestartCycles + 6;
            }
            cycles += 3 + 2 + Branch(0x9F79, 0x9F74, player.State == 7);
            if (player.State == 7) return cycles + 6;
            var motion = ActorMotion.From(target);
            cycles += 2 + 2 + 6 + ActorMotionTiming.MoveY(ref motion, player.GravityFrac, 6, 2, player, tiles, out _);
            int dyAfter = (byte)player.Y - (byte)motion.Y;
            byte distanceAfter = dyAfter >= 0 ? (byte)dyAfter : (byte)-dyAfter;
            cycles += 6 + 4 + 2 + 4 + (dyAfter >= 0 ? 3 : 2 + 2 + 2 + 2) + 6 + 2 + Branch(0x9F87, 0x9F90, distanceAfter >= 6);
            cycles += distanceAfter < 6 ? 2 + 5 + Branch(0x9F8E, 0x9F95, true) : 2 + 5;
            cycles += 4 + 2 + Branch(0x9F9A, 0x9FB7, target.Behavior == 0x22);
            bool odd = (target.Direction & 1) != 0;
            bool right;
            if (target.Behavior == 0x22) { cycles += 4 + 2 + Branch(0x9FBC, 0x9FA3, odd); right = odd; }
            else { cycles += 4 + 2 + Branch(0x9FA1, 0x9FBE, odd); right = !odd; }
            cycles += 2 + 2 + 6 + ActorMotionTiming.MoveX(ref motion, target.XVelInt, target.XVelFrac, !right, right ? 0 : 3, 2, player, tiles) + 3;
            bool wall = motion.ProbeClass != 0;
            if (right) cycles += Branch(0x9FAC, 0x9FB6, !wall);
            else
            {
                cycles += Branch(0x9FC7, 0x9FB6, !wall);
                if (wall) cycles += Branch(0x9FC9, 0x9FAE, true);
            }
            if (wall) cycles += 4 + 2 + 5;
            return cycles + 6;
        }

        /// <summary>$8B80 dropped item: state 0 arms a $FF timer, odd states fall with $F2D7 (points
        /// $28..$2A) until they land, even states count the timer down, blink below $3C, expire through
        /// $FA9A, and otherwise run the $8837/$8A1A pickup test.</summary>
        private static int Behavior0A(ObjectSystem.Slot target, int slotIndex, PlayerCore player, ITileMap tiles, ActorEventTiming events)
        {
            int cycles = 4 + 2;
            bool initial = (target.StateLow & 15) == 0;
            cycles += Branch(0x8B85, 0x8B8F, !initial);
            if (initial) cycles += 2 + 5 + 7;
            bool resting = !initial && (target.StateLow & 2) != 0;
            cycles += 4 + 2 + Branch(0x8B94, 0x8BA5, resting);
            if (!resting)
            {
                var motion = ActorMotion.From(target);
                cycles += 2 + 2 + 6 + ActorMotionTiming.MoveY(ref motion, player.GravityFrac, 0x28, 2, player, tiles, out _) + 3;
                cycles += Branch(0x8B9F, 0x8BA4, motion.ProbeClass == 0);
                if (motion.ProbeClass != 0) cycles += 7;
                return cycles + 6;
            }
            cycles += 4 + Branch(0x8BA8, 0x8BAD, target.T480 != 0);
            if (target.T480 == 0) return cycles + 3 + ClearSlotCycles;
            int timer = target.T480 - 1;
            cycles += 7 + 4 + 2 + Branch(0x8BB5, 0x8BCD, timer >= 0x3C);
            if (timer < 0x3C)
                cycles += 2 + ((timer & 1) != 0 ? 3 + 4 + 2 + 5 : 2 + 4 + 2 + 5 + 3);
            cycles += 4 + 2 + Branch(0x8BD2, 0x8BD7, target.AnimNumber < 0x22) + 3;
            return cycles + PickupTest(target, slotIndex, player, events, target.AnimNumber < 0x22);
        }

        /// <summary>$8837 (anims $22+) / $8A1A (anims $1C+) from JSR $FB7B: a miss returns; a touch
        /// marks the placement ($FC29), clears the slot ($FA9A) and jumps through the anim table.</summary>
        private static int PickupTest(ObjectSystem.Slot target, int slotIndex, PlayerCore player, ActorEventTiming events, bool scoreTable)
        {
            int touch = TouchNoHit(player.Objects, player, target, out bool hit);
            if (!hit) return 6 + touch + 3 + 6;
            if (events == null) throw new NotSupportedException("8837 pickup queue owner absent");
            int cycles = 6 + touch + 2 + 6 + 48 + 6 + ClearSlotCycles + 29;
            byte anim = target.AnimNumber;
            if (scoreTable)
            {
                if (anim != 0x1C && anim != 0x1D) throw new NotSupportedException("8A36 pickup anim " + anim.ToString("X2"));
                // $8A3F / $8A4D: SFX $17, the rising icon, then add 1 / 5 to the bonus digits.
                cycles += 2 + 6 + events.Enqueue(0x17) + 6 + PickupIcon(target, slotIndex, player.Objects) + 2 + 3;
                if (anim == 0x1C) cycles += 3;                          // BNE $8A59
                return cycles + BonusDigitsAdd(player.ScoreDigits, anim == 0x1C ? 1 : 5);
            }
            if (anim == 0x22 || anim == 0x23)
            {
                if (anim != 0x23) throw new NotSupportedException("89D6 full repair pickup");
                int repair = player.HitLadder >= 3 ? 4 + 2 + 2 + 2 + 4 + 6
                    : player.HitLadder >= 2 ? 4 + 2 + 4 + 4 + 2 + 2 + 2 + 4 + 6
                    : 4 + 2 + 4 + 4 + 2 + 3 + 4 + 2 + (player.HitLadder == 1 ? 2 + 2 + 4 : 3) + 6;
                return cycles + 2 + 6 + events.Enqueue(0x17) + 4 + 2 + 3 + repair;
            }
            if (anim >= 0x26 && anim <= 0x28)
            {
                // $8865/$8889/$88AD: SFX $18, icon/bracket into $05BA/$05BC, $54, +10 saved meter
                // ($88FF), then $05C0 gates the $53 promotion and the $88CE copy ($88AD falls into it).
                cycles += 2 + 6 + events.Enqueue(0x18) + 2 + 4 + 2 + 4 + 2 + 3 + 2 + 6
                    + SavedMeterAddTen(player.GasEnergyHi, player.GasEnergyLo) + 4;
                if (player.ActiveGasEnergyLo == 0) return cycles + 3 + 6;
                return cycles + 2 + 2 + 3 + (anim == 0x28 ? 0 : 3) + 3 + 4 + 3 + 4 + 6;
            }
            throw new NotSupportedException("8853 pickup anim " + anim.ToString("X2"));
        }

        /// <summary>$88FF with A=$0A: $61=0 starts the meter at $70/$70; ten units keep the low digit
        /// and raise $60, capped at $79/$79.</summary>
        private static int SavedMeterAddTen(byte high, byte low)
        {
            int cycles = 3 + (low != 0 ? 3 : 2 + 2 + 3 + 3);
            if (low == 0) high = 0x70;
            cycles += 2 + 2 + 2 + 2 + 2 + 2 + 3 + 2 + 2 + 2 + 3 + 2;
            return cycles + (high == 0x79 ? 3 + 2 + 3 + 3 + 6 : 2 + 5 + 3 + 6);
        }

        /// <summary>$8A59: unless every digit is 9, add <paramref name="units"/> to $05CC and carry
        /// single units up through $05CA…$05C4 (a carry out of $05C4 is dropped).</summary>
        private static int BonusDigitsAdd(byte[] digits, int units)
        {
            int cycles = 0;
            for (int index = 0; index < 5; index++)
            {
                cycles += 4 + 2;
                if (digits[index] != 0x79) { cycles += 3; break; }
                cycles += 2;
                if (index == 4) return cycles + 6;
            }
            int value = (digits[4] & 15) + units;
            cycles += 4 + 2 + 2 + 3 + 2;
            if (value < 10) return cycles + 2 + 2 + 2 + 4 + 6;
            cycles += 3 + 2 + 2 + 2 + 2 + 4;
            for (int index = 3; index >= 0; index--)
            {
                cycles += 4 + 2 + 2 + 2 + 2;
                if ((digits[index] & 15) + 1 < 10) return cycles + 2 + 2 + 2 + 4 + 6;
                if (index == 0) return cycles + 3 + 6;
                cycles += 3 + 2 + 4;
            }
            return cycles;
        }

        /// <summary>$8B34 rising pickup icon. The pickup's own slot was just cleared by $FA9A, so the
        /// $FA4C scan may reuse it.</summary>
        private static int PickupIcon(ObjectSystem.Slot target, int ownSlot, ObjectSystem objects)
        {
            int cycles = 6 + FindFreeSlot(objects, out int slot, ownSlot) + Branch(0x8B37, 0x8B7C, slot < 0);
            if (slot < 0) return cycles + 6;
            return cycles + 3 + 4 * (4 + 5) + 4 + 2 + 2 + 2 + 4 + 3 + 6 + AnimSeedSlotYCycles
                + 2 + 5 + 2 + 5 + 2 + 5 + 5 + 5 + 2 + 5 + 6;
        }



        private const int ClearSlotCycles = 2 + 5 + 5 + 5 + 5 + 2 + 5 + 6;   // $FA9A

        /// <summary>$F928: |player X − actor X| low byte, carry set when the player is right of it.</summary>
        private static int DistanceXBody(ObjectSystem.Slot target, PlayerCore player, out byte distance)
        {
            int delta = player.X - target.X;
            distance = delta >= 0 ? (byte)delta : (byte)-(byte)delta;
            return 4 + 2 + 4 + 3 + 4 + 4 + 4 + (delta >= 0 ? 3 : 2 + 2 + 2 + 2) + 6;
        }

        private const int AnimRestartCycles = 2 + 5 + 5 + 6;          // $F869
        private const int AnimSeedSlotYCycles = 5 + 2 + 5 + 5 + 2 + 5 + 2 + 5 + 6; // $FA84

        /// <summary>$FA4C: first free slot from 15 down to 5; −1 when every slot is live.</summary>
        private static int FindFreeSlot(ObjectSystem objects, out int slot, int alsoFree = -1)
        {
            int cycles = 2;
            for (slot = 15; slot >= 5; slot--)
            {
                cycles += 4;
                if (!objects.Slots[slot].Active || slot == alsoFree) return cycles + 3 + 2 + 6;
                cycles += 2 + 2 + 2 + (slot > 5 ? 3 : 2);
            }
            slot = -1;
            return cycles + 2 + 6;
        }

        /// <summary>$9DBD turret: state 0 waits for |dx| &lt; $40, odd states face the player and hold
        /// until anim frame $0D, even states rearm on $0400 visibility and fire at frame $16/timer 4.</summary>
        private static int Behavior2A(ObjectSystem.Slot target, PlayerCore player, ActorEventTiming events)
        {
            int cycles = 4 + 2;
            bool initial = (target.StateLow & 15) == 0;
            cycles += Branch(0x9DC2, 0x9DDC, !initial);
            if (initial)
            {
                cycles += 6 + DistanceXBody(target, player, out byte distance) + 2;
                cycles += Branch(0x9DC9, 0x9DD8, distance >= 0x40);
                if (distance < 0x40) cycles += 7 + 2 + 5 + 2 + 5;
                return cycles + 6 + AnimRestartCycles + 6;
            }
            cycles += 6 + FaceApply(target, player) + 4 + 2;
            bool armed = (target.StateLow & 2) != 0;
            cycles += Branch(0x9DE4, 0x9E01, armed);
            if (!armed)
            {
                cycles += 2 + 5 + 2 + 5 + 4 + 2;
                cycles += Branch(0x9DF5, 0x9E00, target.AnimFrame != 0x0D);
                if (target.AnimFrame == 0x0D) cycles += 7 + 4 + 5;
                return cycles + 6;
            }
            cycles += 4 + 5 + 4 + 2;
            byte flags = target.Flags;
            cycles += Branch(0x9E0C, 0x9E00, flags == 0x50);
            if (flags == 0x50) return cycles + 6;
            cycles += 2 + Branch(0x9E10, 0x9E00, flags == 0x10);
            if (flags == 0x10) return cycles + 6;
            cycles += 4 + 2 + Branch(0x9E17, 0x9E1E, target.AnimFrame == 0x16);
            if (target.AnimFrame == 0x16)
            {
                cycles += 4 + 2 + Branch(0x9E23, 0x9E00, target.AnimTimer != 4);
                if (target.AnimTimer != 4) return cycles + 6;
                if (events == null) throw new NotSupportedException("9E27 turret shot queue owner absent");
                cycles += 2 + 6 + events.Enqueue(0x29) + 6;
                cycles += 6 + FindFreeSlot(player.Objects, out int slot) + Branch(0x9E3F, 0x9E84, slot < 0);
                if (slot >= 0)
                    cycles += 3 + 4 + 5 + 2 + 2 + 4 + 2 + 4 + 3 + 4 + 4 + 3 + 5 + 4 + 5
                        + 4 + 2 + 2 + 5 + 4 + 5 + 2 + 6 + AnimSeedSlotYCycles + 2 + 5 + 2 + 5
                        + 6 + (2 + 5 + 2 + 5 + 6);
                return cycles + 6 + 6;
            }
            cycles += 2 + Branch(0x9E1B, 0x9E2E, target.AnimFrame >= 0x17);
            if (target.AnimFrame < 0x17) return cycles + 6;
            return cycles + 2 + 5 + 2 + 5 + 7 + 6;
        }

        private static int Branch(int instruction, int target, bool taken)
            => taken ? 3 + (((instruction + 2) & 0xFF00) == (target & 0xFF00) ? 0 : 1) : 2;

        public static int TouchNoHit(ObjectSystem objects, PlayerCore player, ObjectSystem.Slot target, out bool hit)
        {
            hit = false;
            int cycles = 3 + 2;
            if (player.State == 7) return cycles + 3 + 2 + 6;
            cycles += 2 + 2;
            if (player.State == 0x0F) return cycles + 3 + 2 + 6;
            cycles += 2 + 4;
            if ((player.Flags & 0x80) == 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 4;
            if (!target.Active) return cycles + 3 + 2 + 6;
            cycles += 2 + 4;
            if ((target.Flags & 0x80) == 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 4 + 2;
            if (target.Behavior == 7) return cycles + 3 + 2 + 6;
            cycles += 2 + 2 + 4;
            if (target.BehaviorParam == 0) return cycles + 3 + 2 + 6;
            int hitClass = target.BehaviorParam & 31;
            cycles += 2 + 2 + 2 + 4 + PageCross(0xFBE9, hitClass) + 3;
            int height = EnemyBehaviors.TouchYHalf[hitClass];
            byte animation = objects.Slots[0].AnimNumber;
            cycles += 4 + 2;
            if (animation == 6) { cycles += 3 + 3 + 2 + 2 + 3; height = (byte)(height - 8); }
            else
            {
                cycles += 2 + 2;
                if (animation == 7) { cycles += 3 + 3 + 2 + 2 + 3; height = (byte)(height - 8); }
                else cycles += 2 + 3;
            }
            int deltaX = player.X - target.X;
            int magnitudeX = deltaX >= 0 ? (byte)deltaX : (byte)-deltaX;
            cycles += 4 + 2 + 4 + 3 + 4 + 4 + 4 + (deltaX >= 0 ? 3 : 2 + 2 + 2) + 4;
            if (magnitudeX >= EnemyBehaviors.TouchXHalf[hitClass]) return cycles + 3 + 6;
            int deltaY = (byte)player.Y - (byte)target.Y;
            int magnitudeY = deltaY >= 0 ? deltaY : -deltaY;
            hit = magnitudeY < height;
            return cycles + 2 + 4 + 2 + 4 + (deltaY >= 0 ? 3 : 2 + 2 + 2) + 3 + (hit ? 3 : 2) + 6;
        }

        private static int PageCross(int address, int index) => ((address & 255) + index) > 255 ? 1 : 0;

        private static int DistanceX(ObjectSystem.Slot target, PlayerCore player)
            => 4 + 2 + 4 + 3 + 4 + 4 + 4 + (player.X >= target.X ? 3 : 2 + 2 + 2 + 2) + 6;

        private static int FaceApply(ObjectSystem.Slot target, PlayerCore player)
        {
            int direction = 2 + 5 + 4 + 2 + 4 + 4 + 4 + (target.X < player.X ? 3 : 2 + 2 + 5) + 6;
            int flags = 4 + 2 + 2 + 2 + 2 + 3 + 4 + 2 + 3 + 5 + 6;
            return 6 + direction + 6 + flags + 6;
        }

        private static int IntegrateX(ObjectSystem.Slot target, bool left)
        {
            return IntegrateXValues(target, left, target.XVelInt, target.XVelFrac);
        }

        private static int IntegrateXValues(ObjectSystem.Slot target, bool left, byte velocity, byte velocityFraction)
        {
            int fraction = left ? target.XFrac - velocityFraction : target.XFrac + velocityFraction;
            int low = left ? (byte)target.X - velocity - (fraction < 0 ? 1 : 0)
                : (byte)target.X + velocity + (fraction >> 8);
            bool pageChange = left ? low < 0 : low > 255;
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + (pageChange ? 2 + 4 + 2 + 5 : 3) + 6;
        }

        private static int IntegrateYSubtract(ObjectSystem.Slot target)
        {
            int fraction = target.YFrac - target.YVelFrac;
            int low = (byte)target.Y - (byte)target.YVelInt - (fraction < 0 ? 1 : 0);
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + (low < 0 ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        private static int IntegrateAndDecayY(ObjectSystem.Slot target, byte gravity)
        {
            int cycles = 4 + (target.YVelInt >= 0 ? 3 : 2) + 6;
            if (target.YVelInt >= 0) cycles += IntegrateYSubtract(target);
            else cycles += IntegrateFallingY(target);
            return cycles + 3 + DecayY(target, gravity);
        }

        private static int IntegrateFallingY(ObjectSystem.Slot target)
        {
            int fraction = target.YFrac - target.YVelFrac;
            int low = (byte)target.Y - (byte)target.YVelInt - (fraction < 0 ? 1 : 0);
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2
                + ((byte)low < 0xF0 ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
        }

        private static int DecayY(ObjectSystem.Slot target, byte gravity)
        {
            int velocity = unchecked((byte)target.YVelInt) - (target.YVelFrac < gravity ? 1 : 0);
            byte nextVelocity = (byte)velocity;
            int cycles = 4 + 2 + 3 + 5 + 4 + 2 + 5;
            if (nextVelocity < 0x80) cycles += 3;
            else cycles += 2 + 2 + (nextVelocity >= 0xF9 ? 3 : 2 + 2 + 5 + 2 + 5);
            return cycles + 6;
        }
    }
}
