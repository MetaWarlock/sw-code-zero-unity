using System;

namespace Darkwing.Core
{
    public static class ActorBank6Timing
    {
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
                    if (!target.Active || target.Behavior <= 4) throw new NotSupportedException("FAAE target early gate");
                    cycles += 3 + 4 + 2 + 5 + 6;
                    cycles += ActorTailTiming.ProjectileNoHit(objects, target, out bool projectileHit, out int projectileIndex);
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
                int carry = left ? (target.XFrac < fraction ? 1 : 0) : (target.XFrac + fraction) >> 8;
                int nextX = (target.X + (left ? -velocity - carry : velocity + carry)) & 65535;
                int probe = new ActorProbeTiming(player, tiles).MeasureEmptyProbe(nextX, target.Y, left ? 15 : 12, 2);
                cycles += 3 + 3 + 6 + IntegrateXValues(target, left, velocity, fraction) + 2 + 3 + 6 + probe;
                cycles += 3 + 2 + 3 + 4 + 2 + 5 + 6;
                if (!left) cycles += 3;
                return cycles + 3 + 2 + 3 + 6;
            }
            if (target.Behavior == 0x05
                && target.AnimNumber != 0xA0 && target.AnimNumber != 0x7E && target.AnimNumber != 0x7F
                && target.AnimNumber != 0x69 && target.AnimNumber != 0x6B && target.AnimNumber != 0x6A && target.AnimNumber != 0x72)
            {
                if (target.AnimNumber == 0x5F || (target.StateLow & 2) != 0) throw new NotSupportedException("86F8 corpse turn");
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
                cycles += 6 + 18 + 4 + 7 * (2 + 2) + 3
                    + 4 + 2 + 2 + 4 + 2 + 3 + 3 + 4 + 2;
                bool left = (target.Direction & 1) != 0;
                cycles += left ? 2 + 6 + IntegrateX(target, true) + 3 : 3 + 6 + IntegrateX(target, false);
                return cycles + 6 + IntegrateAndDecayY(target, player.GravityFrac) + 6;
            }
            if (target.Behavior == 0x1F)
            {
                bool left = target.Direction == 0x0A;
                int own = 4 + 2;
                if (target.Direction == 9) own += 3 + 6 + 3 + 3;
                else own += 2 + 2 + (left ? 3 + 6 + 3 : 2 + 3 + 6 + 3 + 3);
                return own + IntegrateX(target, left) + IntegrateYSubtract(target);
            }
            if (target.Behavior == 0x0A && (target.StateLow & 15) != 0 && (target.StateLow & 2) != 0 && target.T480 != 0)
            {
                int cycles = 4 + 2 + 3 + 4 + 2 + 3 + 4 + 3 + 7 + 4 + 2;
                int timer = target.T480 - 1;
                if (timer >= 0x3C) cycles += 3;
                else
                {
                    cycles += 2 + 2;
                    cycles += (timer & 1) != 0 ? 3 + 4 + 2 + 5 : 2 + 4 + 2 + 5 + 3;
                }
                cycles += 4 + 2 + (target.AnimNumber < 0x22 ? 3 : 2) + 3;
                int touch = TouchNoHit(player.Objects, player, target, out bool hit);
                if (hit)
                {
                    if (target.AnimNumber != 0x23 || events == null) throw new NotSupportedException("8BCD pickup variant or queue owner");
                    int repair = player.HitLadder >= 3 ? 4 + 2 + 2 + 2 + 4 + 6
                        : player.HitLadder >= 2 ? 4 + 2 + 4 + 4 + 2 + 2 + 2 + 4 + 6
                        : 4 + 2 + 4 + 4 + 2 + 3 + 4 + 2 + (player.HitLadder == 1 ? 2 + 2 + 4 : 3) + 6;
                    return cycles + 6 + touch + 2 + 6 + 48 + 6 + 35 + 29 + 2 + 6 + events.Enqueue(0x17) + 4 + 2 + 3 + repair;
                }
                return cycles + 6 + touch + 3 + 6;
            }
            if (target.Behavior == 0x0A && (target.StateLow & 15) == 0 && target.YVelInt < 0)
            {
                if (events?.ReadHookLatch == null) throw new NotSupportedException("F322 actual hook latch owner absent");
                int fraction = target.YFrac - target.YVelFrac;
                int low = (byte)((byte)target.Y - (byte)target.YVelInt - (fraction < 0 ? 1 : 0));
                int page = target.Y >> 8;
                if (low >= 0xF0) { low = (low + 16) & 255; page++; }
                int nextY = ((page & 255) << 8) | low;
                var probe = new ActorProbeTiming(player, tiles);
                int probeCycles = probe.MeasureProbe(target.X, nextY, 40, 3, out byte result);
                if (result < 2) throw new NotSupportedException("8B80 initial debris non-solid landing");
                int snap = 3 + 2 + 3 + 4 + 2 + 3 + 5 + (low >= (probe.LastPointY & 15) ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
                int fall = 3 + 3 + 4 + 3 + 4 + 3 + 4 + 3 + IntegrateAndDecayY(target, player.GravityFrac) - 3
                    + 2 + 4 + 3 + (events.ReadHookLatch() != 0 ? 3 + 2 + 3 : 2)
                    + 6 + probeCycles + 2 + 3 + 2 + 2 + 3 + 2 + 3 + 6 + snap + 6 + 20 + 2 + 6;
                return 4 + 2 + 2 + 2 + 5 + 7 + 4 + 2 + 2 + 2 + 2 + 6 + fall + 3 + 2 + 7 + 6;
            }
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
                && (target.T480 != 0 || (target.StateLow & 15) == 0) && target.AnimNumber != 0x43 && target.Health != 0 && (target.Direction & 1) != 0)
            {
                if (slotIndex < 2 || player.State == 0x0C) throw new NotSupportedException("F200 push or EB52 state0C");
                bool initialize = (target.StateLow & 15) == 0;
                int velocityFraction = initialize ? 0 : target.XVelFrac;
                int velocity = initialize ? 1 : target.XVelInt;
                int fraction = target.XFrac + velocityFraction;
                int lowX = (byte)target.X + velocity + (fraction >> 8);
                int nextX = (target.X + velocity + (fraction >> 8)) & 65535;
                int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5 + (lowX > 255 ? 2 + 4 + 2 + 5 : 3) + 6;
                int probe = new ActorProbeTiming(player, tiles).MeasureEmptyProbe(nextX, target.Y, 0);
                int walk = 3 + 3 + 6 + integrate + 2 + 3 + 6 + probe + 3 + 2 + 3 + 4 + 2 + 5 + 6;
                int deltaX = player.X - nextX;
                int magnitudeX = deltaX >= 0 ? (byte)deltaX : (byte)-deltaX;
                if (magnitudeX < 0x32) throw new NotSupportedException("8C89 thug distance activation");
                int distance = 4 + 2 + 4 + 3 + 4 + 4 + 4 + (deltaX >= 0 ? 3 : 2 + 2 + 2 + 2) + 6;
                int own = 4 + 2 + (initialize ? 2 + 5 + 2 + 5 + 7 + 2 + 5 + 6 + 23 + 5 : 3)
                    + 4 + 2 + 2 + 4 + 2 + 4 + 2 + 2 + 7 + 2 + 4 + 2 + 2
                    + 2 + 6 + 3 + 4 + 2 + 6 + 2 + 3 + 6;
                return own + walk + distance;
            }
            throw new NotSupportedException("bank6 behavior " + target.Behavior.ToString("X2") + " state " + target.StateLow.ToString("X2"));
        }

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
            int fraction = target.YFrac - target.YVelFrac;
            int low = (byte)target.Y - (byte)target.YVelInt - (fraction < 0 ? 1 : 0);
            int cycles = 4 + (target.YVelInt >= 0 ? 3 : 2) + 6;
            if (target.YVelInt >= 0) cycles += IntegrateYSubtract(target);
            else cycles += 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2
                + ((byte)low < 0xF0 ? 3 : 2 + 2 + 5 + 4 + 2 + 5) + 6;
            cycles += 3;
            int velocity = unchecked((byte)target.YVelInt) - (target.YVelFrac < gravity ? 1 : 0);
            byte nextVelocity = (byte)velocity;
            cycles += 4 + 2 + 3 + 5 + 4 + 2 + 5;
            if (nextVelocity < 0x80) cycles += 3;
            else cycles += 2 + 2 + (nextVelocity >= 0xF9 ? 3 : 2 + 2 + 5 + 2 + 5);
            return cycles + 6;
        }
    }
}
