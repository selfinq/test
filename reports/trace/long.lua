local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("long_log.txt", "w")
local function log(...) out:write(string.format(...), "\n"); out:flush() end
local bw, pw = 0, 0
mem:install_write_tap(0x1f000000, 0x1f000003, "bank", function(off, data, mask)
  bw = bw + 1; if bw <= 60 then log("bank write data=%08x mask=%08x pc=%08x", data, mask, st["pc"].value) end; return data end)
mem:install_write_tap(0x1fb00000, 0x1fb80003, "prot", function(off, data, mask)
  pw = pw + 1; if pw <= 20 then log("prot write off=%x data=%08x pc=%08x", off, data, st["pc"].value) end; return data end)
local n = 0
local pcs = {}
emu.register_frame_done(function()
  n = n + 1
  local pc = st["pc"].value & 0x1fffffff
  pcs[pc] = (pcs[pc] or 0) + 1
  if n % 300 == 0 then log("frame %d pc=%08x bankwrites=%d protwrites=%d", n, st["pc"].value, bw, pw) end
  
  if n == 4000 then
    local keys = {} for k,_ in pairs(pcs) do keys[#keys+1]=k end table.sort(keys)
    for _,k in ipairs(keys) do log("pc %08x seen %d", k, pcs[k]) end
    out:close(); m:exit()
  end
end)
