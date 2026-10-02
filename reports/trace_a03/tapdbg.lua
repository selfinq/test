local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local hits = 0
local f = 0
local h = mem:install_write_tap(0x1f801070, 0x1f801077, "irq", function(off, data, mask) hits = hits + 1; return data end)
print("handle", h, type(h))
for _,k in ipairs{"remove","reinstall"} do local ok,v=pcall(function() return h[k] end); print(k, ok, v) end
emu.register_frame_done(function()
  f = f + 1
  local ok, err = pcall(function() h:reinstall() end)
  if f <= 2 then print("reinstall", ok, err) end
  if f % 50 == 0 or f==1 then
    print(string.format("frame %d hits=%d pc=%08x imask=%08x", f, hits, st["pc"].value, mem:read_u32(0x1f801074)))
  end
  if f >= 500 then m:exit() end
end)
