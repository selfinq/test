KEEP = {}
local m = manager.machine
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local scr = m.screens[":screen"]
local out = io.open("/home/user/test/reports/trace_a08/probe_coin3_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local function dump(label)
  log("== %s", label)
  for base = 0x3000, 0x3fff, 0x40 do
    local nz = {}
    for a = base, base + 0x3e, 2 do
      local v = smem:read_u16(0x080000 + a)
      if v ~= 0 then nz[#nz+1] = string.format("%03x:%04x", a, v) end
    end
    if #nz > 0 then log("  %s", table.concat(nz, " ")) end
  end
end
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local ev = {
  [2590] = function() dump("idle before coin") end,
  [2600] = function() f1["Coin 1"]:set_value(1) end,
  [2606] = function() f1["Coin 1"]:set_value(0) end,
  [2640] = function() dump("after coin 1 (credit 1?)") end,
  [2650] = function() f1["Coin 2"]:set_value(1) end,
  [2656] = function() f1["Coin 2"]:set_value(0) end,
  [2690] = function() dump("after coin 2") end,
  [2700] = function() f0["1 Player Start"]:set_value(1) end,
  [2706] = function() f0["1 Player Start"]:set_value(0) end,
  [2740] = function() dump("after P1 start") end,
}
local frame = 0
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if frame == 2760 then out:close(); m:exit() end
end)
