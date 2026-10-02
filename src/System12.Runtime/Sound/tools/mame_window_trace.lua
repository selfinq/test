-- MAME 0.264 Lua: trace the H8 (pre-instruction ER0-ER7, CCR, PC, disassembly) only inside an emulated-time window.
-- env: S12_OUT (dir), S12_T0, S12_T1 (seconds). Needs: -debug -debugger none
local m = manager.machine
local out = os.getenv("S12_OUT") or "."
local t0 = tonumber(os.getenv("S12_T0") or "10")
local t1 = tonumber(os.getenv("S12_T1") or "10.1")
local started = false
local d = m.debugger
emu.register_frame_done(function()
  local t = m.time:as_double()
  if (not started) and t >= t0 then
    started = true
    d:command('trace ' .. out .. '/window_trace.txt,:sub,noloop,{tracelog "R=%x,%x,%x,%x,%x,%x,%x,%x C=%x T=%f ",er0,er1,er2,er3,er4,er5,er6,er7,ccr,0}')
    d:command("go")
  end
  if t >= t1 then m:exit() end
end)
