# agent06: GPU / GTE / DMA(GPU) / timers / IRQ reuse assessment for System 12 (tektagt)

Details and evidence classes: `docs/system12/GPU.md`. Patch proposal: `patches/0004-gpu-vram.md`.
Nothing was committed or pushed; `/home/user/work/recompone` was not modified (all builds ran on copies in the session scratchpad).

## Answers

1. **GPU type / VRAM.** tektagt = `coh700b`: CPU CXD8661R @100 MHz, GPU **CXD8654Q**, VRAM **2 MB** (`RAM ... set_bits(16).set_default_size("2M")`, driver f8f6b22),
   i.e. 1024 x 1024 x 16 bit, not the retail 1024 x 512. MAME 0.264 exposes it as item `:gpu 0/p_vram` = 1 048 576 halfwords. The game really uses the upper half
   (FMV display start Y 0x200/0x2f0, draw areas Y 0x2fa-0x3dd, texture pages at Y base 512 and 768).
2. **GPU register differences.** MAME treats CXD8654Q as "type 2" (same as retail CXD8514Q): draw area Y @10, offset Y @11, display start Y @10 (10 bit);
   GP0(E1) bit 11 = texture page Y +512 (RecompOne: "texture disable"); CLUT Y 10 bit; all VRAM accesses wrap at 1023. No Namco-specific GPU mods in the driver. Lightgun: not used by tektagt.
3. **Mdec: present and used.** CXD8661R has MDEC in MAME; tektagt plays 24-bit FMV (frame 643+ of a fresh boot, MDEC cmd `0x3000xxxx`, DMA0/DMA1). RecompOne's `Mdec` exists but is unvalidated.
4. **Video timing.** 512x240 non-interlaced NTSC in the game, 640x240 and 256x240/24-bit in boot/FMV. Measured frame rate in MAME 0.264: **59.826098 Hz**
   (Lua `screen.refresh` falsely says 60.0000). MAME formula: 53.693175 MHz / (3412.5 x 263). RecompOne's `Pal ? 50 : 60` wall-clock pacing must go.
5. **GPU DMA.** Game uses ch2 linked list (`0x01000401`) and ch2 block write (`0x01000201`), ch0/ch1 for MDEC, ch6 OT clear; DICR/INT3 enabled for ch2. RecompOne's channel-2 code handles these correctly (tested), but completes and raises the IRQ synchronously
   (MAME defers), ignores DPCR, has no channel 5, and assumes 2 MB RAM.
6. **Timers/IRQ.** Only root counter 1 is used (mode `0x0112` = hblank source, IRQ at target, no reset; target 255..259 rewritten each frame). RecompOne's `Timers` is wall-clock based and fires target IRQs only after a full 16-bit lap unless reset-at-target is set: **functionally wrong for this game**.
   IRQ mask in use: INT0, INT3, INT5. `Interrupts` is PS1-BIOS-HLE based and rejects handler addresses >= 2 MB (tektagt code runs up to about 0x3A8350).
7. **GTE.** Same COP2 in CXD8661R, single shared implementation in MAME, nothing arcade specific. RecompOne's GTE is reusable. Not differentially tested.

## Reuse / change summary

* Reuse as is: GP0/GP1 command decode, GPUSTAT, GP1(08) display-mode decode (matches the four modes MAME reported), GTE, DMA ch2/ch0/ch1/ch6 transfer logic, MDEC code (unvalidated).
* Must change: VRAM height + tpage/display Y decoding (patch 0004, opt-in define), GL backends (512-row assumptions; untested), timers, vblank/IRQ model, DMA completion timing, DMA ch5, `Runtime.RamSize`.
* Important finding: RecompOne's `Gpu` has **no rasteriser**; it only forwards primitives to an `IGpuBackend` (GL). A headless backend was therefore required (written: `SoftwareGpuBackend`).

## Deliverables (all under `src/System12.Runtime/Gpu/` unless noted)

* `System12GpuAdapter.cs` (profile constants with sources, `ArcadeVideoMode`, `System12GpuAdapter`), `SoftwareGpuBackend.cs`, `System12GpuSelfTest.cs`, `System12DisplayTool.cs`
* `tools/run_selftest.sh`, `tools/compare_display.py`, `mame/vram_dump.lua`, `mame/hw_usage.lua`
* `docs/system12/GPU.md`, `patches/0004-gpu-vram.md` (with `git apply`-checked diff), this report
* MAME references fetched into `reports/refs/` (git-ignored): `psx.cpp`, `psx.h`, `psxcpu_{dma,irq,mdec,rcnt,psx}.cpp`, `psxcpu_dma.h`.

The adapter could **not** be done without forking: VRAM height is a `const` and GP0 decoders mask Y with 0x1FF, so the adapter can only detect and report it (`ArcadeVramSupported`).

## Validation (what was and was not checked)

Run with `src/System12.Runtime/Gpu/tools/run_selftest.sh <RecompOne.Runtime dir> [patched]` (copies the tree, builds a throwaway console project):

| RecompOne tree | result |
|---|---|
| unpatched @8d0091c | 36 pass, 0 fail, 5 xfail (arcade rows unreachable, expected), 2 xpass (aliasing; not evidence) |
| patch 0004 applied, built without define | identical to unpatched |
| patch 0004 applied, `-p:S12Vram=1` | **43 pass, 0 fail** |

Synthetic checks: GP0 fill, flat and gouraud triangle, rect, A0/C0 round trip, VRAM copy, 15-bit and 4-bit CLUT textured rect, semi-transparency, DMA2 linked list / block / read through the real `RecompOne.Memory.Dma`, display-mode decode vs the modes MAME reported, and (patched only) fill/load/read/texturing at rows >= 512.

Compared with MAME (real data): MAME's 2 MB VRAM dumped at frames 1500 (24-bit FMV, rows >= 512), 3000 and 5200 (512x240 15-bit), converted by `ReadDisplayRgba` and compared with MAME's own screen PNG:
**0 mismatching pixels out of 61 440 / 122 880 / 122 880.**

**Not validated:** rendering of real GP0 streams (no command trace from MAME was captured), the GL backends (no GL here), MDEC output, GTE, timer/IRQ timing, DMA IRQ ordering, PGXP.
Real-hardware correctness is established only as far as MAME is correct; the S12 root-counter time base (MAME: 50 MHz units, 23.26 kHz hblank) looks inconsistent with the game's own targets and was left unresolved.
`SoftwareGpuBackend` is an approximate test rasteriser (no dither, affine texturing), not a reference renderer.

## Notes for other agents
* Sound/IRQ: GPU vblank must also drive the H8/3002 IRQ1 (+ADC trigger, port B bit 7) (`namcos12_sub_irq`).
* MAME 0.264 vs the pinned source differ internally (VRAM device layout, refresh property); numbers used here come from the 0.264 binary.
* `GpuHle.Backend/Active` are process-wide statics.
