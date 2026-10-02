local m = manager.machine
local cpu = m.devices[":maincpu"]
print("cpu", cpu.shortname, cpu.tag)
for k,_ in pairs(cpu.spaces) do print("space", k) end
local st = cpu.state
local names = {}
for k,_ in pairs(st) do names[#names+1]=k end
table.sort(names); print("state:", table.concat(names, " "))
print("PC at frame0:", string.format("%08x", st["PC"].value))
local mem = cpu.spaces["program"]
print("first words at 1fc00000:", string.format("%08x %08x", mem:read_u32(0x1fc00000), mem:read_u32(0x1fc00004)))
local n=0
emu.register_frame_done(function()
  n=n+1
  if n==5 or n==200 then print("frame", n, string.format("PC=%08x", st["PC"].value)) end
  if n==200 then m:exit() end
end)
