-- Which shared-RAM words does the main CPU read/write at steady state (attract), with PCs and values
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_mainreads_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local R, W = {}, {}
local function rec(t, off, data, mask)
  local a = off & 0x3ffc
  local e = t[a]; if not e then e = {n=0, pcs={}, last=0}; t[a] = e end
  e.n = e.n + 1; e.last = data; e.mask = mask
  local pc = st["pc"].value
  e.pcs[pc] = (e.pcs[pc] or 0) + 1
end
local on = false
KEEP[#KEEP+1] = mem:install_read_tap(0x1f080000, 0x1f083fff, "r", function(off, data, mask) if on then rec(R, off, data, mask) end return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f080000, 0x1f083fff, "w", function(off, data, mask) if on then rec(W, off, data, mask) end return data end)
local frame = 0
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 700 then on = true end
  if frame == 760 then
    for _, pair in ipairs({{"READ", R}, {"WRITE", W}}) do
      local keys = {} for k in pairs(pair[2]) do keys[#keys+1] = k end table.sort(keys)
      for _, k in ipairs(keys) do
        local e = pair[2][k]
        local pcs = {} for pc, c in pairs(e.pcs) do pcs[#pcs+1] = string.format("%08x x%d", pc, c) end table.sort(pcs)
        log("%s %05x n=%d last=%08x mask=%08x pcs=%s", pair[1], 0x80000 + k, e.n, e.last, e.mask, table.concat(pcs, " "))
      end
    end
    out:close(); m:exit()
  end
end)
