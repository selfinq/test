-- Retained-handle re-check of the earlier "0 tap hits" note: accesses to 0x1fa00000-0x1fbfffff (bank window / tektagt protection 0x1fb00000, 0x1fb80000).
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_banked_log.txt", "w")
local agg, frame, total = {}, 0, {R=0, W=0}
local function rec(kind, off, data, mask)
  total[kind] = total[kind] + 1
  local k = string.format("%s %08x", kind, off & ~0xffff)
  local e = agg[k]; if not e then e = {n=0, first=frame, pc=st["pc"].value, ex=string.format("%08x/%08x", off, data)} agg[k] = e end
  e.n = e.n + 1
end
KEEP[#KEEP+1] = mem:install_read_tap(0x1fa00000, 0x1fbfffff, "br", function(off, data, mask) rec("R", off, data & mask, mask) return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1fa00000, 0x1fbfffff, "bw", function(off, data, mask) rec("W", off, data & mask, mask) return data end)
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 2000 then
    out:write(string.format("total reads=%d writes=%d (taps retained)\n", total.R, total.W))
    local keys = {} for k in pairs(agg) do keys[#keys+1] = k end table.sort(keys)
    for _, k in ipairs(keys) do local e = agg[k] out:write(string.format("%s (64K block) n=%d first_frame=%d first_pc=%08x example addr/data=%s\n", k, e.n, e.first, e.pc, e.ex)) end
    out:close(); m:exit()
  end
end)
