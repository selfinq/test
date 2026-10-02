local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
cpu.debug:wpset(mem, "w", 0x1f801000, 0x1000, "", 'printf "W %08x %08x %08x %08x\\n",wpaddr,wpdata,d@pc,pc;g')
cpu.debug:wpset(mem, "r", 0x1f801000, 0x1000, "", 'printf "R %08x %08x %08x %08x\\n",wpaddr,wpdata,d@pc,pc;g')
cpu.debug:wpset(mem, "w", 0x1f000000, 0x200000, "", 'printf "W %08x %08x %08x %08x\\n",wpaddr,wpdata,d@pc,pc;g')
local n=0
emu.register_periodic(function()
  local d=m.debugger
  if d.execution_state=="stop" then d.execution_state="run" end
end)
emu.register_frame_done(function()
  n=n+1
  if n==400 then m:exit() end
end)
