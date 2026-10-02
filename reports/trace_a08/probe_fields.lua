-- Press each input field in turn (attract mode) and diff H8-side shared RAM (main sees first 16 KB at 0x1f080000)
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_fields16k_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local lastw = {}      -- addr -> h8 pc of last write
local mainreads = {}  -- addr -> {pc=true}
KEEP[#KEEP+1] = smem:install_write_tap(0x080000, 0x08ffff, "ssw", function(off, data, mask)
  lastw[(off & 0xffff) & ~1] = sst["PC"].value; return data end)
KEEP[#KEEP+1] = mem:install_read_tap(0x1f080000, 0x1f083fff, "msr", function(off, data, mask)
  local a = (off & 0x3fff) & ~1
  local t = mainreads[a]; if not t then t = {}; mainreads[a] = t end
  t[cpu.state["pc"].value] = true; return data end)
local function snap()
  local t = {}
  for a = 0, 0x3fff, 2 do t[a] = smem:read_u16(0x080000 + a) end
  return t
end
local function diff(a, b, label)
  local n = 0
  for ad = 0, 0x3fff, 2 do
    if a[ad] ~= b[ad] then
      n = n + 1
      if n <= 60 then
        local pcs = {}
        local mr = mainreads[ad & 0x3fff]
        if ad < 0x4000 and mr then for pc,_ in pairs(mr) do pcs[#pcs+1] = string.format("%08x", pc) end table.sort(pcs) end
        log("  %s sh[%05x] %04x -> %04x  h8writer=%s mainreaders=%s", label, 0x80000 + ad, a[ad], b[ad], lastw[ad] and string.format("%06x", lastw[ad]) or "?", table.concat(pcs, ","))
      end
    end
  end
  log("  %s total changed words: %d", label, n)
end
local ports = m.ioport.ports
local plan = {}
local order = {
  {":IN1","Coin 1"},{":IN1","Coin 2"},{":IN1","Service 1"},{":IN1","Service Mode"},
  {":IN0","1 Player Start"},{":IN0","2 Players Start"},
  {":IN0","P1 Up"},{":IN0","P1 Down"},{":IN0","P1 Left"},{":IN0","P1 Right"},
  {":IN0","P1 Button 1"},{":IN0","P1 Button 2"},{":IN0","P1 Button 3"},
  {":IN1","P1 Button 4"},{":IN1","P1 Button 5"},{":IN1","P1 Button 6"},
  {":IN0","P2 Up"},{":IN0","P2 Down"},{":IN0","P2 Left"},{":IN0","P2 Right"},
  {":IN0","P2 Button 1"},{":IN0","P2 Button 2"},{":IN0","P2 Button 3"},
  {":IN1","P2 Button 4"},{":IN1","P2 Button 5"},{":IN1","P2 Button 6"},
}
local frame, idx, phase, base, held = 0, 0, "idle", nil, nil
local STARTF = 700
emu.register_frame_done(function()
  frame = frame + 1
  if frame < STARTF then return end
  local k = frame - STARTF
  -- each field: 20-frame slot; press at slot frame 5, sample at 14, release at 16
  local slot = k // 20 + 1
  local sf = k % 20
  local f = order[slot]
  if not f then log("done"); out:close(); m:exit(); return end
  local fld = ports[f[1]].fields[f[2]]
  if sf == 4 then base = snap(); log("== %s %s : IN0/IN1 raw via ioport before press: %04x %04x", f[1], f[2], ports[":IN0"]:read(), ports[":IN1"]:read()) end
  if sf == 5 then fld:set_value(1) end
  if sf == 14 then log("  during press: IN0=%04x IN1=%04x", ports[":IN0"]:read(), ports[":IN1"]:read()); diff(base, snap(), f[2]) end
  if sf == 16 then fld:set_value(0) end
end)
