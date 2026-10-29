import sys

RECORD_SIZE = 2048 + 2048 + 256 + 32  # 4384 bytes

def get_ram(frame):
    with open('Temp/m3_original_validation/replay1/buffers.bin', 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

print("Frame | Orig Scroll: X=FD:FC, Y=FB:FA | Port Scroll: X, Y, Row")
for frame in range(7670, 7678):
    ram = get_ram(frame)
    sx = ram[0xFC] | (ram[0xFD] << 8)
    sy = ram[0xFA] | (ram[0xFB] << 8)
    print(f"{frame:5d} | Orig: ScrollX={sx:04X} ({sx}) ScrollY={ram[0xFA]:02X} ScrollYRow={ram[0xFB]:02X} (FullY={sy})")

# Read port trace lines
with open('Temp/m3_port_15k_fix/trace.csv', 'r') as f:
    lines = f.readlines()
for frame in range(7670, 7678):
    line = lines[frame] # 1-based header is line 0
    parts = line.strip().split(',')
    # frame,gameFrame,x,y,state,ctrl,mask,pal_sum,phase,phaseFrames,timer,parity,stream,textByte,scrollX,scrollY,cell
    print(f"Port line {frame}: frame={parts[0]} x={parts[2]} y={parts[3]} state={parts[4]} scrollX={parts[14]} scrollY={parts[15]} cell={parts[16]}")
