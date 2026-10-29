import sys

path = sys.argv[1] if len(sys.argv) > 1 else "Art/Research/DarkwingDuck/reference/gold_door_id1/ram_frames.bin"
lo = int(sys.argv[2], 0) if len(sys.argv) > 2 else 20
hi = int(sys.argv[3], 0) if len(sys.argv) > 3 else 50
d = open(path, "rb").read()
nf = len(d) // 2048
for i in range(lo, min(hi, nf)):
    r = d[i * 2048:(i + 1) * 2048]
    x = r[0x4F0] | (r[0x500] << 8)
    y = r[0x510] | (r[0x520] << 8)
    print("f%3d X=%04X Y=%04X flg=%02X an=%02X fr=%02d tm=%02d p60=%02X p70=%02X vxf=%02X vxi=%02X vyf=%02X vyi=%02X s2B=%02X s10=%02X s05=%02X s06=%02X" % (
        i + 1, x, y, r[0x400], r[0x420], r[0x410], r[0x430], r[0x460], r[0x470],
        r[0x530], r[0x540], r[0x550], r[0x560], r[0x2B], r[0x10], r[0x05], r[0x06]))
