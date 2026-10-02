# Namco System 12 `tektagt` - device-by-device behaviour and differences from a home PlayStation

Author: agent03. Companion of [`ADDRESS_MAP.md`](ADDRESS_MAP.md) (addresses) and [`address_map.json`](address_map.json) (machine readable).
Evidence tags (`drv:`, `cpu:`, `lua:`, `img:` ...) and status words (V-drv / V-lua / V-both / **HYPOTHESIS**) are defined in ADDRESS_MAP.md section 0.
"PS1" below means the public description of the retail console (nocash psx-spx) and the assumptions visible in the RecompOne runtime
(`/home/user/work/recompone/RecompOne.Runtime`); it was not re-verified here. Everything under "System 12" is source- or probe-backed unless marked.

## 0. Device tree (MAME 0.264 `tektagt`, `lua:map_dump.txt`)

```
maincpu  cxd8661r  (100 MHz)  program space: 32-bit LE, internal PSX map + driver map
 |- irq   psxirq      I_STAT/I_MASK                 -> maincpu IRQ0 (Cause.IP2)
 |- dma   psxdma      7 channels, ch5 = ROM read    -> irq.intin3
 |- mdec  psxmdec     (dma ch0 write / ch1 read)
 |- rcnt  psxrcnt     3 root counters               -> irq.intin4/5/6
 |- sio0  psxsio0     -> irq.intin7   (nothing attached)
 |- sio1  psxsio1     -> irq.intin8   (nothing attached)
 |- ram   ram_device  4 MB
gpu      cxd8654q     2 MB VRAM, pixel clock 53.693175 MHz; vblank -> irq.intin0
screen   raster, dynamic size; vblank -> H8 IRQ1
sub      h83002 16.9344 MHz (+intc, adc, dma, ports 4/6/7/8/9/A/B, timer16 x5, sci0/1, watchdog)
c352     25.4016 MHz /288
at28c16  EEPROM (main CPU bus)       rtc rtc4543        namco_settings
lspeaker/rspeaker
```

## 1. Main CPU - Sony CXD8661R

| Aspect | System 12 `tektagt` | Home PS1 | Evidence |
|---|---|---|---|
| Core | R3000A-class MIPS I, little endian, 5-stage, load delay slot, branch delay slot, no MMU, COP0 (SR, Cause, EPC, BadVAddr, PRId, DCIC/breakpoint regs), COP2 = GTE | same (CXD8530) | cpu:1922-1937 state list |
| Clock | 100,000,000 Hz device clock; MAME executes 1 instruction per 4 clocks (25 M/s) | 33.8688 MHz, ~1 instr/cycle | drv:1122; cpu:182-183 |
| PRId | 2 | 2 | lua:final_checks.txt |
| Reset | PC `0xbfc00000`, SR=`0x00400000` (BEV) | same | cpu:2037-2048 |
| Exceptions | vector `0xbfc00180` while BEV=1 else `0x80000080`; breakpoint `0xbfc00140`/`0x80000040`; interrupt entry is checked at every instruction boundary | same | cpu:1589-1665 |
| Interrupt pin | PSX IRQ controller drives IRQ0 -> Cause.IP2; runtime `SR` = `0x401` (IEc, IM2), `0x40000401` with CU2 | same | cpu:2296-2300, 3464; lua:access_trace_1800.txt snapshots |
| Caches | BIU `0xfffe0130` = `0x0001e988` after boot: scratchpad (RAM+DS) and I-cache enabled. MAME does not model I-cache hits (fetches always see current RAM) | same register | cpu:1336-1350, 295-309 |
| Memory map | see ADDRESS_MAP.md | RAM 2 MB, BIOS 512 KB | |

Notes for the runtime:
* Code runs from three places: the boot ROM (`0xbfc00000`, then `0x1fc2xxxx`), a **Sony-style kernel** that the boot ROM copies into low RAM (PCs `0x0500-0x9fff`; observed `0x1810`, `0x296c`, `0x7c80`, `0xa0005664`; RAM `0x1000` is a copy of ROM `0x6b00`, RAM `0x4000` of ROM `0x9b00`, i.e. kernel blob at ROM ~`0x6000-0xf000` - exact bounds not determined, HYPOTHESIS), and the game image at `0x80010000`. The kernel supplies the A0/B0/C0 call tables, the exception dispatcher (the traced `IRQ W 1f801070 = ffffffff` at PC `0x296c`) and cache routines (BIU writes from `0xa0005664`). **RecompOne's PS1 BIOS HLE is not a drop-in replacement**: the System 12 kernel lives in ROM/RAM and must either be recompiled from the ROM image or re-implemented behaviourally.
* Boot self test reads the entire 4 MB ROM (ADDRESS_MAP.md 5.1) before decompressing the game (frames 15-366, ~6 s emulated).

## 2. Memory: RAM, scratchpad, ROM, VRAM

| Memory | System 12 | PS1 | Evidence |
|---|---|---|---|
| Main RAM | 4 MB, zero-initialised, window selected by RAM_SIZE (final `0xcb4` = 4 MB); no mirrors inside the 4 MB; bus error `0x400000-0x1effffff` | 2 MB mirrored x4 in an 8 MB window, RAM_SIZE normally `0x0b88` | ADDRESS_MAP.md 3/4; lua:final_checks.txt |
| Scratchpad | `0x1f800000-0x1f8003ff` only (physical); behaves as RAM when BIU RAM+DS set | same | cpu:1336-1350 |
| Program ROM | 4 MB at `0x1fc00000` | 512 KB BIOS | ADDRESS_MAP.md 5.1 |
| Banked ROM | 56 MB, only via DMA5 (and a 2 MB window) | none | ADDRESS_MAP.md 5.2 |
| VRAM | 2 MB = 1024x1024x16 | 1 MB = 1024x512x16 | gpu:458-475 (`height = ram_size/width/2`), drv:1168 |
| Boot-hack bytes | `Namco Ltd.--` at RAM `0x10000` at every reset | n/a | drv:1250-1258 |

## 3. Interrupt controller (psxirq)

* Registers (32-bit): `I_STAT` @`0x1f801070`, `I_MASK` @`0x1f801074`. `PSX_IRQ_MASK = 0x7fd` (bit 1 flagged unknown but still stored) (irq:17, 77-82).
* Output = `(I_STAT & I_MASK) != 0`, **level-sensitive**, re-evaluated after every stat/mask change (irq:53-65); asserts maincpu IRQ0 = Cause.IP2 (cpu:2296-2300).
* Input edges: `intin0` (GPU vblank, bit 0), `intin3` (DMA, bit 3), `intin4/5/6` (root counters 0/1/2), `intin7/8` (SIO0/1); each only acts on a rising `state=1` and ORs its bit into I_STAT (irq:108-157).
* **Write semantics (MAME, differs from hardware documentation):** `I_STAT = (I_STAT & ~mem_mask) | (I_STAT & I_MASK & data)` (irq:73). Software acknowledges by writing 0 to the bit, as on a PS1, but a write also silently drops any *pending-but-masked* bit even when `data` has a 1 there (the kernel's `sw 0xffffffff` at PC `0x296c` is therefore not a no-op in MAME). `I_MASK = (I_MASK & ~mem_mask) | data` (irq:78). A 16-bit write only affects its half-word lane.
* Observed use (`lua:final_checks.txt`, 1500 frames, fresh NVRAM): `I_MASK` values `0x00` (critical sections, 2832 writes), `0x01`, `0x09` (VBLANK|DMA, 818), `0x29` (VBLANK|DMA|RC1, 2012+). Acks: `0xfffffffe` (VBLANK, 1129), `0xfffffff7` (DMA, 5025), `0xffffffdf` (RC1, 716), `0xffffffff` (kernel, 6566). In the separate 1800-frame run (`lua:access_trace_1800.txt`) `I_STAT` was read ~203 k times and `I_MASK` ~236 k times (busy waits in the SDK).
* Compared with PS1: same bit assignment; the PS1 sources GPU-IRQ1 (bit 1), CD-ROM (bit 2), SPU (bit 9) and PIO (bit 10) are not wired, so only bits 0,3,4,5,6,7,8 can ever be set (bit 1 is outside MAME's `PSX_IRQ_MASK`).

## 4. Vertical blank and frame timing

* `psxgpu::set_cpu` connects `vblank_callback -> psxirq::intin0` (gpu:50). `psxgpu::vblank(state)` updates GPUSTAT bit 13 and calls the handler **only on the rising edge** (gpu:3450-3466): one `I_STAT.0` per frame.
* The driver additionally routes the screen's vblank (both edges) to the sound CPU: `SCREEN(...).screen_vblank().set(namcos12_sub_irq)` (drv:1170) -> H8 IRQ1 asserted at vblank start and cleared at vblank end, H8 ADC trigger, and bit 7 of H8 port B (drv:1412-1417).
* Frame rate is derived from the GPU display mode (gpu:378-455): NTSC 240-line `59.8260978565 Hz`, NTSC 480-line `59.9400523286 Hz`, PAL `50 Hz`. Observed frame period `0.016715113 s` (59.8261 Hz) at 256x240 and 640x240 (`lua:vbl.txt`). Screen sizes seen: 256x240 at frames 5-100 and 1000, 640x240 at frames 380-700 (display mode is changed by the game). Nominal: 640x480 raster (`reports/mame_ref/listxml.txt:227`).
* Vblank duration: **HYPOTHESIS** ~2.5 ms (`set_vblank_time(2500 us)`, gpu:3514, applied only if the screen was not configured; not measured).
* The game uses one VBLANK interrupt per frame plus root counter 1 (hblank-sourced, see section 7) as a raster timer; the DMA channel interrupt is used for ROM and GPU transfers.

## 5. DMA controller (psxdma) and the channel assignment

Registers per channel `n`: `MADR 0x1f801080+0x10n`, `BCR +4`, `CHCR +8`; `DPCR 0x1f8010f0`; `DICR 0x1f8010f4`.
MAME executes a transfer **at the moment CHCR is written** (dma:214-343) provided CHCR bit 24 (start) is set and `DPCR` bit `3+4n` is set; transfers are
instantaneous (no bus cycles) except for the completion timers listed below. `MADR` is masked with `ram_size-1`.

| Ch | Device | CHCR values MAME accepts / game uses | MAME action and completion | Evidence |
|---|---|---|---|---|
| 0 | MDEC in (`dma_write`) | `0x01000201` (block, RAM->dev) - game: 581 times | data written to MDEC immediately; flag+IRQ immediate | dma:288-296; cpu:3469-3470; lua:access_trace_1800 |
| 1 | MDEC out (`dma_read`) | `0x01000200` (block, dev->RAM) - game: 579 | data read immediately; **completion flag/IRQ after 26000 x 33.8688 MHz ticks (0.77 ms)** | dma:278-287 |
| 2 | GPU (`dma_write`/`dma_read`) | `0x01000201` block write and `0x01000401` linked list (CHCR written 12,497 times in 1800 frames; 1967 list starts in the 1500-frame run); `0x01000200` read is supported but not observed | block: immediate; list: walks the whole list immediately (stops at `0xffffff`, restarts after 16000 ticks if >65535 words) | dma:288-300, 312-319, 141-186 |
| 3 | CD | none (no handler) | "unknown mode" ignored | - |
| 4 | SPU | none | ignored | - |
| 5 | **ROM read** (driver `namcos12_rom_read`) | `0x01000000` (game: 8089 times); MAME also accepts `0x01000200` and `(v & 0xffbffeff)==0x11000000` | copy done **immediately inside the CHCR write**, flag/IRQ immediate | drv:1161, 1310-1365; dma:258-277 |
| 6 | OTC | `0x11000002` (game: 829 times) | builds the reverse ordering table immediately; completion after 2150 ticks | dma:320-338 |

* **DPCR** (`0x1f8010f0`): game writes `0x33333333` (all off) -> `0x33b33333` (only ch5) during ROM loads -> final `0x3bb33bbb` (ch0,1,2,5,6 enabled; ch3,4 off). Plain storage in MAME.
* **DICR** write rule (dma:354-366): `new = (old & 0x80000000) | (old & ~data & 0x7f000000) | (data & 0x00ff803f)`: bits 24-30 are write-1-to-clear flags, bit 31 is derived, bits 16-23 (enables, 23 = master) and 0-5 are plain. Completion (`dma_finished`, dma:113-206) sets flag `1<<(24+ch)`, then `dma_interrupt_update` (dma:92-111): if master enable (bit 23) and `(flags & enables)!=0` -> DICR bit 31 and **I_STAT bit 3 is set**; `m_dicr &= 0x00ffffff | (m_dicr << 8)` keeps a flag only if its enable bit is set (a disabled channel never latches a flag). Clearing DICR does *not* clear I_STAT (`intin3(0)` is a no-op), software must also write I_STAT.
* Game values: DICR enables `0x00a00000` (ch5+master) / `0x00840000` (ch2+master); write `0x20a00000` (clears the ch5 flag, keeps enables) 111 times; reads of `0x84840000` / `0xa0a00000` show flag + enable + master. (`lua:final_checks.txt`)
* **Difference from PS1:** ch5 (PIO, unused on PS1) is the ROM-to-RAM engine; ch3 (CD) and ch4 (SPU) are dead; ROM transfers are *synchronous* so the loader's busy-wait on CHCR bit 24 / DMA IRQ is satisfied on the very next instruction.

## 6. ROM DMA, bank registers and the tektagt protection

State kept by the driver (all reset to 0/false; `m_has_tektagt_dma` also cleared in tektagt `machine_reset`, drv:1838): `m_dmaoffset`, `m_tektagdmaoffset`, `m_bankoffset`, `m_has_tektagt_dma`, `m_ttt_cnt`, `m_ttt_val[2]`.

### 6.1 Offset registers

| Address | Access | Effect |
|---|---|---|
| `0x1f700000 + X` (X even, `< 0x10000`) | write 16 bit, data `D` | `m_dmaoffset = X \| (D << 16)` (drv:1303-1308). A 32-bit store is two 16-bit calls (`X`, then `X+2`), the second wins. |
| `0x1f700000-3` | read 16 bit | returns 0, **sets `m_has_tektagt_dma = true`** (drv:1932-1937) |
| `0x1fb00000` (+0 low half, +2 high half) | write 16 bit (a 32-bit store does both) | low: `m_tektagdmaoffset = D`; high: `\|= D<<16`; `if ttt_cnt != 2` the value is also captured into `ttt_val[ttt_cnt]` (cnt++ on the high half) (drv:1850-1869) |
| `0x1fb00000` | read | low half `0x8000`, high half 0 (busy flag clear); 32-bit load = `0x00008000` (drv:1871-1883) |
| `0x1fb80000` | write 16 bit | low half: `ttt_cnt = 0` (value ignored; game writes 0); high half: ignored (drv:1885-1896) |
| `0x1fb80000` | read | see protection table below; 32-bit load returns `{high<<16 \| low}` (drv:1898-1930) |
| `0x1f000000` | write 16 bit | `mainbank.set_entry(D)` (not used by tektagt) |

### 6.2 DMA5 source selection and copy (`namcos12_rom_read`, drv:1310-1365)

```
offset   = has_tektagt_dma ? tektagdmaoffset : dmaoffset
mainrom  = (offset >> 31) | (exp_base == 0x1f300000)
if mainrom: region = maincpu:rom   (4 MB),   offset &= 0xffffff
else:       region = bankedroms    (56 MB),  offset &= 0x7fffffff        # alt_bank handling is off for tektagt
has_tektagt_dma = false                                                   # consumed by ANY ch5 transfer
words = BCR                                                               # BCR is the full 32-bit register when CHCR bit 9 == 0
words = min(words, (region_bytes/2 - offset/2)/2)                         # clamp to ROM end (negative -> nothing copied)
words = min(words, ram_bytes/4 - MADR/4)                                  # clamp to RAM end
RAM[MADR .. MADR+4*words) = region[offset .. offset+4*words)              # byte for byte, little-endian regions
```
(`offset/2` uses halfword units: an odd byte offset is truncated; the game always aligns to 4.)
**Independently verified** (`lua:dma_verify.txt`): re-implementing exactly this rule in Lua from the observed register writes and comparing against the RAM MAME produced gave 7634 DMA5 transfers in 1200 frames (7523 banked-ROM/armed, 111 main-ROM), **7634 identical, 0 mismatching**, largest transfer 14,652 words, no clamping events.
Observed chunking: a 1-word header read into `0x803fffa8`, then the payload (BCR `0x6a0`, `0x52`, `0x10`, ...); the loader limits one chunk to `0x20000` bytes (`img:0x8012e4e4-0x8012e4f8`). Totals in 1800 frames: 8089 DMA5 starts = 7978 armed banked-ROM reads + 111 main-ROM reads; 8181 strobe writes to `0x1f008000`.

### 6.3 Protection challenge/response (`tektagt_protection_*`)

Handshake used by the game (`img:0x800f8e64`): `sh <halfword from RAM 0x8024357c> -> 0x1fb80000` (restart: low half = cnt reset), `sw v0 -> 0x1fb00000`, `sw v1 -> 0x1fb00000` (two 32-bit values -> cnt = 2), `lw 0x1fb00000` (busy poll), `lw 0x1fb80000` (response), result added into running checks at RAM `0x802435c0/35cc`. The same shape appears at `img:0x800fccdc`, `0x8010a6f0` and `0x8014c178` (static disassembly). The challenge values are computed from fixed code addresses of the game image: e.g. `0x80010240 ^ 0x55555555 = d5545715`, `0x8001024c ^ 0xaaaaaaaa = 2aaba8e6`, `0x80010340 ^ 0x55555555 = d5545615`, and the site at `0x8014c1a0` ORs `0x00a90000` into its second value (so it falls under the generic `0xa9` rule). The four table pairs therefore correspond to the four check sites (`0x800f8e64` x2, `0x800fccdc`, `0x8010a6f0`), consistent with the constants but not individually proven for the last two.

With `ttt_cnt == 2` the response read at `0x1fb80000` is (offset 0 = low half, offset 1 = high half), everything else 0:

| `ttt_val[0]` | `ttt_val[1]` | offset 0 | offset 1 |
|---|---|---|---|
| `806d2c24` | `d5545715` | `0x36e2` | 0 |
| `804c2c84` | `d5545615` | `0x2651` | 0 |
| `2aaba8e6` | `00820040` | 0 | `0x4186` |
| `2aaba592` | `01780544` | 0 | `0x3c7d` |
| other, with `((val1 >> 16) & 0xff) == 0xa9` | | `0x552e` | `0x552e` |

Observed in 1800 frames (`lua:final_checks.txt`): frame 370 `(806d2c24,d5545715) -> 0x000036e2` and `(2aaba8e6,00820040) -> 0x41860000`; subsequent loader writes of byte offsets to `0x1fb00000` happen with `ttt_cnt == 2`, so they update `m_tektagdmaoffset` but are not captured. A replacement must keep this exact state machine (including "cnt stays 2 until the next write to `0x1fb80000`"); a simplification that always captures offsets would break the challenge.
`HYPOTHESIS`: other game builds/modes may issue further pairs; none appeared in the traced attract mode.

## 7. Root counters (psxrcnt)

* Three 16-bit counters at `0x1f801100/110/120`: `+0 count`, `+4 mode`, `+8 target` (rcnt:61-124). Time base: `total_cycles()*2` = 33.8688 MHz equivalent (rcnt:129-133); timers scheduled with `attotime::from_hz(33868800)` (rcnt:222).
* Modeled mode bits: `0x01` stop, `0x04` reset, `0x08` count-to-target, `0x10` IRQ at target, `0x20` IRQ on overflow, `0x40` repeat, `0x100` CLC (alternate clock), `0x200` DIV; read-only flags `0x800` reached target, `0x1000` reached 0xffff (cleared by reading mode). Sync modes are **not** modelled.
* Dividers: counter 0 + CLC = 5 (pixel clock), counter 1 + CLC = **2150** sysclk ticks (hblank, 15.75 kHz), counter 2 + DIV = 8 (rcnt:135-152).
* IRQ outputs `intin4/5/6` -> I_STAT bits 4/5/6 (cpu:3473-3476).
* tektagt: counters 0 and 2 are zeroed at reset and run free (mode `0x1000` = reached-0xffff flag set); **counter 1 is the game's raster timer**: mode `0x0100` -> `0x0112` (CLC | IRQ-target | bit1) / `0x0148` (CLC | repeat | count-to-target), target rewritten ~2650 times (values 0xff, 0x100, 0x102, 0x178-0x17a ...), counter read ~85 k times, IRQ -> I_STAT bit 5 (`I_MASK=0x29`).
* PS1: same three counters and the same sources; real hardware has sync modes and dot-clock gating that MAME (and therefore the reference trace) ignores.

## 8. GPU - CXD8654Q (MAME `psxgpu_device`, type 2)

* Registers: `0x1f801810` GP0 write / GPUREAD read, `0x1f801814` GP1 write / GPUSTAT read (gpu:3228-3300, 3424-3446).
* **VRAM 2 MB, 1024x1024 16-bit** (rows address `n_line % 1024`) vs 1 MB / 512 rows on a PS1 (gpu:466-478). Draw-mode (GP0 E1) bit 11 selects the second 1 MB texture page bank (`ty |= (tpage & 0x800) >> 2`) and is mirrored into GPUSTAT bit 15 (gpu:795-802).
* Same command set as the retail GPU as modelled by MAME. GP1 commands the game actually issues (1800 frames, `lua:access_trace_1800.txt`): `00` reset (15), `01` (743), `02` ack-irq (743; MAME: not handled), `03` display enable (755), `04` DMA direction (21,190), `05` display start (826), `06`/`07` display ranges (831/823), `08` display mode (831), `09` (7), `10` get-info (13). `GP1(0x09)` (first command the boot ROM writes: `0x09000001`) is *not handled* by MAME and ignored (gpu:3299-3302), as is `GP1(0x02)` (gpu:3245-3248). (The GP0 histogram in the trace mixes command and data words and is not a command census.) `GP1(0x10)` get-info index 7 returns the GPU type (2). Lightgun registers exist (GP1 0x0d / info 8) but are unused.
* Reset state `GPUSTAT=0x14802000`; the display mode GP1(08) selects width 256/320/368/384/512/640, 240/480 lines, NTSC/PAL (gpu:378-455) and reconfigures the MAME screen (hence frame rate). GPUSTAT bit 31 (odd line/field) is synthesised from the screen position (gpu:3438-3442). GPUSTAT is polled 63,694 times in 1800 frames.
* DMA: ch2 handlers `dma_write` (RAM->VRAM block and linked list) and `dma_read` (gpu:46-49); ch6 OTC is built into `dma.cpp`.
* Clock: the GPU device is created with `100 MHz / 2` and `vclkn = 53.693175 MHz` (drv:1127, 1166); the only effect MAME derives from them is the refresh rates above.
* VRAM usage by the game (rows >= 512 used, e.g. as FMV double buffer), the GPU/MDEC/GTE adapters and the reusable-vs-must-change analysis are in the sibling document [`GPU.md`](GPU.md) (written by another agent; this document only fixes the register-level facts).
* **PS1 differences:** VRAM size/geometry, the two-bank texture page, GP1(09). The rasteriser, FIFOs and DMA are otherwise the same *model*; MAME's software rasteriser is not cycle accurate.

## 9. GTE and MDEC

* GTE (COP2) is the stock PlayStation GTE (cpu `m_gte`, register names identical to the PS1); enabled via SR.CU2 (observed SR `0x40000401`). Accuracy of MAME's GTE versus RecompOne's `Hardware/Gte.cs` was **not** compared here (risk for differential tests).
* MDEC: stock `psxmdec_device` at `0x1f801820/24`, DMA ch0 (in) and ch1 (out) as on a PS1. The game decodes FMV through it from frame ~641 on (581 command words `0x30000xxx`/`0x4000000x`/`0x60000000`, DMA ch0 `0x01000201` + ch1 `0x01000200`, status polled 581 times at `0x1f801824`, completion polled ~691 k times on `CHCR1` `0x1f801098`).

## 10. SIO0/SIO1, SPU, CD-ROM: present on the die, absent on the board

* SIO0/SIO1 (`0x1f801040-5f`) are real `psxsio` models but no pad/memory-card/serial device is connected in the driver and the game never accesses them (`lua:access_trace_1800.txt`). Controls reach the game through the H8 CPU and shared RAM (section 12).
* CD-ROM (`0x1f801800-03`) and SPU (`0x1f801c00-0x1f801fff`) are forwarded to *unconnected* `m_cd_*` / `m_spu_*` handlers: reads return 0, writes vanish (cpu:3425-3443). Only the kernel's init pokes SPU registers at boot. No XA/CD audio path exists.
* Consequently the RecompOne `Cdrom/`, `Sio0.cs`, `MemoryCard.cs`, `Spu.cs`, `XaAudio.cs` units are not needed (the PS1 SPU must not be used as the sound device; see 12).

## 11. Board devices on the main CPU bus

### 11.1 AT28C16 EEPROM (`0x1f140000-0x1f140fff`)

* 2048 data bytes (+32 ID bytes only reachable with A9=12 V, not wired here). Lane mapping: byte index `i` <-> physical `0x1f140000 + 2*i`, data on byte lanes 0 and 2; verified by the build-info string at EEPROM index `0x280` appearing at `0x1f140500` (`lua:access_trace_1800.txt`, `lua:eeprom_trace.txt`).
* Behaviour (at28:147-203): a write is ignored while busy; it only stores if the byte differs from the current cell and then starts a **200 us busy period** (`write_complete` timer); while busy any read returns `last_written ^ 0x80` (data polling); the erase mode (`oe_12v`) is not wired. Power-up contents `0xff`; backing file `at28c16` is 2080 bytes (`0x820`).
* Game: first boot with blank NVRAM writes ~220 bytes at index `0x280..` (frames 382-385, `lhu/sh` per byte, polling by repeated reads: 1760 reads / 220 writes) - bytes `"22:20:05, JUN 1"`, `00`, `"TEG2"`, ... (`0x280-0x35b`, last write at frame 385). Longer runs (3000 frames) also showed random-looking blocks at `0x380-0x4c1`, `0x580-0x789`, `0x7c0-0x7d1`, written later in attract mode (observed in an NVRAM file from a longer run; not traced). Subsequent boots read first. **Traces must start from a saved NVRAM copy** (see `STATUS.json` facts).

### 11.2 Shared RAM with the H8 (`0x1f080000-0x1f083fff`)

16-bit-array view described in ADDRESS_MAP.md 6. Traffic (1800 frames): ~548 k reads / ~1.1 k writes from the main CPU on 140 distinct words in `0x1f080100-0x1f08217c` and `0x1f083000-0x1f083a00`; the hot words are the mailbox/status cells at `0x1f083078` (449 k reads), `0x1f083068` (43 k), `0x1f0830f0` (43 k reads / 824 writes), `0x1f083380`. This is the sound command interface; its protocol belongs to the sound specification.

### 11.3 Write-only strobes and the ROM-board port

`0x1f008000` (once per DMA chunk), `0x1f018000` (1.1 M halfword-0 writes, poked inside polling loops), `0x1f010000` (never), `0x1f1bff08-0f` (boot ROM streams ~1 M halfwords of ROM data into `+0x0e`, terminators 3/1 at `+0x0c/+0x0a`, game repeats the terminators). MAME maps all of them as `nopw`; the game never reads them back. Purposes are **HYPOTHESES** (watchdog kick, ROM-board chip select/handshake to the KEYCUS CPLD). A replacement may ignore the writes; note the boot ROM's 1 M-iteration copy loop (6 instructions per iteration, ~6 M instructions = ~0.25 s at MAME's 25 M instr/s).

### 11.4 POST / expansion-2 area

`0x1f802041` receives boot progress codes 1-7; `0x1f802020-33` is 20 bytes of RAM written by the kernel's debug-port code; both live in the PSX die's expansion-2 window and behave as on PS1 (cpu:1784-1787).

## 12. Sound / input subsystem (H8/3002 board) - interface only

* H8/3002 @16.9344 MHz, program ROM 512 KB (`teg1.11s`, `ROM_LOAD16_WORD_SWAP`), RAM 64 KB shared, C352 at `0x280000-0x287fff` (88.2 kHz output, 16 MB wave ROM `teg1_wave0/1`), inputs at `0x300000-3` (drv:1173-1216, 1275-1282). The main CPU talks to it only through shared RAM and the vblank IRQ it gets from the screen.
* Port wiring (drv:1175-1186, 1367-1417): P6 read bit1 = "no JVS I/O board" (idle `0xff`), P7 = DIP switches (idle `0xff`: bit7 service mode off, bit6 freeze off, bits 5-0 unused/1), P8 read `0xef` idle (master) / `0x02` (0.264), PA/PB select the RTC4543 (PA0=1, PB5=1) or the NAMCO_SETTINGS serial chip (PA0=0, PB5=1) on SCI1, PB0 -> JVS RTS, PB7 mirrors vblank, ADC0-3 return 0, SCI0 is the JVS link (no device present, so inputs come from the JAMMA-style `in_r`).
* Input layout, active low, H8 `0x300000..3` = `P2, P1, system, buttons2`: see `address_map.json` row `H8_IN`. Idle value `0xffff` for both words (`lua:mainview.txt`), polled ~2 times/frame (`sub R 300000 n=3590`, `300002 n=7184`).
* RTC4543 (32.768 kHz) and the settings shift register are only reachable from the H8; the main CPU never sees them directly.
* **PS1 difference:** there is no SPU; sound = H8 program + C352 PCM, i.e. a second CPU that must be emulated or replaced by an HLE of its mailbox protocol (outside this document).

## 13. Per-device differences from a home PlayStation (summary)

| Device | Home PS1 / RecompOne assumption | System 12 `tektagt` |
|---|---|---|
| CPU | 33.8688 MHz R3000A | 100 MHz xtal CXD8661R; MAME 25 M instr/s |
| BIOS/kernel | 512 KB BIOS ROM at `0xbfc00000`, HLE in RecompOne (`Bios/`) | 4 MB game-specific ROM, own Sony-style kernel copied to RAM, no PS1 BIOS |
| RAM | 2 MB (`MemoryMap.RetailRamSize`), 8 MB window | 4 MB, bus error above |
| Expansion 1/3 | open | bank register, strobes, shared RAM, EEPROM, DMA offset, protection, 2 MB bank window |
| IRQ controller | 11 sources, `stat &= data` | 7 wired sources (bits 0,3,4,5,6,7,8), MAME `stat & mask & data` quirk |
| DMA | ch5 unused, CD/SPU DMA | ch5 = ROM, ch3/4 dead, instantaneous transfers |
| Root counters | same | same (counter 1 as raster timer) |
| GPU | CXD8514Q 1 MB | CXD8654Q 2 MB, GP1(09), texture bank bit |
| SPU / CD / XA | present | absent |
| SIO0/1 | pads / cards | present but unconnected |
| MDEC | same | same, used for FMV |
| Input | controller port | H8 + JAMMA/JVS through shared RAM |
| Storage | memory card | 2 KB EEPROM (+RTC and settings chip behind the H8) |
| Video | NTSC 59.94 | 59.826 Hz (MAME model) at 240 lines, dynamic width 256-640 |
| Copy protection | disc | ROM-board KEYCUS + challenge/response registers |

## 14. Cross-check ledger (what was verified how)

| Claim | Method | File |
|---|---|---|
| Driver + PSX maps (38 + 103 entries) as listed | `address_space.map.entries` dump | `lua:map_dump.txt` |
| ROM window == region, aliases | 1 M word compare | `lua:final_checks.txt` |
| Region bytes == `generated/regions/*.bin` | dump + sha256 | `lua:dump_regions.lua` |
| RAM alias/window, final RAM_SIZE/ROM_CFG/BIU | read-back at frame 700 | `lua:final_checks.txt` |
| Shared RAM mapping, EEPROM lanes | cross-CPU read/write | `lua:mainview.txt` |
| DMA/protection protocol, IRQ patterns | tap traces | `lua:final_checks.txt`, `lua:access_trace_1800.txt` |
| ROM-DMA source/offset/copy rule | independent re-implementation vs MAME RAM, 7634/7634 identical | `lua:dma_verify.txt` |
| EEPROM first-boot writes | tap trace, fresh NVRAM | `lua:eeprom_trace.txt` |
| Frame period | `screen.frame_period` | `lua:vbl.txt` |
| Static code facts (`img:`/`rom:`) | capstone disassembly of the two images | - |

NVRAM provenance: `access_trace_600.txt` and `eeprom_trace.txt` started with an empty AT28C16; `access_trace_1800.txt` reused the file written by the first run; `final_checks.txt` and `vbl.txt` used fresh files. Differences between those traces in EEPROM traffic are expected.

## 15. HYPOTHESES and open questions (not proven)

1. `0x1f018000` is a watchdog clear; `0x1f008000` is a per-DMA strobe; `0x1f1bff08-0f` talks to the KEYCUS CPLD (ADDRESS_MAP.md 3).
2. Master's `mirror(0x800000)` on the arm-read (`0x1ff00000`) is shadowed by the ROM window.
3. VBLANK length (~2.5 ms) and real-silicon CPU speed.
4. Unmapped-address behaviour on real hardware; MAME returns 0 silently.
5. Completeness of the protection table beyond the two pairs actually issued in attract mode.
6. Exact bounds of the kernel image inside the boot ROM and the exact semantics of the kernel's A0/B0/C0 tables (not analysed here).
7. GTE bit-exactness of MAME vs the RecompOne GTE.
