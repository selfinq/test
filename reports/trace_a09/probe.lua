local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local f=io.open("probe_log.txt","w")
local function p(...) f:write(string.format(...),"\n") f:flush() end
p("emu.hash: %s", tostring(emu.hash))
if emu.hash then
  local h = emu.hash("sha256")
  p("hash obj %s", tostring(h))
end
p("read_range: %s", tostring(mem.read_range))
p("os: %s %s", tostring(os), tostring(os and os.getenv))
p("lua version %s", _VERSION)
p("string.pack %s", tostring(string.pack))
p("cpu.state keys:")
local ks={} for k,v in pairs(cpu.state) do ks[#ks+1]=k end table.sort(ks)
p("%s", table.concat(ks," "))
local n=0
emu.register_frame_done(function() n=n+1 if n==5 then
  local t=os.clock()
  local s = mem:read_range(0x80000000,0x803fffff,32)
  p("read_range len %d time %f", #s, os.clock()-t)
  if emu.hash then
    t=os.clock()
    p("sha256 %s", emu.hash("sha256", s) or "nil")
  end
  m:exit() end end)
