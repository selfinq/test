# Phase 1 audit

Environment: Ubuntu 24.04, x86_64, 4 cores, 15 GB RAM. .NET SDK 10.0.112 via apt (dot.net / builds.dotnet.microsoft.com
were denied by the egress policy and not circumvented; the first `apt install` failed with 404 on stale indexes, fixed by `apt-get update`).
MAME not installed (apt candidate 0.264); `namcos12.cpp` taken from mame master, sha256 dd2b01da...a9e959, HEAD f8f6b22 (fetch 2026-10-02).
Build: `dotnet build RecompOne.sln -c Release` -> 0 errors, 2 CS8602 warnings (log: reports/recompone_base_build.log). Both projects target net10.0.
Windows x64 output not testable here (Linux container); only the build itself was checked.

## Reusable (evidence)
- MIPS decode/emit and function detection: Recompiler/CodeGen/InstructionEmitter.cs, Analysis/FunctionDetector.cs, JumpTableAnalyzer.cs.
- Raw-ELF/function-map path: Config/FunctionMapLoader.cs (textBase, LoadAddress), Elf/ElfReader.cs.
- Runtime: GTE (Hardware/Gte.cs), GPU interpreter/backends (Gpu/), DMA, timers, SPU, controller (Hardware/).
## Missing for System 12 (not reusable as is)
- Input is a PS1 disc: Program.cs requires `config.Cue` and `DiscFs.Open`; OverlayWriter.cs:28 reads SYSTEM.CNF; Psx/Parser.cs expects "PS-X EXE".
  AutoConfigure/CodeScore.cs:124-127 assumes load addresses in 0x80010000..0x80200000.
- Memory: Memory/MemoryMap.cs fixes RAM at 2 MB retail (8 MB devkit), BIOS window 0x1FC00000 (512 KB) and expansion bases; the arcade board needs
  banked ROM windows and the tektagt protection registers (namcos12.cpp: 0x1f700000, 0x1fb00000, 0x1fb80000, `m_has_tektagt_dma`).
- No CD-ROM (the arcade has none), no H8/3002 sound CPU or C352 PCM (the runtime has a PS1 SPU), no JVS/coin/NVRAM/RTC-style board devices, no flash handling.
- The runtime ships PS1 BIOS HLE (Bios/); arcade boot code reads the ROM instead. Whether it can be reused is undecided.
## Unverified
Boot flow, entry address, byte order and bank mapping must come from the driver plus analysis of the real binaries; none is assumed here.

## MAME reference (0.264, apt)
`-listxml tektagt` / `-listroms tektagt` / `-verifyroms` outputs and return codes are in reports/mame_ref/ (rc 0/0/2; the 2 is the expected 'romset not found' with an empty rompath).
-listroms matches tools/tektagt_manifest.json exactly (15/15 size+CRC+SHA-1), so 0.264 and the pinned master source agree for this set.
Devices per -listxml: main CPU Sony CXD8661R @100 MHz, sound CPU Hitachi H8/3002 @16.9344 MHz, Namco C352 @25.4016 MHz.

## Boot analysis (MAME 0.264 Lua traces in reports/trace/*.lua, logs *.txt; dumps stay local)
- Reset at 0xbfc00000, boot code executes from ROM. Frames ~119-366: decompressor at 0x1fc21048 (called from 0x1fc20e90, args from ROM words 0x1fc24010/0x1fc24014)
  writes the game image to 0x80010000 (nonzero RAM grows ~8 KB/frame). At frame 367 PC first lands in RAM; entry word @0x1fc2401c = 0x80010000.
- Bank register 0x1f000000 and protection windows 0x1fb00000..0x1fb80003: 0 write-tap hits in 4000 frames (taps do not see direct-RAM writes, so RAM-write taps are not evidence).
- Banked ROM data is read later via other paths (RAM blocks from bankedroms seen near 0x3eff54, 0x3f3a70) - mechanism NOT yet identified.
