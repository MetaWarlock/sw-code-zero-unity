-- Research only. Run with mesen-agent --lua-allow-io from the project root.
-- Does not write game RAM or alter controller state.
local dir = "Tools/DarkwingResearch/runs/checkpoints/"
local frame = 0
emu.addEventCallback(function()
    frame = frame + 1
    if frame % 300 == 0 then
        local f = assert(io.open(dir .. string.format("frame%04d.png", frame), "wb"))
        f:write(emu.takeScreenshot())
        f:close()
        emu.log("checkpoint " .. frame)
    end
end, emu.eventType.endFrame)
