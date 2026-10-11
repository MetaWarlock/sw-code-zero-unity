namespace Darkwing.Core
{
    /// <summary>
    /// Collision surface provider mirroring the original metatile map at RAM $0600.
    /// Probe result semantics ($10 in the original):
    ///  0 empty; 1 one-way; 2 solid floor; 3 solid wall/block; 4 spikes (damage);
    ///  5 door/level object; 6 none; 7 hook tile (sets on-hook flag); 8-15 half/slope family.
    /// Values are maxima over the probe points.
    /// </summary>
    public interface ITileMap
    {
        /// <summary>Type value at world pixel coordinates (16x16 metatile cells).</summary>
        byte TypeAt(int worldX, int worldY);
    }

    /// <summary>Null map: everything empty (used until level data is wired).</summary>
    public sealed class EmptyTileMap : ITileMap
    {
        public static readonly EmptyTileMap Instance = new();
        public byte TypeAt(int x, int y) => 0;
    }
}
