-- a03: independently re-implement the MAME ROM-DMA (ch5) semantics in Lua and compare with the RAM MAME actually produced.
-- Predicts source region/offset from the observed register writes (PROT1 / 0x1f70xxxx / PROT3 arm read / EXP1_BASE), then at the
-- next hardware access after the DMA compares RAM[madr ..] with region[offset ..].
local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local rom  = m.memory.regions[":maincpu:rom"]
local bank = m.memory.regions[":bankedroms"]
local out = io.open("dma_verify.txt", "w")
local function log(...) out:write(string.format(...), "\n"); out:flush() end
local handles, frame = {}, 0
local maxframes = tonumber(os.getenv("A03_FRAMES") or "1500")
local tt_off, dma_off, armed, exp_base = 0, 0, false, 0x1f000000
local madr, bcr = 0, 0
local pending = nil
local stats = {n=0, ok=0, bad=0, banked=0, mainrom=0, armed=0, maxwords=0, clamped=0}
local function words_of(p) return p end
local function verify()
  if not pending then return end
  local p = pending; pending = nil
  local reg = p.mainrom and rom or bank
  local rsize = p.mainrom and 0x400000 or 0x3800000
  local words = p.bcr
  local romleft = ((rsize // 2 - p.offset // 2) // 2)       -- same integer math as namcos12_rom_read (C++ truncation toward zero for positives)
  if rsize // 2 - p.offset // 2 < 0 then romleft = -(((p.offset // 2) - rsize // 2) // 2) end
  if words > romleft then words = romleft; stats.clamped = stats.clamped + 1 end
  local ramleft = (0x400000 // 4) - ((p.madr & 0x3fffff) // 4)
  if words > ramleft then words = ramleft; stats.clamped = stats.clamped + 1 end
  stats.n = stats.n + 1
  if p.mainrom then stats.mainrom = stats.mainrom + 1 else stats.banked = stats.banked + 1 end
  if p.armed then stats.armed = stats.armed + 1 end
  if words > stats.maxwords then stats.maxwords = words end
  local bad = 0
  local base = p.madr & 0x3fffff
  for i = 0, words - 1 do
    if mem:read_u32(base + 4*i) ~= reg:read_u32(p.offset + 4*i) then bad = bad + 1 end
  end
  if bad == 0 then stats.ok = stats.ok + 1 else
    stats.bad = stats.bad + 1
    log("MISMATCH f%d pc=%08x madr=%08x bcr=%08x mainrom=%s armed=%s off=%08x words=%d bad_words=%d", p.frame, p.pc, p.madr, p.bcr, tostring(p.mainrom), tostring(p.armed), p.offset, words, bad)
  end
end
local function tap(r, w, lo, hi, name, fn) end
-- offset registers
handles[#handles+1] = mem:install_write_tap(0x1fb00000, 0x1fb00003, "p1w", function(off, data, mask)
  if mask == 0xffffffff then tt_off = data
  elseif mask == 0x0000ffff then tt_off = (tt_off & 0xffff0000) | (data & 0xffff)
  elseif mask == 0xffff0000 then tt_off = (tt_off & 0x0000ffff) | (data & 0xffff0000) end
  return data end)
handles[#handles+1] = mem:install_write_tap(0x1f700000, 0x1f70ffff, "doff", function(off, data, mask)
  -- dmaoffset_w: (offset*2) | (D << 16) where offset*2 = byte address low bits of the 16-bit unit
  if mask == 0x0000ffff then dma_off = (off & 0xfffe) | ((data & 0xffff) << 16)
  elseif mask == 0xffff0000 then dma_off = ((off + 2) & 0xfffe) | (((data >> 16) & 0xffff) << 16)
  elseif mask == 0xffffffff then dma_off = ((off + 2) & 0xfffe) | (((data >> 16) & 0xffff) << 16) end
  return data end)
handles[#handles+1] = mem:install_read_tap(0x1f700000, 0x1f700003, "arm", function(off, data, mask) armed = true; return data end)
handles[#handles+1] = mem:install_write_tap(0x1f801000, 0x1f801003, "expb", function(off, data, mask) exp_base = 0x1f000000 | (data & 0xffffff); return data end)
handles[#handles+1] = mem:install_write_tap(0x1f8010d0, 0x1f8010d3, "madr", function(off, data, mask) madr = data; return data end)
handles[#handles+1] = mem:install_write_tap(0x1f8010d4, 0x1f8010d7, "bcr", function(off, data, mask) bcr = data; return data end)
handles[#handles+1] = mem:install_write_tap(0x1f8010d8, 0x1f8010db, "chcr", function(off, data, mask)
  verify()
  if data == 0x01000000 then
    local offset = armed and tt_off or dma_off
    local mainrom = ((offset >> 31) & 1) == 1 or exp_base == 0x1f300000
    if mainrom then offset = offset & 0xffffff else offset = offset & 0x7fffffff end
    pending = {frame=frame, pc=st["pc"].value, madr=madr, bcr=bcr, offset=offset, mainrom=mainrom, armed=armed}
    armed = false
  end
  return data end)
-- verification points: first access to any of these after the transfer
for _, r in ipairs{{0x1f801070,0x1f801077},{0x1fb00000,0x1fb00003},{0x1f700000,0x1f70ffff},{0x1f8010d8,0x1f8010db}} do
  handles[#handles+1] = mem:install_read_tap(r[1], r[2], "v"..r[1], function(off, data, mask) if pending and off ~= 0x1f8010d8 then verify() end return data end)
end
emu.register_frame_done(function()
  frame = frame + 1
  for _, h in ipairs(handles) do h:reinstall() end
  verify()
  if frame >= maxframes then
    log("DONE frames=%d transfers=%d identical=%d mismatching=%d banked=%d mainrom=%d armed=%d clamped_events=%d max_words=%d", frame, stats.n, stats.ok, stats.bad, stats.banked, stats.mainrom, stats.armed, stats.clamped, stats.maxwords)
    out:close(); m:exit()
  end
end)
