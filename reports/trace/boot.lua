local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("boot_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
log("reset pc=%08x", st["pc"].value)
-- bank register writes (0x1f000000) and tektagt protection windows
local bankwrites = 0
mem:install_write_tap(0x1f000000, 0x1f000003, "bank", function(off, data, mask)
  bankwrites = bankwrites + 1
  if bankwrites <= 40 then log("bank write data=%08x mask=%08x pc=%08x", data, mask, st["pc"].value) end
  return data
end)
local n = 0
local dumps = {[120]=true, [400]=true, [900]=true}
emu.register_frame_done(function()
  n = n + 1
  if n % 60 == 1 or n < 5 then log("frame %d pc=%08x", n, st["pc"].value) end
  if dumps[n] then
    local f = io.open(string.format("ram_f%d.bin", n), "wb")
    for a = 0, 0x7fffff, 4 do f:write(string.pack("<I4", mem:read_u32(a))) end
    f:close()
    log("dumped ram at frame %d", n)
  end
  if n == 900 then log("bankwrites total %d", bankwrites); out:close(); m:exit() end
end)
