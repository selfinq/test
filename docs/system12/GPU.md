# System 12 video: GPU, VRAM, DMA channel 2, timers, MDEC, GTE

Scope: what in RecompOne (commit `8d0091c`, `/home/user/work/recompone/RecompOne.Runtime`) can be reused for the Namco System 12
board that runs `tektagt`, and what must change. Every statement carries its evidence class:

* **[driver]** MAME `namcos12.cpp` at commit `f8f6b22` (`reports/refs/namcos12.cpp`)
* **[psx.cpp]** / **[psxcpu]** MAME `src/devices/video/psx.cpp`, `src/devices/cpu/psx/*.cpp` at `f8f6b22` (`reports/refs/psx*.cpp`, `psxcpu_*.cpp`, git-ignored)
* **[probe]** measured by Lua probes in the installed MAME 0.264 (Ubuntu apt) running the user's own `tektagt.zip` (`src/System12.Runtime/Gpu/mame/*.lua`)
* **[RecompOne]** read from the RecompOne source
* **[test]** executed here (`src/System12.Runtime/Gpu/tools/run_selftest.sh`, `compare_display.py`)
* **[inference]** my reasoning, not verified

Note: the installed MAME is 0.264, the pinned source is a newer master (`f8f6b22`). They differ internally (see "MAME version caveat"), the numbers that
matter here (2 MB VRAM, 59.826 Hz) agree.

## 1. Verdict table

| Area | RecompOne status for System 12 | Evidence |
|---|---|---|
| GP0/GP1 command decode, polygons/rects/lines/image transfers, GPUSTAT, GP1(08) mode decode | **reusable as is**, with the VRAM changes below | [test] 36 checks pass, incl. display modes tektagt actually uses |
| VRAM size/layout | **must change**: arcade VRAM is 2 MB = 1024 x 1024; RecompOne is a `const` 1024 x 512 | [driver] `RAM(...).set_bits(16).set_default_size("2M")`; [probe] `p_vram` = 1048576 x 2 bytes; game uses rows >= 512 |
| GP0(E1) bit 11, tpage Y, GP1(05) Y | **must change** (tpage Y = bit 4 (+256) + bit 11 (+512); display start Y 10 bit) | [psx.cpp] type 2; [probe] gpustat bit 15 set; display start 0x200/0x2f0 |
| Software rasteriser | RecompOne has **none**: `Gpu` only forwards to `IGpuBackend` (GL). Needed for headless use: `SoftwareGpuBackend` (new) | [RecompOne] `GpuHleForward.cs`; [test] |
| GL backends (Gl21/33/45) | **unusable for arcade without further edits** (512-row assumptions, see patches/0004) | [RecompOne] grep list in patch doc; not run (no GL here) |
| GPU DMA (channel 2: linked list, block write, VRAM read) | **reusable**; timing/IRQ-delivery model must change | [test]; [probe] chcr values; [psxcpu] dma.cpp |
| DMA channel 5 (ROM -> RAM, protection) | **missing** in RecompOne (`Run` ignores ch 5) | [RecompOne] `Dma.cs`; [driver] `install_read_handler(5, namcos12_rom_read)` |
| MDEC | **needed** (FMV in attract mode): RecompOne has `Mdec`; behaviour **not validated** | [probe] MDEC cmd `0x3000xxxx`, DMA0/DMA1 |
| Root counters | **must change**: wall-clock model, IRQ-at-target without reset is wrong; game uses counter 1 (hblank) | [probe] mode 0x0112; [RecompOne] `Timers.cs` |
| VBlank / frame pacing / IRQ delivery | **must change**: wall clock + PS1 BIOS HLE (`BiosB`, intr env) | [RecompOne] `FrameClock.cs`, `Interrupts.cs`; [probe] |
| GTE | **valid as is** (same COP2 in CXD8661R); not differentially tested | [probe] cpu state exposes GTE regs on `cxd8661r`; [psxcpu] one shared GTE |
| Lightgun | not needed for tektagt | [driver] only ptblank2/golgo13-type drivers use guns, and via I/O ports, not the GPU |

## 2. Hardware facts

### GPU chip and board  [driver]
* tektagt = `coh700b` = `coh700` + boot hack. CPU `CXD8661R` @ 100 MHz, GPU **`CXD8654Q`** @ `100 MHz / 2`, `set_vclkn(53.693175 MHz)`.
* VRAM: `RAM(config, m_gpu_ram).set_bits(16).set_default_size("2M")` = 2 x KM4132G271Q SGRAM. (COH-716 board: `CXD8561CQ`, same 2 MB.)
* Main RAM 4 MB (`coh700`), so tektagt does not hit the 16 MB corner cases.
* No Namco-specific GPU modification exists in the driver. The board has an MC44200FT video DAC; it is not emulated and nothing in the driver touches it.
* `screen_vblank` is wired to the sound CPU (`namcos12_sub_irq`: H8/3002 IRQ1, ADC trigger, port B bit 7) in addition to the GPU's own INT0 to the main CPU.

### GPU "type" behaviour  [psx.cpp]
`psx_gpu_init`: `CXD8538Q` -> type 1, **everything else (including CXD8654Q and the retail CXD8514Q) -> type 2**. Type 2 layouts:
draw area Y at bit 10 (10 bit), draw offset Y at bit 11 (11 bit), display start Y at bit 10 (10 bit), CLUT Y `(clut >> 6) & 0x3ff`,
GP0(E1): `tx = (tpage & 0xf) << 6`, `ty = ((tpage & 0x10) << 4) | ((tpage & 0x800) >> 2)` (+256, +512), bits 12/13 = rect flips,
GPUSTAT = `(old & 0xffff7800) | (tpage & 0x7ff) | ((tpage & 0x800) << 4)` (bit 11 shows at bit 15).
All VRAM accesses wrap with `& 1023` in both axes (`p_p_vram[1024]`); the VRAM height is `ram_size / 2048` rows (1 MB -> 512, 2 MB -> 1024).
`GP1(10h)` index 7 returns the GPU type (2). RecompOne returns 0 there (default branch of `SetGpuInfo`): harmless unless the game reads it [inference].
Whether real silicon decodes bit 11 exactly this way is **not verified**; MAME's behaviour is what tektagt was shown to run correctly on.

### VRAM use by the game  [probe]
`hw_usage.lua`, 7000 frames from a fresh boot, one sample per frame (so a lower bound):

| first frame | display (MAME screen) | notes |
|---|---|---|
| 1 | 256x240, 15 bit | boot |
| 375 | 640x240 | early screen |
| 474 | **512x240**, 15 bit, display start Y 0..0xf0 | game / attract (3705 sampled frames) |
| 643 | **256x240, 24 bit**, display start Y **0x200 / 0x2f0** | FMV (MDEC); 2822 sampled frames |

No interlace, no PAL, no 480-line mode and no 368/384/320 widths were observed. GPUSTAT bit 15 (tpage bit 11) set from frame 514 (with bit 4: Y base 768)
and from frame 4711 (Y base 512). Draw area during FMV: Y 0x2fa..0x3dd, draw offset Y 0x2f0 (the lower VRAM half is the FMV double buffer).
GPU DMA direction register always 2 (CPU -> GPU) at frame boundaries.

### Refresh rate / vblank  [probe] + [psx.cpp]
* Emulated frame rate in the 512x240 mode, measured over frames 600..1200 of a fresh boot: 10.029068 s / 600 = **59.826098 Hz** (reproducible with a frame-done callback that reads `machine.time`).
  The Lua `screen.refresh` property prints `60.0000` for the whole run; that is the configured default, not the real rate.
* [psx.cpp] `updatevisiblearea`: NTSC 240-line 59.8260978565 Hz, NTSC 480-line 59.9400523286 Hz, PAL 50 Hz (comment: "not exactly 50"), all derived from 53.693175 MHz.
  I reproduced 59.8260978565 as `53.693175e6 / (3412.5 * 263)` and 59.9400523286 as `/(3412.5 * 262.5)` [derivation, matches to 10 digits].
* So: use the mode-derived rate (59.826 Hz for 240-line), **not** a hard-coded 60 Hz and not RecompOne's `Pal ? 50 : 60`.
* Not verified: where inside the frame INT0 fires (MAME raises it at vblank start of its own screen timing; whether this equals real hardware is unknown).

## 3. DMA

### What the game does  [probe] (last CHCR seen at frame ends, bit 24 already cleared)
| channel | CHCR (with start bit) | meaning |
|---|---|---|
| 2 | `0x01000401` | linked list to GPU (4033 sampled frames) |
| 2 | `0x01000201` | block (sync mode 1) RAM -> GPU image upload (2593 frames) |
| 0 | `0x01000201` | RAM -> MDEC in |
| 1 | `0x01000200` | MDEC out -> RAM |
| 6 | `0x11000002` (seen `0x00000002`) | ordering-table clear |
| 2 read (`0x01000200`) | **not seen** | VRAM -> RAM was not observed (still implemented in RecompOne) |
| 5 | **never non-zero at a frame boundary** | the ROM DMA is not visible by this sampling; does not prove it is unused |

[psxcpu] `dma.cpp` only accepts exactly `0x01000000/0x01000200/0x01000201/0x01000401(ch2)/0x11000002(ch6)` (+ two CD patterns). The values the game uses are all in that set.
DICR values seen: `0x00840000` (master enable + ch2 enable), `0x84840000` (ch2 completed + master flag). IRQ mask register: `0x009` early, `0x029` from frame 472 on
= INT0 (vblank), **INT3 (DMA)** and INT5 (root counter 1).

### RecompOne compatibility  [RecompOne] + [test]
* `Dma.TransferGpu` handles: linked list (`sync == 2`, end on `next == 0xFFFFFF` **or bit 23**), block/burst write (`chcr & 1`, `size * blocks` words), and read (`ReadData`). [test] all three produce the expected VRAM/RAM contents against `SoftwareGpuBackend`.
* Differences from MAME that matter:
  1. **Completion is instantaneous and the IRQ is delivered inside the CHCR write.** MAME defers `DICR`/IRQ3 by 500 sysclk ticks after a list (16000 after > 65535 words) and keeps CHCR bit 24 set until then. The game enables ch2 DMA IRQs (see DICR above), so a synchronous handler call before the kick routine returns can reorder the game's own busy-flag logic [inference]. Recommended: queue the IRQ and clear bit 24 on the next scheduler tick.
  2. Linked-list end test uses bit 23 (matches common hardware descriptions); MAME ends only on `0xFFFFFF` plus loop guards. Same for 4 MB RAM; wrong for a 16 MB COH-716 board (addresses >= 8 MB), irrelevant for tektagt.
  3. `Dma.Run` masks list addresses with `Runtime.RamWordMask`, driven by `Runtime.RamSize` (`internal set`, default 2 MB retail). Must be 4 MB for the arcade.
  4. Channel 5 is not implemented (`default: return`) and `Complete(5)` never runs. [driver] `namcos12_rom_read` (main-ROM vs banked-ROM selection by `exp_base == 0x1f300000` / offset bit 31, tektagt's second offset register) belongs to the memory/protection layer, not to this document.
  5. The DMA enable bits in DPCR are not checked (MAME checks `m_dpcp` bit 3 + 4*ch).

## 4. Timers, IRQ, vblank pacing

[probe]: only root counter 1 is programmed: mode `0x0112` (source = hblank, IRQ on target, **no** reset-on-target, no repeat), target rewritten every frame
(`0x102` 4606 frames, `0xff`, `0x100`, `0x104`, `0x103`). Counters 0 and 2 are never written in 7000 frames.
[probe] unit check: counter `n_start` advances 835 756 units per 16.715 ms frame = 50.0 MHz units. Therefore MAME's S12 root-counter time base is 50 MHz (not 33.8688 MHz);
its hblank source (`/2150`) ticks at 23.26 kHz = 388.7 counts per frame. The game's targets (255..259) look like "one count per scan line, ~263 lines per frame" [inference];
real hblank is 15.734 kHz. I did **not** determine which of the two is right for real hardware. RecompOne's `HblankHz = 15780` is within 0.3 % of 15.734 kHz.

RecompOne `Timers` problems for this game [RecompOne]:
1. Wall-clock (`Stopwatch`) driven, not tied to emulated time: not deterministic, not usable for testing.
2. `Period()` is `target + 1` only if mode bit 3 (reset at target) is set, otherwise 65536. The game uses IRQ-at-target **without** reset, so the IRQ would fire after a full 16-bit lap (about 4 s at 15.78 kHz) instead of at the target. This is a functional bug for tektagt.
3. No mode-register status bits (reached target / 0xFFFF), no sync modes. The game does not appear to use them, but it was not checked per read.

RecompOne `Interrupts`/`FrameClock` [RecompOne]: vblank is generated from a wall clock with `Sdk.LibGpu.Pal ? 50 : 60`; delivery goes through the PS1 BIOS HLE
(`BiosB.DeliverIrqEvents`, `IntrEnvInInterruptAddr`, stack `0xE000`) and `Callable()` accepts handler addresses only in `0x10000..0x1FFFFF`.
The tektagt image is `0x398350` bytes at `0x80010000` (up to about `0x3A8350`, STATUS.json), i.e. above 2 MB, so handlers could be rejected. The arcade needs a
deterministic cycle/frame scheduler with INT0 at the mode-derived rate, INT3 (DMA, deferred), INT5 (counter 1) and a vblank fan-out to the H8/3002 (IRQ1).
This is outside the GPU adapter and is not implemented here.

## 5. MDEC and GTE

* MDEC: [probe] the intro/attract FMV runs from frame 643: MDEC control `0x60000000` (enable in/out) and command `0x60000000` (scale table load), then 3550+ sampled frames with
  `0x300005a0`-style words (`0x3000xxxx`: decode macroblocks, depth bits 28:27 = 2 = **24 bit**, length in the low 16 bits, 0x5a0..0x3dc0 words) and the 24 bit result uploaded to VRAM rows >= 512.
  A MAME snapshot at frame 1500 (24 bit, display start Y 0x200) shows a decoded FMV frame. RecompOne has `Hardware/Mdec.cs` (368 lines) with DMA 0/1 hooks; whether its IDCT/colour output matches MAME is **not validated**. MAME models `mdec` for every `CXD8661R`.
* GTE: [probe] the `cxd8661r` device exposes the full COP2 register set (`irgb orgb sxy0.. ofx ofy h dqa dqb zsf3 zsf4 flag` ...); [psxcpu] one GTE implementation is shared by all PSX CPUs, no arcade variant.
  RecompOne's `Gte` is a static class bound to its recompiled CPU context; nothing System 12 specific is needed. **Not validated** by a differential test against MAME in this work.

## 6. What was built (all under `src/System12.Runtime/Gpu/`, namespace `System12.Runtime.Gpu`, no csproj)

| file | purpose |
|---|---|
| `System12GpuAdapter.cs` | `System12GpuProfile` (constants with sources), `ArcadeVideoMode.FromGp1Mode` (MAME's decode + refresh table), `System12GpuAdapter` (creates `Gpu`, installs the software backend as `GpuHle.Backend`, reports `ArcadeVramSupported`, tracks GP1(08)) |
| `SoftwareGpuBackend.cs` | headless `IGpuBackend`, 1024x1024 VRAM, flat/gouraud/textured (4/8/15 bit, CLUT, tpage Y +256/+512, texture window), semi-transparency modes, mask bit, lines, rect, copies, display read-out (15 and 24 bit, MAME pal5bit expansion) |
| `System12GpuSelfTest.cs`, `System12DisplayTool.cs` | self test / offline display conversion (no `Main`) |
| `tools/run_selftest.sh`, `tools/compare_display.py` | build + run harness, MAME snapshot comparison |
| `mame/vram_dump.lua`, `mame/hw_usage.lua` | MAME probes |

**Limits of the adapter.** It does not and cannot change RecompOne's VRAM height (a `const`); that needs `patches/0004-gpu-vram.md`. `GpuHle.Backend/Active` are process-wide statics,
so only one adapter can be active. The software rasteriser is approximate: no dither, affine texture mapping, simplified edge rules, no PGXP use.

## 7. Validation status (honest)

Passed [test], synthetic input only: 36 checks on an unpatched RecompOne (fill, flat/gouraud triangle, rect, A0/C0 round trip, VRAM copy, 15 bit and 4 bit CLUT textured rect, semi-transparency, DMA2 linked list / block / read, 4 display modes, refresh table);
43/43 on a RecompOne built with patch 0004 + `-p:S12Vram=1` (adds fill/load/read at Y 600/700, tpage Y base 512 and 768, GPUSTAT bits, display start Y 0x2f0). The patched tree built without the define reproduces the unpatched results exactly.
On the unpatched tree, 5 ARCADE checks fail by design (XFAIL) and 2 pass by aliasing (XPASS): the Y-768 texture upload wraps to row 256, and tpage bit 11 sets "texture disable" which shows in GPUSTAT bit 15 by coincidence. Neither is evidence of arcade support.

Compared with MAME [test]: `vram_dump.lua` dumped MAME's real 2 MB VRAM at three frames; `SoftwareGpuBackend.ReadDisplayRgba` was run on those dumps and compared with MAME's own screen snapshot (`compare_display.py`,
which re-creates MAME's `update_screen` border/offset rules):

| frame | content | mode | pixels compared | mismatches |
|---|---|---|---|---|
| 1500 | FMV, VRAM rows >= 512 | 256x240 24 bit | 61 440 | 0 |
| 3000 | attract gameplay | 512x240 15 bit | 122 880 | 0 |
| 5200 | ranking screen | 512x240 15 bit | 122 880 | 0 |

This validates only the **display read-out** (VRAM layout, 15/24 bit decode). It does **not** validate rendering: no GP0 command stream from MAME was captured, so triangles/textures drawn by `SoftwareGpuBackend`
or by RecompOne's GL backends have never been compared with MAME pixels. Not validated at all: MDEC output, GTE results, timer/IRQ timing, the GL backends, DMA IRQ ordering, PGXP.

## 8. MAME probes

Both scripts work in MAME 0.264 with `-video none -sound none -nothrottle -seconds_to_run N` (N is emulated seconds; the script exits MAME itself). Run in a scratch directory; outputs are derived from game data and must not be committed.

```sh
S12_OUT=/tmp/s12 S12_FRAMES=1500,3000,5200 S12_SNAP=1 \
/usr/games/mame tektagt -rompath input/roms -video none -sound none -nothrottle -seconds_to_run 4000 \
    -autoboot_script src/System12.Runtime/Gpu/mame/vram_dump.lua \
    -nvram_directory ./nvram -cfg_directory ./cfg -snapshot_directory ./snap
```
* VRAM access: `emu.item(manager.machine.devices[":gpu"].items["0/p_vram"])`, `:read_block(0, 2097152)` (item size 2, count 1048576). `device.items` maps name -> id; there is no `:gpu_ram` device in 0.264.
  Other readable GPU items: `n_gpustatus`, `n_displaystarty`, `m_n_displaystartx`, `n_horiz_disstart/end`, `n_vert_disstart/end`, `n_drawarea_*`, `n_drawoffset_*`, `m_n_tx/m_n_ty`, `n_ix/n_iy`. DMA, MDEC, root counters and the IRQ controller are readable the same way (`:maincpu:dma`, `:maincpu:mdec`, `:maincpu:rcnt`, `:maincpu:irq`).
* Screen snapshot: `screen:snapshot(path)` works with `-video none` (PNG written; verified, 256x240 and 512x240).
* Not available through Lua: a trace of GP0 writes. A memory tap on `0x1f801810` would miss DMA-fed commands; capturing the DMA list needs a tap on the CHCR write (`0x1f8010a8`) and reading the list from RAM at that time. That is the suggested next step for a rendering comparison; it was not done.
* The 59.826 Hz figure was measured with `machine.time` deltas over frame-done callbacks (not saved as a script; trivial to redo).

## 9. MAME version caveat
Pinned source `f8f6b22`: VRAM is a separate `ram_device` (`m_gpu_ram`), `set_vclkn` exists. MAME 0.264 binary: no `:gpu_ram` device, VRAM is item `0/p_vram` of `:gpu`, and `screen.refresh` reads 60.0 although the emulated rate is 59.826. Any comparison against the pinned source is therefore a comparison against code that is not exactly what produced the probe numbers, even though all quantities checked agree (2 MB, 59.826 Hz, type-2 register layout visible in `n_drawarea_y1 = 0x2fa`, i.e. 10-bit fields).

## 10. Open questions
1. Real-hardware correctness of GP0(E1) bit 11 = Y +512 and of the S12 root-counter time base (only MAME behaviour was established).
2. Exact INT0 position inside the frame and how the game uses root counter 1 (a raster IRQ? a vblank watchdog?).
3. DMA channel 5 usage pattern (never seen non-zero at frame boundaries).
4. GP0 command stream comparison against MAME (see section 8).
5. COH-716 boards (16 MB RAM, `CXD8561CQ`) are not covered by any of this.
