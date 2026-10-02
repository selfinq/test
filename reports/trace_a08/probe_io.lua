-- Probe 1: who touches what: EEPROM window, sharedram (both sides), H8 input reg, watchdog etc.
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_io_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local frame = 0
KEEP = {}  -- tap handles must be kept alive or Lua GC removes the tap
local cnt = {}
local function lim(key, n) cnt[key] = (cnt[key] or 0) + 1; return cnt[key] <= n end
-- main side
KEEP[#KEEP+1] = mem:install_read_tap(0x1f140000, 0x1f140fff, "eer", function(off, data, mask)
  if lim("eer", 30) then log("f%d MAIN eeprom R off=%08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end
  return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f140000, 0x1f140fff, "eew", function(off, data, mask)
  if lim("eew", 60) then log("f%d MAIN eeprom W off=%08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end
  return data end)
for _, a in ipairs({0x1f008000, 0x1f010000, 0x1f018000, 0x1f1bff08}) do
  KEEP[#KEEP+1] = mem:install_write_tap(a, a+3, "w"..a, function(off, data, mask)
    if lim("w"..a, 8) then log("f%d MAIN W %08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end
    return data end)
end
KEEP[#KEEP+1] = mem:install_read_tap(0x1f080000, 0x1f083fff, "shr", function(off, data, mask)
  local k = string.format("shr%04x", off & 0x3fff)
  if lim(k, 3) then log("f%d MAIN shared R off=%08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end
  return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f080000, 0x1f083fff, "shw", function(off, data, mask)
  local k = string.format("shw%04x", off & 0x3fff)
  if lim(k, 3) then log("f%d MAIN shared W off=%08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end
  return data end)
-- H8 side
KEEP[#KEEP+1] = smem:install_read_tap(0x300000, 0x300003, "in", function(off, data, mask)
  local k = string.format("in%08x_%02x", off, data & 0xff)
  if lim(k, 1) then log("f%d H8 in_r off=%06x data=%02x mask=%04x pc=%06x", frame, off, data, mask, sst["PC"].value) end
  return data end)
KEEP[#KEEP+1] = smem:install_write_tap(0x080000, 0x08ffff, "ssw", function(off, data, mask)
  local k = string.format("ssw%05x", off)
  if lim(k, 2) then log("f%d H8 shared W off=%06x data=%04x mask=%04x pc=%06x", frame, off, data, mask, sst["PC"].value) end
  return data end)
emu.register_frame_done(function()
  frame = frame + 1
  if frame % 200 == 0 then log("frame %d main pc=%08x h8 pc=%06x", frame, st["pc"].value, sst["PC"].value) end
  if frame == 1300 then
    local keys = {} for k,v in pairs(cnt) do keys[#keys+1] = k end table.sort(keys)
    for _, k in ipairs(keys) do log("COUNT %s %d", k, cnt[k]) end
    out:close(); m:exit()
  end
end)
