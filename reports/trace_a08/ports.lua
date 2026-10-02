local m = manager.machine
for tag, port in pairs(m.ioport.ports) do
  print("PORT", tag)
  for name, f in pairs(port.fields) do
    print(string.format("   field '%s' mask=%08x type=%s default=%08x", name, f.mask, tostring(f.type), f.defvalue))
  end
end
for k,_ in pairs(m.devices) do print("DEV", k) end
local sub = m.devices[":sub"]
for k,_ in pairs(sub.spaces) do print("subspace", k) end
local n=0
emu.register_frame_done(function() n=n+1 if n==2 then m:exit() end end)
