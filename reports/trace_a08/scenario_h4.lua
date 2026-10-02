-- H4 evidence scenario: coin + 1P start -> character select. Logs: frame, main PC, snapshots every 10 frames after start,
-- distinct main reads of shared RAM (addr, pc), then keeps going: cursor right + button 1 to select characters.
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local scr = m.screens[":screen"]
local SP = (os.getenv("A08_OUT") or "/tmp/a08_out") .. "/"
local out = io.open("/home/user/test/reports/trace_a08/scenario_h4_log.txt", "w")
local frame = 0
local function log(...) out:write(string.format("f%d ", frame), string.format(...), "\n") end
local readers = {}
KEEP[#KEEP+1] = mem:install_read_tap(0x1f080000, 0x1f083fff, "mr", function(off, data, mask)
  local k = string.format("%05x@%08x", 0x80000 + (off & 0x3ffc), st["pc"].value)
  if not readers[k] then readers[k] = frame end
  return data end)
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local function press(fld, n) return function() fld:set_value(1) PEND[frame + n] = function() fld:set_value(0) end end end
PEND = {}
local ev = {
  [2600] = press(f1["Coin 1"], 6),
  [2700] = press(f0["1 Player Start"], 6),
}
-- character select: after 2840, press P1 Right then Button1 ... (tag: two picks)
local seqf = 2900
for i = 1, 12 do
  ev[seqf] = press(f0["P1 Right"], 4); seqf = seqf + 20
  ev[seqf] = press(f0["P1 Button 1"], 4); seqf = seqf + 120
end
local snaps = {}
for fr = 2700, 2860, 10 do snaps[fr] = true end
for fr = 2900, 5600, 100 do snaps[fr] = true end
emu.register_frame_done(function()
  frame = frame + 1
  if PEND[frame] then PEND[frame]() end
  if ev[frame] then ev[frame]() log("INPUT event") end
  if snaps[frame] then
    log("pc=%08x", st["pc"].value)
    pcall(function() scr:snapshot(SP..string.format("s%05d.png", frame)) end)
  end
  if frame == 5700 then
    local keys = {} for k,v in pairs(readers) do keys[#keys+1] = k end table.sort(keys)
    for _, k in ipairs(keys) do log("FIRSTREAD %s at frame %d", k, readers[k]) end
    out:close(); m:exit()
  end
end)
