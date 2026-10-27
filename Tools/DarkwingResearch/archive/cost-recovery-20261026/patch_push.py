root = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core'
p = root + r'\PlayerCore.ReachedDispatchTiming.cs'
s = open(p, encoding='utf-8').read()

# 1. Vertical pair returns the hit and the $11 push instead of throwing.
old = '''        private int ExpandedRejectedVerticalPair(int x, int y)
        {'''
new = '''        private int ExpandedRejectedVerticalPair(int x, int y)
        {
            int cycles = ExpandedVerticalPair(x, y, out bool hit, out _);
            if (hit) throw new NotSupportedException("F79E stand-on-actor contact");
            return cycles;
        }

        /// <summary>$F64A for the player; on a $F7A8 hit it leaves through $F7DE with SEC and
        /// <paramref name="push"/> = $11 (class height − |dy|).</summary>
        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push)
        {
            hit = false;
            push = 0;'''
assert old in s
s = s.replace(old, new)
old = '''                if (!skip)
                {
                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out bool hit) + 3;
                    if (hit) throw new NotSupportedException("F79E stand-on-actor contact");
                    cycles += ScrollBranch(0xF79C, 0xF7A1, true);
                }'''
new = '''                if (!skip)
                {
                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out hit, out push) + 3;
                    if (hit) return cycles + 2 + 3 + 2 + 6;              // BCC not taken, JMP $F7DE: SEC / RTS
                    cycles += ScrollBranch(0xF79C, 0xF7A1, true);
                }'''
assert old in s
s = s.replace(old, new)
old = '''        private int TimingStandGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit)
        {
            hit = false;'''
new = '''        private int TimingStandGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit, out int push)
        {
            hit = false;
            push = 0;'''
assert old in s
s = s.replace(old, new)
old = '''            hit = true;
            return cycles + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0) + 2 + 3 + 3 + 6;
        }'''
new = '''            hit = true;
            push = (byte)(height - dy);
            return cycles + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0) + 2 + 3 + 3 + 6;
        }

        /// <summary>$F2F9…$F319 then $F49C and $F347 after a stand-on-actor hit: lift by $11 (at most 4),
        /// probe above ($09) when the lift moved the player above the pre-move Y, else below ($06);
        /// a solid ceiling (≥3) snaps down, a floor (≥2) snaps up. Always returns SEC.</summary>
        private int PlayerActorLandCycles(int x, ref int y, int previousY, int push, out int probeClass)
        {
            int cycles = 2 + 3;
            if (push > 4) { cycles += 2 + 3; push = 4; }
            else cycles += 3;
            int low = (y & 255) - push, page = y >> 8;
            cycles += 4 + 2 + 3 + 5;
            if (low >= 0) cycles += 3;
            else { cycles += 2 + 2 + 5 + 4 + 2 + 5; low = (low - 0x10) & 255; page--; }
            y = ((page & 255) << 8) | (low & 255);
            cycles += 6 + 4 + 2 + 3 + 4 + 3;                               // JSR $F49C, Y − ($02,$03)
            bool up = y < previousY;
            cycles += ScrollBranch(0xF4A7, 0xF4BE, !up) + 2 + 3 + 6;
            int probe = ExpandedProbeSet(x, y, up ? 9 : 6, out probeClass, out int lastY, 5);
            cycles += probe + 3 + 2;
            if (up)
            {
                if (probeClass < 3) cycles += 3 + 6;
                else
                {
                    cycles += 2 + 6 + PlayerSnapDown(ref y, lastY & 15) + 6 + 20 + 2 + 6;
                }
            }
            else
            {
                if (probeClass < 2) cycles += 3 + 2 + 6;
                else cycles += 2 + (probeClass != 2 ? 3 : 2) + 6 + PlayerSnapUp(ref y, lastY & 15) + 6 + 20 + 2 + 6;
            }
            return cycles + 3 + 6 + 20 + 2 + 6;                           // JMP $F347: $F5FC, SEC, RTS
        }

        private static int PlayerSnapUp(ref int y, int nibble)
        {
            int low = (y & 255) - nibble, page = y >> 8;
            bool borrow = low < 0;
            if (borrow) { low = (low - 0x10) & 255; page--; }
            y = ((page & 255) << 8) | (low & 255);
            return 3 + 2 + 3 + 4 + 2 + 3 + 5 + (borrow ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        private static int PlayerSnapDown(ref int y, int nibble)
        {
            int low = ((y & 255) + ((~nibble) & 15) + 1) & 255, page = y >> 8;
            bool wrap = low >= 0xF0;
            if (wrap) { low = (low + 0x10) & 255; page++; }
            y = ((page & 255) << 8) | low;
            return 3 + 2 + 2 + 2 + 4 + 5 + 2 + (wrap ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }'''
assert old in s
s = s.replace(old, new)

# 2. Ground vertical: price the push path.
old = '''            int pair = ExpandedRejectedVerticalPair(X, nextY);
            int probe = ExpandedProbeSet(X, nextY, 6, out maximum, out int lastY, 3);
            int nibble = lastY & 15;'''
new = '''            int pair = ExpandedVerticalPair(X, nextY, out bool standing, out int push);
            if (standing)
            {
                int landY = nextY;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true) + 6 + integrate;
                head += 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(X, ref landY, Y, push, out maximum);
                nextY = landY;
                return head;
            }
            int probe = ExpandedProbeSet(X, nextY, 6, out maximum, out int lastY, 3);
            int nibble = lastY & 15;'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)

# 3. Air01 falling: push path.
p = root + r'\PlayerCore.Air01Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''            int pair = ExpandedRejectedVerticalPair(coordinateX, coordinateY);'''
new = '''            int pair = ExpandedVerticalPair(coordinateX, coordinateY, out bool standing, out int push);
            if (standing)
            {
                if (!falling) throw new NotSupportedException("F3D7 rising actor push");
                if (newSlot >= 0) throw new NotSupportedException("F850 multiple enqueue owner progression");
                int previousY = Y;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true);
                head += 6 + integrate + 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(coordinateX, ref coordinateY, previousY, push, out maximum);
                velocity = 0xFFAB;
                landed = true;
                return head;
            }'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
