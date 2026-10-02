-- Fresh NVRAM boot: log every main-CPU access to the AT28C16 window with emulated time (us).
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_eeprom_fresh_log.txt", "w")
local frame = 0
local nlog = 0
local function log(...) nlog = nlog + 1 if nlog < 60000 then out:write(string.format("f%d t=%.1fus ", frame, m.time:as_double() * 1e6), string.format(...), "\n") end end
KEEP[#KEEP+1] = mem:install_read_tap(0x1f140000, 0x1f140fff, "er", function(off, data, mask)
  log("R addr=%08x data=%08x mask=%08x pc=%08x", off, data, mask, st["pc"].value) return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f140000, 0x1f140fff, "ew", function(off, data, mask)
  log("W addr=%08x data=%08x mask=%08x pc=%08x", off, data, mask, st["pc"].value) return data end)
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 2000 then out:close(); m:exit() end
end)
