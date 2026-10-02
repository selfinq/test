local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local n, done = 0, false
local log = io.open("firstram_log.txt", "w")
emu.register_frame_done(function()
  n = n + 1
  local pc = st["pc"].value
  if not done and (pc >> 28) == 8 then
    done = true
    local f = io.open("ram_firstgame.bin", "wb")
    for a = 0, 0x3fffff, 4 do f:write(string.pack("<I4", mem:read_u32(a))) end
    f:close()
    log:write(string.format("first frame with pc in RAM: %d pc=%08x\n", n, pc)); log:close(); m:exit()
  end
  if n > 1200 then log:write("never reached RAM\n"); log:close(); m:exit() end
end)
