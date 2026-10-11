namespace Darkwing.Core
{
    /// <summary>
    /// City map screen logic (world 31). Cursor moves between the mission bubbles
    /// selectable at the current tier ($38): tier 0 → missions {0,1,2},
    /// tier 1 → {3,4,5}, tier 2 → {6} (bank 2 $A400-$A607, handlers $A4B0/$A503/$A53A).
    /// Select opens the HELP bubble readout ($CE39); Start enters the node (YES/NO
    /// confirm, then the flight cutscene and world load $DD57).
    /// </summary>
    public sealed class MapScreen
    {
        private readonly WorldState _world;

        public MapScreen(WorldState world) { _world = world; }

        public byte CursorMission;  // mission id under the bubble cursor
        public bool HelpShown;      // Select readout open

        private static readonly int[] Tier0 = { 0, 1, 2 };
        private static readonly int[] Tier1 = { 3, 4, 5 };
        private static readonly int[] Tier2 = { 6 };

        private int[] Selectable()
        {
            return _world.CompletedTier switch
            {
                0 => Tier0,
                1 => Tier1,
                _ => Tier2,
            };
        }

        /// <summary>One frame of map input. Returns a mission id when confirmed, else null.</summary>
        public int? Step(byte held, byte pressed)
        {
            var sel = Selectable();
            if ((pressed & 0x01) != 0 || (pressed & 0x02) != 0) // Left/Right move cursor
            {
                int idx = System.Array.IndexOf(sel, CursorMission);
                if (idx < 0) idx = 0;
                idx = (pressed & 0x01) != 0
                    ? (idx + 1) % sel.Length
                    : (idx + sel.Length - 1) % sel.Length;
                CursorMission = (byte)sel[idx];
            }
            if ((pressed & 0x20) != 0) HelpShown = true;  // Select: HELP bubble
            if ((pressed & 0x10) != 0)                    // Start: enter node
                return CursorMission;
            return null;
        }

        public void OnMissionComplete(int mission)
        {
            _world.CompleteMission(mission);
        }
    }
}
