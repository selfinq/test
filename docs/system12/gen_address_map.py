#!/usr/bin/env python3
"""Generator for docs/system12/address_map.json and the tables in ADDRESS_MAP.md.

Single source of truth for the tektagt (Namco System 12, COH-700 CPU PCB) address map.
Run:  python3 docs/system12/gen_address_map.py   (from the repository root)

Every row carries an evidence list and a status:
  V-drv   verified by reading the MAME driver / device source (line numbers given)
  V-lua   verified dynamically with a MAME 0.264 Lua probe (reports/trace_a03/)
  V-both  both of the above
  HYP     HYPOTHESIS: inferred, not proven
Evidence tags (see ADDRESS_MAP.md section 0):
  drv:N    reports/refs/namcos12.cpp (MAME master f8f6b22) line N
  drv264:N MAME 0.264 src/mame/namco/namcos12.cpp (tag mame0264) line N
  cpu:N    src/devices/cpu/psx/psx.cpp @f8f6b22 line N      (cpu264:N = tag mame0264)
  irq:/dma:/rcnt:/sio:  src/devices/cpu/psx/{irq,dma,rcnt,sio}.cpp @f8f6b22
  gpu:N    src/devices/video/psx.cpp @f8f6b22 (= reports/refs/psx.cpp)
  at28:N   src/devices/machine/at28c16.cpp @f8f6b22
  lua:F    reports/trace_a03/F (MAME 0.264 output)
  img:VA   static disassembly of the decompressed game image (generated/ttt_image.bin, loaded at VA)
  rom:OFF  static disassembly / bytes of the boot ROM (generated/regions/maincpu_rom.bin, file offset)
"""
import json, os, sys

OUT = os.path.dirname(os.path.abspath(__file__))


def h(x):
    return "0x%08x" % x


# ----------------------------------------------------------------------------------------------
# Main CPU (CXD8661R) physical address map.  All addresses physical (vaddr & 0x1fffffff).
# ----------------------------------------------------------------------------------------------
MAIN = [
    dict(id="RAM", lo=0x00000000, hi=0x003fffff, dev="System RAM 4 MB (ram_device, 32-bit)", rw="RW",
         width="8/16/32",
         notes="Also visible at kseg0 0x80000000 and kseg1 0xa0000000. Visible window is set by RAM_SIZE (0x1f801060): "
               "reset value 0x800 = 2 MB window; boot writes 0xfb4 (16 MB window, 4 MB RAM repeated) then 0xcb4 (4 MB window), "
               "and 0xcb4 stays for the whole run. Contents power up as 0 (set_default_value(0)).",
         ev=["drv:1124", "cpu:1353-1414", "cpu:2017", "lua:access_trace_1800.txt (W 1f801060 = 00000fb4, 00000cb4)",
             "lua:final_checks.txt (RAM alias test)"], st="V-both"),
    dict(id="RAM_BOOTHACK", lo=0x00010000, hi=0x0001000b, dev="RAM bytes pre-loaded at machine_reset", rw="R (boot data)",
         width="8",
         notes="coh700b sets m_boot_hack: at every machine_reset 12 bytes are copied from ROM offset 0x20280 to RAM 0x10000. "
               "The bytes are ASCII 'Namco Ltd.--' (4e616d636f204c74642e2d2d). The boot code checks them early; how they get "
               "there on real hardware is unknown (driver comment).",
         ev=["drv:1132-1139", "drv:1250-1258", "lua:final_checks.txt (frame1)", "rom:0x20280"], st="V-both"),
    dict(id="BERR_LOW", lo=0x00400000, hi=0x1effffff, dev="(none) bus error", rw="RW", width="-",
         notes="Once RAM_SIZE window = 4 MB everything between the end of the window and 0x1effffff is installed as "
               "psxcpu berr_r/berr_w (sets the CPU bus-error flag -> DBE exception, returns 0). Reads beyond 4 MB return 0 "
               "(not a mirror).",
         ev=["cpu:1411-1413", "lua:final_checks.txt (RAM beyond 4MB)"], st="V-both"),
    dict(id="BANKREG", lo=0x1f000000, hi=0x1f000003, dev="Bank register (mainbank select)", rw="W16 (low half) / R=nop",
         width="16",
         notes="Read = nop (0). 16-bit write to 0x1f000000-1 -> bankoffset_w -> mainbank.set_entry(data) selecting a 2 MB bank of "
               "'bankedroms' (entries 0..27 for tektagt). tektagt (base namcos12_state, not altbank) uses data directly. "
               "OBSERVED: tektagt NEVER writes it (0 writes in 1500 frames) and only dummy-reads the bank window.",
         ev=["drv:1263-1264", "drv:1285-1289", "drv:1232-1233", "lua:final_checks.txt (bank register write count = 0)"],
         st="V-both"),
    dict(id="STROBE_8000", lo=0x1f008000, hi=0x1f008003, dev="write-only strobe (nop)", rw="W", width="16",
         notes="MASTER ONLY (absent from the 0.264 map, where the write is unmapped and ignored). The game stores halfword 0 "
               "here from three code sites, one of them after every ROM-DMA chunk (img:0x8012e550; others img:0x800f9214, "
               "0x8012e77c): 8832 writes in 1800 frames vs 8089 DMA5 starts. Purpose unknown.",
         ev=["drv:1265", "img:0x8012e550", "lua:final_checks.txt", "lua:access_trace_1800.txt (W 1f008000 n=8832)"],
         st="V-both", ver="master"),
    dict(id="STROBE_10000", lo=0x1f010000, hi=0x1f010003, dev="write-only strobe (nop)", rw="W", width="16",
         notes="Driver comment (0.264): 'very spammy'. tektagt: 0 writes observed in 1500 frames.",
         ev=["drv:1266", "drv264:1438", "lua:final_checks.txt"], st="V-both"),
    dict(id="STROBE_18000", lo=0x1f018000, hi=0x1f018003, dev="write-only strobe (nop)", rw="W", width="16",
         notes="HYPOTHESIS: watchdog-clear style kick. Facts: halfword 0 written from ~55 code sites (img:0x8017bbf0 ...) "
               "and from the boot ROM (rom:0x20f64); 1,105,505 writes in 1500 frames (up to 83,364 in one frame), i.e. it is "
               "poked inside polling/delay loops, not once per frame. MAME ignores it.",
         ev=["drv:1267", "img:0x8017bbf0", "rom:0x20f64", "lua:final_checks.txt", "lua:access_trace_1800.txt (W 1f018000)"],
         st="V-both (existence/behaviour) + HYP (purpose)"),
    dict(id="SHARED_RAM", lo=0x1f080000, hi=0x1f083fff, dev="H8/3002 shared RAM (16 KB window of 64 KB)", rw="RW",
         width="8/16/32 (device is 16-bit)",
         notes="Byte-for-byte window onto H8 address 0x080000-0x083fff (main 0x1f080000+x <-> H8 0x080000+x). Each 16-bit unit "
               "is one array element: the 16-bit VALUE seen by the little-endian main CPU at even x equals the 16-bit value the "
               "big-endian H8 reads at x (so bytes are swapped between the two views). 32-bit accesses are split into two "
               "16-bit handler calls. H8 sees 64 KB (0x080000-0x08ffff); main CPU cannot reach 0x084000+ (reads 0). "
               "Game uses 0x1f080100-0x1f08217c and 0x1f083000-0x1f083a00 (sound command/status mailbox).",
         ev=["drv:1268", "drv:1278", "drv:1291-1301", "lua:mainview.txt", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="EEPROM", lo=0x1f140000, hi=0x1f140fff, dev="AT28C16 2Kx8 EEPROM (NVRAM)", rw="RW", width="8 (lanes 0 and 2)",
         notes="umask32(0x00ff00ff): byte lanes 0 and 2 of every 32-bit word carry data; EEPROM byte index i is at physical "
               "address 0x1f140000 + 2*i (index 0..0x7ff). Odd byte addresses read 0. Game accesses with lhu/sh and ignores "
               "the unused lane. Verified with real data: index 0x280 (build-info string) is read at 0x1f140500. "
               "Write: ignored while busy; only changes the cell if the byte differs; 200 us busy; while busy a read returns "
               "(last_written ^ 0x80). Backing file 'at28c16' is 0x800+0x20 = 2080 bytes, default 0xff.",
         ev=["drv:1269", "at28:147-203", "lua:mainview.txt", "lua:eeprom_trace.txt", "lua:access_trace_1800.txt",
             "img:0x8017d488"], st="V-both"),
    dict(id="ROMBOARD_PORT", lo=0x1f1bff08, hi=0x1f1bff0f, dev="write-only port (nop) - ROM-board/KEYCUS data port?", rw="W",
         width="16",
         notes="HYPOTHESIS for purpose. Facts: boot ROM loop at rom:0x20b80 reads every 4th byte-pair of ROM[0x200 ..] "
               "(0xf7f80 = 1,015,680 halfwords) and stores each to 0x1f1bff0e, then writes 3 to 0x1f1bff0c and 1 to "
               "0x1f1bff0a (rom:0x20f20-0x20f30) before jumping to the entry word. Game code writes 3/1 to the same "
               "addresses again (img:0x8017d1b4). MAME ignores it.",
         ev=["drv:1270", "rom:0x20b68-0x20b94", "rom:0x20f14-0x20f30", "img:0x8017d1b4",
             "lua:access_trace_1800.txt (W 1f1bff0c n=1015681, W 1f1bff08 n=1)"], st="V-both (behaviour) + HYP (purpose)"),
    dict(id="DMAOFFSET", lo=0x1f700000, hi=0x1f70ffff, dev="ROM DMA offset register (m_dmaoffset)", rw="W16",
         width="16",
         notes="16-bit write at byte offset X (0..0xfffe) of the window with data D sets m_dmaoffset = X | (D << 16). "
               "Bit 31 (D bit 15) set selects the MAIN program ROM ('maincpu:rom', offset & 0xffffff) for the next DMA5, "
               "otherwise the 'bankedroms' region. The game uses it for main-ROM reads (img:0x8012e758: "
               "sh 0x84xx -> offset 0x84xx0000|X) and writes 0 at 0x1f700000 before each banked read. Reads: see PROT3.",
         ev=["drv:1271", "drv:1303-1308", "drv:1310-1334", "lua:final_checks.txt (protection/dmaoffset sequence)",
             "lua:access_trace_1800.txt (W 1f70xxxx)"], st="V-both"),
    dict(id="PROT3", lo=0x1f700000, hi=0x1f700003, dev="tektagt_protection_3_r (DMA 'arm' read)", rw="R16 (W goes to DMAOFFSET)",
         width="16",
         notes="Any read returns 0 and sets m_has_tektagt_dma = true, which makes the next DMA5 use m_tektagdmaoffset "
               "(register PROT1) instead of m_dmaoffset; the flag is cleared when that DMA runs and on machine_reset. "
               "Master adds mirror(0x800000) (=0x1ff00000..3), which lies inside the ROM window and is shadowed by it "
               "(HYPOTHESIS: no effect); 0.264 has no mirror. Game: lhu at img:0x8012e518, 7978 times.",
         ev=["drv:1845", "drv:1932-1937", "drv264:1466", "drv264:1610-1616", "img:0x8012e518",
             "lua:final_checks.txt", "lua:access_trace_1800.txt (R 1f700000 n=7978)"], st="V-both"),
    dict(id="BANKWIN", lo=0x1fa00000, hi=0x1fbfffff, dev="mainbank: 2 MB banked ROM window (bankedroms)", rw="R",
         width="8/16/32",
         notes="Read-only view of bankedroms[bank*0x200000 .. +0x1fffff] (bank chosen by BANKREG, default 0). Writes unmapped. "
               "tektagt only performs dummy lhu at 0x1fa00000 (img:0x8012e548; 7673 reads) - game data does NOT come through "
               "this window but through DMA5 (see PROT1 + DMAOFFSET). Windows PROT1/PROT2 overlay 0x1fb00000-3 and "
               "0x1fb80000-3 (reads AND writes).",
         ev=["drv:1272", "drv:1232-1233", "img:0x8012e548", "lua:final_checks.txt (bank window reads)"], st="V-both"),
    dict(id="PROT1", lo=0x1fb00000, hi=0x1fb00003, dev="tektagt protection reg 1 / second DMA offset", rw="RW16 (x2)",
         width="16",
         notes="Write offset0 (low half): m_tektagdmaoffset = D; if ttt_cnt != 2: ttt_val[ttt_cnt] = D. "
               "Write offset1 (high half): m_tektagdmaoffset |= D<<16; if ttt_cnt != 2: ttt_val[ttt_cnt++] |= D<<16. "
               "A 32-bit store does both. Read offset0 -> 0x8000 (busy flag clear), offset1 -> 0; a 32-bit load returns 0x00008000. "
               "m_tektagdmaoffset is the BYTE offset into bankedroms used by DMA5 when armed via PROT3 "
               "(game aligns it with & ~3).",
         ev=["drv:1846", "drv:1850-1883", "drv264:1464", "drv264:1528-1561", "img:0x8012e508", "img:0x800f8e9c",
             "lua:final_checks.txt (protection/dmaoffset sequence)"], st="V-both"),
    dict(id="PROT2", lo=0x1fb80000, hi=0x1fb80003, dev="tektagt protection reg 2 (challenge/response)", rw="RW16 (x2)",
         width="16",
         notes="Write offset0 (any value, game writes 0): ttt_cnt = 0 (restart challenge). Write offset1: ignored. "
               "Read: if ttt_cnt == 2 and (ttt_val[0],ttt_val[1]) == (806d2c24,d5545715): offset0 -> 0x36e2; "
               "== (804c2c84,d5545615): offset0 -> 0x2651; == (2aaba8e6,00820040): offset1 -> 0x4186; "
               "== (2aaba592,01780544): offset1 -> 0x3c7d; else if ((ttt_val[1]>>16)&0xff)==0xa9: 0x552e (either offset). "
               "Everything else returns 0. Observed responses: 36e2, 2651, 41860000 (32-bit read), 0.",
         ev=["drv:1847", "drv:1885-1930", "drv264:1465", "drv264:1563-1608", "img:0x800f8e84", "lua:final_checks.txt"],
         st="V-both"),
    dict(id="SCRATCHPAD", lo=0x1f800000, hi=0x1f8003ff, dev="Scratchpad (D-cache used as 1 KB RAM)", rw="RW", width="8/16/32",
         notes="Becomes RAM only when BIU (0xfffe0130) has RAM(0x08)=1 and DS(0x80)=1 (game/boot write 0x0001e988). Otherwise "
               "reads/writes bus-error (RAM bit off) or reads bus-error/writes nop (DS off). Physical only for the "
               "static map: kseg0/kseg1 aliases are NOT mapped for the scratchpad (access it as 0x1f800000).",
         ev=["cpu:1336-1350", "cpu:1758", "lua:access_trace_1800.txt (BIU W 0001e988)"], st="V-both"),
    dict(id="SCRATCH_GAP", lo=0x1f800400, hi=0x1f800fff, dev="(none) bus error", rw="RW", width="-",
         notes="berr_r/berr_w.", ev=["cpu:1759"], st="V-drv"),
    dict(id="HW_EXP1_BASE", lo=0x1f801000, hi=0x1f801003, dev="EXP1_BASE (exp_base)", rw="RW32", width="32",
         notes="Stores 0x1f000000 | (v & 0xffffff). DOES NOT relocate the driver map. Only effect: DMA5 treats the main ROM "
               "as source if exp_base()==0x1f300000 (not used by tektagt). Observed 0x1f000000 (boot) / 0x1f200000 (game).",
         ev=["cpu:220-235", "cpu:1763", "drv:1316", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="HW_EXP2_BASE", lo=0x1f801004, hi=0x1f801007, dev="EXP2_BASE (plain RAM)", rw="RW32", width="32",
         notes="Backing store only (boot writes 0x1f802000).", ev=["cpu:1764"], st="V-both"),
    dict(id="HW_EXP1_CFG", lo=0x1f801008, hi=0x1f80100b, dev="EXP1_DELAY/SIZE (exp_config)", rw="RW32", width="32",
         notes="Stored with v &= 0xaf1fffff; no timing effect in MAME. Observed 00133777, 00173033, 001730ff, 211730ff, 261730ff.",
         ev=["cpu:237-247", "cpu:1765", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="HW_EXP3_CFG", lo=0x1f80100c, hi=0x1f80100f, dev="EXP3_DELAY (plain RAM)", rw="RW32", width="32",
         notes="Backing store only.", ev=["cpu:1766"], st="V-both"),
    dict(id="HW_ROM_CFG", lo=0x1f801010, hi=0x1f801013, dev="BIOS_ROM delay/size (rom_config)", rw="RW32", width="32",
         notes="Reset 0x00130000. Bits 16-20 = log2(ROM window size), capped at 4 MB; when they change the ROM window "
               "(0x1fc00000, and 0x9fc00000/0xbfc00000) is re-installed. Boot writes 0x0013246f (512 KB), 0x00163055 (4 MB), "
               "0x00163088 (4 MB).",
         ev=["cpu:266-281", "cpu:1416-1451", "cpu:2020", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="HW_DELAYS", lo=0x1f801014, hi=0x1f80101f, dev="SPU/CD/EXP2 delay regs (plain RAM)", rw="RW", width="32",
         notes="Backing store only.", ev=["cpu:1767"], st="V-drv"),
    dict(id="HW_COM_DELAY", lo=0x1f801020, hi=0x1f801023, dev="COM_DELAY", rw="RW32", width="32",
         notes="Stored, no effect. Observed 0x00031125.", ev=["cpu:283-293", "cpu:1771"], st="V-both"),
    dict(id="HW_MISC1", lo=0x1f801024, hi=0x1f80102f, dev="plain RAM", rw="RW", width="32", notes="", ev=["cpu:1772"], st="V-drv"),
    dict(id="SIO0", lo=0x1f801040, hi=0x1f80104f, dev="SIO0 (psxsio0)", rw="RW32", width="32",
         notes="Real device model, but nothing is connected (no pad/memcard) and tektagt never touches it (0 accesses).",
         ev=["cpu:1773", "sio:*", "lua:access_trace_1800.txt (no 1f8010[45]x lines)"], st="V-both"),
    dict(id="SIO1", lo=0x1f801050, hi=0x1f80105f, dev="SIO1 (psxsio1)", rw="RW32", width="32",
         notes="Same as SIO0, unconnected, unused.", ev=["cpu:1774"], st="V-both"),
    dict(id="HW_RAM_CFG", lo=0x1f801060, hi=0x1f801063, dev="RAM_SIZE (ram_config)", rw="RW32", width="32",
         notes="Reset 0x800. When bits 8-15 change the RAM windows are rebuilt: ((v>>8)&0xf) 0x3/0x2/0x5/0x6/0xc -> 4 MB window, "
               "0x0/0x1 -> 1 MB, 0x4/0x8/0x9 -> 2 MB, 0x7/0xa/0xb/0xd/0xe -> 8 MB, 0xf -> 16 MB; RAM (4 MB) is repeated across "
               "the window and everything above the window up to 0x1effffff bus-errors. Boot: 0xfb4 then 0xcb4.",
         ev=["cpu:249-264", "cpu:1353-1414", "cpu:2017", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="HW_MISC2", lo=0x1f801064, hi=0x1f80106f, dev="plain RAM", rw="RW", width="32", notes="", ev=["cpu:1776"], st="V-drv"),
    dict(id="IRQ", lo=0x1f801070, hi=0x1f801077, dev="Interrupt controller I_STAT (+0) / I_MASK (+4)", rw="RW32", width="32",
         notes="Write I_STAT: new = (stat & ~mem_mask) | (stat & mask & data) - NOTE MAME clears pending bits that are not in "
               "I_MASK on any write (real PS1: stat &= data). Write I_MASK: new = (mask & ~mem_mask) | data (bit 1 'CD' and bits "
               ">10 flagged unknown but kept). Output: (stat & mask) != 0 asserts CPU IRQ0 = COP0 Cause.IP2 (level, "
               "re-evaluated on every stat/mask change). Sources: bit0 VBLANK(GPU), bit3 DMA, bit4/5/6 root counters 0/1/2, "
               "bit7 SIO0, bit8 SIO1. Observed mask values 0x00, 0x01, 0x09, 0x29.",
         ev=["irq:17", "irq:53-65", "irq:67-90", "irq:108-190", "cpu:1777", "cpu:2296-2300", "cpu:3464",
             "lua:final_checks.txt (IRQ writes/reads)"], st="V-both"),
    dict(id="DMA", lo=0x1f801080, hi=0x1f8010ff, dev="DMA controller (psxdma): ch0-6 MADR/BCR/CHCR, DPCR @0x1f8010f0, DICR @0x1f8010f4",
         rw="RW32", width="32",
         notes="Channel n at 0x1f801080 + 0x10*n: +0 MADR, +4 BCR, +8 CHCR. System 12 assignments: ch0 MDEC-in, ch1 MDEC-out, "
               "ch2 GPU, ch3 CD (no handler), ch4 SPU (no handler), ch5 = ROM read (namcos12_rom_read), ch6 OTC. See DEVICES.md "
               "for the exact CHCR patterns MAME executes and their completion timing.",
         ev=["cpu:1778", "cpu:3466-3471", "drv:1161", "dma:214-376", "dma:378-418"], st="V-both"),
    dict(id="RCNT", lo=0x1f801100, hi=0x1f80112f, dev="Root counters 0-2 (psxrcnt): +0 count, +4 mode, +8 target", rw="RW32",
         width="32", notes="tektagt uses only counter 1 (hblank source) as a raster timer; counters 0 and 2 are zeroed at boot "
                           "and then free-run (mode 0x1000).",
         ev=["cpu:1779", "rcnt:*", "lua:access_trace_1800.txt (W 1f801114/18, snapshots)"], st="V-both"),
    dict(id="CD", lo=0x1f801800, hi=0x1f801803, dev="CD-ROM controller (not present)", rw="RW8", width="8",
         notes="psxcpu cd_r/cd_w forward to unconnected handlers (read 0, write ignored). No CD drive on System 12 "
               "(except Truck Kyosokyoku). tektagt: 0 accesses.",
         ev=["cpu:1780", "cpu:3435-3443"], st="V-both"),
    dict(id="GPU", lo=0x1f801810, hi=0x1f801817, dev="GPU CXD8654Q: +0 GP0/GPUREAD, +4 GP1/GPUSTAT", rw="RW32", width="32",
         notes="Forwarded to psxgpu_device::read/write. 2 MB VRAM (1024x1024x16). See DEVICES.md.",
         ev=["cpu:1781", "gpu:44-52", "gpu:3228-3300", "gpu:3424-3446", "drv:1127"], st="V-both"),
    dict(id="MDEC", lo=0x1f801820, hi=0x1f801827, dev="MDEC (psxmdec): +0 data/command, +4 status/control", rw="RW32", width="32",
         notes="Used by tektagt (581 command/DMA rounds from frame ~641: ch0 CHCR 0x01000201 / ch1 CHCR 0x01000200).",
         ev=["cpu:1782", "cpu:3469-3471", "lua:access_trace_1800.txt (W 1f801820 n=581)"], st="V-both"),
    dict(id="SPU", lo=0x1f801c00, hi=0x1f801fff, dev="SPU (not present)", rw="RW16", width="16",
         notes="Master maps 0x1f801c00-0x1f801fff (0.264: -0x1f801dff) to unconnected spu_r/spu_w (read 0, write ignored). "
               "Only the boot kernel touches it (writes 0xffff to 0x1f801c00, 0 to 0x1f801d80/84). Sound is the C352 on the H8 board.",
         ev=["cpu:1783", "cpu:3425-3433", "lua:access_trace_1800.txt (W 1f801c00, 1f801d80, 1f801d84 only at boot)"], st="V-both"),
    dict(id="EXP2_RAM", lo=0x1f802020, hi=0x1f802033, dev="plain RAM (20 bytes, unknown)", rw="RW", width="8/16/32",
         notes="Boot kernel writes 0x0b/0x0a/0x20/0x30/0x40/0x50 bytes at 0x1f802020 (PS1 'DUART' debug port).",
         ev=["cpu:1784", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="POST", lo=0x1f802040, hi=0x1f802043, dev="POST/dip port (write nop)", rw="W (R unmapped)", width="8",
         notes="Boot kernel writes progress codes 1..7 to byte 0x1f802041 (13 writes).",
         ev=["cpu:1787", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="ROM", lo=0x1fc00000, hi=0x1fffffff, dev="Main program ROM 'maincpu:rom' (4 MB, ROM_REGION32_LE)", rw="R", width="8/16/32",
         notes="Installed by psxcpu update_rom_config at 0x1fc00000, 0x9fc00000, 0xbfc00000. Window = 1<<((ROM_CFG>>16)&0x1f), "
               "capped 4 MB: reset 512 KB (ROM_CFG=0x00130000), set to 4 MB (0x1fc00000-0x1fffffff, exactly the ROM size) by "
               "the boot code at rom:0x20b64. Above a smaller window = bus error. Region bytes = teg2verc1.2e (even bytes) "
               "interleaved with teg2verc1.2j (odd bytes). Byte-for-byte identical to generated/regions/maincpu_rom.bin "
               "(0 mismatching words of 1,048,576). Reset PC = 0xbfc00000.",
         ev=["drv:3359-3361", "cpu:1416-1451", "cpu:2020-2048", "lua:final_checks.txt (ROM window compare)",
             "lua:dump_regions.lua (sha256 equal)"], st="V-both"),
    dict(id="BERR_MID", lo=0x20000000, hi=0x7fffffff, dev="(none) bus error", rw="RW", width="-", notes="berr_r/berr_w.",
         ev=["cpu:1790"], st="V-drv"),
    dict(id="BERR_HIGH", lo=0xc0000000, hi=0xfffdffff, dev="(none) bus error", rw="RW", width="-",
         notes="kseg2 (virtual): bus error.", ev=["cpu:1791"], st="V-drv"),
    dict(id="BIU", lo=0xfffe0130, hi=0xfffe0133, dev="BIU / cache control", rw="RW32", width="32",
         notes="Changing RAM(0x08)/DS(0x80) re-installs the scratchpad. Boot sequence 0x804, 0x800, 0x0001e988 "
               "(scratchpad + I-cache enabled). The model keeps I-cache/D-cache bookkeeping only for isolated-cache (SR.ISC) accesses.",
         ev=["cpu:295-309", "cpu:1792", "lua:access_trace_1800.txt (BIU W)"], st="V-both"),
]

# ----------------------------------------------------------------------------------------------
# H8/3002 sound CPU (:sub) address map.  24-bit address, 16-bit big-endian data bus.
# ----------------------------------------------------------------------------------------------
SUB = [
    dict(id="H8_ROM", lo=0x000000, hi=0x07ffff, dev="Sound program ROM 'sub' (512 KB, teg1.11s)", rw="R", width="16 BE",
         notes="ROM_LOAD16_WORD_SWAP; reset vectors at 0.", ev=["drv:1277", "drv:3375-3376"], st="V-both"),
    dict(id="H8_SHRAM", lo=0x080000, hi=0x08ffff, dev="Shared RAM 64 KB (share 'sharedram')", rw="RW", width="16 BE",
         notes="Main CPU sees 0x080000-0x083fff at 0x1f080000.", ev=["drv:1278", "lua:map_dump.txt"], st="V-both"),
    dict(id="H8_C352", lo=0x280000, hi=0x287fff, dev="Namco C352 PCM (25.4016 MHz, divider 288 = 88.2 kHz)", rw="RW", width="16 BE",
         notes="Wave ROM region 'c352' 16 MB (teg1_wave0/1) via :c352:bank. 118,188 writes / 45,273 reads in 1800 frames.",
         ev=["drv:1279", "drv:1214", "drv:3378-3380", "lua:access_trace_1800.txt"], st="V-both"),
    dict(id="H8_IN", lo=0x300000, hi=0x300003, dev="Player/system inputs (JAMMA, active low)", rw="R", width="8 per offset (master) / 2x16 (0.264)",
         notes="Byte 0x300000 = P2, 0x300001 = P1 (bit0 RIGHT,1 LEFT,2 DOWN,3 UP,4 B1,5 B2,6 B3,7 START), 0x300002 = "
               "bit7 P1 SERVICE, bit6 TEST/service switch, bit5 COIN1, bit4 COIN2, bits3-0 JAMMA B7/B8, 0x300003 = "
               "bits7-5 P2 B6/B5/B4, bits3-1 P1 B6/B5/B4. All read 0xff when idle (observed ffff/ffff).",
         ev=["drv:1280", "drv:1419-1458", "drv264:2060-2094", "lua:mainview.txt", "lua:access_trace_1800.txt (sub R 300000/300002)"],
         st="V-both"),
    dict(id="H8_WAIT10", lo=0x300010, hi=0x300011, dev="nop r/w (wait-state generator)", rw="RW", width="16", notes="", ev=["drv:1281"], st="V-drv"),
    dict(id="H8_WAIT30", lo=0x300030, hi=0x300031, dev="nop r/w (wait-state generator)", rw="RW", width="16",
         notes="BIOS writes here 'simply to generate a wait state'; values seen 0,0x1000..0x8400 (1948 writes).",
         ev=["drv:1282", "lua:access_trace_1800.txt (sub W 300030)"], st="V-both"),
    dict(id="H8_ONCHIP", lo=0xfffd10, hi=0xffffff, dev="H8/3002 on-chip RAM (ffff d10-ff0f) and I/O registers", rw="RW", width="8/16",
         notes="Internal; see reports/refs/h83002.cpp (map lines 31-139) and h8*.cpp. Not part of the board address map.",
         ev=["lua:map_dump.txt (:sub entries 08-103)"], st="V-both"),
]


MAIN.sort(key=lambda r: (r["lo"], -r["hi"]))
SUB.sort(key=lambda r: (r["lo"], -r["hi"]))


def row_dict(r, cpu):
    return {
        "id": r["id"], "cpu": cpu,
        "start": r["lo"], "end": r["hi"], "start_hex": h(r["lo"]) if cpu == "main" else "0x%06x" % r["lo"],
        "end_hex": h(r["hi"]) if cpu == "main" else "0x%06x" % r["hi"],
        "size": r["hi"] - r["lo"] + 1,
        "device": r["dev"], "access": r["rw"], "width_bits": r["width"], "notes": r["notes"],
        "evidence": r["ev"], "status": r["st"], "mame_versions": r.get("ver", "both"),
    }


def md_table(rows, cpu):
    out = ["| Range | Device | R/W | Width | Notes | Evidence | Status |", "|---|---|---|---|---|---|---|"]
    for r in rows:
        rng = ("%s - %s" % (h(r["lo"]), h(r["hi"]))) if cpu == "main" else ("0x%06x - 0x%06x" % (r["lo"], r["hi"]))
        ver = "" if r.get("ver", "both") == "both" else " **[%s only]**" % r["ver"]
        out.append("| `%s` | %s%s | %s | %s | %s | %s | %s |" % (
            rng, r["dev"], ver, r["rw"], r["width"], r["notes"].replace("|", "/"), "<br>".join(r["ev"]), r["st"]))
    return "\n".join(out)


# ----------------------------------------------------------------------------------------------
# JSON
# ----------------------------------------------------------------------------------------------
DOC = {
    "schema": "system12.address_map/1",
    "game": "tektagt",
    "title": "Namco System 12 - Tekken Tag Tournament (tektagt) hardware as seen by a runtime replacement",
    "author": "agent03",
    "provenance": {
        "mame_source_commit": "f8f6b22c3f328b9e0b04ff502989f8536c0d66c0 (master; driver in reports/refs/namcos12.cpp)",
        "mame_binary_used_for_lua_probes": "0.264+dfsg.1-1 (Ubuntu apt)",
        "machine_config": "coh700b + tektagt_state (master); coh700 + namcos12_boothack_state::tektagt (0.264)",
        "how_sources_were_fetched": "psx cpu/irq/dma/rcnt/sio/at28c16 sources were fetched read-only from raw.githubusercontent.com at the pinned commit and at tag mame0264; they are not stored in the repo.",
        "probe_directory": "reports/trace_a03/",
        "nvram_note": "First boot with an empty AT28C16 writes build/ID data to EEPROM bytes 0x280.. (frames ~382-390); traces that depend on EEPROM state must start from the same NVRAM file.",
    },
    "status_legend": {
        "V-drv": "verified by reading MAME source", "V-lua": "verified by a MAME 0.264 Lua probe",
        "V-both": "both", "HYP": "HYPOTHESIS - inferred only"},
    "address_space": {
        "cpu": "Sony CXD8661R (R3000A-class MIPS I, little endian, 32-bit bus) with GTE (COP2); no MMU, no FPU",
        "device_clock_hz": 100000000,
        "mame_instruction_rate_hz": 25000000,
        "mame_instruction_rate_note": "MAME converts device clocks to cycles as clocks/4 (psx.h:182-183); measured ~25 M instr/s during the boot ROM loop (152 M instruction fetches in ~6.1 s emulated). Real silicon speed is NOT verified.",
        "segments": [
            {"name": "kuseg", "virtual": [0x00000000, 0x7fffffff], "physical": "virtual", "note": "kernel code uses it directly, e.g. hardware at 0x1f80xxxx"},
            {"name": "kseg0", "virtual": [0x80000000, 0x9fffffff], "physical": "virtual & 0x1fffffff", "cached": True},
            {"name": "kseg1", "virtual": [0xa0000000, 0xbfffffff], "physical": "virtual & 0x1fffffff", "cached": False},
            {"name": "kseg2", "virtual": [0xc0000000, 0xffffffff], "physical": "none (bus error) except BIU 0xfffe0130", "cached": False}],
        "physical_mask": 0x1fffffff,
        "alias_note": "MAME 0.264 installs RAM and ROM at the 0x0/0x80000000/0xa0000000 aliases but the hardware registers and all driver devices ONLY at the physical (kuseg) address. Master also aliases the PSX hardware registers (not the driver map). The tektagt code always uses 0x1fxxxxxx for devices, so a runtime that masks with 0x1fffffff is a superset.",
        "reset_pc": 0xbfc00000,
        "reset_cop0": {"SR": 0x00400000, "PRId": 2, "note": "SR.BEV=1 at reset; exception vectors 0xbfc00180 (BEV=1) / 0x80000080 (BEV=0); breakpoint 0xbfc00140 / 0x80000040"},
        "interrupts": {"cpu_line": "IRQ0 -> Cause.IP2 (SR.IM bit 10)", "runtime_SR_value": "0x00000401 / 0x40000401 (IEc=1, IM2=1, CU2=1 when GTE used)"},
    },
    "memories": {
        "ram": {"size": 0x400000, "phys": 0x0, "aliases": ["0x80000000", "0xa0000000"], "evidence": ["drv:1124", "lua:final_checks.txt"]},
        "scratchpad": {"size": 0x400, "phys": 0x1f800000, "enable": "BIU & 0x88 == 0x88"},
        "boot_rom": {"region": "maincpu:rom", "size": 0x400000, "phys": 0x1fc00000, "load": "ROM_LOAD16_BYTE teg2verc1.2e @even + teg2verc1.2j @odd",
                      "sha256_of_region_bytes": "8f59ab62215b896ea866b2463af0f37000703295338a4b771c91a2ee9a5cdba0"},
        "banked_roms": {"region": "bankedroms", "size": 0x3800000, "banks_of_2MB": 28,
                         "layout": [
                             {"offset": 0x0000000, "size": 0x1000000, "files": "teg1_rom0e.ic9 (halfwords at +0) + teg1_rom0o.ic13 (halfwords at +2)", "load": "ROM_LOAD32_WORD"},
                             {"offset": 0x1000000, "size": 0x1000000, "files": "teg1_rom1e.ic10 + teg1_rom1o.ic14", "load": "ROM_LOAD32_WORD"},
                             {"offset": 0x2000000, "size": 0x1000000, "files": "teg1_rom2e.ic11 + teg1_rom2o.ic15", "load": "ROM_LOAD32_WORD"},
                             {"offset": 0x3000000, "size": 0x800000, "files": "flel.ic4 (+0) fleu.ic5 (+1) flol.ic6 (+2) flou.ic7 (+3)", "load": "ROM_LOAD32_BYTE"}],
                         "sha256_of_region_bytes": "e06862305a58a1f9b98c356899c298b429477f0d6d12e0cd8ddb0129194e10ef",
                         "evidence": ["drv:3363-3373", "lua:dump_regions.lua"],
                         "note": "Region bytes as MAME stores them (LE 32-bit region) are byte-identical to tools/build_regions.py output (generated/regions/*.bin) - the earlier 'LOAD32_WORD byte order unverified' item is closed for maincpu:rom and bankedroms."},
        "gpu_vram": {"size": 0x200000, "geometry": "1024x1024x16bpp", "evidence": ["drv:1168"]},
        "eeprom": {"device": "AT28C16", "size": 0x800, "file_size": 0x820, "default_byte": 0xff, "evidence": ["at28:*"]},
        "h8_shared_ram": {"size": 0x10000, "main_window": [0x1f080000, 0x1f083fff]},
    },
    "map_main_cpu": [row_dict(r, "main") for r in MAIN],
    "map_sub_cpu_h8": [row_dict(r, "sub") for r in SUB],
}

DOC["dynamic_config"] = {
    "ram_size_reg": {"addr": 0x1f801060, "reset": 0x800, "boot_values": [0xfb4, 0xcb4], "final": 0xcb4, "final_window_bytes": 0x400000},
    "rom_cfg_reg": {"addr": 0x1f801010, "reset": 0x130000, "boot_values": [0x13246f, 0x163055, 0x163088], "final": 0x163088, "final_window_bytes": 0x400000},
    "exp_base_reg": {"addr": 0x1f801000, "boot": 0x1f000000, "game": 0x1f200000},
    "exp_cfg_reg": {"addr": 0x1f801008, "game_values": [0x211730ff, 0x261730ff]},
    "biu": {"addr": 0xfffe0130, "boot_sequence": [0x804, 0x800, 0x1e988], "final": 0x1e988},
    "irq_mask_values_seen": [0x0, 0x1, 0x9, 0x29],
    "dpcr_final": 0x3bb33bbb,
    "dicr_enable_value": 0x00a00000,
    "dicr_ack_ch5": 0x20a00000,
}

DOC["rom_dma"] = {
    "channel": 5,
    "trigger": "CHCR write with value exactly 0x01000000 (also accepted for ch5 handlers: 0x01000200 block mode, (v & 0xffbffeff)==0x11000000) while DPCR bit 23 (ch5 enable) is set",
    "executes": "synchronously inside the CHCR store; no wait states; CHCR bit24/28 cleared; DICR flag bit 29 set; if DICR master+ch5 enable set -> DICR bit31 and I_STAT bit3",
    "size_words": "BCR (full 32-bit register value) when CHCR bit 9 == 0",
    "dest_address": "MADR & (ram_size-1)",
    "source_select": [
        {"if": "m_has_tektagt_dma", "offset": "m_tektagdmaoffset (PROT1 32-bit value)"},
        {"else": "m_dmaoffset (from 0x1f70xxxx writes)"}],
    "region_select": "is_mainrom = bit31(offset) || exp_base()==0x1f300000 -> maincpu:rom with offset &= 0xffffff; else bankedroms with offset &= 0x7fffffff",
    "clamps": ["words limited to remaining ROM bytes/4", "words limited to remaining RAM bytes/4 from MADR"],
    "copy": "RAM[MADR .. +4*n) = region[offset .. +4*n) byte for byte (regions are little-endian 32-bit)",
    "post": "m_has_tektagt_dma = false (always, even for main ROM reads)",
    "evidence": ["drv:1310-1365", "drv:1161", "dma:258-264", "img:0x8012e4a0-0x8012e590", "lua:final_checks.txt"],
    "observed_chunking": "Loader reads a 1-word header into 0x803fffa8 (BCR=1) and then the payload (BCR up to 0x81f words seen, max chunk 0x20000 bytes); 8089 DMA5 starts in 1800 frames.",
}

DOC["tektagt_protection"] = {
    "state": {"ttt_cnt": "int, reset 0", "ttt_val": "uint32[2], reset 0", "m_tektagdmaoffset": "uint32, reset 0", "m_has_tektagt_dma": "bool, reset false", "m_dmaoffset": "uint32, reset 0"},
    "prot1_write": {"offset0_lo16": "tektagdmaoffset = D; if cnt != 2: val[cnt] = D", "offset1_hi16": "tektagdmaoffset |= D<<16; if cnt != 2: val[cnt++] |= D<<16"},
    "prot1_read": {"offset0": 0x8000, "offset1": 0},
    "prot2_write": {"offset0": "cnt = 0", "offset1": "ignored"},
    "prot2_read_table": [
        {"val0": 0x806d2c24, "val1": 0xd5545715, "offset0": 0x36e2, "offset1": 0},
        {"val0": 0x804c2c84, "val1": 0xd5545615, "offset0": 0x2651, "offset1": 0},
        {"val0": 0x2aaba8e6, "val1": 0x00820040, "offset0": 0, "offset1": 0x4186},
        {"val0": 0x2aaba592, "val1": 0x01780544, "offset0": 0, "offset1": 0x3c7d}],
    "prot2_read_default": "if cnt==2 and ((val1>>16)&0xff)==0xa9: 0x552e for either offset; else 0. Reads with cnt != 2 return 0.",
    "prot3_read": "return 0; m_has_tektagt_dma = true",
    "observed_handshakes": [
        {"frame": 370, "pc": "0x800f8e84", "val": [0x806d2c24, 0xd5545715], "response_32bit": 0x000036e2},
        {"frame": 370, "pc": "0x800f8efc", "val": [0x2aaba8e6, 0x00820040], "response_32bit": 0x41860000}],
    "evidence": ["drv:1850-1937", "drv264:1528-1616", "lua:final_checks.txt"],
}

DOC["interrupts"] = {
    "psx_irq_bits": {"0": "VBLANK (GPU vblank start, edge)", "1": "CD-ROM (unused)", "2": "unused", "3": "DMA (DICR master flag)", "4": "root counter 0", "5": "root counter 1", "6": "root counter 2", "7": "SIO0", "8": "SIO1", "9": "SPU (not present)", "10": "PIO (not present)"},
    "used_by_game": ["bit0 VBLANK", "bit3 DMA", "bit5 root counter 1 (hblank-based raster timer)"],
    "cpu_line": "psxirq.irq -> maincpu IRQ0 -> Cause.IP2, level = ((I_STAT & I_MASK) != 0)",
    "vblank": {
        "ntsc_240_line_hz": 59.8260978565, "ntsc_480_line_hz": 59.9400523286, "pal_hz": 50.0,
        "frame_period_seconds_observed_240": 0.016715113,
        "main_cpu": "GPU vblank callback fires once per frame at vblank start -> psxirq intin0 -> I_STAT bit0",
        "sound_cpu": "screen vblank (both edges) -> H8 IRQ1 level (assert at vblank start, clear at end); also drives H8 ADC trigger and port B bit 7",
        "vblank_duration": "HYPOTHESIS: ~2500 us (psxgpu sets set_vblank_time(2500us) only if the screen was not set up; not measured)",
        "evidence": ["gpu:3450-3466", "gpu:378-455", "gpu:50", "drv:1170", "drv:1412-1417", "lua:vbl.txt"]},
    "dma_irq": "DMA completion: DICR flag bit (24+ch) latched only if enable bit (16+ch) is set; if master enable bit23 && (flags & enables) -> DICR bit31 and I_STAT bit3 pulse. Clearing DICR does NOT clear I_STAT (intin3(0) is a no-op) - software must write I_STAT.",
    "evidence": ["irq:*", "dma:92-111", "dma:344-376", "lua:final_checks.txt"],
}

DOC["h8_sound_cpu"] = {
    "device": "Hitachi H8/3002 (h83002), 16.9344 MHz, 24-bit address, big-endian 16-bit bus", "evidence": ["drv:1173"],
    "ports": {
        "P6": "read: bit1 = (jvs_sense != Initialized) -> 1 when no JVS board; | 0xfd (master: 0xff idle)",
        "P7": "read: DSW (bit7 Service Mode off=1, bit6 Freeze off=1)",
        "P8": "read: master (jvs_sense != None)<<4 | 0x02 | 0xed = 0xef idle; 0.264 returns 0x02; write stores m_sub_port8",
        "PA": "bit0 selects RTC4543 (1) vs NAMCO_SETTINGS (0) chip enable together with PB bit5",
        "PB": "bit0 -> JVS RTS, bit5 chip-enable gate, bit7 mirrors screen vblank (read), reset value 0x50",
        "ADC0-3": "ioport ADC_0..3 (unused: 0)"},
    "serial": {"SCI0": "JVS port (nothing connected for tektagt; external clock period 14.7456 MHz/8)", "SCI1": "TX -> namco_settings data, clock -> RTC4543 clk (inverted) and settings clk; RX <- RTC data"},
    "irq": {"IRQ1": "screen vblank"},
    "devices": {"C352": "25,401,600 Hz, divider 288", "RTC4543": "32.768 kHz", "NAMCO_SETTINGS": "serial settings chip (uPD6345-like)", "AT28C16": "main-CPU visible EEPROM, not H8"},
    "evidence": ["drv:1173-1216", "drv:1367-1417"],
}

DOC["h8_sound_cpu"]["inputs"] = {
    "note": "Active low. tektagt has no JVS I/O board (JVS_PORT default option = none) so m_jamma stays true and the H8 reads JAMMA-style bytes. 0.264 exposes the same data as two 16-bit ports IN0 (0x300000) and IN1 (0x300002).",
    "H8_0x300000": {"byte": "P2", "bits": {"0": "RIGHT", "1": "LEFT", "2": "DOWN", "3": "UP", "4": "BUTTON1", "5": "BUTTON2", "6": "BUTTON3", "7": "START"}},
    "H8_0x300001": {"byte": "P1", "bits": {"0": "RIGHT", "1": "LEFT", "2": "DOWN", "3": "UP", "4": "BUTTON1", "5": "BUTTON2", "6": "BUTTON3", "7": "START"}},
    "H8_0x300002": {"byte": "system", "bits": {"7": "P1 SERVICE (SERVICE1)", "6": "TEST (service switch, no toggle)", "5": "COIN1", "4": "COIN2", "3": "P1 BUTTON7 (unused)", "2": "P2 BUTTON7 (unused)", "1": "P1 BUTTON8 (unused)", "0": "P2 BUTTON8 (unused)"}},
    "H8_0x300003": {"byte": "buttons 4-6", "bits": {"7": "P2 BUTTON6", "6": "P2 BUTTON5", "5": "P2 BUTTON4", "3": "P1 BUTTON6", "2": "P1 BUTTON5", "1": "P1 BUTTON4"}},
    "idle_value": "0xff in every byte (observed 0xffff for both words)",
    "DSW_port7": {"0x80": "Service Mode (1 = off)", "0x40": "Freeze (1 = off)", "0x3f": "unused, read 1", "idle": "0xff"},
    "game_buttons": "6 buttons per player (tektagt uses P1/P2 BUTTON1-6, START, coin 1/2, service, test)",
    "evidence": ["drv:1419-1458", "drv:1992-2040 (INPUT_PORTS namcos12)", "drv264:2060-2094", "lua:mainview.txt", "lua:access_trace_1800.txt"],
}

DOC["differences_from_home_ps1_note"] = "PS1 column = public PS1 hardware documentation (nocash psx-spx) and the RecompOne runtime assumptions; it was not re-verified in this work. System 12 column is verified unless marked."

DOC["differences_from_home_ps1"] = [
    {"device": "CPU", "ps1": "R3000A 33.8688 MHz in CXD8530; BIOS at 0xbfc00000 (512 KB)", "system12": "CXD8661R, 100 MHz xtal (MAME: 1 instr/4 clocks); 4 MB boot ROM at 0xbfc00000; same MIPS I core + GTE"},
    {"device": "RAM", "ps1": "2 MB (8 MB window mirrored)", "system12": "4 MB (window selected by RAM_SIZE: 4 MB after boot; bus error above)"},
    {"device": "ROM", "ps1": "512 KB BIOS only", "system12": "4 MB program ROM + 28 x 2 MB banked ROMs (56 MB) reachable only through DMA5/bank window"},
    {"device": "Expansion 1 (0x1f000000)", "ps1": "unpopulated/parallel port", "system12": "bank register, strobes, shared RAM, EEPROM, DMA-offset and protection registers"},
    {"device": "Expansion 3 (0x1fa00000)", "ps1": "unpopulated", "system12": "2 MB banked ROM window with protection overlay"},
    {"device": "GPU", "ps1": "CXD8514Q, 1 MB VRAM (1024x512)", "system12": "CXD8654Q, 2 MB SGRAM VRAM (1024x1024); MAME treats it as 'type 2' like the retail GPU"},
    {"device": "SPU / CD-ROM / XA", "ps1": "SPU 512 KB RAM + CD-ROM drive", "system12": "absent: registers read 0 / ignore writes; audio is C352 on the H8 board"},
    {"device": "SIO0/SIO1", "ps1": "pads, memory cards, link", "system12": "present in CPU but unconnected and unused; inputs arrive via H8 (JAMMA/JVS)"},
    {"device": "DMA", "ps1": "ch5 PIO unused; CD/SPU DMA", "system12": "ch5 = ROM-to-RAM copy; ch3/ch4 have no device; ch0/1 MDEC and ch2 GPU as PS1"},
    {"device": "Interrupts", "ps1": "I_STAT write: stat &= data", "system12": "MAME model: stat = (stat & ~mem_mask) | (stat & mask & data) on write (also drops pending bits that are masked off)"},
    {"device": "Root counters", "ps1": "3 counters, dot-clock/hblank/sysclk", "system12": "same model; tektagt uses counter 1 only"},
    {"device": "Watchdog / strobes", "ps1": "none", "system12": "write-only strobes at 0x1f008000/0x1f010000/0x1f018000"},
    {"device": "Sound", "ps1": "SPU", "system12": "Hitachi H8/3002 + Namco C352, shared RAM mailbox at 0x1f080000"},
    {"device": "Video timing", "ps1": "NTSC 59.94/PAL 50", "system12": "MAME GPU model: 59.826 Hz for 240-line NTSC progressive (gpu:378-455)"},
    {"device": "Region / security", "ps1": "CD region lock", "system12": "KEYCUS CPLD + FPGA on ROM board; MAME emulates only the observed challenge/response (PROT1/PROT2)"},
]

DOC["hypotheses"] = [
    {"id": "H-STROBE18", "text": "0x1f018000 is a watchdog clear. Evidence only shows a write-only halfword-0 strobe poked ~700 times per frame from polling loops."},
    {"id": "H-STROBE8", "text": "0x1f008000 is a per-DMA-chunk strobe/chip-select; observed once per DMA5 chunk."},
    {"id": "H-PORT1BFF", "text": "0x1f1bff08-0f is a data/handshake port to the ROM-board KEYCUS/CPLD; boot ROM streams ~1M halfwords into +0x0e."},
    {"id": "H-MIRROR", "text": "Master's mirror(0x800000) of PROT3 (0x1ff00000) has no effect because the ROM window shadows it."},
    {"id": "H-VBLK", "text": "VBLANK duration of ~2.5 ms (only used for the H8 level IRQ and screen vblank flag)."},
    {"id": "H-CLOCK", "text": "Real hardware CPU speed; only MAME's instruction-rate model (clock/4) is verified."},
    {"id": "H-UNMAPPED", "text": "Reads of unmapped 0x1f000004-0x1f7fffff addresses return 0 and writes are ignored (MAME default); real bus behaviour unknown."},
    {"id": "H-PROT-COMPLETE", "text": "Only the five challenge/response pairs coded in MAME (4 table pairs + 0xa9 rule) are known; the game may be able to issue others (it only issued 806d2c24/d5545715, 2aaba8e6/00820040 in 1800 frames)."},
]

if __name__ == "__main__":
    with open(os.path.join(OUT, "address_map.json"), "w") as f:
        json.dump(DOC, f, indent=1)
        f.write("\n")
    # Regenerate the marked tables inside ADDRESS_MAP.md (prose is hand-written)
    md_path = os.path.join(OUT, "ADDRESS_MAP.md")
    if os.path.exists(md_path):
        import re
        md = open(md_path).read()
        for tag, rows, cpu in (("MAIN_TABLE", MAIN, "main"), ("SUB_TABLE", SUB, "sub")):
            pat = re.compile(r"(<!-- BEGIN %s -->)\n.*?(<!-- END %s -->)" % (tag, tag), re.S)
            md = pat.sub(lambda m: m.group(1) + "\n" + md_table(rows, cpu) + "\n" + m.group(2), md)
        open(md_path, "w").write(md)
    if len(sys.argv) > 1 and sys.argv[1] == "--tables":
        print("### Main CPU\n")
        print(md_table(MAIN, "main"))
        print("\n### H8/3002\n")
        print(md_table(SUB, "sub"))
    else:
        print("wrote address_map.json with %d main rows and %d sub rows" % (len(MAIN), len(SUB)))
