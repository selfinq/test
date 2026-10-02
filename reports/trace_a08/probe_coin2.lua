KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local scr = m.screens[":screen"]
local SP = "/tmp/claude-0/-home-user-test/af2ad0ad-b38d-519b-aaa8-37707f74d539/scratchpad/"
local out = io.open("/home/user/test/reports/trace_a08/probe_coin2_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local lastw = {}
KEEP[#KEEP+1] = smem:install_write_tap(0x080000, 0x083fff, "ssw", function(off, data, mask) lastw[(off & 0xffff) & ~1] = sst["PC"].value; return data end)
local mrd = {}
local mw = {}
KEEP[#KEEP+1] = mem:install_write_tap(0x1f080000, 0x1f083fff, "msw", function(off, data, mask)
  if COINWIN then log("  f%d MAIN WRITE shared %05x data=%08x mask=%08x pc=%08x", FRAME, off & 0x3ffc, data, mask, st["pc"].value) end
  return data end)
local function snapsh() local t = {} for a = 0, 0x3fff, 2 do t[a] = smem:read_u16(0x080000 + a) end return t end
local function diffsh(a, b, label)
  for ad = 0, 0x3fff, 2 do
    if a[ad] ~= b[ad] and ad ~= 0x594 and ad ~= 0x300e and ad ~= 0x3036 then
      log("  %s sh[%05x] %04x -> %04x writer=%s", label, 0x80000 + ad, a[ad], b[ad], lastw[ad] and string.format("%06x", lastw[ad]) or "?")
    end
  end
end
local fld = m.ioport.ports[":IN1"].fields["Coin 1"]
local frame = 0
local S0 = 2600
local base
FRAME = 0
emu.register_frame_done(function()
  frame = frame + 1; FRAME = frame
  if frame == S0 - 1 then base = snapsh() end
  if frame == S0 then fld:set_value(1); COINWIN = true; log("press coin at frame %d", frame) end
  if frame > S0 and frame < S0 + 90 and (frame - S0) % 2 == 0 then
    local b = snapsh(); local before = #tostring(0)
    log(" t+%d", frame - S0); diffsh(base, b, "sh"); base = b
  end
  if frame == S0 + 10 then fld:set_value(0); log("release coin at frame %d", frame) end
  if frame == S0 + 60 then pcall(function() scr:snapshot(SP.."coin_after.png") end) end
  if frame == S0 + 100 then out:close(); m:exit() end
end)
