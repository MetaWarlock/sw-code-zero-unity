using System.IO;
using System.Linq;
using UnityEngine;

public static class PhysCompare
{
    public static string DeathSoundContract()
    {
        int checks = 0;
        foreach (byte gate in new byte[] { 0, 0x7F, 0x80, 0xFF })
            foreach (byte mission in new byte[] { 0, 0x80 })
            {
                var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance);
                int sounds = 0;
                player.Sfx = id => { if (id != 0x25) throw new System.Exception("Wrong death sound"); sounds++; };
                player.DeathSoundGate = gate;
                player.PendingMissionFlag = mission;
                player.EnergyColLeft = 0x73; player.EnergyColRight = 0x71;
                player.DeathService();
                player.DeathService();
                if (sounds != (gate < 0x80 ? 2 : 0) || player.DeathSoundGate != gate)
                    throw new System.Exception("CB01 gate/repeated service/mission sign mismatch");
                checks++;
            }
        var actor = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance);
        actor.Objects = new Darkwing.Core.ObjectSystem(null);
        var slot = actor.Objects.Slots[0];
        slot.Active = true; slot.StateLow = 0x81; slot.T480 = 0x1F;
        actor.State = 7;
        int calls = 0;
        actor.Sfx = id => { if (id != 0x25) throw new System.Exception("Unexpected death event"); calls++; };
        actor.Step(0, 0);
        if (slot.T480 != 0x1E || actor.DeathSoundGate != 0xFF || calls != 1)
            throw new System.Exception("D3B6 did not latch the death sound gate");
        checks++;
        actor.DeathService();
        if (calls != 1) throw new System.Exception("Normal death queued sound twice");
        checks++;
        actor.ApplyWorldPrelude(new Darkwing.Core.RoomDecoder.World { RowWidth = 1, Grid = new byte[256], Flags = new byte[256] });
        if (actor.DeathSoundGate != 0) throw new System.Exception("DE60 did not clear death gate");
        checks++;
        if (new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance).DeathSoundGate != 0)
            throw new System.Exception("Power-on gate is not clear");
        checks++;
        return $"PASS {checks}/{checks} CB01 death SFX gate, repeated service, mission signs, D3B6 latch and DE60 reset";
    }

    public static string PauseActivityContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(Read("chr_full"), Read("levelsets"), Read("screens"), Read("levels"), Read("anims"));
        var game = new Darkwing.Core.DarkwingGame(data) { Bank4 = Read("prg_bank4"), Bank7 = Read("world_bank7") };
        var expected = new byte[16];
        for (int index = 0; index < 16; index++)
        {
            var slot = game.Objects.Slots[index];
            slot.Active = (index & 2) == 0;
            slot.StateLow = new byte[] { 0, 1, 0x83, 0x7F }[index & 3];
            slot.X = (ushort)(0x120 + index); slot.Y = (ushort)(0x340 + index);
            slot.Flags = 0xD0; slot.AnimNumber = 1; slot.AnimFrame = 2;
            expected[index] = (byte)((slot.Active ? 0x80 : 0) | (slot.StateLow & 0x7F));
        }
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Darkwing.Core.DarkwingGame).GetMethod("PauseEnter", binding).Invoke(game, null);
        if (!game.Paused) throw new System.Exception("Pause did not take ownership");
        typeof(Darkwing.Core.DarkwingGame).GetMethod("PauseRestore", binding).Invoke(game, null);
        int failures = 0;
        for (int index = 0; index < 16; index++)
        {
            var slot = game.Objects.Slots[index];
            byte actual = (byte)((slot.Active ? 0x80 : 0) | (slot.StateLow & 0x7F));
            if (actual != expected[index]) failures++;
            if (slot.X != 0x120 + index || slot.Y != 0x340 + index || slot.Flags != 0xD0
                || slot.AnimNumber != 1 || slot.AnimFrame != 2)
                throw new System.Exception("Pause restore altered saved actor fields");
        }
        return failures == 0 ? "PASS 16/16 pause activity bytes; low-only active states, inactive stale bit7 and saved fields"
            : $"FAIL {failures}/16 pause activity bytes";
    }

    public static string HorizontalPushContract()
    {
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var move = typeof(Darkwing.Core.PlayerCore).GetMethod("MoveHorizontal", binding);
        var failures = new System.Collections.Generic.List<string>();
        byte[] bank = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        int checks = 0;
        void Check(string name, bool right, ushort otherX, ushort otherY, byte parameter,
            ushort expectedX, byte state = 1, bool active = true, byte flags = 0x90,
            bool partner = false, bool earlier = false, bool? probeRight = null)
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var map = new PushProbeMap();
            ushort startX = right ? (ushort)0x1C0 : (ushort)0x1E0;
            var player = new Darkwing.Core.PlayerCore(map) { Objects = objects,
                X = startX, PrevPlayerX = startX, Y = 0x03A6, ScrollYRow = 3,
                XFrac = right ? (byte)0xF8 : (byte)8, XVelInt = 1, XVelFrac = 0x80, State = state };
            var obstacle = objects.Slots[15];
            obstacle.Active = active; obstacle.Flags = flags; obstacle.Behavior = 0x12;
            obstacle.BehaviorParam = parameter; obstacle.X = otherX; obstacle.Y = otherY;
            if (partner)
            {
                var pair = objects.Slots[1];
                pair.Active = true; pair.Flags = 0xD0; pair.Behavior = 1;
                pair.X = right ? (ushort)0x1C2 : (ushort)0x1DE; pair.Y = 0x03A6;
            }
            if (earlier)
            {
                var first = objects.Slots[2];
                first.Active = true; first.Flags = 0x90; first.BehaviorParam = 0x87;
                first.X = 0x1D1; first.Y = 0x03B0;
            }
            move.Invoke(player, new object[] { right });
            bool expectedRight = probeRight ?? (right ? expectedX >= startX : expectedX > startX);
            int expectedProbe = expectedX + (expectedRight ? 12 : -12);
            byte expectedFraction = right ? (byte)0x78 : (byte)0x88;
            if (player.X != expectedX || player.XFrac != expectedFraction || map.LastX != expectedProbe)
                failures.Add($"{name}: X{player.X:X4}/{expectedX:X4} frac{player.XFrac:X2}/{expectedFraction:X2} probe{map.LastX:X}/{expectedProbe:X}");
            checks++;
        }
        Check("source12294 right", true, 0x1D0, 0x03B0, 0x87, 0x1C0);
        Check("left", false, 0x1D0, 0x03B0, 0x87, 0x1E0);
        Check("unarmed", true, 0x1D0, 0x03B0, 7, 0x1C2);
        Check("inactive", true, 0x1D0, 0x03B0, 0x87, 0x1C2, active: false);
        Check("not visible", true, 0x1D0, 0x03B0, 0x87, 0x1C2, flags: 0x10);
        Check("state0B excluded", true, 0x1D0, 0x03B0, 0x87, 0x1C2, state: 0x0B);
        Check("dy equal rejects", true, 0x1D0, 0x03C2, 0x87, 0x1C2);
        Check("dx high rejects", true, 0x2D0, 0x03B0, 0x87, 0x1C2);
        Check("dx equal zero push", true, 0x1D2, 0x03B0, 0x87, 0x1C2);
        Check("dx outside rejects", true, 0x1D3, 0x03B0, 0x87, 0x1C2);
        Check("right cap keeps SBC borrow", true, 0x1C2, 0x03B0, 0x87, 0x1BD, probeRight: false);
        Check("cap4 left reverse probe", false, 0x1DE, 0x03B0, 0x87, 0x1E2, probeRight: true);
        Check("F642 pair falls into scan", true, 0x1D0, 0x03B0, 0x87, 0x1C0, partner: true);
        Check("pair alone is not solid", true, 0x1D0, 0x03B0, 7, 0x1C2, partner: true);
        Check("first scanned slot wins", true, 0x1C2, 0x03B0, 0x87, 0x1C1, earlier: true);
        for (byte touchClass = 0; touchClass < 32; touchClass++)
        {
            int halfX = bank[0x3C09 + touchClass], halfY = bank[0x3BE9 + touchClass];
            int overlap = halfX >= 14 && halfY > 10 ? halfX - 14 : 0;
            int push = overlap > 4 ? 5 : overlap;
            Check("class" + touchClass, true, 0x1D0, 0x03B0, (byte)(0x80 | touchClass), (ushort)(0x1C2 - push));
        }
        return failures.Count == 0 ? $"PASS {checks}/{checks} F200/F254 horizontal push; armed scan, pair fallthrough, right SBC borrow, opposite probe and source tables"
            : $"FAIL {failures.Count}/{checks}: " + string.Join("; ", failures);
    }

    public static string MissionCardEntryContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(Read("chr_full"), Read("levelsets"), Read("screens"), Read("levels"), Read("anims"));
        var game = new Darkwing.Core.DarkwingGame(data) { Bank5 = Read("prg_bank5") };
        var slot = game.Objects.Slots[0];
        slot.AnimNumber = 1;
        for (int offset = 0; offset < 256; offset += 4)
        {
            game.Oam.WriteShadowByte(offset, 104);
            game.Oam.WriteShadowByte(offset + 1, 0xA5);
        }
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Darkwing.Core.PlayerCore).GetMethod("MissionIntroCard", binding).Invoke(game.Player, null);
        int failures = 0;
        for (int offset = 0; offset < 256; offset += 4)
        {
            if (game.Oam.ReadShadowByte(offset) != 0xF8) failures++;
            if (game.Oam.ReadShadowByte(offset + 1) != 0xA5) throw new System.Exception("C21E altered tile bytes");
        }
        if (!game.Oam.MissionCardHold || slot.AnimNumber != 0x2B) throw new System.Exception("Card entry not armed");
        game.Oam.BlitMissionCard(Read("prg_bank2"), 0);
        byte[] expected = new byte[160];
        for (int offset = 0; offset < 160; offset++) expected[offset] = game.Oam.ReadShadowByte(offset);
        typeof(Darkwing.Core.PlayerCore).GetMethod("MissionIntroCard", binding).Invoke(game.Player, null);
        for (int offset = 0; offset < 160; offset++)
            if (expected[offset] != game.Oam.ReadShadowByte(offset)) throw new System.Exception("Card re-entry wiped existing text");
        return failures == 0 ? "PASS D545/C21E hides prior hero once before card reservation; preserves tile bytes and later card"
            : $"FAIL card entry retains {failures} previous visible records";
    }

    public static string ReloadWalkContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(Read("chr_full"), Read("levelsets"), Read("screens"), Read("levels"), Read("anims"));
        var game = new Darkwing.Core.DarkwingGame(data) { Bank5 = Read("prg_bank5") };
        game.Player.X = 44; game.Player.Y = 6; game.Player.Flags = 0xD0;
        var player = game.Objects.Slots[0];
        player.Active = true; player.StateLow = 0x80; player.Flags = 0xD0; player.X = 44; player.Y = 6;
        player.AnimNumber = 0x2C; player.AnimFrame = 0; player.AnimTimer = 0;
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Darkwing.Core.DarkwingGame).GetField(name, binding).SetValue(game, value);
        Set("_reloadStage", 4); Set("_fadeFrame", (byte)0); Set("_fadeLevel", (byte)0x40); Set("_fadeDelta", (byte)0xF0);
        var advance = typeof(Darkwing.Core.DarkwingGame).GetMethod("AdvanceReloadStage", binding);
        byte rng = Darkwing.Core.EnemyBehaviors.RngE4;
        advance.Invoke(game, null);
        if (game.FrameParity != 1 || player.AnimTimer != 1)
            return $"FAIL first death-reload walk parity{game.FrameParity} animTimer{player.AnimTimer}; source f11666 expects1/1";
        advance.Invoke(game, null);
        if (game.FrameParity != 1 || player.AnimTimer != 1 || game.Player.X != 44 || game.Player.Y != 6 || Darkwing.Core.EnemyBehaviors.RngE4 != rng)
            throw new System.Exception("Fade hold advanced world physics/RNG or another walk");
        return "PASS first death-reload fade performs one OAM walk; following hold preserves animation, physics and RNG";
    }

    public static string RowEdgeContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var decoder = new Darkwing.Core.RoomDecoder(Read("prg_bank0"), Read("prg_bank1"), Read("world_bank7"), Read("levelsets"));
        var world = decoder.GetWorld(0);
        int cases = 0;
        foreach (int grid in new[] { world.StartCell - 1, (int)world.StartCell, world.StartCell + 1 })
        foreach (int page in new[] { 0, 1, world.RowWidth - 1 })
        for (int row = 0; row < 30; row++)
        {
            bool right = page != world.RowWidth - 1;
            int neighbor = grid + (right ? 1 : -1);
            if (world.Grid[neighbor] == 0 && page != 0 && right) { right = false; neighbor = grid - 1; }
            var expected = new byte[1024];
            bool exists = world.Grid[neighbor] != 0;
            if (exists) decoder.Decode(world, world.Grid[neighbor], new byte[64], expected, new byte[256]);
            var ppu = new Darkwing.Core.Ppu(null);
            for (int index = 0; index < ppu.NametableRam.Length; index++) ppu.NametableRam[index] = 0xA5;
            var map = new Darkwing.Core.LevelTileMap(decoder, ppu, world);
            map.PaintRow(row, grid, page);
            int opposite = ((page & 1) ^ 1) * 1024;
            for (int column = 0; column < 32; column++)
            {
                bool edge = exists && (right ? column < 2 : column >= 30);
                byte tile = edge ? expected[row * 32 + column] : (byte)0xA5;
                if (ppu.NametableRam[opposite + row * 32 + column] != tile)
                    throw new System.Exception($"E954 edge grid{grid:X2} page{page} row{row} col{column}");
            }
            cases++;
        }
        return $"PASS {cases}/{cases} E954 neighbor-edge rows; first/interior/last page and missing-neighbor controls";
    }

    public static string WorldBandCarryContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var decoder = new Darkwing.Core.RoomDecoder(Read("prg_bank0"), Read("prg_bank1"), Read("world_bank7"), Read("levelsets"));
        var world = decoder.GetWorld(0);
        var ppu = new Darkwing.Core.Ppu(null);
        var previous = new Darkwing.Core.LevelTileMap(decoder, ppu, world);
        previous.StreamBand(0, world.StartCell);
        var field = typeof(Darkwing.Core.LevelTileMap).GetField("_bandTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var expected = (byte[])((byte[])field.GetValue(previous)).Clone();
        int empty = System.Array.FindIndex(world.Grid, value => value == 0);
        var carried = new Darkwing.Core.LevelTileMap(decoder, ppu, world, previous);
        carried.StreamBand(0, empty);
        var freshPpu = new Darkwing.Core.Ppu(null);
        new Darkwing.Core.LevelTileMap(decoder, freshPpu, world).StreamBand(0, empty);
        int negativeDifferences = 0;
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 2; column++)
        {
            int target = row * 32 + column;
            if (ppu.NametableRam[target] != expected[row * 4 + column])
                throw new System.Exception("World load erased EA36 retained macro");
            if (freshPpu.NametableRam[target] != ppu.NametableRam[target]) negativeDifferences++;
        }
        if (negativeDifferences == 0) throw new System.Exception("Carry fixture cannot reject a fresh zero cache");
        return "PASS world-to-world EA36 cache carry; zero-cache negative control rejected";
    }

    public static string YieldOamPublishContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(Read("chr_full"), Read("levelsets"), Read("screens"), Read("levels"), Read("anims"));
        var game = new Darkwing.Core.DarkwingGame(data);
        byte[] bank2 = Read("prg_bank2");
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Darkwing.Core.PlayerCore).GetField("_cardWait", binding).SetValue(game.Player, (byte)2);
        var nmi = typeof(Darkwing.Core.DarkwingGame).GetMethod("BeginNmi", binding);
        var shadowField = typeof(Darkwing.Core.OamDispatcher).GetField("_shadow", binding);
        game.Oam.BlitMissionCard(bank2, 0);
        byte[] expected = (byte[])((byte[])shadowField.GetValue(game.Oam)).Clone();
        nmi.Invoke(game, null);
        bool published = System.Linq.Enumerable.SequenceEqual(expected, game.Ppu.Oam);
        byte[] previousDma = (byte[])game.Ppu.Oam.Clone();
        game.RenderOff();
        game.Oam.BlitMissionCard(bank2, 1);
        nmi.Invoke(game, null);
        bool held = System.Linq.Enumerable.SequenceEqual(previousDma, game.Ppu.Oam);
        return published && held ? "PASS yield NMI publishes pending OAM; render-off control preserves previous DMA"
            : $"FAIL yield OAM published={published} renderOffPreserved={held}";
    }

    public static string RowBandCacheContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var decoder = new Darkwing.Core.RoomDecoder(Read("prg_bank0"), Read("prg_bank1"), Read("world_bank7"), Read("levelsets"));
        var world = decoder.GetWorld(0);
        int valid = world.StartCell;
        int empty = System.Array.FindIndex(world.Grid, value => value == 0);
        var tiles = new byte[1024];
        decoder.Decode(world, world.Grid[valid], new byte[64], tiles, new byte[256]);
        int failures = 0;
        for (int row = 0; row < 30; row++)
        {
            var ppu = new Darkwing.Core.Ppu(null);
            var map = new Darkwing.Core.LevelTileMap(decoder, ppu, world);
            map.StreamBand(7, valid);
            map.PaintRow(row, valid, world.RowWidth - 1);
            map.StreamBand(0, empty);
            for (int blockRow = 0; blockRow < 4; blockRow++)
                for (int column = 0; column < 2; column++)
                    if (ppu.NametableRam[blockRow * 32 + column] != tiles[((row >> 2) * 4 + blockRow) * 32 + 28 + column])
                        failures++;
        }
        return failures == 0 ? "PASS 30/30 row-to-empty-band cache cases; E870-E8E6 leaves macro column7 in $0140"
            : $"FAIL row-to-empty-band cache: {failures} tile mismatches";
    }

    public static string SplitWeaponContract()
    {
        var failures = new System.Collections.Generic.List<string>();
        int checks = 0;
        foreach (byte direction in new byte[] { 1, 2 })
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(new PushProbeMap()) { Objects = objects };
            var dispatcher = new Darkwing.Core.EnemyDispatcher(new byte[16384], objects, player);
            var parent = objects.Slots[2];
            parent.Active = true; parent.StateLow = 0x81; parent.Flags = 0xD0;
            parent.AnimNumber = 0x14; parent.AnimFrame = 4; parent.AnimTimer = 2;
            parent.Behavior = 4; parent.Direction = direction; parent.X = 300; parent.Y = 0x0310;
            Darkwing.Core.EnemyBehaviors.Dispatch(2, parent, 4, dispatcher);
            void Check(bool value, string label) { checks++; if (!value) failures.Add($"dir{direction} {label}"); }
            Check(!parent.Active, "parent retires");
            for (int slot = 3; slot <= 4; slot++)
            {
                var child = objects.Slots[slot];
                int childIndex = slot - 3;
                byte expectedDirection = (byte)((direction == 1 ? 5 : 6) + childIndex * 4);
                Check(child.Active && child.StateLow == 0x80 && child.Behavior == 7, "child state");
                Check(child.AnimNumber == (slot == 3 ? 0x1A : 0x19), "869C-86A1 direction/animation tables");
                Check(child.Direction == expectedDirection, "diagonal direction");
                Check(child.XVelFrac == 0x2E && child.XVelInt == 3 && child.YVelFrac == 0x2E && child.YVelInt == 3, "3.2E constant velocity");
                Darkwing.Core.EnemyBehaviors.Dispatch(slot, child, 7, dispatcher);
                Check(child.X == (direction == 1 ? 303 : 296), "horizontal travel");
                Check(child.Y == (slot == 3 ? 0x0313 : 0x030C), "vertical travel");
                Check((child.Flags & 0x40) == (direction == 1 ? 0x40 : 0), "facing");
                Check(child.YVelFrac == 0x2E && child.YVelInt == 3, "no gravity");
            }
        }
        return failures.Count == 0 ? $"PASS {checks}/{checks} split-weapon checks; both directions, two children and constant diagonals"
            : $"FAIL {failures.Count}/{checks}: " + string.Join("; ", failures);
    }

    private static readonly string Root =
#if UNITY_EDITOR
        Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
#else
        Path.GetFullPath(".").Replace('\\', '/');
#endif

    public static string SpritePaletteWorldContract()
    {
        var bank = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var table = new Darkwing.Core.SectionTable(bank);
        int cases = 0;
        foreach (byte index in new byte[] { 0, 7, 31 })
        for (byte world = 0; world < 16; world++)
        {
            var actual = new byte[16];
            var baseline = new byte[16];
            table.ReadSpritePaletteByIndex(index, baseline);
            table.ReadSpritePaletteByIndex(index, actual, world);
            byte expected = world == 8 || world == 9 ? (byte)0x26 : world == 0x0B ? (byte)0x11 : bank[0x4FD];
            if (actual[0] != expected) throw new System.Exception($"C33C-C35C tail world{world:X2} got{actual[0]:X2}/{expected:X2}");
            for (int entry = 1; entry < 16; entry++)
                if (actual[entry] != baseline[entry]) throw new System.Exception("Unrelated palette entry changed");
            cases++;
        }
        return $"PASS {cases} C319/C33C-C35C palette cases: world08/09=26,0B=11,ordinary control and other15 bytes preserved";
    }

    public static string UniversalBackgroundContract()
    {
        var chr = new byte[4096];
        for (int row = 0; row < 8; row++) chr[16 + row] = 0x80;
        var data = new Darkwing.Core.GameData(chr, new byte[0], new byte[0],
            new System.Collections.Generic.List<Darkwing.Core.GameData.LevelDef>(),
            new System.Collections.Generic.List<Darkwing.Core.GameData.AnimationDef>(),
            new System.Collections.Generic.List<Darkwing.Core.GameData.FrameDef>());
        var ppu = new Darkwing.Core.Ppu(data) { Chr0Bank = 0, Chr1Bank = 0, Mask = 0x1E };
        for (int index = 0; index < 64; index++)
        {
            ppu.Oam[index * 4] = 0xF8;
            ppu.NametableRam[0x3C0 + index] = 0xE4;
        }
        ppu.PaletteRam[0] = 0x26;
        ppu.PaletteRam[4] = ppu.PaletteRam[8] = ppu.PaletteRam[12] = 0x0F;
        var framebuffer = new byte[256 * 240];
        ppu.Render(framebuffer);
        for (int index = 0; index < framebuffer.Length; index++)
            if (framebuffer[index] != 0x26)
                throw new System.Exception($"Universal background mismatch x={index % 256} y={index / 256}: {framebuffer[index]:X2}/26");
        ppu.NametableRam[0] = 1;
        ppu.NametableRam[0x3C0] = 3;
        ppu.PaletteRam[13] = 0x30;
        ppu.Render(framebuffer);
        if (framebuffer[0] != 0x30 || framebuffer[1] != 0x26)
            throw new System.Exception("Nonzero palette color or transparent-neighbor control failed");
        return "PASS all61440 transparent background pixels use3F00 across all4 attribute quadrants; nonzero color retains its subpalette";
    }

    public static string ClearSceneContract()
    {
        byte[] Data(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        var decoder = new Darkwing.Core.RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets"));
        var game = new Darkwing.Core.DarkwingGame(data)
        {
            Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank2 = Data("prg_bank2"),
            Bank6 = Data("prg_bank6"), Bank4 = Data("prg_bank4"), ObjectTypeTable = Data("object_types")
        };
        byte[] reference = File.ReadAllBytes(Root + "/Art/Temp/stand_m1_orig_shift79/replay1/buffers.bin");
        if (reference.Length != 24261 * 4384) throw new System.IO.InvalidDataException("Incomplete original capture");
        int Differences(byte[] nt, int frame)
        {
            int count = 0;
            for (int index = 0; index < 2048; index++)
                if (nt[index] != reference[(frame - 1) * 4384 + 2048 + index]) count++;
            return count;
        }
        game.TileMap = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, decoder.GetWorld(0));
        game.WireWorldFeed();
        game.BeginSceneLoad(decoder, 8);
        var stage = game.GetType().GetField("_sceneLoadStage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        int passes = 0;
        while ((int)stage.GetValue(game) == 0 && passes++ < 100) game.TickSceneLoad();
        if ((int)stage.GetValue(game) != 3) throw new System.Exception("Staging did not reach loader tail");
        byte[] staged = (byte[])game.Ppu.NametableRam.Clone();
        game.TickSceneLoad();
        for (int index = 0; index < 1024; index++)
            if (staged[index] != reference[(22580 - 1) * 4384 + 2048 + index])
                throw new System.Exception($"Clear staging page0 mismatch ${index:X3}");
        for (int index = 0; index < 2048; index++)
            if (game.Ppu.NametableRam[index] != staged[index])
                throw new System.Exception($"Clear loader tail overwrote staged NT ${index:X3}");
        Directory.CreateDirectory(Root + "/Art/Temp/clear_scene_probe");
        File.WriteAllBytes(Root + "/Art/Temp/clear_scene_probe/staged.bin", staged);
        File.WriteAllBytes(Root + "/Art/Temp/clear_scene_probe/tail.bin", game.Ppu.NametableRam);
        game.TileMap.StreamColumns(game.Player.ScrollX);
        if (Differences(game.Ppu.NametableRam, 22580) <= Differences(staged, 22580))
            throw new System.Exception("Injected whole-window repaint was not detected");
        return $"PASS native world8 loader: page0 all1024 bytes exact versus original f22580, tail preserves staged NT2048, injected whole-window repaint rejected; {passes} batches; no original RAM seeded. Back page residue is not claimed by this isolated fixture.";
    }
    [System.Serializable] private sealed class CaptureRequest { public string rom_sha256; public string[] argv; }
    [System.Serializable] private sealed class CaseResult
    {
        public string name, result, strict, details, error;
    }
    [System.Serializable] private sealed class SuiteResult
    {
        public int schema = 1, cases, resultPassed, strictPassed, errors;
        public string verdict, scope = "Selected fields only; seeded fixtures, not full playthrough or full-frame equivalence.";
        public CaseResult[] results;
    }

    // Read the capture contract, rather than trusting a caller's old advisory `frames` value.
    // Several callers deliberately replay the WHOLE capture (e.g. WorldWalks: 700, not 360).
    private static void ValidateCapture(string dir, byte[] ram, int seed, byte[] pal = null, byte[] oam = null)
    {
        var req = JsonUtility.FromJson<CaptureRequest>(File.ReadAllText(dir + "/request.json"));
        if (req.rom_sha256 != "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00")
            throw new System.IO.InvalidDataException("Unexpected ROM identity: " + dir);
        int n = System.Array.IndexOf(req.argv, "--frames");
        if (n < 0 || n + 1 >= req.argv.Length || !int.TryParse(req.argv[n + 1], out int expected))
            throw new System.IO.InvalidDataException("No frame count in request: " + dir);
        ValidateRecords(ram.Length, expected, 2048, seed, "RAM");
        if (pal != null) ValidateRecords(pal.Length, expected, 32, seed, "palette");
        if (oam != null) ValidateRecords(oam.Length, expected, 256, seed, "OAM");
    }

    private static void ValidateRecords(int bytes, int expected, int stride, int seed, string label)
    {
        if (expected < 2 || seed < 0 || seed >= expected - 1 || bytes != (long)expected * stride)
            throw new System.IO.InvalidDataException($"{label}: bytes={bytes}, expected={expected}x{stride}, seed={seed}; no partial/empty acceptance");
    }

    private static string VerdictTail(string full)
    {
        int i = full.LastIndexOf("RESULT:", System.StringComparison.Ordinal);
        if (i < 0) throw new System.IO.InvalidDataException("Missing RESULT verdict");
        return full.Substring(i).Trim(); // Never cap: trailing STRICT/ENEMIES/OAM diagnostics matter.
    }

    private static bool SlotsEqual(byte[] port, byte[] rom, out int slot, out string field, out int got, out int want)
    {
        int[] addrs = { 0x4F0, 0x500, 0x510, 0x520, 0x4D0, 0x4E0, 0x420 };
        string[] names = { "Xlo", "Xhi", "Ylo", "Yhi", "state", "beh", "anim" };
        for (int s = 1; s < 16; s++)
        {
            bool a = (port[0x4D0 + s] & 0x80) != 0, b = (rom[0x4D0 + s] & 0x80) != 0;
            if (a != b) { slot = s; field = "active"; got = a ? 1 : 0; want = b ? 1 : 0; return false; }
            if (!a) continue;
            for (int k = 0; k < addrs.Length; k++)
                if (port[addrs[k] + s] != rom[addrs[k] + s])
                { slot = s; field = names[k]; got = port[addrs[k] + s]; want = rom[addrs[k] + s]; return false; }
        }
        slot = got = want = -1; field = null; return true;
    }
    /// <summary>
    /// Golden physics comparison through the full DarkwingGame stack (movement +
    /// anim advance + slot sync), replaying the captured original input schedule.
    /// </summary>
    private static string Compare(string name, int frames, System.Func<int, byte> input,
        int seedFrame = 0, bool camera = false, bool enemies = false, int world = 0,
        int entryFreeze = 0, bool trace = false, int limit = 0, bool palette = false,
        string goldenDir = null, bool scoreEnemies = false, bool scoreOam = false, bool pause = false,
        bool residentFade = false, bool mapScreen = false,
        System.Action<int, Darkwing.Core.DarkwingGame> frameObserver = null)
    {
        string root = Root;
        string gdir = goldenDir ?? $"gold_{name}";
        byte[] ramAll = File.ReadAllBytes(root + $"/Art/Research/DarkwingDuck/reference/{gdir}/ram_frames.bin");
        byte[] palAll = palette
            ? File.ReadAllBytes(root + $"/Art/Research/DarkwingDuck/reference/{gdir}/palette_frames.bin")
            : System.Array.Empty<byte>();
        byte[] oamAll = scoreOam
            ? File.ReadAllBytes(root + $"/Art/Research/DarkwingDuck/reference/{gdir}/oam_frames.bin")
            : System.Array.Empty<byte>();
        int avail = ramAll.Length / 2048;
        ValidateCapture(root + $"/Art/Research/DarkwingDuck/reference/{gdir}", ramAll, seedFrame,
            palette ? palAll : null, scoreOam ? oamAll : null);

        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        // $E388's section descriptors, the $E5BD $3D column and the $EFBE/$F008/$F049 palette-patch
        // tables all live in bank 7: a boot without Bank7 leaves Sections == null, so those services
        // are silently skipped and the $3E/$3F cursors never move.
        game.Bank7 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        // The pause phase ($8F00…$9025) installs its panel from the bank-4 tables, so a boot
        // without Bank4 leaves the install reading a null array.
        game.Bank4 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank4.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld((byte)world);
        game.SetWorldSource(decoder, (byte)world);   // $D437 re-runs $DD57 from the death resolve
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, world0);
        map.StreamColumns(0);
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();
        if (enemies)
        {
            game.Bank6 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank6.bytes");
            game.ObjectTypeTable = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes");
            // RunWorldServices only spawns when a level def is live; the route golden needs
            // the mission-1 placements so the room-0x02 walker exists for the f337 contact.
            game.CurrentLevel = data.Levels[world];
        }

        // Seed from the golden frame `seedFrame`.
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seedFrame * 2048, ram0, 0, 2048);
        game.Ppu.WriteRegister(0x2000, ram0[0xFF]);
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
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.ColsToStream = ram0[0x1F];   // $1F only changes through $E66C, so seed the captured value
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        // $DDFE STA $50 runs during the load frame itself, so a capture seeded on the first
        // frame that shows $50=$09 still owes 16 of the 17 fade frames ($DE03/$C270).
        p.EntryFreezeFrames = entryFreeze;
        // $0320/$0300/$3F00 are loader state the RAM-seeded start point does not carry: without
        // this the first fade ramps from zeros instead of from the live world table. The three
        // tables are seeded SEPARATELY (the ROM's own $0320/$0300/$3F00 values at seedFrame) —
        // they are legitimately out of step, e.g. gold_m1_longroute f0 has $0300[13]=$0F while
        // $3F00[13]=$05 is still on screen.
        if (palette)
        {
            var seedPal = new byte[32];
            System.Array.Copy(palAll, seedFrame * 32, seedPal, 0, 32);
            game.SeedPaletteTables(ram0, seedPal);
        }
        // The arena-entry capture is seeded INSIDE the resident $C270-$C2BF fade schedule (f29 is
        // the first frame of the $0F=$30 step), so the port must start parked there: $0F/$0D are
        // read from the golden, the cadence index is the phase the seed frame is in (the ROM keeps
        // it on the process stack, not in RAM).
        if (residentFade) game.SeedResidentFadeIn(ram0[0x0F], ram0[0x0D], 1);
        // $3D/$3E/$3F: the $EFBE palette-patch script is mid-cycle in every route capture, so its
        // cursor is captured state like $22 or $95. $66 gates the zone service ($CA75 BMI).
        p.SectionFlag3D = ram0[0x3D];
        p.PalStep3E = ram0[0x3E];
        p.PalIndex3F = ram0[0x3F];
        p.PendingMissionFlag = ram0[0x66];
        // $54 picks the panel's weapon name ($904E) and the slot-3 anim override, $60/$61 are
        // the two gas digits the panel draws — pause goldens have to carry them as captured.
        if (pause)
        {
            p.WeaponSelected = ram0[0x54];
            p.GasEnergyHi = ram0[0x60];
            p.GasEnergyLo = ram0[0x61];
            // $FE is the live $2001 mirror. Gameplay never writes it in the port (the ROM's
            // per-frame $2001 writes are not modelled), so the pause phase would otherwise start
            // from 0 and score $8FB1's AND/$901C's OR against a mask the ROM never held.
            game.Ppu.Mask = ram0[0xFE];
        }
        var s0 = game.Objects.Slots[0];
        if (enemies)
        {
            p.StateTimer = ram0[0x51];
            p.SavedState = ram0[0x58];
            p.InvulnFrames = ram0[0x7C];
            p.DeathSoundGate = ram0[0x9B];
            // Mid-mission route seeds (gold_m*_route) start with up to 3 hearts already spent,
            // and the ROM keeps that count in the player's RAM metasprite tile bytes, not a counter:
            // $F9C2 marks $05B4 ($80→$82), $05B8/$05B6 ($81→$83), then $05B2 plus the death write.
            // Without this the port reads the ROM's 4th (lethal) hit as a 1st and stays in $06.
            p.HitLadder = (byte)(ram0[0x5B2] != 0x80 ? 4
                                 : (ram0[0x5B4] != 0x80 ? 1 : 0) + (ram0[0x5B8] != 0x81 ? 1 : 0) +
                                   (ram0[0x5B6] != 0x81 ? 1 : 0));
            p.DirBits = ram0[0x52];
            // The $CE39 path is indexed by the EQUIPPED weapon ($53) and the kind 1/2/3
            // gate reads the live meter $05BE/$05C0, so a projectile golden has to carry them.
            p.WeaponActive = ram0[0x53];
            p.ActiveGasEnergyHi = ram0[0x5BE];
            p.ActiveGasEnergyLo = ram0[0x5C0];
            // $3B/$EE/$ED are the boss framework's own globals and a mid-fight seed has to carry
            // them: while $3B counts, the ROM holds every boss body at $F880/$F869, so without it
            // the port moves the boss through frames the original freezes (measured on
            // tmp_boss_pilot1 f29: ball $051E port C3 vs ROM D0). $3B is the same byte the player
            // state machine reads at $D580, and the port keeps that view in IntroHoldFlag.
            Darkwing.Core.BossBehaviors.DefeatTimer = ram0[0x3B];
            Darkwing.Core.BossBehaviors.DamageAccum = ram0[0xEE];
            Darkwing.Core.BossBehaviors.InvulnTimer = ram0[0xED];
            // $EF is the boss slot whose $B36A pass owns the $ED countdown; a mid-fight seed
            // without it freezes $ED on the seeded value and blocks every later hit
            // (pilot14: $ED stuck at $13, the f38 bolt never registered).
            Darkwing.Core.BossBehaviors.ProjectileSlot = ram0[0xEF];
            p.IntroHoldFlag = ram0[0x3B];
            game.FrameParity = ram0[0x95];
            for (int s = 0; s < 16; s++)
            {
                var sl = game.Objects.Slots[s];
                sl.Flags = ram0[0x400 + s];
                sl.AnimFrame = ram0[0x410 + s];
                sl.AnimNumber = ram0[0x420 + s];
                sl.AnimTimer = ram0[0x430 + s];
                sl.ScreenY = ram0[0x440 + s];
                sl.ScreenX = ram0[0x450 + s];
                sl.YFrac = ram0[0x460 + s];
                sl.XFrac = ram0[0x470 + s];
                sl.T480 = ram0[0x480 + s];
                sl.T490 = ram0[0x490 + s];
                sl.T4A0 = ram0[0x4A0 + s];
                sl.T4B0 = ram0[0x4B0 + s];
                sl.HitFlash = ram0[0x4C0 + s];
                sl.StateLow = ram0[0x4D0 + s];
                sl.Active = (ram0[0x4D0 + s] & 0x80) != 0;
                sl.Behavior = ram0[0x4E0 + s];
                sl.X = (ushort)(ram0[0x4F0 + s] | (ram0[0x500 + s] << 8));
                sl.Y = (ushort)(ram0[0x510 + s] | (ram0[0x520 + s] << 8));
                sl.XVelFrac = ram0[0x530 + s];
                sl.XVelInt = ram0[0x540 + s];
                sl.YVelFrac = ram0[0x550 + s];
                sl.YVelInt = (sbyte)ram0[0x560 + s];
                sl.BehaviorParam = ram0[0x570 + s];
                sl.Direction = ram0[0x580 + s];
                sl.PlacementIndex = ram0[0x590 + s];
                sl.Health = ram0[0x5A0 + s];
            }
            // $0340 = СБОРАННЫЕ размещения (бит ставит только pickup-путь через $FC29),
            // не «заспавненные»: захват начинается в середине уровня, поэтому уже
            // собранные предметы нельзя возродить в отыгрываемом прогоне.
            game.Objects.SeedSpawnMask(ram0);
            map.LoadRow(p.ScrollYRow);
            map.StreamColumns(p.ScrollX);
        }
        else
        {
            // $CAFC gates the whole player state machine on $04D0 bit 7, so the light seed
            // path has to carry the real flag byte too (ROM keeps $04D0=$81 while walking).
            s0.StateLow = ram0[0x4D0];
            s0.Active = (ram0[0x4D0] & 0x80) != 0;
            s0.AnimNumber = ram0[0x420];
            s0.AnimFrame = ram0[0x410];
            s0.AnimTimer = ram0[0x430];
        }

        // These historical "enemy" captures contain the city-map service, not a mission.
        // Restore its scheduler ownership as well as the slot RAM. Keep SlotsEqual unchanged.
        if (mapScreen)
        {
            if (ram0[0x420] != 0x36 || ram0[0x421] != 0x39 || ram0[0x422] != 0xBB)
                throw new InvalidDataException("Expected map plate/caret/bubble signature");
            game.WorldRunning = false;
            game.MapScreen = game.MapChooser = true;
            game.MapCursorBubble = ram0[0x6C];
            game.Oam.HudActive = false;
        }

        // Per-field first-divergence report, and the input to the STRICT verdict below. RESULT
        // itself only gates X/Y (plus the camera fields when asked), so this table is what makes
        // a "$25 drifts but RESULT says MATCH" run legible instead of misleading.
        var fieldNames = new[] { "X", "Y", "$50 state", "$FA scrollY", "$FB row",
                                 "$22 cell", "$23 next", "$2B scrollstate", "$25 streamcursor",
                                 "$0460 Yfrac", "$0470 Xfrac", "$0560 YvelI", "$0550 YvelF",
                                 "ladder($05B2-B8)", "$2F scrollmode", "$10 colmask",
                                 "$28 redrawscroll", "$FC scrollXlo",
                                 "$1F passes", "$20 prevmode" };
        var firstBad = new int[fieldNames.Length];
        var badPort = new int[fieldNames.Length];
        var badOrig = new int[fieldNames.Length];
        for (int i = 0; i < fieldNames.Length; i++) firstBad[i] = -1;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[player_mechanics] {name}: ");
        int firstDiv = -1;
        int matchFrames = 0;
        // Enemy-trajectory scoring: per frame, EVERY slot (active in either side) must match on
        // position (X/Y), the state/active byte, behavior and anim number. This is what proves the
        // bank-6 dispatcher deterministically, not just the player.
        int enemyMatchFrames = 0, enemyTotalFrames = 0, enemyFirstDiv = -1, enemyDivSlot = -1;
        string enemyDivField = null; int enemyDivPort = 0, enemyDivOrig = 0;
        // Per-frame outcomes of every clause STRICT is allowed to fail on (see the verdict block).
        bool fEnemies = true, fPal = true, fOam = true;
        int fieldBadIdx = -1, strictMatch = 0, strictFirstDiv = -1, strictNoOam = 0;
        string strictFirstDetail = null;
        int palOk = 0, palFirstBad = -1, winOk = 0, winTotal = 0;   // $3F00 byte compare
        // $3F00 can only diverge through the $0300 content or the $18 upload timing, so score the
        // port's two internal palette states against the captured RAM to localise which one is wrong.
        int mirOk = 0, srcOk = 0, flagOk = 0, mirFirstBad = -1, srcFirstBad = -1, flagFirstBad = -1;
        string mirFirstDetail = "", srcFirstDetail = "", flagFirstDetail = "";
        var mirP = new byte[32]; var srcP = new byte[32];
        int oamFrameOk = 0, oamTotal = 0, oamFirstBad = -1, oamByteSum = 0;
        int pauseOk = 0, pauseTotal = 0, pauseFirstBad = -1;
        bool panelOk = true;
        string pauseFirstDetail = "";
        int[] oamEntryBad = new int[64];
        string oamFirstDetail = "";
        const int OamLag = 0;
        // Streamer observability ($E63C inputs/outputs). The RESULT-gated dump windows only cover
        // frames where X/Y/$22/$23 differ, so a $25-only drift had no frame history at all; these
        // lines print every frame where any streamer byte changed on either side.
        int[] strPrev = null;
        int oamFramesAvail = oamAll.Length / 256;
        if (scoreOam)
            // The golden pairs OAM[f] with RAM[f] for the HUD placement but with RAM[f-1] for the
            // object records: the capture reads the live sprite buffer, which the NMI of frame f
            // filled from the shadow the walk of frame f-1 composed (Art/Temp/oam_lag.js: 622/622 and
            // 631/631 object records byte-exact with that pairing, 0 with any other). The port
            // reproduces it with a shadow + PublishToOam, so the comparison stays same-index and
            // the seed has to fill the SHADOW, not the live buffer.
            game.Oam.SeedShadow(oamAll, seedFrame * 256);
        Darkwing.Core.EnemyBehaviors.RngE4 = ram0[0xE4];
        Darkwing.Core.EnemyBehaviors.RngE5 = ram0[0xE5];
        Darkwing.Core.EnemyBehaviors.RngE6 = ram0[0xE6];
        Darkwing.Core.EnemyBehaviors.RngE7 = ram0[0xE7];
        game.SeedPrevHeld(input(seedFrame));
        p.LastVerticalRedraw = ram0[0x29]; p.VerticalRedrawRow = ram0[0x2A]; p.PrevVerticalMode = ram0[0x21];
        frameObserver?.Invoke(seedFrame, game);
        for (int f = seedFrame + 1; f < avail && (limit <= 0 || f - seedFrame <= limit); f++)
        {
            fEnemies = fPal = fOam = true; fieldBadIdx = -1;
            var pad = Darkwing.Core.NesButtons.FromByte(input(f));
            game.Step(pad);
            frameObserver?.Invoke(f, game);
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int gx = g[0x4F0] | (g[0x500] << 8);
            int gy = g[0x510] | (g[0x520] << 8);
            // $8F2A/$8F24 save slot 0's X/Y LOW bytes to $0378/$0372 and the install at
            // $8F78/$8F72 overwrites only $04F0/$0510, so while $8F00 owns the frame the ROM's
            // live player position is the page byte still in $0500/$0520 plus the saved low byte.
            if (pause && game.Paused)
            {
                gx = (g[0x500] << 8) | g[0x378];
                gy = (g[0x520] << 8) | g[0x372];
            }
            if (scoreEnemies)
            {
                byte[] prE = PackedSlots(game);
                enemyTotalFrames++;
                bool allMatch = SlotsEqual(prE, g, out int badSlot, out string badField, out int got, out int want);
                if (!allMatch && enemyFirstDiv < 0)
                { enemyFirstDiv = f; enemyDivSlot = badSlot; enemyDivField = badField; enemyDivPort = got; enemyDivOrig = want; }
                if (allMatch) enemyMatchFrames++;
                fEnemies = allMatch;
            }
            if (pause)
            {
                // The panel is six re-installed slots plus the $05C2 block, so this is the field
                // list $8F12…$8F7B writes — save/restore included, because a resume golden scores
                // the frames AFTER $8FCD too.
                pauseTotal++;
                byte[] prP = PackedSlots(game);
                string bad = null;
                int[] pAddrs = { 0x4D0, 0x400, 0x420, 0x4F0, 0x500, 0x510, 0x520 };
                for (int s = 0; s <= 5 && bad == null; s++)
                    for (int k = 0; k < pAddrs.Length && bad == null; k++)
                        if (prP[pAddrs[k] + s] != g[pAddrs[k] + s])
                            bad = $"slot{s} ${pAddrs[k]:X3} port={prP[pAddrs[k] + s]:X2} rom={g[pAddrs[k] + s]:X2}";
                var blk = game.PauseBlockForTest;
                for (int i = 0; i < 38 && bad == null; i++)
                    if (blk[i] != g[0x5C2 + i]) bad = $"${0x5C2 + i:X3} port={blk[i]:X2} rom={g[0x5C2 + i]:X2}";
                if (bad == null && p.InvulnFrames != g[0x7C]) bad = $"$7C port={p.InvulnFrames:X2} rom={g[0x7C]:X2}";
                if (bad == null && game.Ppu.Mask != g[0xFE]) bad = $"$FE port={game.Ppu.Mask:X2} rom={g[0xFE]:X2}";
                // Read the two live animation bytes directly for the player diagnostics.
                for (int s = 0; s <= 5 && bad == null; s++)
                {
                    var sl = game.Objects.Slots[s];
                    if (sl.AnimFrame != g[0x410 + s]) bad = $"slot{s} $410 port={sl.AnimFrame:X2} rom={g[0x410 + s]:X2}";
                    else if (sl.AnimTimer != g[0x430 + s]) bad = $"slot{s} $430 port={sl.AnimTimer:X2} rom={g[0x430 + s]:X2}";
                }
                panelOk = bad == null;
                if (bad == null) pauseOk++;
                else if (pauseFirstBad < 0) { pauseFirstBad = f; pauseFirstDetail = $"f{f} {bad}"; }
            }
            if (trace)
            {
                sb.Append($"f{f} P X={p.X:X4} xv={p.XVelInt:X2},{p.XVelFrac:X2} xfc={p.XFrac:X2} Y={p.Y:X4} vy={p.YVelInt:X2},{p.YVelFrac:X2} ffc={p.YFrac:X2} st={p.State:X2} an={s0.AnimNumber:X2}/{s0.AnimFrame:X2}/{s0.AnimTimer:X2} | O X={gx:X4} xv={g[0x540]:X2},{g[0x530]:X2} xfc={g[0x470]:X2} Y={gy:X4} vy={g[0x560]:X2},{g[0x550]:X2} ffc={g[0x460]:X2} st={g[0x50]:X2} an={g[0x420]:X2}/{g[0x410]:X2}/{g[0x430]:X2} p95={game.FrameParity:X2}/{g[0x95]:X2} r97={g[0x97]:X2} t92={g[0x92]:X2}");
                sb.Append($" H pre={p.PrevState:X2} ho={p.HurtOrigin:X2} p10={p.ProbeResultForTest:X2} iv={p.InvulnFrames:X2} hl={p.HitLadder:X2} | O10={g[0x10]:X2} O7C={g[0x7C]:X2} Ohl={g[0x5B2]:X2},{g[0x5B4]:X2},{g[0x5B6]:X2},{g[0x5B8]:X2}{DeathDiag(g, s0)}");
                sb.Append($" BB ee={Darkwing.Core.BossBehaviors.DamageAccum:X2}/{g[0xEE]:X2} ed={Darkwing.Core.BossBehaviors.InvulnTimer:X2}/{g[0xED]:X2} ef={Darkwing.Core.BossBehaviors.ProjectileSlot:X2}/{g[0xEF]:X2} 3b={Darkwing.Core.BossBehaviors.DefeatTimer:X2}/{g[0x3B]:X2}");
                sb.Append($" | S 22={p.CellIndex:X2}/{g[0x22]:X2} 23={p.CellIndexNext:X2}/{g[0x23]:X2} 25={p.StageCursor:X2}/{g[0x25]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} 2F={p.ScrollMode:X2}/{g[0x2F]:X2} FC={p.ScrollX & 0xFF:X2}/{g[0xFC]:X2}");
                var hs = game.Enemies != null && game.Enemies.HurtSlot >= 0
                    ? game.Objects.Slots[game.Enemies.HurtSlot] : null;
                if (hs != null)
                    sb.Append($" HS={game.Enemies.HurtSlot:X2}@{game.Enemies.HurtSlotX:X4},{game.Enemies.HurtSlotY:X4} an{game.Enemies.HurtSlotAnim:X2} bh{game.Enemies.HurtSlotBh:X2} p{game.Enemies.HurtSlotParam:X2} fl{game.Enemies.HurtSlotFlags:X2} st{game.Enemies.HurtSlotState:X2} act{(hs.Active ? 1 : 0)}");
                if (f >= _enemyDumpLo && f <= _enemyDumpHi)
                {
                    sb.Append($" RNG={Darkwing.Core.EnemyBehaviors.RngE4:X2}{Darkwing.Core.EnemyBehaviors.RngE5:X2}{Darkwing.Core.EnemyBehaviors.RngE6:X2}{Darkwing.Core.EnemyBehaviors.RngE7:X2}/{g[0xE4]:X2}{g[0xE5]:X2}{g[0xE6]:X2}{g[0xE7]:X2} E {ListSlots(PackedSlots(game), true)} | {ListSlots(g, true)}");
                    File.WriteAllBytes(Root + "/Art/Temp/m1_render_nt.bin", game.Ppu.NametableRam);
                    File.WriteAllBytes(Root + "/Art/Temp/m1_render_oam.bin", game.Ppu.Oam);
                    File.WriteAllBytes(Root + "/Art/Temp/m1_render_pal.bin", game.Ppu.PaletteRam);
                    var tex = new Texture2D(256,240,TextureFormat.RGBA32,false);
                    var px = new Color32[256*240];
                    byte savedMask = game.Ppu.Mask, savedCtrl = game.Ppu.Ctrl;
                    game.Ppu.Mask = g[0xFE]; game.Ppu.WriteRegister(0x2000,0x90);
                    game.Ppu.Render(game.Framebuffer);
                    game.Ppu.Mask = savedMask; game.Ppu.WriteRegister(0x2000,savedCtrl);
                    for (int y=0;y<240;y++) for(int x=0;x<256;x++)
                        px[(239-y)*256+x] = Darkwing.UnityApp.NesPalette.Table[game.Framebuffer[y*256+x]&63];
                    tex.SetPixels32(px); tex.Apply(false);
                    File.WriteAllBytes(Root+"/Art/Temp/m1_render.png",ImageConversion.EncodeToPNG(tex));
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                sb.Append('\n');
            }
            if (trace && f == _tileDumpFrame && game.TileMap != null)
            {
                var tm = game.TileMap;
                int py = p.Y + 12;
                var strip = new System.Text.StringBuilder();
                for (int c = 0; c < 16; c++) strip.Append(tm.TypeAt((p.X & 0xFF00) | (c * 16 + 8), py).ToString("X"));
                var win = new System.Text.StringBuilder();
                for (int b = 0x600; b < 0x680; b++) win.Append(g[b].ToString("X2"));
                sb.Append($"DUMP cell={p.X >> 8} row={(py & 0xFF) >> 4} probes={tm.TypeAt(p.X - 11, py):X}{tm.TypeAt(p.X, py):X}{tm.TypeAt(p.X + 11, py):X} strip={strip} ROM600={win}\n");
            }
            if (!trace && (f - seedFrame <= 2 || (f >= 238 && f <= 262) || (f >= 348 && f <= 366)
                           || (f >= _enemyDumpLo && f <= _enemyDumpHi)))
            {
                byte[] pr = PackedSlots(game);
                // Spawner diagnostic ($80DA/$80FC): the placement records near the
                // divergence, the port's own window test inputs, and its live $0590 set.
                int scx = p.ScrollX, scy = (p.ScrollYRow << 8) | p.ScrollY;
                var sp = new System.Text.StringBuilder(
                    $"[SP scx={scx:X4} scy={scy:X4}");
                foreach (int pl in game.CurrentLevel?.Objects != null ? new[] { 2, 3, 4, 5, 6 } : System.Array.Empty<int>())
                {
                    var rec = game.CurrentLevel.Objects[pl];
                    int px = (rec.ScreenX << 8) | rec.X, py = (rec.ScreenY << 8) | rec.Y;
                    int dxb = (px & 0xFF) - (scx & 0xFF), dyb = (py & 0xFF) - (scy & 0xFF);
                    bool win = ((px >> 8) - (scx >> 8) - (dxb < 0 ? 1 : 0)) == 0
                            && ((py >> 8) - (scy >> 8) - (dyb < 0 ? 1 : 0)) == 0;
                    int slot = -1;
                    for (int sl = 1; sl <= 0x0F; sl++)
                        if (game.Objects.Slots[sl].Active && game.Objects.Slots[sl].PlacementIndex == pl) { slot = sl; break; }
                    sp.Append($" {pl:X2}:({px:X4},{py:X4})t{rec.Type:X2}w{(win ? 1 : 0)}s{slot:X2}");
                }
                int freeSlot = -1;
                for (int sl = 14; sl >= 4; sl--)
                    if (!game.Objects.Slots[sl].Active) { freeSlot = sl; break; }
                sp.Append($" mask0={game.Objects.SpawnedBitmask(0):X2} free={freeSlot:X2}]");
                sb.Append(sp.ToString() + " ");
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}/{p.CellIndexNext:X2} 25={p.StageCursor:X2}/{g[0x25]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} 23o={g[0x23]:X2} 2B={p.ScrollState:X2} 10={p.ScrollStep:X2} 2F={p.ScrollMode:X2} vx={p.XVelInt},{p.XVelFrac:X2} xfc={s0.XFrac:X2} at={s0.AnimTimer:X2} pf={s0.AnimFrame:X2} dur={(game.Oam == null ? 0 : game.Oam.AnimHeader(s0.AnimNumber).duration)} iv={p.InvulnFrames:X2} vy={p.YVelInt},{p.YVelFrac:X2} ffc={p.YFrac:X2} hl={p.HitLadder} D={p.DeathFlag} d0={pr[0x4D0]:X2} e6={p.EnergyColRight:X2} e4={p.EnergyColLeft:X2} scx={p.ScrollX:X4}/{g[0xFC]:X2},{g[0xFD]:X2} | O({gx:X4},{gy:X4}) st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2} 2B={g[0x2B]:X2} 10={g[0x10]:X2} 2F={g[0x2F]:X2} vx={(sbyte)g[0x540]},{g[0x530]:X2} xfc={g[0x470]:X2} at={g[0x430]:X2} vy={(sbyte)g[0x560]},{g[0x550]:X2} ffc={g[0x460]:X2} ivc={g[0x7C]:X2} d0={g[0x4D0]:X2} e6={g[0x5E6]:X2} e4={g[0x5E4]:X2} 64={g[0x64]:X2} 6d={g[0x6D]:X2} af={s0.AnimFrame}/{g[0x410]} an={s0.AnimNumber:X2}/{g[0x420]:X2}] [E {ListSlots(pr)}| {ListSlots(g)}] ");
            }
            if (!trace)
            {
                int[] strNow = { p.StageCursor, g[0x25], p.CellIndexNext, g[0x23],
                                 p.ScrollMode, g[0x2F], p.ColsToStream, g[0x1F],
                                 p.PrevScrollMode, g[0x20], p.LastRedrawScroll, g[0x28],
                                 p.ScrollX & 0xFF, g[0xFC], p.ScrollStep, g[0x10] };
                bool strChanged = strPrev == null;
                if (strPrev != null)
                    for (int i = 0; i < strNow.Length; i++)
                        if (strNow[i] != strPrev[i]) { strChanged = true; break; }
                if (strChanged)
                    sb.Append($"STR f{f} 25={p.StageCursor:X2}/{g[0x25]:X2} 23={p.CellIndexNext:X2}/{g[0x23]:X2} 2F={p.ScrollMode:X2}/{g[0x2F]:X2} 1F={p.ColsToStream:X2}/{g[0x1F]:X2} 20={p.PrevScrollMode:X2}/{g[0x20]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} FC={p.ScrollX & 0xFF:X2}/{g[0xFC]:X2} 10={p.ScrollStep:X2}/{g[0x10]:X2} 50={p.State:X2}/{g[0x50]:X2} 97={g[0x97]:X2} ");
                strPrev = strNow;
            }
            bool pos = p.X == gx && p.Y == gy;
            bool cam = !camera || (p.State == g[0x50] && p.ScrollY == g[0xFA] && p.ScrollYRow == g[0xFB]
                                   && p.CellIndex == g[0x22] && p.CellIndexNext == g[0x23] && p.ScrollState == g[0x2B]);
            int[] pv = { p.X, p.Y, p.State, p.ScrollY, p.ScrollYRow,
                         p.CellIndex, p.CellIndexNext, p.ScrollState, p.StageCursor,
                         p.YFrac, p.XFrac, p.YVelInt & 0xFF, p.YVelFrac, p.HitLadder,
                         p.ScrollMode, p.ScrollStep, p.LastRedrawScroll, p.ScrollX & 0xFF,
                         p.ColsToStream, p.PrevScrollMode };
            int[] ov = { gx, gy, g[0x50], g[0xFA], g[0xFB],
                         g[0x22], g[0x23], g[0x2B], g[0x25],
                         g[0x460], g[0x470], g[0x560], g[0x550],
                         g[0x5B2] != 0x80 ? 4 : (g[0x5B4] != 0x80 ? 1 : 0) +
                             (g[0x5B8] != 0x81 ? 1 : 0) + (g[0x5B6] != 0x81 ? 1 : 0),
                         g[0x2F], g[0x10], g[0x28], g[0xFC], g[0x1F], g[0x20] };
            for (int i = 0; i < pv.Length; i++)
                if (pv[i] != ov[i])
                {
                    if (fieldBadIdx < 0) fieldBadIdx = i;
                    if (firstBad[i] < 0) { firstBad[i] = f; badPort[i] = pv[i]; badOrig[i] = ov[i]; }
                }
            if (palette)
            {
                bool same = true;
                for (int i = 0; i < 32; i++)
                    if (game.Ppu.PaletteRam[i] != palAll[f * 32 + i]) { same = false; break; }
                fPal = same;
                if (same) palOk++;
                else if (palFirstBad < 0) palFirstBad = f;
                if (f >= 795 && f <= 841) { winTotal++; if (same) winOk++; }
                game.GetPaletteModel(mirP, srcP);
                bool m = true, s2 = true;
                for (int i = 0; i < 32; i++)
                {
                    if (m && mirP[i] != g[0x300 + i])
                    {
                        m = false;
                        if (mirFirstBad < 0) { mirFirstBad = f; mirFirstDetail = $"f{f} i{i} port={mirP[i]:X2} rom={g[0x300 + i]:X2} 3D={p.SectionFlag3D:X2} 3E={p.PalStep3E:X2} 3F={p.PalIndex3F:X2}"; }
                    }
                    if (s2 && srcP[i] != g[0x320 + i])
                    {
                        s2 = false;
                        if (srcFirstBad < 0) { srcFirstBad = f; srcFirstDetail = $"f{f} i{i} port={srcP[i]:X2} rom={g[0x320 + i]:X2}"; }
                    }
                }
                if (m) mirOk++;
                if (s2) srcOk++;
                byte rom18 = g[0x18];
                if (game.PaletteUploadFlag == rom18) flagOk++;
                else if (flagFirstBad < 0) flagFirstDetail = $"f{f} port={game.PaletteUploadFlag:X2} rom={rom18:X2}";
                if (flagFirstBad < 0 && game.PaletteUploadFlag != rom18) flagFirstBad = f;
            }
            if (scoreOam && f + OamLag < oamFramesAvail)
            {
                int o = (f + OamLag) * 256;
                int ok = 0, badByte = -1;
                for (int i = 0; i < 256; i++)
                {
                    if (game.Ppu.Oam[i] == oamAll[o + i]) ok++;
                    else if (badByte < 0) badByte = i;
                }
                oamTotal++; oamByteSum += ok;
                if (trace && scoreOam)
                {
                    var od = new System.Text.StringBuilder($" [OAM f{f} ok={ok}/256 P:");
                    for (int k = 0; k < 64; k++) od.Append($"{game.Ppu.Oam[k * 4]:X2}{game.Ppu.Oam[k * 4 + 1]:X2}{game.Ppu.Oam[k * 4 + 2]:X2}{game.Ppu.Oam[k * 4 + 3]:X2} ");
                    od.Append("| O:");
                    for (int k = 0; k < 64; k++) od.Append($"{oamAll[o + k * 4]:X2}{oamAll[o + k * 4 + 1]:X2}{oamAll[o + k * 4 + 2]:X2}{oamAll[o + k * 4 + 3]:X2} ");
                    od.Append(']');
                    sb.Append(od);
                }
                for (int e = 0; e < 64; e++)
                    if (game.Ppu.Oam[e * 4] != oamAll[o + e * 4] || game.Ppu.Oam[e * 4 + 1] != oamAll[o + e * 4 + 1] ||
                        game.Ppu.Oam[e * 4 + 2] != oamAll[o + e * 4 + 2] || game.Ppu.Oam[e * 4 + 3] != oamAll[o + e * 4 + 3])
                        oamEntryBad[e]++;
                fOam = ok == 256;
                if (ok == 256) oamFrameOk++;
                else if (oamFirstBad < 0)
                {
                    oamFirstBad = f;
                    int e = badByte >> 2;
                    var detail = new System.Text.StringBuilder();
                    for (int k = 0; k < 64 && detail.Length < 700; k++)
                        if (game.Ppu.Oam[k * 4] != oamAll[o + k * 4] || game.Ppu.Oam[k * 4 + 1] != oamAll[o + k * 4 + 1] ||
                            game.Ppu.Oam[k * 4 + 2] != oamAll[o + k * 4 + 2] || game.Ppu.Oam[k * 4 + 3] != oamAll[o + k * 4 + 3])
                            detail.Append($"e{k}:{game.Ppu.Oam[k*4]:X2},{game.Ppu.Oam[k*4+1]:X2},{game.Ppu.Oam[k*4+2]:X2},{game.Ppu.Oam[k*4+3]:X2}/" +
                                          $"{oamAll[o + k * 4]:X2},{oamAll[o + k * 4 + 1]:X2},{oamAll[o + k * 4 + 2]:X2},{oamAll[o + k * 4 + 3]:X2} ");
                    oamFirstDetail = $" f{oamFirstBad}: {ok}/256 bytes, first diff b{badByte} (entry {e})" + detail;
                }
            }
            else if (scoreOam) strictNoOam++;
            if (pos && cam) matchFrames++;
            else
            {
                if (firstDiv < 0) firstDiv = f;
                if (f - firstDiv < 6 || f % 25 == 0)
                    sb.Append($"f{f}: port=({p.X:X4},{p.Y:X4} st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}) " +
                              $"orig=({gx:X4},{gy:X4} st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2}) ");
            }
            // STRICT = every field this comparison actually reads, so a match here — and only here —
            // means "frame-identical on all scored state", not "the two position bytes agree".
            bool strict = pos && cam && fieldBadIdx < 0 && fEnemies && fPal && fOam && (!pause || panelOk);
            if (strict) strictMatch++;
            else if (strictFirstDiv < 0)
            {
                strictFirstDiv = f;
                strictFirstDetail =
                    !pos ? $"player X/Y port=({p.X:X4},{p.Y:X4}) rom=({gx:X4},{gy:X4})"
                  : !cam ? $"cam $50 {p.State:X2}!={g[0x50]:X2} $FA {p.ScrollY:X2}!={g[0xFA]:X2} $FB {p.ScrollYRow:X2}!={g[0xFB]:X2} " +
                         $"$22 {p.CellIndex:X2}!={g[0x22]:X2} $23 {p.CellIndexNext:X2}!={g[0x23]:X2} $2B {p.ScrollState:X2}!={g[0x2B]:X2}"
                  : fieldBadIdx >= 0 ? $"{fieldNames[fieldBadIdx]} port={pv[fieldBadIdx]:X4} rom={ov[fieldBadIdx]:X4}"
                  : !fEnemies ? "ENEMIES (see ENEMIES clause)"
                  : !fPal ? "PAL $3F00"
                  : !fOam ? "OAM"
                  : !panelOk ? "PANEL" : "?";
            }
        }
        int span = avail - 1 - seedFrame;
        if (limit > 0) span = System.Math.Min(limit, span);
        // Which fields each verdict is allowed to certify. Printed inline so a RESULT match cannot
        // be read as full-frame identity: RESULT covers 2 (or 8, with camera) fields, STRICT covers
        // every field this comparison reads, including the per-slot enemy state.
        string resultOf = camera
            ? string.Join(",", new[] { fieldNames[0], fieldNames[1], fieldNames[2], fieldNames[3],
                                       fieldNames[4], fieldNames[5], fieldNames[6], fieldNames[7] })
            : fieldNames[0] + "," + fieldNames[1];
        string strictOf = string.Join(",", fieldNames)
            + (scoreEnemies ? ",ENEMIES" : "") + (palette ? ",PAL$3F00" : "")
            + (scoreOam ? ",OAM" : "") + (pause ? ",PANEL" : "");
        if (palette)
        {
            sb.Append(palFirstBad < 0
                ? $"PAL $3F00: MATCH {palOk}/{span} "
                : $"PAL $3F00: {palOk}/{span} (first bad f{palFirstBad}, death window f795-f841 {winOk}/{winTotal}) ");
            sb.Append($"PALMODEL[$0300 {mirOk}/{span}{((mirFirstBad >= 0) ? " first " + mirFirstDetail : " MATCH")} | $0320 {srcOk}/{span}{((srcFirstBad >= 0) ? " first " + srcFirstDetail : " MATCH")} | $18 {flagOk}/{span}{((flagFirstBad >= 0) ? " first " + flagFirstDetail : " MATCH")}] ");
        }
        sb.Append("FIELDS[");
        for (int i = 0; i < fieldNames.Length; i++)
            if (firstBad[i] >= 0)
                sb.Append($"{fieldNames[i]}:f{firstBad[i]} {badPort[i]:X4}!={badOrig[i]:X4} ");
        sb.Append("] ");
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {matchFrames}/{span} frames (seed {seedFrame}) [of {resultOf}]"
            : $"RESULT: first divergence frame {firstDiv} (matched {matchFrames}/{span}, seed {seedFrame}) [of {resultOf}]");
        sb.Append(strictFirstDiv < 0
            ? $"  STRICT: MATCH {strictMatch}/{span} [of {strictOf}]"
            : $"  STRICT: first divergence f{strictFirstDiv} {strictFirstDetail} (matched {strictMatch}/{span}) [of {strictOf}]");
        if (strictNoOam > 0) sb.Append($" [OAM data absent on {strictNoOam} frames: not scored]");
        if (scoreOam)
        {
            sb.Append($"  OAM: exact {oamFrameOk}/{oamTotal} frames, mean {(oamTotal == 0 ? 0 : oamByteSum / oamTotal)}/256 bytes,{oamFirstDetail}");
            var hist = new System.Text.StringBuilder();
            for (int e = 0; e < 64 && hist.Length < 420; e++)
                if (oamEntryBad[e] > 0) hist.Append($"e{e}:{oamEntryBad[e]} ");
            sb.Append($"  OAM-BAD-ENTRIES(of {oamTotal}): {hist}");
        }
        if (scoreEnemies)
            sb.Append(enemyFirstDiv < 0
                ? $"  ENEMIES: MATCH {enemyMatchFrames}/{enemyTotalFrames} frames (slots 01-0F: X/Y/state+active/beh/anim identical both ways)"
                : $"  ENEMIES: first divergence f{enemyFirstDiv} slot {enemyDivSlot:X2} field {enemyDivField} port={enemyDivPort:X2} orig={enemyDivOrig:X2} (matched {enemyMatchFrames}/{enemyTotalFrames})");
        if (pause)
            sb.Append(pauseFirstBad < 0
                ? $"  PANEL: MATCH {pauseOk}/{pauseTotal} frames (slots 0-5 $04D0/$0400/$0410/$0420/$0430/$04F0/$0500/$0510/$0520 + $05C2 block + $7C + $FE)"
                : $"  PANEL: first divergence {pauseFirstDetail} (matched {pauseOk}/{pauseTotal})");
        File.WriteAllText(root + $"/Art/Temp/{name}_trace.txt", sb.ToString());   // the run_script console truncates
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

    public static string JumpSoundContract()
    {
        string directory = Root + "/Art/Research/DarkwingDuck/reference/gold_jump";
        byte[] ram = File.ReadAllBytes(directory + "/ram_frames.bin");
        ValidateCapture(directory, ram, 19);
        for (int frame = 15; frame <= 28; frame++)
        {
            int offset = frame * 2048;
            if (ram[offset + 0xD9] != 4 || ram[offset + 0xD0] != 0 || ram[offset + 0xD1] != 0)
                throw new System.Exception("Original jump audio contract changed at " + frame);
        }
        if (ram[19 * 2048 + 0x50] != 0 || ram[20 * 2048 + 0x50] != 1)
            throw new System.Exception("Original control does not contain the expected jump");

        void Verify(bool injectFalseSound)
        {
            int soundCount = 0;
            bool checkedJump = false;
            string result = Compare("jump", 120, frame =>
                (byte)((frame >= 10 && frame <= 40 ? 1 : 0) | (frame >= 20 && frame <= 22 ? 0x80 : 0)),
                frameObserver: (frame, game) => {
                    if (frame == 0)
                    {
                        var enqueue = game.Player.Sfx;
                        game.Player.Sfx = id => { soundCount++; enqueue?.Invoke(id); };
                    }
                    if (frame != 20) return;
                    if (game.Player.State != ram[20 * 2048 + 0x50])
                        throw new System.Exception("Native control did not take the original ground jump");
                    if (injectFalseSound) game.Player.Sfx(0x21);
                    if (soundCount != 0)
                        throw new System.IO.InvalidDataException("Ground jump must not enqueue a sound");
                    checkedJump = true;
                });
            if (!checkedJump || !VerdictTail(result).Contains("RESULT: MATCH"))
                throw new System.Exception("Jump control was incomplete or diverged");
        }

        Verify(false);
        bool rejected = false;
        try { Verify(true); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        if (!rejected) throw new System.Exception("Jump sound negative control was accepted");
        return "PASS original gold_jump f15-28 audio task control; native ground jump silent; injected SFX21 rejected (not waveform equivalence)";
    }

    public static string WorldAudioContract()
    {
        byte[] Read(string name) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
        var bank7 = Read("world_bank7");
        var world = new Darkwing.Core.WorldState(bank7);
        var decoder = new Darkwing.Core.RoomDecoder(Read("prg_bank0"), Read("prg_bank1"), bank7, Read("levelsets"));
        var game = new Darkwing.Core.DarkwingGame(Darkwing.Core.DataLoader.Load(
            Read("chr_full"), Read("levelsets"), Read("screens"), Read("levels"), Read("anims"))) {
            Bank7 = bank7, Bank5 = Read("prg_bank5")
        };
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var queue = (byte[])typeof(Darkwing.Core.DarkwingGame).GetField("_audioQueue", flags).GetValue(game);
        byte LastId() => (byte)typeof(Darkwing.Core.DarkwingGame).GetField("_audioLastId", flags).GetValue(game);
        int Cursor() => (int)typeof(Darkwing.Core.DarkwingGame).GetField("_audioWrite", flags).GetValue(game);
        if (world.BgPaletteIndex(1) != 1 || decoder.GetWorld(1).RingEvent != 5)
            throw new System.Exception("Original DF04 palette and DF13 audio descriptors must be distinct");
        game.BeginSceneLoad(decoder, 0);
        if (LastId() != 0 || Cursor() != 0)
            throw new System.Exception("DDA0 must preserve a zero incoming D9");
        game.PlaySound(0xF0);
        game.BeginSceneLoad(decoder, 0);
        game.BeginSceneLoad(decoder, 1);
        if (LastId() != 5 || Cursor() != 3 || queue[0] != 0xF0 || queue[1] != 4 || queue[2] != 5)
            throw new System.Exception("DDA6 must enqueue each world's DF13 task, not the palette index");
        game.BeginSceneLoad(decoder, 9);
        if (LastId() != 0 || Cursor() != 4 || queue[3] != 0)
            throw new System.Exception("Nonzero incoming D9 must also enqueue a zero DF13 task");
        game.BeginSceneLoad(decoder, 1);
        if (LastId() != 0 || Cursor() != 4)
            throw new System.Exception("Zero D9 must suppress the next world task");
        game.PlaySfx(0x3F);
        if (LastId() != 0 || Cursor() != 5 || queue[4] != 0x3F)
            throw new System.Exception("F850 must enqueue SFX without changing D9");
        game.PlaySound(0xF0);
        game.Bank6 = Read("prg_bank6");
        game.Enemies.Sfx(0x12);
        if (LastId() != 0xF0 || Cursor() != 7 || queue[6] != 0x12)
            throw new System.Exception("Enemy SFX must use the same NMI queue and preserve D9");
        game.PlaySfx(0x11);
        game.PlaySound(9);
        if (LastId() != 9 || Cursor() != 0 || queue[0] != 0xF0)
            throw new System.Exception("A full ring must drop F84E data but retain the new D9");
        return "PASS DF04/DF13 distinction; incoming D9 guard; world tasks4/5 and zero; F84E/F850 distinction; enemy queue; full-ring D9 (diagnostic fixture)";
    }

    public static string AudioPeriodTailContract()
    {
        byte[] bank = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes");
        var audio = new Darkwing.Audio.AudioSequencer(bank);
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = audio.GetType();
        var channel = (byte[])type.GetField("_p7", binding).GetValue(audio);
        foreach (bool tablePath in new[] { false, true })
        {
            foreach (var sample in new[] {
                (instrument: (byte)41, flags: (byte)0x28, reset: true),
                (instrument: (byte)1, flags: (byte)0x20, reset: true),
                (instrument: (byte)41, flags: (byte)0x20, reset: false) })
            {
                channel[0] = sample.instrument; channel[4] = sample.flags; channel[8] = 0x7F;
                type.GetMethod("SetInstrumentPtr", binding).Invoke(audio, new object[] { 0 });
                if (tablePath)
                {
                    type.GetField("_c3", binding).SetValue(audio, (byte)0);
                    type.GetMethod("StorePeriod", binding).Invoke(audio, new object[] { 0 });
                }
                else type.GetMethod("SetPeriodHiLo", binding).Invoke(audio, new object[] { 0, (byte)4, (byte)0 });
                byte expectedFlags = sample.reset ? (byte)(sample.flags & 0x37) : sample.flags;
                byte expectedDecay = sample.reset ? (byte)0 : (byte)0x7F;
                if (channel[4] != expectedFlags || channel[8] != expectedDecay)
                    throw new System.Exception($"Missing 863E-8659 tail: table={tablePath} instrument={sample.instrument} flags={channel[4]:X2}/{expectedFlags:X2} decay={channel[8]:X2}/{expectedDecay:X2}");
            }
        }
        return "PASS six bank3 863E-8659 period-tail contracts: direct/table, reload bit, negative instrument byte4 and no-reset control (not waveform equivalence)";
    }

    /// <summary>G58 pause golden: mission 1 resumed from `gold_w00/gameplay.mss` with Start
    /// pressed at f20 and held to f23, so f20…69 are the bank-4 $8F00 phase. `$14` is an
    /// edge byte, so the held press cannot resume — the resume needs the second capture.</summary>
    public static string PauseHold() => Compare("pause_hold", 70,
        f => (f >= 20 && f < 24) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_m1", pause: true);

    /// <summary>The resume path $8FCD…$9025: a second Start edge at f45 closes the phase,
    /// so the same six slots, block, palette and mask have to come back byte-exactly.</summary>
    public static string PauseResume() => Compare("pause_resume", 80,
        f => ((f >= 20 && f < 24) || (f >= 45 && f < 49)) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_resume", pause: true);

    /// <summary>Same run as <see cref="PauseHold"/> but with the full per-frame trace plus the
    /// 64-entry port/ROM OAM dump, written to Art/Temp/pause_trace_trace.txt.</summary>
    public static string PauseTrace() => Compare("pause_trace", 70,
        f => (f >= 20 && f < 24) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_m1", pause: true, trace: true);

    /// <summary><see cref="PauseResume"/> with the per-frame OAM dump, used to attribute which
    /// frames still diverge: Art/Temp/pause_res_trace_trace.txt.</summary>
    public static string PauseResumeTrace() => Compare("pause_res_trace", 80,
        f => ((f >= 20 && f < 24) || (f >= 45 && f < 49)) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_resume", pause: true, trace: true);

    /// <summary>
    /// Mission-1 traversal golden `gold_traverse_m1`: the original resumed from
    /// m1_standing.mss with Right held from frame 2. The player walks to the room-0x02 pylon
    /// wall and stops at X=$114 (frame 159) — the first end-to-end check of the world feed
    /// (cell/row advance, camera, spawn on cell entry), so camera state is compared too.
    /// </summary>
    public static string Traverse() => Compare("traverse_m1", 600,
        f => (byte)(f >= 2 ? 0x01 : 0x00), camera: true);

    /// <summary>
    /// Mission-1 route golden `gold_m1_fulljump`: Right held from f2 plus ONE 26-frame
    /// A-hold at f160-185. A 3-frame tap only hops 14 px, so the pylon in room 0x02
    /// (solid rows 6-15, cols 2-3) is impassable; the held jump (apex ~$0425 velocity,
    /// 52 px rise) lands the player on the pylon top at Y=$354, he then walks right,
    /// drops into the lower floor of cell $34 ($22 advances at f254, scroll crosses
    /// $0100) and takes a NATURAL enemy hit at f338 ($50=$06, anim $09). One independent
    /// trajectory that covers world feed + cell advance + spawn + bank-6 dispatch + $813B.
    /// </summary>
    public static string M1Route() => Compare("m1_fulljump", 400,
        f => (byte)((f >= 2 && f <= 390 ? 0x01 : 0x00) | (f >= 160 && f <= 185 ? 0x80 : 0x00)),
        camera: true, enemies: true);

    /// <summary>
    /// Golden `gold_m1_cling` (probe_run `--input 0:right@2-190 --input 0:a@40-65 --input 0:a@70-95`,
    /// no RAM patch): the first jump rises 40 px and parks against the room-0x02 pylon face at
    /// f51 ($50=$04, anim $0B, SFX $12, $F607) with X=114/Y=844 held for 19 frames; the SECOND A
    /// edge at f70 goes through $D224 (impulse $0550=$E5/$0560=$04, $50=$0B, anim $11) and rises
    /// another 50 px, so the player lands on the pylon top shelf at Y=$324 (surface $330) — a
    /// 80 px climb from the street. This is the mechanic every mission-1 vertical shaft uses,
    /// so the park + re-launch arc has to be byte-exact before any route attempt. The replay
    /// feeds the schedule exactly as recorded: Right f2..190, then two A-holds f40..65 and
    /// f70..95. The golden's release at f190 is the only idle-transition observation this
    /// fixture has, so the replay must stop holding too — an unbounded Right tests a walk the
    /// original never ran.
    /// </summary>
    public static string M1Cling() => Compare("m1_cling", 200,
        f => (byte)((f >= 2 && f <= 190 ? 0x01 : 0x00) | (f >= 40 && f <= 65 ? 0x80 : 0x00)
              | (f >= 70 && f <= 95 ? 0x80 : 0x00)),
        camera: true, enemies: true, trace: true);

    /// <summary>
    /// Natural climb golden `gold_m1_climb` (probe_run `--input 0:right@2-390 --input 0:up@180-390`,
    /// no RAM patch, no warp): the mission-1 walk into the room-0x02 pylon, then UP held from
    /// f180 — the hook/grab attempt every mission-1 vertical shaft needs. The suite's "hook" case
    /// rides a PATCHED seed cell, so it cannot prove the grab is reachable by input; this fixture
    /// is the input-driven version and locates the first divergence of the climb path.
    /// </summary>
    public static string M1Climb() => Compare("m1_climb", 400,
        f => (byte)((f >= 2 && f <= 390 ? 0x01 : 0x00) | (f >= 180 && f <= 390 ? 0x08 : 0x00)),
        camera: true, enemies: true, trace: true);

    /// <summary>
    /// Natural contact golden `gold_contact` (objective item 3/7): mission 1 from
    /// `m1_standing.mss`, original input is `0:right@10-360` — NO jump, so the player walks
    /// right until the room-0x02 tower wall (the G14 finding: the walker sits behind that
    /// wall, so a straight walk can't reach it). Replaying the EXACT schedule and comparing
    /// byte-exact tells whether the port stops at the same wall at the same X as the ROM.
    /// If it diverges that is a real finding (G14 climb), not something to fake.
    /// </summary>
    public static string Contact() => Compare("contact", 400,
        f => (byte)(f >= 10 && f <= 360 ? 0x01 : 0x00),
        camera: true, enemies: true);

    /// <summary>
    /// M7 HUD (G44): full 256-byte OAM compare against gameplay goldens, frame by frame.
    /// This is the instrument that measures where the 8-sprite HUD block lands; before the
    /// $95-driven cursor it sat at a fixed entries 1-8 and the score plateaued near
    /// OamVerify's ~101/256.
    /// </summary>
    public static string HudOam()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Compare("hud_oam_contact", 400,
            f => (byte)(f >= 10 && f <= 360 ? 0x01 : 0x00),
            camera: true, enemies: true, goldenDir: "gold_contact", scoreOam: true));
        sb.Append(" ||| ");
        sb.Append(Compare("hud_oam_jump", 120, f =>
        {
            byte b = 0;
            if (f >= 10 && f <= 40) b |= 0x01;
            if (f >= 20 && f <= 22) b |= 0x80;
            return b;
        }, goldenDir: "gold_jump", scoreOam: true));
        sb.Append(" ||| ");
        sb.Append(Compare("hud_oam_longroute", 2500,
            f =>
            {
                byte b = (f >= 2 && f <= 2490) ? (byte)0x01 : (byte)0x00;
                for (int k = 0; k < 18; k++)
                {
                    int s = 160 + 130 * k;
                    if (f >= s && f <= s + 25) b |= 0x80;
                }
                return b;
            },
            camera: true, enemies: true, goldenDir: "gold_m1_longroute", scoreOam: true));
        File.WriteAllText(Root + "/Art/Temp/hud_oam_sweep.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// G36 step 1 — the long mission-1 route `gold_m1_longroute` (2500 frames from
    /// m1_standing.mss, Right held f2-2490 plus 26-frame A-holds every 130 frames from f160,
    /// NO B so the unported $CE39 weapon spawn cannot pollute the diff). The ROM on this exact
    /// schedule clears the pylon, parks in state $04 at X=$01C1 for 126 frames, escapes it via
    /// $D21E on the next A-hold ($50=$0B), reaches maxX=$0394 and only then dies in a pit
    /// (f795, $50=$07) — so the live port's death at X=$01CF is a port divergence, not input
    /// policy. Full 2500-frame capture: the 620-frame cap was only ever a guess at the
    /// run_script budget, and the whole comparison executes in ~75 ms.
    /// </summary>
    public static string M1LongRoute() => Compare("m1_longroute", 2500,
        f =>
        {
            byte b = (f >= 2 && f <= 2490) ? (byte)0x01 : (byte)0x00;
            for (int k = 0; k < 18; k++)
            {
                int s = 160 + 130 * k;
                if (f >= s && f <= s + 25) b |= 0x80;
            }
            return b;
        },
        camera: true, enemies: true, trace: true, limit: 0, palette: true);

    /// <summary>
    /// Mission-1 trench crossing candidate route `gold_m1_step` (900 frames from
    /// m1_standing.mss, Right held f2-899 plus 26-frame A-holds at f150, f195 and f258).
    /// The ROM on this schedule: jumps the room-0x02 wall onto the $864 step (f183 X=310),
    /// is knocked off the second jump by the behavior-$0C walker that stands on the $816
    /// one-way (state $06 at f201), then drops into the 128x64 trench at f276. There is no
    /// ladder mechanic: holding Up at the $98/$A0/$A8 ladder art (X=468, feet 960) leaves
    /// $50=$00 and the walk anim unchanged, and world 0 has no type-7/8 hook tiles at all, so
    /// the only way out of the trench is the $816 one-way stepping stone. This case measures
    /// whether the port reproduces the step landing, the one-way and the contact.
    /// </summary>
    public static string M1StepRoute() => Compare("m1_step", 900,
        f =>
        {
            byte b = (f >= 2 && f <= 899) ? (byte)0x01 : (byte)0x00;
            if (f >= 150 && f <= 175) b |= 0x80;
            if (f >= 195 && f <= 220) b |= 0x80;
            if (f >= 258 && f <= 283) b |= 0x80;
            return b;
        },
        camera: true, enemies: true, trace: true, limit: 0, palette: true,
        goldenDir: "gold_m1_step");

    /// <summary>
    /// Verified natural trench crossing, golden `m1_trench` (620 frames from m1_standing.mss,
    /// no RAM patch, no warp): the player is knocked back only twice and ends standing on the
    /// $896 ledge at X=473. Sequence: Right to the $288 face (blocked at X=276, the side probe
    /// is X±12), 12-frame A-hold onto the $864 step (f183 X=296), two B presses at the wrong
    /// altitude (the $849 gas passes under the walker at Y=$804 - a projectile keeps the Y it
    /// was spawned at), 20-frame A-hold from X=303 onto the $816 one-way (f278 X=342), one B
    /// press at X=343 whose gas meets the walker at f293 X=402, then a 20-frame A-hold from
    /// X=405 that carries the player over the 64-px trench onto the $896 ledge. The route
    /// measures the port's one-way support (floor probe X-11/X/X+11), gas spawn offset and
    /// altitude, behavior-$0C walker patrol and the $D2DE knockback velocity cut.
    /// </summary>
    public static string M1Trench() => Compare("m1_trench", 620,
        f =>
        {
            byte b = ((f >= 2 && f <= 184) || (f >= 236 && f <= 620)) ? (byte)0x01 : (byte)0x00;
            if ((f >= 229 && f <= 232) || (f >= 235 && f <= 238) || (f >= 282 && f <= 285)) b |= 0x40;
            if (f >= 162 && f <= 173) b |= 0x80;
            if (f >= 252 && f <= 271) b |= 0x80;
            if (f >= 369 && f <= 388) b |= 0x80;
            return b;
        },
        camera: true, enemies: true, trace: true, limit: 0, palette: true,
        goldenDir: "m1_trench", scoreEnemies: true);

    /// <summary>Replay the capture's actual button schedule, including releases.
    /// Temporary diagnostic captures may be passed as an absolute directory.</summary>
    public static string M1Natural(string capture, int dumpFrame = -1, int seedFrame = 0)
    {
        string dir = Path.GetFullPath(capture);
        var req = JsonUtility.FromJson<CaptureRequest>(File.ReadAllText(dir + "/request.json"));
        var windows = new System.Collections.Generic.List<(int lo, int hi, byte mask)>();
        for (int i = 0; i + 1 < req.argv.Length; i++)
        {
            if (req.argv[i] != "--input") continue;
            var m = System.Text.RegularExpressions.Regex.Match(req.argv[++i], @"^0:(a|b|select|start|up|down|left|right)@(\d+)-(\d+)$");
            if (!m.Success) throw new System.IO.InvalidDataException("Unsupported input schedule");
            string[] names = { "right", "left", "down", "up", "start", "select", "b", "a" };
            windows.Add((int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                (byte)(1 << System.Array.IndexOf(names, m.Groups[1].Value))));
        }
        _enemyDumpLo = _enemyDumpHi = dumpFrame;
        try { return Compare("m1_natural", 0, f => {
            byte held = 0;
            foreach (var w in windows) if (f >= w.lo && f <= w.hi) held |= w.mask;
            return held;
        }, seedFrame: seedFrame, camera: true, enemies: true, trace: true, palette: true,
           goldenDir: Path.GetRelativePath(Root + "/Art/Research/DarkwingDuck/reference", dir), scoreEnemies: true); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>The trench run diverges where the ROM takes an enemy contact. Dump the full
    /// per-slot list (port | ROM) around that frame so the missing $813B hit is attributable
    /// to a slot field instead of guessed.</summary>
    public static string M1TrenchSlotDump()
    {
        _enemyDumpLo = 222; _enemyDumpHi = 320;
        try { return M1Trench(); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>
    /// Shared schedule for the mission route goldens `gold_m2_route` … `gold_m7_route`:
    /// 1500 frames captured from each `gold_wNN/gameplay.mss` — the state probe_run saves at
    /// the END of that world's entry capture, so the port seeds mid-mission (no $DDFE entry
    /// handover, hence seedFrame 0 and no entryFreeze). Rhythm is the m1 long route: Right held
    /// f2-1490 plus 26-frame A-holds every 130 frames from f120, and NO B so the unported
    /// $CE39 weapon spawn cannot pollute the diff. First traversal comparison per mission
    /// beyond the 371-frame entry fall.
    /// </summary>
    private static string Route(string name, int world, bool trace = false) => Compare(name, 1500,
        f =>
        {
            byte b = (f >= 2 && f <= 1490) ? (byte)0x01 : (byte)0x00;
            for (int k = 0; k < 11; k++)
            {
                int s = 120 + 130 * k;
                if (f >= s && f <= s + 25) b |= 0x80;
            }
            return b;
        },
        camera: true, enemies: true, world: world, palette: true, trace: trace,
        // G51 root cause, measured in the golden itself: `gold_m6_route` is the only route captured
        // while the world loader is still resident. f0…f2 hold $50=$09, $10=$40, $95=$FE, $97=$70,
        // $0460/$0550/$0560 and slot $0F byte-identical, and only $92 ticks. $97 goes to $04 and
        // $95 to $FF on f3, but $0460/$0550 stay at 9E/AB on f3 and only move to F3/6B on f4 —
        // so m6's release frame ticks the engine (parity + anim) one frame BEFORE the player pass
        // runs, unlike gold_w05 f345 where they coincide. The three frozen iterations reproduce
        // the tail of `$DE03 JSR $FF06 + $DE06 JMP $C254→$C270`; the parity catch-up fires on
        // iteration 3 (port f3 $95=FF matches golden), and gravity starts on iteration 4.
        entryFreeze: world == 5 ? 3 : 0);

    public static string M2Route() => Route("m2_route", 1);
    public static string M3Route() => Route("m3_route", 2);
    public static string M4Route() => Route("m4_route", 3);
    public static string M5Route() => Route("m5_route", 4);
    public static string M6Route() => Route("m6_route", 5);
    public static string M7Route() => Route("m7_route", 6);

    /// <summary>Per-frame dumps for the two diverging routes, written to
    /// Art/Temp/{name}_trace.txt (m3 diverges at f31 on Y/$0550/$0560, m4 at f63 on
    /// state $06-vs-$07 plus the whole gravity vector).</summary>
    public static string RouteTraces()
    {
        M3RouteTrace(); M4RouteTrace();
        return "wrote Art/Temp/m3_route_trace.txt and Art/Temp/m4_route_trace.txt";
    }
    public static string M3RouteTrace() => Route("m3_route", 2, trace: true);
    public static string M4RouteTrace() => Route("m4_route", 3, trace: true);

    /// <summary>Shift 56.1: m6_route is the only route whose RESULT diverges inside the run
    /// (f330) and has no trace wrapper, so the f325-f335 window is unreadable. Same shape as
    /// M4RouteTrace; the trace is written to Art/Temp/m6_route_trace.txt by Route(trace: true).</summary>
    public static string M6RouteTrace() => Route("m6_route", 5, trace: true);

    /// <summary>G51: `gold_m6_route` diverges on the SECOND frame, so the diff has to start at the
    /// seed. Full per-frame lines plus the ROM/port slot lists for f0…f12 → Art/Temp/m6_route_trace.txt.
    /// Known so far: the seed is already `$50=$09` with `$10=$40` (the port gives `$10=$01`) and slot
    /// `$0F` is live with `beh=$69`, which the bank-6 dispatcher never has to serve in m1…m5.</summary>
    public static string M6EntryDump()
    {
        _enemyDumpLo = 0; _enemyDumpHi = 12;
        try { return Route("m6_route", 5, trace: true); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>G52: m3 f1314 is the ROM's post-death world reload ($D41B -> $D423), not a
    /// fourth heart-ladder hit - f1305..f1313 match byte-for-byte with a frozen death
    /// snapshot and a FULL ladder on both sides. Dumps the full per-frame line for
    /// f1240-f1320 (ladders, $64/$6D branch counters, slot lists) so the reload transition
    /// itself is visible. Pair with Art/Tools/DarkwingResearch/frame_diff.py for the byte list.</summary>
    public static string M3ReloadDump()
    {
        _enemyDumpLo = 1240; _enemyDumpHi = 1320;
        try { return Route("m3_route", 2, trace: true); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Missions 2-7 traversal scoreboard: one RESULT line each (Compare keeps the
    /// full per-frame detail in Art/Temp/{name}_trace.txt because the console truncates).</summary>
    public static string Routes()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, System.Func<string> run)
        {
            string full;
            try { full = run(); }
            catch (System.Exception e) { sb.Append(label).Append(": ERROR ").Append(e.Message).Append('\n'); return; }
            int i = full.LastIndexOf("RESULT:");
            sb.Append(label).Append(": ")
              .Append(i < 0 ? "no result" : full.Substring(i))
              .Append('\n');
        }
        One("m2(w01)", M2Route);
        One("m3(w02)", M3Route);
        One("m4(w03)", M4Route);
        One("m5(w04)", M5Route);
        One("m6(w05)", M6Route);
        One("m7(w06)", M7Route);
        File.WriteAllText(Root + "/Art/Temp/routes_summary.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>G25 acceptance: the per-frame PPU-palette verdict ($3F00-$3F1F, 32 bytes) of the
    /// captures whose route runs the zone service $EDF1. Of the zone handlers' outputs, the ones
    /// these fixtures record frame by frame are the $C2F9/$C319 palette reload and the $18 upload
    /// flag ($1A/$6D/$70 are zero on every frame of all seven — see the note in GAPS), so this is
    /// the metric that decides whether a handler's cell bands and palette indices are right.
    /// `Routes()` truncates at RESULT:, which drops the PAL clause that precedes it.</summary>
    public static string ZoneScore()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, System.Func<string> run)
        {
            string full;
            try { full = run(); }
            catch (System.Exception e) { sb.Append(label).Append(": ERROR ").Append(e.Message).Append('\n'); return; }
            int i = full.IndexOf("PAL $3F00:");
            int j = full.IndexOf("RESULT:");
            sb.Append(label).Append(": ")
              .Append(i < 0 ? "PAL NOT SCORED " : full.Substring(i, j > i ? j - i : full.Length - i))
              .Append(j < 0 ? "(no result)" : full.Substring(j, System.Math.Min(58, full.Length - j)))
              .Append('\n');
        }
        One("w0(m1_longroute)", M1LongRoute);
        One("w1(m2_route)", M2Route);
        One("w2(m3_route)", M3Route);
        One("w3(m4_route)", M4Route);
        One("w4(m5_route)", M5Route);
        One("w5(m6_route)", M6Route);
        One("w6(m7_route)", M7Route);
        File.WriteAllText(Root + "/Art/Temp/zone_score.txt", sb.ToString());
        return sb.ToString();
    }

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
    /// Historical EnemyVerify fixture: the save named m1_gameplay is a city map (slots
    /// $36/$39/$BB/$73/$73), so this tests map slots, not bank-6 enemy coverage.
    /// All active/missing/extra slots and seven fields are still compared at the same index.
    /// </summary>
    public static string EnemyVerify() => Compare("enemy_verify", 600, f => 0,
        enemies: true, world: 0, goldenDir: "probe_enemy_verify", scoreEnemies: true, mapScreen: true, trace: false);

    public static string EnemyFixtureRegression()
    {
        string wrong = Compare("enemy_seed_control", 600, f => 0, enemies: true,
            goldenDir: "probe_enemy_verify", scoreEnemies: true, limit: 20);
        string fixedSeed = Compare("enemy_seed_fixed", 600, f => 0, enemies: true,
            goldenDir: "probe_enemy_verify", scoreEnemies: true, limit: 20, mapScreen: true);
        if (!wrong.Contains("ENEMIES: first divergence f9 slot 01 field active port=00 orig=01")
            || !fixedSeed.Contains("ENEMIES: MATCH 20/20"))
            throw new System.Exception("Map ownership negative control did not reproduce/repair f9");
        return "PASS: world-owner control fails f9 slot01 active; map-owner seed matches 20/20; identical SlotsEqual and golden.";
    }

    public static string EnemyVerifyTrace() => Compare("enemy_verify_trace", 600, f => 0,
        enemies: true, world: 0, goldenDir: "probe_enemy_verify", scoreEnemies: true, mapScreen: true, trace: true);

    /// <summary>Sweep the five historical map fixtures; ENEMIES is the legacy slot verdict label.</summary>
    public static string EnemySweep()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string dir in new[] { "probe_enemy_verify", "probe_enemy_verify2", "probe_enemy_verify3",
                                        "probe_enemy_verify4", "probe_enemy_verify5" })
        {
            if (!System.IO.File.Exists(Root + "/Art/Research/DarkwingDuck/reference/" + dir + "/ram_frames.bin"))
            { sb.Append($"{dir}: NO CAPTURE\n"); continue; }
            string r = Compare("enemy_sweep_" + dir, 600, f => 0, enemies: true, world: 0,
                goldenDir: dir, scoreEnemies: true, mapScreen: true, trace: false);
            int i = r.IndexOf("ENEMIES:");
            sb.Append($"{dir}: {(i >= 0 ? r.Substring(i) : "(no enemy verdict)")} \n");
        }
        System.IO.File.WriteAllText(Root + "/Art/Temp/enemy_sweep.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>Per-world `$DD57` seeding. `reference/gold_wNN` are original-ROM captures made
    /// with `probe_run --patch 31=NN --patch-frame 314`: $31 is only READ by the level entry
    /// (bank 7 `$C8B5 JSR $FF55 #$04` → `$C8BD JSR $DD57`), so injecting the world id one frame
    /// before the loader runs selects the world without touching the ROM. The loader's seed
    /// ($36/$FB/$22/$23/$2E/$2D/$1A/$1B/$3D/$04F0/$0510/$FC) must equal DarkwingGame.LoadWorld.
    /// Fixture note: the world id is injected, the loader and all data are the original ROM's.</summary>
    public static string WorldSeeds()
    {
        string root = Root;
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var sb = new System.Text.StringBuilder();
        int okAll = 0, nAll = 0;
        for (int world = 0; world < 7; world++)
        {
            string dir = root + $"/Art/Research/DarkwingDuck/reference/gold_w{world:X2}";
            if (!File.Exists(dir + "/ram_frames.bin")) throw new FileNotFoundException("Missing world capture", dir);
            byte[] ram = File.ReadAllBytes(dir + "/ram_frames.bin");
            ValidateCapture(dir, ram, 0);
            int frames = ram.Length / 2048;

            var game = new Darkwing.Core.DarkwingGame(data);
            game.Bank5 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
            game.Bank7 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
            var decoder = new Darkwing.Core.RoomDecoder(
                File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
                File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
                File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes"),
                File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));
            game.LoadWorld(decoder, (byte)world);
            var p = game.Player;

            // $2E is $01 before the load and 0x0A..0x13 for every world after it.
            int lf = -1;
            for (int f = 300; f < frames; f++)
                if (ram[f * 2048 + 0x2E] != 0x01) { lf = f; break; }
            if (lf < 0) throw new System.IO.InvalidDataException($"w{world}: loader never ran");
            byte[] g = new byte[2048];
            System.Array.Copy(ram, lf * 2048, g, 0, 2048);

            // $DD76-$DDB6 (descriptor) lands in the load frame; the slot-0/player seed
            // ($DDDB-$DDFE) only shows in the snapshot once the ~10-frame $DE24 staging
            // loop has run, so the ROM reports it from f328 for these fixtures and the
            // port needs the same number of Steps. $1A/$1B hold the CHR request until
            // the NMI at f330, which is why the latch itself is compared two frames later.
            int pf = System.Math.Min(lf + 10, frames - 1);
            int cf = System.Math.Min(lf + 12, frames - 1);
            byte[] q = new byte[2048];
            System.Array.Copy(ram, pf * 2048, q, 0, 2048);
            byte[] r = new byte[2048];
            System.Array.Copy(ram, cf * 2048, r, 0, 2048);
            var pad = Darkwing.Core.NesButtons.FromByte(0);
            for (int i = lf; i < pf; i++) game.Step(pad);

            var fields = new System.Collections.Generic.List<string>();
            void One(string label, byte got, byte want)
            {
                nAll++;
                bool ok = got == want;
                if (ok) okAll++;
                fields.Add(ok ? label : $"{label}={want:X2}!={got:X2}");
            }
            One("$36", p.TilesetBank, g[0x36]);
            One("$FB", p.ScrollYRow, g[0xFB]);
            One("$2E", p.RowStride, g[0x2E]);
            One("$2D", p.BottomRow, g[0x2D]);
            One("$3D", p.SectionFlag3D, g[0x3D]);
            One("$22", p.CellIndex, q[0x22]);
            // $DD7B/$DD7D seed $23 = $22, $DE7E DEC $23 puts it one cell BEHIND the camera,
            // and the $DE86-$DE91 loop walks the $25 cursor to $0F while stepping $23 with
            // it (144 passes), ending at StartCell+1. The ROM time-slices that loop over
            // ~10 frames, the port runs it inline inside ApplyWorldLoad.
            One("$23", p.CellIndexNext, q[0x23]);
            One("$04F0", (byte)(p.X & 0xFF), q[0x4F0]);
            One("$0500", (byte)(p.X >> 8), q[0x500]);
            One("$0510", (byte)(p.Y & 0xFF), q[0x510]);
            One("$0520", (byte)(p.Y >> 8), q[0x520]);
            One("$FC", (byte)(p.ScrollX & 0xFF), q[0xFC]);
            One("$FD", (byte)(p.ScrollX >> 8), q[0xFD]);
            One("$0400", p.Flags, q[0x400]);
            One("$50", p.State, q[0x50]);
            // The port applies the request inside LoadWorld and zeroes the latch there, so
            // only the consumed-by-f330 semantics can be compared — the banks are below.
            One("$1A", p.ChrLatch0, r[0x1A]);
            One("$1B", p.ChrLatch1, r[0x1B]);
            One("CHR1", (byte)game.Ppu.Chr1Bank, (byte)(g[0x1B] & 0x7F));
            One("CHR0", (byte)game.Ppu.Chr0Bank, (byte)(g[0x1A] & 0x7F));
            sb.Append($"w{world}@f{lf}/{pf}: {okAll}/{nAll} cum [{string.Join(" ", fields)}]\n");
        }
        sb.Append($"RESULT: {(okAll == 133 && nAll == 133 ? "MATCH" : "DIVERGENCE")} world seeds {okAll}/{nAll} fields over 7 captures\n");
        return sb.ToString();
    }

    /// <summary>Per-mission gameplay golden. Every `gold_wNN` capture is the level-entry
    /// fixture (`probe_run --patch 31=NN --patch-frame 314 --input` idle pad), so the port is
    /// seeded from f330 — two frames after the $DDFE state-$09 handover — and replayed with
    /// zero input through the full stack (gravity, collision map, camera, column streamer,
    /// spawner). Missions 2-7 had no frame comparisons at all before these captures.</summary>
    public static string WorldWalks()
    {
        var sb = new System.Text.StringBuilder();
        for (int world = 0; world < 7; world++)
            sb.Append(Compare($"w{world:X2}", 360, WorldInput,
                seedFrame: 328, camera: true, enemies: true, world: world, entryFreeze: 16)).Append('\n');
        return sb.ToString();
    }

    /// <summary>All seven `gold_wNN/request.json` files carry the identical schedule
    /// (`0:right@30-33 0:a@120-123 0:a@300-303 0:a@460-463 0:right@520-690`). Only the last
    /// two fall inside the f328-seeded window; replaying them is mandatory — with zero input
    /// the port stands still while the golden walks right from f520.</summary>
    private static byte WorldInput(int f) =>
        (byte)(((f >= 520 && f <= 690) ? 0x01 : 0) | ((f >= 460 && f <= 463) ? 0x80 : 0));

    /// <summary>
    /// Compact per-frame trace of the $D517 entry fall for one world (G28): the golden is
    /// seeded on f328, the first frame that shows $50=$09, and only Y/vy/$0460/state are
    /// printed so the whole 700-frame capture still fits in the tool output.
    /// </summary>
    /// <summary>Frame at which the trace also dumps the port's tile-type strip and the
    /// ROM's own $0600 collision window (-1 = off; set by the *Dump entries below).</summary>
    private static int _tileDumpFrame = -1;

    /// <summary>w03 falls through the floor the ROM lands on at f582 (G30) — dump the probe data.</summary>
    public static string FloorProbeDumpW03()
    {
        _tileDumpFrame = 582;
        try { return TraceEntry(3, 578, 584); }
        finally { _tileDumpFrame = -1; }
    }

    public static string WorldEntryTrace() => TraceEntry(0, 505, 600);

    /// <summary>G33: the pit-death frame of gold_w05 (f656, $04D0→$00) plus the death-service
    /// frames that follow, where $D41B tail-calls $DD57 and the world stays frozen. Prints
    /// $04D0/$05E6/$05E4/$64/$6D on both sides.</summary>
    public static string DeathTraceW05()
    {
        _enemyDumpLo = 648; _enemyDumpHi = 700;
        try { return Compare("w05", 360, WorldInput, seedFrame: 328,
                             camera: true, enemies: true, world: 5, entryFreeze: 16); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Shift-16 G41 follow-up: the w03 card park (gold $95 frozen f460-520, exactly
    /// as in w01/w02/w04, which all match 371/371) is not what moved w03's first divergence
    /// from f664 to f604, so dump the per-frame anim/X/Xfrac clock and the live slots across
    /// the release (f521) and the f568-f599 state-1/6 event.</summary>
    public static string GaitTraceW03()
    {
        _enemyDumpLo = 560; _enemyDumpHi = 612;
        try
        {
            return Compare("w03", 360, WorldInput, seedFrame: 328, camera: true,
                enemies: true, world: 3, entryFreeze: 16, trace: true, limit: 300);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Inclusive frame window for the per-frame active-slot dump inside the trace
    /// (-1 disables it). G31 needs the w03 landing frames 575-592.</summary>
    private static int _enemyDumpLo = -1, _enemyDumpHi = -1;

    /// <summary>Maps the port's object slots onto the ROM's $04xx address layout.</summary>
    private static byte[] PackedSlots(Darkwing.Core.DarkwingGame game)
    {
        var pr = new byte[2048];
        for (int sl = 0; sl <= 0x0F; sl++)
        {
            var s = game.Objects.Slots[sl];
            pr[0x4D0 + sl] = (byte)(s.Active ? 0x80 | s.StateLow : s.StateLow);
            pr[0x400 + sl] = s.Flags;
            pr[0x570 + sl] = s.BehaviorParam;
            pr[0x4E0 + sl] = s.Behavior;
            pr[0x480 + sl] = s.T480;
            pr[0x490 + sl] = s.T490;
            pr[0x580 + sl] = s.Direction;
            pr[0x590 + sl] = s.PlacementIndex;
            pr[0x4F0 + sl] = (byte)(s.X & 0xFF); pr[0x500 + sl] = (byte)(s.X >> 8);
            pr[0x510 + sl] = (byte)(s.Y & 0xFF); pr[0x520 + sl] = (byte)(s.Y >> 8);
            pr[0x420 + sl] = s.AnimNumber;
            pr[0x410 + sl] = s.AnimFrame;
            pr[0x430 + sl] = s.AnimTimer;
            pr[0x530 + sl] = s.XVelFrac; pr[0x540 + sl] = (byte)s.XVelInt;
            pr[0x550 + sl] = s.YVelFrac; pr[0x560 + sl] = (byte)s.YVelInt;
        }
        return pr;
    }

    /// <summary>G36 step 6: ROM slot-0 death counters vs the port's, so the $D353 timer
    /// ladder is read from the capture instead of guessed.</summary>
    private static string DeathDiag(byte[] r, Darkwing.Core.ObjectSystem.Slot s0) =>
        $" | D d0={r[0x4D0]:X2}/{s0.StateLow:X2} t80={r[0x480]:X2}/{s0.T480:X2} t90={r[0x490]:X2}/{s0.T490:X2} f00={r[0x400]:X2}/{s0.Flags:X2} hp={r[0x5A0]:X2}/{s0.Health:X2}";

    /// <summary>G36 step 6: read the m1_longroute golden directly — death-window zero-page
    /// bytes plus the full changed-address list per frame, so the f814 respawn transition is
    /// identified by RAM address instead of guessed.</summary>
    public static string DeathDump()
    {
        string root = Root;
        byte[] ramAll = File.ReadAllBytes(root + "/Art/Research/DarkwingDuck/reference/gold_m1_longroute/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        var sb = new System.Text.StringBuilder();
        int[] keys = { 0x14, 0x31, 0x3B, 0x50, 0x51, 0x53, 0x54, 0x58, 0x5F, 0x60, 0x61, 0x64, 0x65, 0x66,
                       0x6D, 0x7C, 0x97, 0x98, 0x9A, 0x9B,
                       0x400, 0x420, 0x4D0, 0x4F0, 0x500, 0x510, 0x520, 0x5E4, 0x5E6, 0xFA, 0xFB, 0xFC, 0xFD };
        sb.Append($"frames={avail}\nKEYS\n");
        for (int f = 770; f <= 830 && f < avail; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f} ");
            for (int k = 0; k < keys.Length; k++) sb.Append($"{keys[k]:X3}={g[keys[k]]:X2} ");
            sb.Append('\n');
        }
        sb.Append("\nDIFFS\n");
        byte[] prev = new byte[2048];
        System.Array.Copy(ramAll, 770 * 2048, prev, 0, 2048);
        for (int f = 771; f <= 830 && f < avail; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f}: ");
            int shown = 0;
            for (int a = 0; a < 2048; a++)
            {
                if (g[a] == prev[a]) continue;
                if (a > 0xFF && !(a >= 0x400 && a <= 0x600)) continue;
                sb.Append($"{a:X3}={prev[a]:X2}>{g[a]:X2} ");
                if (++shown >= 90) { sb.Append("+more "); break; }
            }
            sb.Append('\n');
            System.Array.Copy(g, prev, 2048);
        }
        File.WriteAllText(root + "/Art/Temp/death_dump.txt", sb.ToString());
        return $"death_dump.txt written: {sb.Length} chars";
    }

    private static string ListSlots(byte[] r, bool inactive = false)
    {
        var s = new System.Text.StringBuilder();
        for (int sl = 1; sl <= 0x0F; sl++)
        {
            bool active = (r[0x4D0 + sl] & 0x80) != 0;
            if (!active && !inactive) continue;
            // An inactive slot is only interesting while it still carries a template.
            if (!active && r[0x4E0 + sl] == 0 && r[0x420 + sl] == 0) continue;
            s.Append($"{(active ? "" : "-")}{sl:X2}:{r[0x4F0 + sl] | (r[0x500 + sl] << 8):X4},{r[0x510 + sl] | (r[0x520 + sl] << 8):X4}/an{r[0x420 + sl]:X2}af{r[0x410 + sl]:X2}at{r[0x430 + sl]:X2}bh{r[0x4E0 + sl]:X2}st{r[0x4D0 + sl]:X2}t{r[0x480 + sl]:X2}q{r[0x490 + sl]:X2}d{r[0x580 + sl]:X2}pl{r[0x590 + sl]:X2}p{r[0x570 + sl]:X2}f{r[0x400 + sl]:X2}vy{r[0x560 + sl]:X2},{r[0x550 + sl]:X2} ");
        }
        return s.ToString();
    }

    /// <summary>G31: which placement does the ROM activate at the w03 landing point, where
    /// the golden goes to $50=$06 (hurt) at f583 while the port keeps walking.</summary>
    public static string EnemyDumpW03()
    {
        _enemyDumpLo = 575; _enemyDumpHi = 592;
        try { return TraceEntry(3, 575, 592); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>The two remaining world divergences (w03 f664, w05 f656 — w01 matched after
    /// the spawner became pointer state) with the active-slot dump, to tell tile hazards
    /// from enemy contact.</summary>
    public static string DivTraceAll()
    {
        var sb = new System.Text.StringBuilder();
        int[][] w = { new[] { 3, 658, 670 }, new[] { 5, 652, 664 } };
        foreach (var t in w)
        {
            _enemyDumpLo = t[1]; _enemyDumpHi = t[2];
            sb.Append($"=== w{t[0]:X2} ===\n").Append(TraceEntry(t[0], t[1], t[2])).Append('\n');
            _enemyDumpLo = _enemyDumpHi = -1;
        }
        return sb.ToString();
    }

    /// <summary>w03 starts falling on its own at f568 — the release-window trace for it.</summary>
    public static string ReleaseTraceW03() => TraceEntry(3, 540, 600);

    public static string TraceEntry(int world, int from, int to)
    {
        var full = Compare($"w{world:X2}", 360, WorldInput,
            seedFrame: 328, camera: true, enemies: true, world: world, entryFreeze: 16, trace: true);
        var lines = full.Split('\n');
        var keep = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
            if (l.StartsWith("DUMP")) { keep.Append(l).Append('\n'); continue; }
            int sp = l.IndexOf(' ');
            if (!l.StartsWith("f") || sp < 2) continue;
            if (!int.TryParse(l.Substring(1, sp - 1), out int f)) continue;
            if (f >= from && f <= to) keep.Append(l).Append('\n');
        }
        keep.Append(lines[lines.Length - 1]);
        return keep.ToString();
    }

    /// <summary>Dumps the mission-1 placement list the spawner reads, to find why the
    /// route golden has no live objects (activation compares ScreenX/ScreenY against
    /// $FD/$FB, and the ROM walker slot $0D spawns one cell AHEAD of the camera).</summary>
    public static string ProbeLevel()
    {
        string root = Root;
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var sb = new System.Text.StringBuilder();
        sb.Append($"levels={data.Levels.Length} ");
        for (int li = 0; li < System.Math.Min(3, data.Levels.Length); li++)
        {
            var l = data.Levels[li];
            int maxSX = 0, maxSY = 0;
            foreach (var o in l.Objects ?? new Darkwing.Core.GameData.Placement[0])
            {
                if (o.ScreenX > maxSX) maxSX = o.ScreenX;
                if (o.ScreenY > maxSY) maxSY = o.ScreenY;
            }
            sb.Append($"[{li} id={l.Id} {l.Name} wh={l.Width}x{l.Height} n={l.ObjectCount} sx<= {maxSX} sy<= {maxSY}] ");
        }
        var lv = data.Levels[0];
        foreach (var o in lv.Objects ?? new Darkwing.Core.GameData.Placement[0])
            if (o.ScreenY <= 4 && o.ScreenX <= 3)
                sb.Append($"#{o.Index} t={o.Type:X2} sx={o.ScreenX} x={o.X:X2} sy={o.ScreenY} y={o.Y:X2} wx={(o.ScreenX << 8) | o.X:X4} wy={(o.ScreenY << 8) | o.Y:X4}; ");
        return sb.ToString();
    }

    /// <summary>
    /// $CE39 weapon-spawn golden `gold_b_attack`: mission 1 standing still, B held for four
    /// frames at f30-33, f100-103 and f160-163 ($16 = $40 there, $14 = $40 only on the first
    /// frame of each window). Slot 4 must appear on the PRESS frame already initialized
    /// ($04D0=$81, $0540=$04, X = player $2C + $0F + one 4 px step = $3F), travel 4 px/frame
    /// to the right edge kill, and the player must hold the fire pose $05 until the anim
    /// reaches frame 2 / timer 4, where $CBF6-$CC02 releases it back to idle $01.
    /// Slot 1 is live for the whole capture (beh $00, anim $1B) and $CE48 never tests it,
    /// so this also proves the projectile scan is restricted to slots 4..2.
    /// </summary>
    public static string BAttack() => Compare("b_attack", 200,
        f => (f >= 30 && f <= 33) || (f >= 100 && f <= 103) || (f >= 160 && f <= 163)
             ? (byte)0x40 : (byte)0x00,
        seedFrame: 29, camera: true, enemies: true, goldenDir: "gold_b_attack", scoreEnemies: true);

    /// <summary>The corrected ENEMIES check (Y + the active bit both ways) found a class the old
    /// ROM-active filter could not see: b_attack f79 holds slot 04 alive in the port and dead in
    /// the ROM. Same run with both slot lists printed around that frame.</summary>
    public static string BAttackSlotDump()
    {
        _enemyDumpLo = 74; _enemyDumpHi = 84;
        try
        {
            return Compare("b_attack_dump", 200,
                f => (f >= 30 && f <= 33) || (f >= 100 && f <= 103) || (f >= 160 && f <= 163)
                     ? (byte)0x40 : (byte)0x00,
                seedFrame: 29, camera: true, enemies: true, goldenDir: "gold_b_attack",
                scoreEnemies: true, trace: true);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>All cases retain full details; JSON is the authoritative automation verdict.</summary>
    public static string Suite()
    {
        var sb = new System.Text.StringBuilder();
        var rows = new System.Collections.Generic.List<CaseResult>();
        void One(string label, System.Func<string> run)
        {
            var row = new CaseResult { name = label };
            rows.Add(row);
            try
            {
                string full = run();
                row.details = "Art/Temp/suite_" + label + ".txt";
                File.WriteAllText(Root + "/" + row.details, full);
                string tail = VerdictTail(full);
                row.result = tail.StartsWith("RESULT: MATCH ") ? "PASS" : "FAIL";
                int strict = tail.IndexOf("STRICT:", System.StringComparison.Ordinal);
                row.strict = strict < 0 ? row.result : tail.Substring(strict).StartsWith("STRICT: MATCH ") ? "PASS" : "FAIL";
                sb.AppendLine(label + ": " + tail);
            }
            catch (System.Exception e)
            {
                row.result = row.strict = "ERROR"; row.error = e.ToString();
                row.details = "Art/Temp/suite_" + label + ".txt";
                File.WriteAllText(Root + "/" + row.details, row.error);
                sb.AppendLine(label + ": ERROR " + e.Message);
            }
        }
        One("walk", Walk); One("jump", Jump); One("b_attack", BAttack); One("hook", Hook);
        One("traverse", Traverse); One("contact", Contact); One("m1route", M1Route);
        One("hurt", Hurt); One("door", Door); One("worldseeds", WorldSeeds);
        for (int w = 0; w < 7; w++)
        {
            int world = w;
            One($"w{w:X2}_fall", () => Compare($"w{world:X2}", 360, WorldInput,
                seedFrame: 328, camera: true, enemies: true, world: world, entryFreeze: 16));
        }
        One("longroute", M1LongRoute);
        One("m2", M2Route); One("m3", M3Route); One("m4", M4Route);
        One("m5", M5Route); One("m6", M6Route); One("m7", M7Route);
        var report = new SuiteResult { results = rows.ToArray(), cases = rows.Count };
        foreach (var row in rows)
        {
            if (row.result == "PASS") report.resultPassed++;
            if (row.strict == "PASS") report.strictPassed++;
            if (row.result == "ERROR") report.errors++;
        }
        report.verdict = report.errors > 0 ? "ERROR" : report.strictPassed == report.cases ? "PASS" : "FAIL";
        sb.AppendLine($"SUITE: {report.verdict}; RESULT {report.resultPassed}/{report.cases}; STRICT {report.strictPassed}/{report.cases}; errors {report.errors}");
        File.WriteAllText(Root + "/Art/Temp/suite_summary.txt", sb.ToString());
        File.WriteAllText(Root + "/Art/Temp/suite_result.json", Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
        return sb.ToString();
    }

    /// <summary>Synthetic faults only; never mutates ROM or golden files.</summary>
    private sealed class PushProbeMap : Darkwing.Core.ITileMap
    {
        public int Reads;
        public int LastX;
        public byte TypeAt(int x, int y) { Reads++; LastX = x; return 0; }
    }

    private sealed class CrouchDropMap : Darkwing.Core.ITileMap
    {
        public byte TypeAt(int x, int y) => 1;
    }

    public static string CrouchDropContract()
    {
        var o=new Darkwing.Core.ObjectSystem(null);
        var p=new Darkwing.Core.PlayerCore(new CrouchDropMap()) {
            Objects=o, State=2, X=3888, Y=340, YFrac=0xBD, ScrollYRow=1,
            YVelInt=-1, YVelFrac=0xAB, Flags=0xD0 };
        o.Slots[0].AnimNumber=6;o.Slots[0].AnimFrame=1;o.Slots[0].StateLow=0x81;
        p.Step(0x84,0x80);
        if(p.State!=0x0A||p.Y!=341||p.YFrac!=0x12||o.Slots[0].AnimNumber!=0x12)
            throw new System.Exception($"$D018 integrates before entering the drop state: {p.State:X2} {p.Y:X4}:{p.YFrac:X2} anim{o.Slots[0].AnimNumber:X2}");
        for(int i=0;i<17;i++)p.Step(4,0);
        if(p.State!=4||p.Y!=380||p.YFrac!=0xB7||p.YVelInt!=-1||p.YVelFrac!=0xAB||o.Slots[0].AnimNumber!=0x0B)
            throw new System.Exception("$D5E7 middle-probe guard must release after seventeen passes");
        return "PASS crouch A same-frame integration and seventeen-pass drop guard (human f18770-f18788)";
    }

    public static string QuackerjackKickContract()
    {
        foreach(byte row in new byte[]{0x30,0x90})
        {
            var o=new Darkwing.Core.ObjectSystem(null);
            var p=new Darkwing.Core.PlayerCore(new PushProbeMap()) {Objects=o,Y=(ushort)(0x100+row+4)};
            var c=new Darkwing.Core.EnemyDispatcher(new byte[16384],o,p);
            var s=o.Slots[14];s.Active=true;s.StateLow=0x81;s.Flags=0x90;s.Behavior=0x51;
            s.X=0xF60;s.Y=(ushort)(0x100+row);s.XVelInt=1;s.XVelFrac=0x80;
            Darkwing.Core.BossBehaviors.Reset();
            Darkwing.Core.EnemyBehaviors.RngE4=0;Darkwing.Core.EnemyBehaviors.RngE5=1;
            Darkwing.Core.BossBehaviors.Dispatch(14,s,0x51,c);
            bool up=row>=0x50;
            if(s.StateLow!=0x82||s.T4B0!=15||s.YVelInt!=(up?6:-1)||s.YVelFrac!=(up?0x8E:0xAB)||s.T480!=(up?0:255))
                throw new System.Exception("$98DA ADC must inherit CMP #$50 carry");
        }
        return "PASS Quackerjack kick carries both sides of CMP #$50 into RNG";
    }

    private sealed class LobProbeMap : Darkwing.Core.ITileMap
    {
        public byte Type;
        public System.Collections.Generic.List<int> Ys=new System.Collections.Generic.List<int>();
        public byte TypeAt(int x,int y){Ys.Add(y);return Type;}
    }

    public static string LobAndFlashContract()
    {
        int checks=0;
        void Check(bool ok,string label){if(!ok)throw new System.Exception(label);checks++;}
        var o=new Darkwing.Core.ObjectSystem(null);var p=new Darkwing.Core.PlayerCore(new PushProbeMap()){Objects=o,ScrollYRow=1};
        var map=new LobProbeMap();var c=new Darkwing.Core.EnemyDispatcher(new byte[16384],o,p){Tiles=map};
        var s=o.Slots[2];s.Active=true;s.StateLow=0x80;s.AnimNumber=0x16;s.Behavior=2;s.X=0xF80;s.Y=0x158;
        Darkwing.Core.EnemyBehaviors.Dispatch(2,s,2,c);
        Check(s.Y==0x155&&s.YVelInt==2&&s.YVelFrac==0xC0,"lob initial arc");
        var b7=File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        Check(map.Ys.Count==2&&map.Ys[0]==s.Y+(sbyte)b7[0x2CAD+0x14]&&map.Ys[1]==s.Y+(sbyte)b7[0x2CAD+0x15],"lob rising probes use $12 + count + 1");
        o.Slots[4].StateLow=0x83;o.Slots[3].StateLow=0x82;
        map.Type=3;s.YVelInt=-1;s.YVelFrac=0;
        Darkwing.Core.EnemyBehaviors.Dispatch(2,s,2,c);
        Check(s.AnimNumber==0x14,"lob lands into burst");
        foreach(int i in new[]{4,3})
            Check(o.Slots[i].Active&&o.Slots[i].StateLow==0x80&&o.Slots[i].AnimNumber==0x18&&o.Slots[i].Behavior==1&&o.Slots[i].BehaviorParam==1,"lob burst spawns real gas clouds and re-arms reused slots");
        Check(o.Slots[4].Direction==1&&o.Slots[3].Direction==2,"$841A split directions");
        Darkwing.Core.BossBehaviors.Reset();
        var boss=o.Slots[14];boss.Flags=0x90;
        Darkwing.Core.BossBehaviors.InvulnTimer=27;Darkwing.Core.BossBehaviors.ProjectileSlot=14;
        Darkwing.Core.BossBehaviors.HitFlash(c,boss);
        Check(boss.Flags==0x98&&Darkwing.Core.BossBehaviors.InvulnTimer==26,"three LSRs blink on bit 2");
        Darkwing.Core.BossBehaviors.HitFlash(c,o.Slots[15]);
        Check(Darkwing.Core.BossBehaviors.InvulnTimer==26,"only hit owner decrements the shared timer");
        Darkwing.Core.BossBehaviors.Reset();
        boss.Active=true;boss.Flags=0x90;boss.Behavior=0x51;boss.BehaviorParam=1;
        var cloud=o.Slots[4];cloud.Active=true;cloud.Flags=0x90;cloud.AnimNumber=0x18;cloud.Behavior=1;cloud.X=boss.X;cloud.Y=boss.Y;
        Check(Darkwing.Core.BossBehaviors.TakeProjectileHit(c,boss)&&cloud.AnimNumber==0x18&&cloud.Behavior==1&&Darkwing.Core.BossBehaviors.DamageAccum==2,"boss takes two damage without consuming persistent cloud");
        return "PASS "+checks+" checks: lob probe/burst/reused slots and boss flash owner";
    }

    private sealed class VictoryFloorMap : Darkwing.Core.ITileMap
    { public byte TypeAt(int x,int y)=>3; }

    // Static rendering/selection contract, not a mission acceptance replay. In particular,
    // bank-5 pickup IDs must not be substituted for bank-4 special player poses.
    public static string PlayerAnimationContextContract()
    {
        int checks=0, rendered=0;
        void Check(bool ok,string label){if(!ok)throw new System.Exception(label);checks++;}
        byte[] D(string n)=>File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/"+n+".bytes");
        byte[] b4=D("prg_bank4"),b5=D("prg_bank5"),b7=D("world_bank7");
        byte[] rom=File.ReadAllBytes(Root+"/Art/Darkwing Duck (USA).nes");
        using(var hash=System.Security.Cryptography.SHA256.Create())
            Check(System.BitConverter.ToString(hash.ComputeHash(rom)).Replace("-","").ToLowerInvariant()==
                "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00","ROM identity");
        foreach(int bank in new[]{4,5,7})
            Check(rom.Skip(16+bank*16384).Take(16384).SequenceEqual(bank==4?b4:bank==5?b5:b7),"exported PRG identity");
        Check(b7[0xF9DC-0xC000]==0x20&&b7[0xF9DD-0xC000]==0x29&&b7[0xF9DE-0xC000]==0xFA&&
              b7[0xF9DF-0xC000]==0x20&&b7[0xF9E0-0xC000]==0x78&&b7[0xF9E1-0xC000]==0xFA,
              "$F9DC calls $FA29 then player SetAnim $FA78");
        foreach(byte cp in new byte[]{0,8,12,9}) foreach(byte hazard in new byte[]{0,0x4D,0x5D})
        {
            var o=new Darkwing.Core.ObjectSystem(null);
            var p=new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance){Objects=o,CheckpointId=cp,State=0,InvulnFrames=0x3C};
            p.TakeHit(hazard);
            byte expected=cp==8&&hazard==0x4D?(byte)0x1D:cp==12&&hazard==0x5D?(byte)0x1C:(byte)9;
            Check(o.Slots[0].AnimNumber==expected&&p.State==6&&p.HitLadder==1,"$FA29 checkpoint/hazard selection");
        }
        foreach(byte state in new byte[]{4,0x0D,0x0E})
        {
            var o=new Darkwing.Core.ObjectSystem(null);
            var p=new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance){Objects=o,State=state,InvulnFrames=0x3C};
            o.Slots[0].AnimNumber=0x0C;p.TakeHit();
            Check(o.Slots[0].AnimNumber==0x0C&&p.SavedState==state,"held poses survive TakeHit");
        }
        var data=Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var decoder=new Darkwing.Core.RoomDecoder(D("prg_bank0"),D("prg_bank1"),b7,D("levelsets"));
        var sections=new Darkwing.Core.SectionTable(b7);
        Check(decoder.GetWorld(3).Grid[sections.StartCell(8)]==0x76&&sections.ChrRequest0(8)==0x8C,"section08 bank4/CHR12");
        Check(decoder.GetWorld(5).Grid[sections.StartCell(12)]==0xB5&&sections.ChrRequest0(12)==0x8F,"section0C bank4/CHR15");
        var game=new Darkwing.Core.DarkwingGame(data){Bank5=b5};
        game.Ppu.Chr0Bank=25;game.Player.ChrLatch0=0x8E;
        typeof(Darkwing.Core.DarkwingGame).GetMethod("NmiChrUpload",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(game,null);
        Check(game.Ppu.Chr0Bank==14&&game.Player.ChrLatch0==0,"NMI $8E selects CHR14 and consumes request");
        string output=Root+"/Art/Temp/player_animation_context";Directory.CreateDirectory(output);
        foreach(byte anim in new byte[]{1,3,4,5,6,7,8,9,11,12,13,14,15,17,18,28,29,43,45})
        {
            bool special=anim==28||anim==29;int section=anim==28?12:8;
            byte[] bank=special?b4:b5;
            int Ptr(int lo,int hi,int i)=>(bank[lo-0x8000+i]|bank[hi-0x8000+i]<<8)-0x8000;
            int entry=Ptr(special?0xB222:0xB429,special?0xB292:0xB4F0,anim);
            int count=(bank[entry]&127)+1;
            if(special)Check(bank[entry+2]==(anim==28?46:47),"bank4 uses player frames46/47, not pickups40/41");
            var objects=new Darkwing.Core.ObjectSystem(null);
            var player=new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance){Objects=objects};
            var ppu=new Darkwing.Core.Ppu(data){Mask=0x14,Chr0Bank=special?(sections.ChrRequest0(section)&31):14};
            ppu.PaletteRam[0]=0x3F;byte[] pal=new byte[16];
            if(special)sections.ReadSpritePalette(section,pal);else sections.ReadSpritePaletteByIndex(b7[0xDF22-0xC000],pal);
            System.Array.Copy(pal,0,ppu.PaletteRam,16,16);
            var oam=new Darkwing.Core.OamDispatcher(b5,objects,ppu,player);oam.SetBank4(b4);
            var slot=objects.Slots[0];slot.X=128;slot.Y=120;slot.Flags=0xC0;slot.AnimNumber=anim;
            byte[] pixels=new byte[256*240],rgba=new byte[count*48*48*4];
            for(int frame=0;frame<count;frame++)
            {
                slot.AnimFrame=(byte)frame;slot.AnimTimer=0x80;oam.UseBank4Tables=special;
                oam.BeginFrameWalk(0);oam.FinishWalk();oam.PublishToOam();ppu.Render(pixels);
                Check(slot.Active&&slot.AnimFrame==frame,"static walk keeps frame and active slot");
                for(int y=0;y<48;y++)for(int x=0;x<48;x++)
                {
                    byte index=pixels[(97+y)*256+104+x];if(index==0x3F)continue;
                    var color=Darkwing.UnityApp.NesPalette.Table[index&63];int dest=(y*(count*48)+frame*48+x)*4;
                    rgba[dest]=color.r;rgba[dest+1]=color.g;rgba[dest+2]=color.b;rgba[dest+3]=255;
                }
                rendered++;
            }
            File.WriteAllBytes(output+"/anim_"+anim.ToString("X2")+".rgba",rgba);
        }
        return $"PASS {checks} checks; {rendered} native OAM/PPU frames exported as top-down RGBA for GDD comparison; diagnostic fixtures only";
    }

    public static string BossTailContract()
    {
        int checks=0;
        void Check(bool ok,string label){if(!ok)throw new System.Exception(label);checks++;}
        var o=new Darkwing.Core.ObjectSystem(null);var p=new Darkwing.Core.PlayerCore(new VictoryFloorMap()){Objects=o,State=1,X=0xF68,Y=0x14E,ScrollYRow=1};
        var c=new Darkwing.Core.EnemyDispatcher(new byte[16384],o,p);
        o.Slots[0].Active=true;o.Slots[0].AnimNumber=0x12;o.Slots[0].StateLow=0x81;
        var boss=o.Slots[14];boss.Active=true;boss.StateLow=0x82;boss.Behavior=0x51;boss.Flags=0x98;boss.BehaviorParam=12;boss.Y=0x17F;
        int clears=0,sounds=0;c.BossDefeated=()=>clears++;c.Sfx=id=>{if(id==0x1E)sounds++;};
        Darkwing.Core.BossBehaviors.DamageAccum=17;
        Darkwing.Core.BossBehaviors.HitFlash(c,boss);
        Check(p.State==0x0F&&boss.Active&&boss.StateLow==0x80&&boss.Behavior==0x4F&&boss.BehaviorParam==0&&o.Slots[0].StateLow==0x80,"$B315 retasks live body and re-arms player");
        Check(clears==0,"boss hit must not call mission clear");
        Darkwing.Core.BossBehaviors.Dispatch(14,boss,0x4F,c);
        Check(p.State==0x0F&&boss.Y==0x17B&&boss.YVelInt==3&&boss.YVelFrac==4&&boss.XVelFrac==0x80,"defeat pop uses gravity and low-byte exit test");
        Darkwing.Core.BossBehaviors.Dispatch(14,boss,0x4F,c);
        Check(sounds==1,"pop sound runs once");
        boss.Y=0x1DF;boss.YFrac=0;boss.YVelInt=-1;boss.YVelFrac=0;
        Darkwing.Core.BossBehaviors.Dispatch(14,boss,0x4F,c);
        Check(p.State==0x15,"pop exits only at low Y >= E0");
        o.Slots[0].StateLow=0x80;p.Y=0x1D4;p.YFrac=0;p.YVelInt=-1;p.YVelFrac=0;
        int music=-1;p.Sound=id=>music=id;p.Step(0,0);
        Check(o.Slots[0].AnimNumber==0x2D&&p.ChrLatch0==0x8E&&music==2&&p.Y==0x1D4,"$DA04 lands into real victory animation/music");
        o.Slots[0].AnimFrame=0x2B;o.Slots[0].AnimTimer=4;p.Step(0,0);
        Check(p.YieldOnlyFrame&&p.State==0x15,"victory starts pure FEFF hold");
        for(int i=0;i<55;i++)p.Step(0,0);
        Check(p.YieldOnlyFrame&&p.State==0x15,"hold lasts 56 yields");
        p.Step(0,0);Check(!p.YieldOnlyFrame&&p.State==0x11,"native D838 dispatch follows the hold");
        return "PASS "+checks+" checks: B315/B3F8/DA04 native boss tail (diagnostic fixture, not route proof)";
    }

    public static string SceneDescriptorContract()
    {
        byte[] D(string n)=>File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/"+n+".bytes");
        var data=Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var g=new Darkwing.Core.DarkwingGame(data){Bank5=D("prg_bank5"),Bank7=D("world_bank7"),Bank2=D("prg_bank2")};
        var decoder=new Darkwing.Core.RoomDecoder(D("prg_bank0"),D("prg_bank1"),D("world_bank7"),D("levelsets"));
        g.BeginSceneLoad(decoder,8);
        if(g.Player.CellIndex!=2)throw new System.Exception("$DD7B scene cell must remain the descriptor's 2, though scroll is 0");
        int frames=0;while(!g.TickSceneLoad()&&++frames<50){}
        if(frames>=50||g.Player.CellIndex!=2||g.Player.CellIndexNext!=3)
            throw new System.Exception("staging must finish once, without a second full cursor wrap");
        g.LoadSection(2);var idle=default(Darkwing.Core.NesButtons);bool applied=false;
        for(int f=0;f<40;f++){g.Step(in idle);if(g.Ppu.Chr0Bank==10&&g.Ppu.Chr1Bank==16){applied=true;break;}}
        if(!applied)throw new System.Exception("render-on NMI must apply $1A=8A/$1B=90 before clearing the latches");
        return "PASS native scene descriptor cell, one staging completion, and real section CHR upload";
    }

    public static string OffscreenAnimationContract()
    {
        byte[] D(string n)=>File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/"+n+".bytes");
        var data=Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var g=new Darkwing.Core.DarkwingGame(data){Bank5=D("prg_bank5")};
        g.Player.ScrollX=0x180;g.Player.ScrollYRow=3;
        var s=g.Objects.Slots[14];s.Active=true;s.StateLow=0x80;s.Flags=0x90;
        s.X=0x178;s.Y=0x3B8;s.AnimNumber=0x1C;
        g.Oam.RunFrame(1);
        if(!s.Active||s.Flags!=0x10||s.AnimTimer!=0||s.AnimFrame!=0)
            throw new System.Exception("$80C6 offscreen-surviving object must return before $8100");
        g.Player.ScrollX=0x100;g.Oam.RunFrame(2);
        if(s.Flags!=0x90||s.AnimTimer!=1)
            throw new System.Exception("onscreen negative control must resume animation");
        var player=g.Objects.Slots[0];player.Active=true;player.StateLow=0x80;player.Flags=0x90;
        player.X=0x178;player.Y=0x2E0;player.AnimNumber=0x11;
        g.Oam.RunFrame(3);
        if((player.Flags&0x80)!=0||player.AnimTimer!=1)
            throw new System.Exception("$806E player above the camera still reaches animation at $80E1");
        return "PASS side-offscreen animation freeze, visible resume, and player-above-camera exception";
    }

    private sealed class AirPoseMap : Darkwing.Core.ITileMap
    { public byte TypeAt(int x,int y)=>(byte)(x==189?2:0); }

    public static string PlayerPoseContract()
    {
        int checks=0;void Check(bool ok,string label){if(!ok)throw new System.Exception(label);checks++;}
        foreach(byte sub in new byte[]{0,2})
        {
            var o=new Darkwing.Core.ObjectSystem(null);
            var p=new Darkwing.Core.PlayerCore(new AirPoseMap()){Objects=o,State=1,X=200,Y=884,ScrollYRow=3,YVelInt=3,YVelFrac=0x25,AirSubState=sub};
            o.Slots[0].AnimNumber=sub==0?(byte)0x11:(byte)0x0E;
            p.Step(0x80,0);
            Check(p.YVelInt>=0&&o.Slots[0].AnimNumber==(sub==0?0x12:0x0F),"$CD82 probe class 2 selects fall pose even with rising velocity");
        }
        var objects=new Darkwing.Core.ObjectSystem(null);
        var hang=new Darkwing.Core.PlayerCore(new PushProbeMap()){Objects=objects,State=0x0D};
        var s=objects.Slots[0];s.AnimNumber=0x0C;s.AnimFrame=0;s.AnimTimer=0;
        hang.Step(0,0);Check(s.AnimNumber==0x0C,"$D1C0 keeps the hanging shot until its last frame");
        s.AnimFrame=2;s.AnimTimer=4;hang.Step(0,0);
        Check(s.AnimNumber==0x0B&&s.AnimFrame==0&&s.AnimTimer==0,"$D1C5/$D1CE release the completed hanging shot");
        return "PASS "+checks+" checks: rising class-2 contact and hanging firing pose";
    }

    public static string ResidentReturnContract()
    {
        byte[] D(string n)=>File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/"+n+".bytes");
        var data=Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var g=new Darkwing.Core.DarkwingGame(data){Bank5=D("prg_bank5"),WorldRunning=false};
        var p=g.Player;p.State=1;p.Y=0x360;p.YVelInt=-1;p.YVelFrac=0;p.ScrollYRow=3;
        g.Objects.Slots[0].Active=true;g.Objects.Slots[0].StateLow=0x80;g.Objects.Slots[0].AnimNumber=0x12;
        g.Ppu.Ctrl=0x90;g.Ppu.Mask=0x1E;
        g.ResidentProcess=pad=>{g.ResidentProcess=null;g.WorldRunning=true;};
        var idle=default(Darkwing.Core.NesButtons);g.Step(in idle);
        if(p.Y!=0x361||g.FrameParity!=1||g.Frame!=1||g.NmiCounter!=1)
            throw new System.Exception("resident RTS must reach the same player/engine pass with one NMI and one frame");
        return "PASS resident return continues player/engine in the same frame, without another NMI";
    }

    public static string CompletedMapContract()
    {
        byte[] D(string n)=>File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/"+n+".bytes");
        var data=Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var g=new Darkwing.Core.DarkwingGame(data){Bank5=D("prg_bank5"),Bank7=D("world_bank7"),Bank2=D("prg_bank2"),MapPalette=D("map_palette"),MapChrBank0=31,MapChrBank1=26};
        var decoder=new Darkwing.Core.RoomDecoder(D("prg_bank0"),D("prg_bank1"),D("world_bank7"),D("levelsets"));
        var flow=new Darkwing.Core.GameFlow(g,decoder);flow.World.CompletedCount=1;g.Objects.Slots[2].Behavior=2;
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        typeof(Darkwing.Core.GameFlow).GetMethod("BeginResidentMap",flags).Invoke(flow,new object[]{false});
        if(flow.World.CompletedMask!=1||flow.World.CompletedCount!=1||g.Objects.Slots[2].AnimNumber!=0xBD||g.Objects.Slots[2].Behavior!=2)
            throw new System.Exception("clear map sets progress and preserves unwritten object bytes");
        typeof(Darkwing.Core.GameFlow).GetMethod("SelectMapBubble",flags).Invoke(flow,null);
        if(flow.World.StreamId!=16||g.Objects.Slots[2].AnimNumber!=0xC4||flow.BubbleAvailable(0))
            throw new System.Exception("cleared selected bubble uses C4 and already-cleared stream 16");
        flow.World.CursorBubble=1;typeof(Darkwing.Core.GameFlow).GetMethod("SelectMapBubble",flags).Invoke(flow,null);
        if(flow.World.StreamId!=1||g.Objects.Slots[2].AnimNumber!=0xBD||!flow.BubbleAvailable(1))
            throw new System.Exception("uncleared bubble remains selectable");
        g.Player.X=0x100;g.Player.Y=0x300;g.Player.ResetHud();g.Oam.HudActive=true;g.ResidentWalk();
        if(g.Objects.Slots[1].ScreenX!=24||g.Objects.Slots[1].ScreenY!=28)
            throw new System.Exception("HUD still executes the absolute-position update");
        return "PASS cleared-map progression, C4 selection, preserved behavior, and HUD screen position (diagnostic fixture)";
    }

    public static string Mission1Contracts()
    {
        return string.Join("\n",new[]{G92Exit(),HorizontalPushContract(),PauseActivityContract(),DeathSoundContract(),AirShotContract(),PickupContract(),WeaponSwitchContract(),
            CrouchDropContract(),QuackerjackKickContract(),LobAndFlashContract(),BossTailContract(),
            SceneDescriptorContract(),OffscreenAnimationContract(),PlayerPoseContract(),
            ResidentReturnContract(),CompletedMapContract()});
    }

    public static string Mission1Regressions()
    {
        var sb=new System.Text.StringBuilder();
        foreach(var test in new System.Func<string>[]{M1Trench,M1Cling,M1Climb})
        {
            string tail=VerdictTail(test());sb.AppendLine(tail);
            if(!tail.Contains("RESULT: MATCH")||!tail.Contains("STRICT: MATCH"))
                throw new System.Exception("Mission-1 regression: "+tail);
        }
        return sb.ToString();
    }

    public static string Mission23Contracts()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new System.Exception(why); }
        // ROM bank6 $8483/$84A8 are tail JMPs. They must neither probe nor kill
        // the bolt after the integrator. The two $84BA outcomes differ as well.
        foreach (var test in new[] {
            (scroll: (byte)4, x: (ushort)0xF0, right: true, playerX: (ushort)0x30, alive: true, end: (ushort)0xF1, probes: false),
            (scroll: (byte)4, x: (ushort)0x0F, right: false, playerX: (ushort)0x30, alive: true, end: (ushort)0x0E, probes: false),
            (scroll: (byte)0, x: (ushort)0x0F, right: false, playerX: (ushort)0x29, alive: true, end: (ushort)0x0E, probes: false),
            (scroll: (byte)0, x: (ushort)0x0F, right: false, playerX: (ushort)0x2A, alive: false, end: (ushort)0x0F, probes: false),
            (scroll: (byte)4, x: (ushort)0x80, right: true, playerX: (ushort)0x30, alive: true, end: (ushort)0x81, probes: true) })
        {
            var map = new PushProbeMap();
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(map) { Objects = objects, X = test.playerX, ScrollState = test.scroll, ScrollYRow = 0 };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player) { Tiles = map };
            var bolt = objects.Slots[3];
            bolt.Active = true; bolt.StateLow = 0x81; bolt.AnimNumber = 0x17;
            bolt.Direction = (byte)(test.right ? 1 : 2); bolt.X = test.x; bolt.XVelInt = 1; bolt.Y = 0x80;
            Darkwing.Core.EnemyBehaviors.Dispatch(3, bolt, 3, ctx);
            Check(bolt.Active == test.alive && bolt.X == test.end, "Thunder tail/left-edge branch");
            Check((map.Reads > 0) == test.probes, "Thunder tail must not continue through the wall probe");
        }
        byte[] D(string n) => File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/" + n + ".bytes");
        var data = Darkwing.Core.DataLoader.Load(D("chr_full"),D("levelsets"),D("screens"),D("levels"),D("anims"));
        var game = new Darkwing.Core.DarkwingGame(data) { Bank5 = D("prg_bank5"), Bank4 = D("prg_bank4") };
        game.Oam.UseBank4Tables = true;
        Check(game.Oam.AnimHeader(0x6B).duration == 4, "Arena uses bank4 animation duration");
        game.Oam.RunFrame(1);
        Check(!game.Oam.UseBank4Tables && game.Oam.AnimHeader(0x6B).duration == 0x10,
            "Resident bank5 walk must not inherit arena tables");
        foreach (bool right in new[] { false, true })
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance) { Objects = objects };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player) { Tiles = Darkwing.Core.EmptyTileMap.Instance };
            var boss = objects.Slots[15];
            boss.Active = true; boss.StateLow = 0x82; boss.Behavior = 0x57; boss.AnimNumber = 0x41;
            boss.X = 0xF30; boss.XVelInt = 1; boss.Direction = (byte)(right ? 1 : 2);
            Darkwing.Core.BossBehaviors.Dispatch(15, boss, 0x57, ctx);
            Check(boss.X == (right ? 0xF31 : 0xF2F), "Liquidator F24B/F2A6 face + exactly one X integration");
        }
        {
            var map = new LobProbeMap { Type = 3 };
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(map) { Objects = objects, GravityFrac = 0x40, ScrollYRow = 0 };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player) { Tiles = map };
            var crawler = objects.Slots[12];
            crawler.Active = true; crawler.StateLow = 0x86; crawler.Behavior = 0x37;
            crawler.X = 0x129; crawler.Y = 228; crawler.YFrac = 120;
            crawler.YVelInt = -5; crawler.YVelFrac = 43; crawler.XVelInt = 2; crawler.Direction = 2;
            Darkwing.Core.EnemyBehaviors.Dispatch(12, crawler, 0x37, ctx);
            Check(crawler.Y == 233 && crawler.T480 == 0 && crawler.YVelInt == -6 && crawler.YVelFrac == 235,
                "Original m3 poll3170->3171: no landing on the next viewport's floor");
        }
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance) { Objects = objects };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player);
            var geyser = objects.Slots[14];
            geyser.Active = true; geyser.StateLow = 0x81; geyser.Behavior = 0x59;
            geyser.Y = 0x230; geyser.T4A0 = 0x38; geyser.YVelInt = 1;
            Darkwing.Core.BossBehaviors.Dispatch(14, geyser, 0x59, ctx);
            Check(geyser.StateLow == 0x82, "Geyser A0BB increments state 1->2, not OR2->3");
        }
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance) { Objects = objects, X = 0x2A0 };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player);
            var dropper = objects.Slots[15];
            dropper.Active = true; dropper.StateLow = 0x80; dropper.Behavior = 0x2D;
            dropper.X = 0x2A8; dropper.Y = 0x160; dropper.AnimNumber = 0x7E; dropper.T490 = 0x5A;
            Darkwing.Core.EnemyBehaviors.Dispatch(15, dropper, 0x2D, ctx);
            Check(dropper.StateLow == 0x82 && dropper.T480 == 0x1E && dropper.T490 == 0x5A && dropper.BehaviorParam == 0x57,
                "Dropper A53E init+arm: INC twice, timer1E, no write to0490");
            dropper.AnimFrame = 0x23; dropper.AnimTimer = 4; dropper.Flags = 0xD0;
            Darkwing.Core.EnemyBehaviors.Dispatch(15, dropper, 0x2D, ctx);
            var children = objects.Slots.Where(s => s.Active && s.Behavior == 6).ToArray();
            Check(children.Length == 2 && dropper.AnimNumber == 0x7F, "Dropper A592/A5BF emits two children");
            Check(children.All(s => s.X == 0x288 && s.Y == 0x15E && s.StateLow == 0x81 && s.AnimNumber == 0x80 &&
                s.BehaviorParam == 0x0A && s.YVelInt == 3 && s.YVelFrac == 0x44 && s.XVelInt == 0 && s.XVelFrac == 0x80),
                "Dropper native child coordinates, velocity and hitbox");
            Check(children.Select(s => s.Direction).OrderBy(d => d).SequenceEqual(new byte[] { 5, 6 }), "Dropper child direction pair");
            dropper.AnimFrame = 0x0A; dropper.AnimTimer = 2;
            Darkwing.Core.EnemyBehaviors.Dispatch(15, dropper, 0x2D, ctx);
            Check(dropper.AnimNumber == 0x7E, "Dropper A59F animation7F returns to7E");
        }
        {
            var objects = new Darkwing.Core.ObjectSystem(null);
            var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance) { Objects = objects, ScrollYRow = 1, GravityFrac = 0x40 };
            var ctx = new Darkwing.Core.EnemyDispatcher(null, objects, player) { Tiles = Darkwing.Core.EmptyTileMap.Instance };
            var jumper=objects.Slots[14];jumper.Active=true;jumper.StateLow=0x81;jumper.Behavior=0x3B;
            jumper.X=0x34C;jumper.Y=0x194;jumper.AnimNumber=0x4F;jumper.YVelInt=4;jumper.T480=1;jumper.Direction=2;
            Darkwing.Core.EnemyBehaviors.Dispatch(14,jumper,0x3B,ctx);
            Check(jumper.T480==1&&jumper.AnimNumber==0x50&&jumper.Y==0x190,"Jumper A689 airborne return preserves landing counter");
        }
        return $"PASS {checks}/{checks} Thunder, arena-bank lifetime, Liquidator movement, viewport probes, Dropper and Jumper";
    }

    /// <summary>Explicit diagnostic fixtures for the successful $D6B4 -> $D709 exit.
    /// Runs the public dispatcher with RIGHT+B so fallthrough is observable in both
    /// movement and fire. This is branch coverage, not a natural-route golden.</summary>
    public static string AirShotContract()
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new System.Exception(label); checks++; }
        Darkwing.Core.PlayerCore Setup(out Darkwing.Core.ObjectSystem objects)
        {
            objects = new Darkwing.Core.ObjectSystem(null);
            objects.Slots[0].AnimNumber = 0x11;
            return new Darkwing.Core.PlayerCore(new PushProbeMap()) {
                Objects=objects, State=1, X=302, Y=804, YFrac=0x9C,
                YVelInt=0, YVelFrac=0xE5, Flags=0xD0, DirBits=1 };
        }
        // Human original f4454 -> f4455: B replaces A with the player still rising.
        var p=Setup(out var o);
        p.Step(0x40,0x40);
        Check(p.Y==803 && p.YFrac==0xB7 && p.YVelInt==0 && p.YVelFrac==0xA5,"CE39 preserves player rise");
        Check(p.HasPendingFire && p.PendingFire.CutYVelocity,"F5FC targets allocated projectile");
        Check(o.Slots[0].AnimNumber==0x0E,"rising firing pose");
        var dispatcher=new Darkwing.Core.EnemyDispatcher(new byte[16384],o,p);
        dispatcher.SpawnWeapon(p.PendingFire);p.HasPendingFire=false;
        Check(o.Slots[4].YVelInt==-1 && o.Slots[4].YVelFrac==0xAB,"projectile receives indexed cut");
        p.Step(0x40,0);
        Check(p.Y==804 && p.YFrac==0x0C && p.YVelInt==-1 && p.YVelFrac==0x6B,"next frame cuts player");
        Check(o.Slots[0].AnimNumber==0x0F,"falling firing pose");
        var control=Setup(out _);control.Step(0x40,0);
        Check(control.Y==804 && control.YFrac==0xF1 && control.YVelInt==-1,"without a fresh shot release cuts player");
        return "PASS "+checks+" checks: original f4454/f4455 shot contract and negative control";
    }

    public static string PickupContract()
    {
        int checks=0;
        foreach(byte anim in new byte[]{0x22,0x23})
        foreach(byte damage in new byte[]{0,1,3})
        {
            var o=new Darkwing.Core.ObjectSystem(null);
            var p=new Darkwing.Core.PlayerCore(new PushProbeMap()) {
                Objects=o, X=0x100,Y=0x100, Flags=0xD0, HitLadder=damage, WeaponSelected=3 };
            var ctx=new Darkwing.Core.EnemyDispatcher(new byte[16384],o,p);
            var s=o.Slots[2];s.Active=true;s.Flags=0x80;s.Behavior=8;s.BehaviorParam=0x40;
            s.AnimNumber=anim;s.X=p.X;s.Y=p.Y;
            Darkwing.Core.EnemyBehaviors.Dispatch(2,s,8,ctx);
            byte expected=anim==0x22?(byte)0:(byte)System.Math.Max(0,damage-1);
            if(s.Active||p.HitLadder!=expected||p.ActiveGasEnergyLo!=0||p.ActiveGasEnergyHi!=0||p.WeaponActive!=0)
                throw new System.Exception("$89D6 health pickup altered gas or failed to heal");
            checks++;
        }
        Darkwing.Core.EnemyBehaviors.LoadTypeTable(File.ReadAllBytes(Root+"/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes"));
        Darkwing.Core.EnemyBehaviors.TryGetType(9,out _,out _,out _,out _,out byte hp);
        if(hp!=0x83)throw new System.Exception("$8175 strided HP for placed frog type 9");
        return "PASS "+(checks+1)+" checks: repair pickups preserve inactive gas; native frog HP83";
    }

    public static string WeaponSwitchContract()
    {
        int checks=0;
        void Check(bool ok,string label){if(!ok)throw new System.Exception(label);checks++;}
        var g=new Darkwing.Core.DarkwingGame();var p=g.Player;
        g.Objects.Slots[0].Active=true;g.Objects.Slots[0].StateLow=0x80;
        p.State=0x0F;p.LevelActive=0;p.WeaponSelected=1;p.GasEnergyHi=0x74;p.GasEnergyLo=0x70;
        var select=Darkwing.Core.NesButtons.FromByte(0x20);var idle=default(Darkwing.Core.NesButtons);
        g.Step(in select);
        Check(p.WeaponActive==1&&p.GasActive==1&&p.GasToggle==1&&p.ActiveGasEnergyHi==0x74&&p.ActiveGasEnergyLo==0x70,"Select equips saved weapon/meter");
        g.Step(in select);Check(p.GasActive==1&&p.GasToggle==1,"held Select has no second edge");
        p.DrainActiveMeter(0xFE);
        Check(p.ActiveGasEnergyHi==0x73&&p.ActiveGasEnergyLo==0x78,"CLC/SBC FE drains two units");
        g.Step(in idle);g.Step(in select);
        Check(p.WeaponActive==0&&p.GasActive==0&&p.ActiveGasEnergyLo==0&&p.GasEnergyHi==0x73&&p.GasEnergyLo==0x78,"Select stows remaining meter");
        p.GasEnergyHi=0x74;p.GasEnergyLo=0x70;p.AddSavedMeter(0xFE);
        Check(p.GasEnergyHi==0x73&&p.GasEnergyLo==0x78,"saved meter uses the same borrow");
        g.Step(in idle);p.State=9;g.Step(in select);
        Check(p.WeaponActive==0&&p.GasToggle==0,"mission entry guards Select");
        g.Step(in idle);p.State=0x0F;p.GasToggle=p.GasActive=1;p.WeaponActive=p.WeaponSelected=2;
        p.GasEnergyLo=p.ActiveGasEnergyLo=0x70;p.ActiveGasEnergyHi=0x73;
        foreach(int i in new[]{2,3,4})
        {
            var bolt=g.Objects.Slots[i];bolt.Active=true;bolt.StateLow=0x82;
            bolt.Behavior=3;bolt.AnimNumber=0x29;bolt.Flags=0x90;bolt.BehaviorParam=0x84;
            bolt.X=0x2C;bolt.Y=0x1B1;bolt.T480=0x51;bolt.YVelFrac=1;
        }
        g.Step(in select);
        Check(g.Objects.Slots[2].Active&&g.Objects.Slots[3].Active&&g.Objects.Slots[2].StateLow==0x83&&g.Objects.Slots[3].StateLow==0x83,
            "$CAE7 Select stows Thunder by releasing live bolts, not deleting them");
        Check(g.Objects.Slots[4].Active&&g.Objects.Slots[4].StateLow==0x82,"$CAF1 does not write slot4");
        return "PASS "+checks+" checks: $C9B3 Select and indexed meter subtraction";
    }

    public static string G92Exit()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new System.Exception("G92: " + name); checks++; }
        foreach (bool scan in new[] { false, true })
        foreach (ushort y in new ushort[] { 0x0360, 0x0300 })
        {
            var map = new PushProbeMap();
            var objects = new Darkwing.Core.ObjectSystem(null);
            var p = new Darkwing.Core.PlayerCore(map) {
                Objects = objects, State = 0x0B, AirSubState = 2,
                X = 0x0100, Y = y, YVelInt = -1, YVelFrac = 0, Flags = 0xD0
            };
            var support = objects.Slots[scan ? 2 : 1];
            support.Active = true; support.Flags = 0x80; support.Behavior = 1;
            support.AnimNumber = 1; support.BehaviorParam = 0x87;
            support.X = p.X; support.Y = (ushort)(y + 8);
            p.Step(0x41, 0x40);
            Check(p.State == 0, "successful support branch reached");
            Check(p.X == 0x0100 && p.XFrac == 0, "RTS suppresses steering");
            Check(!p.HasPendingFire, "RTS suppresses airborne fire");
            Check(map.Reads == 3, $"only initial floor probes; no hook/side probe (expected 3, observed {map.Reads})");
            Check(p.Y == (y == 0x0300 ? 0x02ED : 0x035D), "lift clamp and 240px row borrow");
            Check(p.AirSubState == 2, "$56 preserved");
            // Same geometry without an active support must reach normal airborne control.
            support.Active = false; p.State = 0x0B; p.X = 0x0100; p.Y = y;
            p.YVelInt = -1; p.YVelFrac = 0; map.Reads = 0;
            p.Step(0x41, 0x40);
            Check(p.State == 0x0B && p.X > 0x0100 && p.HasPendingFire,
                "negative control reaches steering and fire");
        }
        // Class 7 is taller than it is wide ($FBE9=28, $FC09=16). The exact Y edge
        // must accept a zero lift; using the X half-size here wrongly rejects it.
        var edgeMap = new PushProbeMap();
        var edgeObjects = new Darkwing.Core.ObjectSystem(null);
        var edge = new Darkwing.Core.PlayerCore(edgeMap) {
            Objects = edgeObjects, State = 0x0B, X = 0x0100, Y = 0x0360,
            YVelInt = -1, Flags = 0xD0
        };
        var tall = edgeObjects.Slots[2];
        tall.Active = true; tall.Flags = 0x80; tall.BehaviorParam = 0x87;
        tall.X = edge.X; tall.Y = 0x037D;
        edge.Step(0x41, 0x40);
        Check(edge.State == 0 && edge.Y == 0x0361 && edge.X == 0x0100 && !edge.HasPendingFire,
            "tall support Y edge accepts zero lift");
        return $"PASS G92 successful pair+scan exits, row borrow, tall Y edge and negative controls: {checks} checks";
    }

    public static string AuditSelfTests()
    {
        int count = 0;
        void Check(bool condition, string label) { if (!condition) throw new System.Exception("FAILED: " + label); count++; }
        void Reject(System.Action a, string label)
        {
            bool rejected = false;
            try { a(); } catch (System.IO.InvalidDataException) { rejected = true; }
            Check(rejected, label);
        }
        Reject(() => ValidateRecords(256, 2, 256, 0, "OAM"), "missing OAM frame");
        Reject(() => ValidateRecords(4095, 2, 2048, 0, "RAM"), "partial RAM record");
        Reject(() => ValidateRecords(4096, 3, 2048, 0, "RAM"), "whole-record truncation");
        Reject(() => ValidateRecords(2048, 1, 2048, 0, "RAM"), "zero compared frames");
        Reject(() => ValidateRecords(4096, 2, 2048, 1, "RAM"), "seed at EOF");
        ValidateRecords(4096, 2, 2048, 0, "RAM"); count++;
        string tail = "RESULT: MATCH 1/1 " + new string('x', 650) + " STRICT: first divergence f1 ENEMIES: Yhi";
        Check(VerdictTail(tail).EndsWith("ENEMIES: Yhi"), "summary keeps trailing errors beyond 500 chars");
        Reject(() => VerdictTail("no verdict"), "missing verdict");
        var a = new byte[2048]; var b = new byte[2048];
        bool Equal() => SlotsEqual(a, b, out _, out _, out _, out _);
        a[0x4D1] = 0x80; Check(!Equal(), "extra port object");
        a[0x4D1] = 0; b[0x4D1] = 0x80; Check(!Equal(), "missing port object");
        a[0x4D1] = 0x80; Check(Equal(), "same active slot");
        a[0x511] = 1; Check(!Equal(), "Y low mismatch"); a[0x511] = 0;
        a[0x521] = 1; Check(!Equal(), "Y high mismatch"); a[0x521] = 0;
        a[0x4D1] = b[0x4D1] = 0;
        a[0x4D2] = b[0x4D3] = 0x80; Check(!Equal(), "different slots do not rematch by proximity");
        string result = $"PASS {count} targeted harness checks";
        File.WriteAllText(Root + "/Art/Temp/audit_selftests.txt", result);
        return result;
    }

    /// <summary>
    /// Damage/knockback golden: `gold_hurt_a41` is mission 1 where the capture patches the
    /// player onto slot 13 (beh $0C, anim $41 => $814F C9 3F BCS passes, so this enemy DOES
    /// hurt) at capture frame 174; frame 173 is therefore already recorded with the patched
    /// position and is the seed. The original detects contact inside frame 174 ($50 06, $51 0F,
    /// $7C 3B, anim $09 via $FA78) and then runs the state-6 handler, whose knockback drift is
    /// $0540/$0530 = 00,80 — i.e. $F9C2 does NOT zero the velocity.
    /// </summary>
    public static string Hurt()
    {
        string root = Root;
        byte[] ramAll = File.ReadAllBytes(root + "/Art/Research/DarkwingDuck/reference/gold_hurt_a41/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        ValidateCapture(root + "/Art/Research/DarkwingDuck/reference/" + "gold_hurt_a41", ramAll, 173);
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        game.Bank6 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank6.bytes");
        game.ObjectTypeTable = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes");
        game.Bank7 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
            game.Bank7,
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld(0);
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, world0);
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();

        const int seed = 173;                 // recorded WITH the teleport already applied
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
        p.StateTimer = ram0[0x51];
        p.SavedState = ram0[0x58];
        p.InvulnFrames = ram0[0x7C];
        p.DeathSoundGate = ram0[0x9B];
        // $F9C2's heart ladder is not a counter but the player's own RAM metasprite tile bytes:
        // the $C8CF template leaves $05B2=$80, $05B4=$80, $05B6=$81, $05B8=$81, and each survived
        // hit marks the next one ($05B4→$82, $05B8→$83, $05B6→$83); the 4th marks $05B2→$82 and
        // writes $50=$07. Mid-mission seeds (gold_m*_route) start at up to 3 marks, so without
        // this the port takes the 4th hit as a 1st and stays in state $06 where the ROM dies.
        p.HitLadder = (byte)(ram0[0x5B2] != 0x80 ? 4                       // $FA11: lethal hit landed
                             : (ram0[0x5B4] != 0x80 ? 1 : 0) + (ram0[0x5B8] != 0x81 ? 1 : 0) +
                               (ram0[0x5B6] != 0x81 ? 1 : 0));
        p.DirBits = ram0[0x52];
        // $95 increments in the NMI before the frame's logic, so the pre-Step value
        // is the seed frame's capture and Step() produces frame f's value.
        game.FrameParity = ram0[0x95];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.ColsToStream = ram0[0x1F];   // $1F only changes through $E66C, so seed the captured value
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        // Bulk-seed every object slot table the dispatcher reads ($0400..$05A0, 16 bytes/slot).
        for (int s = 0; s < 16; s++)
        {
            var sl = game.Objects.Slots[s];
            sl.Flags = ram0[0x400 + s];
            sl.AnimFrame = ram0[0x410 + s];
            sl.AnimNumber = ram0[0x420 + s];
            sl.AnimTimer = ram0[0x430 + s];
            sl.ScreenY = ram0[0x440 + s];
            sl.ScreenX = ram0[0x450 + s];
            sl.YFrac = ram0[0x460 + s];
            sl.XFrac = ram0[0x470 + s];
            sl.T480 = ram0[0x480 + s];
            sl.T490 = ram0[0x490 + s];
            sl.T4A0 = ram0[0x4A0 + s];
            sl.T4B0 = ram0[0x4B0 + s];
            sl.HitFlash = ram0[0x4C0 + s];
            sl.StateLow = ram0[0x4D0 + s];
            sl.Active = (ram0[0x4D0 + s] & 0x80) != 0;
            sl.Behavior = ram0[0x4E0 + s];
            sl.X = (ushort)(ram0[0x4F0 + s] | (ram0[0x500 + s] << 8));
            sl.Y = (ushort)(ram0[0x510 + s] | (ram0[0x520 + s] << 8));
            sl.XVelFrac = ram0[0x530 + s];
            sl.XVelInt = ram0[0x540 + s];
            sl.YVelFrac = ram0[0x550 + s];
            sl.YVelInt = (sbyte)ram0[0x560 + s];
            sl.BehaviorParam = ram0[0x570 + s];
            sl.Direction = ram0[0x580 + s];
            sl.PlacementIndex = ram0[0x590 + s];
            sl.Health = ram0[0x5A0 + s];
        }
        map.LoadRow(p.ScrollYRow);
        map.StreamColumns(p.ScrollX);
        var dbg = new System.Text.StringBuilder("TYPES ");
        for (int r = 0; r < 16; r++)
        {
            dbg.Append($"r{r:X}:");
            for (int q = 0; q < 16; q++) dbg.Append(map.TypeAt(0x108 + q * 16, 0x308 + r * 16).ToString("X"));
            dbg.Append(" ");
        }
        var names = new[] { "$50 state", "$51 timer", "$7C invuln", "$0420 anim", "$0410 aframe",
                            "$0430 atimer", "X", "Y", "$0540/$0530 vx", "$0560/$0550 vy",
                            "$0460 ffc", "$0400 flags", "13:$04D0", "13:X", "13:Y", "13:$0420" };
        var ok = new int[names.Length];
        var firstBad = new int[names.Length];
        for (int i = 0; i < names.Length; i++) firstBad[i] = -1;
        var sb = new System.Text.StringBuilder("[hurt_a41] " + dbg);
        int span = avail - 1 - seed;
        for (int f = seed + 1; f < avail; f++)
        {
            // The capture held right from frame 10 to 170, so nothing is pressed from the seed on.
            game.Step(Darkwing.Core.NesButtons.FromByte(0));
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            var e13 = game.Objects.Slots[13];
            if (f <= seed + 4)
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} t={p.StateTimer:X2} vx={p.XVelInt},{p.XVelFrac:X2} xfc={p.XFrac:X2} fl={p.Flags:X2} dir={p.DirBits:X2} iv={p.InvulnFrames:X2} E13({e13.X:X4},{e13.Y:X4})an={e13.AnimNumber:X2} | O({(g[0x4F0] | (g[0x500] << 8)):X4},{(g[0x510] | (g[0x520] << 8)):X4}) st={g[0x50]:X2} t={g[0x51]:X2} vx={(sbyte)g[0x540]},{g[0x530]:X2} xfc={g[0x470]:X2} fl={g[0x400]:X2} dir={g[0x52]:X2} iv={g[0x7C]:X2} 13({g[0x4F0 + 13] | (g[0x500 + 13] << 8):X4})an={g[0x420 + 13]:X2}] ");
            int[] port = { p.State, p.StateTimer, p.InvulnFrames, game.Objects.Slots[0].AnimNumber,
                           game.Objects.Slots[0].AnimFrame, game.Objects.Slots[0].AnimTimer,
                           p.X, p.Y, (p.XVelInt << 8) | p.XVelFrac, ((byte)p.YVelInt << 8) | p.YVelFrac,
                           p.YFrac, p.Flags, e13.StateLow, e13.X, e13.Y, e13.AnimNumber };
            int[] orig = { g[0x50], g[0x51], g[0x7C], g[0x420], g[0x410], g[0x430],
                           g[0x4F0] | (g[0x500] << 8), g[0x510] | (g[0x520] << 8),
                           (g[0x540] << 8) | g[0x530], (g[0x560] << 8) | g[0x550],
                           g[0x460], g[0x400], g[0x4D0 + 13],
                           g[0x4F0 + 13] | (g[0x500 + 13] << 8), g[0x510 + 13] | (g[0x520 + 13] << 8),
                           g[0x420 + 13] };
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
        FieldResult(sb, names, ok, firstBad, span);
        File.WriteAllText(root + "/Art/Temp/hurt_trace.txt", sb.ToString());   // full text, the console truncates
        return sb.ToString();
    }

    /// <summary>
    /// Per-field golden checks (Hurt/Door) report one line per field, so Suite's RESULT-only
    /// scoreboard used to print "no result" for checks that actually passed every field of every
    /// frame. This folds them into the same verdict line the frame-based checks use: all
    /// field-frames must match, and the first diverging frame is the first field divergence.
    /// </summary>
    private static void FieldResult(System.Text.StringBuilder sb, string[] names, int[] ok, int[] firstBad, int span)
    {
        if (span <= 0) throw new System.IO.InvalidDataException("No compared field-frames");
        int totalOk = 0, allFields = 0, firstDiv = -1;
        for (int i = 0; i < names.Length; i++)
        {
            totalOk += ok[i]; allFields += span;
            if (firstBad[i] >= 0 && (firstDiv < 0 || firstBad[i] < firstDiv)) firstDiv = firstBad[i];
        }
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {allFields}/{allFields} field-frames ({names.Length} fields x {span} frames)"
            : $"RESULT: first divergence frame {firstDiv} (matched {totalOk}/{allFields} field-frames)");
    }

    /// <summary>
    /// Door/section golden: `gold_door_id1` is mission 1 (world 0) with the request byte
    /// $5B seeded to 1 at capture frame 5. The original consumes it at $CA6E (LDX $5B /
    /// JSR $E388) 18 frames later; the loader yields per row-redraw batch ($FF06/$E698),
    /// so staging spans 10 redraw frames plus a tail frame. Seeds the port at index 21
    /// (last pre-load frame) and compares every RAM byte $E388 writes.
    /// </summary>
    public static string Door()
    {
        string root = Root;
        byte[] ramAll = File.ReadAllBytes(root + "/Art/Research/DarkwingDuck/reference/gold_door_id1/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        ValidateCapture(root + "/Art/Research/DarkwingDuck/reference/gold_door_id1", ramAll, 21);
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
            game.Bank7,
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));
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
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.ColsToStream = ram0[0x1F];   // $1F only changes through $E66C, so seed the captured value
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        byte doorId = ram0[0x5B];
        // Seed the player's object slot exactly as the capture has it at the seed frame:
        // $E388 leaves $0410/$0420/$0430 alone until the $FA78 seed after its staging.
        var slot0 = game.Objects.Slots[0];
        slot0.AnimNumber = ram0[0x420];
        slot0.AnimFrame = ram0[0x410];
        slot0.AnimTimer = ram0[0x430];
        slot0.Active = (ram0[0x4D0] & 0x80) != 0;
        slot0.StateLow = ram0[0x4D0];   // $CAFC gates on this byte, so the seed must carry it
        map.LoadRow(p.ScrollYRow);
        map.StreamColumns(p.ScrollX);
        // $E388 was already entered 16 frames earlier (the capture's $5B patch at f5), so
        // the port starts the frame at index 22 INSIDE the loader: enter it at the seed.
        if (doorId != 0) game.LoadSection(doorId);

        var names = new[] { "$22 cell", "$23 next", "$FB row", "$FD colhi", "$FC collo",
                            "$FA scrollY", "$50 state", "$36 tileset", "$1A chr0", "$1B chr1",
                            "$0500/$04F0 X", "$0520/$0510 Y", "$0400 flags", "$0420 anim",
                            "$0460 ffc" };
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
                           p.X, p.Y, p.Flags, game.Objects.Slots[0].AnimNumber, p.YFrac };
            int[] orig = { g[0x22], g[0x23], g[0xFB], g[0xFD], g[0xFC],
                           g[0xFA], g[0x50], g[0x36], g[0x1A], g[0x1B],
                           g[0x4F0] | (g[0x500] << 8), g[0x510] | (g[0x520] << 8),
                           g[0x400], g[0x420], g[0x460] };
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
        FieldResult(sb, names, ok, firstBad, span);
        return sb.ToString();
    }

    /// <summary>
    /// ROM palette ($3F00-$3F1F) per frame across the death→reload window of
    /// gold_m1_longroute, side by side with the port's own Ppu.PaletteRam. The golden
    /// artifact is 32 bytes/frame written by the same endFrame callback as ram_frames.bin,
    /// so the harness frame index maps identically.
    /// </summary>
    /// <summary>
    /// G77 — the first ROM golden that contains a live boss. Captured with
    /// `probe_run.py --load-state reference/m1_standing.mss --patch 005B=02 --patch 006D=02`
    /// (no pad input at all): the $CA6E door poll takes id 2 into the Quackerjack arena, cell
    /// $22=$20 of world 0, and the boss core is slot 15 (beh $50, anim $92, $05A0=$80) with the
    /// ball in slot 14 (beh $51) and children (beh $52) appearing from f~300.
    /// Seeded on f27, the first frame in which the boss slot is active.
    /// </summary>
    public static string BossFight() => BossFightSeed(27);

    public static string BossFightSeed(int seedFrame)
    {
        byte[] ram = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/tmp_boss_pilot1/ram_frames.bin");
        return Compare($"boss_pilot1_qjack", ram.Length / 2048, f => 0,
            seedFrame: seedFrame, camera: true, enemies: true, scoreEnemies: true,
            goldenDir: "tmp_boss_pilot1", trace: true, residentFade: true);
    }

    /// <summary>
    /// G86 — the ROM golden that contains a boss DEFEAT: `tmp_boss_pilot14_wolf_defeat` (400 frames,
    /// loaded from `tmp_boss_pilot13_stage520/midfight520.mss`, RAM patched `$00EE=$0F` so the first hit
    /// crosses the `$10` line, and B held 4 frames every 20: `0:b@15-18, 35-38, … 395-398`). Boss slot is
    /// index $0E (behaviour `$54`), so the defeat path `$B36A → $B315` is measurable here: ROM shows
    /// `$EE` 0F→10 at f38, slot behaviour `$54→$4F` at f39 and the slot dying at f79.
    /// </summary>
    public static string BossFightWolfDefeat() => BossWolfDefeatSeed(0);

    public static string BossWolfDefeatSeed(int seedFrame)
    {
        byte[] ram = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/tmp_boss_pilot14_wolf_defeat/ram_frames.bin");
        _enemyDumpLo = 0; _enemyDumpHi = 48;
        try
        {
            return Compare("boss_pilot14_wolf_defeat", ram.Length / 2048,
                f => (f % 20 >= 15 && f % 20 <= 18) ? (byte)0x40 : (byte)0x00,
                seedFrame: seedFrame, camera: true, enemies: true, scoreEnemies: true, world: 1,
                goldenDir: "tmp_boss_pilot14_wolf_defeat", trace: true, residentFade: false);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    public static string PaletteDeath() => PaletteWindow(786, 860);

    /// <summary>
    /// Thunder bolt (behavior 3, bank 6 $841C-$85BB) against the ORIGINAL: the golden is the
    /// RAM slice of the second human recording `20261022T164819Z` (polls 34291-34600 of
    /// Art/Temp/human_164819_fulla/replay1/buffers.bin, provenance.json beside it) — world 1 with
    /// the picked Thunder actually fired. Seeded at local 21 (orig poll34312): the bolt is in
    /// flight (anim $14 pre-launch), sticks to the wall at local f40 (param $84/state $82)
    /// and rides the stuck chain to timer $78/anim $29 at f175 (orig f34465). The recorded
    /// input bytes drive the port. These captures are PRE-input, whereas Compare's ordinary
    /// fixtures are POST-input: step f therefore consumes input[f-1]. The fire frame itself
    /// stays outside this seeded fixture's claim.
    /// </summary>
    public static string ThunderWorld1() => ThunderWorld1Seed(21);

    public static string ThunderWorld1Seed(int seedFrame)
    {
        byte[] ram = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/thunder_world1_164819Z/ram_frames.bin");
        byte[] input = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/thunder_world1_164819Z/input_slice.bin");
        byte previous = File.ReadAllBytes(Root + "/Art/Playthroughs/20261022T164819Z/input.bin")[34289];
        _enemyDumpLo = 0; _enemyDumpHi = 60;
        try
        {
            return Compare("thunder_world1_164819Z", ram.Length / 2048, f => f == 0 ? previous : input[f - 1],
                seedFrame: seedFrame, camera: true, enemies: true, scoreEnemies: true, world: 1,
                goldenDir: "thunder_world1_164819Z", trace: true, residentFade: false);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>
    /// G90 — the ROM golden where the wolf actually THROWS the runner child:
    /// `tmp_boss_pilot15_wolf_throw` (600 frames, same midfight520.mss load, NO $EE patch,
    /// B held 4 frames every 40 from f55). Determinism: two independent runs byte-identical
    /// across all 600 RAM frames. The child (behaviour $55) lives in slot 13 at f288-f295
    /// (X $0F3B, Y $01C8 rising), so the $9BEC handler and its $04AF kill are measurable.
    /// </summary>
    public static string BossFightWolfThrow() => BossWolfThrowSeed(0);

    /// <summary>Local immutable slice of original mission-3 input/RAM, polls 68633–71800.
    /// Starts after the arena intro; diagnostic boss combat, not a natural route PASS.</summary>
    public static string LiquidatorArena()
    {
        string dir = Root + "/Art/Temp/liquidator_reference_slice";
        byte[] input = File.ReadAllBytes(dir + "/input_slice.bin");
        byte[] ram = File.ReadAllBytes(dir + "/ram_frames.bin");
        byte previous = File.ReadAllBytes(Root + "/Art/Playthroughs/20261024T183247Z/input.bin")[68631];
        int bossSamples = 0, bossMatched = 0, bossFirst = -1, bossAddress = -1;
        // State at poll N precedes input[N]; Compare's step into record f uses f-1.
        string result = Compare("liquidator_arena", input.Length, f => f == 0 ? previous : input[f - 1],
            camera: true, enemies: true, scoreEnemies: true, world: 2,
            goldenDir: "../../../Temp/liquidator_reference_slice", trace: true,
            frameObserver: (f, game) =>
            {
                var s = game.Objects.Slots[15];
                int[] addresses = {0x40F,0x41F,0x42F,0x43F,0x48F,0x49F,0x4AF,0x4DF,0x4EF,
                    0x4FF,0x50F,0x51F,0x52F,0x57F,0x58F,0x5AF,0xED,0xEE,0xEF,0x3B};
                int[] values = {s.Flags,s.AnimFrame,s.AnimNumber,s.AnimTimer,s.T480,s.T490,s.T4A0,
                    s.Active ? s.StateLow | 0x80 : s.StateLow,s.Behavior,
                    s.X & 255,s.X >> 8,s.Y & 255,s.Y >> 8,s.BehaviorParam,s.Direction,s.Health,
                    Darkwing.Core.BossBehaviors.InvulnTimer,Darkwing.Core.BossBehaviors.DamageAccum,
                    Darkwing.Core.BossBehaviors.ProjectileSlot,Darkwing.Core.BossBehaviors.DefeatTimer};
                bool equal = true;
                for (int i = 0; i < addresses.Length; i++)
                    if (values[i] != ram[f * 2048 + addresses[i]])
                    { equal = false; if (bossFirst < 0) { bossFirst = f; bossAddress = addresses[i]; } }
                bossSamples++; if (equal) bossMatched++;
            });
        return result + $"\nLIQUIDATOR_BOSS20: {bossMatched}/{bossSamples}; first={bossFirst} address={bossAddress:X4}. Seeded diagnostic, not route acceptance.";
    }

    public static string BossWolfThrowSeed(int seedFrame)
    {
        byte[] ram = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/tmp_boss_pilot15_wolf_throw/ram_frames.bin");
        _enemyDumpLo = 150; _enemyDumpHi = 320;
        try
        {
            return Compare("boss_pilot15_wolf_throw", ram.Length / 2048,
                f => (f >= 55 && f % 40 >= 15 && f % 40 <= 18) ? (byte)0x40 : (byte)0x00,
                seedFrame: seedFrame, camera: true, enemies: true, scoreEnemies: true, world: 1,
                goldenDir: "tmp_boss_pilot15_wolf_throw", trace: true, residentFade: false);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Diagnostic: per-frame af/at of slots 14 (wolf), 2/3/4 (bolts) and the walk
    /// cursor phase, to locate why enemy anim timers stay frozen in the throw fixture.</summary>
    public static string BossFightWolfThrowProbe()
    {
        byte[] ram = File.ReadAllBytes(
            Root + "/Art/Research/DarkwingDuck/reference/tmp_boss_pilot15_wolf_throw/ram_frames.bin");
        var log = new System.Text.StringBuilder();
        _enemyDumpLo = 150; _enemyDumpHi = 320;
        try
        {
            var r = Compare("boss_pilot15_wolf_throw", 230,
                f => (f >= 55 && f % 40 >= 15 && f % 40 <= 18) ? (byte)0x40 : (byte)0x00,
                seedFrame: 0, camera: true, enemies: true, scoreEnemies: true, world: 1,
                goldenDir: "tmp_boss_pilot15_wolf_throw", trace: true, residentFade: false,
                frameObserver: (f, game) =>
                {
                    if (f < 214 || f > 224) return;
                    var o = game.Objects;
                    log.Append($"f{f} walkActive={game.Oam.WalkActive} slot=");
                    for (int s = 1; s <= 15; s++)
                    {
                        var sl = o.Slots[s];
                        if (!sl.Active) continue;
                        log.Append($"{s:X2}(an{sl.AnimNumber:X2} af{sl.AnimFrame:X2} at{sl.AnimTimer:X2}) ");
                    }
                    log.Append('\n');
                });
            File.WriteAllText(Root + "/Art/Temp/wolf_throw_anim_probe.txt",
                log.ToString() + "\n==== verdict ====\n" + VerdictTail(r));
            return "wrote Art/Temp/wolf_throw_anim_probe.txt";
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    public static string PaletteWindow(int lo, int hi, string golden = "m1_longroute", bool runPort = false)
    {
        string root = Root;
        string dir = root + $"/Art/Research/DarkwingDuck/reference/gold_{golden}";
        byte[] palAll = File.ReadAllBytes(dir + "/palette_frames.bin");
        byte[] ramAll = File.ReadAllBytes(dir + "/ram_frames.bin");
        var sb = new System.Text.StringBuilder($"[PaletteWindow {golden} f{lo}..f{hi}]\n");
        for (int f = lo; f <= hi && f * 32 < palAll.Length; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f} st={g[0x50]:X2} d0={g[0x4D0]:X2} x={g[0x500]:X2}{g[0x4F0]:X2} y={g[0x520]:X2}{g[0x510]:X2} " +
                      $"w31={g[0x31]:X2} 64={g[0x64]:X2} 66={g[0x66]:X2} 0F={g[0x0F]:X2} 0D={g[0x0D]:X2} 0E={g[0x0E]:X2} FE={g[0xFE]:X2} F0={g[0xF0]:X2} " +
                      $"src320={g[0x320]:X2}{g[0x321]:X2}{g[0x322]:X2} mir300={g[0x300]:X2}{g[0x301]:X2}{g[0x302]:X2} ");
            for (int i = 0; i < 32; i++) sb.Append(palAll[f * 32 + i].ToString("X2"));
            sb.Append('\n');
        }
        string text = sb.ToString();
        File.WriteAllText(root + "/Art/Temp/palette_window.txt", text);
        return $"wrote Art/Temp/palette_window.txt ({text.Length} chars)";
    }
}
