using System.IO;
using System.Text;

/// <summary>
/// Behavioral probe for the bank-6 enemy port (EnemyBehaviors/EnemyDispatcher):
/// builds the GameData stack like PhysCompare, spawns specific types from the
/// bank-4 object type tables into slot 5, runs 120 dispatcher frames and prints
/// the slot's X/Y/state/anim trajectory for sanity-checking against the
/// documented motion.
/// Run: unity run_script --entry EnemyProbe.Run
/// </summary>
public static class EnemyProbe
{
    private static string _root = "D:/Art/SW Code Zero Unity";

    private static (Darkwing.Core.DarkwingGame game, Darkwing.Core.EnemyDispatcher disp) Build()
    {
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank6 = File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank6.bytes");
        game.ObjectTypeTable = File.ReadAllBytes(_root + "/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes");
        var disp = game.Enemies;
        disp.Tiles = null; // probe runs tile-free: wall/floor probes return 0 (open air)
        // Anchor the player far away so aggro/touch paths stay idle.
        game.Player.X = 0x30;
        game.Player.Y = 0x0140;
        game.Player.State = 0;
        game.Player.ScrollX = 0;
        game.Player.ScrollYRow = 3;
        game.Objects.Slots[0].AnimNumber = 0x01;
        return (game, disp);
    }

    private static Darkwing.Core.ObjectSystem.Slot SpawnType(Darkwing.Core.DarkwingGame game, int type, ushort x, ushort y, int slot = 5)
    {
        var s = game.Objects.Slots[slot];
        s.Active = true;
        s.X = x;
        s.Y = y;
        s.PlacementIndex = 0xFF;
        s.StateLow = 0;
        s.Direction = 0;
        Darkwing.Core.EnemyBehaviors.TryGetType(type, out byte flags, out byte behavior,
                                                out byte anim, out byte param, out byte hp);
        s.Flags = flags;
        s.Behavior = behavior;
        s.AnimNumber = anim;
        s.BehaviorParam = param;
        s.Health = hp;
        return s;
    }

    private static string RunCase(string name, int type, ushort x, ushort y, int frames, int slot = 5)
    {
        var (game, disp) = Build();
        var s = SpawnType(game, type, x, y, slot);
        var sb = new StringBuilder();
        sb.Append($"[{name}] type={type} behavior=0x{s.Behavior:X2} hp={s.Health} start=({s.X},{s.Y}): ");
        for (int f = 1; f <= frames; f++)
        {
            disp.RunFrame();
            game.Objects.EndFrame();
            if (f % 20 == 0 || f == 1)
                sb.Append($"f{f} X={s.X} Y={s.Y} st={s.StateLow & 0xF} anim={s.AnimNumber:X2} t480={s.T480:X2}; ");
        }
        sb.Append($"END X={s.X} Y={s.Y} active={s.Active}");
        return sb.ToString();
    }

    /// <summary>All probe cases; returns the multi-line report.</summary>
    public static string Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== EnemyBehaviors probe (120 frames each, tile-free arena, player anchored at 0x30/0x140) ==");
        // Type 0 thug walker ($8C34 / behavior $0C): documented 1 px/f walk ($0540=1);
        // expected: 22 frames left, anim $43 wind-up 60 frames (still walking left),
        // then turn (dir^=3) and walk right.
        sb.AppendLine(RunCase("walker-type0", 0, 200, 0x0140, 120));
        // Type 16 weapon pickup ($8A1A / behavior $09): no touch → idle.
        sb.AppendLine(RunCase("pickup-type16", 16, 200, 0x0140, 120));
        // Behavior 1 gas cloud ($82F0): documented 4 px/f horizontal travel.
        {
            var (game, disp) = Build();
            var s = SpawnType(game, 0, 200, 0x0140);
            s.Behavior = 0x01; s.AnimNumber = 0x13; s.BehaviorParam = 0x01; s.Health = 0;
            var sb2 = new StringBuilder();
            sb2.Append("[gas-cloud-beh1] behavior=0x01 start=(200): ");
            for (int f = 1; f <= 120; f++)
            {
                disp.RunFrame();
                game.Objects.EndFrame();
                if (f % 20 == 0 || f == 1) sb2.Append($"f{f} X={s.X} st={s.StateLow & 0xF} anim={s.AnimNumber:X2}; ");
            }
            sb.AppendLine(sb2.ToString());
        }
        // Type 15 falling spike ($9ABE / behavior $1E): ceiling patrol 1 px/f with
        // direction flip every $18 frames; RNG delay drops behavior-1 chunks.
        sb.AppendLine(RunCase("spike-type15", 15, 200, 0x0080, 120));
        // Type 9 hopper fish ($9424 / behavior $17): RNG idle delay (1E/3C/5A/1E).
        sb.AppendLine(RunCase("hopper-type9", 9, 200, 0x0140, 120));
        // Boss smoke test: behavior $4F defeat pop rises 3.27 px/f.
        {
            var (game, disp) = Build();
            var s = game.Objects.Slots[6];
            s.Active = true; s.X = 0x100; s.Y = 0x0180; s.Behavior = 0x4F; s.Flags = 0xD0;
            var sb2 = new StringBuilder();
            sb2.Append("[boss-4F-defeat-pop] behavior=0x4F start=(0x100,0x180): ");
            for (int f = 1; f <= 60; f++)
            {
                disp.RunFrame();
                game.Objects.EndFrame();
                if (f % 10 == 0 || f == 1) sb2.Append($"f{f} Y={s.Y} st={s.StateLow & 0xF}; ");
            }
            sb.AppendLine(sb2.ToString());
        }
        return sb.ToString();
    }
}
