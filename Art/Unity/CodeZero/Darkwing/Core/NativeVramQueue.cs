using System;
namespace Darkwing.Core
{
    public sealed class NativeVramQueue
    {
        public readonly byte[] Bytes = new byte[256];
        public readonly byte[] Attributes = new byte[128];
        public byte Pending { get; private set; }
        public string Unsupported { get; private set; }
        public int BandEvents { get; private set; }
        public int RowEvents { get; private set; }
        public int TextEvents { get; private set; }
        public int DescriptorBytes { get; private set; }
        private readonly byte[] _bank7;
        private byte _macroPalette;
        public void AssignPaletteScratch(byte value) => _macroPalette = value;

        public NativeVramQueue(byte[] bank7)
        {
            _bank7 = bank7;
            Bytes[0] = 0xFF;
        }

        public void BeginBody()
        {
            BandEvents = 0;
            RowEvents = 0;
            TextEvents = 0;
        }

        private int Run(int cursor, int address, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1 || payload.Length > 128 || cursor + payload.Length + 3 >= Bytes.Length)
                throw new NotSupportedException("VRAM run requires extended queue storage or another transfer mode");
            Bytes[(byte)cursor++] = (byte)(address >> 8);
            Bytes[(byte)cursor++] = (byte)address;
            Bytes[(byte)cursor++] = (byte)(payload.Length - 1);
            foreach (byte value in payload) Bytes[(byte)cursor++] = value;
            return cursor;
        }

        private void End(int cursor)
        {
            Bytes[(byte)cursor] = 0xFF;
            DescriptorBytes = cursor + 1;
        }

        public void Band(LevelTileMap map, byte stage, int gridIndex, bool blanked)
        {
            BandEvents++;
            if (BandEvents == 1 && RowEvents == 0 && TextEvents == 0) Unsupported = null;
            if (RowEvents != 0 || TextEvents != 0) Unsupported = "Multiple VRAM producer grouping is not implemented";
            if (BandEvents > 1 && !blanked) Unsupported = "Multiple E698 passes require operation-group cursor";
            int page = stage >> 7;
            int bandRow = stage & 7;
            int attributeColumn = (stage >> 4) & 7;
            int half = (stage >> 3) & 1;
            int column = 4 * attributeColumn + 2 * half;
            int cursor = 0;
            ReadOnlySpan<byte> tiles = map.NativeQueueBandTiles;
            for (int offset = 0; offset < 4; offset++)
            {
                int row = bandRow * 4 + offset;
                if (row < 30) cursor = Run(cursor, 0x2000 + page * 0x400 + row * 32 + column, tiles.Slice(4 * offset + 2 * half, 2));
            }
            ReadOnlySpan<byte> palettes = map.NativeQueueBandPalettes;
            int attribute = page * 64 + bandRow * 8 + attributeColumn;
            int mask = half == 0 ? 0x33 : 0xCC;
            if (map.GridByte(gridIndex) != 0)
                _macroPalette = (byte)(palettes[0] | (palettes[1] << 2) | (palettes[2] << 4) | (palettes[3] << 6));
            _macroPalette &= (byte)mask;
            Attributes[attribute] = (byte)((Attributes[attribute] & ~mask) | _macroPalette);
            cursor = Run(cursor, 0x23C0 + page * 0x400 + bandRow * 8 + attributeColumn, Attributes.AsSpan(attribute, 1));
            End(cursor);
            Pending++;
            if (blanked) Publish(null, 0, 0);
        }

        public void Row(LevelTileMap map, PlayerCore player, int row, int gridIndex, int page)
        {
            RowEvents++;
            if (RowEvents == 1 && BandEvents == 0 && TextEvents == 0) Unsupported = null;
            else Unsupported = "Multiple VRAM producer grouping is not implemented";
            if (!map.NativeQueueRoom(gridIndex, out byte[] tiles, out byte[] meta, out byte[] attributes))
            {
                Edge(map, row, gridIndex, page, 0);
                Unsupported = "E7C6 empty-room retained macro row";
                Pending = 0xFF;
                return;
            }
            int kind = ((player.ScrollMode != player.PrevVerticalMode) ? 8 : player.ScrollMode & 4) | (row & 3);
            int mask = _bank7[0x2A16 + kind];
            int cursor = Run(0, 0x2000 + (page & 1) * 0x400 + row * 32, tiles.AsSpan(row * 32, 32));
            {
                int attributeRow = row >> 2;
                int cache = (page & 1) * 64 + attributeRow * 8;
                for (int column = 0; column < 8; column++)
                {
                    int packed = 0;
                    for (int quadrant = 0; quadrant < 4; quadrant++)
                        packed |= (attributes[meta[(attributeRow * 2 + (quadrant >> 1)) * 16 + column * 2 + (quadrant & 1)]] & 3) << (quadrant * 2);
                    _macroPalette = (byte)packed;
                    if (mask != 0)
                    {
                        _macroPalette &= (byte)mask;
                        Attributes[cache + column] = (byte)((Attributes[cache + column] & _bank7[0x2A22 + kind]) | _macroPalette);
                    }
                }
                if (mask != 0) cursor = Run(cursor, 0x23C0 + (page & 1) * 0x400 + attributeRow * 8, Attributes.AsSpan(cache, 8));
            }
            cursor = Edge(map, row, gridIndex, page, cursor);
            End(cursor);
            Pending = 0xFF;
        }

        private int Edge(LevelTileMap map, int row, int gridIndex, int page, int cursor)
        {
            bool right = page != map.GridWidth - 1;
            int neighbor = (byte)(gridIndex + (right ? 1 : -1));
            if (map.GridByte(neighbor) == 0)
            {
                if (page == 0 || !right) return cursor;
                right = false;
                neighbor = (byte)(gridIndex - 1);
                if (map.GridByte(neighbor) == 0) return cursor;
            }
            if (!map.NativeQueueRoom(neighbor, out byte[] tiles, out byte[] meta, out byte[] attributes))
            {
                Unsupported = "E954 neighboring decoded room unavailable";
                return cursor;
            }
            int column = right ? 0 : 30;
            int attributeColumn = right ? 0 : 7;
            int attributeRow = row >> 2;
            int cache = ((page & 1) ^ 1) * 64 + attributeRow * 8 + attributeColumn;
            int packed = 0;
            for (int quadrant = 0; quadrant < 4; quadrant++)
                packed |= (attributes[meta[(attributeRow * 2 + (quadrant >> 1)) * 16 + attributeColumn * 2 + (quadrant & 1)]] & 3) << (quadrant * 2);
            Attributes[cache] = (byte)packed;
            _macroPalette = (byte)packed;
            cursor = Run(cursor, 0x2000 + ((page & 1) ^ 1) * 0x400 + row * 32 + column, tiles.AsSpan(row * 32 + column, 2));
            return Run(cursor, 0x23C0 + ((page & 1) ^ 1) * 0x400 + attributeRow * 8 + attributeColumn, Attributes.AsSpan(cache, 1));
        }

        public void TextTile(int address, byte value, bool deferred = true)
        {
            TextEvents++;
            if (TextEvents == 1 && BandEvents == 0 && RowEvents == 0) Unsupported = null;
            if (BandEvents != 0 || RowEvents != 0) Unsupported = "Multiple VRAM producer grouping is not implemented";
            if (TextEvents > 1 && deferred) Unsupported = "Text clear/multiwrite source grouping not implemented";
            Span<byte> tile = stackalloc byte[] { value };
            End(Run(0, address, tile));
            Pending = 0xFF;
            if (!deferred) Publish(null,0,0);
        }

        /// <summary>Bank2 $AADD builds one marker and three descending 29-byte runs.
        /// The map uses a different producer and must not enter this grouping.</summary>
        public void TextClear(int[] lines, byte value, bool deferred)
        {
            if (lines == null || lines.Length != 3)
                throw new ArgumentException("AADD requires three line bases");
            TextEvents++;
            Unsupported = BandEvents == 0 && RowEvents == 0 && TextEvents == 1 ? null
                : "Multiple VRAM producer grouping is not implemented";
            Span<byte> fill = stackalloc byte[29];
            fill.Fill(value);
            int cursor = Run(0, 0x22FE, fill.Slice(0, 1));
            for (int line = 2; line >= 0; line--) cursor = Run(cursor, lines[line], fill);
            End(cursor);
            Pending = 0xFF;
            if (!deferred) Publish(null, 0, 0);
        }

        public bool Publish(Ppu destination, byte blank, byte busy)
        {
            if ((blank | busy) != 0) return false;
            if (Unsupported != null) return false;
            if (Pending == 0) return true;
            if (destination != null)
            {
                int cursor = 0;
                for (int command = 0; command < 256 && Bytes[(byte)cursor] < 0x80; command++)
                {
                    int address = (Bytes[(byte)cursor++] << 8) | Bytes[(byte)cursor++];
                    int length = Bytes[(byte)cursor++] + 1;
                    for (int index = 0; index < length; index++) destination.WriteVram(address + index, Bytes[(byte)cursor++]);
                }
            }
            Pending = 0;
            return true;
        }

        public NmiTiming.Publication Publication(byte blank, byte busy, byte stream1C, byte stream1D, byte chr0, byte chr1, byte palette)
        {
            if ((blank | busy) == 0)
            {
                if (stream1C != 0 || stream1D != 0) throw new NotSupportedException("Only native flag19 producer is implemented");
                if (Unsupported != null) throw new NotSupportedException(Unsupported);
            }
            return new NmiTiming.Publication(blank,busy,stream1C,stream1D,Pending,chr0,chr1,palette,ReadDescriptor);
        }

        public bool TryMeasurePublicationCycles(long handlerCycle, byte blank, byte busy, byte stream1C, byte stream1D, byte chr0, byte chr1, byte palette,
            out int cycles, out string unsupported)
        {
            try
            {
                cycles = NmiTiming.PublicationCycles(Publication(blank,busy,stream1C,stream1D,chr0,chr1,palette),handlerCycle);
                unsupported = null;
                return true;
            }
            catch (NotSupportedException exception)
            {
                cycles = -1;
                unsupported = exception.Message;
                return false;
            }
        }

        public byte ReadDescriptor(int address)
        {
            int offset = address - 0x0360;
            if ((uint)offset >= (uint)Bytes.Length) throw new NotSupportedException("VRAM descriptor absolute-indexed read beyond0360..045F");
            return Bytes[offset];
        }
    }
}
