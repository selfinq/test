local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("loader_log.txt", "w")
local function log(...) out:write(string.format(...), "\n"); out:flush() end
local count, tap = 0, nil
local seenpc = {}
tap = mem:install_write_tap(0x00100000, 0x001fffff, "codewrite", function(off, data, mask)
  local pc = st["pc"].value
  if not seenpc[pc] then
    seenpc[pc] = true
    count = count + 1
    log("first write by pc=%08x addr=%08x data=%08x a0=%08x a1=%08x a2=%08x a3=%08x s0=%08x s1=%08x ra=%08x", pc, off, data, st["a0"].value, st["a1"].value, st["a2"].value, st["a3"].value, st["s0"].value, st["s1"].value, st["ra"].value)
    if count >= 40 then tap:remove(); log("tap removed") end
  end
  return data
end)
local n = 0
emu.register_frame_done(function() n = n + 1; if n == 700 then out:close(); m:exit() end end)
