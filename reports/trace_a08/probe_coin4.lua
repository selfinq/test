KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local scr = m.screens[":screen"]
local SP = "/tmp/claude-0/-home-user-test/af2ad0ad-b38d-519b-aaa8-37707f74d539/scratchpad/"
local out = io.open("/home/user/test/reports/trace_a08/probe_coin4_log.txt", "w")
local frame = 0
local function log(...) out:write(string.format("f%d ", frame), string.format(...), "\n") end
local function interesting(a) a = a & 0x3fff
  return (a >= 0x3100 and a < 0x3200) or (a >= 0x3280 and a < 0x32c4) or (a >= 0x3900 and a < 0x3960) or a == 0x3200 or a == 0x3240 or (a>=0x3200 and a<0x3260) or (a>=0x3380 and a < 0x3390) end
KEEP[#KEEP+1] = mem:install_write_tap(0x1f083000, 0x1f083fff, "mw", function(off, data, mask)
  if interesting(off) then log("MAIN W %05x data=%08x mask=%08x pc=%08x", 0x80000+(off&0x3fff), data, mask, st["pc"].value) end return data end)
local lastr = {}
KEEP[#KEEP+1] = mem:install_read_tap(0x1f083000, 0x1f083fff, "mr", function(off, data, mask)
  if interesting(off) then
    local k = off & 0x3fff
    local key = string.format("%x:%x", data, mask)
    if lastr[k] ~= key then lastr[k] = key; log("MAIN R(changed) %05x data=%08x mask=%08x pc=%08x", 0x80000+k, data, mask, st["pc"].value) end
  end return data end)
KEEP[#KEEP+1] = smem:install_write_tap(0x083000, 0x083fff, "hw", function(off, data, mask)
  if interesting(off) and off ~= 0x83036 then
    log("H8 W %05x data=%04x pc=%06x", off, data, sst["PC"].value) end return data end)
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local ev = {
  [2600] = function() f1["Coin 1"]:set_value(1) log("-- Coin1 down") end,
  [2606] = function() f1["Coin 1"]:set_value(0) log("-- Coin1 up") end,
  [2650] = function() f1["Coin 2"]:set_value(1) log("-- Coin2 down") end,
  [2656] = function() f1["Coin 2"]:set_value(0) log("-- Coin2 up") end,
  [2700] = function() f1["Coin 1"]:set_value(1) log("-- Coin1 down") end,
  [2701] = function() f1["Coin 1"]:set_value(0) log("-- Coin1 up (1 frame pulse)") end,
  [2750] = function() pcall(function() scr:snapshot(SP.."credits_before_start.png") end) end,
  [2760] = function() f0["1 Player Start"]:set_value(1) log("-- P1 start down") end,
  [2766] = function() f0["1 Player Start"]:set_value(0) log("-- P1 start up") end,
  [2840] = function() pcall(function() scr:snapshot(SP.."after_start.png") end) end,
}
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if frame == 2850 then out:close(); m:exit() end
end)
