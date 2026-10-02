-- Event trace for banked ROM / DMA / protection validation (agent04).
-- Taps are kept in a global table: MAME removes a tap when its handle is garbage collected
-- (this is why earlier single-tap scripts reported 0 hits after the first GC).
local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local N = tonumber(os.getenv("NFRAMES") or "6000")
local OUT = os.getenv("EVOUT") or "ev_log.txt"
local out = io.open(OUT, "w")
local frame, seq = 0, 0
local pending = nil
local BLK, MAXBLK = 256, 64
local madr, bcr, dpcr = 0, 0, 0
local ndma, dumps = 0, 0
local DUMPSEQ = {}
for v in string.gmatch(os.getenv("DUMPSEQ") or "", "%d+") do DUMPSEQ[tonumber(v)] = true end
local DUMPDIR = os.getenv("DUMPDIR") or "."

local function hashblock(a, nwords)
  local h = 0
  for i = 0, nwords - 1 do
    h = (h * 31 + mem:read_u32(a + i * 4)) & 0xffffffff
  end
  return h
end

local function check_pending()
  if not pending then return end
  local p = pending
  pending = nil
  local parts = {}
  local total = p.size
  local nblk = (total + BLK - 1) // BLK
  if nblk > MAXBLK then nblk = MAXBLK end
  for b = 0, nblk - 1 do
    local nw = BLK
    if (b + 1) * BLK > total then nw = total - b * BLK end
    parts[#parts + 1] = string.format("%08x", hashblock(p.addr + b * BLK * 4, nw))
  end
  out:write(string.format("DMAPOST seq=%d addr=%08x size=%d nblk=%d h=%s\n", p.seq, p.addr, p.size, nblk, table.concat(parts, ",")))
  if (dumps < 12 or DUMPSEQ[p.seq]) and total <= 0x20000 then
    dumps = dumps + 1
    local f = io.open(string.format("%s/dma_dump_%02d.bin", DUMPDIR, dumps), "wb")
    for i = 0, total - 1 do f:write(string.pack("<I4", mem:read_u32(p.addr + i * 4))) end
    f:close()
    out:write(string.format("DMADUMP seq=%d file=dma_dump_%02d.bin\n", p.seq, dumps))
  end
end

local function ev(kind)
  return function(off, data, mask)
    check_pending()
    seq = seq + 1
    out:write(string.format("%d f%d %s %08x %08x %08x pc=%08x\n", seq, frame, kind, off, data, mask, st["pc"].value))
    if kind == "W" then
      if off == 0x1f8010d0 then madr = data
      elseif off == 0x1f8010d4 then bcr = data
      elseif off == 0x1f8010f0 then dpcr = data
      elseif off == 0x1f8010d8 then
        local chcr = data
        if (chcr & 0x1000000) ~= 0 and (dpcr & (1 << 23)) ~= 0 then
          local size = bcr
          if (chcr & 0x200) ~= 0 then
            local ba = bcr >> 16
            if ba == 0 then ba = 0x10000 end
            size = (bcr & 0xffff) * ba
          end
          local addr = madr & 0x3fffff
          local maxw = (0x400000 - addr) // 4
          if size > maxw then size = maxw end
          pending = {seq = seq, addr = addr, size = size}
          out:write(string.format("DMASTART seq=%d madr=%08x bcr=%08x chcr=%08x dpcr=%08x\n", seq, madr, bcr, chcr, dpcr))
        end
      end
    end
    return data
  end
end

KEEP = {}
local function tap(lo, hi)
  KEEP[#KEEP + 1] = mem:install_write_tap(lo, hi, "w" .. lo, ev("W"))
  KEEP[#KEEP + 1] = mem:install_read_tap(lo, hi, "r" .. lo, ev("R"))
end
tap(0x1f000000, 0x1f000003)
tap(0x1f700000, 0x1f70ffff)
tap(0x1fa00000, 0x1fbfffff)
-- DMA channel 5 regs + DPCR (write taps only; keep read noise out)
KEEP[#KEEP + 1] = mem:install_write_tap(0x1f8010d0, 0x1f8010db, "dma5", ev("W"))
KEEP[#KEEP + 1] = mem:install_write_tap(0x1f8010f0, 0x1f8010f3, "dpcr", ev("W"))

emu.register_frame_done(function()
  frame = frame + 1
  check_pending()
  if frame >= N then
    local okb, ent = pcall(function() return m.memory.banks[":mainbank"].entry end)
    out:write("MAINBANK entry=" .. tostring(ent) .. "\n")
    out:write("END frame " .. frame .. "\n"); out:close(); m:exit()
  end
end)
