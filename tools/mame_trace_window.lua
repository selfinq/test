-- Ground-truth check: log EVERY instruction PC of maincpu for a few short windows using MAME's debugger
-- 'trace' command, to compare against the PC set from mame_pc_capture.lua (see tools/mame_pc_validate.py).
-- Env: TW_STARTS "3000,3500" (frame numbers), TW_FRAMES (window length in frames, default 2), TW_PREFIX output prefix,
--      PCC_INPUT optional input script (same one used for the capture run to reproduce the scenario).
-- Run with: mame tektagt -debug -debugger none -autoboot_script tools/mame_trace_window.lua ...  (NVRAM from seed)
local dbg = manager.machine.debugger
local starts = {}
for s in string.gmatch(os.getenv("TW_STARTS") or "3000", "%d+") do starts[#starts + 1] = tonumber(s) end
local len = tonumber(os.getenv("TW_FRAMES") or "2")
local prefix = os.getenv("TW_PREFIX") or "tw"
local input_fn
if os.getenv("PCC_INPUT") then input_fn = dofile(os.getenv("PCC_INPUT")) end
local frame, k, active_until = 0, 1, nil
emu.register_frame_done(function()
  frame = frame + 1
  if input_fn then input_fn(frame) end
  if active_until and frame >= active_until then
    dbg:command("trace off")
    active_until = nil
    if k > #starts then manager.machine:exit() end
  end
  if not active_until and k <= #starts and frame == starts[k] then
    dbg:command(string.format("trace %s_%d.txt,maincpu,noloop", prefix, starts[k]))
    active_until = frame + len
    k = k + 1
  end
end)
