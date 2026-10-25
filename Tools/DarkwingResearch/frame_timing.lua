local root = 'D:/Art/SW Code Zero Unity/'
local function env(name) return os and os.getenv and os.getenv(name) end
local first = tonumber(TIMING_FIRST_POLL or env('TIMING_FIRST_POLL')) or 0
local last = tonumber(TIMING_LAST_POLL or env('TIMING_LAST_POLL'))
local instructions = (TIMING_MODE or env('TIMING_MODE')) == 'loader'
local source = assert(io.open(root .. (TIMING_SESSION or env('TIMING_SESSION') or 'Playthroughs/20261019T212535Z') .. '/input.bin', 'rb'))
local input = source:read('*a')
source:close()
local out = assert(io.open(root .. (TIMING_OUTPUT or env('TIMING_OUTPUT') or 'Temp/frame_timing') .. '.csv', 'w'))
out:write('poll,cycle,pc,bank,x,y,sp,e4,bracket,a,f5,f6,r25,r23,r05,returnPc\n')
local poll = 0
local finished = false
local inNmi = false
local names = {'right','left','down','up','start','select','b','a'}
local function log(pc)
    if finished or poll < first then return end
    local state = emu.getState()
    local sp = state['cpu.sp']
    local returnPc = 0
    if pc == 0xC000 then
        returnPc = emu.read(0x100+(sp+2)%256,emu.memType.nesRam)
            + 256*emu.read(0x100+(sp+3)%256,emu.memType.nesRam)
    end
    out:write(string.format('%d,%d,%04X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%04X\n', poll,
        state['cpu.cycleCount'], pc, emu.read(0xF1,emu.memType.nesRam),
        state['cpu.x'],state['cpu.y'],state['cpu.sp'],
        emu.read(0xE4,emu.memType.nesRam),emu.read(0x9A,emu.memType.nesRam),state['cpu.a'],
        emu.read(0xF5,emu.memType.nesRam),emu.read(0xF6,emu.memType.nesRam),
        emu.read(0x25,emu.memType.nesRam),emu.read(0x23,emu.memType.nesRam),emu.read(0x05,emu.memType.nesRam),returnPc))
end
local marks = {0xC000,0xC023,0xC0A1,0xC0BF,0xC0DC,0xC0F6,0xC0F9,0xC114,
    0xFECF,0xC162,0xCAE2,0xCA40,0xCA59,0xCA6B,0xCA79,0xCA7C,0xCA84,
    0xCA8C,0xCABB,0xCAD1,0xCAD8,0xCADF,0xE698,0xE7C6,0xEA36,0xFF06,0xFF17,0xFF19,0xF850}
if instructions then
    emu.addMemoryCallback(function(address)
        if address == 0xC000 then inNmi = true; log(address); return end
        if address == 0xC114 then log(address); inNmi = false; return end
        if not inNmi then log(address) end
    end,emu.callbackType.exec,0x8000,0xFFFF)
else
    for _,pc in ipairs(marks) do
        emu.addMemoryCallback(function(address) log(address) end,emu.callbackType.exec,pc,pc)
    end
end
emu.addEventCallback(function()
    if finished then return end
    poll = poll + 1
    log(0)
    if poll > (last or #input) then finished = true; out:close(); emu.stop(); return end
    local buttons = {}
    local byte = string.byte(input,poll)
    for index,name in ipairs(names) do buttons[name] = math.floor(byte/2^(index-1))%2==1 end
    emu.setInput(buttons,0)
    if poll%500 == 0 then out:flush() end
end,emu.eventType.inputPolled)
