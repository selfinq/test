-- a03: dump static address maps (main + sub CPU), devices, regions, shares, banks
local m = manager.machine
local out = io.open("map_dump.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
log("system=%s %s", m.system.name, m.system.description)
for tag, dev in pairs(m.devices) do
  local clk = 0
  pcall(function() clk = dev.clock end)
  log("device %-28s shortname=%-12s clock=%s", tag, dev.shortname, tostring(clk))
end
local function dumpmap(tag)
  local dev = m.devices[tag]
  for sname, sp in pairs(dev.spaces) do
    log("== %s space=%s addrwidth-mask=%08x datawidth=%d endian=%s", tag, sname, sp.address_mask, sp.data_width, sp.endianness)
    local i = 0
    for _, e in pairs(sp.map.entries) do
      i = i + 1
      local r, w = e.read, e.write
      log("[%02d] %08x-%08x mirror=%08x mask=%08x unitmask=%x cswidth=%d  R=%s/%s/%s/bits%d  W=%s/%s/%s/bits%d share=%s region=%s+%x",
        i, e.address_start, e.address_end, e.address_mirror, e.address_mask, e.mask, e.cswidth,
        tostring(r.handlertype), tostring(r.name), tostring(r.tag), r.bits or -1,
        tostring(w.handlertype), tostring(w.name), tostring(w.tag), w.bits or -1,
        tostring(e.share), tostring(e.region), e.region_offset or 0)
    end
  end
end
dumpmap(":maincpu")
dumpmap(":sub")
for k,v in pairs(m.memory.shares) do log("share %s size=%x", k, v.size) end
for k,v in pairs(m.memory.regions) do log("region %s size=%x", k, v.size) end
for k,v in pairs(m.memory.banks) do log("bank %s entry=%d", k, v.entry) end
out:close()
m:exit()
