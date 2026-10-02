-- a03: assorted dynamic checks (run with scratch NVRAM). Output: final_checks.txt
local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local rom = m.memory.regions[":maincpu:rom"]
local out = io.open("final_checks.txt", "w")
local function log(...) out:write(string.format(...), "\n"); out:flush() end
local handles, frame = {}, 0
local bankrd, bankrd_pc, bankwr = {}, {}, 0
local p1r, p1w, p2r, p2w, p3r = 0,0,0,0,0
handles[#handles+1] = mem:install_read_tap(0x1fa00000, 0x1fafffff, "bankA", function(off, data, mask)
  local k = string.format("%08x", off & ~0xffff); bankrd[k] = (bankrd[k] or 0) + 1
  local p = string.format("%08x", st["pc"].value); bankrd_pc[p] = (bankrd_pc[p] or 0) + 1; return data end)
handles[#handles+1] = mem:install_read_tap(0x1fb00004, 0x1fbfffff, "bankB", function(off, data, mask)
  if off >= 0x1fb80000 and off < 0x1fb80004 then return data end
  local k = string.format("%08x", off & ~0xffff); bankrd[k] = (bankrd[k] or 0) + 1
  local p = string.format("%08x", st["pc"].value); bankrd_pc[p] = (bankrd_pc[p] or 0) + 1; return data end)
handles[#handles+1] = mem:install_write_tap(0x1f000000, 0x1f000003, "bankreg", function(off, data, mask) bankwr = bankwr + 1; return data end)
-- watchdog-ish writes: count per frame
local wd = {[0x1f008000]=0,[0x1f010000]=0,[0x1f018000]=0}
local wdframe = {[0x1f008000]={}, [0x1f010000]={}, [0x1f018000]={}}
local wdvals = {[0x1f008000]={}, [0x1f010000]={}, [0x1f018000]={}}
for a,_ in pairs(wd) do
  handles[#handles+1] = mem:install_write_tap(a, a+3, "wd"..a, function(off, data, mask)
    wd[a] = wd[a] + 1
    wdframe[a][frame] = (wdframe[a][frame] or 0) + 1
    local k = string.format("%08x/%08x", data, mask); wdvals[a][k] = (wdvals[a][k] or 0) + 1
    return data end)
end
-- IRQ stat/mask traffic per frame
local irqw, irqr = {}, {}
handles[#handles+1] = mem:install_write_tap(0x1f801070, 0x1f801077, "irqw", function(off, data, mask)
  local k = string.format("%08x=%08x/%08x", off, data, mask); irqw[k] = (irqw[k] or 0) + 1; return data end)
handles[#handles+1] = mem:install_read_tap(0x1f801070, 0x1f801077, "irqr", function(off, data, mask)
  local k = string.format("%08x->%08x", off, data); irqr[k] = (irqr[k] or 0) + 1; return data end)
-- DMA ch5/ch2/ch6 chcr writes
local dmaw = {}
handles[#handles+1] = mem:install_write_tap(0x1f801080, 0x1f8010ff, "dmaw", function(off, data, mask)
  local k = string.format("%08x=%08x/%08x", off, data, mask); dmaw[k] = (dmaw[k] or 0) + 1; return data end)
local dmaseq = {}
handles[#handles+1] = mem:install_write_tap(0x1f8010d0, 0x1f8010df, "dma5seq", function(off, data, mask)
  if #dmaseq < 400 then dmaseq[#dmaseq+1] = string.format("f%d pc=%08x %08x=%08x/%08x", frame, st["pc"].value, off, data, mask) end return data end)
local protoseq = {}
for _, r in ipairs{{0x1f700000,0x1f70ffff,"1f70"},{0x1fb00000,0x1fb00003,"1fb0"},{0x1fb80000,0x1fb80003,"1fb8"}} do
  handles[#handles+1] = mem:install_write_tap(r[1], r[2], r[3].."w", function(off, data, mask)
    if #protoseq < 600 then protoseq[#protoseq+1] = string.format("f%d pc=%08x W %08x=%08x/%08x", frame, st["pc"].value, off, data, mask) end return data end)
  handles[#handles+1] = mem:install_read_tap(r[1], r[2], r[3].."r", function(off, data, mask)
    if #protoseq < 600 then protoseq[#protoseq+1] = string.format("f%d pc=%08x R %08x->%08x/%08x", frame, st["pc"].value, off, data, mask) end return data end)
end
emu.register_frame_done(function()
  frame = frame + 1
  for _, h in ipairs(handles) do h:reinstall() end
  if frame == 1 then
    local b = {} for i = 0, 11 do b[#b+1] = string.format("%02x", mem:read_u8(0x10000 + i)) end
    log("frame1: RAM[0x10000..0x1000b] = %s (boot_hack copy of ROM+0x20280, 12 bytes)", table.concat(b, ""))
    log("PRId=%08x SR=%08x", st["PRId"].value, st["SR"].value)
  end
  if frame == 10 then
    local bad = 0
    for off = 0, 0x3ffffc, 4 do
      if mem:read_u32(0x1fc00000 + off) ~= rom:read_u32(off) then bad = bad + 1 end
    end
    log("frame10: ROM window compare 0x1fc00000..0x1fffffff vs region: %d mismatching words (of 1048576)", bad)
    local bad2 = 0
    for off = 0, 0x3ffffc, 0x1004 do
      if mem:read_u32(0x9fc00000 + off) ~= rom:read_u32(off) or mem:read_u32(0xbfc00000 + off) ~= rom:read_u32(off) then bad2 = bad2 + 1 end
    end
    log("frame10: kseg0/kseg1 aliases 9fc/bfc sample mismatches: %d", bad2)
  end
  if frame == 700 then
    log("frame700: ram_cfg=%08x rom_cfg=%08x biu=%08x SR=%08x Cause=%08x", mem:read_u32(0x1f801060), mem:read_u32(0x1f801010), mem:read_u32(0xfffe0130), st["SR"].value, st["Cause"].value)
    -- RAM window / aliases (write to unused high RAM, then read through all aliases)
    local a = 0x3ff000
    local keep = mem:read_u32(a)
    mem:write_u32(a, 0xdeadbeef)
    log("RAM alias: [0x003ff000]=%08x [0x803ff000]=%08x [0xa03ff000]=%08x", mem:read_u32(a), mem:read_u32(0x80000000 + a), mem:read_u32(0xa0000000 + a))
    log("RAM beyond 4MB: [0x007ff000]=%08x [0x807ff000]=%08x [0xa07ff000]=%08x (0 => not a mirror; berr)", mem:read_u32(0x400000 + a), mem:read_u32(0x80400000 + a), mem:read_u32(0xa0400000 + a))
    mem:write_u32(a, keep)
  end
  if frame == 1500 then
    log("== bank window reads by 64KB page (count): ")
    for k, v in pairs(bankrd) do log("  %s %d", k, v) end
    for k, v in pairs(bankrd_pc) do log("  pc %s %d", k, v) end
    log("bank register 0x1f000000 write count = %d", bankwr)
    for a, _ in pairs(wd) do
      local nf, mn, mx = 0, 1e9, 0
      for f, c in pairs(wdframe[a]) do nf = nf + 1; if c < mn then mn = c end; if c > mx then mx = c end end
      local vs = {} for k, v in pairs(wdvals[a]) do vs[#vs+1] = k .. "x" .. v end
      log("write-only reg %08x: total=%d frames_with_writes=%d per-frame min=%s max=%d values=[%s]", a, wd[a], nf, nf > 0 and mn or "-", mx, table.concat(vs, " "))
    end
    log("== IRQ writes"); for k, v in pairs(irqw) do log("  %s x%d", k, v) end
    log("== IRQ reads");  for k, v in pairs(irqr) do log("  %s x%d", k, v) end
    log("== DMA register writes"); for k, v in pairs(dmaw) do log("  %s x%d", k, v) end
    log("== DMA ch5 sequence (first %d)", #dmaseq); for _, s in ipairs(dmaseq) do log("  %s", s) end
    log("== protection/dmaoffset window sequence (first %d)", #protoseq); for _, s in ipairs(protoseq) do log("  %s", s) end
    out:close(); m:exit()
  end
end)
