local m = manager.machine
local mem = m.devices[":maincpu"].spaces["program"]
local f = 0
emu.register_frame_done(function()
  f = f + 1
  if f == 5 then
    print("writing bank 27 (valid)")
    mem:write_u16(0x1f000000, 27)
    print("window now", string.format("%08x", mem:read_u32(0x1fa00000)))
    print("writing bank 28 (one past the end)")
    mem:write_u16(0x1f000000, 28)
    print("survived write of bank 28; window", string.format("%08x", mem:read_u32(0x1fa00000)))
    m:exit()
  end
end)
