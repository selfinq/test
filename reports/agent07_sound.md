# agent07 - sound subsystem (H8/3002 + C352)

Deliverables (all new; nothing committed or pushed; no ROM downloaded; MAME BSD sources only read into git-ignored `reports/refs/`):

* `src/System12.Runtime/Sound/C352.cs` - C352 mixer (port of MAME `c352.cpp`, BSD-3 header retained)
* `src/System12.Runtime/Sound/H8Decoder.cs`, `H8Cpu.cs`, `H83002.cs` - H8/300H interpreter + H8/3002 peripherals (declared emulated component)
* `src/System12.Runtime/Sound/S12SoundBoard.cs` - board glue (shared RAM, C352 window, inputs, RTC, vblank, lazy audio generation)
* `src/System12.Runtime/Sound/SoundSelfTests.cs` - 500 ROM-free checks + 4-check firmware regression
* `src/System12.Runtime/Sound/tools/` - MAME Lua capture scripts, compare scripts, harness source (`SoundTool/Program.cs.txt`, `.csproj.txt`, deliberately not `.cs`)
* `docs/system12/SOUND.md` - architecture, decision, API, verification, limitations

No csproj was touched (another agent owns it); the files compile as plain C# 12 / net10 with namespace `System12.Runtime.Sound` (built through a scratch csproj: 0 errors).

## Decision

Option (a): embed an H8/3002 interpreter that executes the unmodified `sub.bin`, declared an emulated component in every file header. HLE of the sequencer
is not done. Rationale in SOUND.md section 2: most of the ROM is data entered through interrupts/serial/shared RAM, timing is observable, the peripheral model is the
bulk of the work either way, only 63 mnemonics are executed, and the interpreter runs the 40 s attract session (116.9 M instructions) in about 7 s of CPU time on the dev container
(not a rigorous benchmark). A static recompile (b) is left as an optional optimisation with the interpreter as its oracle.

## Implemented vs not

Implemented
* C352: all 32 voices, 8-bit linear and mu-law, noise LFSR, loop / ping-pong / link / reverse / filter, phase flags, volume ramping, key-on/off, register file, 88.2 kHz 4-channel generator (System 12 uses front L/R).
* H8/300H: decoder + disassembler (MAME syntax), interpreter for the full H8/300H advanced-mode set (except below), MAME-exact state counts, interrupt entry/RTE, I/UI masking.
* H8/3002 peripherals: INTC (IRQ0-5, NMI, ICR priorities), ITU0-4 (compare/overflow/timing), SCI0/1 byte level (async + sync), A/D (software + ADTRG start), WDT, ports, on-chip RAM/registers.
* Board: 64 KiB shared RAM with the main-CPU lane convention (`MainRead32/MainWrite32`), C352 window, IN0/IN1, DIP port, RTC4543 on SCI1, vblank (IRQ1 + ADTRG + port B bit 7), lazy 192-states-per-frame audio generation.

Not implemented / not claimed
* JVS bus (SCI0 TX discarded), settings device (sink), RTC write mode; DMAC, ITU capture/PWM/phase/external clock, standby, refresh (register store only).
* H8S-only opcodes, movfpe/movtpe, normal mode, illegal-instruction exceptions (CPU halts), byte push/pop with ER7 adjustment.
* Host audio output / resampling.
* Meaning of the individual sound-command words in shared RAM (no function-level reverse engineering of the sequencer was done).
* Hardware-level accuracy: every fidelity statement means "identical to MAME 0.264 for tektagt".
* A static recompiler of sub.bin.

## Evidence

| Check | Oracle | Result |
|---|---|---|
| Disassembly of whole sub.bin | MAME `dasm` linear sweep | identical at 255,820 positions |
| CPU instruction replay | MAME 0.264 debugger trace (regs, CCR, total cycles) in several windows, ~5 M instructions | registers, CCR, PC and per-instruction cycles identical |
| Boot | MAME trace | first 225,348 instructions identical in PC and cycles |
| Boot C352 writes | MAME write tap | 1,201 writes identical (address, data, mask), 0-state offset |
| C352 mixer | MAME `-wavwrite` (88.2 kHz) driven by MAME's own 150,709 C352 writes | 3,528,000 / 3,528,000 frames identical (40 s) |
| Standalone boot audio | MAME wav | first 6.2 s bit-exact |
| Full board vs MAME (MAME vblank + main shared-RAM writes) | MAME wav and write log | all 150,708 compared C352 writes equal in address/data/mask and order; 1,292 timing-exact (to 6.96 s); 99.86 % within 192 states afterwards (max 19 ms); audio 94.84 % samples exact (7,055,996 compared), first difference 12.30 s, max 726 LSB |
| Unit tests | `SoundSelfTests` | 500 passed, 0 failed; firmware regression 4/4 |

Coverage: 40 s attract executes 3,453 distinct instruction addresses (all below 0x720d) with 63 distinct mnemonics (all oracle-covered). Opcodes never executed by
this firmware are unit-tested only.

## Uncertainties / things the reader must not over-read

1. The post-6.96 s timing difference is attributed to MAME's multi-CPU scheduler quantum (main-CPU writes become visible to the H8 late). Supporting evidence: the write streams are identical in content and
   order and the mixer is bit-exact given MAME's times; the "visible on next frame" alternative was tested and rejected. The cause itself is a hypothesis; it is not modelled.
2. `H8Intc.MameIcrQuirk` reproduces a byte-write handling bug of MAME 0.264's `icr_w`; the firmware needs it for a bit-identical write stream (`false` = datasheet-style behaviour, unverified on hardware). The firmware regression sets it true.
3. SCI behaviour follows the 0.264 binary (differs from current MAME master); SCI is byte-level.
4. RTC default is a fixed date (2026-10-02 05:08:35); the firmware's control flow depends on the year. `SetRtc` changes it.
5. The C352 model is MAME's reverse-engineered one; ROM address masking to 24 bits, the control register, rear channels are not verified on hardware.
6. Exact historical MAME command line for the captures was not saved; SOUND.md section 7 gives a reconstructed pattern (needs `-samplerate 88200`, empty NVRAM). Results depend on that NVRAM/boot state.
7. The "sequencer" description of the firmware is inferred from ROM text labels and the C352 write stream, not from reverse-engineered functions.

## Integration notes for the runtime owner

* Time base: H8 states = main-CPU seconds x 16,934,400. Call `MainWrite32/MainWrite16` on the main CPU's stores to `0x1f080000..0x1f083fff`, `RunUntil` to the main CPU's time, drain `ReadAudio`.
* The firmware only accepts coins after the main CPU's initialisation writes (see `reports/agent08_input_io.md`).
* Sources are added to the runtime csproj by its owner (glob `Sound/*.cs`; do not glob `Sound/tools/**`: harness files are `.txt`).
