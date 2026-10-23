using System;
using System.IO;
using Darkwing.Core;

public static class VerticalTimingVerify
{
    public static string Run(string stem = "Temp/prefix_native_cost_rows", string nextPollCapture = "Temp/human_164819_fulla/replay1/buffers.bin")
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        string data = root + "Assets/Darkwing/Data/";
        var decoder = new RoomDecoder(File.ReadAllBytes(data + "prg_bank0.bytes"), File.ReadAllBytes(data + "prg_bank1.bytes"),
            File.ReadAllBytes(data + "world_bank7.bytes"), File.ReadAllBytes(data + "levelsets.bytes"));
        byte[] source = File.ReadAllBytes(root + stem + ".bin");
        string[] lines = File.ReadAllLines(root + stem + ".csv");
        byte[] ending = File.Exists(root + stem + "_end.bin") ? File.ReadAllBytes(root + stem + "_end.bin") : null;
        byte[] nextPoll = ending == null ? File.ReadAllBytes(root + nextPollCapture) : null;
        if (lines.Length <= 1 || source.Length != (lines.Length - 1) * 2048 || (ending != null && ending.Length != source.Length))
            throw new Exception("Incomplete vertical source fixtures");
        if (nextPoll != null && nextPoll.Length % 4384 != 0) throw new Exception("Incomplete next-poll capture");
        int macros = 0, rows = 0, descriptors = 0, cursorBytes = 0, negatives = 0, unsupported = 0, scrolls = 0, unsupportedScrolls = 0;
        for (int index = 1; index < lines.Length; index++)
        {
            string[] cells = lines[index].Split(',');
            if (cells.Length != 3) throw new Exception("Malformed fixture row");
            int poll = int.Parse(cells[0]);
            int expected = int.Parse(cells[2]);
            var ram = new byte[2048];
            Array.Copy(source, (index - 1) * 2048, ram, 0, 2048);
            var world = decoder.GetWorld(ram[0x31]);
            world.SetBank = ram[0x36];
            var map = new LevelTileMap(decoder, new Ppu(null), world);
            var player = new PlayerCore(map)
            {
                ScrollX = (ushort)(ram[0xFC] | ram[0xFD] << 8), ScrollY = ram[0xFA],
                LastVerticalRedraw = ram[0x29], VerticalRedrawRow = ram[0x2A],
                ScrollMode = ram[0x2F], PrevVerticalMode = ram[0x21], CellIndexNext = ram[0x23], RowStride = ram[0x2E],
                TimingDeferredBankAudio = ram[0xF6] != 0
            };
            int actual;
            if (cells[1] == "macro")
            {
                int roomPointer = ram[0x26] | ram[0x27] << 8;
                int room = (roomPointer - 0x7FC0) / 64;
                int gridIndex = Array.IndexOf(world.Grid, (byte)room);
                if (gridIndex < 0) throw new Exception("Macro fixture room not in world");
                actual = map.MeasureMacroExpansionCycles(gridIndex, ram[0x24]);
                macros++;
            }
            else if (cells[1] == "scroll")
            {
                player.State = ram[0x50];
                player.X = (ushort)(ram[0x4F0] | ram[0x500] << 8);
                player.Y = (ushort)(ram[0x510] | ram[0x520] << 8);
                player.PrevPlayerX = (ushort)(ram[0x6E] | ram[0x6F] << 8);
                player.ScrollYRow = ram[0xFB];
                player.ScrollState = ram[0x2B];
                player.CellIndex = ram[0x22];
                player.CheckpointId = ram[0x6D];
                player.GridAt = map.GridByte;
                player.FlagAt = map.FlagByte;
                player.ReloadRow = player.VerticalStreamPass;
                player.HorizontalScroll(map.GridByte, map.FlagByte);
                player.CellGridByte = map.GridByte(player.CellIndex);
                player.CellScriptByte = map.FlagByte(player.CellIndex);
                player.HookGrabCheck();
                actual = player.LastScrollServiceCycles;
                if (actual < 0)
                {
                    if (player.ScrollTimingUnsupported == null) throw new Exception("Unexplained unsupported scroll");
                    unsupportedScrolls++;
                    continue;
                }
                int expectedOffset = ending == null ? poll * 4384 : (index - 1) * 2048;
                byte[] after = ending ?? nextPoll;
                int[] addresses = {0x21, 0x22, 0x23, 0x29, 0x2A, 0x2B, 0x2F, 0x6D, 0x6E, 0x6F, 0xFA, 0xFB, 0xFC, 0xFD};
                byte[] values = {player.PrevVerticalMode, player.CellIndex, player.CellIndexNext, player.LastVerticalRedraw,
                    player.VerticalRedrawRow, player.ScrollState, player.ScrollMode, player.CheckpointId,
                    (byte)player.PrevPlayerX, (byte)(player.PrevPlayerX >> 8), player.ScrollY, player.ScrollYRow,
                    (byte)player.ScrollX, (byte)(player.ScrollX >> 8)};
                for (int field = 0; field < addresses.Length; field++)
                {
                    if (values[field] != after[expectedOffset + addresses[field]]) throw new Exception("Scroll state mismatch " + poll + " $" + addresses[field].ToString("X2"));
                    cursorBytes++;
                }
                scrolls++;
            }
            else if (cells[1] == "row")
            {
                int distance = Math.Abs((sbyte)(byte)(player.ScrollY - player.LastVerticalRedraw));
                if (((distance & 15) >> 3) != 0)
                {
                    if (map.MeasureVerticalStreamCycles(player, true) != -1 || map.VerticalTimingUnsupported == null)
                        throw new Exception("Deferred bank work was silently priced");
                    unsupported++;
                }
                player.VerticalStreamPass();
                actual = map.LastVerticalStreamCycles;
                rows++;
                int expectedOffset = ending == null ? poll * 4384 : (index - 1) * 2048;
                byte[] after = ending ?? nextPoll;
                if (expectedOffset + 2048 > after.Length) throw new Exception("Missing row ending");
                int[] addresses = {0x21, 0x23, 0x29, 0x2A};
                byte[] values = {player.PrevVerticalMode, player.CellIndexNext, player.LastVerticalRedraw, player.VerticalRedrawRow};
                for (int field = 0; field < addresses.Length; field++)
                {
                    if (values[field] != after[expectedOffset + addresses[field]]) throw new Exception("Vertical cursor mismatch " + poll);
                    cursorBytes++;
                }
                if (map.VerticalStreamPending != 0)
                {
                    byte Read(int address) => after[expectedOffset + address];
                    int originalCost = NmiTiming.DescriptorCycles(0, Read);
                    int nativeCost = NmiTiming.DescriptorCycles(0, map.ReadVerticalDescriptorLayout);
                    if (originalCost != nativeCost) throw new Exception("Vertical descriptor timing mismatch " + poll);
                    if (nativeCost == NmiTiming.DescriptorCycles(0, address => 0xFF)) throw new Exception("Descriptor mismatch escaped " + poll);
                    negatives++;
                    map.ClearVerticalStreamPending();
                    if (map.VerticalStreamPending != 0) throw new Exception("Descriptor clear failed");
                    descriptors++;
                }
            }
            else throw new Exception("Unknown fixture kind");
            if (actual != expected) throw new Exception($"Vertical cost {poll}/{cells[1]}: source={expected}, native={actual}, unsupported={map.VerticalTimingUnsupported}");
        }
        if (macros == 0 || rows == 0 || descriptors == 0 || negatives != descriptors) throw new Exception("Missing vertical fixture coverage");
        return $"PASS macro={macros} row={rows} scroll={scrolls} unsupported-scroll={unsupportedScrolls} cursor-bytes={cursorBytes} descriptor={descriptors} rejected={negatives} unsupported={unsupported}; isolated source CPU fixtures, not integrated route timing";
    }
}
