local m = manager.machine
local cpu = m.devices[":maincpu"]
local sp = cpu.spaces["program"]
local mp = sp.map
local e = mp.entries[1]
for _,k in ipairs{"addrstart","addrend","addrmirror","addrmask","addrselect","address_start","address_end","address_mirror","address_mask","address_select","mask","cswidth","share","region","region_offset","read","write","start","end_","stop","mirror"} do
  local ok,v = pcall(function() return e[k] end)
  print(k, ok, v)
end
m:exit()
