-- MAME Lua probe: which GPU display modes, DMA modes, MDEC commands, root-counter modes and IRQ masks does the
-- game actually use? Samples MAME save-state items once per frame (so it sees the LAST value written during a
-- frame, not every write; short-lived values are missed => results are a lower bound).
-- Usage: S12_OUT=dir S12_MAXFRAME=6000 mame tektagt ... -autoboot_script hw_usage.lua  (see vram_dump.lua)
local m = manager.machine
local outdir = os.getenv("S12_OUT") or "."
local maxf = tonumber(os.getenv("S12_MAXFRAME") or "6000")

local function item(devtag, name)
  local d = m.devices[devtag]
  local id = d and d.items[name]
  return id and emu.item(id) or nil
end
local function rd(it) return it and it:read(0) or nil end

local gpu_st = item(":gpu", "0/n_gpustatus")
local gpu_dsy = item(":gpu", "0/n_displaystarty")
local scr = m.screens[":screen"]
local dma = {}
for ch = 0, 6 do
  dma[ch] = { chcr = item(":maincpu:dma", ch .. "/dma->n_channelcontrol"),
              bcr = item(":maincpu:dma", ch .. "/dma->n_blockcontrol"),
              base = item(":maincpu:dma", ch .. "/dma->n_base") }
end
local mdec0 = item(":maincpu:mdec", "0/n_0_command")
local mdec1 = item(":maincpu:mdec", "0/n_1_command")
local rc = { mode = item(":maincpu:rcnt", "0/root_counter.n_mode"), target = item(":maincpu:rcnt", "0/root_counter.n_target") }
local irqmask = item(":maincpu:irq", "0/n_irqmask")
local dicr = item(":maincpu:dma", "0/m_dicr")

local seen = {}   -- category -> value-string -> {first_frame, count}
local function note(cat, val, f)
  seen[cat] = seen[cat] or {}
  local e = seen[cat][val]
  if not e then seen[cat][val] = { f, 1 } else e[2] = e[2] + 1 end
end

local n = 0
emu.register_frame_done(function()
  n = n + 1
  local st = rd(gpu_st)
  if st then
    note("display", string.format("screen=%dx%d@%.4f hres=%d h368=%d v480=%d pal=%d rgb24=%d interlace=%d dispoff=%d dispY>=512=%s",
      scr.width, scr.height, scr.refresh, (st >> 17) & 3, (st >> 16) & 1, (st >> 19) & 1, (st >> 20) & 1,
      (st >> 21) & 1, (st >> 22) & 1, (st >> 23) & 1, tostring((rd(gpu_dsy) or 0) >= 512)), n)
    note("gpu_dmadir(gpustat29-30)", tostring((st >> 29) & 3), n)
    note("gpu_texpageY_bit(gpustat4)&bit11->gpustat15", string.format("bit4=%d bit15=%d", (st >> 4) & 1, (st >> 15) & 1), n)
  end
  for ch = 0, 6 do
    local c = dma[ch].chcr and dma[ch].chcr:read(0)
    if c and c ~= 0 then note("dma" .. ch .. " chcr(last)", string.format("0x%08x", c), n) end
  end
  local c0, c1 = rd(mdec0), rd(mdec1)
  if c0 and c0 ~= 0 then note("mdec cmd0", string.format("0x%08x", c0), n) end
  if c1 and c1 ~= 0 then note("mdec cmd1", string.format("0x%08x", c1), n) end
  if rc.mode then
    for i = 0, 2 do
      local mo = rc.mode:read(i)
      if mo and mo ~= 0 then note("rcnt" .. i .. " mode", string.format("0x%04x target=0x%04x", mo, rc.target:read(i)), n) end
    end
  end
  if irqmask then note("irq mask", string.format("0x%03x", irqmask:read(0)), n) end
  if dicr then note("dma dicr", string.format("0x%08x", dicr:read(0)), n) end
  if n >= maxf then
    local f = assert(io.open(outdir .. "/hw_usage.txt", "w"))
    local cats = {}
    for k in pairs(seen) do cats[#cats + 1] = k end
    table.sort(cats)
    for _, k in ipairs(cats) do
      f:write("[" .. k .. "]\n")
      local vals = {}
      for v, e in pairs(seen[k]) do vals[#vals + 1] = { v, e[1], e[2] } end
      table.sort(vals, function(a, b) return a[2] < b[2] end)
      for _, e in ipairs(vals) do f:write(string.format("  %s  first_frame=%d frames_seen=%d\n", e[1], e[2], e[3])) end
    end
    f:close()
    m:exit()
  end
end)
