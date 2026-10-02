-- Reads and writes of the nopw windows (0x1f008000, 0x1f010000, 0x1f018000, 0x1f1bff08) and every other main-bus access in
-- 0x1f000000-0x1f7fffff outside {EEPROM, shared RAM, 0x1f80xxxx PSX regs}: distinct (addr, width mask, value) with counts.
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_nopw_log.txt", "w")
local agg = {}
local frame = 0
local function rec(kind, off, data, mask)
  local pc = st["pc"].value
  local k = string.format("%s %08x mask=%08x data=%08x", kind, off, mask, data)
  local e = agg[k]; if not e then e = {n=0, first=frame, pc=pc}; agg[k] = e end
  e.n = e.n + 1
end
local function tap(a, b)
  KEEP[#KEEP+1] = mem:install_read_tap(a, b, "r"..a, function(off, data, mask) rec("R", off, 0, mask) return data end)
  KEEP[#KEEP+1] = mem:install_write_tap(a, b, "w"..a, function(off, data, mask) rec("W", off, data & mask, mask) return data end)
end
tap(0x1f000000, 0x1f07ffff)       -- bank register, 0x1f008000, 0x1f010000, 0x1f018000
tap(0x1f084000, 0x1f13ffff)
tap(0x1f141000, 0x1f7fffff)       -- includes 0x1f1bff08, 0x1f700000
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 2800 then
    local keys = {} for k in pairs(agg) do keys[#keys+1] = k end table.sort(keys)
    for _, k in ipairs(keys) do local e = agg[k] out:write(string.format("%s n=%d first_frame=%d first_pc=%08x\n", k, e.n, e.first, e.pc)) end
    out:close(); m:exit()
  end
end)
