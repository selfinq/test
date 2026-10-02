-- Does MAME 0.264 expose ANY output (lamp / counter / LED) for tektagt? Enumerate output ids at several moments.
local m = manager.machine
local out = io.open("/home/user/test/reports/trace_a08/probe_outputs_log.txt", "w")
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local function dump(tag)
  local n = 0
  for i = 0, 1000 do
    local nm = m.output:id_to_name(i)
    if nm then n = n + 1 out:write(string.format("%s: output id %d name %s value %s\n", tag, i, nm, tostring(m.output:get_value(nm)))) end
  end
  out:write(string.format("%s: %d outputs\n", tag, n))
end
local frame = 0
local ev = {
  [300] = function() dump("frame 300 (boot)") end,
  [2600] = function() f1["Coin 1"]:set_value(1) end,
  [2606] = function() f1["Coin 1"]:set_value(0) end,
  [2650] = function() dump("frame 2650 (after coin)") end,
  [2700] = function() f0["1 Player Start"]:set_value(1) end,
  [2706] = function() f0["1 Player Start"]:set_value(0) end,
  [2800] = function() dump("frame 2800 (character select)") end,
}
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if frame == 2801 then out:close(); m:exit() end
end)
