local m = manager.machine
local cpu = m.devices[":maincpu"]
local f = io.open("bp2_log.txt","w")
cpu.debug:bpset(0x80010000, "", "")
local n=0
local last
emu.register_periodic(function()
  local d = m.debugger
  local s = d.execution_state
  local pc = cpu.state["pc"].value
  if s ~= last or pc>>28==8 then
  f:write(string.format("periodic n=%d pc=%08x state=%s paused=%s\n", n, pc, s, tostring(m.paused))); f:flush() end
  last = s
  if s=="stop" and n>5 then d.execution_state="run" end
end)
emu.register_frame_done(function()
  n=n+1
  if n==380 then m:exit() end
end)
