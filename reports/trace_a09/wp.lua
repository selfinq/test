local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = {} for k,v in pairs(cpu.state) do st[k]=v end
local f=io.open("wp_log.txt","w")
local ks={} for k,v in pairs(cpu.spaces) do ks[#ks+1]=k end
f:write("spaces: ", table.concat(ks,","), "\n")
local idx = cpu.debug:wpset(mem, "w", 0x1f8010a8, 4, "", "")
f:write("wp idx ", tostring(idx), "\n")
local idx2 = cpu.debug:wpset(mem, "w", 0x1f801810, 8, "", "")
local n=0
local hits=0
emu.register_periodic(function()
  local d=m.debugger
  if d.execution_state=="stop" then
    hits=hits+1
    if n>=370 and n<390 then f:write(string.format("stop #%d frame %d pc=%08x\n", hits, n, st["pc"].value)) f:flush() end
    d.execution_state="run"
  end
end)
emu.register_frame_done(function()
  n=n+1
  if n==450 then f:write("hits ",hits,"\n") f:close() m:exit() end
end)
