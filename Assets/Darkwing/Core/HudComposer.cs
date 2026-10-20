namespace Darkwing.Core
{
    /// <summary>
    /// The in-game HUD, which is NOT a separate sprite writer: bank 7 $C850 copies the 18-byte
    /// $C8CF template over $05B0…$05C1 and $C887 hands object slot 1 to it ($04D0/$0400=$80,
    /// anim $1B, $0570=0, $04F0=$18, $0510=$1C, cull bit kept clear). So the HUD is composed by
    /// the bank-5 walk like any other object — 8 records through coord table $24 — and its entry
    /// index is wherever the walk happens to stand when it reaches slot 1: right after slot 0's
    /// records on ($95 &amp; 1) == 0 frames (walk 0→15) and right before them on odd frames
    /// (walk 15→0). That is GAPS G44.1; the earlier "block at entry 1 / at the walk end" reading
    /// was only the two extremes of that rule.
    /// </summary>
    public sealed class HudComposer
    {
        public const byte Slot = 1;         // $C887
        public const byte RelX = 0x18;      // $04F0,1
        public const byte RelY = 0x1C;      // $0510,1

        // $C8CF: part count, coord-table id, then (tile,attr) for $05B2…$05C1.
        private static readonly byte[] Template =
        {
            0x07, 0x24,
            0x80, 0x00, 0x80, 0x40,     // $05B2 TL, $05B4 TR
            0x81, 0x00, 0x81, 0x40,     // $05B6 BL, $05B8 BR
            0x00, 0x00, 0x00, 0x00,     // $05BA, $05BC (weapon icon row)
            0x00, 0x00, 0x00, 0x00,     // $05BE, $05C0 (gas meter digits)
        };

        // RAM, not a constant: $FE26 clears $05B0…$05C1 at reset and only $C850 puts the template
        // there, so the block stays empty until the world HUD is installed.
        private readonly byte[] _param = new byte[Template.Length];
        private bool _installed;

        /// <summary>The $05B0…$05C1 block the walk reads as this slot's part table.</summary>
        public byte[] Param => _param;

        /// <summary>$C850 as called by $D55D/$E40C: template (and therefore a full heart) restored.</summary>
        public void Reset()
        {
            Template.CopyTo(_param, 0);
            _installed = true;
        }

        /// <summary>Rewrites the tile bytes from the state that owns them.</summary>
        public void Refresh(PlayerCore p)
        {
            if (!_installed) return;   // $F9EA/$89D6 only reach the block once $C850 has written it
            // $F9EA…$FA13 drains TR→BR→BL→TL ($80→$82, $81→$83); bank 6 $89D6 refills the same
            // rungs backwards, so the four quadrants are a pure function of the rung count.
            byte rung = p.HitLadder > 4 ? (byte)4 : p.HitLadder;
            _param[2] = rung >= 4 ? (byte)0x82 : (byte)0x80;   // $05B2 TL (only on the killing hit)
            _param[4] = rung >= 1 ? (byte)0x82 : (byte)0x80;   // $05B4 TR
            _param[6] = rung >= 3 ? (byte)0x83 : (byte)0x81;   // $05B6 BL
            _param[8] = rung >= 2 ? (byte)0x83 : (byte)0x81;   // $05B8 BR
            // Weapon pickups bank 6 $8865/$8889/$89AD write the icon and the $7D bracket together.
            _param[10] = p.PendingWeaponIcon;                                        // $05BA
            _param[12] = p.PendingWeaponIcon != 0 ? (byte)0x7D : (byte)0x00;          // $05BC
            _param[14] = p.ActiveGasEnergyHi;                                        // $05BE
            _param[16] = p.ActiveGasEnergyLo;                                        // $05C0
        }
    }
}
