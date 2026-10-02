-- MAME 0.264 Lua: for several emulated-time windows, dump the H8's RAM at the window start and trace every H8 instruction
-- (pre-state ER0-ER7, CCR, totalcycles, PC, disassembly) until the window ends. Used to oracle-replay the C# H8 core on
-- code that runs deep inside the game (see SOUND.md). Needs: -debug -debugger none.
-- env: S12_OUT (dir), S12_WINDOWS ("t0:t1,t0:t1,...", seconds)
local m = manager.machine
local out = os.getenv("S12_OUT") or "."
local sub = m.devices[":sub"]
local sp = sub.spaces["program"]
local d = m.debugger
local wins = {}
for a, b in string.gmatch(os.getenv("S12_WINDOWS") or "10:10.03", "([%d%.]+):([%d%.]+)") do wins[#wins+1] = {tonumber(a), tonumber(b), false, false} end
local function dump(idx)
  local f = io.open(string.format("%s/ram_%d.bin", out, idx), "wb")
  -- shared RAM window 0x080000-0x08ffff followed by on-chip RAM 0xfffd10-0xffff0f
  for a = 0x080000, 0x08ffff do f:write(string.char(sp:read_u8(a))) end
  for a = 0xfffd10, 0xffff0f do f:write(string.char(sp:read_u8(a))) end
  f:close()
end
emu.register_frame_done(function()
  local t = m.time:as_double()
  for i, w in ipairs(wins) do
    if (not w[3]) and t >= w[1] then
      w[3] = true
      dump(i)
      d:command(string.format('trace %s/win_%d.txt,:sub,noloop,{tracelog "R=%%x,%%x,%%x,%%x,%%x,%%x,%%x,%%x C=%%x T=%%d ",er0,er1,er2,er3,er4,er5,er6,er7,ccr,totalcycles}', out, i))
      d:command("go")
    end
    if w[3] and (not w[4]) and t >= w[2] then
      w[4] = true
      d:command("trace off")
    end
  end
  local all = true
  for _, w in ipairs(wins) do if not w[4] then all = false end end
  if all then m:exit() end
end)
