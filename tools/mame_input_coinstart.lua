-- Input injection script for tools/mame_pc_capture.lua (PCC_INPUT=this file) and any MAME Lua run.
-- Returns function(frame) to be called once per frame_done. Deterministic (fixed LCG, no os.time/random).
-- Timeline (60 Hz frames, from the archived NVRAM seed; verified by screenshots in the report):
--   1500 / 1600  Coin 1 pulse (6 frames each)      -> credits
--   1700         1 Player Start pulse              -> character select appears (~frame 1800)
--   1800..2399   every 90 frames alternately tap P1 Right / P1 Button 1 (cursor move / confirm)
--   >= 2400      button masher: every 6 frames a new pseudo-random combination of P1 directions
--                and Buttons 1-4 is held (exercises fight code). Match: "STAGE 1" fight by ~frame 3600.
-- Port/field names from MAME 0.264 -listxml tektagt:  :IN1 "Coin 1", :IN0 "1 Player Start", ...
local ports = manager.machine.ioport.ports
local function fld(tag, name) return ports[tag].fields[name] end
local coin, start = fld(":IN1", "Coin 1"), fld(":IN0", "1 Player Start")
local dirs = { fld(":IN0", "P1 Right"), fld(":IN0", "P1 Left"), fld(":IN0", "P1 Down"), fld(":IN0", "P1 Up") }
local btns = { fld(":IN0", "P1 Button 1"), fld(":IN0", "P1 Button 2"), fld(":IN0", "P1 Button 3"), fld(":IN1", "P1 Button 4") }
local seed = 12345
local function rnd() seed = (seed * 1103515245 + 12345) % 2147483648; return seed >> 8 end
local function set_all(list, v) for _, f in ipairs(list) do f:set_value(v) end end
return function(frame)
  coin:set_value(((frame >= 1500 and frame < 1506) or (frame >= 1600 and frame < 1606)) and 1 or 0)
  start:set_value((frame >= 1700 and frame < 1706) and 1 or 0)
  if frame >= 1800 and frame < 2400 then
    local k, ph = (frame - 1800) // 90, (frame - 1800) % 90
    dirs[1]:set_value((k % 2 == 0 and ph < 6) and 1 or 0)
    btns[1]:set_value((k % 2 == 1 and ph < 6) and 1 or 0)
  elseif frame >= 2400 and (frame % 6) == 0 then
    local r = rnd()
    for i, f in ipairs(dirs) do f:set_value(((r >> i) & 3) == 0 and 1 or 0) end
    for i, f in ipairs(btns) do f:set_value(((r >> (i + 5)) & 3) == 0 and 1 or 0) end
  end
end
