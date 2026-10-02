local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = {} for k,v in pairs(cpu.state) do st[k]=v end
local f=io.open("io3_log.txt","w")
local n=0
local tapc=0
mem:install_write_tap(0x1f801000, 0x1f801fff, "w3", function(o,d,mk) tapc=tapc+1 return d end)
emu.register_frame_done(function()
  n=n+1
  if n%100==0 or (n>=1000 and n<1006) then
    f:write(string.format("f%d pc=%08x sr=%08x cause=%08x istat=%08x imask=%08x gpustat=%08x dma2chcr=%08x dpcr=%08x madr2=%08x tapc=%d\n", n, st["pc"].value, st["SR"].value, st["Cause"].value,
      mem:read_u32(0x1f801070), mem:read_u32(0x1f801074), mem:read_u32(0x1f801814), mem:read_u32(0x1f8010a8), mem:read_u32(0x1f8010f0), mem:read_u32(0x1f8010a0), tapc))
    f:flush()
  end
  if n==1500 then m:exit() end
end)
