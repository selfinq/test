# Input, coin, EEPROM and I/O outputs (tektagt)

Scope: how controls reach the game, the AT28C16 EEPROM, coin/credit bookkeeping and the (absent) lamp/counter outputs.
Implementation: `src/System12.Runtime/Input/*.cs` (namespace `System12.Runtime.Input`). Evidence: `reports/trace_a08/` (MAME 0.264 Lua probes run
against the user-supplied `tektagt` set; scripts and small text logs only - no ROM data, dumps stay local). Report: `reports/agent08_input_io.md`.

Legend: **MEASURED** = observed in MAME 0.264 with the real ROMs in this session; **SOURCE** = read from `reports/refs/namcos12.cpp` (master, f8f6b22)
and not independently measured; **UNKNOWN / NOT IMPLEMENTED** = explicitly not modelled.

## 1. How the controls reach the game (MEASURED)

```
host keyboard / pad --> HostInputMapper --> ArcadeInputState --> JammaPort (4 bytes, active low)
        --(H8/3002 bus 0x300000..0x300003, 16-bit big-endian reads, + DSW on H8 port 7)-->
   H8 firmware (ROM teg1.11s, runs on the sound agent's H8 core)
        --(once per frame: input words in shared RAM, H8 0x080000.. = main bus 0x1f080000..)--> main CPU game code
```

* The main CPU never reads a pad. `0` accesses to PSX SIO0 (`0x1f801040-4f`) and SIO1 (`0x1f801050-5f`) in 2 800 frames including coin + start
  (`probe_sio.lua`; the control tap on the IRQ registers counted 673 861 reads, so taps in that region work). There is no PS1 pad emulation in this machine.
* tektagt instantiates **no JVS device** (`jvs_port_devices` default option is `nullptr`, so `configure_jvs()` never runs and `m_jamma` stays true):
  the H8 handler `in_r` serves JAMMA-style bytes. The game's test menu shows "JVS CABINET OPTIONS" greyed out (MEASURED, screenshot at frame 2900).
  Therefore: **JVS is not involved**; the H8 `SCI0` JVS link has nothing attached.
* Consequence for the runtime: run the real H8 firmware (sound agent) and feed it `JammaPort`. A shared-RAM HLE of the input block was **not** written:
  the protocol around it (handshake at `0x83004`, command mailboxes at `0x80100..`, RTC at `0x83036`) belongs to the firmware and is not reverse engineered here.

### 1.1 What the H8 reads (MEASURED: `golden_inr.txt`, SOURCE: `in_r()`)

All bits **active low** (0 = pressed). Words are read big-endian: word0 @0x300000 = `byte0:byte1`, word1 @0x300002 = `byte2:byte3`.

| byte (H8 addr) | b7 | b6 | b5 | b4 | b3 | b2 | b1 | b0 |
|---|---|---|---|---|---|---|---|---|
| 0 (0x300000) P2 | START | BTN3 | BTN2 | BTN1 | UP | DOWN | LEFT | RIGHT |
| 1 (0x300001) P1 | START | BTN3 | BTN2 | BTN1 | UP | DOWN | LEFT | RIGHT |
| 2 (0x300002) | P1 SERVICE | TEST | COIN1 | COIN2 | P1 BTN7 | P2 BTN7 | P1 BTN8 | P2 BTN8 |
| 3 (0x300003) | P2 BTN6 | P2 BTN5 | P2 BTN4 | - | P1 BTN6 | P1 BTN5 | P1 BTN4 | - |

* 26 single-input pairs (word0, word1) were measured and are reproduced bit-exactly by `JammaPort` (`InputSelfTests`, `jamma:` group); no cross talk between any two inputs.
* BTN7/BTN8 are in the **driver source** only; the 0.264 binary has no ports for them and the game's SWITCH TEST screen shows 6 buttons per player: modelled, **not verified** for tektagt.
* DIP switches (H8 port 7 = port "DSW"): bit7 = SW2:1 Service Mode (0 = on), bit6 = SW2:2 Freeze (0 = on), others 1. Default `0xff` (MEASURED). `JammaPort.Dsw`.
* MAME 0.264 (the binary used for all measurements) names the ports `IN0`/`IN1`; the pinned master source names them `JVS_PLAYER1/2`, `JVS_COIN1/2`, `JVS_SYSTEM` and converts in `in_r`.
  The two agree bit for bit on every measured pair; the `ArcadeInputState` bit positions follow the master `JVS_PLAYER` words (the canonical host-facing form).
* Which physical buttons Tekken Tag uses is **UNVERIFIED**. The SWITCH TEST screen lays out six buttons (top row BTN1-3, bottom row BTN4-6) plus start/stick. The `tekken3` port in the driver
  uses BTN1, BTN2, BTN4, BTN5 (4-button panel); by analogy tektagt probably does, but this was not tested in a fight.
* Sampling: the H8 reads the words several times per frame (about 6 reads/frame measured) and publishes them once per frame. A one-frame coin pulse was enough for the firmware to count a coin (MEASURED, 2nd pulse in `credit_sequence_trace.txt`).
  The mapper therefore samples once per emulated frame, latches taps shorter than a frame and stretches coin/test/service/start to `MinHoldSamples` (default 2) samples.

### 1.2 What the firmware publishes to the main CPU (MEASURED, observation only)

H8 view (add `0x1f080000 - 0x80000` for the main bus; main sees only the first 16 KB, H8 addresses `0x80000-0x83fff`).
`SharedRamLayout` / `SharedRamPlayerWord` / `SharedRamInputMonitor` implement the decoding; nothing writes these words except the firmware.

| H8 addr | meaning | evidence |
|---|---|---|
| `0x83140` / `0x83142` | P1 / P2 current state, **active high**, rewritten every frame. bit15 START, 14 SERVICE (P1 only), 13 UP, 12 DOWN, 11 LEFT, 10 RIGHT, 9 BTN1, 8 BTN2, 7 BTN3, 6 BTN4, 5 BTN5, 4 BTN6; bits 3..0 never changed | `probe_fields16k_log.txt` (all 24 inputs); the game's SWITCH TEST shows `1P:0200` for BTN1, `0080` for BTN3, `0040` for BTN4, `4000` for SERVICE |
| `0x83180` / `0x83182` | latch: OR of the above since boot in the traces; clearing rule **UNKNOWN**, never read by the game in attract/select | same |
| `0x83380` | `0x8000` while TEST is held; `0x83384` is also read, meaning UNKNOWN | probe |
| `0x83900` / `0x83902` | coin slot 1 / 2: coins accepted and not yet consumed (16-bit, rewritten each frame) | `credit_sequence_trace.txt` |
| `0x832c0` / `0x832c2` | mirror of the above written by a second firmware routine; difference UNKNOWN | same |
| `0x83940` / `0x83942` | **main -> H8**: coins consumed. Game wrote `1` when 1P START spent a credit; counter `0x83900` then dropped by 1 | same |
| `0x83036` | host wall-clock (RTC4543 data), differs between two otherwise identical MAME runs | `shared_f*.bin` md5 comparison |
| `0x8300e` | free-running per-frame counter | probe |

Measured credit sequence (attract, seeded NVRAM): coin1 held 6 frames -> `0x83900=1` (about 3 frames after release); coin2 -> `0x83902=1`; coin1 held **1 frame** -> `0x83900=2`;
the title screen showed `CREDITS 3`; 1P START (held 6 frames) -> main writes `0x83940=1`, `0x83900=1`; character select followed. Free play / coins-per-credit handling lives in the game and is not modelled.

### 1.3 End-to-end check with the real firmware (local, needs the user's ROM-derived regions)

`reports/trace_a08/e2e_harness_Program.cs.txt` (+ `e2e_harness_output.txt`): `HostInputMapper` -> `JammaPort` -> `S12SoundBoard.In0/In1/Dsw` (the sound agent's H8/3002 core running the unmodified `sub.bin`) -> shared RAM -> `SharedRamInputMonitor`.
The harness replays the 588 main-CPU shared-RAM writes recorded from MAME frames 0..700 (`main_shared_writes_boot.txt`) because the firmware only accepts coins after the main CPU has configured it
(without the replay: P1/P2 words and TEST worked, coins stayed 0). Result, all matching what MAME showed:

* COIN1 held 6 frames -> `0x83900 = 1` (event `coin_accepted` slot 0); COIN2 tapped 1 frame (mapper stretches to 2) -> `0x83902 = 1`.
* Main writes `1` to `0x83940` -> the firmware lowers `0x83900` to 0 and zeroes `0x83940` (event `coins_consumed`); `0x83902` untouched.
* P1 START+UP+BTN1 -> `0x83140 = 0xa200` (= `SharedRamPlayerWord.Encode`), TEST -> `0x83380 = 0x8000`, both released -> 0.

This is the strongest available evidence that the LLE path is functionally right; it is **not** a game run (no main CPU) and does not exercise the game's own use of these words.

## 2. Host input mapper (`HostInputMapper`, `DefaultBindings`, `BindingTable`)

Plain data, no UI library. A source is a string: `Key:<name>`, `Pad<n>:<button>` or `Pad<n>:<axis>-` / `+` (axes via `SetAxis`, dead zone 0.5). Multiple sources may drive one arcade input.
Text format (`BindingTable.Parse/ToText`): `P1Up = Key:Up, Pad0:DPadUp`; unknown targets raise `FormatException` (never skipped).

Default keyboard table (this project's own, modelled on common MAME conventions):

| Input | Key | Input | Key |
|---|---|---|---|
| P1 stick | arrows | P2 stick | R (up) F (down) D (left) G (right) |
| P1 BTN1-6 | LeftCtrl, LeftAlt, Space, LeftShift, Z, X | P2 BTN1-6 | A, S, Q, W, E, T |
| P1 BTN7/8 | C, V (unverified) | P2 BTN7/8 | Y, U (unverified) |
| P1 START / P2 START | 1 / 2 | COIN1 / COIN2 | 5 / 6 |
| SERVICE | 9 | TEST | F2 |

Gamepad defaults (`DefaultBindings.Gamepad(pad, player)`): D-pad or left stick = stick, West/North/South/East = BTN1/2/4/5, RightShoulder = BTN3, RightTrigger = BTN6, Start = START, Back = COIN.
Opposing directions report neutral by default (`OpposingDirectionPolicy`).

## 3. AT28C16 EEPROM (`At28c16`, `At28c16Store`, `EepromIoDevice`)

Modelled **exactly as MAME 0.264/f8f6b22 models it** (`src/devices/machine/at28c16.cpp`, fetched at the pinned commit):

| Aspect | MAME behaviour (implemented) |
|---|---|
| Storage | 0x800 data bytes + 0x20 ID bytes = 0x820 (the `.nv` file is 2080 bytes, checked against `/tmp/nv2` seed) |
| Write | programs one byte only if it differs from the stored byte; starts a 200 us write cycle; same-value write = no-op, no cycle |
| Busy | any read (any address) returns `lastWritten ^ 0x80` (/DATA polling, bit 7 complemented); any write is **dropped** (not queued) |
| Toggle bit | **not modelled by MAME** (only data polling), so neither here |
| OE at 12 V | write erases all 0x820 bytes to 0xff, lastWrite = 0xff, 200 us cycle |
| A9 at 12 V | offsets >= 0x7e0 redirect to ID bytes (+0x20) |
| A9/OE pins | `set_a9_12v/set_oe_12v` are private in MAME and nothing in `namcos12.cpp` calls them: **unreachable in tektagt**. Properties exist only for fidelity |
| Default | all 0xff (tektagt has no default-data region) |
| File | raw 0x820 bytes. Missing -> default. Wrong size -> MAME logs "Error reading NVRAM file" and the zero-initialised RAM stays: **all zeros** (reproduced, `CorruptFileZeroed`) |

Bus mapping (MEASURED, `probe_eeprom_fresh_log.txt`): window `0x1f140000-0x1f140fff`, `umask32 0x00ff00ff`; device offset = `(phys - 0x1f140000) >> 1`, only even byte addresses carry data
(lane 0 and lane 2 of each word). `lw` = two chip accesses (offset n then n+1), `lh` = one, `lb` at an even address = one; the second chip write of a `sw` is dropped by the chip while busy.
The game's routine (pc `8017fe80..8017fec4`) reads, writes with `sb`/`sh`, reads back immediately (busy value `data ^ 0x80`), then 800 us later reads the real value.
Odd byte addresses: MAME returns 0 / ignores; `EepromIoDevice` does the same **and counts it** (`UnmappedLaneAccesses`, `IMmioTrace.OnNotImplemented` when a trace is attached).

Persistence: `At28c16Store(game, dataDir?)` -> `<dataDir>/nvram/<game>/at28c16` (same relative layout as MAME's `-nvram_directory`, so MAME files can be reused).
`dataDir` = argument, else `$SYSTEM12_DATA_DIR`, else `%LOCALAPPDATA%/System12Recomp` (`~/.local/share/System12Recomp`). Atomic temp-file + rename; `SaveIfDirty` writes only after a change.
Call `Save` on exit (MAME saves only at exit) and optionally every few seconds.

Time source: the 200 us window needs **emulated** time (`new At28c16(() => emulatedMicroseconds)`). Wall-clock time would make boots non-deterministic; the first-boot sequence needs only that 200 us < 800 us.

Verification (no game data needed for the unit tests; the replay needs the local trace): `InputSelfTests` replays the **whole MAME fresh-NVRAM boot trace** (4 338 reads, 1 078 writes, with timestamps) and requires
0 read mismatches and a final 0x820-byte image identical to the `.nv` file MAME saved. A deliberate mutation (cycle 1000 us) makes the unit tests fail.

### Other main-bus registers owned by this module

| Address | MAME | This module |
|---|---|---|
| `0x1f018000` | `nopw`; 16-bit 0 written 1 187 719 times in 2 800 frames (boot ROM from frame 24, then the game inside polling loops) | `WatchdogKickDevice`: counts, raises `Kicked`; **no enforcing watchdog** (reference has none); reads NOT IMPLEMENTED |
| `0x1f008000` | `nopw`; 16-bit 0 written 9 912 times (about once per DMA chunk) | `NopWriteDevice` |
| `0x1f010000` | `nopw`; never written in 2 800 frames | `NopWriteDevice` |
| `0x1f1bff08-0f` | `nopw`; boot ROM writes a 16-bit counter to `+0x0e` (about 1 M writes), terminator values at `+0x0a/+0x0c` | `NopWriteDevice` (8 bytes) |

None of these windows was ever read by the game (`nopw_windows_summary.txt`). `ArcadeIoFactory.CreateMainBusDevices` returns the five devices (EEPROM, watchdog, three nopw) for `System12Memory.MapDevice`.
Verified against the real `System12Memory` bus in a throw-away project: `sb 0xbf140500` -> busy poll `b2`, value readable after 800 us, watchdog read throws `NotImplementedMmioException`.

## 4. Coin / credit counters and lamps (`ArcadeOutputs`, `SharedRamInputMonitor`)

* MAME 0.264 exposes **zero outputs** for tektagt at boot, after a coin and in character select (`probe_outputs.lua`; the output list was enumerated with `id_to_name`, which I could not test against a game that has outputs).
  In `namcos12.cpp` start-lamp outputs exist only in `ptblank2_state`, LED outputs in `technodr_state`. No H8 port is wired to a lamp, coin-counter coil or lockout.
  -> `coin_counter_coil`, `coin_lockout`, `lamp_start1/2` are declared with `OutputSupport.NotPresentInReference`: **NOT IMPLEMENTED, never raised**. If the real PCB drives them from H8 pins that MAME leaves unconnected, nothing here can tell.
* Observable events (`ArcadeOutputs.Changed`) derived from the firmware's shared RAM words: `coin_accepted(slot, old, new)`, `coins_consumed(slot, old, new)`, `test_switch`. The measured sequence is a unit test.
  The game's own credit display is in game RAM and is not read.

## 5. Evidence for Milestone H4 (coin + start -> character select, MAME 0.264)

Pristine seed `/tmp/nv2/tektagt/at28c16` copied per run (the game writes bookkeeping back into the NVRAM, so reuse drifts the file but did not change the frames). Input script: `scenario_h4_fine.lua`.

| Frame (MAME `frame_done` count, 0-based from reset) | Event |
|---|---|
| 2600 .. 2606 | COIN 1 held 6 frames; title shows `CREDITS 1` |
| 2700 .. 2706 | 1P START held 6 frames |
| 2707 | title fade starts (screen differs from idle) |
| **2721** | first frame showing the character-select board (empty grid, "2ND") |
| **2723** | board populated (portraits, "XIAOYU", timer 39, "1ST") |

Main-CPU PC at the frame boundary: 2721 `801270a0`, 2723 `80127098` - both inside the vsync wait loops (`80127088-801270a4`, `8012f3f0-8012f444`), so the PC is **not** a distinctive marker; use RAM hashes.
Reproducibility (3 runs, one with a drifted NVRAM): identical screenshots for every frame 2690-2740, identical PC sequence; two pristine runs: identical md5 of the 4 MiB main RAM dump at frames
2700 `87d82ef5d55b3749e607ed23eddbae84`, 2723 `297245144025be07b09589ca1bfeee3d`, 2740 `a19515e9e30aa5e39936490b8146c42d` (dump = `read_range(0,0x3fffff,8)`, address order). Screenshot md5 at 2723: `16b3efbe3609878ada75df9c58899f61`
(512x240 PNG from `screen:snapshot`). The shared RAM block is **not** reproducible bit for bit because of `0x83036` (host clock).
Longer run (`scenario_h4.lua`): after selecting characters with P1 BTN1 the game reached a fight (frame ~3500, "STAGE 1", timer 60) and was still fighting at frame 4500.
With a recompiled build, the same input script (coin at 2600, START at 2700, in the same frame numbering) should reach the same RAM hash if time, RTC and RNG inputs match; this has **not** been attempted.

## 6. Pitfalls found while probing (for everyone writing MAME Lua probes)

* `install_read_tap/write_tap` return a handle; if the Lua object is garbage-collected the **tap silently disappears** (taps stopped firing after a few hundred frames in the first attempts). Keep every handle in a global table.
  This explains the "0 write-tap hits ... in 4000 frames" for the tektagt protection windows `0x1fb00000..0x1fb80003` in `reports/PHASE1_AUDIT.md`: with retained handles
  (`probe_banked.lua`, 2 000 frames) `0x1fb00000` is written 8 084 times and read 16 159 times, `0x1fb80000` is read 8 081 times, `0x1fa00000` read 8 078 times, first hits at frame 370-373
  (pc `800f8e84..800f8ea8`, `8012e548`). The bank register `0x1f000000` really is never written in 2 800 frames (`nopw_windows_summary.txt` covers `0x1f000000-0x1f07ffff`).
* Taps report physical addresses; a 32-bit access to a 16-bit or 8-bit handler is split into several callbacks, each with its lane `mask`.
* `-video none` still lets `screen:snapshot()` work (used for all screenshots here).
* Relative paths in Lua are relative to MAME's cwd, not the script.

## 7. NOT IMPLEMENTED / UNKNOWN (explicit list)

* Shared-RAM HLE of the input block (the real firmware must run) - deliberate.
* Meaning of `0x83180/82` clearing, `0x832c0/c2` vs `0x83900/02`, `0x83384`, the ring buffers at `0x83100/0x83280`, bits 3..0 of the player words.
* BTN7/BTN8 on tektagt; which of BTN1-6 the game uses in a fight (SOURCE suggests 1, 2, 4, 5).
* Coin mode / coins per credit / free play (inside the game); coin lockout; coin counter coils; lamps (not present in the reference).
* Enforcing watchdog; reads of the nopw windows; EEPROM toggle bit, page mode, ID-mode access from the bus.
* DIP switch UI (the property exists, no front end).
* 4-button panel presets for Tekken (UNVERIFIED, see 1.1).
* Wiring is the integrator's job but trivial: `S12SoundBoard` (sound agent) already exposes `In0`, `In1`, `Dsw` and `SharedRam`, so once per frame:
  `board.In0 = port.ReadWord(0); board.In1 = port.ReadWord(1); board.Dsw = port.Dsw; port.State = mapper.Sample();` and
  `monitor = new SharedRamInputMonitor(SharedRamInputMonitor.BigEndianReader(board.SharedRam), outputs); monitor.Poll(frame);`.
  This exact path was run locally against the real firmware, see 1.3.

## 8. Running the tests

`InputSelfTests.RunAll(Console.Out)` returns the number of failed checks (96 checks, 0 failed with the local trace; 94 + 1 skipped without it). The golden trace is found through `S12_TRACE_A08_DIR`
or by walking up from the current directory to `reports/trace_a08/`. Throw-away console project used here (not part of the repository): a `Compile Include` of `Input/*.cs` and `Core/IMmioDevice.cs` plus
`return InputSelfTests.RunAll(Console.Out);`.
