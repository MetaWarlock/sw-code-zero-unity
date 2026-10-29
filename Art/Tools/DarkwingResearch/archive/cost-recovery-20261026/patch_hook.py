root = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core'
p = root + r'\PlayerCore.Air01Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''            if (falling)
            {
                cycles += 6 + integrate + 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, true);
                cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, false) + 6 + probe;
                cycles += 2 + 3 + 2 + ScrollBranch(0xF32E, 0xF34B, maximum < 1);
                if (maximum == 0) return cycles + 6;
                cycles += ScrollBranch(0xF330, 0xF33A, maximum != 1);
                if (maximum == 1)
                {
                    int nibble = lastY & 15;
                    cycles += 3 + 2 + 2 + ScrollBranch(0xF338, 0xF360, nibble >= 8);
                    if (nibble >= 8) throw new NotSupportedException("F360 air pass-through hook dance");
                }
                cycles += 2 + ScrollBranch(0xF33C, 0xF344, false) + 3 + 2 + ScrollBranch(0xF342, 0xF360, maximum == 2);
                if (maximum == 2) throw new NotSupportedException("F360 air class2 hook dance");'''
new = '''            if (falling)
            {
                cycles += 6 + integrate + 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, true);
                if (_hookLatch != 0)
                {
                    // $F324 BNE $F366: a latched fall skips the $06 probe and goes straight to the dance.
                    cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, true);
                    return cycles + HookDanceCycles(coordinateX, ref coordinateY, velocity, ref state, audioQueueAccepts,
                        out maximum, out landed, out sound);
                }
                cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, false) + 6 + probe;
                cycles += 2 + 3 + 2 + ScrollBranch(0xF32E, 0xF34B, maximum < 1);
                if (maximum == 0) return cycles + 6;
                cycles += ScrollBranch(0xF330, 0xF33A, maximum != 1);
                bool dance = false;
                if (maximum == 1)
                {
                    int nibble = lastY & 15;
                    cycles += 3 + 2 + 2 + ScrollBranch(0xF338, 0xF360, nibble >= 8);
                    dance = nibble >= 8;
                }
                if (!dance)
                {
                    cycles += 2 + ScrollBranch(0xF33C, 0xF344, false) + 3 + 2 + ScrollBranch(0xF342, 0xF360, maximum == 2);
                    dance = maximum == 2;
                }
                if (dance)
                {
                    // $F360: CPX #1 / BCS not taken for the player, INC $5C, then $F366.
                    cycles += 2 + 2 + 5;
                    return cycles + HookDanceCycles(coordinateX, ref coordinateY, velocity, ref state, audioQueueAccepts,
                        out maximum, out landed, out sound);
                }'''
assert old in s
s = s.replace(old, new)
old = '''        private int Air01Horizontal(ref int coordinateX, int coordinateY, int direction)'''
new = '''        /// <summary>$F366…$F3C1 for the player: probe the head band ($3A…$3C). A class 1/2 there
        /// catches the hook ($50=$0D, SFX $12) unless already hanging; an empty band re-probes the
        /// feet ($06): a class-1 floor lands only at fall speed $FA or faster than $F9, ≥3 lands.</summary>
        private int HookDanceCycles(int coordinateX, ref int coordinateY, int velocity, ref int state, bool? audioQueueAccepts,
            out int maximum, out bool landed, out bool sound)
        {
            landed = sound = false;
            int cycles = 2 + 2 + 2 + 3 + 2 + 3 + 6;
            cycles += ExpandedProbeSet(coordinateX, coordinateY, 0x3A, out int head, out _, 5) + 2 + 3 + 2;
            if (head >= 1)
            {
                cycles += 2 + 2;
                if (head != 1)
                {
                    cycles += 2 + 2;
                    if (head != 2) { maximum = head; return cycles + 2 + 3 + 6; }
                }
                cycles += 3 + 3 + 2;
                maximum = head;
                if (state == 0x0D)
                {
                    landed = true;
                    return cycles + 3 + 6 + 20 + 2 + 6;
                }
                if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                state = 0x0D;
                sound = true;
                landed = head == 2;
                return cycles + 2 + 2 + 3 + 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 2 + 3 + 3 + 2
                    + (head != 2 ? 3 : 2) + 6 + 20 + 6;
            }
            cycles += 3 + 2 + 3 + 2 + 3 + 6;
            cycles += ExpandedProbeSet(coordinateX, coordinateY, 6, out maximum, out int lastY, 5) + 2 + 3 + 2;
            if (maximum == 0) return cycles + 3 + 6;
            cycles += 2 + 2;
            if (maximum == 1)
            {
                cycles += 3 + 4 + 2;
                int high = velocity >> 8;
                if (high != 0xFA)
                {
                    cycles += 2 + 2;
                    if (high >= 0xF9) return cycles + 2 + 2 + 6;
                    cycles += 3;
                }
                else cycles += 3;
                cycles += 2 + 3 + 3;
            }
            else
            {
                cycles += 2 + 2;
                if (maximum == 2) return cycles + 3 + 6;
                cycles += 2 + 2 + 3 + 3;
            }
            landed = true;
            return cycles + 6 + PlayerSnapUp(ref coordinateY, lastY & 15) + 6 + 20 + 2 + 6;
        }

        private int Air01Horizontal(ref int coordinateX, int coordinateY, int direction)'''
assert old in s
s = s.replace(old, new, 1)
s = s.replace('''            if (_hookLatch != 0) throw new NotSupportedException("F324 hook-latched fall");\n''', '')
open(p, 'w', encoding='utf-8').write(s)

# Probe point tables: ROM $ED19 (X) / $ECAD (Y), 64 entries.
xs = "12,12,12,-12,-12,-12,-11,0,11,-11,0,11,2,2,2,-2,-2,-2,-1,1,1,-1,1,1,12,12,12,-12,-12,-12,-1,0,1,-1,0,1,9,9,-9,-9,-8,8,-8,8,20,20,-20,-20,-1,1,-1,1,4,4,4,-4,-4,-4,-1,0,1,-1,0,1"
ys = "-11,0,11,-11,0,11,12,12,12,-12,-12,-12,-1,1,1,-1,1,1,2,2,2,-2,-2,-2,-1,0,1,-1,0,1,2,2,2,-2,-2,-2,-7,7,-7,7,8,8,-8,-8,-4,4,-4,4,2,2,-2,-2,-3,0,3,-3,0,3,-20,-20,-20,-3,-3,-3"
p = root + r'\PlayerCore.DispatchTiming.cs'
s = open(p, encoding='utf-8').read()
old = '''        private static readonly sbyte[] TimingPointX = {12,12,12,-12,-12,-12,-11,0,11,-11,0,11};
        private static readonly sbyte[] TimingPointY = {-11,0,11,-11,0,11,12,12,12,-12,-12,-12};'''
new = '''        // $EBE8 point offsets: X at $ED19+point, Y at $ECAD+point (64 entries in the fixed bank).
        private static readonly sbyte[] TimingPointX = {''' + xs + '''};
        private static readonly sbyte[] TimingPointY = {''' + ys + '''};'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
p = root + r'\ActorProbeTiming.cs'
s = open(p, encoding='utf-8').read()
old = s[s.index('        private static readonly sbyte[] PointX'):s.index('        public int LastPointY')]
new = '''        // $EBE8 point offsets: X at $ED19+point, Y at $ECAD+point (64 entries in the fixed bank).
        private static readonly sbyte[] PointX = { ''' + xs + ''' };
        private static readonly sbyte[] PointY = { ''' + ys + ''' };
'''
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
