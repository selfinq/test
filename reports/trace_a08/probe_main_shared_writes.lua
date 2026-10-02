-- Every main-CPU write to the shared RAM window during boot (frames 0..700) with frame number, for replaying the main side's initialisation into an H8-only test harness.
KEEP = {}
local m = manager.machine
local cpu = m.devices[":maincpu"]; local st = cpu.state; local mem = cpu.spaces["program"]
local out = io.open((os.getenv("A08_OUT") or "/tmp/a08_out") .. "/main_shared_writes.txt", "w")
local frame = 0
KEEP[#KEEP+1] = mem:install_write_tap(0x1f080000, 0x1f083fff, "msw", function(off, data, mask)
  out:write(string.format("%d %05x %08x %08x %08x\n", frame, off & 0x3ffc, data, mask, st["pc"].value)) return data end)
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 700 then out:close(); m:exit() end
end)
