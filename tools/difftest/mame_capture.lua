-- mame_capture.lua : deterministic reference capture for the tektagt differential test harness.
--
-- Run only through tools/difftest/capture_mame.py (it restores the NVRAM seed, sets the
-- environment variables below, runs MAME and converts the raw output into a snapshot JSON).
-- Output (all inside $DT_OUT):
--   checkpoints.jsonl   one JSON object per checkpoint (registers, event counter, ram file name)
--   ram_NNNN.raw        raw RAM dump (DT_RAM_SIZE bytes from DT_RAM_BASE) for checkpoint NNNN
--   events.tsv          tab separated I/O + DMA event log (see docs/difftest/CANDIDATE_SNAPSHOT.md)
--   manifest.json       facts about the run (mame version, debugger mode, truncation, ...)
--   done.txt            written last; absent when the run died early
--
-- Environment (all optional):
--   DT_OUT         output directory (must exist)                              [required]
--   DT_EVERY       periodic frame checkpoint interval            (default 60, 0 = off)
--   DT_MAX_FRAME   last frame (final checkpoint, then exit)           (default 1500)
--   DT_PCS         "addr:nth[,addr:nth...]" instruction-exact checkpoints; needs `-debug -debugger none`
--   DT_EVENTS      1 = record I/O + DMA events                                (default 1)
--   DT_EVENT_MAX   event cap                                                  (default 2000000)
--   DT_RAM_BASE / DT_RAM_SIZE   RAM window dumped                    (default 0x80000000 / 0x400000)
--
-- Notes on what is (not) captured, learned the hard way on MAME 0.264:
--   * memory taps fire for I/O ranges handled by devices, NOT for direct RAM/ROM -> RAM state is
--     captured by whole-RAM dumps at checkpoints, never by write taps.
--   * the tap `offset` is the word aligned ABSOLUTE address, `mask` is the byte-lane mask of the
--     access and `data` is in lane position. Events store addr, mask and (data & mask).
--   * register names for GPR/hi/lo/pc/sp are lowercase, COP0 names are mixed case (SR, Cause, EPC, BadA).

local m   = manager.machine
local cpu = m.devices[":maincpu"]
local st0 = cpu.state
-- st["at"] resolves to a container method, not the register: copy the entries into a plain table
local st = {}
for k, v in pairs(st0) do st[k] = v end
local mem = cpu.spaces["program"]

local OUT      = os.getenv("DT_OUT") or error("DT_OUT not set")
local EVERY    = tonumber(os.getenv("DT_EVERY") or "60")
local MAXF     = tonumber(os.getenv("DT_MAX_FRAME") or "1500")
local EVENTS   = (os.getenv("DT_EVENTS") or "1") == "1"
local EVMAX    = tonumber(os.getenv("DT_EVENT_MAX") or "2000000")
local RAM_BASE = tonumber(os.getenv("DT_RAM_BASE") or "0x80000000")
local RAM_SIZE = tonumber(os.getenv("DT_RAM_SIZE") or "0x400000")
local PCS      = os.getenv("DT_PCS") or ""

local function hex32(v) return string.format("0x%08x", v & 0xffffffff) end

----------------------------------------------------------------------------------------------
-- event log
----------------------------------------------------------------------------------------------
local evf = io.open(OUT .. "/events.tsv", "w")
evf:setvbuf("full")
local ev_count, ev_truncated = 0, false
local frame = 0
local volatile_reads = 0           -- reads of timing dependent registers: counted, not logged

local function ev_line(...)
  if ev_count >= EVMAX then ev_truncated = true; return end
  ev_count = ev_count + 1
  evf:write(string.format(...), "\n")
end

local function pc_now() return st["pc"].value & 0xffffffff end

-- PSX DMA shadow registers (taps see every CPU write, so MADR/BCR written before CHCR are known)
local dma_madr, dma_bcr = {}, {}

local FNV_OFFSET = 0xcbf29ce484222325
local FNV_PRIME  = 0x100000001b3
local RAM_PHYS_MASK = 0x3ffffc

local function dma_hash(ch, chcr, madr, bcr)
  -- returns nwords, fnv1a64 over the words (little endian bytes, LSB first) read from RAM
  local dir_from_ram = (chcr & 1) == 1
  local sync = (chcr >> 9) & 3
  if not dir_from_ram then
    local n = bcr & 0xffff; if n == 0 then n = 0x10000 end
    if sync == 1 then n = (bcr & 0xffff) * (bcr >> 16) end
    return n, 0   -- transfer into RAM: parameters only, no source data to hash
  end
  local h, n = FNV_OFFSET, 0
  local function feed(w)
    for sh = 0, 24, 8 do
      h = (h ~ ((w >> sh) & 0xff)) * FNV_PRIME
    end
    n = n + 1
  end
  local a = madr & RAM_PHYS_MASK
  if sync == 2 then
    local nodes = 0
    while nodes < 0x40000 do
      local hdr = mem:read_u32(a)
      feed(hdr)
      local size = hdr >> 24
      for i = 1, size do feed(mem:read_u32((a + 4 * i) & RAM_PHYS_MASK)) end
      nodes = nodes + 1
      local nxt = hdr & 0xffffff
      if (nxt & 0x800000) ~= 0 then break end
      a = nxt & RAM_PHYS_MASK
    end
  else
    local cnt
    if sync == 1 then cnt = (bcr & 0xffff) * (bcr >> 16) else cnt = bcr & 0xffff; if cnt == 0 then cnt = 0x10000 end end
    if cnt > 0x100000 then cnt = 0x100000 end
    for i = 0, cnt - 1 do feed(mem:read_u32((a + 4 * i) & RAM_PHYS_MASK)) end
  end
  return n, h
end

local function is_volatile_read(a)
  -- registers whose value depends on emulated time: never part of the compared event stream
  if a >= 0x1f801040 and a <= 0x1f80105f then return true end   -- SIO/JOY
  if a == 0x1f801070 then return true end                        -- I_STAT
  if a >= 0x1f801080 and a <= 0x1f8010ff then return true end   -- DMA registers
  if a >= 0x1f801100 and a <= 0x1f80112f then return true end   -- root counters
  if a == 0x1f801810 or a == 0x1f801814 then return true end     -- GPUREAD / GPUSTAT
  return false
end

local function on_write(off, data, mask)
  local a = off & 0xfffffffc
  if a >= 0x1f801080 and a <= 0x1f8010ef then
    local rel = a - 0x1f801080
    local ch, reg = rel // 16, rel % 16
    local v = data & mask
    if reg == 0 then dma_madr[ch] = v elseif reg == 4 then dma_bcr[ch] = v end
    if reg == 8 and (v & 0x01000000) ~= 0 and mask == 0xffffffff then
      -- DMA start: log the register write first, then the transfer summary
      ev_line("W\t%08x\t%08x\t%08x\t%08x\t%d", a, mask, v, pc_now(), frame)
      local madr, bcr = dma_madr[ch] or 0, dma_bcr[ch] or 0
      local n, h = dma_hash(ch, v, madr, bcr)
      ev_line("D\t%d\t%08x\t%08x\t%08x\t%08x\t%016x\t%08x\t%d", ch, v, madr, bcr, n, h, pc_now(), frame)
      return data
    end
  end
  ev_line("W\t%08x\t%08x\t%08x\t%08x\t%d", a, mask, data & mask, pc_now(), frame)
  return data
end

local function on_read(off, data, mask)
  local a = off & 0xfffffffc
  if is_volatile_read(a) then volatile_reads = volatile_reads + 1; return data end
  ev_line("R\t%08x\t%08x\t%08x\t%08x\t%d", a, mask, data & mask, pc_now(), frame)
  return data
end

if EVENTS then
  -- write taps
  mem:install_write_tap(0x1f000000, 0x1f1fffff, "dt_w_board", on_write)      -- bank reg, shared RAM, EEPROM
  mem:install_write_tap(0x1f700000, 0x1f70ffff, "dt_w_dmaoff", on_write)     -- tektagt DMA offset
  mem:install_write_tap(0x1fb00000, 0x1fb80003, "dt_w_prot", on_write)       -- tektagt protection
  mem:install_write_tap(0x1f801000, 0x1f801fff, "dt_w_psx", on_write)        -- PSX I/O (GPU, DMA, IRQ, timers, SPU)
  -- read taps (small ranges only; the banked ROM window 0x1fa00000.. is deliberately not tapped)
  mem:install_read_tap(0x1f000000, 0x1f1fffff, "dt_r_board", on_read)
  mem:install_read_tap(0x1f700000, 0x1f700003, "dt_r_prot3", on_read)
  mem:install_read_tap(0x1fb00000, 0x1fb00003, "dt_r_prot1", on_read)
  mem:install_read_tap(0x1fb80000, 0x1fb80003, "dt_r_prot2", on_read)
  mem:install_read_tap(0x1f801000, 0x1f801fff, "dt_r_psx", on_read)
end

----------------------------------------------------------------------------------------------
-- checkpoint
----------------------------------------------------------------------------------------------
local GPR = { "zero","at","v0","v1","a0","a1","a2","a3","t0","t1","t2","t3","t4","t5","t6","t7",
              "s0","s1","s2","s3","s4","s5","s6","s7","t8","t9","k0","k1","gp","sp","fp","ra" }
local COP2_D = { "vxy0","vz0","vxy1","vz1","vxy2","vz2","rgb","otz","ir0","ir1","ir2","ir3",
                 "sxy0","sxy1","sxy2","sxyp","sz0","sz1","sz2","sz3","rgb0","rgb1","rgb2","res1",
                 "mac0","mac1","mac2","mac3","irgb","orgb","lzcs","lzcr" }
local COP2_C = { "r11r12","r13r21","r22r23","r31r32","r33","trx","try","trz",
                 "l11l12","l13l21","l22l23","l31l32","l33","rbk","gbk","bbk",
                 "lr1lr2","lr31g1","lg2lg3","lb1lb2","lb3","rfc","gfc","bfc",
                 "ofx","ofy","h","dqa","dqb","zsf3","zsf4","flag" }

local seq = 0
local MAX_CHECKPOINTS = 2000      -- hard guard against runaway loops
local cpf = io.open(OUT .. "/checkpoints.jsonl", "w")
local errf = io.open(OUT .. "/lua_errors.txt", "w")
local function guarded(name, fn, ...)
  local ok, err = pcall(fn, ...)
  if not ok then errf:write(name, ": ", tostring(err), "\n"); errf:flush() end
  return ok
end

local function regs_json(names)
  local t = {}
  for i, n in ipairs(names) do t[i] = '"' .. hex32(st[n].value) .. '"' end
  return "[" .. table.concat(t, ",") .. "]"
end

local function checkpoint(id, sync_json)
  seq = seq + 1
  assert(seq <= MAX_CHECKPOINTS, "too many checkpoints")
  local ramname = string.format("ram_%04d.raw", seq)
  local s = mem:read_range(RAM_BASE, RAM_BASE + RAM_SIZE - 1, 8)
  assert(#s == RAM_SIZE, "RAM dump has unexpected length " .. #s)
  local rf = io.open(OUT .. "/" .. ramname, "wb"); rf:write(s); rf:close()
  evf:flush()
  local cop0 = string.format('{"sr":"%s","cause":"%s","epc":"%s","badvaddr":"%s"}',
    hex32(st["SR"].value), hex32(st["Cause"].value), hex32(st["EPC"].value), hex32(st["BadA"].value))
  local line = string.format(
    '{"id":"%s","sync":%s,"frame":%d,"pc":"%s","gpr":%s,"hi":"%s","lo":"%s","cop0":%s,"cop2_d":%s,"cop2_c":%s,' ..
    '"event_count":%d,"io_volatile_reads":%d,"ram_file":"%s"}',
    id, sync_json, frame, hex32(st["pc"].value), regs_json(GPR), hex32(st["hi"].value), hex32(st["lo"].value),
    cop0, regs_json(COP2_D), regs_json(COP2_C), ev_count, volatile_reads, ramname)
  cpf:write(line, "\n"); cpf:flush()
end

----------------------------------------------------------------------------------------------
-- instruction exact checkpoints (debugger breakpoints; require -debug -debugger none)
----------------------------------------------------------------------------------------------
local bps = {}      -- pc -> { wanted = {nth=true}, last = maxnth, hits = 0, idx = bp index }
local debug_ok = false
if PCS ~= "" then
  debug_ok = (cpu.debug ~= nil) and (m.debugger ~= nil)
  if debug_ok then
    for spec in PCS:gmatch("[^,]+") do
      local a, n = spec:match("^(%x+):(%d+)$")
      assert(a, "bad DT_PCS entry " .. spec)
      local pc = tonumber(a, 16) & 0xffffffff
      local b = bps[pc]
      if not b then b = { wanted = {}, last = 0, hits = 0 }; bps[pc] = b end
      b.wanted[tonumber(n)] = true
      if tonumber(n) > b.last then b.last = tonumber(n) end
    end
    for pc, b in pairs(bps) do b.idx = cpu.debug:bpset(pc, "", "") end
  end
end

local function handle_stop_inner()
  local d = m.debugger
  if not d or d.execution_state ~= "stop" then return end
  local pc = pc_now()
  local b = bps[pc]
  if b then
    b.hits = b.hits + 1
    if b.wanted[b.hits] then
      checkpoint(string.format("pc_%08x_%d", pc, b.hits),
        string.format('{"type":"pc_hit","pc":"%s","nth":%d}', hex32(pc), b.hits))
    end
    if b.hits >= b.last and b.idx then cpu.debug:bpclear(b.idx); b.idx = nil end
  end
end
local function handle_stop()
  local d = m.debugger
  if not d or d.execution_state ~= "stop" then return end
  guarded("handle_stop", handle_stop_inner)
  d.execution_state = "run"   -- always resume, also the initial stop that `-debug` imposes
end
if debug_ok then emu.register_periodic(handle_stop) end

----------------------------------------------------------------------------------------------
-- frame checkpoints
----------------------------------------------------------------------------------------------
local first_ram_done = false
local function finish()
  evf:flush(); evf:close(); cpf:close()
  local mf = io.open(OUT .. "/manifest.json", "w")
  mf:write(string.format(
    '{"mame_version":"%s","debugger":%s,"pcs_requested":"%s","frames":%d,"event_count":%d,' ..
    '"event_truncated":%s,"events_enabled":%s,"ram_base":"%s","ram_size":%d,"every":%d}\n',
    emu.app_version(), tostring(debug_ok), PCS, frame, ev_count, tostring(ev_truncated),
    tostring(EVENTS), hex32(RAM_BASE), RAM_SIZE, EVERY))
  mf:close()
  local df = io.open(OUT .. "/done.txt", "w"); df:write("ok\n"); df:close()
  m:exit()
end

emu.register_frame_done(function() guarded("frame_done", function()
  frame = frame + 1
  if not first_ram_done and (pc_now() >> 28) == 8 then
    first_ram_done = true
    checkpoint("first_ram_frame", string.format('{"type":"frame","frame":%d}', frame))
  end
  if EVERY > 0 and frame % EVERY == 0 and frame < MAXF then
    checkpoint(string.format("frame_%05d", frame), string.format('{"type":"frame","frame":%d}', frame))
  end
  if frame >= MAXF then
    checkpoint("final", string.format('{"type":"frame","frame":%d}', frame))
    finish()
  end
end) end)
