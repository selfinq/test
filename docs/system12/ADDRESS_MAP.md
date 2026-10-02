# Namco System 12 `tektagt` - main CPU address map (spec for a runtime replacement)

Author: agent03. Scope: exactly what the **Tekken Tag Tournament (World, TEG2/VER.C1, set 1)** PCB looks like to its
R3000A-class main CPU (`CXD8661R`) and, in a short appendix, to the H8/3002 sound CPU.
Machine-readable twin: [`address_map.json`](address_map.json). Device behaviour and PS1 differences: [`DEVICES.md`](DEVICES.md).
The two tables below are generated from `gen_address_map.py` (single source of truth); everything else is hand-written.

## 0. Evidence and status conventions

Nothing here is taken from memory: every row cites the MAME source line it comes from and/or a Lua probe that was run
against the MAME 0.264 binary (`/usr/games/mame`, `tektagt`) with the user-supplied ROM set (verified, `reports/romset_verify.json`).

| Tag | Meaning |
|---|---|
| `drv:N` | `reports/refs/namcos12.cpp`, line N (MAME master, commit `f8f6b22`) |
| `drv264:N` | MAME **0.264** `src/mame/namco/namcos12.cpp` (tag `mame0264`) line N - the version that produced all Lua evidence |
| `cpu:N` / `cpu264:N` | `src/devices/cpu/psx/psx.cpp` (the PSX CPU + on-die hardware map) at `f8f6b22` / `mame0264` |
| `irq:` `dma:` `rcnt:` `sio:` | same directory, files `irq.cpp`, `dma.cpp`, `rcnt.cpp`, `sio.cpp` @ `f8f6b22` |
| `gpu:N` | `src/devices/video/psx.cpp` @ `f8f6b22` (= `reports/refs/psx.cpp`; the file is the GPU, not the CPU) |
| `at28:N` | `src/devices/machine/at28c16.cpp` @ `f8f6b22` |
| `lua:F` | output file `F` in `reports/trace_a03/` produced by the Lua script of the same stem (MAME 0.264) |
| `img:VA` | static disassembly of the decompressed game image (`generated/ttt_image.bin`, loaded at `0x80010000`) at virtual address VA |
| `rom:OFF` | static disassembly of the boot ROM (`generated/regions/maincpu_rom.bin`), file offset OFF (= physical `0x1fc00000 + OFF`) |

Status column: **V-drv** verified in source, **V-lua** verified by a probe, **V-both** both, **HYP** = HYPOTHESIS (inferred, not proven).
The CPU/IRQ/DMA/timer sources were fetched read-only from `raw.githubusercontent.com` at the two pinned revisions and are *not*
stored in the repository; the commit ids in the table above reproduce them exactly.

**Two MAME versions.** The reference binary is 0.264, the pinned driver source is master `f8f6b22`. They differ for tektagt
in a handful of places (section 8); none changes behaviour that the game depends on, but where they differ both are stated.

## 1. Quick facts

| Item | Value | Evidence |
|---|---|---|
| Board | CPU PCB COH-700 (`coh700b` config) + SYSTEM12 MOTHER(B) + ROM PCB M8F4 (`8661961901`) with KEYCUS KC044 (MACH211 CPLD) and an Altera EPM7128 (IC8, "S12M841") | drv:74-75, 614-660, 1132, 3713 |
| Main CPU | Sony `CXD8661R`, R3000A-class (MIPS I, little endian) + GTE (COP2); no MMU/FPU; device clock 100,000,000 Hz | drv:1122; `reports/mame_ref/listxml.txt:223` |
| CPU speed in MAME | 1 instruction = 4 device clocks (`clocks/4` cycles) = ~25 M instr/s; measured 152 M ROM fetches in 6.1 s of boot | cpu:182-183, lua:access_trace_1800.txt |
| Sound CPU | Hitachi H8/3002 16.9344 MHz; sound chip Namco C352 25.4016 MHz (divider 288) | drv:1173, 1214 |
| Main RAM | **4 MB**, 32-bit (2x KM416V1204) | drv:1124 |
| GPU | `CXD8654Q`, **2 MB** SGRAM VRAM, 16-bit (1024x1024x16), pixel clock 53.693175 MHz | drv:1127, 1166-1168 |
| Boot/program ROM | `maincpu:rom`, 4 MB at physical `0x1fc00000` | drv:3359-3361 |
| Banked ROM | `bankedroms`, 56 MB = 28 banks x 2 MB (8 mask ROMs + 4 flash) | drv:3363-3373 |
| EEPROM | AT28C16 (2 KB) at `0x1f140000` | drv:1209, 1269 |
| Reset PC | `0xbfc00000`; `SR.BEV=1`, `PRId=2` | cpu:2037-2048, lua:final_checks.txt |

## 2. Address translation seen by the CPU

The R3000A-class core in MAME has **no translation**: kuseg `0x00000000-0x7fffffff` is used as-is, kseg0 `0x80000000-0x9fffffff`
and kseg1 `0xa0000000-0xbfffffff` alias the low 512 MB. A runtime may simply compute `phys = vaddr & 0x1fffffff`
(kseg2 `>= 0xc0000000` is a bus error except `0xfffe0130`).

* RAM and the boot ROM are installed by MAME at all three aliases (`0x0`, `0x80000000`, `0xa0000000`; `0x1fc00000`, `0x9fc00000`, `0xbfc00000`): cpu:1403-1405, 1438-1440 (0.264: 1391-1393, 1426-1428).
* Hardware registers and every driver device are installed at the **physical** address only in MAME 0.264 (master additionally aliases the PSX register block to kseg0/kseg1: cpu:1760). The game always addresses devices as `0x1fxxxxxx` (taps on physical addresses saw all accesses), so masking with `0x1fffffff` is a safe superset.
* Instruction fetch from device space is never done (boot ROM executes from kseg1 `0xbfc00000` then kuseg-style `0x1fc2xxxx`; the game runs from RAM at `0x80010000`).

## 3. Main CPU physical address map

Overlapping entries: MAME resolves read and write direction separately and the **later** entry wins; device-internal (PSX) entries
are applied first and the driver map second (`lua:map_dump.txt` lists all 38 entries in order). Concretely
`0x1f700000-3` has write handler `DMAOFFSET` (entry 8) *and* read handler `PROT3` (entry 12), and `PROT1`/`PROT2`
(entries 10/11) punch holes in the bank window for both reads and writes.

<!-- BEGIN MAIN_TABLE -->
| Range | Device | R/W | Width | Notes | Evidence | Status |
|---|---|---|---|---|---|---|
| `0x00000000 - 0x003fffff` | System RAM 4 MB (ram_device, 32-bit) | RW | 8/16/32 | Also visible at kseg0 0x80000000 and kseg1 0xa0000000. Visible window is set by RAM_SIZE (0x1f801060): reset value 0x800 = 2 MB window; boot writes 0xfb4 (16 MB window, 4 MB RAM repeated) then 0xcb4 (4 MB window), and 0xcb4 stays for the whole run. Contents power up as 0 (set_default_value(0)). | drv:1124<br>cpu:1353-1414<br>cpu:2017<br>lua:access_trace_1800.txt (W 1f801060 = 00000fb4, 00000cb4)<br>lua:final_checks.txt (RAM alias test) | V-both |
| `0x00010000 - 0x0001000b` | RAM bytes pre-loaded at machine_reset | R (boot data) | 8 | coh700b sets m_boot_hack: at every machine_reset 12 bytes are copied from ROM offset 0x20280 to RAM 0x10000. The bytes are ASCII 'Namco Ltd.--' (4e616d636f204c74642e2d2d). The boot code checks them early; how they get there on real hardware is unknown (driver comment). | drv:1132-1139<br>drv:1250-1258<br>lua:final_checks.txt (frame1)<br>rom:0x20280 | V-both |
| `0x00400000 - 0x1effffff` | (none) bus error | RW | - | Once RAM_SIZE window = 4 MB everything between the end of the window and 0x1effffff is installed as psxcpu berr_r/berr_w (sets the CPU bus-error flag -> DBE exception, returns 0). Reads beyond 4 MB return 0 (not a mirror). | cpu:1411-1413<br>lua:final_checks.txt (RAM beyond 4MB) | V-both |
| `0x1f000000 - 0x1f000003` | Bank register (mainbank select) | W16 (low half) / R=nop | 16 | Read = nop (0). 16-bit write to 0x1f000000-1 -> bankoffset_w -> mainbank.set_entry(data) selecting a 2 MB bank of 'bankedroms' (entries 0..27 for tektagt). tektagt (base namcos12_state, not altbank) uses data directly. OBSERVED: tektagt NEVER writes it (0 writes in 1500 frames) and only dummy-reads the bank window. | drv:1263-1264<br>drv:1285-1289<br>drv:1232-1233<br>lua:final_checks.txt (bank register write count = 0) | V-both |
| `0x1f008000 - 0x1f008003` | write-only strobe (nop) **[master only]** | W | 16 | MASTER ONLY (absent from the 0.264 map, where the write is unmapped and ignored). The game stores halfword 0 here from three code sites, one of them after every ROM-DMA chunk (img:0x8012e550; others img:0x800f9214, 0x8012e77c): 8832 writes in 1800 frames vs 8089 DMA5 starts. Purpose unknown. | drv:1265<br>img:0x8012e550<br>lua:final_checks.txt<br>lua:access_trace_1800.txt (W 1f008000 n=8832) | V-both |
| `0x1f010000 - 0x1f010003` | write-only strobe (nop) | W | 16 | Driver comment (0.264): 'very spammy'. tektagt: 0 writes observed in 1500 frames. | drv:1266<br>drv264:1438<br>lua:final_checks.txt | V-both |
| `0x1f018000 - 0x1f018003` | write-only strobe (nop) | W | 16 | HYPOTHESIS: watchdog-clear style kick. Facts: halfword 0 written from ~55 code sites (img:0x8017bbf0 ...) and from the boot ROM (rom:0x20f64); 1,105,505 writes in 1500 frames (up to 83,364 in one frame), i.e. it is poked inside polling/delay loops, not once per frame. MAME ignores it. | drv:1267<br>img:0x8017bbf0<br>rom:0x20f64<br>lua:final_checks.txt<br>lua:access_trace_1800.txt (W 1f018000) | V-both (existence/behaviour) + HYP (purpose) |
| `0x1f080000 - 0x1f083fff` | H8/3002 shared RAM (16 KB window of 64 KB) | RW | 8/16/32 (device is 16-bit) | Byte-for-byte window onto H8 address 0x080000-0x083fff (main 0x1f080000+x <-> H8 0x080000+x). Each 16-bit unit is one array element: the 16-bit VALUE seen by the little-endian main CPU at even x equals the 16-bit value the big-endian H8 reads at x (so bytes are swapped between the two views). 32-bit accesses are split into two 16-bit handler calls. H8 sees 64 KB (0x080000-0x08ffff); main CPU cannot reach 0x084000+ (reads 0). Game uses 0x1f080100-0x1f08217c and 0x1f083000-0x1f083a00 (sound command/status mailbox). | drv:1268<br>drv:1278<br>drv:1291-1301<br>lua:mainview.txt<br>lua:access_trace_1800.txt | V-both |
| `0x1f140000 - 0x1f140fff` | AT28C16 2Kx8 EEPROM (NVRAM) | RW | 8 (lanes 0 and 2) | umask32(0x00ff00ff): byte lanes 0 and 2 of every 32-bit word carry data; EEPROM byte index i is at physical address 0x1f140000 + 2*i (index 0..0x7ff). Odd byte addresses read 0. Game accesses with lhu/sh and ignores the unused lane. Verified with real data: index 0x280 (build-info string) is read at 0x1f140500. Write: ignored while busy; only changes the cell if the byte differs; 200 us busy; while busy a read returns (last_written ^ 0x80). Backing file 'at28c16' is 0x800+0x20 = 2080 bytes, default 0xff. | drv:1269<br>at28:147-203<br>lua:mainview.txt<br>lua:eeprom_trace.txt<br>lua:access_trace_1800.txt<br>img:0x8017d488 | V-both |
| `0x1f1bff08 - 0x1f1bff0f` | write-only port (nop) - ROM-board/KEYCUS data port? | W | 16 | HYPOTHESIS for purpose. Facts: boot ROM loop at rom:0x20b80 reads every 4th byte-pair of ROM[0x200 ..] (0xf7f80 = 1,015,680 halfwords) and stores each to 0x1f1bff0e, then writes 3 to 0x1f1bff0c and 1 to 0x1f1bff0a (rom:0x20f20-0x20f30) before jumping to the entry word. Game code writes 3/1 to the same addresses again (img:0x8017d1b4). MAME ignores it. | drv:1270<br>rom:0x20b68-0x20b94<br>rom:0x20f14-0x20f30<br>img:0x8017d1b4<br>lua:access_trace_1800.txt (W 1f1bff0c n=1015681, W 1f1bff08 n=1) | V-both (behaviour) + HYP (purpose) |
| `0x1f700000 - 0x1f70ffff` | ROM DMA offset register (m_dmaoffset) | W16 | 16 | 16-bit write at byte offset X (0..0xfffe) of the window with data D sets m_dmaoffset = X / (D << 16). Bit 31 (D bit 15) set selects the MAIN program ROM ('maincpu:rom', offset & 0xffffff) for the next DMA5, otherwise the 'bankedroms' region. The game uses it for main-ROM reads (img:0x8012e758: sh 0x84xx -> offset 0x84xx0000/X) and writes 0 at 0x1f700000 before each banked read. Reads: see PROT3. | drv:1271<br>drv:1303-1308<br>drv:1310-1334<br>lua:final_checks.txt (protection/dmaoffset sequence)<br>lua:access_trace_1800.txt (W 1f70xxxx) | V-both |
| `0x1f700000 - 0x1f700003` | tektagt_protection_3_r (DMA 'arm' read) | R16 (W goes to DMAOFFSET) | 16 | Any read returns 0 and sets m_has_tektagt_dma = true, which makes the next DMA5 use m_tektagdmaoffset (register PROT1) instead of m_dmaoffset; the flag is cleared when that DMA runs and on machine_reset. Master adds mirror(0x800000) (=0x1ff00000..3), which lies inside the ROM window and is shadowed by it (HYPOTHESIS: no effect); 0.264 has no mirror. Game: lhu at img:0x8012e518, 7978 times. | drv:1845<br>drv:1932-1937<br>drv264:1466<br>drv264:1610-1616<br>img:0x8012e518<br>lua:final_checks.txt<br>lua:access_trace_1800.txt (R 1f700000 n=7978) | V-both |
| `0x1f800000 - 0x1f8003ff` | Scratchpad (D-cache used as 1 KB RAM) | RW | 8/16/32 | Becomes RAM only when BIU (0xfffe0130) has RAM(0x08)=1 and DS(0x80)=1 (game/boot write 0x0001e988). Otherwise reads/writes bus-error (RAM bit off) or reads bus-error/writes nop (DS off). Physical only for the static map: kseg0/kseg1 aliases are NOT mapped for the scratchpad (access it as 0x1f800000). | cpu:1336-1350<br>cpu:1758<br>lua:access_trace_1800.txt (BIU W 0001e988) | V-both |
| `0x1f800400 - 0x1f800fff` | (none) bus error | RW | - | berr_r/berr_w. | cpu:1759 | V-drv |
| `0x1f801000 - 0x1f801003` | EXP1_BASE (exp_base) | RW32 | 32 | Stores 0x1f000000 / (v & 0xffffff). DOES NOT relocate the driver map. Only effect: DMA5 treats the main ROM as source if exp_base()==0x1f300000 (not used by tektagt). Observed 0x1f000000 (boot) / 0x1f200000 (game). | cpu:220-235<br>cpu:1763<br>drv:1316<br>lua:access_trace_1800.txt | V-both |
| `0x1f801004 - 0x1f801007` | EXP2_BASE (plain RAM) | RW32 | 32 | Backing store only (boot writes 0x1f802000). | cpu:1764 | V-both |
| `0x1f801008 - 0x1f80100b` | EXP1_DELAY/SIZE (exp_config) | RW32 | 32 | Stored with v &= 0xaf1fffff; no timing effect in MAME. Observed 00133777, 00173033, 001730ff, 211730ff, 261730ff. | cpu:237-247<br>cpu:1765<br>lua:access_trace_1800.txt | V-both |
| `0x1f80100c - 0x1f80100f` | EXP3_DELAY (plain RAM) | RW32 | 32 | Backing store only. | cpu:1766 | V-both |
| `0x1f801010 - 0x1f801013` | BIOS_ROM delay/size (rom_config) | RW32 | 32 | Reset 0x00130000. Bits 16-20 = log2(ROM window size), capped at 4 MB; when they change the ROM window (0x1fc00000, and 0x9fc00000/0xbfc00000) is re-installed. Boot writes 0x0013246f (512 KB), 0x00163055 (4 MB), 0x00163088 (4 MB). | cpu:266-281<br>cpu:1416-1451<br>cpu:2020<br>lua:access_trace_1800.txt | V-both |
| `0x1f801014 - 0x1f80101f` | SPU/CD/EXP2 delay regs (plain RAM) | RW | 32 | Backing store only. | cpu:1767 | V-drv |
| `0x1f801020 - 0x1f801023` | COM_DELAY | RW32 | 32 | Stored, no effect. Observed 0x00031125. | cpu:283-293<br>cpu:1771 | V-both |
| `0x1f801024 - 0x1f80102f` | plain RAM | RW | 32 |  | cpu:1772 | V-drv |
| `0x1f801040 - 0x1f80104f` | SIO0 (psxsio0) | RW32 | 32 | Real device model, but nothing is connected (no pad/memcard) and tektagt never touches it (0 accesses). | cpu:1773<br>sio:*<br>lua:access_trace_1800.txt (no 1f8010[45]x lines) | V-both |
| `0x1f801050 - 0x1f80105f` | SIO1 (psxsio1) | RW32 | 32 | Same as SIO0, unconnected, unused. | cpu:1774 | V-both |
| `0x1f801060 - 0x1f801063` | RAM_SIZE (ram_config) | RW32 | 32 | Reset 0x800. When bits 8-15 change the RAM windows are rebuilt: ((v>>8)&0xf) 0x3/0x2/0x5/0x6/0xc -> 4 MB window, 0x0/0x1 -> 1 MB, 0x4/0x8/0x9 -> 2 MB, 0x7/0xa/0xb/0xd/0xe -> 8 MB, 0xf -> 16 MB; RAM (4 MB) is repeated across the window and everything above the window up to 0x1effffff bus-errors. Boot: 0xfb4 then 0xcb4. | cpu:249-264<br>cpu:1353-1414<br>cpu:2017<br>lua:access_trace_1800.txt | V-both |
| `0x1f801064 - 0x1f80106f` | plain RAM | RW | 32 |  | cpu:1776 | V-drv |
| `0x1f801070 - 0x1f801077` | Interrupt controller I_STAT (+0) / I_MASK (+4) | RW32 | 32 | Write I_STAT: new = (stat & ~mem_mask) / (stat & mask & data) - NOTE MAME clears pending bits that are not in I_MASK on any write (real PS1: stat &= data). Write I_MASK: new = (mask & ~mem_mask) / data (bits outside 0x7fd, i.e. bit 1 and bits >10 flagged unknown but kept). Output: (stat & mask) != 0 asserts CPU IRQ0 = COP0 Cause.IP2 (level, re-evaluated on every stat/mask change). Sources: bit0 VBLANK(GPU), bit3 DMA, bit4/5/6 root counters 0/1/2, bit7 SIO0, bit8 SIO1. Observed mask values 0x00, 0x01, 0x09, 0x29. | irq:17<br>irq:53-65<br>irq:67-90<br>irq:108-190<br>cpu:1777<br>cpu:2296-2300<br>cpu:3464<br>lua:final_checks.txt (IRQ writes/reads) | V-both |
| `0x1f801080 - 0x1f8010ff` | DMA controller (psxdma): ch0-6 MADR/BCR/CHCR, DPCR @0x1f8010f0, DICR @0x1f8010f4 | RW32 | 32 | Channel n at 0x1f801080 + 0x10*n: +0 MADR, +4 BCR, +8 CHCR. System 12 assignments: ch0 MDEC-in, ch1 MDEC-out, ch2 GPU, ch3 CD (no handler), ch4 SPU (no handler), ch5 = ROM read (namcos12_rom_read), ch6 OTC. See DEVICES.md for the exact CHCR patterns MAME executes and their completion timing. | cpu:1778<br>cpu:3466-3471<br>drv:1161<br>dma:214-376<br>dma:378-418 | V-both |
| `0x1f801100 - 0x1f80112f` | Root counters 0-2 (psxrcnt): +0 count, +4 mode, +8 target | RW32 | 32 | tektagt uses only counter 1 (hblank source) as a raster timer; counters 0 and 2 are zeroed at boot and then free-run (mode 0x1000). | cpu:1779<br>rcnt:*<br>lua:access_trace_1800.txt (W 1f801114/18, snapshots) | V-both |
| `0x1f801800 - 0x1f801803` | CD-ROM controller (not present) | RW8 | 8 | psxcpu cd_r/cd_w forward to unconnected handlers (read 0, write ignored). No CD drive on System 12 (except Truck Kyosokyoku). tektagt: 0 accesses. | cpu:1780<br>cpu:3435-3443 | V-both |
| `0x1f801810 - 0x1f801817` | GPU CXD8654Q: +0 GP0/GPUREAD, +4 GP1/GPUSTAT | RW32 | 32 | Forwarded to psxgpu_device::read/write. 2 MB VRAM (1024x1024x16). See DEVICES.md. | cpu:1781<br>gpu:44-52<br>gpu:3228-3300<br>gpu:3424-3446<br>drv:1127 | V-both |
| `0x1f801820 - 0x1f801827` | MDEC (psxmdec): +0 data/command, +4 status/control | RW32 | 32 | Used by tektagt (581 command/DMA rounds from frame ~641: ch0 CHCR 0x01000201 / ch1 CHCR 0x01000200). | cpu:1782<br>cpu:3469-3471<br>lua:access_trace_1800.txt (W 1f801820 n=581) | V-both |
| `0x1f801c00 - 0x1f801fff` | SPU (not present) | RW16 | 16 | Master maps 0x1f801c00-0x1f801fff (0.264: -0x1f801dff) to unconnected spu_r/spu_w (read 0, write ignored). Only the boot kernel touches it (writes 0xffff to 0x1f801c00, 0 to 0x1f801d80/84). Sound is the C352 on the H8 board. | cpu:1783<br>cpu:3425-3433<br>lua:access_trace_1800.txt (W 1f801c00, 1f801d80, 1f801d84 only at boot) | V-both |
| `0x1f802020 - 0x1f802033` | plain RAM (20 bytes, unknown) | RW | 8/16/32 | Boot kernel writes 0x0b/0x0a/0x20/0x30/0x40/0x50 bytes at 0x1f802020 (PS1 'DUART' debug port). | cpu:1784<br>lua:access_trace_1800.txt | V-both |
| `0x1f802040 - 0x1f802043` | POST/dip port (write nop) | W (R unmapped) | 8 | Boot kernel writes progress codes 1..7 to byte 0x1f802041 (13 writes). | cpu:1787<br>lua:access_trace_1800.txt | V-both |
| `0x1fa00000 - 0x1fbfffff` | mainbank: 2 MB banked ROM window (bankedroms) | R | 8/16/32 | Read-only view of bankedroms[bank*0x200000 .. +0x1fffff] (bank chosen by BANKREG, default 0). Writes unmapped. tektagt only performs dummy lhu at 0x1fa00000 (img:0x8012e548; 7673 reads) - game data does NOT come through this window but through DMA5 (see PROT1 + DMAOFFSET). Windows PROT1/PROT2 overlay 0x1fb00000-3 and 0x1fb80000-3 (reads AND writes). | drv:1272<br>drv:1232-1233<br>img:0x8012e548<br>lua:final_checks.txt (bank window reads) | V-both |
| `0x1fb00000 - 0x1fb00003` | tektagt protection reg 1 / second DMA offset | RW16 (x2) | 16 | Write offset0 (low half): m_tektagdmaoffset = D; if ttt_cnt != 2: ttt_val[ttt_cnt] = D. Write offset1 (high half): m_tektagdmaoffset /= D<<16; if ttt_cnt != 2: ttt_val[ttt_cnt++] /= D<<16. A 32-bit store does both. Read offset0 -> 0x8000 (busy flag clear), offset1 -> 0; a 32-bit load returns 0x00008000. m_tektagdmaoffset is the BYTE offset into bankedroms used by DMA5 when armed via PROT3 (game aligns it with & ~3). | drv:1846<br>drv:1850-1883<br>drv264:1464<br>drv264:1528-1561<br>img:0x8012e508<br>img:0x800f8e9c<br>lua:final_checks.txt (protection/dmaoffset sequence) | V-both |
| `0x1fb80000 - 0x1fb80003` | tektagt protection reg 2 (challenge/response) | RW16 (x2) | 16 | Write offset0 (any value, game writes 0): ttt_cnt = 0 (restart challenge). Write offset1: ignored. Read: if ttt_cnt == 2 and (ttt_val[0],ttt_val[1]) == (806d2c24,d5545715): offset0 -> 0x36e2; == (804c2c84,d5545615): offset0 -> 0x2651; == (2aaba8e6,00820040): offset1 -> 0x4186; == (2aaba592,01780544): offset1 -> 0x3c7d; else if ((ttt_val[1]>>16)&0xff)==0xa9: 0x552e (either offset). Everything else returns 0. Observed responses: 36e2, 2651, 41860000 (32-bit read), 0. | drv:1847<br>drv:1885-1930<br>drv264:1465<br>drv264:1563-1608<br>img:0x800f8e84<br>lua:final_checks.txt | V-both |
| `0x1fc00000 - 0x1fffffff` | Main program ROM 'maincpu:rom' (4 MB, ROM_REGION32_LE) | R | 8/16/32 | Installed by psxcpu update_rom_config at 0x1fc00000, 0x9fc00000, 0xbfc00000. Window = 1<<((ROM_CFG>>16)&0x1f), capped 4 MB: reset 512 KB (ROM_CFG=0x00130000), set to 4 MB (0x1fc00000-0x1fffffff, exactly the ROM size) by the boot code at rom:0x20b64. Above a smaller window = bus error. Region bytes = teg2verc1.2e (even bytes) interleaved with teg2verc1.2j (odd bytes). Byte-for-byte identical to generated/regions/maincpu_rom.bin (0 mismatching words of 1,048,576). Reset PC = 0xbfc00000. | drv:3359-3361<br>cpu:1416-1451<br>cpu:2020-2048<br>lua:final_checks.txt (ROM window compare)<br>lua:dump_regions.lua (sha256 equal) | V-both |
| `0x20000000 - 0x7fffffff` | (none) bus error | RW | - | berr_r/berr_w. | cpu:1790 | V-drv |
| `0xc0000000 - 0xfffdffff` | (none) bus error | RW | - | kseg2 (virtual): bus error. | cpu:1791 | V-drv |
| `0xfffe0130 - 0xfffe0133` | BIU / cache control | RW32 | 32 | Changing RAM(0x08)/DS(0x80) re-installs the scratchpad. Boot sequence 0x804, 0x800, 0x0001e988 (scratchpad + I-cache enabled). The model keeps I-cache/D-cache bookkeeping only for isolated-cache (SR.ISC) accesses. | cpu:295-309<br>cpu:1792<br>lua:access_trace_1800.txt (BIU W) | V-both |
<!-- END MAIN_TABLE -->

Gaps inside `0x1f000000-0x1f7fffff` that are not listed (e.g. `0x1f000004-0x1f007fff`, `0x1f100000-0x1f13ffff`) are unmapped: MAME returns 0 and ignores writes
(logged only), **no bus error** (HYPOTHESIS that real hardware behaves the same: H-UNMAPPED). The game does not touch them
(the aggregate trace `lua:access_trace_1800.txt` lists every address it did touch).

## 4. Dynamic configuration (what changes the map while the game boots)

Observed register writes (all from `lua:access_trace_1800.txt` chronological events; frame 0 = reset):

| Order | PC | Register | Value | Effect in MAME |
|---|---|---|---|---|
| 1 | `bfc0000c` | ROM_CFG `0x1f801010` | `0x0013246f` | ROM window 512 KB (same as reset value size) |
| 2 | `bfc001a4` | EXP1_BASE `0x1f801000` / `0x1f801004` / EXP1_CFG `0x1f801008` / `0x1f80100c` / `0x1f801014-1c` / COM `0x1f801020` | `1f000000` / `1f802000` / `00133777` / `00003022` / `200931e1,00020843,00070777` / `00031125` | stored only |
| 3 | `bfc00234`.. | BIU `0xfffe0130` | `0x804`, `0x800`, `0x0001e988` | cache init, scratchpad (RAM+DS bits) and I-cache enabled |
| 4 | `bfc003b0` | RAM_SIZE `0x1f801060` | `0x00000fb4` | RAM window 16 MB (4 MB RAM repeated 4x) while kernel RAM is cleared |
| 5 | `bfc0228c` | RAM_SIZE | `0x00000cb4` | **RAM window 4 MB - final** |
| 6 | `1fc20b64` | ROM_CFG | `0x00163055` | **ROM window 4 MB** (maps all of `0x1fc00000-0x1fffffff`) |
| 7 | `1fc20ba8..bd0` (frame 15) | EXP1_CFG / `0x1f80100c` / ROM_CFG | `001730ff` / `00153688` / `00163088` | stored; ROM window stays 4 MB |
| 8 | game, frame 373+ | EXP1_BASE / EXP1_CFG / `0x1f80100c` | `1f200000` / `211730ff`,`261730ff` / `001536ff` | stored only; does **not** relocate the driver map |

Consequences for a replacement:

* Treat the final configuration as fixed: RAM `0x00000000-0x003fffff` (+ kseg0/1 aliases), ROM `0x1fc00000-0x1fffffff`. The early states (2 MB window at reset, 512 KB ROM window, 16 MB RAM window) are only visible to the first few hundred boot-ROM instructions.
* `EXP1_BASE` has exactly one functional use: `exp_base()==0x1f300000` forces the main-ROM source for DMA5 (drv:1316). tektagt never sets it (it uses `0x1f000000` then `0x1f200000`), so the DMA source is chosen by the offset registers alone.
* The delay/size registers are write-through storage in MAME (cpu:242-247 masks `exp_config` with `0xaf1fffff`); no wait states are modelled.

## 5. How the ROM regions are mapped

### 5.1 `maincpu:rom` at `0x1fc00000`

* Region definition: `ROM_REGION32_LE 0x400000 "maincpu:rom"`, `ROM_LOAD16_BYTE teg2verc1.2e @0` (even bytes) + `teg2verc1.2j @1` (odd bytes), 2 MB each (drv:3359-3361). Hence `rom[2i] = 2e[i]`, `rom[2i+1] = 2j[i]`; as a little-endian word stream the CPU reads exactly these bytes.
* Mapping: `psxcpu_device::update_rom_config()` installs `m_rom` (the device's child region `"rom"` = `maincpu:rom`) at `0x1fc00000`, `0x9fc00000`, `0xbfc00000` for `window = 1 << ((ROM_CFG >> 16) & 0x1f)` bytes capped at 4 MB, repeating the ROM if the window is larger than the ROM; everything between the window end and `0x1fffffff` bus-errors (cpu:1416-1451). Reset value `0x00130000` -> 512 KB window; the boot ROM itself raises it to 4 MB at `rom:0x20b64`. Because the ROM is exactly 4 MB there is no mirroring.
* **Verified**: CPU view of `0x1fc00000..0x1fffffff` equals the region word for word (1,048,576 words, 0 mismatches), and `0x9fc00000`/`0xbfc00000` aliases agree (`lua:final_checks.txt`). The region equals `generated/regions/maincpu_rom.bin` byte for byte (sha256 `8f59ab62...cdba0`, `lua:dump_regions.lua`). This closes the "byte order inside LOAD32_WORD / LOAD16_BYTE unverified" item for `maincpu_rom.bin`.
* Content facts used by the boot flow: reset code at `0x1fc00000` (`lui t0,0x13; ori t0,0x246f; ... sw t0,0x1f801010`), a PlayStation-style kernel image copied to low RAM (PCs `0x1810`, `0x296c`, `0x7c80`, `0xa0005664` appear in the traces), the game loader/decompressor at `0x1fc20b00-0x1fc21608` and the word table at `0x1fc24010-0x1fc2401c` (`[0x1fc24014]=0x80010000` dest, entry word `[0x1fc2401c] = 0x80010000`). The 12 bytes at ROM `0x20280` = `Namco Ltd.--` are copied to RAM `0x10000` at every reset by the driver (`m_boot_hack`).
* During boot (frames ~15-366) the CPU also sweeps the whole 4 MB ROM with word loads (self test): in `lua:access_trace_1800.txt` every 4 KB page of `0x1fc00000-0x1fffffff` that is not code receives at least 1024 word reads (pages `0x121000-0x3ff000`: exactly 1024). The pages `0x028000-0x120fff` receive 2048-3072 because the loader reads the compressed game image there (`[0x1fc24010]`..), ~1 MB that decompresses to 3,769,168 bytes at `0x80010000`; ROM above `0x121000` is game data fetched later by main-ROM DMA (offsets such as `0x130bf8`, `0x21de50`). The runtime must therefore keep all 4 MB readable at `0x1fc00000` (or skip the self test).

### 5.2 `bankedroms` and the three ways the game reaches it

`bankedroms` is `ROM_REGION32_LE 0x3800000` (56 MB; drv:3363-3373):

| Region offset | Size | Files | Rule |
|---|---|---|---|
| `0x0000000` | 16 MB | `teg1_rom0e.ic9` (halfwords at +0), `teg1_rom0o.ic13` (halfwords at +2) | `ROM_LOAD32_WORD`: `reg[4k..4k+1] = e[2k..2k+1]`, `reg[4k+2..4k+3] = o[2k..2k+1]` |
| `0x1000000` | 16 MB | `rom1e.ic10`, `rom1o.ic14` | same |
| `0x2000000` | 16 MB | `rom2e.ic11`, `rom2o.ic15` | same |
| `0x3000000` | 8 MB | `flel.ic4`(+0) `fleu.ic5`(+1) `flol.ic6`(+2) `flou.ic7`(+3) | `ROM_LOAD32_BYTE`: `reg[4k+j] = file_j[k]` |

Byte order verified: MAME's loaded region equals `generated/regions/bankedroms.bin` (sha256 `e0686230...e10ef`, `lua:dump_regions.lua`).

Three access paths exist in the driver; **only DMA is used by the game**:

1. **Bank window** `0x1fa00000-0x1fbfffff` (2 MB) = `bankedroms[bank*0x200000 ..]`, bank chosen by the 16-bit register at `0x1f000000`. tektagt never writes the register (0 writes in 1500 frames) and only performs one dummy `lhu 0x1fa00000` per DMA chunk (7673 reads, all from `img:0x8012e548`). Nevertheless the window must exist as read-only ROM bank 0 for the dummy read (and returns bank data except at the two protection holes).
2. **Banked-ROM DMA ("tektagt DMA")** - the real data path (`img:0x8012e4a0`). Per chunk the game executes: `lhu 0x1fb80000` (dummy), `sw byte_offset -> 0x1fb00000`, `lw 0x1fb00000`, `sh 0 -> 0x1f700000`, **`lhu 0x1f700000` (arms the flag)**, programs EXP1_BASE/CFG, writes MADR/BCR/CHCR=`0x01000000` of DMA channel 5 (`0x1f8010d0/d4/d8`), dummy `lhu 0x1fa00000`, `sh 0 -> 0x1f008000`, polls `lhu 0x1fb00000` until bit 15 is set (always already set in MAME).
3. **Main-ROM DMA**: `sh 0x84xx -> 0x1f70xxxx` (`img:0x8012e758`) sets `m_dmaoffset = (addr & 0xffff) | (0x84xx << 16)`; bit 31 selects `maincpu:rom` (offset & 0xffffff) and the flag is not armed. Seen for offsets like `0x21de50`, `0x130bf8`.

The exact DMA5 semantics (clamps, byte copy, flag clear) and the protection state machine are specified in `DEVICES.md` section 6 and in `address_map.json` (`rom_dma`, `tektagt_protection`). The copy rule was independently re-implemented and matched MAME's RAM for all 7634 transfers of a 1200-frame run (`lua:dma_verify.txt`: 0 mismatches).
This also **explains the earlier open item in `reports/PHASE1_AUDIT.md`** ("bank register 0x1f000000 and protection windows: 0 write-tap hits in 4000 frames"): those taps were silently dropped by MAME 0.264 around frame 360 (see section 10); with `handle:reinstall()` every frame the same windows show 7,984 writes to `0x1fb00000` and 7,978 arming reads of `0x1f700000`.

## 6. Appendix: H8/3002 sound CPU address map (24-bit, big-endian 16-bit bus)

<!-- BEGIN SUB_TABLE -->
| Range | Device | R/W | Width | Notes | Evidence | Status |
|---|---|---|---|---|---|---|
| `0x000000 - 0x07ffff` | Sound program ROM 'sub' (512 KB, teg1.11s) | R | 16 BE | ROM_LOAD16_WORD_SWAP; reset vectors at 0. | drv:1277<br>drv:3375-3376 | V-both |
| `0x080000 - 0x08ffff` | Shared RAM 64 KB (share 'sharedram') | RW | 16 BE | Main CPU sees 0x080000-0x083fff at 0x1f080000. | drv:1278<br>lua:map_dump.txt | V-both |
| `0x280000 - 0x287fff` | Namco C352 PCM (25.4016 MHz, divider 288 = 88.2 kHz) | RW | 16 BE | Wave ROM region 'c352' 16 MB (teg1_wave0/1) via :c352:bank. 118,188 writes / 45,273 reads in 1800 frames. | drv:1279<br>drv:1214<br>drv:3378-3380<br>lua:access_trace_1800.txt | V-both |
| `0x300000 - 0x300003` | Player/system inputs (JAMMA, active low) | R | 8 per offset (master) / 2x16 (0.264) | Byte 0x300000 = P2, 0x300001 = P1 (bit0 RIGHT,1 LEFT,2 DOWN,3 UP,4 B1,5 B2,6 B3,7 START), 0x300002 = bit7 P1 SERVICE, bit6 TEST/service switch, bit5 COIN1, bit4 COIN2, bits3-0 JAMMA B7/B8, 0x300003 = bits7-5 P2 B6/B5/B4, bits3-1 P1 B6/B5/B4. All read 0xff when idle (observed ffff/ffff). | drv:1280<br>drv:1419-1458<br>drv264:2060-2094<br>lua:mainview.txt<br>lua:access_trace_1800.txt (sub R 300000/300002) | V-both |
| `0x300010 - 0x300011` | nop r/w (wait-state generator) | RW | 16 |  | drv:1281 | V-drv |
| `0x300030 - 0x300031` | nop r/w (wait-state generator) | RW | 16 | BIOS writes here 'simply to generate a wait state'; values seen 0,0x1000..0x8400 (1948 writes). | drv:1282<br>lua:access_trace_1800.txt (sub W 300030) | V-both |
| `0xfffd10 - 0xffffff` | H8/3002 on-chip RAM (ffff d10-ff0f) and I/O registers | RW | 8/16 | Internal; see reports/refs/h83002.cpp (map lines 31-139) and h8*.cpp. Not part of the board address map. | lua:map_dump.txt (:sub entries 08-103) | V-both |
<!-- END SUB_TABLE -->

Main/H8 view of the shared RAM was verified by writing from each side (`lua:mainview.txt`): main `0x1f083ff0 <- 0x11223344` reads back
`0x11223344`; the H8 reads `0x3344` at `0x083ff0` and `0x1122` at `0x083ff2`; an H8 write of `0xcafe` at `0x080100` reads `0x0000cafe` at `0x1f080100`.
H8 on-chip I/O (`0xfffd10-0xffffff`) is part of the CPU, see `reports/refs/h83002.cpp`.

## 7. Bus errors and unmapped accesses

* `berr_r`/`berr_w` (cpu:207-218) set the CPU's bus-error flag; the core then raises a data-bus-error exception (code 7) at the next instruction boundary (cpu:2160, 2266, 2366 ...). Reads return 0. Used for: RAM window overflow (`0x400000-0x1effffff`), the 1 KB hole `0x1f800400-0x1f800fff`, scratchpad when the BIU disables it, ROM window overflow, `0x20000000-0x7fffffff`, `0xc0000000-0xfffdffff`.
* The tektagt game never generates a bus error in the 1800 frames traced; a replacement may treat these as fatal diagnostics.
* All other unmapped addresses (e.g. most of `0x1f000000-0x1f7fffff`, `0x1f802000-0x1f80201f`, `0x1f802034-0x1f80203f`) read 0 and ignore writes.

## 8. MAME 0.264 (binary) vs master f8f6b22 (pinned source) for tektagt

| Item | 0.264 | master |
|---|---|---|
| Machine function | `namcos12_boothack_state::tektagt` -> `coh700` + `tektagt_map` (drv264:1803-1810, 1847) | `coh700b` (= `coh700` + `m_boot_hack`) + `tektagt_state::maincpu_map` (drv:1132-1139, 1841-1848) |
| `0x1f008000` write | unmapped | `nopw` (drv:1265) |
| `0x1f700000` read (arm) | `map(0x1f700000,0x1f700003).r(...)` (drv264:1466) | adds `.mirror(0x800000)` -> also `0x1ff00000` (shadowed by ROM, HYP no effect) (drv:1845) |
| PSX hw-register map | physical only (cpu264 map) | physical + kseg0 + kseg1 aliases (cpu:1760) |
| SPU register block | `0x1f801c00-0x1f801dff` | `0x1f801c00-0x1f801fff` |
| H8 inputs | two 16-bit ports `IN0`/`IN1` at `0x300000/0x300002` | byte-wide `in_r` at `0x300000-3` (JAMMA converted from JVS ports) - same bit layout |
| H8 port 8 idle value | `0x02` | `0xef` (`(sense!=None)<<4 | 0x02 | 0xed`) |
| Bank reset | `bankoffset_w(0,0,...)` in `machine_reset` | `set_entry(0)` in `machine_start` |
| DMA5 byte copy | identical (`WORD_XOR_LE` copy of halfwords) | identical |
| Protection registers | identical | identical |

## 9. Implementation checklist for the runtime replacement

```
phys = va & 0x1fffffff
if va >= 0xc0000000:   only 0xfffe0130..0xfffe0133 (BIU) is valid, else bus error
if phys < 0x00400000:  RAM[phys]                                      # 4 MB, final window
elif phys < 0x1f000000: bus error
elif phys >= 0x1fc00000: ROM[phys - 0x1fc00000]                       # 4 MB, read only (writes: bus error/ignore)
elif 0x1f800000 <= phys < 0x1f800400: scratchpad (needs BIU & 0x88 == 0x88)
elif 0x1f800400 <= phys < 0x1f801000: bus error
elif 0x1f801000 <= phys < 0x1f802000: PSX hardware registers (section 3 / DEVICES.md): IRQ, DMA, RCNT, GPU, MDEC, plus config storage
elif 0x1f802020 <= phys < 0x1f802034: 20 bytes RAM;  0x1f802040..43: write ignored
elif phys in 0x1f140000..0x1f140fff: EEPROM byte index (phys-0x1f140000)>>1, only even byte addresses carry data
elif phys in 0x1f080000..0x1f083fff: shared RAM (16-bit little-endian halfword view of the H8 array)
elif phys in 0x1fa00000..0x1fbfffff: PROT1 at 1fb00000-3, PROT2 at 1fb80000-3, else bankedroms[bank*0x200000 + (phys & 0x1fffff)]
elif phys in 0x1f700000..0x1f70ffff: writes -> dma_offset; read at 0x1f700000..3 -> arm flag
elif phys in 0x1f000000..0x1f000001: write -> bank register
elif phys in {0x1f008000, 0x1f010000, 0x1f018000, 0x1f1bff08..0x1f1bff0f}: write ignored
else: reads 0 / writes ignored
```

Performance notes for the compiler/runtime (not MAME behaviour):

* The hot paths are RAM and the hardware registers; the game hammers `0x1f801070/74/98/a8/e8/f0/f4/110/814` and `0x1f018000` (1.1 M writes) - keep these cheap (`lua:access_trace_1800.txt` has exact counts).
* 8/16-bit accesses to the 32-bit hardware registers are legal and used (`sh` to I_STAT/I_MASK, `lhu` of GPUSTAT halves); handlers must honour byte lanes like MAME (`mem_mask`).
* Plain-RAM scratch registers (`0x1f801004`, `0x1f80100c`, `0x1f801014-1f`, `0x1f801024-2f`, `0x1f801064-6f`, `0x1f802020-33`) must store and return what was written.

## 10. Open items / HYPOTHESES

See `address_map.json` -> `hypotheses`. In short: purpose of the three write-only strobes and of `0x1f1bff08-0f`; VBLANK duration; real CPU speed; real-hardware behaviour of unmapped addresses; whether the game can issue protection challenges beyond the two pairs seen (the MAME table has four plus the `0xa9` rule).
**Tooling note for anyone writing MAME Lua taps** (cost me a mis-diagnosis): in MAME 0.264 older `install_read_tap`/`install_write_tap` handles on the `0x1f80xxxx`/`0x1fxxxxxx` hardware range stop firing roughly when the game starts (~frame 360); calling `handle:reinstall()` on every handle each frame restores them (`reports/trace_a03/tapdbg.lua`, `access_trace.lua`). Direct-RAM taps are unaffected.
