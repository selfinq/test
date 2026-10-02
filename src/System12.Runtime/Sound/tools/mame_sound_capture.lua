-- MAME 0.264 Lua: captures (a) trace of the H8/3002 via the debugger (to $S12_TRACE), (b) every
-- C352 register write by the H8 and (c) every main-CPU write to the shared RAM window, with emu time.
local m = manager.machine
local outdir = os.getenv("S12_OUT") or "."
local sub = m.devices[":sub"]
local main = m.devices[":maincpu"]
local cw = io.open(outdir .. "/c352_writes.txt", "w")
local sw = io.open(outdir .. "/shared_writes.txt", "w")
local ir = io.open(outdir .. "/sub_in.txt", "w")
cw:setvbuf("line"); sw:setvbuf("line"); ir:setvbuf("line")
local subspace = sub.spaces["program"]
local mainspace = main.spaces["program"]
-- NOTE: the tap handle MUST stay referenced (GLOBAL variables, not chunk-locals) or Lua GC silently removes the tap.
tap_c352 = subspace:install_write_tap(0x280000, 0x287fff, "c352w", function(off, data, mask)
  cw:write(string.format("%.9f %x %04x %04x\n", m.time:as_double(), off, data & 0xffff, mask & 0xffff))
  return data
end)
tap_shared = mainspace:install_write_tap(0x1f080000, 0x1f083fff, "shw", function(off, data, mask)
  sw:write(string.format("%.9f %x %08x %08x\n", m.time:as_double(), off, data, mask))
  return data
end)
-- H8 side reads of 0x300000 (io) not logged
local tr = os.getenv("S12_TRACE")
if tr then
  local d = m.debugger
  d:command(os.getenv("S12_TRACECMD") or ("trace " .. tr .. ",:sub,noloop"))
  d:command("go")
end
-- frames.txt: one line per frame_done (= start of vblank): time, frame period, time from there to vblank end.
local fw = io.open(outdir .. "/frames.txt", "w")
fw:setvbuf("line")
local scr = m.screens[":screen"]
local n = 0
emu.register_frame_done(function()
  n = n + 1
  fw:write(string.format("%.9f %.9f %.9f\n", m.time:as_double(), scr.frame_period, scr:time_until_vblank_end():as_double()))
  if n % 600 == 0 then cw:flush(); sw:flush() end
end)
emu.register_stop(function() cw:close(); sw:close(); ir:close() end)
