-- Who writes the 4 words of the seq-305020 table after the DMA? (watchpoints set just before frame 8015)
local m = manager.machine
local dbg = m.debugger
local f = 0
local armed = false
emu.register_frame_done(function()
  f = f + 1
  if f == 8013 and not armed then
    armed = true
    for _, a in ipairs({0x003988b4, 0x00398ac0, 0x003992d4, 0x003994e0}) do
      dbg:command(string.format('wpset %08x,4,w,1,{printf "WP f%d pc=%%08x addr=%08x data=%%08x\\n",pc,wpdata; go}', a, 0, a))
    end
  end
  if f >= 8025 then m:exit() end
end)
dbg:command('go')
