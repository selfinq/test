local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("appear_log.txt", "w")
local function log(...) out:write(string.format(...), "\n"); out:flush() end
local function nonzero(base, size)
  local c = 0
  for a = base, base + size - 1, 64 do if mem:read_u32(a) ~= 0 then c = c + 1 end end
  return c
end
local n, prev = 0, -1
emu.register_frame_done(function()
  n = n + 1
  local c = nonzero(0x100000, 0xb0000)
  if c ~= prev and (n < 400) then
    log("frame %d pc=%08x ra=%08x sp=%08x a0=%08x a1=%08x a2=%08x nonzero64B(0x100000..0x1b0000)=%d", n, st["pc"].value, st["ra"].value, st["sp"].value, st["a0"].value, st["a1"].value, st["a2"].value, c)
    prev = c
  end
  if n == 400 then out:close(); m:exit() end
end)
