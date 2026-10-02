local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local f=io.open("io2_log.txt","w")
local cnt=0
local function h(kind) return function(off, data, mask)
  cnt=cnt+1
  if cnt<=4000 then f:write(string.format("%s off=%08x data=%08x mask=%08x pc=%08x\n", kind, off, data, mask, st["pc"].value)) end
  return data end end
mem:install_write_tap(0x1f000000, 0x1f1fffff, "w1", h("W"))
mem:install_write_tap(0x1f700000, 0x1f70ffff, "w2", h("W"))
mem:install_write_tap(0x1f801000, 0x1f801fff, "w3", h("W"))
mem:install_write_tap(0x1fa00000, 0x1fbfffff, "w4", h("W"))
mem:install_read_tap(0x1f000000, 0x1f1fffff, "r1", h("R"))
mem:install_read_tap(0x1f700000, 0x1f70ffff, "r2", h("R"))
mem:install_read_tap(0x1fb00000, 0x1fbfffff, "r4", h("R"))
local n=0
emu.register_frame_done(function() n=n+1 if n==700 then f:close() m:exit() end end)
