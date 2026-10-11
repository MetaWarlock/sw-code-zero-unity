-- Research instrumentation; no writes to emulated memory.
-- Substituted by probe_run.py, not meant for direct execution.
-- Captures: MMC1 register model, PPU register writes, per-frame RAM/palette/OAM,
-- screenshots at intervals. Frame numbers are relative to script start (frame 0 = before first frame end).
local dir = "D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/probe_raw"
local frame = 0
local ram = assert(io.open(dir .. "/ram_frames.bin", "wb"))
local pal = assert(io.open(dir .. "/palette_frames.bin", "wb"))
local oam = assert(io.open(dir .. "/oam_frames.bin", "wb"))
local vram = assert(io.open(dir .. "/vram_frames.bin", "wb"))  -- both nametables 0x2000-0x2FFF + 0x3F00-0x3FFF handled separately
local chr = assert(io.open(dir .. "/chr_map.csv", "w"))
local regs = assert(io.open(dir .. "/regs.csv", "w"))
local meta = assert(io.open(dir .. "/frames.csv", "w"))
local ppulog = assert(io.open(dir .. "/ppu_writes.csv", "w"))
local banklog = assert(io.open(dir .. "/mmc1_writes.csv", "w"))
local allw = assert(io.open(dir .. "/mmc1_all_writes.csv", "w"))
meta:write("relative_frame,ppu_frame,cpu_cycle,pc,scanline,ppu_cycle\n")
ppulog:write("relative_frame,cpu_cycle,pc,address,value\n")
banklog:write("relative_frame,cpu_cycle,pc,address,value,shift_count,latched_reg,bank\n")

-- MMC1 model -------------------------------------------------------------
local mmc1 = { shift = 0, count = 0, ctrl = 0x0C, chr0 = 0, chr1 = 0, prg = 0 }
local function mmc1BankState()
    -- PRG mode (ctrl bits 2-3): 0/1 = 32K at $8000, 2 = fixed FIRST at $8000, 3 = fixed LAST at $C000
    local prgMode = (math.floor(mmc1.ctrl / 4) % 4)
    local prgLo, prgHi
    if prgMode == 2 then prgLo = 0; prgHi = mmc1.prg % 0x10
    elseif prgMode == 3 then prgLo = mmc1.prg % 0x10; prgHi = 0x0F
    else local b = (mmc1.prg % 0x10) - (mmc1.prg % 2); prgLo = b; prgHi = b + 1 end
    local chrMode = mmc1.ctrl % 4  -- 0: 8K, 1: two 4K
    local chrLow, chrHigh
    if chrMode == 0 then chrLow = mmc1.chr0 - (mmc1.chr0 % 2); chrHigh = chrLow + 1
    else chrLow = mmc1.chr0 % 0x20; chrHigh = mmc1.chr1 % 0x20 end
    return prgLo, prgHi, chrLow, chrHigh
end
local function mmc1Write(pc, address, value)
    local s = emu.getState()
    local cyc = s["cpu.cycleCount"]
    allw:write(string.format("%d,%d,%04X,%04X,%02X\n", frame, cyc, pc, address, value))
    if value >= 0x80 then
        mmc1.shift, mmc1.count = 0, 0
    else
        mmc1.shift = math.floor(mmc1.shift / 2) + (value % 2) * 16
        mmc1.count = mmc1.count + 1
        if mmc1.count == 5 then
            local reg = math.floor(address / 0x2000) % 4
            mmc1.shift = mmc1.shift % 32
            if reg == 0 then mmc1.ctrl = mmc1.shift
            elseif reg == 1 then mmc1.chr0 = mmc1.shift
            elseif reg == 2 then mmc1.chr1 = mmc1.shift
            else mmc1.prg = mmc1.shift end
            mmc1.shift, mmc1.count = 0, 0
            local pl, ph, cl, ch = mmc1BankState()
            banklog:write(string.format("%d,%d,%04X,%04X,%d,%d,%d,%d,%d,%d\n",
                frame, cyc, pc, address, value, reg, pl, ph, cl, ch))
        end
    end
end
emu.addMemoryCallback(function(a, v) mmc1Write(emu.getState()["cpu.pc"], a, v) end, emu.callbackType.write, 0x8000, 0xFFFF)

-- PPU register writes ------------------------------------------------------
emu.addMemoryCallback(function(a, v)
    local s = emu.getState()
    ppulog:write(string.format("%d,%d,%04X,%04X,%d\n", frame, s["cpu.cycleCount"], s["cpu.pc"], a, v))
end, emu.callbackType.write, 0x2000, 0x2007)

-- Frame-end capture --------------------------------------------------------
local closed = false
local function bytes(kind, length)
    local data = {}
    for i = 0, length - 1 do data[#data+1] = string.char(emu.read(i, kind, false)) end
    return table.concat(data)
end
emu.addEventCallback(function()
    frame = frame + 1
    local s = emu.getState()
    ram:write(bytes(emu.memType.nesInternalRam, 2048))
    pal:write(bytes(emu.memType.nesPaletteRam, 32))
    oam:write(bytes(emu.memType.nesSpriteRam, 256))
    -- nametable space 0x2000-0x2FFF (4 KiB) only at configured intervals; VRAM is reconstructable from ppu_writes
    if frame % 60 == 0 then vram:write(string.pack("<I4", frame) .. bytes(emu.memType.nesNametableRam, 0x1000)) end
    local pl, ph, cl, ch = mmc1BankState()
    chr:write(string.format("%d,%d,%d,%d,%d,%d\n", frame, mmc1.ctrl, pl, ph, cl, ch))
    regs:write(string.format("%d,%d\n", frame, s["ppu.ctrl"] or -1))
    meta:write(string.format("%d,%d,%d,%04X,%d,%d\n", frame, s["ppu.frameCount"], s["cpu.cycleCount"], s["cpu.pc"], s["ppu.scanline"], s["ppu.cycle"]))
    if frame % 1000000000 == 0 then
        local f = assert(io.open(dir .. string.format("/frame_%06d.png", frame), "wb"))
        f:write(emu.takeScreenshot()); f:close()
    end
    if frame == 260 then
        ram:close(); pal:close(); oam:close(); vram:close(); chr:close(); regs:close(); meta:close(); ppulog:flush(); banklog:flush(); allw:flush()
        closed = true
        emu.log("runtime capture complete: " .. frame .. " frames")
    end
end, emu.eventType.endFrame)
emu.addEventCallback(function()
    if closed then return end
    for _, h in ipairs({ram, pal, oam, vram, chr, regs, meta, ppulog, banklog, allw}) do h:flush(); h:close() end
end, emu.eventType.scriptEnded)
