local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = {} for k,v in pairs(cpu.state) do st[k]=v end
local f=io.open("gte_log.txt","w")
local D = { "vxy0","vz0","vxy1","vz1","vxy2","vz2","rgb","otz","ir0","ir1","ir2","ir3","sxy0","sxy1","sxy2","sxyp","sz0","sz1","sz2","sz3","rgb0","rgb1","rgb2","res1","mac0","mac1","mac2","mac3","irgb","orgb","lzcs","lzcr" }
local C = { "r11r12","r13r21","r22r23","r31r32","r33","trx","try","trz","l11l12","l13l21","l22l23","l31l32","l33","rbk","gbk","bbk","lr1lr2","lr31g1","lg2lg3","lb1lb2","lb3","rfc","gfc","bfc","ofx","ofy","h","dqa","dqb","zsf3","zsf4","flag" }
local function dump(tag)
  local t={}
  for _,n in ipairs(D) do t[#t+1]=string.format("%s=%x",n,st[n].value) end
  for _,n in ipairs(C) do t[#t+1]=string.format("%s=%x",n,st[n].value) end
  f:write(tag," ",table.concat(t," "),"\n")
end
dump("before")
for _,n in ipairs(D) do st[n].value = 0 end
for _,n in ipairs(C) do st[n].value = 0 end
dump("after")
local n=0
emu.register_frame_done(function()
  n=n+1
  if n==100 then dump("f100")
    local s = mem:read_range(0x1f800000, 0x1f8003ff, 8)
    f:write("scratch len ", #s, " nz ", select(2, s:gsub("[^\0]", "")), " pc=", string.format("%x", st["pc"].value), "\n")
    local w={} for i=0,7 do w[#w+1]=string.format("%08x", mem:read_u32(0x1f800000+4*i)) end
    f:write(table.concat(w," "),"\n")
    f:close() m:exit() end
end)
