local m = manager.machine
local cpu = m.devices[":maincpu"]
local names = {}
for k,_ in pairs(cpu.state) do names[#names+1]=k end
table.sort(names); print("state:", table.concat(names, " "))
local scr = m.screens[":screen"]
print("screen", scr, scr.width, scr.height, scr.refresh, scr.frame_number, scr.visible_area)
local ks = {}
for _,k in ipairs{"refresh","period","vblank","vpos","hpos","frame_period","time_until_pos","width","height","xoffset"} do
  local ok,v = pcall(function() return scr[k] end); print(k, ok, ok and type(v)=="function" and "fn" or v)
end
local sub = m.devices[":sub"]
local names = {}
for k,_ in pairs(sub.state) do names[#names+1]=k end
table.sort(names); print("substate:", table.concat(names, " "))
print(cpu.spaces["program"]:read_u32(0x1f801060))
m:exit()
