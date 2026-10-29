import re
root = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core'
p = root + r'\PlayerCore.ReachedDispatchTiming.cs'
s = open(p, encoding='utf-8').read()
a = s.index('        private int ExpandedRejectedVerticalPair()')
b = s.index('        private static int ExpandedQueueCycles(bool accepts)')
new = '''        /// <summary>$F64A for the player at (x, y): the slot-1 pair test, then the $F784 scan of
        /// slots 2..15. A solid actor ($0400/$0570 bit 7) runs the $F7A8 geometry; a hit leaves
        /// through $F7DE with SEC, which the callers do not price yet.</summary>
        private int ExpandedRejectedVerticalPair(int x, int y)
        {
            var partner = Objects.Slots[1];
            bool rejected = !partner.Active;
            int cycles = 2 + 2 + 2 + 4 + ScrollBranch(0xF651, 0xF695, rejected);
            if (!rejected) { rejected = (Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF656, 0xF695, rejected); }
            if (!rejected) { rejected = (partner.Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF65B, 0xF695, rejected); }
            if (!rejected) { rejected = partner.Behavior == 0; cycles += 4 + ScrollBranch(0xF660, 0xF695, rejected); }
            if (!rejected) throw new NotSupportedException("F662 vertical pair geometry");
            cycles += 2 + 3 + 2;
            for (int slot = 2; slot < 16; slot++)
            {
                var state = Objects.Slots[slot];
                bool skip = !state.Active;
                cycles += 4 + ScrollBranch(0xF789, 0xF7A1, skip);
                if (!skip) { skip = (state.Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF78E, 0xF7A1, skip); }
                if (!skip) { skip = (state.BehaviorParam & 0x80) == 0; cycles += 4 + ScrollBranch(0xF793, 0xF7A1, skip); }
                if (!skip)
                {
                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out bool hit) + 3;
                    if (hit) throw new NotSupportedException("F79E stand-on-actor contact");
                    cycles += ScrollBranch(0xF79C, 0xF7A1, true);
                }
                cycles += 2 + 2 + ScrollBranch(0xF7A4, 0xF786, slot != 15);
            }
            return cycles + 2 + 6;
        }

        /// <summary>$F699 with X = the player: $0400 bit 4 selects the 16-bit world distance.</summary>
        private int TimingDistanceX(int x, ObjectSystem.Slot actor, out int low, out int high)
        {
            if ((Objects.Slots[0].Flags & 0x10) == 0) throw new NotSupportedException("F6C6 screen-relative actor distance");
            int delta = (x - actor.X) & 0xFFFF;
            int cycles = 4 + 2 + 2 + 4 + 2 + 4 + 3 + 4 + 4 + 3;
            bool negative = x < actor.X;
            if (negative) { delta = (-delta) & 0xFFFF; cycles += 2 + 3 + 2 + 2 + 3 + 3 + 2 + 2 + 3; }
            else cycles += 3;
            low = delta & 255; high = delta >> 8;
            return cycles + 3 + 6;
        }

        /// <summary>$F6DF: player Y minus the actor Y moved by ±$10 across a 240-row page; the
        /// result is folded by the sign of its LOW byte.</summary>
        private static int TimingDistanceY(int y, ObjectSystem.Slot actor, out byte value)
        {
            int actorY = actor.Y;
            int cycles = 4 + 3 + 4 + 3 + 4;
            int actorPage = actorY >> 8, playerPage = (y >> 8) & 255;
            if (actorPage == playerPage) cycles += 3;
            else if (actorPage > playerPage) { cycles += 2 + 2 + 3 + 2 + 3 + 3 + 2 + 3 + 3; actorY = (actorY - 0x10) & 0xFFFF; }
            else { cycles += 2 + 3 + 3 + 2 + 3 + 3 + 2 + 3; actorY = (actorY + 0x10) & 0xFFFF; }
            byte low = (byte)((y & 255) - (actorY & 255));
            cycles += 4 + 2 + 3 + 3 + 4 + 3 + 3 + 3;
            if (low < 0x80) cycles += 3;
            else { cycles += 2 + 2 + 2 + 2; low = (byte)(-low); }
            value = low;
            return cycles + 4 + 6;
        }

        /// <summary>$F7A8 stand test: |dx| below the class width and |dy| at most the class height.</summary>
        private int TimingStandGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit)
        {
            hit = false;
            int cls = actor.BehaviorParam & 31;
            int cycles = 4 + 2 + 3 + 6 + TimingDistanceX(x, actor, out int low, out int high) + 3;
            if (high != 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 3 + 4;
            if (low >= EnemyBehaviors.TouchXHalf[cls]) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 6 + TimingDistanceY(y, actor, out byte dy) + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0);
            int height = EnemyBehaviors.TouchYHalf[cls];
            if (dy == height) cycles += 3;
            else
            {
                cycles += 2;
                if (dy > height) return cycles + 3 + 2 + 6;
                cycles += 2;
            }
            hit = true;
            return cycles + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0) + 2 + 3 + 3 + 6;
        }

        /// <summary>$F74E side test: |dx| at most the class width and |dy| below the class height.</summary>
        private int TimingSideGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit)
        {
            hit = false;
            int cls = actor.BehaviorParam & 31;
            int cycles = 4 + 2 + 3 + 6 + TimingDistanceX(x, actor, out int low, out int high) + 3;
            if (high != 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 3 + 4;
            int width = EnemyBehaviors.TouchXHalf[cls];
            if (low == width) cycles += 3;
            else
            {
                cycles += 2;
                if (low > width) return cycles + 3 + 2 + 6;
                cycles += 2;
            }
            cycles += 3 + 3 + 6 + TimingDistanceY(y, actor, out byte dy) + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0);
            if (dy >= EnemyBehaviors.TouchYHalf[cls]) return cycles + 3 + 2 + 6;
            hit = true;
            return cycles + 2 + 3 + 4 + 2 + 3 + 3 + 6;
        }

'''
s = s[:a] + new + s[b:]
s = s.replace('''                    pair = ExpandedRejectedVerticalPair();
                    dispatch += 6 + ScrollBranch(0xD6B4, 0xD709, false)''', '''                    pair = ExpandedRejectedVerticalPair(X, nextY);
                    dispatch += 6 + ScrollBranch(0xD6B4, 0xD709, false)''')
s = s.replace('''                    int horizontalPair = TimingRejectedHorizontalPair();''', '''                    int horizontalPair = TimingRejectedHorizontalPair(nextX, nextY);''')
s = s.replace('''            int pair = ExpandedRejectedVerticalPair();
            int probe = ExpandedProbeSet(X, nextY, 6, out maximum, out int lastY, 3);''', '''            int pair = ExpandedRejectedVerticalPair(X, nextY);
            int probe = ExpandedProbeSet(X, nextY, 6, out maximum, out int lastY, 3);''')
s = s.replace('''            foreach (var slot in Objects.Slots)
                if (slot.Active && (slot.Flags & slot.BehaviorParam & 0x80) != 0) throw new NotSupportedException("F73B armed horizontal geometry");
            int fraction = right ? XFrac + velocityFraction : XFrac - velocityFraction;''', '''            int fraction = right ? XFrac + velocityFraction : XFrac - velocityFraction;''')
s = s.replace('''            int pair = TimingRejectedHorizontalPair();
            int probe = ExpandedProbeSet(nextX, coordinateY, first, out int maximum, out _, 3);''', '''            int pair = TimingRejectedHorizontalPair(nextX, coordinateY);
            int probe = ExpandedProbeSet(nextX, coordinateY, first, out int maximum, out _, 3);''')
open(p, 'w', encoding='utf-8').write(s)

p = root + r'\PlayerCore.Air01Timing.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('            int pair = ExpandedRejectedVerticalPair();', '            int pair = ExpandedRejectedVerticalPair(coordinateX, coordinateY);')
open(p, 'w', encoding='utf-8').write(s)

p = root + r'\PlayerCore.DispatchTiming.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('''                int pair = TimingRejectedHorizontalPair();''', '''                int pair = TimingRejectedHorizontalPair(nextX, nextY);''')
s = s.replace('''        private int TimingRejectedHorizontalPair()
        {''', '''        /// <summary>$F612 for the player at (x, y), then the $F724 side scan: solid actors run
        /// the $F74E geometry unless the player is in state $0B.</summary>
        private int TimingRejectedHorizontalPair(int x, int y)
        {''')
s = s.replace('''                if (!skip) cycles += 3 + 2 + ScrollBranch(0xF739, 0xF747, State == 0x0B);''', '''                if (!skip)
                {
                    cycles += 3 + 2 + ScrollBranch(0xF739, 0xF747, State == 0x0B);
                    if (State != 0x0B)
                    {
                        cycles += 3 + 6 + TimingSideGeometry(x, y, state, out bool hit) + 3;
                        if (hit) throw new NotSupportedException("F744 side actor contact");
                        cycles += ScrollBranch(0xF742, 0xF747, true);
                    }
                }''')
open(p, 'w', encoding='utf-8').write(s)
print('ok')
