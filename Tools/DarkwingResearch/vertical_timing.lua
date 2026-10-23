local root = 'D:/Art/SW Code Zero Unity/'
local source = assert(io.open(root .. (VERTICAL_SESSION or 'Playthroughs/20261022T164819Z') .. '/input.bin', 'rb'))
local input = source:read('*a')
source:close()
local first = VERTICAL_FIRST or 1
local last = VERTICAL_LAST or #input
local output = VERTICAL_OUTPUT or 'Temp/vertical_timing'
local csv = assert(io.open(root .. output .. '.csv', 'w'))
local bin = assert(io.open(root .. output .. '.bin', 'wb'))
local ending = assert(io.open(root .. output .. '_end.bin', 'wb'))
csv:write('poll,kind,cycles\n')
local poll, finished = 0, false
local active = {}
local function clock() return emu.getState()['cpu.cycleCount'] end
local function snapshot()
    local bytes = {}
    for address=0,2047 do bytes[#bytes+1] = string.char(emu.read(address,emu.memType.nesRam)) end
    return table.concat(bytes)
end
local function begin(kind)
    if finished or poll < first or poll > last then return end
    assert(not active[kind], 'Nested vertical timing call')
    active[kind] = {clock(),snapshot(),0}
end
local function complete(kind, returnCycles)
    local data = active[kind]
    if not data then return end
    csv:write(string.format('%d,%s,%d\n',poll,kind,clock()-data[1]+(returnCycles or 6)-data[3]))
    bin:write(data[2])
    ending:write(snapshot())
    active[kind] = nil
end
emu.addMemoryCallback(function() begin('row') end,emu.callbackType.exec,0xE7C6,0xE7C6)
emu.addMemoryCallback(function() complete('row') end,emu.callbackType.exec,0xE7DB,0xE7DB)
emu.addMemoryCallback(function() complete('row') end,emu.callbackType.exec,0xE935,0xE935)
emu.addMemoryCallback(function() begin('macro') end,emu.callbackType.exec,0xEA36,0xEA36)
emu.addMemoryCallback(function() complete('macro') end,emu.callbackType.exec,0xEA3A,0xEA3A)
emu.addMemoryCallback(function() complete('macro') end,emu.callbackType.exec,0xEAD6,0xEAD6)
if VERTICAL_SCROLL then
    emu.addMemoryCallback(function() begin('scroll') end,emu.callbackType.exec,0xCA59,0xCA59)
    emu.addMemoryCallback(function() complete('scroll',0) end,emu.callbackType.exec,0xCA6B,0xCA6B)
end
local nmiStart
emu.addMemoryCallback(function() if next(active) then nmiStart = clock() end end,emu.callbackType.exec,0xC000,0xC000)
emu.addMemoryCallback(function()
    if nmiStart then
        local cost = clock()+6-nmiStart+7
        for _,data in pairs(active) do data[3] = data[3]+cost end
        nmiStart = nil
    end
end,emu.callbackType.exec,0xC114,0xC114)
local names = {'right','left','down','up','start','select','b','a'}
emu.addEventCallback(function()
    if finished then return end
    poll = poll+1
    if poll > last then
        assert(not next(active), 'Unfinished vertical timing call')
        finished = true
        csv:close(); bin:close(); ending:close(); emu.stop(); return
    end
    local buttons = {}
    local value = string.byte(input,poll)
    for index,name in ipairs(names) do buttons[name] = math.floor(value/2^(index-1))%2==1 end
    emu.setInput(buttons,0)
end,emu.eventType.inputPolled)
