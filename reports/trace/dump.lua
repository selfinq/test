local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local n = 0
emu.register_frame_done(function()
  n = n + 1
  if n == 1500 then
    local f = io.open("ram_game_f1500.bin", "wb")
    for a = 0, 0x3fffff, 4 do f:write(string.pack("<I4", mem:read_u32(a))) end
    f:close()
    local g = io.open("dump_info.txt", "w")
    g:write(string.format("frame 1500 pc=%08x sp=%08x ra=%08x\n", st["pc"].value, st["sp"].value, st["ra"].value)); g:close()
    m:exit()
  end
end)
