local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local w, r = {}, {}
local function key(off) return string.format("%08x", 0x1f801000+off) end
mem:install_write_tap(0x1f801000, 0x1f801fff, "iow", function(off, data, mask)
  local k = key(off); w[k] = (w[k] or 0)+1; return data end)
mem:install_read_tap(0x1f801000, 0x1f801fff, "ior", function(off, data, mask)
  local k = key(off); r[k] = (r[k] or 0)+1; return data end)
local n=0
emu.register_frame_done(function()
  n=n+1
  if n==450 then
    local f=io.open("io_log.txt","w")
    local ks={} for k in pairs(w) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do f:write("W ",k," ",w[k],"\n") end
    ks={} for k in pairs(r) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do f:write("R ",k," ",r[k],"\n") end
    f:close(); m:exit() end
end)
