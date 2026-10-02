local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local f=io.open("wp4_log.txt","w")
local d = m.debugger
f:write("consolelog type ", type(d.consolelog), " ", tostring(d.consolelog), "\n")
cpu.debug:wpset(mem, "w", 0x1f8010a8, 4, "(wpdata & 0x01000000) != 0", 'printf "DW 2 %08x pc=%08x\\n",wpdata,pc')
cpu.debug:wpset(mem, "w", 0x1f8010a8, 4, "(wpdata & 0x01000000) == 0", 'printf "W %08x %08x\\n",wpaddr,wpdata;g')
local n=0
local stops=0
emu.register_periodic(function()
  local d=m.debugger
  if d.execution_state=="stop" then
    stops=stops+1
    if stops<=6 then
      local cl = d.consolelog
      f:write("stop ", stops, " n=", #cl, " last=", tostring(cl[#cl]), " | prev=", tostring(cl[#cl-1]), "\n")
      d:command('printf "D test %d\\n",1')
      cl = d.consolelog
      f:write(" after command n=", #cl, " last=", tostring(cl[#cl]), "\n") f:flush()
    end
    d.execution_state="run"
  end
end)
emu.register_frame_done(function()
  n=n+1
  if n==450 then f:write("stops ", stops, "\n") f:close() m:exit() end
end)
