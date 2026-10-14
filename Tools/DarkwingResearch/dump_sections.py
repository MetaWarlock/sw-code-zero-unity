import sys
b = open('Assets/Darkwing/Data/world_bank7.bytes', 'rb').read()
names = [('cell', 0x2497), ('rowFB', 0x24C1), ('scrollFD', 0x24EB), ('chr1A', 0x2515),
         ('chr1B', 0x253F), ('bgpal', 0x2569), ('sprpal', 0x2593), ('x3D', 0x25BD),
         ('set36', 0x25E7), ('ring', 0x2611)]
cols = {n: b[o:o + 42] for n, o in names}
print('id  cell rowFB scrollFD chr1A chr1B bgpal sprpal  x3D set36 ring')
for i in range(42):
    print('%2d   %s' % (i, '   '.join('%02X' % cols[n][i] for n, o in names)))

# Mission-1 bridge world grid + flags (world 0 descriptor from RoomDecoder offsets)
print()
print('bridge flag cells with low bits set ($41/$42/$C1/$C2/$C3/$C4 family):')
