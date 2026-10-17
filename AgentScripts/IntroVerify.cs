using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// Frame-exact check of the three bank-2 story panels ($C66B $8F00, $C684 $9100, $C69D $9000)
/// and of $C6B6 $A000 against the press-free capture gold_intro_panels.
///
/// Each panel is seeded by the port and then stepped with pad = $00; the golden is read at the
/// frame the panel's BODY starts (segment start + the 45-frame $C95C/$DD57 loader phase measured
/// in shift 63), so the comparison is "same inputs, same fields", not "same absolute frame zero".
/// $9100 and $9000 open with a $FA/$FB scroll-settle stage that needs $F7E0 (GAPS G66), so they
/// enter at their sprite seeds — the harness therefore checks the seeds AND the resident loop.
///
/// Entry: IntroVerify.Run
/// </summary>
public static class IntroVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string RefDir = Root + "/Research/DarkwingDuck/reference/gold_intro_panels";

    // $31 segments + per-panel windows measured in shift 63 (evidence/intro_panels_shift63.txt):
    // $09 f688, $0E f1009, $0C f1202, $0A f1437 — each body starts after its loader phase, and
    // panel 3 seeds only at f1374 because $9000 spends f1247..f1373 in its $FA/$FB settle stage.
    private static readonly (string name, int body, int frames)[] Panels =
    {
        ("$8F00", 733, 276), ("$9100", 1054, 148), ("$9000", 1374, 63),
    };

    public static string Run()
    {
        var sb = new StringBuilder();
        try { Verify(sb); }
        catch (System.Exception e) { sb.AppendLine("EXCEPTION: " + e); }
        File.WriteAllText(Root + "/Temp/intro_verify.txt", sb.ToString());
        return sb.ToString();
    }

    private static DarkwingGame Boot(byte[] ram)
    {
        var data = DataLoader.Load(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new DarkwingGame(data);
        game.Bank2 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank2.bytes");
        game.Bank5 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/world_bank7.bytes");
        return game;
    }

    private static void Verify(StringBuilder sb)
    {
        byte[] ram = File.ReadAllBytes(RefDir + "/ram_frames.bin");
        int avail = ram.Length / 2048;

        var game = Boot(ram);
        bool first = true;
        foreach (var (name, body, frames) in Panels)
        {
            int hi = System.Math.Min(frames, avail - body);
            // One continuous game: $C65F/$C678/$C691 each open with JSR $C95C, and $C235 writes
            // only $0400/$04D0/$0590 — so $0420/$0430/$0470/$04F0/$0510 carry across panels and
            // panel 3's slot-2 anim $B5 + slot-1 subpixel accumulator are real residues, not
            // values the harness may re-seed.
            if (!first) game.ClearObjectRamAndScroll();
            first = false;
            switch (name)
            {
                case "$8F00": game.IntroPanel8F00Seed(); break;
                case "$9100": game.IntroPanel9100Seed(); break;
                default: game.IntroPanel9000Seed(); break;
            }

            int match = 0, firstBad = -1; string why = "";
            var trace = new List<string>(hi);
            for (int i = 0; i < hi; i++)
            {
                // The panel loop is $8F5F LDY #4 / $8F61 STY $97 / $8F63 JSR $F89A / $8F6C JSR
                // $F51B / $8F71 JSR $F4FD / $8F74 gate — it has NO $FF06 of its own, so the
                // frame boundary sits inside $F89A and the capture samples the RAM between the
                // walk and the integrations: golden(i) = port post-$F89A(i) + pre-$F51B(i).
                // $F89A writes only $0410/$0430 (and OAM), $F51B/$F4FD only $04F0/$0500, and the
                // $9090 flag flip follows $F51B — so positions/flags are compared from the
                // pre-step state and the anim pair from the post-step state.
                int g = (body + i) * 2048;
                var pre = new (ushort x, ushort y, byte flags, byte anim)[3];
                for (int s = 1; s <= 2; s++)
                {
                    var slot = game.Objects.Slots[s];
                    pre[s] = (slot.X, slot.Y, slot.Flags, slot.AnimNumber);
                }

                bool alive = name == "$8F00" ? game.IntroPanel8F00Step(0)
                           : name == "$9100" ? game.IntroPanel9100Step(0)
                                             : game.IntroPanel9000Step(0);

                bool ok = true;
                for (int s = 1; s <= 2 && ok; s++)
                {
                    var slot = game.Objects.Slots[s];
                    ok = pre[s].flags == ram[g + 0x400 + s]
                         && pre[s].anim == ram[g + 0x420 + s]
                         && pre[s].x == (ushort)(ram[g + 0x4F0 + s] | (ram[g + 0x500 + s] << 8))
                         && pre[s].y == (ushort)(ram[g + 0x510 + s] | (ram[g + 0x520 + s] << 8))
                         && slot.AnimFrame == ram[g + 0x410 + s]
                         && slot.AnimTimer == ram[g + 0x430 + s];
                    if (!ok && firstBad < 0)
                        why = $"slot{s}: port t${slot.AnimTimer:X2}/f${slot.AnimFrame:X2}"
                            + $" anim ${pre[s].anim:X2}/fl${pre[s].flags:X2}"
                            + $" X ${pre[s].x:X4} Y ${pre[s].y:X4} vs gold t${ram[g + 0x430 + s]:X2}"
                            + $"/f${ram[g + 0x410 + s]:X2} anim ${ram[g + 0x420 + s]:X2}"
                            + $"/fl${ram[g + 0x400 + s]:X2}"
                            + $" X ${ram[g + 0x500 + s]:X2}{ram[g + 0x4F0 + s]:X2}"
                            + $" Y ${ram[g + 0x520 + s]:X2}{ram[g + 0x510 + s]:X2}";
                }
                if (ok) match++;
                else if (firstBad < 0) firstBad = i;

                // Per-frame trace (kept for the window around the FIRST divergence): the anim
                // pair from the post-step state, positions from the pre-step state, plus the
                // scroll/settle fields that decide which stage of $9100 runs.
                trace.Add($"  {name} f{body + i}: port1 t${game.Objects.Slots[1].AnimTimer:X2}"
                    + $" f${game.Objects.Slots[1].AnimFrame:X2} a${pre[1].anim:X2} fl${pre[1].flags:X2}"
                    + $" X${pre[1].x:X4} Y${pre[1].y:X4} D${(game.Objects.Slots[1].Active ? 0x80 : 0) | game.Objects.Slots[1].StateLow:X2}"
                    + $" | gold1 t${ram[g + 0x431]:X2} f${ram[g + 0x411]:X2} a${ram[g + 0x421]:X2}"
                    + $" fl${ram[g + 0x401]:X2} X${ram[g + 0x501]:X2}{ram[g + 0x4F1]:X2}"
                    + $" Y${ram[g + 0x511]:X2} D${ram[g + 0x4D1]:X2}"
                    + $" || port2 t${game.Objects.Slots[2].AnimTimer:X2} f${game.Objects.Slots[2].AnimFrame:X2}"
                    + $" a${pre[2].anim:X2} X${pre[2].x:X4} | gold2 t${ram[g + 0x432]:X2}"
                    + $" f${ram[g + 0x412]:X2} a${ram[g + 0x422]:X2} X${ram[g + 0x502]:X2}{ram[g + 0x4F2]:X2}"
                    + $" || port FA${game.Player.ScrollY:X2} FB${game.Player.ScrollYRow:X2}"
                    + $" 10=${game.Player.ScrollStep:X2} 2F=${game.Player.ScrollMode:X2}"
                    + $" | gold FA${ram[g + 0xFA]:X2} FB${ram[g + 0xFB]:X2} 10=${ram[g + 0x10]:X2}"
                    + $" 2F=${ram[g + 0x2F]:X2} 23=${ram[g + 0x23]:X2} 51=${ram[g + 0x51]:X2}"
                    + $" || port s0 Y${game.Objects.Slots[0].Y:X4} 0460${game.Objects.Slots[0].YFrac:X2}"
                    + $" velI{game.Objects.Slots[0].YVelInt:X2} | gold s0 Y${ram[g + 0x520]:X2}{ram[g + 0x510]:X2}"
                    + $" 0460${ram[g + 0x460]:X2}");

                if (!alive) { sb.AppendLine($"  {name}: body RTS-ed at port step {i} (golden frame {body + i})"); break; }
            }
            sb.AppendLine($"{name} window f{body}..f{body + hi - 1}: MATCH {match}/{hi}"
                + (firstBad >= 0 ? $"  first divergence f{body + firstBad}: {why}" : ""));
            if (firstBad >= 0)
            {
                for (int i = 0; i < System.Math.Min(hi, 8); i++) sb.AppendLine(trace[i]);
                int lo = System.Math.Max(0, firstBad - 4), n = System.Math.Min(hi, firstBad + 5);
                for (int i = lo; i < n; i++) sb.AppendLine(trace[i]);
            }
        }

        // $A000 is a seed-only routine (no wait loop): compare its writes against the $0A segment.
        {
            var game4 = Boot(ram);
            game4.IntroEntryA000();
            int g = 1482 * 2048;                     // $A000's seeds land at f1482 (loader f1437..f1481)
            var s1 = game4.Objects.Slots[1];
            sb.AppendLine($"$A000 at f1482: port slot1 anim ${s1.AnimNumber:X2} flags ${s1.Flags:X2}"
                + $" X ${s1.X:X4} Y ${s1.Y:X4} velI {s1.XVelInt:X2} velF {s1.XVelFrac:X2}"
                + $" | gold anim ${ram[g + 0x421]:X2} flags ${ram[g + 0x401]:X2}"
                + $" X ${ram[g + 0x501]:X2}{ram[g + 0x4F1]:X2} Y ${ram[g + 0x521]:X2}{ram[g + 0x511]:X2}"
                + $" $0531=${ram[g + 0x531]:X2} $0541=${ram[g + 0x541]:X2}");
        }
    }
}
