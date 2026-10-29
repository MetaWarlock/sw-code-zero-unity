-- Deterministic replay of a recorded NES pad track, plus per-frame fingerprints of the original.
-- Rendered by Tools/DarkwingResearch/stand/stand.py (tokens "D:/Art/SW Code Zero Unity Builds/Mission3-Liquidator/Temp/m3_original_validation/replay2", "D:/Art/SW Code Zero Unity Builds/Mission3-Liquidator/Playthroughs/20261024T183247Z/input.bin", 60,
-- ). Controller-only: never writes emulated memory, never warps.
--
-- Frame model, measured on this ROM with mesen-agent: Mesen raises exactly one InputPolled and one
-- EndFrame per emulated NES frame (120 polls = 120 frames in 2 s), and its own controller-refresh
-- counter (controlManager.pollCounter, the pollCnt column) advances by exactly 1 per poll in every
-- run measured -- 300/300 from reset and 399/399 from a mission state. So the poll index IS the
-- emulator frame index and one byte per poll describes one whole frame. Mesen refreshes the device
-- state at that same point its own movie recorder samples it, which is why byte i of input.bin is
-- also line i+1 of a Mesen movie. Emulator pause executes no frames and therefore adds no polls.
-- A state load or rewind in the middle of a run is recorded as INVALID and makes the trace after
-- it worthless: the tooling refuses such a session instead of silently trusting a broken trace.
-- mesen-agent executes at exactly real time (3000 polls in 49.94 s, 600 in 10.01 s, with the full
-- per-frame fingerprinting and with an empty callback alike, and --no-max-speed changes nothing),
-- so a replay costs one second per 60 recorded frames.
local dir = "D:/Art/SW Code Zero Unity Builds/Mission3-Liquidator/Temp/m3_original_validation/replay2"
local fullEvery = 60
local BITS = { right = 1, left = 2, down = 4, up = 8, start = 16, select = 32, b = 64, a = 128 }
local NAMES = { "right", "left", "down", "up", "start", "select", "b", "a" }
local ram, pal, oam, nt = emu.memType.nesInternalRam, emu.memType.nesPaletteRam,
	emu.memType.nesSpriteRam, emu.memType.nesNametableRam
local HEADER = "poll,abs_frame,pollCnt,pc,x,y,state,ctrl,mask,ram_sum,pal_sum\n"
-- Read-only MMC1 observer, shared semantics with runtime_capture.lua.in.
-- Sprite/pattern banks are required when equal object RAM still renders differently.
local mmc1 = { shift=0, count=0, ctrl=-1, chr0=-1, chr1=-1, prg=-1 }
emu.addMemoryCallback(function(address,value)
    if value >= 0x80 then mmc1.shift,mmc1.count=0,0; return end
    mmc1.shift=math.floor(mmc1.shift/2)+(value%2)*16; mmc1.count=mmc1.count+1
    if mmc1.count ~= 5 then return end
    local reg=math.floor(address/0x2000)%4
    if reg==0 then mmc1.ctrl=mmc1.shift
    elseif reg==1 then mmc1.chr0=mmc1.shift
    elseif reg==2 then mmc1.chr1=mmc1.shift
    else mmc1.prg=mmc1.shift end
    mmc1.shift,mmc1.count=0,0
end,emu.callbackType.write,0x8000,0xFFFF)

local log = assert(io.open(dir .. "/stand_log.txt", "a"))
local function note(text)
	log:write(text .. "\n"); log:flush()
	emu.log(text)
end
local function r(a) return emu.read(a, ram, false) end
local function sum(kind, from, count)
	local s = 0
	for a = from, from + count - 1 do s = s + emu.read(a, kind, false) end
	return s
end
local function padTable(byte)
	local t = {}
	for _, n in ipairs(NAMES) do t[n] = math.floor(byte / BITS[n]) % 2 == 1 end
	return t
end
local function on(event, fn)
	local t = emu.eventType[event]
	if t == nil then note("WARN emu.eventType." .. event .. " missing") return end
	emu.addEventCallback(fn, t)
end

-- One row per frame: Mesen's own frame counter, its controller-read counter, the CPU program
-- counter, the original's player X ($4F0|$500<<8) and Y ($510|$520<<8) pairs, player state $50,
-- the controller shadows $FF/$FE, and checksums of work RAM $0400-$05FF plus the 32-byte palette.
-- Nametable and OAM checksums appear as "# full" comment lines every 60 frames, so a
-- plain-text diff stays small while the picture-bearing memory is still covered.
local function traceRow(trc, poll, st)
	trc:write(string.format("%d,%d,%d,%04X,%d,%d,%d,%d,%d,%d,%d\n", poll, st["frameCount"],
		st["controlManager.pollCounter"] or 0, st["cpu.pc"],
		r(0x4F0) + r(0x500) * 256, r(0x510) + r(0x520) * 256, r(0x50), r(0xFF), r(0xFE),
		sum(ram, 0x400, 512), sum(pal, 0, 32)))
	if poll % fullEvery == 0 then
		-- 2048 = the $2000-$27FF window Mesen exposes as nametable RAM; the port mirrors to the
		-- same 2 KiB, so both sides sum exactly the bytes the console can see.
		trc:write(string.format("# full %d nt=%d oam=%d\n", poll, sum(nt, 0, 2048), sum(oam, 0, 256)))
	end
end

local f = assert(io.open("D:/Art/SW Code Zero Unity Builds/Mission3-Liquidator/Playthroughs/20261024T183247Z/input.bin", "rb"))
local data = f:read("*a"); f:close()
local shots = {}
for n in string.gmatch("", "%d+") do shots[tonumber(n)] = true end
local trc = assert(io.open(dir .. "/trace.csv", "w"))
local buffers = assert(io.open(dir .. "/buffers.bin", "wb"))
local chr = assert(io.open(dir .. "/chr_map.csv", "w"))
chr:write("poll,ctrl,chr0,chr1,prg\n")
local function raw(kind, count)
	local b = {}
	for a = 0, count - 1 do b[#b+1] = string.char(emu.read(a, kind, false)) end
	return table.concat(b)
end
trc:write(HEADER)
local poll = 0

on("inputPolled", function()
	poll = poll + 1
	local byte = 0
	if poll <= #data then byte = string.byte(data, poll) end
	emu.setInput(padTable(byte), 0)
	-- The run keeps the pad idle past the end of the track but stops fingerprinting there:
	-- mesen-agent overshoots --frames on long tracks (measured: --frames 24261 --timeout 600
	-- executed 36046 frames and reported reason "timeout", while --frames 6000 stopped at 6000),
	-- and a trace that ends with the track is what makes two runs of one session comparable.
	if poll > #data then return end
	traceRow(trc, poll, emu.getState())
    chr:write(string.format("%d,%d,%d,%d,%d\n",poll,mmc1.ctrl,mmc1.chr0,mmc1.chr1,mmc1.prg))
	-- Fixed record: 2048 RAM + 2048 physical NT + 256 OAM + 32 palette bytes.
	-- Sums above remain diagnostics; strict checks compare these bytes (or SHA-256).
	buffers:write(raw(ram,2048), raw(nt,2048), raw(oam,256), raw(pal,32))
	if shots[poll] then
		local p = assert(io.open(dir .. "/frame_" .. poll .. ".png", "wb"))
		p:write(emu.takeScreenshot()); p:close()
		note("shot poll=" .. poll)
	end
	trc:flush()
end)
on("stateLoaded", function() note("INVALID poll=" .. poll .. " state loaded") end)
-- Only a load moves the console. A save does not: mesen-agent wrote its own auto-save state into
-- the run's isolated --home (SaveStates/<rom>_11.mss) at poll 18029 of a long replay, ~300 s in,
-- and the trace kept advancing by exactly one per frame afterwards.
on("stateSaved", function() note("state saved poll=" .. poll) end)
on("scriptEnded", function()
	note("replay end polls=" .. poll .. " traced=" .. math.min(poll, #data) .. " track=" .. #data)
	trc:close(); buffers:close(); chr:close(); log:close()
end)
note("replay start track=" .. #data)
