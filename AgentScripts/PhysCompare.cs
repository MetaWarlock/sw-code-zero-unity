using System.IO;
using UnityEngine;

public static class PhysCompare
{
    /// <summary>
    /// Golden physics comparison through the full DarkwingGame stack (movement +
    /// anim advance + slot sync), replaying the captured original input schedule.
    /// </summary>
    private static string Compare(string name, int frames, System.Func<int, byte> input,
        int seedFrame = 0, bool camera = false)
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + $"/Research/DarkwingDuck/reference/gold_{name}/ram_frames.bin");
        int avail = ramAll.Length / 2048;

        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld(0);
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, world0);
        map.StreamColumns(0);
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();

        // Seed from the golden frame `seedFrame`.
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seedFrame * 2048, ram0, 0, 2048);
        var p = game.Player;
        p.X = (ushort)(ram0[0x4F0] | (ram0[0x500] << 8));
        p.Y = (ushort)(ram0[0x510] | (ram0[0x520] << 8));
        p.YVelFrac = ram0[0x550];
        p.YVelInt = (sbyte)ram0[0x560];
        p.YFrac = ram0[0x460];
        p.XFrac = ram0[0x470];
        p.XVelFrac = ram0[0x530];
        p.XVelInt = ram0[0x540];
        p.State = ram0[0x50];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        // Camera/level-table state the hook mechanic reads ($FA, $22, $2E, $2D).
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        var s0 = game.Objects.Slots[0];
        s0.AnimNumber = ram0[0x420];
        s0.AnimFrame = ram0[0x410];
        s0.AnimTimer = ram0[0x430];

        var sb = new System.Text.StringBuilder();
        sb.Append($"[player_mechanics] {name}: ");
        int firstDiv = -1;
        int matchFrames = 0;
        for (int f = seedFrame + 1; f < avail; f++)
        {
            var pad = Darkwing.Core.NesButtons.FromByte(input(f));
            game.Step(pad);
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int gx = g[0x4F0] | (g[0x500] << 8);
            int gy = g[0x510] | (g[0x520] << 8);
            if (f - seedFrame <= 2 || (f >= 75 && f <= 100))
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2} 2B={p.ScrollState:X2} 10={p.ScrollStep:X2} 2F={p.ScrollMode:X2} vy={p.YVelInt},{p.YVelFrac:X2} ffc={p.YFrac:X2} hl={p.HitLadder} D={p.DeathFlag} | O({gx:X4},{gy:X4}) st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2} 2B={g[0x2B]:X2} 10={g[0x10]:X2} 2F={g[0x2F]:X2} vy={(sbyte)g[0x560]},{g[0x550]:X2} ffc={g[0x460]:X2} af={s0.AnimFrame}/{g[0x410]} an={s0.AnimNumber:X2}/{g[0x420]:X2}] ");
            bool pos = p.X == gx && p.Y == gy;
            bool cam = !camera || (p.State == g[0x50] && p.ScrollY == g[0xFA] && p.ScrollYRow == g[0xFB]
                                   && p.CellIndex == g[0x22] && p.CellIndexNext == g[0x23] && p.ScrollState == g[0x2B]);
            if (pos && cam) matchFrames++;
            else
            {
                if (firstDiv < 0) firstDiv = f;
                if (f - firstDiv < 6 || f % 25 == 0)
                    sb.Append($"f{f}: port=({p.X:X4},{p.Y:X4} st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}) " +
                              $"orig=({gx:X4},{gy:X4} st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2}) ");
            }
        }
        int span = avail - 1 - seedFrame;
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {matchFrames}/{span} frames (seed {seedFrame})"
            : $"RESULT: first divergence frame {firstDiv} (matched {matchFrames}/{span}, seed {seedFrame})");
        return sb.ToString();
    }

    public static string Walk() => Compare("walk", 120,
        f => (f >= 10 && f <= 70) ? (byte)0x01 : (byte)0x00);

    public static string Jump() => Compare("jump", 120, f =>
    {
        byte b = 0;
        if (f >= 10 && f <= 40) b |= 0x01;
        if (f >= 20 && f <= 22) b |= 0x80;
        return b;
    });

    /// <summary>
    /// Hook ride golden: `gold_hook_ride2` is mission 1 (world 0) with the original
    /// seeded by probe_run --patch into cell $22=$1C (row 1, col 11, script byte $41 =
    /// hook column) at the row bottom; the grab fires next frame and the camera rides
    /// down one 240-px row ($FA 3 px/frame), then $D7F9 releases the player ($50=1).
    /// Frame 0 of the capture is the patched seed frame, so the port starts at frame 1
    /// (first ridden frame) and every later frame is compared, camera state included.
    /// </summary>
    public static string Hook() => Compare("hook_ride2", 300, f => 0, seedFrame: 1, camera: true);

    /// <summary>
    /// Door/section golden: `gold_door_id1` is mission 1 (world 0) with the request byte
    /// $5B seeded to 1 by probe_run --patch at capture frame 5. The original consumes it at
    /// $CA6E (LDX $5B / JSR $E388) 18 frames later; capture frame 23 = array index 22 is the
    /// first frame where the geometry writes ($E394-$E3D3) are visible while $DE24 has
    /// already zeroed $50, and $E406 ($50=$12, $0420=$2C from $FA78) only appears at
    /// capture frame 33 — $E388 yields per row-redraw batch ($FF06/$E698), it is NOT
    /// one frame. This harness seeds the port at index 21 (last pre-load frame) with
    /// PendingEvent=1 and compares, per field, every RAM byte $E388 is documented to write.
    /// </summary>
    public static string Door()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/gold_door_id1/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            game.Bank7,
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, decoder.GetWorld(0));
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();

        const int seed = 21;             // capture frame 22: last frame before the load
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seed * 2048, ram0, 0, 2048);
        var p = game.Player;
        p.X = (ushort)(ram0[0x4F0] | (ram0[0x500] << 8));
        p.Y = (ushort)(ram0[0x510] | (ram0[0x520] << 8));
        p.YVelFrac = ram0[0x550];
        p.YVelInt = (sbyte)ram0[0x560];
        p.YFrac = ram0[0x460];
        p.XFrac = ram0[0x470];
        p.XVelFrac = ram0[0x530];
        p.XVelInt = ram0[0x540];
        p.State = ram0[0x50];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        p.PendingEvent = ram0[0x5B];      // $5B door request, consumed by $CA6E in Step
        map.LoadRow(p.ScrollYRow);
        map.StreamColumns(p.ScrollX);

        var names = new[] { "$22 cell", "$23 next", "$FB row", "$FD colhi", "$FC collo",
                            "$FA scrollY", "$50 state", "$36 tileset", "$1A chr0", "$1B chr1",
                            "$0500/$04F0 X", "$0520/$0510 Y", "$0400 flags", "$0420 anim" };
        var ok = new int[names.Length];
        var firstBad = new int[names.Length];
        for (int i = 0; i < names.Length; i++) firstBad[i] = -1;
        var sb = new System.Text.StringBuilder("[door_id1] ");
        int span = avail - 1 - seed;
        for (int f = seed + 1; f < avail; f++)
        {
            game.Step(Darkwing.Core.NesButtons.FromByte(0));
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int[] port = { p.CellIndex, p.CellIndexNext, p.ScrollYRow, p.ScrollX >> 8, p.ScrollX & 0xFF,
                           p.ScrollY, p.State, p.TilesetBank, p.ChrLatch0, p.ChrLatch1,
                           p.X, p.Y, p.Flags, game.Objects.Slots[0].AnimNumber };
            int[] orig = { g[0x22], g[0x23], g[0xFB], g[0xFD], g[0xFC],
                           g[0xFA], g[0x50], g[0x36], g[0x1A], g[0x1B],
                           g[0x4F0] | (g[0x500] << 8), g[0x510] | (g[0x520] << 8),
                           g[0x400], g[0x420] };
            for (int i = 0; i < names.Length; i++)
            {
                if (port[i] == orig[i]) { ok[i]++; continue; }
                if (firstBad[i] < 0)
                {
                    firstBad[i] = f;
                    sb.Append($"FIRST {names[i]}: f{f} port={port[i]:X4} orig={orig[i]:X4}; ");
                }
            }
        }
        for (int i = 0; i < names.Length; i++)
            sb.Append($"{names[i]}={ok[i]}/{span} (first bad {(firstBad[i] < 0 ? "-" : firstBad[i].ToString())}) ");
        return sb.ToString();
    }
}
