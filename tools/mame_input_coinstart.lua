-- Input injection script for tools/mame_pc_capture.lua (PCC_INPUT=this file).
-- Returns function(frame). Timeline (frames at ~60 Hz): coin at 1500 & 1600, 1P Start at 1700,
-- then every 90 frames alternately tap P1 Right / P1 Button 1 (advance select screens).
-- Port/field names from MAME 0.264 -listxml tektagt:  :IN1 "Coin 1", :IN0 "1 Player Start", ...
local ports = manager.machine.ioport.ports
local function fld(tag, name) return ports[tag].fields[name] end
local coin, start = fld(":IN1", "Coin 1"), fld(":IN0", "1 Player Start")
local right, b1 = fld(":IN0", "P1 Right"), fld(":IN0", "P1 Button 1")
local function pulse(f, frame, at, len) f:set_value((frame >= at and frame < at + len) and 1 or 0) end
return function(frame)
  pulse(coin, frame, 1500, 6)
  if frame >= 1600 and frame < 1606 then coin:set_value(1) end
  pulse(start, frame, 1700, 6)
  if frame >= 1800 then
    local k = (frame - 1800) // 90
    local ph = (frame - 1800) % 90
    right:set_value((k % 2 == 0 and ph < 6) and 1 or 0)
    b1:set_value((k % 2 == 1 and ph < 6) and 1 or 0)
  end
end
