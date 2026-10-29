using System;

namespace Darkwing.Core
{
    public sealed partial class LevelTileMap
    {
        public int LastVerticalStreamCycles { get; private set; }
        public string VerticalTimingUnsupported { get; private set; }
        private readonly byte[] _verticalDescriptorLayout = new byte[256];
        public byte VerticalStreamPending { get; private set; }
        public int LastVerticalDescriptorBytes { get; private set; }

        public byte ReadVerticalDescriptorLayout(int address)
        {
            int cursor = address - 0x0360;
            if ((uint)cursor >= 256) throw new ArgumentOutOfRangeException(nameof(address));
            return _verticalDescriptorLayout[cursor];
        }

        private int AddVerticalDescriptor(int cursor, int length)
        {
            _verticalDescriptorLayout[(byte)cursor] = 0x20;
            _verticalDescriptorLayout[(byte)(cursor + 2)] = (byte)(length - 1);
            return (byte)(cursor + 3 + length);
        }

        public void ClearVerticalStreamPending()
        {
            VerticalStreamPending = 0;
        }

        private static int TakenBranch(int address, int target, bool taken)
        {
            return taken ? 3 + (((address + 2) & 0xFF00) == (target & 0xFF00) ? 0 : 1) : 2;
        }

        private int GridReadCycles(int index)
        {
            return 5 + (((_world.GridAddress & 255) + (byte)index) > 255 ? 1 : 0);
        }

        public int MeasureMacroExpansionCycles(int gridIndex, int macroIndex)
        {
            if ((uint)gridIndex >= (uint)_world.Grid.Length)
                throw new NotSupportedException("EA36 grid index outside decoded world");
            if (GridByte(gridIndex) == 0) return 11;
            int cell = gridIndex % _world.RowWidth;
            int sourceRow = gridIndex / _world.RowWidth;
            if (!TryGetRoom(cell * 32, sourceRow, out _, out byte[] meta, out _))
                throw new NotSupportedException("EA36 room unavailable");
            int cycles = 6 + 6 + 7 + (_world.SetBank >= 6 ? 4 : 3) + 61 + 55;
            cycles += 5 + (_world.Id == 6 ? 2 : 3);
            if (_world.Id == 6)
                throw new NotSupportedException("EA36 world 6 checkpoint-dependent tileset");
            cycles += 25 + 55;
            int macroRow = (macroIndex >> 3) & 7;
            int macroColumn = macroIndex & 7;
            for (int quadrant = 3; quadrant >= 0; quadrant--)
            {
                int metaRow = macroRow * 2 + (quadrant >> 1);
                int metaColumn = macroColumn * 2 + (quadrant & 1);
                int metatile = meta[metaRow * 16 + metaColumn];
                cycles += 40 + (metatile >= 32 ? 1 : 0);
                cycles += (quadrant & 2) == 0 ? 3 + 11 : 2 + 8;
                cycles += 32;
                cycles += quadrant == 0 ? 3 : 2 + quadrant * 9 - 1;
                cycles += 50 + (metatile >= 32 ? 4 : 0);
                cycles += quadrant == 0 ? 3 : 2 + 3;
            }
            cycles += 5 + (macroIndex < 0x38 ? 3 : 2 + 16) + 6;
            return cycles;
        }

        private int RoomPointerCycles(int gridIndex)
        {
            return 83 + GridReadCycles(gridIndex) - 5;
        }

        private static int NextVerticalRoomCycles(byte mode, ref byte gridIndex, byte rowStride)
        {
            int cycles = 5 + TakenBranch(0xE93A, 0xE94C, (mode & 8) != 0);
            if ((mode & 8) != 0)
            {
                gridIndex = (byte)(gridIndex - rowStride);
                return cycles + 17;
            }
            gridIndex = (byte)(gridIndex + rowStride);
            cycles += 13 + TakenBranch(0xE945, 0xE94B, gridIndex != 5);
            if (gridIndex == 5) { gridIndex = 0; cycles += 5; }
            return cycles + 6;
        }

        private int VerticalEdgeCycles(int row, int gridIndex, int page)
        {
            int cycles = 3 + TakenBranch(0xE956, 0xE965, page == 0);
            if (page == 0) return cycles + VerticalNeighborCycles(row, gridIndex, true, out _);
            cycles += 3 + TakenBranch(0xE95A, 0xE99A, page == _world.RowWidth - 1);
            if (page == _world.RowWidth - 1)
                return cycles + VerticalNeighborCycles(row, gridIndex, false, out _);
            cycles += 6 + VerticalNeighborCycles(row, gridIndex, true, out bool wrote);
            cycles += TakenBranch(0xE95F, 0xE964, wrote);
            if (!wrote) cycles += 6 + VerticalNeighborCycles(row, gridIndex, false, out _);
            return cycles + 6;
        }

        private int VerticalNeighborCycles(int row, int gridIndex, bool right, out bool wrote)
        {
            int neighbor = (byte)(gridIndex + (right ? 1 : -1));
            wrote = GridByte(neighbor) != 0;
            int cycles = 5 + GridReadCycles(neighbor) + 2;
            if (!wrote) return cycles + 6;
            cycles++;
            int macroIndex = (row >> 2) * 8 + (right ? 0 : 7);
            cycles += (right ? 20 : 22) + RoomPointerCycles(neighbor) + MeasureMacroExpansionCycles(neighbor, macroIndex);
            cycles += (right ? 47 : 48) + 98;
            return cycles;
        }

        public int MeasureVerticalStreamCycles(PlayerCore player, bool deferredBankAudio = false)
        {
            VerticalTimingUnsupported = null;
            try
            {
                int difference = (sbyte)(byte)(player.ScrollY - player.LastVerticalRedraw);
                int cycles = 8 + TakenBranch(0xE7CB, 0xE7D2, difference >= 0);
                if (difference < 0) { difference = -difference; cycles += 6; }
                int passes = (difference & 15) >> 3;
                cycles += 11 + TakenBranch(0xE7D9, 0xE7DC, passes != 0);
                if (passes == 0) return LastVerticalStreamCycles = cycles + 6;
                if (deferredBankAudio) throw new NotSupportedException("EA36 FF55 deferred audio F6");
                byte mode = player.ScrollMode;
                byte gridIndex = player.CellIndexNext;
                int row = player.VerticalRedrawRow;
                int last = player.LastVerticalRedraw;
                int page = (byte)(player.ScrollX >> 8);
                int descriptorCursor = 0;
                Array.Clear(_verticalDescriptorLayout, 0, _verticalDescriptorLayout.Length);
                bool changed = mode != player.PrevVerticalMode;
                bool down = (mode & 4) != 0;
                cycles += 6 + TakenBranch(0xE7E0, 0xE7FF, !changed);
                if (changed)
                {
                    cycles += 15;
                    row = (byte)(row + (down ? 1 : -1));
                    cycles += TakenBranch(0xE7ED, 0xE7FA, row < 30);
                    if (row >= 30)
                    {
                        row = (byte)(row + (down ? 2 : -2)) & 31;
                        cycles += 21 + NextVerticalRoomCycles(mode, ref gridIndex, player.RowStride);
                    }
                    cycles += 9 + NextVerticalRoomCycles(mode, ref gridIndex, player.RowStride);
                }
                cycles += 2;
                while (passes-- > 0)
                {
                    cycles += 3 + 18 + TakenBranch(0xE80F, 0xE815, !changed);
                    cycles += changed ? 5 : 2;
                    int rowKind = (changed ? 8 : mode & 4) | (row & 3);
                    bool attributes = rowKind == 1 || rowKind == 3 || rowKind == 4 || rowKind == 6 || rowKind >= 8;
                    descriptorCursor = AddVerticalDescriptor(descriptorCursor, 32);
                    if (attributes) descriptorCursor = AddVerticalDescriptor(descriptorCursor, 8);
                    changed = false;
                    cycles += 17 + 6 + RoomPointerCycles(gridIndex) + 117;
                    int macroIndex = (row >> 2) * 8;
                    for (int column = 0; column < 8; column++)
                    {
                        if (column != 0) cycles += 3;
                        cycles += 6 + MeasureMacroExpansionCycles(gridIndex, macroIndex + column);
                        cycles += 23 + 3 * 17 + 3 * 3 + 2;
                        cycles += 7 + TakenBranch(0xE889, 0xE8DC, rowKind == 0 || rowKind == 2 || rowKind == 5 || rowKind == 7);
                        if (attributes) cycles += 124;
                        cycles += 7 + TakenBranch(0xE8E2, 0xE8E8, column == 7);
                        if (column != 7) cycles += 5 + TakenBranch(0xE8E6, 0xE86E, true);
                    }
                    cycles += 7 + TakenBranch(0xE8ED, 0xE8F4, !(rowKind == 1 || rowKind == 3 || rowKind == 4 || rowKind == 6 || rowKind >= 8));
                    if (rowKind == 1 || rowKind == 3 || rowKind == 4 || rowKind == 6 || rowKind >= 8) cycles += 8;
                    cycles += 3 + 6 + VerticalEdgeCycles(row, gridIndex, page);
                    bool right = page != _world.RowWidth - 1;
                    bool edge = GridByte((byte)(gridIndex + (right ? 1 : -1))) != 0;
                    if (!edge && page != 0 && right) edge = GridByte((byte)(gridIndex - 1)) != 0;
                    if (edge)
                    {
                        descriptorCursor = AddVerticalDescriptor(descriptorCursor, 2);
                        descriptorCursor = AddVerticalDescriptor(descriptorCursor, 1);
                    }
                    cycles += 21;
                    row = (byte)(row + (down ? 1 : -1));
                    cycles += TakenBranch(0xE908, 0xE910, row < 30);
                    if (row >= 30)
                    {
                        cycles += 6 + NextVerticalRoomCycles(mode, ref gridIndex, player.RowStride) + 4;
                        row = down ? 0 : 29;
                    }
                    cycles += 21;
                    last = (byte)(last + (down ? 8 : -8));
                    cycles += TakenBranch(0xE91F, 0xE925, last < 240);
                    if (last >= 240) { cycles += 6; last = (byte)(last + (down ? 16 : -16)); }
                    cycles += 3 + 5 + TakenBranch(0xE929, 0xE92E, passes == 0);
                    if (passes != 0) cycles += 3;
                }
                _verticalDescriptorLayout[descriptorCursor] = 0xFF;
                LastVerticalDescriptorBytes = descriptorCursor + 1;
                VerticalStreamPending = 0xFF;
                return LastVerticalStreamCycles = cycles + 16;
            }
            catch (NotSupportedException exception)
            {
                VerticalTimingUnsupported = exception.Message;
                return LastVerticalStreamCycles = -1;
            }
        }
    }
}
