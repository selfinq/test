local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = {} for k,v in pairs(cpu.state) do st[k]=v end
local f=io.open("wp2_log.txt","w")
cpu.debug:wpset(mem, "w", 0x1f8010a0, 0x10, "", 'printf "DMA2 %08x %08x pc=%08x\\n",wpaddr,wpdata,pc;g')
cpu.debug:wpset(mem, "w", 0x1f801810, 8, "", 'printf "GPU %08x %08x pc=%08x\\n",wpaddr,wpdata,pc;g')
local n=0
emu.register_periodic(function()
  local d=m.debugger
  if d.execution_state=="stop" then f:write(string.format("stop frame %d pc=%08x\n", n, st["pc"].value)) f:flush() d.execution_state="run" end
end)
emu.register_frame_done(function()
  n=n+1
  if n==450 then f:close() m:exit() end
end)
