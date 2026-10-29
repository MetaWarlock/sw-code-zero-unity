-- Research instrumentation; no writes to emulated memory.
-- Substituted by record_reference.py, not meant for direct execution.
local dir = "D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/det_a"
local count = 300
local screenshotEvery = 150
local frame = 0
local ram = assert(io.open(dir .. "/ram_frames.bin", "wb"))
local pal = assert(io.open(dir .. "/palette_frames.bin", "wb"))
local oam = assert(io.open(dir .. "/oam_frames.bin", "wb"))
local meta = assert(io.open(dir .. "/frames.csv", "w"))
local apu = assert(io.open(dir .. "/apu_writes.csv", "w"))
meta:write("relative_frame,ppu_frame,cpu_cycle,pc,scanline,ppu_cycle\n")
apu:write("relative_frame,cpu_cycle,pc,prg_offset,address,value\n")
local function bytes(kind, length)
    local data = {}
    for i = 0, length - 1 do data[#data+1] = string.char(emu.read(i, kind, false)) end
    return table.concat(data)
end
local function apuWrite(address, value)
    local s = emu.getState()
    local pc = s["cpu.pc"]
    local absolute = emu.convertAddress(pc, emu.memType.nesMemory)
    apu:write(string.format("%d,%d,%d,%d,%d,%d\n",frame,s["cpu.cycleCount"],pc,absolute.address,address,value))
    -- Return nothing: returning a number would modify the write.
end
emu.addMemoryCallback(apuWrite, emu.callbackType.write, 0x4000, 0x4013)
emu.addMemoryCallback(apuWrite, emu.callbackType.write, 0x4015, 0x4015)
emu.addMemoryCallback(apuWrite, emu.callbackType.write, 0x4017, 0x4017)
emu.addEventCallback(function()
    frame = frame + 1
    local s = emu.getState()
    ram:write(bytes(emu.memType.nesInternalRam,2048))
    pal:write(bytes(emu.memType.nesPaletteRam,32))
    oam:write(bytes(emu.memType.nesSpriteRam,256))
    meta:write(string.format("%d,%d,%d,%d,%d,%d\n",frame,s["ppu.frameCount"],s["cpu.cycleCount"],s["cpu.pc"],s["ppu.scanline"],s["ppu.cycle"]))
    if screenshotEvery > 0 and frame % screenshotEvery == 0 then
        local f = assert(io.open(dir .. string.format("/frame_%06d.png",frame),"wb"))
        f:write(emu.takeScreenshot()); f:close()
    end
    if frame == count then
        ram:close(); pal:close(); oam:close(); meta:close(); apu:flush()
        emu.log("Captured " .. frame .. " frame-end records. Check phase against main-loop completion before using gameplay goldens.")
    end
end, emu.eventType.endFrame)
emu.addEventCallback(function() apu:flush(); apu:close() end, emu.eventType.scriptEnded)
