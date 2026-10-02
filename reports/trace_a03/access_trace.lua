-- a03: aggregate every access to non-RAM/non-ROM-code regions of the main CPU, and log tektagt DMA/protection protocol events.
-- usage: -autoboot_script access_trace.lua ; env A03_FRAMES (default 1500) A03_OUT (default access_trace)
local m = manager.machine
local cpu = m.devices[":maincpu"]
local sub = m.devices[":sub"]
local st = cpu.state
local mem = cpu.spaces["program"]
local smem = sub.spaces["program"]
local maxframes = tonumber(os.getenv("A03_FRAMES") or "1500")
local outname = os.getenv("A03_OUT") or "access_trace"
local frame = 0
local handles = {}
-- NOTE (verified, see tapdbg.lua): MAME 0.264 drops older taps in the PSX hw range once the game starts (~frame 360);
-- handle:reinstall() each frame restores them.
local function TR(sp, lo, hi, name, fn) handles[#handles+1] = sp:install_read_tap(lo, hi, name, fn) end
local function TW(sp, lo, hi, name, fn) handles[#handles+1] = sp:install_write_tap(lo, hi, name, fn) end
local agg = {}      -- key -> record
local events = {}   -- chronological protocol events
local function pc() return st["pc"].value end
local function rec(kind, addr, mask, data, extra)
  local key = string.format("%s %08x", kind, addr)
  local r = agg[key]
  if not r then r = {n=0, firstframe=frame, firstpc=pc(), vals={}, nvals=0, masks={}, lastframe=frame}; agg[key]=r end
  r.n = r.n + 1; r.lastframe = frame
  local mk = string.format("%08x", mask)
  r.masks[mk] = (r.masks[mk] or 0) + 1
  if data ~= nil then
    local k = string.format("%08x", data)
    if r.vals[k] == nil and r.nvals < 12 then r.vals[k] = r.n; r.nvals = r.nvals + 1 end
    r.last = data
  end
  r.pcs = r.pcs or {}
  local p = string.format("%08x", pc())
  if r.pcs[p] == nil then
    local c=0 for _ in pairs(r.pcs) do c=c+1 end
    if c < 6 then r.pcs[p]=true end
  end
end
local function ev(s) if #events < 6000 then events[#events+1] = string.format("f%04d pc=%08x %s", frame, pc(), s) end end

local ALIASES = {0x00000000}  -- 0.264 maps hw regs only at physical addresses (see map_dump.txt); aliased taps unnecessary
local function tapw(lo, hi, name, fn) for ai,a in ipairs(ALIASES) do TW(mem, lo|a, hi|a, name..ai, fn) end end
local function tapr(lo, hi, name, fn) for ai,a in ipairs(ALIASES) do TR(mem, lo|a, hi|a, name..ai, fn) end end
local function tapreg(lo, hi, label, quiet)
  for ai, a in ipairs(ALIASES) do
    TR(mem, lo|a, hi|a, label.."r"..ai, function(off, data, mask)
      rec("R", off & ~3, mask, data)
      return data
    end)
    TW(mem, lo|a, hi|a, label.."w"..ai, function(off, data, mask)
      rec("W", off & ~3, mask, data)
      return data
    end)
  end
end
-- NOTE: in MAME 0.264 lua taps, 'off' is the byte address of the access (word aligned for 32-bit bus)
tapreg(0x1f000000, 0x1f7fffff, "ioA")
tapreg(0x1f800000, 0x1f802fff, "hw")
-- protection windows inside mainbank
tapreg(0x1fb00000, 0x1fb00003, "p1")
tapreg(0x1fb80000, 0x1fb80003, "p2")
tapreg(0x1ff00000, 0x1ff00003, "p3mirror")
-- sequence log for the DMA/protection protocol
tapw(0x1f8010d0, 0x1f8010df, "dma5", function(off, data, mask)
  ev(string.format("DMA5 W %08x = %08x /%08x", off, data, mask)); return data end)
tapw(0x1f8010f0, 0x1f8010f7, "dpcr", function(off, data, mask)
  ev(string.format("DPCR/DICR W %08x = %08x /%08x", off, data, mask)); return data end)
tapw(0x1f801000, 0x1f801013, "expcfg", function(off, data, mask)
  ev(string.format("EXP/ROMCFG W %08x = %08x /%08x", off, data, mask)); return data end)
tapw(0x1f801060, 0x1f801063, "ramcfg", function(off, data, mask)
  ev(string.format("RAMCFG W %08x = %08x /%08x", off, data, mask)); return data end)
tapw(0x1f801070, 0x1f801077, "irqw", function(off, data, mask)
  ev(string.format("IRQ W %08x = %08x /%08x", off, data, mask)); return data end)
tapw(0xfffe0130, 0xfffe0133, "biu", function(off, data, mask)
  ev(string.format("BIU W %08x = %08x /%08x", off, data, mask)); return data end)
for _, r in ipairs{{0x1f700000,0x1f70ffff,"dmaoff"},{0x1fb00000,0x1fb00003,"prot1"},{0x1fb80000,0x1fb80003,"prot2"},{0x1f000000,0x1f000003,"bank"}} do
  tapw(r[1], r[2], r[3].."w", function(off, data, mask)
    ev(string.format("%s W %08x = %08x /%08x", r[3], off, data, mask)); return data end)
  tapr(r[1], r[2], r[3].."r", function(off, data, mask)
    ev(string.format("%s R %08x -> %08x /%08x", r[3], off, data, mask)); return data end)
end
-- GP0/GP1 command histograms
local gp0, gp1 = {}, {}
local gp1n = 0
tapw(0x1f801810, 0x1f801813, "gp0", function(off, data, mask)
  local c = (data >> 24) & 0xff; gp0[c] = (gp0[c] or 0) + 1; return data end)
tapw(0x1f801814, 0x1f801817, "gp1", function(off, data, mask)
  local c = (data >> 24) & 0xff; gp1[c] = (gp1[c] or 0) + 1
  gp1n = (gp1n or 0) + 1
  if gp1n <= 60 and (c ~= 0x05) then ev(string.format("GP1 W %08x", data)) end
  return data end)
-- ROM-window data reads: page histogram (4 KB)
local romhist = {}
tapr(0x1fc00000, 0x1fffffff, "romrd", function(off, data, mask)
  local pg = ((off & 0x1fffffff) - 0x1fc00000) >> 12
  romhist[pg] = (romhist[pg] or 0) + 1
  return data
end)
-- sub CPU
local sagg = {}
local function srec(kind, addr, data)
  local key = string.format("%s %06x", kind, addr)
  local r = sagg[key]
  if not r then r = {n=0, vals={}, nv=0}; sagg[key] = r end
  r.n = r.n + 1
  if data ~= nil then
    local k = string.format("%x", data)
    if r.vals[k] == nil and r.nv < 8 then r.vals[k]=true; r.nv=r.nv+1 end
  end
end
TR(smem, 0x300000, 0x300033, "sio_r", function(off, data, mask) srec("R", off, data); return data end)
TW(smem, 0x300000, 0x300033, "sio_w", function(off, data, mask) srec("W", off, data); return data end)
local shr, shw = {}, {}
TR(smem, 0x080000, 0x08ffff, "sh_r", function(off, data, mask) local k=(off-0x80000)>>8; shr[k]=(shr[k] or 0)+1; return data end)
TW(smem, 0x080000, 0x08ffff, "sh_w", function(off, data, mask) local k=(off-0x80000)>>8; shw[k]=(shw[k] or 0)+1; return data end)
local c352r, c352w = 0, 0
TR(smem, 0x280000, 0x287fff, "c352_r", function(off, data, mask) c352r=c352r+1; return data end)
TW(smem, 0x280000, 0x287fff, "c352_w", function(off, data, mask) c352w=c352w+1; return data end)

local snaps = {}
local function snap()
  local s = {}
  local function r(a) return string.format("%08x", mem:read_u32(a)) end
  s.frame = frame; s.pc = string.format("%08x", pc())
  s.sr = string.format("%08x", st["SR"].value); s.cause = string.format("%08x", st["Cause"].value)
  s.exp_base=r(0x1f801000); s.exp_cfg=r(0x1f801008); s.rom_cfg=r(0x1f801010); s.com_delay=r(0x1f801020); s.ram_cfg=r(0x1f801060)
  s.i_stat=r(0x1f801070); s.i_mask=r(0x1f801074); s.dpcr=r(0x1f8010f0); s.dicr=r(0x1f8010f4)
  s.biu=r(0xfffe0130); s.gpustat=r(0x1f801814)
  for c=0,2 do s["rcnt"..c] = string.format("cnt=%08x mode=%08x tgt=%08x", mem:read_u32(0x1f801100+c*16), mem:read_u32(0x1f801104+c*16), mem:read_u32(0x1f801108+c*16)) end
  for c=0,6 do s["dma"..c] = string.format("madr=%08x bcr=%08x chcr=%08x", mem:read_u32(0x1f801080+c*16), mem:read_u32(0x1f801084+c*16), mem:read_u32(0x1f801088+c*16)) end
  local scr = m.screens[":screen"]
  s.screen = string.format("%dx%d refresh=%.6f", scr.width, scr.height, scr.refresh)
  snaps[#snaps+1] = s
end
local snapat = {[2]=true,[10]=true,[60]=true,[130]=true,[200]=true,[300]=true,[370]=true,[400]=true,[600]=true,[1000]=true,[1500]=true,[2500]=true}

emu.register_frame_done(function()
  frame = frame + 1
  for _, h in ipairs(handles) do h:reinstall() end
  if snapat[frame] then snap() end
  if frame >= maxframes then
    local f = io.open(outname..".txt","w")
    local function w(...) f:write(string.format(...), "\n") end
    w("# frames=%d", frame)
    w("## snapshots")
    for _,s in ipairs(snaps) do
      local keys = {} for k,_ in pairs(s) do keys[#keys+1]=k end table.sort(keys)
      for _,k in ipairs(keys) do w("snap f%d %s = %s", s.frame, k, tostring(s[k])) end
    end
    w("## aggregate main-cpu accesses (kind addr count firstframe lastframe firstpc masks pcs last vals)")
    local keys = {} for k,_ in pairs(agg) do keys[#keys+1]=k end table.sort(keys)
    for _,k in ipairs(keys) do
      local r = agg[k]
      local vs = {} for v,_ in pairs(r.vals) do vs[#vs+1]=v end table.sort(vs)
      local ms = {} for v,c in pairs(r.masks) do ms[#ms+1]=v..":"..c end table.sort(ms)
      local ps = {} for v,_ in pairs(r.pcs) do ps[#ps+1]=v end table.sort(ps)
      w("%s n=%d f=%d..%d firstpc=%08x masks=[%s] pcs=[%s] last=%s vals=[%s]", k, r.n, r.firstframe, r.lastframe, r.firstpc, table.concat(ms,","), table.concat(ps,","), r.last and string.format("%08x", r.last) or "-", table.concat(vs," "))
    end
    w("## GP0 first-byte histogram")
    local ks = {} for k,_ in pairs(gp0) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do w("gp0 %02x %d", k, gp0[k]) end
    w("## GP1 first-byte histogram")
    ks = {} for k,_ in pairs(gp1) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do w("gp1 %02x %d", k, gp1[k]) end
    w("## ROM window data-read 4KB page histogram (offset from 0x1fc00000)")
    ks = {} for k,_ in pairs(romhist) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do w("rompage %06x %d", k<<12, romhist[k]) end
    w("## sub CPU 0x300000.. accesses")
    ks = {} for k,_ in pairs(sagg) do ks[#ks+1]=k end table.sort(ks)
    for _,k in ipairs(ks) do local r=sagg[k]; local vs={} for v,_ in pairs(r.vals) do vs[#vs+1]=v end table.sort(vs); w("sub %s n=%d vals=[%s]", k, r.n, table.concat(vs," ")) end
    w("## sub shared RAM 256B-block access counts (offset from 0x80000)")
    ks = {} for k,_ in pairs(shr) do ks[#ks+1]=k end for k,_ in pairs(shw) do if shr[k]==nil then ks[#ks+1]=k end end table.sort(ks)
    for _,k in ipairs(ks) do w("shared blk %04x r=%d w=%d", k<<8, shr[k] or 0, shw[k] or 0) end
    w("sub c352 reads=%d writes=%d", c352r, c352w)
    w("## chronological protocol events (first %d)", #events)
    for _,e in ipairs(events) do w("%s", e) end
    f:close()
    m:exit()
  end
end)
