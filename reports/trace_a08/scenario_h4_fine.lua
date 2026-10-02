-- Fine-grained: coin at frame 2600 (6 frames), P1 start at 2700 (6 frames); snapshot every frame 2700..2740.
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state
local scr = m.screens[":screen"]
local SP = "/tmp/claude-0/-home-user-test/af2ad0ad-b38d-519b-aaa8-37707f74d539/scratchpad/seq2/"
local out = io.open("/home/user/test/reports/trace_a08/scenario_h4_fine_log.txt", "w")
local frame = 0
local function log(...) out:write(string.format("f%d ", frame), string.format(...), "\n") end
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 2600 then f1["Coin 1"]:set_value(1) log("coin down") end
  if frame == 2606 then f1["Coin 1"]:set_value(0) log("coin up") end
  if frame == 2700 then f0["1 Player Start"]:set_value(1) log("start down") end
  if frame == 2706 then f0["1 Player Start"]:set_value(0) log("start up") end
  if frame >= 2690 and frame <= 2740 then
    log("pc=%08x", st["pc"].value)
    pcall(function() scr:snapshot(SP..string.format("s%05d.png", frame)) end)
  end
  if frame == 2741 then out:close(); m:exit() end
end)
