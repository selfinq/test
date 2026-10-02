-- a03: vblank timing as seen at frame_done (start of vblank): remaining time to line 0, frame period, vpos
local m = manager.machine
local scr = m.screens[":screen"]
local out = io.open("vbl.txt","w")
local f = 0
emu.register_frame_done(function()
  f = f + 1
  if f == 5 or f == 100 or f == 380 or f == 450 or f == 700 or f == 1000 then
    local ok, v = pcall(function() return scr:time_until_pos(0, 0) end)
    out:write(string.format("f%d size=%dx%d frame_period=%.9f (%.4f Hz) time_until_pos(0,0)=%s\n", f, scr.width, scr.height, scr.frame_period, 1/scr.frame_period, ok and string.format("%.9f", v) or tostring(v)))
    out:flush()
  end
  if f >= 1000 then out:close(); m:exit() end
end)
