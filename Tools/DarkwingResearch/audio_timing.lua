local audioTiming = assert(io.open(dir .. "/audio_ticks.csv", "w"))
local audioBefore = assert(io.open(dir .. "/audio_before.bin", "wb"))
local audioAfter = assert(io.open(dir .. "/audio_after.bin", "wb"))
local audioWalk = assert(io.open(dir .. "/audio_walk.csv", "w"))
local audioTaskTiming = assert(io.open(dir .. "/audio_tasks.csv", "w"))
local audioTaskBefore = assert(io.open(dir .. "/audio_task_before.bin", "wb"))
local audioTaskAfter = assert(io.open(dir .. "/audio_task_after.bin", "wb"))
audioTaskTiming:write("poll,id,kind,cycles,offset\n")
local activeTask = nil
local taskOffset = 0
audioTiming:write("poll,kind,cycles,offset\n")
audioWalk:write("poll,pc,cycles\n")
local audioOffset = 0
local activeAudio = nil
local function audioCapture(kind)
    local state = emu.getState()
    local bytes = {}
    for address = 0, 2047 do bytes[#bytes + 1] = string.char(r(address)) end
    if kind == "begin" then audioBefore:write(table.concat(bytes))
    else audioAfter:write(table.concat(bytes)) end
    audioTiming:write(string.format("%d,%s,%d,%d\n", poll, kind, state["cpu.cycleCount"], audioOffset))
    audioTiming:flush()
end
emu.addMemoryCallback(function()
    if poll < 17200 or poll > 17236 then return end
    activeAudio = poll
    audioCapture("begin")
end, emu.callbackType.exec, 0xC0F6, 0xC0F6)
emu.addMemoryCallback(function()
    if activeAudio == nil then return end
    audioCapture("end")
    activeAudio = nil
    audioOffset = audioOffset + 2048
end, emu.callbackType.exec, 0xC0F9, 0xC0F9)
emu.addMemoryCallback(function(address)
    if activeAudio ~= nil then
        audioWalk:write(string.format("%d,%04X,%d\n", poll, address, emu.getState()["cpu.cycleCount"]))
    end
end, emu.callbackType.exec, 0x8000, 0x8914)
local function taskCapture(kind)
    local state = emu.getState()
    local bytes = {}
    for address = 0, 2047 do bytes[#bytes + 1] = string.char(r(address)) end
    if kind == "begin" then audioTaskBefore:write(table.concat(bytes))
    else audioTaskAfter:write(table.concat(bytes)) end
    audioTaskTiming:write(string.format("%d,%d,%s,%d,%d\n", poll, activeTask, kind, state["cpu.cycleCount"], taskOffset))
end
emu.addMemoryCallback(function()
    if mmc1.prg ~= 3 then return end
    activeTask = emu.getState()["cpu.a"]
    taskCapture("begin")
end, emu.callbackType.exec, 0x80FE, 0x80FE)
emu.addMemoryCallback(function()
    if activeTask == nil then return end
    taskCapture("end")
    activeTask = nil
    taskOffset = taskOffset + 2048
end, emu.callbackType.exec, 0x8105, 0x8105)
emu.addEventCallback(function()
    audioTiming:close(); audioBefore:close(); audioAfter:close(); audioWalk:close()
    audioTaskTiming:close(); audioTaskBefore:close(); audioTaskAfter:close()
end, emu.eventType.scriptEnded)
