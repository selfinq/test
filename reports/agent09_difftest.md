# Agent 09: differential-validation harness (MAME reference capture + comparer)

Status: harness built and exercised on the MAME side only. **There is no candidate (C# runtime) snapshot producer yet, so no
recompiled code has been compared with anything.** What exists and was verified is: a deterministic MAME reference capture,
a snapshot format with JSON schema, a comparer, 50 tests (48 offline + 2 that need MAME), and the C# contract
(`docs/difftest/CANDIDATE_SNAPSHOT.md`). Nothing was committed or pushed (some earlier versions of my files were committed by
someone else while I worked; the tree contains later edits).

## 1. What was built (all paths relative to /home/user/test)

| file | purpose |
|---|---|
| `tools/difftest/nvram_seed/{tektagt/at28c16,SEED.sha256,README.txt}` | archived NVRAM seed (2080 bytes, sha256 `5bea1522...e939a`) |
| `tools/difftest/restore_nvram.sh` | gives every run a fresh copy of the seed (verifies its sha256, wipes nvram/cfg/ini of the work dir, refuses a corrupted seed with rc 2) |
| `tools/difftest/mame_capture.lua` | MAME Lua: checkpoints (all GPR, hi, lo, pc, SR/Cause/EPC/BadA, 64 GTE regs, whole-RAM dump, event stream position), instruction-exact checkpoints (debugger breakpoints), I/O and DMA events (debugger watchpoints), state pinning |
| `tools/difftest/capture_mame.py` | driver: restore seed, build time shim, run MAME headless, convert raw output to the snapshot (region/page/live-stack SHA-256, event chains, sidecar) |
| `tools/difftest/snapshot.schema.json`, `snapshotlib.py` | JSON Schema (draft 2020-12) + stdlib validator, hashing, chains, `dma_summary` |
| `tools/difftest/regions_tektagt.json` | 7 RAM regions (kernel, data, code, rodata, bss, overlay, stack) tiling the 4 MB; **heuristic** boundaries (evidence per region in the file) |
| `tools/difftest/compare.py`, `compare_config.default.json`, `compare_config.strict.json` | comparer with explicit normalisation config |
| `tools/difftest/determinism_check.py`, `mutation_check.py`, `make_vectors.py`, `run_tests.sh` | determinism proof runner, 10-mutant check of the tests, cross-implementation vectors, test runner |
| `tools/difftest/fixedtime.c`, `dma_hash_selftest.lua`, `make_nvram_seed.sh` | wall-clock shim, Lua-vs-Python DMA hash selftest, seed maintenance script |
| `tests/difftest/` | `test_compare.py` (28), `test_capture.py` (20), `test_integration_mame.py` (2, need MAME: `DIFFTEST_MAME=1`), `synth.py`, `vectors.json` |
| `docs/difftest/CANDIDATE_SNAPSHOT.md` | the contract for the C# side |
| `reports/trace_a09/` | experiments, outputs, golden reference snapshot (hashes only; see README.txt there) |

Use:
```
python3 tools/difftest/capture_mame.py --out ref.json            # 1500 frames, every 60, pc_80010000_1, events (3-5 min idle, ~10 min under load)
python3 tools/difftest/compare.py ref.json cand.json [--config cfg.json]   # exit 0 / 1 diverge / 2 bad input / 3 strict-unverified
tools/difftest/run_tests.sh                                       # DIFFTEST_MAME=1 adds the MAME tests
```

## 2. Snapshot format and comparer (summary; details in the contract doc)

* Checkpoints keyed by `id`; `sync` is `pc_hit` (state immediately before the nth execution of an exact pc: instruction exact)
  or `frame` (host frame boundary of the producer: not reproducible by a candidate without cycle-accurate timing).
* Per checkpoint: `cpu` (pc, 32 GPR, hi, lo, cop0 sr/cause/epc/badvaddr, GTE data/control), `ram` (per region sha256 + per 4 KB page
  hash + `stack_live` = bytes from sp to the top of the stack), `events` (two hash chains: writes+DMA starts, reads).
* Events: `W` (exact physical byte address, zero-extended value), `D` (DMA start: channel, chcr, shadow madr/bcr, n, FNV-1a64 of the
  transferred RAM range), `R` (opt-in). Sidecar JSONL localises event divergences.
* Comparer: per sync type profile (`pc_hit`: cpu exact, ram exact, events chain; `frame`: cpu ignored, ram `static_pages` = pages that
  never change in the reference from `first_ram_frame` on, events ignored), region overrides (default: dead `stack` region ignored,
  `stack_live` compared), register ignore list and bit masks, RAM/event ignore ranges (a `reason` is mandatory and printed),
  `prefix_ok` for a shorter candidate stream, `require_ids`. Result items are `match | ignored | unverified | diverge`; an `unverified` item
  (candidate does not provide the data) is never reported as a pass; every applied exclusion is printed. The headline FIRST DIVERGENCE is the
  earliest of the first diverging checkpoint and the first diverging event (ordered by the reference event position), with last good
  checkpoint, page address range and +-N events of context.

## 3. Determinism (the part that was not deterministic)

Capture runs are made from the archived seed. Result on the final tooling: **5 captures (1500 frames, debugger, 10380 events, 27
checkpoints) have the identical `snapshot_sha256` `d2100cb0aed64038...`, identical per-checkpoint hashes and identical events sidecar**
(`reports/trace_a09/outputs/determinism_check.txt`). Three of them ran concurrently under host load ~12 (4 cores) with deliberately different
heap layouts (20 kB of extra environment, a longer work-dir path); the fourth ran separately, and a fifth run with the final scripts (after the last small Lua/driver edits) gave the same hash again. The EEPROM after each run equals the seed
(fixed point). Earlier tooling versions showed the same equality on other 3-run sets (real clock, no time shim).

It was **not** deterministic out of the box. Causes found, in the order they were found:

1. **Uninitialised GTE (COP2) registers in MAME 0.264.** Two identical captures produced different hashes; the strict comparer located
   the only difference: GTE data regs 0,1,24,25 and control regs 8,9, constant for all 1500 frames in each run, e.g. `0x6ecceff0`/`0x0000559e`
   = the host pointer `0x559e6ecceff0` split over two registers (leftover free-list data of a recycled heap chunk), different per run (ASLR).
   Whether garbage is present, and how much (0 to 30 registers observed; `hi`/`lo` too), depends on the heap layout, i.e. on trivia like environment size.
   Reproduction without the fix (`DT_NO_PIN=1`, 3 runs, 480 frames): 3 different hashes, `outputs/determinism_check_WITHOUT_pin.txt`;
   all differing items are COP2 registers, RAM/GPR/events are identical. **Fix:** `mame_capture.lua` sets every GTE register, hi, lo, EPC and
   BadA to 0 before the first instruction and records what it overwrote (`manifest.json: pinned_nonzero_before_pin`). This changes the
   reference's initial state relative to stock MAME (whose value is undefined host memory); a candidate has to start the same registers at 0.
2. **Host wall clock.** The board's Epson R4543 RTC (read by the H8/3002 sound CPU) starts from `time()`, and the game writes a BCD date/time into the
   EEPROM at its first boot (offsets 672..679; with the clock frozen at 2026-01-01 it reads `26 20 01 01 00 04 06 00`, with the real clock the
   seed has `26 20 02 10 07 05 27 35`). `fixedtime.c` (LD_PRELOAD, only `time()`, default 2026-01-01 UTC, built with gcc at capture time) freezes it.
   Measured: from the seed the 1500-frame state is **independent of the RTC value** (frozen 2026-01-01, frozen 2027-06-07, real clock: strict
   compare MATCH incl. all events, `outputs/rtc_independence.txt`), so the shim is a precaution for the seeded path and a necessity only for first boots.
3. **NVRAM state.** Known from earlier work; shown here on real data: the same capture with an empty NVRAM diverges from the seeded one at I/O event #1791
   (reference writes sound shared RAM `0x1f08306c`=`0xc000`, the empty-NVRAM run writes GPU `0x1f801814`=`0x03000001`), before checkpoint `frame_00540`
   (`outputs/compare_seeded_vs_emptynvram_*.txt`). Registers and RAM at the first 480 frames were identical for both, so the effect shows up later.

**Open, not solved:** a first boot from an *empty* NVRAM is not reproducible. Ten first boots gave nine different EEPROM images (hashes in
`outputs/seed_reproducibility.txt`). Identical command lines reproduce (two frozen-clock first boots started 20 s apart: same file; boots from the seed leave it unchanged), but changing only the length of the command line/environment changes the result, even with the RTC frozen and the GTE/hi/lo
pinned. So some other uninitialised input exists in the first-boot path; I did not find it (candidates: H8/3002 or C352 internal state, SPU/GPU
registers; untested). Consequence: the archived seed is an artifact of record and cannot be regenerated bit-for-bit (`make_nvram_seed.sh` makes
a *new* seed and says so). It does not affect the captures, which only read the seed.

## 4. Event capture: what works in MAME 0.264 (important for anyone extending this)

* **Lua memory taps are useless for the game phase.** They fired 580 times, all during the boot ROM phase (frame 0), and never again although the game
  programs DMA ch2 and interrupts from frame ~375 (`reports/trace_a09/io3_log.txt`: tap counter stuck at 580 while CHCR2/DPCR/IMASK change; `io4_log.txt`:
  taps installed at start and at frame 200 on exactly the DMA2 CHCR word never fire). Cause not determined.
* **Debugger watchpoints do fire.** With `-debug -debugger none -debuglog`, `wpset` with a `printf` action + `g` logs every access without stopping
  (`wpaddr` = exact byte address, `wpdata` = value right-aligned; the access width is not available, so events carry no width). Stopping watchpoints
  (only for DMA starts) are serviced from `emu.register_periodic`; the stop message is read from `debugger.consolelog`, results are written back into the
  log with `debugger:command('printf ...')`, so ordering is exact.
* The debugger stops **after** the instruction. MAME's DMA runs inside the CHCR store, so the live MADR is already `0x00ffffff` after a linked list. The first
  version of `D` events was wrong because of this (n=262272 junk words) and, separately, because the pattern for the stop message did not accept hex
  watchpoint ids (`F`, `1B`): ch2 starts were silently missing in two intermediate 1500-frame sets. Both fixed; MADR/BCR are now shadows recovered from the
  printed `W` lines and the hash is of the transferred range after the transfer. The Lua hash is checked against the Python definition inside MAME
  (`test_integration_mame.py::LuaDmaHash`, 5 cases incl. linked list, slice, step -4).
* Cost: debug mode alone ~4x slower than plain MAME (480 frames: 17 s vs ~4 s); the PSX I/O watchpoints add ~2.8x (`perf_*.txt`; hypothesis, unverified: the 16 KB
  page containing the scratchpad becomes slow). Read watchpoints are optional (`--reads`, works: 4 read addresses seen in 420 frames) because they are slower still and
  the polled registers are time dependent. Watching the `nopw` ranges `0x1f008000/0x1f010000/0x1f018000/0x1f1bff08` produced ~1.7 M events per 400 frames of no information: not watched.
* **Debug mode shifts the frame/time alignment**: first RAM pc at frame 368 (debugger, pc `0x80010000`) vs 367 (plain, pc `0x8017babc`). Both are deterministic, they are not
  interchangeable; `compare.py` warns when `producer.debugger` differs. The "frame 367" quoted in the project notes is the plain-mode figure.
* Facts about the game that fell out (golden snapshot, 1500 frames): DMA ch5 (burst into RAM, 750 starts, up to 1696 words) is preceded by exactly one write each to the tektagt
  DMA-offset register `0x1f700000` (750 writes), so these are very likely the banked-ROM data loads (mechanism not analysed; protection `0x1fb00000` is written 734 times,
  `0x1fb80000` 3 times); ch6 ordering-table clears (85), ch2 GPU linked lists (169) and VRAM block uploads (27); after the boot ROM the game still writes 114 GP0 and 578 GP1 words by PIO;
  356 shared-RAM (sound CPU) writes; no write to the bank register `0x1f000000` and none to the EEPROM `0x1f140000..` in 1500 frames.

## 5. Tests and evidence

* `tools/difftest/run_tests.sh`: 48 offline tests pass; with `DIFFTEST_MAME=1` 50 pass (90 s). Synthetic snapshots prove that the comparer detects: register differences (incl. masked
  bits), RAM differences localised to a page address, partially covered pages not hidden by an ignore range, live-stack vs dead-stack, static-page vs volatile-page handling and the
  settle point, event chain/sidecar divergence with context, ordering of event vs checkpoint divergence, shorter streams, missing checkpoints, unverified data, invalid input (rc 2); and
  ignores exactly what the config says. `mutation_check.py` breaks `compare.py` in 10 ways, every mutant is caught (10/10). `vectors.json` gives input/expected pairs for the event
  chain, FNV, DMA ranges, RAM/page/stack hashing and the snapshot hash for the C# side (FNV constants checked against published test vectors).
* Comparer on real data (`outputs/`): run vs run strict = MATCH (3052 items); a register changed in the pc_hit checkpoint -> `gpr.a0` named with both values and last good checkpoint; a code page
  changed -> `ram.code`, page `0x800fe000-0x800fefff`, dead-stack difference ignored; seeded vs empty NVRAM -> event #1791 with context.
* Not tested: any candidate; behaviour on snapshots from another producer; the `--reads` path beyond a 420-frame run; behaviour of `swl/swr`-style partial stores in the event stream.

## 6. Limits and honest caveats

* The region boundaries are a heuristic from two RAM dumps (frame 367 and 1500), not a linker map. Only pages that are static in this one run are compared at `frame` checkpoints,
  so absence of divergence there says little. The `kernel` region (0x80000000-0x8000ffff) contains what the boot ROM copied; a candidate that skips the boot ROM will differ there and needs
  `ram.ignore_ranges` with a reason, or a page-level look at which pages differ.
* Only one instruction-exact point exists in the golden snapshot (`pc_80010000_1`). That is the strongest check available now (full registers + all RAM at the game entry); more points need
  function entries chosen from the recompiler's function table (`--pcs addr:nth`) and a re-capture. A pc inside a delay slot or a KUSEG alias of a KSEG0 address will not match (exact pc).
* The H2 evidence (decompressed image == MAME RAM at frame 367) is compatible with this harness but was not re-derived here.
* Events are limited to the watched ranges, `W`/`D` only by default; sound CPU/C352 internals, scratchpad, VRAM and SPU RAM are not captured.
* The golden snapshot and `outputs/` contain hashes and register values of a run of the game; they were derived from user-supplied ROMs. I did not commit anything; `.gitignore` does not exclude them, so the
  lead decides.
