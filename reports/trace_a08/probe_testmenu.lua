-- Enter the System12 test program (Service Mode switch), open SWITCH TEST, hold selected inputs and snapshot.
KEEP = {}
local m = manager.machine
local scr = m.screens[":screen"]
local SP = (os.getenv("A08_OUT") or "/tmp/a08_out") .. "/"
local out = io.open("/home/user/test/reports/trace_a08/probe_testmenu_log.txt", "w")
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local frame = 0
local ev, snaps = {}, {}
ev[2600] = function() f1["Service Mode"]:set_value(1) end
ev[2610] = function() f1["Service Mode"]:set_value(0) end
ev[2900] = function() f0["P1 Down"]:set_value(1) end
ev[2906] = function() f0["P1 Down"]:set_value(0) end
ev[2930] = function() f0["P1 Button 1"]:set_value(1) end
ev[2936] = function() f0["P1 Button 1"]:set_value(0) end
snaps[3010] = "idle"
local t = 3020
local tests = {
  {f0,"P1 Button 1"},{f0,"P1 Button 2"},{f0,"P1 Button 3"},{f1,"P1 Button 4"},{f1,"P1 Button 5"},{f1,"P1 Button 6"},
  {f0,"P2 Button 1"},{f0,"P2 Button 2"},{f0,"P2 Button 3"},{f1,"P2 Button 4"},{f1,"P2 Button 5"},{f1,"P2 Button 6"},
  {f1,"Coin 1"},{f1,"Coin 2"},{f1,"Service 1"},{f0,"1 Player Start"},{f0,"2 Players Start"},{f0,"P1 Up"},{f0,"P1 Left"},
}
for _, tc in ipairs(tests) do
  local fl, name = tc[1], tc[2]
  ev[t] = function() fl[name]:set_value(1) end
  snaps[t + 12] = name
  ev[t + 14] = function() fl[name]:set_value(0) end
  t = t + 30
end
local lastend = t
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if snaps[frame] then
    local nm = snaps[frame]:gsub("[^%w]", "_")
    out:write(string.format("f%d snapshot %s\n", frame, snaps[frame]))
    pcall(function() scr:snapshot(SP..string.format("sw_%05d_%s.png", frame, nm)) end)
  end
  if frame == lastend + 5 then out:close(); m:exit() end
end)
