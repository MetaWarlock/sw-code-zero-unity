local root = 'D:/Art/SW Code Zero Unity/'
local source = assert(io.open(root .. (OAM_SESSION or 'Art/Playthroughs/20261019T212535Z') .. '/input.bin', 'rb'))
local input = source:read('*a')
source:close()
local output = OAM_OUTPUT or 'Art/Temp/oam_work'
local first = OAM_FIRST or 1
local last = OAM_LAST or #input
local out = assert(io.open(root .. output .. '.csv', 'w'))
local ramout = assert(io.open(root .. output .. '.bin', 'wb'))
local endout = OAM_CAPTURE_STATES and assert(io.open(root .. output .. '_end.bin', 'wb'))
local nmiout = OAM_CAPTURE_STATES and assert(io.open(root .. output .. '_nmi.bin', 'wb'))
local nmicsv = OAM_CAPTURE_STATES and assert(io.open(root .. output .. '_nmi.csv', 'w'))
if nmicsv then nmicsv:write('poll,start,elapsed,engine\n') end
out:write('poll,start,cycles,nmi,engine\n')
local poll,finished,start,nmiStart,nmiCycles = 0,false,nil,nil,0
local pending,engine
local replayCycles
if OAM_REPLAY_PHASES then
    replayCycles = {}
    for line in io.lines(root .. OAM_REPLAY_PHASES) do
        local frame,clock = line:match('^(%d+),(%d+),0000,')
        if frame then replayCycles[tonumber(clock)] = tonumber(frame) end
    end
end
local names = {'right','left','down','up','start','select','b','a'}
local function cycle() return emu.getState()['cpu.cycleCount'] end
local function snapshot()
    local bytes = {}
    for address=0,2047 do bytes[#bytes+1] = string.char(emu.read(address,emu.memType.nesRam)) end
    return table.concat(bytes)
end
local function begin(address)
    if finished or poll < first then return end
    engine = address == 0xCAD1 and 4 or 5
    start = cycle()
    nmiCycles = 0
    pending = snapshot()
end
local function complete()
    if not finished and start then
        out:write(string.format('%d,%d,%d,%d,%d\n',poll,start,cycle()-start-nmiCycles,nmiCycles,engine))
        ramout:write(pending)
        if endout then endout:write(snapshot()) end
        start=nil
        pending=nil
    end
end
emu.addMemoryCallback(begin,emu.callbackType.exec,0xCABB,0xCABB)
emu.addMemoryCallback(begin,emu.callbackType.exec,0xCAD1,0xCAD1)
emu.addMemoryCallback(complete,emu.callbackType.exec,0xCABE,0xCABE)
emu.addMemoryCallback(complete,emu.callbackType.exec,0xCAD4,0xCAD4)
emu.addMemoryCallback(function()
    if start then
        nmiStart=cycle()
        if nmiout then
            nmiout:write(snapshot())
            nmicsv:write(string.format('%d,%d,%d,%d\n',poll,start,cycle()-start-nmiCycles-13,engine))
        end
    end
end,emu.callbackType.exec,0xC000,0xC000)
emu.addMemoryCallback(function()
    if start and nmiStart then nmiCycles=nmiCycles+cycle()+6-nmiStart+7; nmiStart=nil end
end,emu.callbackType.exec,0xC114,0xC114)
emu.addEventCallback(function()
    if finished then return end
    if replayCycles then
        local actual = assert(replayCycles[cycle()], 'Save-state replay does not match the measured poll clock')
        if poll ~= 0 then assert(actual == poll + 1, 'Skipped or duplicated source poll') end
        poll = actual
    else poll=poll+1 end
    if poll>last then
        finished=true; out:close(); ramout:close()
        if endout then endout:close(); nmiout:close(); nmicsv:close() end
        emu.stop(); return
    end
    local buttons={}
    local byte=string.byte(input,poll)
    for index,name in ipairs(names) do buttons[name]=math.floor(byte/2^(index-1))%2==1 end
    emu.setInput(buttons,0)
end,emu.eventType.inputPolled)
