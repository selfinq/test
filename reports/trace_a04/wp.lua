-- Who writes the 4 words of the seq-305020 table after the DMA? (watchpoints set just before frame 8015)
local m = manager.machine
local dbg = m.debugger
local f = 0
local armed = false
emu.register_frame_done(function()
  f = f + 1
  if f == 8013 and not armed then
    armed = true
    for _, a in ipairs({0x803988b4, 0x80398ac0, 0x80399294, 0x80399520}) do
      dbg:command(string.format('wpset %08x,4,w,1,{printf "WP f%d pc=%%08x addr=%08x data=%%08x\\n",pc,wpdata; go}', a, 0, a))
    end
  end
  if f >= 8025 then m:exit() end
end)
dbg:command('go')
