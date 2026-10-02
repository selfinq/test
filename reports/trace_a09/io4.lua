local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = {} for k,v in pairs(cpu.state) do st[k]=v end
local f=io.open("io4_log.txt","w")
local n=0
local tapc, tap2, tap3, tap4=0,0,0,0
mem:install_write_tap(0x1f801000, 0x1f801fff, "w3", function(o,d,mk) tapc=tapc+1 return d end)
mem:install_write_tap(0x1f8010a8, 0x1f8010ab, "w4", function(o,d,mk) tap4=tap4+1 return d end)
local installed=false
local last=-1
emu.register_frame_done(function()
  n=n+1
  if n==200 and not installed then installed=true
    mem:install_write_tap(0x1f8010a8, 0x1f8010ab, "w5", function(o,d,mk) tap2=tap2+1 return d end)
    mem:install_write_tap(0x1f801810, 0x1f801817, "w6", function(o,d,mk) tap3=tap3+1 return d end)
  end
  if n>=360 and n<=380 or n%200==0 then
    f:write(string.format("f%d pc=%08x tapc=%d tap4=%d tap2=%d tap3=%d dma2chcr=%08x\n", n, st["pc"].value, tapc, tap4, tap2, tap3, mem:read_u32(0x1f8010a8)))
    f:flush()
  end
  if n==800 then m:exit() end
end)
