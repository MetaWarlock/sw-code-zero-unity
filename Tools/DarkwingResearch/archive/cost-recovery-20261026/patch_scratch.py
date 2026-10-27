root = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core'
p = root + r'\PlayerCore.cs'
s = open(p, encoding='utf-8').read()
old = '''        private byte _push00;
        private byte _push01;'''
new = '''        private byte _push00;
        private byte _push01;
        // $02/$03: $F2DB/$F2E0 park the pre-move Y there for $F49C, but $F7A8 reuses $02 for the
        // touch class and $F7CF $03 for |dY|, so a stand-on-actor hit compares against those.
        private byte _scratch02;
        private byte _scratch03;'''
assert old in s
s = s.replace(old, new)
old = '''            byte cls = (byte)(o.Slots[y].BehaviorParam & 0x1F);         // $F7AB AND #$1F
            PushDx(y);
            if (_push01 != 0) return false;                             // $F7B4 BNE $F7DC
            if (_push00 >= EnemyBehaviors.TouchXHalf[cls]) return false; // $F7BA BCS $F7DC
            byte dy = PushDy(y, out _);                                 // $F7C1
            if (dy > EnemyBehaviors.TouchYHalf[cls]) return false;      // $F7CB
            push = (byte)(EnemyBehaviors.TouchYHalf[cls] - dy);         // $F7D3 LDA $FBE9,Y; SEC; SBC $03'''
new = '''            byte cls = (byte)(o.Slots[y].BehaviorParam & 0x1F);         // $F7AB AND #$1F
            _scratch02 = cls;                                           // $F7AD STA $02
            PushDx(y);
            if (_push01 != 0) return false;                             // $F7B4 BNE $F7DC
            if (_push00 >= EnemyBehaviors.TouchXHalf[cls]) return false; // $F7BA BCS $F7DC
            byte dy = PushDy(y, out _);                                 // $F7C1
            if (dy > EnemyBehaviors.TouchYHalf[cls]) return false;      // $F7CB
            _scratch03 = dy;                                            // $F7CF STA $03
            push = (byte)(EnemyBehaviors.TouchYHalf[cls] - dy);         // $F7D3 LDA $FBE9,Y; SEC; SBC $03'''
assert old in s
s = s.replace(old, new)
old = '''            if (falling)
            {
                ushort previousY = Y;                                // $F2DB-$F2E3
                IntegrateY();               // $F2ED JSR $F5A4 (down)
                if (PushPairOrScan(out byte supportLift))             // $F2F4/$F2F7
                {
                    LiftByObject(supportLift);                       // $F2F9-$F316
                    bool movedUp = Y < previousY;                    // $F49C-$F4A7'''
new = '''            if (falling)
            {
                _scratch02 = (byte)Y; _scratch03 = (byte)(Y >> 8);   // $F2DB-$F2E3
                IntegrateY();               // $F2ED JSR $F5A4 (down)
                if (PushPairOrScan(out byte supportLift))             // $F2F4/$F2F7
                {
                    LiftByObject(supportLift);                       // $F2F9-$F316
                    bool movedUp = Y < (_scratch03 << 8 | _scratch02); // $F49C-$F4A7 vs $02/$03'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)

p = root + r'\PlayerCore.ReachedDispatchTiming.cs'
s = open(p, encoding='utf-8').read()
old = '''        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push)
        {
            hit = false;
            push = 0;'''
new = '''        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push) => ExpandedVerticalPair(x, y, out hit, out push, out _);

        /// <summary>As above; <paramref name="scratch"/> is $02/$03 after a scan hit (class | |dY|·256).</summary>
        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push, out int scratch)
        {
            hit = false;
            push = 0;
            scratch = -1;'''
assert old in s
s = s.replace(old, new)
old = '''                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out hit, out push) + 3;
                    if (hit) return cycles + 2 + 3 + 2 + 6;              // BCC not taken, JMP $F7DE: SEC / RTS'''
new = '''                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out hit, out push) + 3;
                    if (hit)
                    {
                        int touchClass = state.BehaviorParam & 31;
                        scratch = touchClass | (EnemyBehaviors.TouchYHalf[touchClass] - push) << 8;
                        return cycles + 2 + 3 + 2 + 6;                   // BCC not taken, JMP $F7DE: SEC / RTS
                    }'''
assert old in s
s = s.replace(old, new)
# page cross of BCS $F301 from $F2FD
old = '''            int cycles = 2 + 3;
            if (push > 4) { cycles += 2 + 3; push = 4; }
            else cycles += 3;'''
new = '''            int cycles = 2 + 3;
            if (push > 4) { cycles += 2 + 3; push = 4; }
            else cycles += 4;                                              // BCS $F301 crosses $F2/$F3'''
assert old in s
s = s.replace(old, new)
old = '''            int pair = ExpandedVerticalPair(X, nextY, out bool standing, out int push);
            if (standing)
            {
                int landY = nextY;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true) + 6 + integrate;
                head += 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(X, ref landY, Y, push, out maximum);'''
new = '''            int pair = ExpandedVerticalPair(X, nextY, out bool standing, out int push, out int scratch);
            if (standing)
            {
                int landY = nextY;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true) + 6 + integrate;
                head += 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(X, ref landY, scratch, push, out maximum);'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)

p = root + r'\PlayerCore.Air01Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''            int pair = ExpandedVerticalPair(coordinateX, coordinateY, out bool standing, out int push);'''
new = '''            int pair = ExpandedVerticalPair(coordinateX, coordinateY, out bool standing, out int push, out int scratch);'''
assert old in s
s = s.replace(old, new)
old = '''                int previousY = Y;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true);'''
new = '''                int previousY = scratch;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true);'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
