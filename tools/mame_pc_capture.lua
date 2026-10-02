-- mame_pc_capture.lua : record the SET of maincpu PCs that MAME executes (one-shot breakpoints).
-- Run with:  mame tektagt -debug -debugger none -autoboot_script mame_pc_capture.lua ...
-- (tools/mame_pc_capture.sh wraps this reproducibly: fixed NVRAM seed, fresh cfg dir.)
-- Env:  PCC_RANGES      "80010000-80400000,bfc00000-bfc40000"  candidate virtual PC ranges [lo,hi), word aligned
--       PCC_OUT         output file: sorted 8-hex-digit virtual PCs, one per line
--       PCC_MODE        "disable" (default: action "bpdisable N;go") or "clear" (action "bpclear N;go")
--       PCC_STOP_FRAME  frame after which the result is written and MAME exits (0 = rely on -seconds_to_run)
--       PCC_DUMP_EVERY  also (over)write PCC_OUT every N frames (0 = only at end)
--       PCC_INPUT       optional Lua file; must return function(frame) called each frame (input injection)
-- Method: one breakpoint (no condition) per candidate word; its action disables the breakpoint and resumes,
-- so an address costs one debugger stop on first execution and nothing afterwards.
-- Executed set = candidates whose breakpoint is disabled at the end. Only candidate ranges are observable.
local m = manager.machine
local cpu = m.devices[":maincpu"]
local dbg = cpu.debug
local out_path = os.getenv("PCC_OUT") or "pcset.txt"
local mode = os.getenv("PCC_MODE") or "disable"
local stop_frame = tonumber(os.getenv("PCC_STOP_FRAME") or "0")
local dump_every = tonumber(os.getenv("PCC_DUMP_EVERY") or "0")
local ranges = {}
for lo, hi in string.gmatch(os.getenv("PCC_RANGES") or "80010000-80400000", "(%x+)%-(%x+)") do
  ranges[#ranges + 1] = { tonumber(lo, 16), tonumber(hi, 16) }
end
local verb = (mode == "clear") and "bpclear " or "bpdisable "

-- find the next breakpoint index MAME will hand out
local probe = dbg:bpset(0xffffff00, "", "")
dbg:bpclear(probe)
local nxt = probe + 1
local first = nxt
local addr_of = {}
local count = 0
local tstart = os.clock()
for _, r in ipairs(ranges) do
  for a = r[1], r[2] - 4, 4 do
    local i = dbg:bpset(a, "", verb .. nxt .. ";go")
    if i ~= nxt then error(string.format("bp index mismatch: got %d expected %d", i, nxt)) end
    addr_of[i] = a
    nxt = nxt + 1
    count = count + 1
  end
end
print(string.format("pc_capture: %d breakpoints set in %.1fs (mode=%s)", count, os.clock() - tstart, mode))

local function write_result(final)
  local live = {}   -- index -> true for still-armed breakpoints
  for i, bp in pairs(dbg:bplist()) do
    if bp.enabled then live[i] = true end
  end
  local hit = {}
  for i = first, nxt - 1 do
    if not live[i] then hit[#hit + 1] = addr_of[i] end
  end
  table.sort(hit)
  local f = assert(io.open(out_path .. ".tmp", "w"))
  for _, a in ipairs(hit) do f:write(string.format("%08x\n", a)) end
  f:close()
  os.rename(out_path .. ".tmp", out_path)
  print(string.format("pc_capture: %s %d/%d candidate PCs executed -> %s", final and "FINAL" or "partial", #hit, count, out_path))
end

local input_fn
if os.getenv("PCC_INPUT") then input_fn = dofile(os.getenv("PCC_INPUT")) end
local frame = 0
emu.register_frame_done(function()
  frame = frame + 1
  if input_fn then input_fn(frame) end
  if dump_every > 0 and frame % dump_every == 0 then write_result(false) end
  if stop_frame > 0 and frame >= stop_frame then
    write_result(true)
    m:exit()
  end
end)
emu.add_machine_stop_notifier(function() if stop_frame == 0 then write_result(true) end end)
