-- Audio probe: APU writes, task-queue writes, task-table reads, driver state dumps.
-- Substituted: "D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/probe_audio_sfx" 500
local dir = "D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/probe_audio_sfx"
local count = 500
local frame = 0
local apu = assert(io.open(dir .. "/apu_writes.csv", "w"))
local queue = assert(io.open(dir .. "/queue_writes.csv", "w"))
local tbl = assert(io.open(dir .. "/task_reads.csv", "w"))
local state = assert(io.open(dir .. "/driver_state.bin", "wb"))
local meta = assert(io.open(dir .. "/frames.csv", "w"))
apu:write("relative_frame,cpu_cycle,pc,address,value\n")
queue:write("relative_frame,cpu_cycle,pc,address,value\n")
tbl:write("relative_frame,cpu_cycle,pc,address,value_read\n")
meta:write("relative_frame,ppu_frame,cpu_cycle,pc\n")

local function logw(file, address, value)
    local s = emu.getState()
    file:write(string.format("%d,%d,%04X,%04X,%02X\n", frame, s["cpu.cycleCount"], s["cpu.pc"], address, value))
end

-- APU registers $4000-$4017
emu.addMemoryCallback(function(a, v) logw(apu, a, v) end, emu.callbackType.write, 0x4000, 0x4017)
-- Driver task queue $DB-$E3 and engine flags $C0-$D8 region writes
emu.addMemoryCallback(function(a, v) logw(queue, a, v) end, emu.callbackType.write, 0x00DB, 0x00E3)
emu.addMemoryCallback(function(a, v) logw(queue, a, v) end, emu.callbackType.write, 0x00C0, 0x00D8)
-- Reads of the 64-entry task table $8A43-$8AC2 (PRG space)
emu.addMemoryCallback(function(a, v) logw(tbl, a, v) end, emu.callbackType.read, 0x8A43, 0x8AC2)

local function bytes(kind, length)
    local data = {}
    for i = 0, length - 1 do data[#data+1] = string.char(emu.read(i, kind, false)) end
    return table.concat(data)
end

emu.addEventCallback(function()
    frame = frame + 1
    local s = emu.getState()
    -- driver state: $C0-$DF (32) + $0700-$07FF (256), tagged
    state:write(string.pack("<I4", frame) .. bytes(emu.memType.nesInternalRam, 0x200):sub(0xC1, 0xE0))
    state:write(bytes(emu.memType.nesInternalRam, 0x800):sub(0x701, 0x800))
    meta:write(string.format("%d,%d,%d,%04X\n", frame, s["ppu.frameCount"], s["cpu.cycleCount"], s["cpu.pc"]))
    if frame == count then
        apu:flush(); queue:flush(); tbl:flush(); state:flush(); meta:flush()
        emu.log("audio probe complete: " .. frame .. " frames")
    end
end, emu.eventType.endFrame)

emu.addEventCallback(function()
    for _, f in ipairs({apu, queue, tbl, state, meta}) do f:flush(); f:close() end
end, emu.eventType.scriptEnded)
