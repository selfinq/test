-- Does the main CPU ever touch the PSX pad/memory-card port (SIO0 0x1f801040-4f) or SIO1 (0x1f801050-5f)? Coin+start applied meanwhile.
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_sio_log.txt", "w")
local n = {R0=0, W0=0, R1=0, W1=0, CTRL_IRQ_R=0}
local first = {}
local frame = 0
local function rec(k, off, data, mask) n[k] = n[k] + 1 if not first[k] then first[k] = string.format("frame %d off=%08x data=%08x mask=%08x pc=%08x", frame, off, data, mask, st["pc"].value) end end
KEEP[#KEEP+1] = mem:install_read_tap(0x1f801040, 0x1f80104f, "sio0r", function(off, data, mask) rec("R0", off, data, mask) return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f801040, 0x1f80104f, "sio0w", function(off, data, mask) rec("W0", off, data, mask) return data end)
KEEP[#KEEP+1] = mem:install_read_tap(0x1f801050, 0x1f80105f, "sio1r", function(off, data, mask) rec("R1", off, data, mask) return data end)
KEEP[#KEEP+1] = mem:install_write_tap(0x1f801050, 0x1f80105f, "sio1w", function(off, data, mask) rec("W1", off, data, mask) return data end)
-- control: the IRQ controller registers (0x1f801070/74) are certainly used; proves taps in this region work
KEEP[#KEEP+1] = mem:install_read_tap(0x1f801070, 0x1f801077, "ctl", function(off, data, mask) rec("CTRL_IRQ_R", off, data, mask) return data end)
local f1 = m.ioport.ports[":IN1"].fields
local f0 = m.ioport.ports[":IN0"].fields
local ev = { [2600] = function() f1["Coin 1"]:set_value(1) end, [2606] = function() f1["Coin 1"]:set_value(0) end,
             [2700] = function() f0["1 Player Start"]:set_value(1) end, [2706] = function() f0["1 Player Start"]:set_value(0) end }
emu.register_frame_done(function()
  frame = frame + 1
  if ev[frame] then ev[frame]() end
  if frame == 2800 then
    for _, k in ipairs({"R0","W0","R1","W1","CTRL_IRQ_R"}) do out:write(string.format("%s count=%d first: %s\n", k, n[k], first[k] or "-")) end
    out:close(); m:exit()
  end
end)
