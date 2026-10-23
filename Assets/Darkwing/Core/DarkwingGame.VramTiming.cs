namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        public NativeVramQueue NativeVram { get; private set; }

        private void BindNativeVramProducers()
        {
            if (Bank7 == null) return;
            if (NativeVram == null) NativeVram = new NativeVramQueue(Bank7);
            Player.NativePaletteScratchAssigned = (player,value) => NativeVram.AssignPaletteScratch(value);
            if (TileMap != null)
            {
                TileMap.NativeBandQueued = (map,stage,grid) => NativeVram.Band(map,stage,grid,_blankDepth != 0);
                TileMap.NativeRowQueued = (map,row,grid,page) => NativeVram.Row(map,Player,row,grid,page);
            }
            if (_text != null) _text.NativeQueueWrite = (address,value,deferred) => NativeVram.TextTile(address,value,deferred);
        }

        public void BeginNativeVramBody() => NativeVram?.BeginBody();

        public bool PublishNativeVram(byte busy, bool writePpu)
        {
            return NativeVram != null && NativeVram.Publish(writePpu ? Ppu : null,(byte)_blankDepth,busy);
        }

        public bool TryMeasureNativeNmiPublication(long handlerCycle, byte busy, byte stream1C, byte stream1D,
            out int cycles, out string unsupported)
        {
            if (NativeVram == null)
            {
                cycles = -1;
                unsupported = "Native VRAM producers are not initialized";
                return false;
            }
            return NativeVram.TryMeasurePublicationCycles(handlerCycle,(byte)_blankDepth,busy,stream1C,stream1D,
                Player.ChrLatch0,Player.ChrLatch1,_palDirty,out cycles,out unsupported);
        }
    }
}
