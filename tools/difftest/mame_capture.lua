-- mame_capture.lua : deterministic reference capture for the tektagt differential test harness.
--
-- Run only through tools/difftest/capture_mame.py (it restores the NVRAM seed, sets the environment
-- variables below, runs MAME with `-debug -debugger none -debuglog` and converts the raw output into a
-- snapshot JSON).  Output (all inside $DT_OUT):
--   checkpoints.jsonl   one JSON object per checkpoint (registers, ram file name, seq)
--   ram_NNNN.raw        raw RAM dump (DT_RAM_SIZE bytes from DT_RAM_BASE) for checkpoint NNNN
--   debug.log           MAME debug console log (-debuglog, written in the process cwd = $DT_OUT): contains the
--                       event lines  "W aaaaaaaa dddddddd" / "R aaaaaaaa dddddddd" / "D ch chcr madr bcr n hash"
--                       and the checkpoint markers "C <seq>" (events before the marker belong to the checkpoint)
--   manifest.json, done.txt, lua_errors.txt
--
-- Environment:
--   DT_OUT         output directory (must exist)                                  [required]
--   DT_EVERY       periodic frame checkpoint interval                 (default 60, 0 = off)
--   DT_MAX_FRAME   last frame (final checkpoint, then exit)                (default 1500)
--   DT_PCS         "addr:nth[,addr:nth...]" instruction exact checkpoints (debugger breakpoints)
--   DT_EVENTS      1 = record I/O + DMA events (debugger watchpoints)             (default 1)
--   DT_READS       1 = also watch reads of EEPROM/protection/I_MASK (slows MAME ~10x)   (default 0)
--   DT_RAM_BASE / DT_RAM_SIZE   RAM window dumped                    (default 0x80000000 / 0x400000)
--
-- What MAME 0.264 teaches (all verified by experiment, see reports/agent09_difftest.md):
--   * Lua memory taps (install_write_tap/read_tap) only see the boot ROM's I/O accesses; once the game code
--     runs, NO tap fires any more although the game uses DMA and interrupts. Debugger WATCHPOINTS do fire,
--     so events are recorded with `wpset ... printf` actions (inline, no stop) -> debug.log.
--   * wpaddr is the exact byte address of the access, wpdata the value right aligned (width is not available).
--   * `pc` inside the debugger is not reliable for I/O attribution; it is not part of the compared event.
--   * a debugger stop is serviced by emu.register_periodic (debugger.execution_state = "run" resumes).
--   * st["at"] collides with a container method -> registers are copied into a plain table first.
--   * high rate "nopw" writes (0x1f008000, 0x1f010000, 0x1f018000, 0x1f1bff08, ~1M/400 frames) are NOT watched.

local m   = manager.machine
local cpu = m.devices[":maincpu"]
local st = {}
for k, v in pairs(cpu.state) do st[k] = v end
local mem = cpu.spaces["program"]

local OUT      = os.getenv("DT_OUT") or error("DT_OUT not set")
local EVERY    = tonumber(os.getenv("DT_EVERY") or "60")
local MAXF     = tonumber(os.getenv("DT_MAX_FRAME") or "1500")
local EVENTS   = (os.getenv("DT_EVENTS") or "1") == "1"
local RAM_BASE = tonumber(os.getenv("DT_RAM_BASE") or "0x80000000")
local RAM_SIZE = tonumber(os.getenv("DT_RAM_SIZE") or "0x400000")
local PCS      = os.getenv("DT_PCS") or ""
local READS    = (os.getenv("DT_READS") or "0") == "1"

local function hex32(v) return string.format("0x%08x", v & 0xffffffff) end
local function pc_now() return st["pc"].value & 0xffffffff end

local errf = io.open(OUT .. "/lua_errors.txt", "w")
local function guarded(name, fn, ...)
  local ok, err = pcall(fn, ...)
  if not ok then errf:write(name, ": ", tostring(err), "\n"); errf:flush() end
  return ok
end

local dbg = m.debugger
local debug_ok = (cpu.debug ~= nil) and (dbg ~= nil)
local frame = 0

local function console(fmt, ...)
  dbg:command(string.format('printf "' .. fmt .. '\\n"', ...))
end

----------------------------------------------------------------------------------------------
-- DMA command list hashing (ch2 linked lists = GPU command lists)
----------------------------------------------------------------------------------------------
-- BEGIN dma_hash (extracted verbatim by dma_hash_selftest.lua: needs only `mem`)
local FNV_OFFSET = 0xcbf29ce484222325
local FNV_PRIME  = 0x100000001b3
local RAM_PHYS_MASK = 0x3ffffc

-- Returns nwords, fnv1a64 over the 32-bit words (LSB first) of the RAM range covered by the transfer, read AFTER the
-- transfer (the debugger stops after the CHCR store, MAME's DMA already ran): the source list/data of a transfer from RAM,
-- the delivered data of a transfer into RAM. chcr bit 1 = step -4 (ordering table clear, ch6).
local function dma_hash(chcr, madr, bcr)
  local sync = (chcr >> 9) & 3
  local step = ((chcr & 2) ~= 0) and -4 or 4
  local h, n = FNV_OFFSET, 0
  local function feed(w)
    for sh = 0, 24, 8 do h = (h ~ ((w >> sh) & 0xff)) * FNV_PRIME end
    n = n + 1
  end
  local a = madr & RAM_PHYS_MASK
  if sync == 2 then
    local nodes = 0
    while nodes < 0x40000 do
      local hdr = mem:read_u32(a)
      feed(hdr)
      for i = 1, hdr >> 24 do feed(mem:read_u32((a + 4 * i) & RAM_PHYS_MASK)) end
      nodes = nodes + 1
      local nxt = hdr & 0xffffff
      if (nxt & 0x800000) ~= 0 then break end
      a = nxt & RAM_PHYS_MASK
    end
  else
    local cnt
    if sync == 1 then cnt = (bcr & 0xffff) * (bcr >> 16) else cnt = bcr & 0xffff; if cnt == 0 then cnt = 0x10000 end end
    if cnt > 0x100000 then cnt = 0x100000 end
    for i = 0, cnt - 1 do feed(mem:read_u32((a + step * i) & RAM_PHYS_MASK)) end
  end
  return n, h
end
-- END dma_hash

----------------------------------------------------------------------------------------------
-- watchpoints
----------------------------------------------------------------------------------------------
local WACT = 'printf "W %08x %08x\\n",wpaddr,wpdata;g'
local RACT = 'printf "R %08x %08x\\n",wpaddr,wpdata;g'
local DMA_CHCR0 = 0x1f801088      -- CHCR of channel 0; channel n at +0x10*n (MADR = CHCR-8, BCR = CHCR-4)

local function install_watchpoints()
  local function wp(kind, base, len, cond, act) cpu.debug:wpset(mem, kind, base, len, cond or "", act) end
  -- board registers (bank, shared RAM with the sound CPU, EEPROM), tektagt DMA offset and protection
  wp("w", 0x1f000000, 4, nil, WACT)
  wp("w", 0x1f080000, 0x4000, nil, WACT)
  wp("w", 0x1f140000, 0x1000, nil, WACT)
  wp("w", 0x1f700000, 0x10000, nil, WACT)
  wp("w", 0x1fb00000, 4, nil, WACT)
  wp("w", 0x1fb80000, 4, nil, WACT)
  -- reads of the shared RAM (sound CPU handshake, polled ~40k times in 120 frames, time dependent) are NOT watched
  if READS then
    -- OPTIONAL (DT_READS=1): a read watchpoint makes MAME ~10x slower (every read of the address space is checked)
    wp("r", 0x1f140000, 0x1000, nil, RACT)
    wp("r", 0x1f700000, 4, nil, RACT)
    wp("r", 0x1fb00000, 4, nil, RACT)
    wp("r", 0x1fb80000, 4, nil, RACT)
    wp("r", 0x1f801074, 4, nil, RACT)            -- I_MASK (I_STAT, GPUSTAT, timers, DMA status are time dependent)
  end
  -- PSX I/O writes: everything except the seven DMA CHCR words, which are split by the start bit (bit 24)
  local cur = 0x1f801000
  for ch = 0, 6 do
    local c = DMA_CHCR0 + 0x10 * ch
    wp("w", cur, c - cur, nil, WACT)
    cur = c + 4
    -- non start writes: plain event; start writes: event + STOP (the periodic handler adds the D event)
    wp("w", c, 4, "(wpdata & 0x01000000) == 0", WACT)
    wp("w", c, 4, "(wpdata & 0x01000000) != 0", 'printf "W %08x %08x\\n",wpaddr,wpdata')
  end
  wp("w", cur, 0x1f802000 - cur, nil, WACT)
end

----------------------------------------------------------------------------------------------
-- checkpoint
----------------------------------------------------------------------------------------------
----------------------------------------------------------------------------------------------
-- register tables and input pinning
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


-- DETERMINISM FIX. MAME 0.264 never resets the GTE (COP2) register file: it lives in uninitialised heap memory of the
-- psxcpu device. Depending on heap history (even on the length of command line arguments) up to six GTE registers
-- (data 0,1,24,25, control 8,9) hold leftovers of freed heap chunks, e.g. a host pointer 0x0000559e6ecceff0 split over
-- two registers, which changes with ASLR on every run (observed: two identical runs, different snapshot hashes, only
-- these registers differing). The reference therefore pins the state before the first instruction executes: every GTE
-- register, hi, lo, EPC and BadA are set to 0. The pre-pin non-zero values are recorded in manifest.json.
local PINNED = {}
if os.getenv("DT_TEST_DIRTY") == "1" then   -- test hook: simulate the uninitialised-memory garbage
  st["vxy0"].value = 0xdeadbeef; st["mac0"].value = 0x1234; st["l11l12"].value = 0x55; st["lo"].value = 7
end
local NO_PIN = os.getenv("DT_NO_PIN") == "1"     -- demonstration hook: reproduces the MAME non-determinism
local function pin(name)
  local e = st[name]
  local v = e.value
  if v ~= 0 then PINNED[#PINNED + 1] = string.format("%s=0x%x", name, v) end
  if not NO_PIN then e.value = 0 end
end
for _, n in ipairs(COP2_D) do pin(n) end
for _, n in ipairs(COP2_C) do pin(n) end
for _, n in ipairs({ "hi", "lo", "EPC", "BadA" }) do pin(n) end

local seq = 0
local MAX_CHECKPOINTS = 2000      -- hard guard against runaway loops
local cpf = io.open(OUT .. "/checkpoints.jsonl", "w")

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
  if debug_ok then console("C %d", seq) end        -- event stream position of this checkpoint
  local cop0 = string.format('{"sr":"%s","cause":"%s","epc":"%s","badvaddr":"%s"}',
    hex32(st["SR"].value), hex32(st["Cause"].value), hex32(st["EPC"].value), hex32(st["BadA"].value))
  cpf:write(string.format(
    '{"seq":%d,"id":"%s","sync":%s,"frame":%d,"pc":"%s","gpr":%s,"hi":"%s","lo":"%s","cop0":%s,"cop2_d":%s,"cop2_c":%s,"ram_file":"%s"}\n',
    seq, id, sync_json, frame, hex32(st["pc"].value), regs_json(GPR), hex32(st["hi"].value), hex32(st["lo"].value),
    cop0, regs_json(COP2_D), regs_json(COP2_C), ramname))
  cpf:flush()
end

----------------------------------------------------------------------------------------------
-- debugger stops: instruction exact checkpoints and DMA starts
----------------------------------------------------------------------------------------------
local bps = {}      -- pc -> { wanted = {nth=true}, last = maxnth, hits = 0, idx = bp index }
local dma_events, bp_checkpoints = 0, 0
if debug_ok and PCS ~= "" then
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
if debug_ok and EVENTS then install_watchpoints() end

-- The debugger stops AFTER the instruction (and thus the DMA, which MAME runs inside the CHCR write handler) completed,
-- so MADR/BCR read from the registers are already post-transfer (e.g. MADR = 0x00ffffff after a linked list). The guest
-- visible inputs are therefore recovered from the most recent "W <addr> <value>" lines of the debug console that the
-- watchpoint actions printed (shadow registers).
local shadow = {}
local function shadow_reg(cl, n, addr)
  local key = string.format("W %08x ", addr)
  for i = n, math.max(1, n - 3000), -1 do
    local line = tostring(cl[i])
    if line:sub(1, 11) == key then
      local v = tonumber(line:sub(12, 19), 16)
      shadow[addr] = v
      return v
    end
  end
  return shadow[addr]          -- not in the window: last value seen at an earlier stop (nil -> caller falls back)
end

local function handle_stop_inner()
  local cl = dbg.consolelog
  local n = #cl
  -- DMA start watchpoint?
  for i = n, math.max(1, n - 3), -1 do
    local val, addr = tostring(cl[i]):match("Stopped at watchpoint %x+ writing (%x+) to (%x+)")
    if val then
      local chcr, a = tonumber(val, 16), tonumber(addr, 16)
      local ch = (a - DMA_CHCR0) // 16
      local madr = shadow_reg(cl, n, a - 8) or mem:read_u32(a - 8)
      local bcr = shadow_reg(cl, n, a - 4) or mem:read_u32(a - 4)
      local words, h = dma_hash(chcr, madr, bcr)
      console("D %d %08x %08x %08x %d %016x", ch, chcr, madr, bcr, words, h)
      dma_events = dma_events + 1
      return
    end
  end
  local pc = pc_now()
  local b = bps[pc]
  if b then
    b.hits = b.hits + 1
    if b.wanted[b.hits] then
      bp_checkpoints = bp_checkpoints + 1
      checkpoint(string.format("pc_%08x_%d", pc, b.hits),
        string.format('{"type":"pc_hit","pc":"%s","nth":%d}', hex32(pc), b.hits))
    end
    if b.hits >= b.last and b.idx then cpu.debug:bpclear(b.idx); b.idx = nil end
  end
end

local function handle_stop()
  if dbg.execution_state ~= "stop" then return end
  guarded("handle_stop", handle_stop_inner)
  dbg.execution_state = "run"   -- always resume, also the initial stop that `-debug` imposes
end
if debug_ok then emu.register_periodic(handle_stop) end

----------------------------------------------------------------------------------------------
-- frame checkpoints
----------------------------------------------------------------------------------------------
local first_ram_done = false
local finished = false
local function finish()
  finished = true
  cpf:close()
  local mf = io.open(OUT .. "/manifest.json", "w")
  mf:write(string.format(
    '{"mame_version":"%s","debugger":%s,"pcs_requested":"%s","frames":%d,"events_enabled":%s,' ..
    '"ram_base":"%s","ram_size":%d,"every":%d,"dma_events":%d,"pc_checkpoints":%d,"pinned_nonzero_before_pin":"%s"}\n',
    emu.app_version(), tostring(debug_ok), PCS, frame, tostring(debug_ok and EVENTS),
    hex32(RAM_BASE), RAM_SIZE, EVERY, dma_events, bp_checkpoints, table.concat(PINNED, " ")))
  mf:close()
  local df = io.open(OUT .. "/done.txt", "w"); df:write("ok\n"); df:close()
  m:exit()
end

emu.register_frame_done(function() guarded("frame_done", function()
  if finished then return end        -- m:exit() takes effect a little later: more frame callbacks may arrive
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
