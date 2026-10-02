local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local sub = m.devices[":sub"]; local sst = sub.state; local smem = sub.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/probe_tap_log.txt", "w")
local function log(...) out:write(string.format(...), "\n") end
local c = {}
local function hook(space, a, b, name)
  space:install_read_tap(a, b, name.."r", function(off, data, mask) c[name.."r"] = (c[name.."r"] or 0)+1; if c[name.."r"]<=5 then log("%s R off=%08x data=%08x mask=%08x", name, off, data, mask) end return data end)
  space:install_write_tap(a, b, name.."w", function(off, data, mask) c[name.."w"] = (c[name.."w"] or 0)+1; if c[name.."w"]<=5 then log("%s W off=%08x data=%08x mask=%08x", name, off, data, mask) end return data end)
end
hook(mem, 0x1f000000, 0x1fbfffff, "mainio")
hook(mem, 0x1fc00000, 0x1fc3ffff, "mainrom")
hook(mem, 0x80000000, 0x8000ffff, "ramk0")
hook(mem, 0x00000000, 0x0000ffff, "ramphys")
hook(mem, 0xbf000000, 0xbf3fffff, "kseg1io")
hook(smem, 0x080000, 0x08ffff, "sshared")
hook(smem, 0x300000, 0x300003, "sin")
hook(smem, 0x280000, 0x287fff, "c352")
hook(smem, 0x000000, 0x07ffff, "srom")
local f = 0
emu.register_frame_done(function() f = f + 1
  if f == 400 then for k,v in pairs(c) do log("COUNT %s %d", k, v) end out:close() m:exit() end end)
