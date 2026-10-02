-- a03: empirical main-CPU view of H8 shared RAM and AT28C16 lanes (run with a scratch NVRAM dir; writes to EEPROM!)
local m = manager.machine
local cpu = m.devices[":maincpu"]
local sub = m.devices[":sub"]
local mem = cpu.spaces["program"]
local smem = sub.spaces["program"]
local out = io.open("mainview.txt","w")
local function log(...) out:write(string.format(...), "\n") end
local f = 0
emu.register_frame_done(function()
  f = f + 1
  if f == 3 then
    -- shared RAM: write 32-bit on main, read back on main (32/16/8) and on H8
    local base = 0x1f083ff0
    mem:write_u32(base, 0x11223344)
    mem:write_u32(base+4, 0x55667788)
    log("main wr32 %08x=11223344 ; +4=55667788", base)
    log("main rd32 %08x %08x", mem:read_u32(base), mem:read_u32(base+4))
    log("main rd16 +0=%04x +2=%04x +4=%04x +6=%04x", mem:read_u16(base), mem:read_u16(base+2), mem:read_u16(base+4), mem:read_u16(base+6))
    log("main rd8 +0..+7 = %02x %02x %02x %02x %02x %02x %02x %02x", mem:read_u8(base), mem:read_u8(base+1), mem:read_u8(base+2), mem:read_u8(base+3), mem:read_u8(base+4), mem:read_u8(base+5), mem:read_u8(base+6), mem:read_u8(base+7))
    -- 1:1 byte mapping hypothesis: main 0x1f080000+x <-> H8 0x080000+x
    for a = 0x83ff0, 0x83fff, 2 do log("H8 rd16 %06x = %04x", a, smem:read_u16(a)) end
    -- write 16-bit on main upper half only
    mem:write_u16(base+10, 0xabcd)
    log("main wr16 %08x=abcd ; rd32=%08x", base+8+2, mem:read_u32(base+8))
    for a = 0x83ff8, 0x83ffe, 2 do log("H8 rd16 %06x = %04x", a, smem:read_u16(a)) end
    -- H8 side write, read from main
    smem:write_u16(0x80100, 0xcafe)
    log("H8 wr16 080100=cafe ; main rd32 1f080200 (idx 0x80)=%08x ; rd32 1f080100=%08x", mem:read_u32(0x1f080200), mem:read_u32(0x1f080100))
    -- sharedram upper region reachability: main 0x1f083fff last, 0x1f084000?
    log("main rd32 1f084000=%08x (outside 0x1f080000-0x1f083fff range)", mem:read_u32(0x1f084000))
    -- EEPROM lanes
    log("EEPROM raw main reads 1f140000..: %08x %08x %08x %08x", mem:read_u32(0x1f140000), mem:read_u32(0x1f140004), mem:read_u32(0x1f140008), mem:read_u32(0x1f14000c))
    log("EEPROM u8 reads 1f140000..07: %02x %02x %02x %02x %02x %02x %02x %02x", mem:read_u8(0x1f140000), mem:read_u8(0x1f140001), mem:read_u8(0x1f140002), mem:read_u8(0x1f140003), mem:read_u8(0x1f140004), mem:read_u8(0x1f140005), mem:read_u8(0x1f140006), mem:read_u8(0x1f140007))
    log("EEPROM u8 reads 1f140280..: %02x %02x %02x %02x ; u32 1f140500: %08x %08x", mem:read_u8(0x1f140500), mem:read_u8(0x1f140502), mem:read_u8(0x1f140504), mem:read_u8(0x1f140506), mem:read_u32(0x1f140500), mem:read_u32(0x1f140504))
    mem:write_u32(0x1f140010, 0x00aa00bb)
    log("EEPROM write32 1f140010=00aa00bb ; readback u32=%08x u8@+0=%02x @+2=%02x", mem:read_u32(0x1f140010), mem:read_u8(0x1f140010), mem:read_u8(0x1f140012))
    -- IN ports on the H8 side
    log("H8 IN0 300000=%04x IN1 300002=%04x ; u8 300000..3=%02x %02x %02x %02x", smem:read_u16(0x300000), smem:read_u16(0x300002), smem:read_u8(0x300000), smem:read_u8(0x300001), smem:read_u8(0x300002), smem:read_u8(0x300003))
  end
  if f == 6 then
    -- 0x1f700000 mirror behaviour: read via debug path cannot show side effects; just read what unmapped/ROM returns
    log("read 1f700000=%08x ; 1ff00000=%08x ; ROM@1fc00000=%08x ROM@1ff00000-offset=%08x", mem:read_u32(0x1f700000), mem:read_u32(0x1ff00000), mem:read_u32(0x1fc00000), mem:read_u32(0x1fc00000+0x300000))
    -- reads of the 1fa00000 bank window: should equal bankedroms entry 0 (word-swapped?) 
    log("bank window 1fa00000..: %08x %08x %08x %08x", mem:read_u32(0x1fa00000), mem:read_u32(0x1fa00004), mem:read_u32(0x1fa00008), mem:read_u32(0x1fa0000c))
    local r = m.memory.regions[":bankedroms"]
    log("bankedroms region read_u32 0..: %08x %08x %08x %08x", r:read_u32(0), r:read_u32(4), r:read_u32(8), r:read_u32(12))
    log("bank window 1fb00004=%08x 1fb80004=%08x 1fb00000=%08x 1fb80000=%08x (protection windows)", mem:read_u32(0x1fb00004), mem:read_u32(0x1fb80004), mem:read_u32(0x1fb00000), mem:read_u32(0x1fb80000))
    local mr = m.memory.regions[":maincpu:rom"]
    log("maincpu:rom region u32 0..: %08x %08x ; 1fc00000 window: %08x %08x", mr:read_u32(0), mr:read_u32(4), mem:read_u32(0x1fc00000), mem:read_u32(0x1fc00004))
    log("RAM mirror test (4MB RAM window): rd 00000000=%08x 00400000=%08x 80400000=%08x 80000000=%08x", mem:read_u32(0), mem:read_u32(0x400000), mem:read_u32(0x80400000), mem:read_u32(0x80000000))
    out:close(); m:exit()
  end
end)
