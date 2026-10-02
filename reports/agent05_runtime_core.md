# agent05 - System12.Runtime core (memory bus, hook interfaces)

Scope owned: `src/System12.Runtime/System12.Runtime.csproj`, `src/System12.Runtime/Core/`, `src/System12.Runtime.Tests/`, this report,
`patches/0003-memory-cast.{md,patch}`, and a section in `NOTICE`. Nothing was committed or pushed. No ROM was downloaded; the only game data
touched is the user's existing `generated/regions/maincpu_rom.bin`, read (never embedded) by one optional test.

## 1. What exists now

| file | purpose |
|---|---|
| `Core/IMmioDevice.cs` | **hook interface for other agents** (`IMmioDevice`, `MmioRange`, `MmioDeviceBase`, `NotImplementedMmioException`, `IMmioTrace`) - written first |
| `Core/System12Memory.cs` | `sealed class System12Memory : IMemory`: the physical bus of the CXD8661R main CPU |
| `Core/PsxDma.cs` | DMA controller (7 channels, DPCR, DICR) with pluggable per-channel handlers (`SetToRamHandler(ch, ..)`, `SetFromRamHandler`) |
| `Core/IrqController.cs` | I_STAT / I_MASK, `Raise(source)`, `OnLineChanged` |
| `Core/IGpuPort.cs` | `IGpuPort` (GP0/GP1/GPUREAD/GPUSTAT), a throwing default, adapter `RecompOneGpuPort` for RecompOne's PS1 `Gpu` |
| `Core/BusErrorException.cs` | `BusErrorException`, `AddressErrorException`, `UnimplementedPolicy {Throw, LogAndContinue}` |
| `System12.Runtime.csproj` | net10.0, ProjectReference to `/home/user/work/recompone/RecompOne.Runtime`, globbing `**/*.cs` |
| `src/System12.Runtime.Tests` | xUnit, 63 tests, no game data needed |
| `patches/0003-memory-cast.{md,patch}` | recompiler patch + analysis of the `(PSMemory)m` cast |

### Plugging devices in (for the Banked / Input / Sound agents)

    class MyDev : MmioDeviceBase {                       // every access not overridden throws "NOT IMPLEMENTED"
        public override string Name => "EepromIo";
        public override IReadOnlyList<MmioRange> Ranges => new[] { MmioRange.FromLength(0x1f140000, 0x1000) };
        public override ushort ReadU16(uint offset) { ... }   // offset = physical - range.Start
    }
    mem.MapDevice(new MyDev());                          // throws ArgumentException on any overlap
    mem.Dma.SetToRamHandler(5, (mem, ramOffset, words, chcr) => rom.DmaRead(mem.RamSpan, ramOffset, words));
    mem.Irq.Raise(0);                                    // VBLANK; Irq.OnLineChanged -> CPU Cause.IP2
    mem.Gpu = new MyGpuPort();                           // IGpuPort

Devices see physical offsets after kuseg/kseg0/kseg1 folding. They may live anywhere in 0x1f000000-0x1fbfffff and in the not-yet-implemented
holes of the hardware-register page (SIO 0x1f801040-5f, CD 0x1f801800, SPU 0x1f801c00-ff, 0x1f802000+). `MapDevice` refuses ranges that overlap
RAM, ROM, scratchpad, IRQ, DMA, root counters, GPU/MDEC ports, or another device. The Banked agent's `BankedRomController` / `PsxDmaChannel5`
are not yet wired to this API (they model DPCR/DICR themselves); the thin adapter is `MmioDeviceBase` for 0x1f000000/0x1f700000/0x1fa00000 plus
`Dma.SetToRamHandler(5, ...)` - note `PsxDma` already owns DPCR/DICR and raises IRQ3, so their `PsxDmaChannel5` register logic should be dropped, not duplicated.

## 2. Memory map implemented (and its evidence)

Primary evidence: MAME sources at the pinned commit f8f6b22 - `src/devices/cpu/psx/psx.cpp` (`update_ram_config`, `update_rom_config`, `psxcpu_internal_map`,
`update_scratchpad`, `cache_*`), `irq.cpp`, `dma.cpp`, `rcnt.cpp` (fetched read-only with curl from the pinned commit; copies with original headers kept in `reports/refs/psxcpu/`) and the already-present
`reports/refs/namcos12.cpp` (`coh700` RAM `"4M"`, `maincpu_map`, `tektagt_state::maincpu_map`, `namcos12_rom_read`). Cross-check with the project's own
evidence: STATUS.json facts (4 MB RAM, stack 0x803fffc8, ROM 4 MB at 0x1fc00000, entry word at ROM+0x2401c = 0x80010000) - the last is verified through the bus
by test `RealRom_IfPresent_BootFactsHold` (it ran against the real `maincpu_rom.bin`: passed).

| item | implementation | evidence |
|---|---|---|
| RAM size | 4 MB default (`System12Memory(ramBytes)`, 1..16 MB power of two; COH-716 would be 16 MB) | `namcos12.cpp: RAM(config, m_ram).set_default_size("4M")` in `coh700` |
| RAM window / mirroring | **not fixed**: set by RAM_SIZE (0x1f801060 bits 8-11). Reset value 0x800 gives a **2 MB** window; nibble 3 -> 4 MB (zn2/System 12), 0xf -> 16 MB (System 10/12), RAM repeats inside the window, anything beyond is a bus error | `psx.cpp update_ram_config`, `device_reset` (`m_ram_config = 0x800`) |
| segments | kuseg 0x0, kseg0 0x8, kseg1 0xa alias 512 MB physical; 0x20000000-0x7fffffff and 0xc0000000-0xfffdffff are bus errors | `psx.cpp psxcpu_internal_map` (`berr_r/berr_w`) |
| main ROM | 4 MB image loaded **at runtime** (`LoadMainRom`/`LoadMainRomFile`, power of two <= 4 MB, never embedded) at 0x1fc00000 (any segment). Visible window set by ROM config 0x1f801010 (reset 0x00130000 = 512 KB); image repeats inside the window; beyond it a bus error; writes are NOT IMPLEMENTED (flash commands not modelled) | `psx.cpp update_rom_config`, `m_rom_config = 0x00130000` |
| scratchpad | 1 KB at 0x1f800000, only reachable through kuseg/kseg0, only when BIU (0xfffe0130) has RAM(0x08)|DS(0x80); RAM-only: reads fault, writes dropped; 0x1f800400-0xfff bus error | `update_scratchpad`, `psxcpu_internal_map` |
| BIU / cache | 0xfffe0130 register; icache/tag/dcache model used only while `CacheIsolated` (SR.IsC) is set | `cache_readword/cache_writeword`, `update_memory_handlers` |
| memory control | 0x1f801000 (exp base, forced 0x1f......), 08 (exp config & 0xaf1fffff), 10 (ROM config), 20 (COM delay), 60 (RAM size), plain-RAM registers 04/0c/14-1f/24-2f/64-6f and 0x1f802020-33 | `psxcpu_internal_map` |
| IRQ | own `IrqController` (see validity below); I_STAT write follows MAME: `stat = (stat & ~lane) | (stat & mask & data)` | `irq.cpp write` |
| DMA | own `PsxDma`: ch0 MDEC-in, ch1 MDEC-out, ch2 GPU (block / linked list / GPUREAD), ch6 OTC built in; ch5 (Namco ROM DMA) and any other channel via handler hooks; DPCR/DICR semantics as reference; transfers run synchronously when CHCR bit 24 is written and DPCR enables the channel; burst size = whole BCR word, block size = size x count | `dma.cpp`; `namcos12.cpp install_read_handler(5, ...)` |
| root counters | RecompOne `Timers` instance, registers 0x1f801100-2f | see validity |
| MDEC | RecompOne `Mdec` instance at 0x1f801820/24 | `psx.cpp device_add_mconfig` creates `PSX_MDEC` |
| GPU | `IGpuPort` at 0x1f801810/14 (32-bit only), default throws | `psx.cpp gpu_r/gpu_w` -> `m_gpu_*_handler` |
| arcade MMIO | **no device inside Core**; unmapped addresses throw `NotImplementedMmioException` that names the missing device from the `namcos12.cpp` map (bankoffset 0x1f000000, nopw 0x1f008000/10000/18000/1bff08, sharedram 0x1f080000-3fff, AT28C16 0x1f140000-fff, dmaoffset 0x1f700000-ffff, banked ROM 0x1fa00000-1fbfffff incl. tektagt protection at 0x1fb00000/0x1fb80000) | `namcos12.cpp maincpu_map`, `tektagt_state::maincpu_map` |

### Validity of the PS1-compatible blocks already in RecompOne.Runtime

| RecompOne class | used? | reason |
|---|---|---|
| `Mdec` | **yes** (instance) | pure class, no host/static dependencies; CXD8661R's MDEC exists in the reference (`PSX_MDEC`) |
| `Hardware/Timers` | **yes, with a caveat** | register layout is right (`PSX_RCNT` exists), but its time base is the host wall clock, so counter values and IRQ timing are non-deterministic and counter sources (dot clock/hblank) are rough approximations. Fine for bring-up, replace before accuracy work. IRQs are raised by `PollRootCounters()` |
| `Interrupts` (static) | **no** | it is the PS1 BIOS HLE: BiosB event tables, `Callable()` rejects RAM >= 2 MB, exception stack 0xE000, host `FrameClock`. System 12 runs its own boot ROM and exception handler. Replaced by `IrqController` |
| `Memory/Dma` | **no** | needs concrete `Gpu`, `Spu`, `Mdec`, uses `Runtime.RamWordMask` (static, set by PSMemory), has no ch5, SPU/CD channels are PS1-only. Replaced by `PsxDma` |
| `Gpu` | **adapter only** | `RecompOneGpuPort` exists, but it models 1 MB PS1 VRAM; System 12 has 2 MB (`gpu_ram "2M"`); the Gpu agent decides |
| `Sio0` | **no** | PS1 pad/memory-card protocol bound to `Runtime.CardA/B`; the reference leaves System 12 JVS on SIO1. 0x1f801040-5f throw NOT IMPLEMENTED until an input device is mapped |
| `Spu`, `CdController`, `XaAudio` | **no** | System 12 has no PS1 SPU/CD (sound = C352 on the H8 board). The reference leaves both handlers unset. SPU/CD registers and DMA ch3/ch4 throw NOT IMPLEMENTED; `SetCd` throws |
| `PSMemory` | **no** | sealed, PS1-specific (see section 3) |

## 3. The `(PSMemory)m` cast - decision

Generated code: `var mem = (PSMemory)m;` (`FunctionEmmiter.cs:111`). `PSMemory` is sealed and builds PS1 hardware in its constructor, so `System12Memory`
can neither derive from it nor hide inside it (the cast would still fail; test `PSMemoryCast_FailsForSystem12Memory_...` demonstrates the
`InvalidCastException`). Chosen approach: **keep the cast, make its type configurable** - `"memoryType": "System12.Runtime.Core.System12Memory"` in the recompiler
config (3-line patch `patches/0003-memory-cast.patch`, default unchanged, applies cleanly after 0001 and 0002). Verified end to end: the patched
recompiler emitted `(System12.Runtime.Core.System12Memory)m` for a synthetic image, the output compiled against `Core/*.cs`, and the generated function ran
correctly. A no-patch alternative (`sed` on the generated tree, or `var mem = m;`) is documented in `0003-memory-cast.md`, with a micro-benchmark: without
JIT guarded devirtualisation the sealed-class cast was roughly 2x faster than interface dispatch (noisy; 34-92 vs 106-184 M accesses/s).

## 4. Unimplemented behaviour policy (no silent success)

* Every unmapped/unimplemented access throws `NotImplementedMmioException` ("NOT IMPLEMENTED: <device> read32 @0x... - <what to implement>") or
  `BusErrorException` ("BUS ERROR: ..."), annotated with the recompiled function found on the stack (`[guest func_80012345]`; the .NET stack trace
  lists the full generated call chain as well).
* `System12Memory.Policy = UnimplementedPolicy.LogAndContinue` is an explicit exploration mode: first occurrence of each (device, direction, address) is
  logged, every hit counted in `UnimplementedHits`, reads return 0, writes are dropped. Default is `Throw`.
* Misaligned 16/32-bit accesses throw `AddressErrorException` (MIPS AdEL/AdES) instead of working silently as PSMemory does.
* Deliberately dropped (and counted) writes exist only where the reference map says `nopw`: 0x1f802040 (`NopWriteCount`), and scratchpad writes with only BIU.RAM set.
* Differences from the reference that make us stricter: unknown DMA CHCR combinations and channels without a handler throw (MAME logs and ignores); bus errors throw
  (MAME sets `m_berr` and the CPU takes a DBE exception, which recompiled code cannot do from inside a memory call).

## 5. Tests

`cd src/System12.Runtime.Tests && dotnet test` -> see section 7 for the final result line. 63 xUnit tests on a synthetic ROM (distinct word pattern; no game
data): RAM window/mirroring/reset values, segment aliasing and bus errors, misalignment, LWL/LWR/SWL/SWR against an independent bytewise model for all alignments,
ROM window/mirroring/size checks/file loading, scratchpad/BIU gating, cache isolation and tag flush semantics, plain registers with byte lanes, IRQ semantics incl.
the MAME quirk and line callback, root counters, NOT IMPLEMENTED for SIO/CD/SPU/IRQ holes, GPU port, OTC and GPU DMA (linked list, block, GPUREAD), ch5 hook
(masked address, burst word count), unhandled channels, DICR flag/ack, device routing at all widths and segments, overlap rejection, arcade-region naming,
guest-function annotation from a generated-style caller, `LogAndContinue`, and a generated-code-shaped function using the exact member set of the real output.
By default the tests compile `Core/*.cs` directly so a broken sibling folder cannot block them; `-p:S12TestFullProject=true` references the whole project.
One test (`RealRom_IfPresent_BootFactsHold`) reads the user's `generated/regions/maincpu_rom.bin` when present (or `$S12_MAIN_ROM`) and otherwise
prints that it did not exercise game data.

## 6. Build notes for the team

* `System12.Runtime.csproj` globs `**/*.cs`, so new sibling folders are picked up. To keep a broken sibling from blocking a build:
  `dotnet build "-p:S12ExcludeFolders=Sound%3BGpu"` (semicolon-separated, written `%3B` on the command line; removes `<folder>/**` from `Compile`). Used
  successfully earlier while `Sound/H8Decoder.cs` did not compile; at the end of this task the whole project built with no exclusions.
* The ProjectReference points at the absolute path `/home/user/work/recompone/RecompOne.Runtime/RecompOne.Runtime.csproj`; several agents building it
  concurrently share its `obj/`/`bin/` and can collide (build once, or retry).

## 7. Results

* `dotnet build` of `src/System12.Runtime` (Release, whole project incl. sibling folders): Build succeeded, 0 errors (earlier, with a broken `Sound/`, only the
  `S12ExcludeFolders` build succeeded - mechanism verified).
* `dotnet test` in `src/System12.Runtime.Tests` (Core compiled in): **Passed 63, Failed 0, Skipped 0**.
* `dotnet test -p:S12TestFullProject=true` (project reference to the whole `System12.Runtime`): **Passed 63, Failed 0, Skipped 0**.
* Real-data check inside the suite: `RealRom_IfPresent_BootFactsHold` ran against `generated/regions/maincpu_rom.bin` (4 MB) and confirmed the boot-loader entry word
  0x80010000 at ROM+0x2401c through the bus.
* Recompiler patch check: synthetic image -> patched recompiler -> `var mem = (System12.Runtime.Core.System12Memory)m;` -> compiled with Core -> executed (copied 0xdeadbeef).

## 8. Not done / known gaps (honest list)

* No CPU/COP0/exception model: `System12Memory` only exposes `Irq.OnLineChanged` and `CacheIsolated`; nothing connects `mtc0 SR` to them yet, and the generated
  code still calls the PS1 `Interrupts.Poll`, `Bios.Syscall`, `Bios.Break` (see `0003-memory-cast.md`, "Related gaps").
* Whether kseg0/kseg1 aliases exist for the *driver-level* arcade map is not proven by the sources I have (the reference CPU file maps its own registers in all three
  segments, `namcos12.cpp` maps only the kuseg addresses and I could not find where the CPU folds segments). The bus treats all segments as aliases of 29-bit physical
  space, which is how the hardware behaves; if MAME disagrees for some arcade device, a differential run is needed.
* No differential test against a MAME run yet (RAM window/ROM window behaviour is taken from the MAME source, not from a trace of the real boot).
* Root counters are wall-clock based; DMA completes instantly (MAME also, except ch1/ch4/ch6 delays); no DMA timing/priority.
* MDEC/GPU DMA correctness beyond the tested plumbing depends on the RecompOne MDEC and on the Gpu agent's port.
* The Banked/Input/Sound/Gpu agents still have to implement `IMmioDevice` adapters; `Core` contains none of the arcade devices by design.
