local m = manager.machine
local out = io.open("/home/user/test/reports/trace_a08/probe_outputs_log.txt", "w")
local ok, e = pcall(function()
  m.output:set_global_notifier(function(name, value) out:write(string.format("OUTPUT %s = %s\n", name, tostring(value))) end)
end)
out:write("notifier install: ", tostring(ok), " ", tostring(e), "\n")
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local frame = 0
local ev = {
  [2600] = function() f1["Coin 1"]:set_value(1) end,
  [2606] = function() f1["Coin 1"]:set_value(0) end,
  [2700] = function() f0["1 Player Start"]:set_value(1) end,
  [2706] = function() f0["1 Player Start"]:set_value(0) end,
}
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if frame == 2900 then out:close(); m:exit() end
end)
