# agent08 - input path, coin/credit, AT28C16 EEPROM, I/O outputs (tektagt)

Status: DONE for the requested scope; end-to-end game-level use of the input block is not exercised (no main CPU yet). Nothing committed or pushed. Details: `docs/system12/INPUT_IO.md`.

## Files written
* `src/System12.Runtime/Input/At28c16.cs` (device + `At28c16Store`), `EepromIoDevice.cs` (bus windows: EEPROM, watchdog `0x1f018000`, nopw `0x1f008000`, `0x1f010000`, `0x1f1bff08`; `ArcadeIoFactory`),
  `ArcadeInput.cs` (`ArcadeInputState`, `JammaPort`), `HostInputMapper.cs` (bindings as data, mapper, text table), `ArcadeOutputs.cs` (events, shared-RAM layout/decoder/monitor), `InputSelfTests.cs` (console tests, no `Main`).
  Uses the real `IMmioDevice`/`MmioDeviceBase` from `Core/IMmioDevice.cs`; no csproj touched.
* `docs/system12/INPUT_IO.md`, this report, `reports/trace_a08/` (Lua probes + small text logs; `eeprom_fresh_final.bin` is covered by the `*.bin` ignore rule).

## Answers
**(b) How input reaches the main CPU: through the H8/3002, not through SIO0 pads and not through JVS.** MEASURED in MAME 0.264 with the real ROMs:
host controls -> H8 bus `0x300000/0x300002` (JAMMA-style `in_r`, active low) -> H8 firmware `teg1.11s` -> shared RAM words (H8 `0x83140/42` player words, `0x83380` test, `0x83900/02` coin counters, `0x83940/42` main->H8 "coins consumed")
-> main CPU at `0x1f08xxxx`. Zero main-CPU accesses to SIO0/SIO1 in 2 800 frames (control tap on IRQ regs proves taps work). tektagt instantiates no JVS device (test menu greys "JVS CABINET OPTIONS").
All 26 single-input (word0, word1) pairs measured in MAME are reproduced bit-exactly by `JammaPort`. The 0.264 binary names ports `IN0/IN1`; the pinned master source uses `JVS_*`; they agree on every pair.

**(a) AT28C16**: modelled exactly as MAME f8f6b22 (fetched `at28c16.cpp`): 0x820 bytes, byte write only if different, 200 us busy, busy reads return `last ^ 0x80` (data polling only - MAME has **no toggle bit**), busy writes dropped, OE-12V chip erase, A9-12V ID redirect
(both pins unreachable in tektagt), default 0xFF, MAME-compatible raw 0x820-byte file at `<dataDir>/nvram/tektagt/at28c16` (`$SYSTEM12_DATA_DIR` or `~/.local/share/System12Recomp`), wrong-size file -> zeros as in MAME. Bus lanes: device offset = `(phys-0x1f140000)>>1`, even byte addresses only.
**Differential proof**: replay of the complete MAME fresh-NVRAM boot trace (4 338 reads incl. busy polls, 1 078 writes, timestamps from emulated time) gives 0 read mismatches and a final image byte-identical to the `.nv` MAME saved; mutating the cycle time makes the unit tests fail.

**(c) Host mapper**: keyboard + gamepad tables as plain data (`DefaultBindings`), text (de)serialiser, tap latch, minimum hold for coin/start/test (default 2 frames; a 1-frame coin pulse was enough in MAME), opposing-direction policy. 8 buttons per player are modelled (BTN7/8 from driver source only).

**(d) Coin/credit/lamps**: MAME exposes **no outputs at all** for tektagt (enumerated at boot, after coin, in character select). Lamps, coin coil, lockout are declared `NotPresentInReference` and are NOT IMPLEMENTED (never raised).
Observable events exist for what the firmware does publish: `coin_accepted`, `coins_consumed`, `test_switch` (from shared RAM words; sequence measured and unit-tested).

## H4 evidence (MAME 0.264, seeded NVRAM `/tmp/nv2`, scripted input)
Coin1 held frames 2600-2606, 1P START held frames 2700-2706 (0-based `frame_done` count): title fade starts 2707, **character-select board first visible at frame 2721, populated at 2723**.
Main PC at those frame boundaries (`801270a0`, `80127098`) is inside the idle/vsync wait loops, so it is not a usable marker. Reproducibility: 3 runs (one with drifted NVRAM) give identical screenshots for frames 2690-2740 and identical PC sequence;
two pristine runs give identical md5 of the 4 MiB main RAM at frames 2700/2723/2740 (`87d82ef5...`, `29724514...`, `a19515e9...`, `read_range(0,0x3fffff,8)`), screenshot md5 at 2723 `16b3efbe...`. Shared RAM differs only at `0x83036` (host clock).
Continuing the script (P1 BTN1 presses) reached an actual fight by frame ~3500. Logs: `scenario_h4_fine_log.txt`, `scenario_h4_log.txt`; dumps/PNGs not stored.

## Tests
`InputSelfTests.RunAll(Console.Out)`: **96 passed, 0 failed, 0 skipped** with the local trace (94 passed + 1 explicit SKIP without it; golden tests are SKIP, not PASS, when the trace is absent). Groups: eeprom (state machine, ID/erase, import/export, events), eepromio (lane semantics, game polling loop),
store (path/default/save/corrupt), golden replay, jamma (26 measured pairs + pairwise no-cross-talk), sharedword, mapper/bindings, outputs/monitor. Built with `TreatWarningsAsErrors` in a scratch project (0 warnings).
Also run locally: (1) devices mapped on the real `System12Memory` bus (sb/lb/lw via kseg1, watchdog read throws NotImplementedMmioException); (2) end-to-end with the sound agent's `S12SoundBoard` running the real `sub.bin`:
mapper -> JammaPort -> firmware -> shared RAM -> monitor reproduced coin accept (6-frame and 1-frame), main "consume" handshake, P1 word `0xa200`, TEST `0x8000` (`e2e_harness_*`; needs the main-init write replay `main_shared_writes_boot.txt`, otherwise coins are not accepted).

## Findings that affect other agents
1. **Lua tap handles must be kept in a global table**; otherwise the Lua GC silently removes the tap. The earlier "0 write-tap hits" for the tektagt protection windows was this artefact: with retained handles `0x1fb00000` has 8 084 writes / 16 159 reads, `0x1fb80000` 8 081 reads, `0x1fa00000` 8 078 reads from frame 370 (first pc `800f8e84`). The bank register `0x1f000000` genuinely sees no access in 2 800 frames. (`probe_banked.lua`, `probe_banked_log.txt`.)
2. `S12SoundBoard` already has `In0/In1/Dsw`; wiring is `board.In0 = port.ReadWord(0)` etc.; the firmware needs the main CPU's init writes before it accepts coins.
3. The shared RAM word `0x83036` carries host wall-clock time (RTC4543): any shared-RAM comparison against MAME must mask it.
4. `0x1f1bff0e` gets ~1 M incrementing 16-bit writes from the boot ROM and `0x1f018000` 1.19 M zero writes: the nopw devices are on a hot path; their `Interlocked` counter is cheap but a bus fast-path may be wanted.

## NOT IMPLEMENTED / UNVERIFIED (explicit)
Shared-RAM HLE of the input block (deliberately: real firmware runs); clearing rule of latch words `0x83180/82`; difference `0x832c0` vs `0x83900`; `0x83384`; BTN7/BTN8 on tektagt; which of BTN1-6 Tekken Tag uses in a fight
(driver's `tekken3` uses 1,2,4,5; not tested for tektagt); coin mode/free play/lockout/lamps/coin coils (absent in the reference); enforcing watchdog; EEPROM toggle bit; DIP switch UI; any run of the actual game on the recompiled code.
`id_to_name` output enumeration could not be validated against a machine that does have outputs.
