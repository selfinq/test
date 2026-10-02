# Namco System 12 sound subsystem (tektagt)

Scope: how the main CPU, the H8/3002 sound CPU and the C352 PCM chip fit together, what the runtime does about them, and exactly how far
that has been verified. Everything stated as "verified" names its oracle (MAME 0.264 binary run on the user's ROMs, or MAME source at
`f8f6b22c3f328b9e0b04ff502989f8536c0d66c0`). Statements without an oracle are marked **unverified**.

Code: `src/System12.Runtime/Sound/` (namespace `System12.Runtime.Sound`). Report: `reports/agent07_sound.md`.

## 1. Architecture

```
 R3000A main CPU                        sub board ("MOTHER PCB")
 ----------------                       ---------------------------------------------------------------
 0x1f080000..0x1f083fff  <-- shared --> H8/3002 0x080000..0x08ffff   (RAM, 64 KiB on the H8 side)
 (16 KiB window)            RAM          H8/3002 0x000000..0x07ffff   (sub.bin, 512 KiB firmware + sequence data)
 vblank ---------------------------->    IRQ1 (+ ADTRG, port B bit 7)
                                         H8/3002 0x280000..0x287fff   C352 (16-bit registers)
                                         H8/3002 0x300000/0x300002    IN0/IN1 (JAMMA words, active low)
                                         SCI0 -> JVS bus (nothing attached on tektagt), SCI1 -> RTC4543 / settings
                                         C352 -> sample ROM region "c352" (16 MiB) -> front L/R DAC
```

Sources: MAME `namcos12.cpp` (`sub_program_map`, `maincpu_map`, `namcos12_sub_irq`, `s12_mcu_*` handlers, `C352(config,"c352",25401600,288)`).

### 1.1 Main CPU <-> H8 communication

* **Shared RAM only.** There is no command FIFO and no main-to-H8 interrupt: the H8 gets exactly one external interrupt, IRQ1, which is the
  video vblank (`screen_vblank -> namcos12_sub_irq`). All commands are words the main CPU stores into the shared RAM and the firmware polls.
* **Address/lane convention.** The main CPU window is `0x1f080000..0x1f083fff`; MAME's handler (`sharedram_r/w`) indexes the 16-bit RAM
  with the 32-bit bus offset, so the same 16-bit word answers on **both** halfword lanes of a 32-bit access: the main CPU sees the H8 word at
  `0x080000+off` in the low half and the word at `0x080000+off+2` in the high half. (H8 side: big-endian 16-bit words.) Verified from MAME's
  write tap: the main CPU's periodic write is a high-lane store to `0x1f0830f0` (mask `ffff0000`) and the firmware reads/writes `0x0830f2`
  (`mov.w @h'0830f2` at H8 PCs 0x0be2, 0x122a, 0x12b8). `S12SoundBoard.MainRead32/MainWrite32` implement this.
* **Observed shared-RAM traffic** (MAME run, 40 s attract, 1,462 main-CPU writes): `0x1f0830f0` 1,148 times (one per frame, a heartbeat),
  initialisation blocks at `0x1f080100..13c`, `0x1f082010..70`, `0x1f083000..78`, `0x1f083100..13c`, `0x1f083280..2bc`, `0x1f0833c0..3fc`,
  a handful at `0x1f083810..3a00`. The input words the firmware publishes (player/test/coin words at H8 `0x83140/42`, `0x83380`, `0x83900/02`,
  `0x83940/42`) are documented in `docs/system12/INPUT_IO.md`. The *meaning* of the sound-command words in `0x082xxx/0x083xxx` has not been
  reverse engineered (not needed for the interpreter design, and in attract mode only the init blocks and the heartbeat are written).
* **Other signals into the H8:** vblank also drives the A/D converter trigger (ADTRG) and bit 7 of port B (MAME `namcos12_sub_irq`). Port 7
  reads the DIP switches (`0xff`), port 6 = `0xfd | jvssense<<1`, port 8 reads `0x02`, ports A/B select RTC/settings chip-enables
  (RTC CE = portB.5 & portA.0).

### 1.2 What the H8 firmware is (sub.bin, 512 KiB)

Evidence is from the ROM contents and from the executed-PC set of a 40 s MAME attract run (3,453 distinct instruction addresses, 63 distinct
mnemonics); **no function-level reverse engineering was done**.

* Identification string at `0x41b6`: `namco ltd.;System12;Ver2.15;JPN` + `copyrights 1996,1998 NAMCO LTD. ALL RIGHTS RESERVED`; I/O board name
  table at `0x2522..` (`I/O CYBER LEAD`, `Jamma I/O`, `TSS-I/O`, ...) - the firmware also owns the JVS/input side.
* All executed code lies in `0x0100..0x720c` (the first 64 KiB; the code area could extend further, unexecuted); from `0x10000` upward the ROM looks like data (strings found): sound-check menu strings (`Sound Check (Sound Data Ver.419a)`,
  `BEAT_CHECKER`), then per-track sequence data labelled with text (`PAUL STAGE BGM`, `BGM03/STAGE_LAW`, `Character Select`, `ENDING(Long Version)`,
  `BGM19/OPENING_MOVIE`, `RANNYUU_JINGLE`, ...). This is consistent with a **music/SFX sequencer running on the H8**: the main CPU sends
  track/SFX requests, the H8 interprets note/instrument data at its own tick rate and writes C352 voice registers directly. (Inference from
  data layout, the observed C352 write stream, and the absence of any other path into the C352.)
* Vector table: reset `0x100`; non-default handlers for IRQ1 (vector 13 -> `0x40f2`, the vblank handler), vectors 20/21/24/25 -> `0x3fa4`,
  26 -> `0x3fa6`, 28/29 -> `0x403e`, 30 -> `0x4040` (ITU/refresh/WDT group; tiny handlers), SCI0 vectors 52..55 -> `0x36ce, 0x35d2, 0x371c, 0x3736`.
  SYSCR is set to 3 (UE=1: two-level interrupt masking, ICR priorities honoured).
* At boot (about 6.2 s of emulated time in MAME, 1,201 C352 writes, first at H8 state 224,014) the firmware initialises the C352 (a 70-state loop
  clears the voice registers), and reads the RTC over SCI1 (both verified by the standalone-boot trace); that it then exports time and input words to shared RAM follows from the
  shared-RAM traffic and `INPUT_IO.md`; the sound-driver tick runs from the vblank handler and timers (inferred, not traced function by function).

### 1.3 C352

Register window H8 `0x280000..0x287fff`; the chip sees 16-bit **word** offsets (`(addr-0x280000)>>1`); the H8 bus is big-endian so an 8-bit
store to an even address is the high byte (MAME mask `ff00`). The firmware uses both 8- and 16-bit stores (the C352WriteTrace/MAME tap record
`(addr, data, mask)`).

| word offset | meaning |
|---|---|
| `voice*8 + 0` | volume front: high byte = left target, low byte = right target |
| `voice*8 + 1` | volume rear: high byte = rear-left, low byte = rear-right (not wired on System 12) |
| `voice*8 + 2` | frequency (16-bit phase increment; `0x10000` = one ROM sample per output frame, with linear interpolation) |
| `voice*8 + 3` | flags (below) |
| `voice*8 + 4` | wave bank (bits 16..23 of the ROM address) |
| `voice*8 + 5` | wave start (low 16 bits) |
| `voice*8 + 6` | wave end |
| `voice*8 + 7` | wave loop start |
| `0x200` | control register (stored; function unknown, as in MAME) |
| `0x202` | write `0xffff`-masked word: execute pending KEYON/KEYOFF for all 32 voices |

Flags (reg 3): `8000` BUSY, `4000` KEYON, `2000` KEYOFF, `1000` LOOPTRG, `0800` LOOPHIST, `0400` FM (unused), `0200` PHASE_RL, `0100` PHASE_FL,
`0080` PHASE_FR, `0040` LDIR (ping-pong direction), `0020` LINK, `0010` NOISE, `0008` MULAW, `0004` FILTER (disables interpolation),
`0002` LOOP, `0001` REVERSE.

* **Voices:** 32. **Sample formats:** signed 8-bit linear (shifted into the high byte), or 8-bit mu-law-like (MAME's table, "similar to namco's
  VC emulator": 128-entry compressive table, negative half is the complement), or the 16-bit LFSR noise generator (seed `0x1234`, taps `0xfff6`).
* **Volume:** four 8-bit current volumes ramp by +-1 toward the targets each time bit 15 or 16 of the phase counter toggles (2 steps per
  sample at full rate), output = `(sample * vol) >> 8` summed over voices, `>> 3` at the end.
* **Rate:** `25,401,600 / 288 = 88,200 Hz` stereo (front L/R); `= exactly 192 H8 states` per output frame at 16.9344 MHz, so the C352 is
  advanced lazily from the H8 clock (generate frames up to the H8 time before each register access).
* The ROM region `c352` is 16 MiB; addresses are masked to 24 bits (**unverified on hardware**, same as MAME).

## 2. Runtime design decision

Options considered: (a) embed an H8/3002 interpreter executing the real `sub.bin` (declared **emulated component**, not "native"); (b) statically
recompile `sub.bin` to C#; (c) HLE of the sequencer (rejected: not acceptable as "native", and it would need the sequence format and instrument
tables reverse engineered, with nothing but listening to check it).

**Recommendation and implementation: (a).** Evidence:

1. The program is not a self-contained batch of code: 7/8 of the ROM (`0x10000..0x7ffff`) holds what looks like sequence/instrument data (text labels; not parsed) and no code that ran (all code executed in 40 s of attract mode lies below
   `0x10000`), and the code is entered through interrupts, timers, serial peripherals and a shared-RAM mailbox. Static recompilation would still need the whole H8 peripheral model (INTC, ITU, SCI, ADC, ports)
   and exact interrupt-time interaction with the main CPU - the peripherals are the larger part of the work, and recompilation does not remove it.
2. Cycle behaviour is part of the audible/observable behaviour: the C352 register writes happen at specific H8 times, and the shared RAM
   is shared with a live main CPU. The interpreter below reproduces MAME's per-instruction state counts exactly (section 4), so timing-dependent behaviour (RTC reads, ADC conversion,
   timer interrupts) comes out identical; a recompiler would have to reintroduce a cycle counter at every instruction anyway.
3. Executed instruction set is small: 63 distinct mnemonics in 40 s of attract mode, all covered by the oracle traces; an interpreter is
   simple and its speed is sufficient: the 40 s MAME-driven run (116.9 million H8 instructions, 677 million states, plus 3.53 million C352 frames) takes
   about 7 s user time on the development container (JIT warm-up included; not a rigorous benchmark), i.e. roughly 5-6x real time on that machine, one thread.
   A (b) port is therefore an optimisation to be considered only if profiling on target hardware demands it; its test oracle would be the interpreter in this directory.

The interpreter is **declared** as an emulated component in every file header. It executes the unmodified firmware; nothing in `Sound/` re-implements
the sequencer.

## 3. API (how the rest of the runtime uses it)

```csharp
var board = new S12SoundBoard(subBin /*512 KiB*/, c352Rom /*16 MiB*/);
board.Mcu.Intc.MameIcrQuirk = false;        // true only to reproduce MAME 0.264 bit-exactly (see 5)
board.SetRtc(DateTime.Now);                  // optional; default is a fixed date for reproducibility
// per emulated main-CPU time slice (main seconds * 16,934,400 = H8 states):
board.MainWrite32(0x30f0, value, 0xffff0000);  // main CPU stores to 0x1f0830f0.. (offset within the 16 KiB window)
board.RunUntil(h8StatesTarget);              // internal vblank (59.826 Hz) unless ExternalVBlank = true (then call SetVBlank)
board.In0 = ...; board.In1 = ...;            // inputs, active low; read by the firmware, published via shared RAM
int n = board.AvailableAudioFrames;          // stereo 88.2 kHz frames, interleaved short; ReadAudio(...) drains them
```

Notes: the audio output is **88.2 kHz 16-bit stereo, exactly MAME's pre-resampling rate**. Host playback (resampling to the device rate, the
sink) is *not* implemented here. `S12SoundBoard.CaptureAudio=false` skips sample generation if only the register stream matters.
`SoundSelfTests.RunAll(Action<string>?)` runs 500 checks without ROMs; `SoundSelfTests.RunFirmwareRegression(sub, c352, log)` runs the boot
regression with the user's ROMs (needs `MameIcrQuirk` on; the method sets it).

## 4. What exists and how it was verified

| Piece | File | Oracle | Result |
|---|---|---|---|
| C352 mixer | `C352.cs` (port of MAME `c352.cpp`, BSD-3, credit R. Belmont/superctr) | MAME 0.264 `-wavwrite` at `-samplerate 88200`; the C# chip is fed MAME's own C352 write log (150,709 writes, tap on `:sub` space 0x280000-0x287fff) | **3,528,000 / 3,528,000 stereo frames identical (40 s)**, after modelling MAME's float->int16 stage (x32767/32768, truncation), shift +2 frames |
| C352 unit tests | `SoundSelfTests.cs` | synthetic ROM data | mu-law table, register map, keyon/keyoff, linear/mu-law/phase/filter, one-shot, loop, ping-pong, noise LFSR, interpolation, banking, 32 voices |
| H8 decoder | `H8Decoder.cs` | MAME `dasm` of the whole sub.bin (linear sweep) | identical text at all 255,820 positions after the fixes listed in section 6; H8S-only forms/movfpe/movtpe are not decoded |
| H8 CPU | `H8Cpu.cs` | MAME 0.264 debugger trace (pre-instruction ER0-ER7/CCR/PC + total cycles), replayed instruction by instruction | register/CCR/PC and per-instruction cycle counts identical over ~5 million traced instructions in several windows (boot, 1st-4th frames, attract), including interrupt entry (14 states) and reset (6 states) |
| H8 peripherals | `H83002.cs` (INTC, ITU0-4, SCI0/1 byte level, ADC, WDT, ports) | MAME traces of the boot | first 225,348 instructions identical in PC and cycle count; C352 write stream of the standalone boot (6.2 s, 1,201 writes) identical in address, data, mask and time (0-state offset) |
| Board | `S12SoundBoard.cs` | MAME vblank times (`frames.txt`) + main-CPU shared-RAM write log | **all 150,708 C352 writes in the first 40 s match MAME in address, data and mask and occur in the same order**; 1,292 of them (up to 6.96 s) match to the state; afterwards timing differs (99.86 % within 192 states = one audio frame, max 325,682 states = 19 ms) |

Full-board audio (MAME-driven vblank + main shared-RAM writes, 40 s, 7,055,996 samples compared): **94.84 % exact, max |diff| 726 LSB, first
difference at 12.30 s**. The mismatch is a *timing* difference between the sound CPU and MAME's main CPU, not a mixer or decode error (proof: with
MAME's own C352 write times, the mixer output is 100 % identical). Likely cause (hypothesis, partially tested): MAME schedules main and sub CPU with a
scheduler quantum, so main-CPU writes to shared RAM become visible to the H8 later than the logged write time; the obvious alternative (visible on
the next frame) was tested and rejected. This is not modelled. For a native runtime the main CPU's shared-RAM writes land at *its* instruction time, so
the runtime will not be bit-identical to MAME in a closed-loop run, and nothing here claims it should be.

Interpreter coverage: of the 63 mnemonics the firmware executes, all were oracle-tested; instruction forms the firmware never executes are implemented and
checked by unit tests (flag truth tables, addressing modes, bit ops, BCD, mulxu/divxu etc.) but not against an oracle.

## 5. MAME quirks that affect bit-exact comparison (and are optional here)

* `h8_intc` `icr_w` in the 0.264 binary mishandles byte writes (value and masks combined from the wrong byte); with the firmware's byte writes the
  interrupt priorities differ from the datasheet. `H8Intc.MameIcrQuirk=true` reproduces it (needed for the identical write stream); default `false` = datasheet
  behaviour (**not verified against hardware**; whether the real chip behaves as MAME's quirk is unknown).
* The 0.264 SCI/RTC/serial rules differ from current MAME master; `H8Sci` follows 0.264 (the reference binary). SCI is modelled at byte level.
* The firmware reads the RTC year during boot and its control flow depends on it (`>= 0x70` is treated as 19xx); the default RTC is 2026-10-02 05:08:35 for reproducibility.
* MAME's vblank/frame time is `16.715113234 ms` (59.826 Hz) with a 2.5 ms vblank for this driver; both are parameters of `S12SoundBoard`.

## 6. Limitations and not implemented

* JVS bus (SCI0 transmit is discarded; no I/O board answers), the settings device (write-only sink), RTC write mode.
* DMAC, ITU input capture / PWM / phase counting / external clocks, software standby, refresh controller: registers store values only.
  Illegal instructions halt the CPU (`LastIllegalPc`). H8S-only instructions, normal mode and byte pre/post-modify of ER7 are not supported.
* Multi-sample audio path to the host (resampler, buffering, device) is not provided.
* Hardware accuracy: all fidelity claims are "equal to MAME 0.264 for these ROMs". MAME's C352 is a reverse-engineered model (e.g. the control register `0x200`,
  the rear channels, ROM address bits above 24, clipping behaviour).
* Sample-exact equality in a closed loop with a differently-timed main CPU is not achievable or claimed (see section 4).

Fixes found by the oracle comparisons (kept for future maintainers): 32-bit register forms require bit 3 of the nibble clear; disp16/abs32 printed raw;
reset costs 6 states, interrupt entry 14; bus accesses inside an instruction are ordered prefetch -> data; events must be serviced before every bus
micro-operation (`IH8Bus.Tick`); the A/D converter has to be triggered by ADTRG at vblank end or the firmware hangs; vblank times must be floored when converted to states.

## 7. Reproducing the verification

Tools are in `src/System12.Runtime/Sound/tools/` (not compiled by the runtime project):
`mame_sound_capture.lua` (C352 write tap, shared-RAM tap, per-frame times), `mame_window_trace.lua` / `mame_multiwindow_trace.lua` (debugger trace windows),
`uniq_trace.py`, `compare_window_traces.py`, and `SoundTool/Program.cs.txt` + `SoundTool.csproj.txt` (rename to build a console harness; modes documented at the top).

MAME capture (reconstructed pattern; the exact historical command line was not preserved): with an empty NVRAM directory and ROMs in `-rompath`,
`S12_OUT=<dir> SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy mame tektagt -rompath <roms> -video none -sound none -wavwrite <dir>/out.wav -samplerate 88200 -seconds_to_run 40 -autoboot_script mame_sound_capture.lua -nvram_directory <fresh> -cfg_directory <fresh>`
(MAME's default wav rate is 48 kHz and cannot be used for comparison). Then
`SoundTool mixcmp c352.bin <dir>/c352_writes.txt <dir>/out.wav 2` with `MAMEOUT=1 EPS=-1e-3`,
`SoundTool full sub.bin c352.bin <dir>/shared_writes.txt <dir>/frames.txt 40 <out>` (`QUIRK=1`) and `SoundTool pcmcmp <out>/mine.pcm <dir>/out.wav`.
The NVRAM state matters for the main CPU's behaviour; results above came from one specific capture and are not claimed for other NVRAM contents.

## 8. Licence and credits

`C352.cs`: derived from MAME `c352.cpp` (BSD-3-Clause; R. Belmont, superctr; thanks to Cap of VivaNonno, The_Author, cync and the hoot team); licence text
retained in the file. `H8Cpu.cs`, `H83002.cs`: written from the Hitachi H8/300H manual and cross-checked against MAME's `h8` family (BSD-3-Clause, Olivier Galibert); behaviour adopted
from MAME where they differ. Files carry SPDX `BSD-3-Clause` headers. MAME reference sources were fetched read-only into `reports/refs/` (git-ignored); no ROM was downloaded.
