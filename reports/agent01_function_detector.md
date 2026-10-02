# Agent 01: function detector fix (raw image mode)

All numbers below come from runs I did in this session. Image: generated/ttt_image.bin (0x398350 bytes, base 0x80010000).

## Cause (confirmed)
* `RefineEnd` only ends a function at `jr` (or `jr ra`). A plain `j`/`b` is not an end, so func_80010000 (`j 0x8017baa4`) went on into the following data until some stray `jr`.
* `DetectFromScan` seeds a function at every `jal` target found in every word of the image, including data. Those words are decoded as code (about 20k "jal" words, most of them data).
* Zero-filled memory (bss, overlay areas that are loaded at runtime) decodes as `nop`, so a seed in it became a function of up to 63k nops.
* Nothing checked for words that are known instructions but never appear in compiled code, such as `lb x, off($zero)`. Jump-table and pointer data look exactly like that.

## Fix (patches/0002-function-detector.patch, files under RecompOne.Recompiler/Analysis/ only)
New file `DetectorOptions.cs`, plus changes to `FunctionDetector.cs` and `FunctionPipeline.cs`.

The switch is off by default, so disc-game behaviour is unchanged. Enable it with the environment variable `RECOMPONE_STRICT_DETECT=1`. I was only allowed to edit Analysis/, so there is no JSON config key yet. The lead should add one (for example `"strictDetect": true` -> `DetectorOptions.Strict`) in ConfigLoader/Program. Tunables: `RECOMPONE_MAX_INTRA_JUMP` (hex, default 0x2000) and `RECOMPONE_MAX_NOP_RUN` (default 16).

Strict mode:
1. A function also ends at an unconditional `j`, `b` or `bgez $0` plus its delay slot, once no earlier branch reaches past it.
2. A function is cut in front of any word that is not a known instruction, or that is implausible (load/store with base `$zero`). A branch whose delay slot would fall in the cut is dropped too.
3. 16 or more consecutive nops end a function. A function whose body is only nops is rejected (DetectFromScan, DetectFromAddresses, BuildFunc).
4. A forward `j`/branch farther than 0x2000 bytes counts as a tail call and does not extend the function.
5. Scan seeds (jal targets and prologue pointers) must reach a real terminator. Seeds that hit data or the next entry are rejected.
6. New `DiscoverAdjacent` pass (called from `FunctionPipeline.Run` in strict mode). The code after a known function's end, skipping nops, is tried as the next function. This catches leaf functions and callbacks that nothing calls with `jal`. Each candidate must decode cleanly and reach a terminator, so it stops at data. It iterates together with call discovery.
7. Jump tables: the existing `JumpTableAnalyzer` and `GrowByJumpTables` are unchanged and read the tables from the image bytes. Since a function ends at its last `jr`/`j`, the table data after it is no longer emitted as code. Check: none of the 68 runs of 4 or more consecutive code pointers in the image falls inside a detected function body.
8. Debug aid: `RECOMPONE_FNMAP_OUT=<file>` writes `start end ninstr name` for every function (tools/fnmap_*.tsv). Analysis scripts: tools/fnmap_analyze.py, tools/fnmap_cover.py, tools/fnmap_run_a01.sh.

## Results

| | before (non-strict, 0001 only) | strict |
|---|---|---|
| functions | 3418 | 1865 |
| main.cs | 206,163,265 bytes (206 MB) | 23,032,926 bytes (23 MB) |
| `[Unknown]` words printed by the emitter | about 761k | 0 |
| max C# bytes per function | about 4 MB | 266,767 |
| median C# bytes per function | not measured | 4,609 |
| p90 / p99 C# bytes | not measured | 29,112 / 129,509 |
| largest function (instructions) | not measured | 1959 (func_8017137C) |

* Median function is 35 instructions, p90 217, p99 975.
* Strict mode detects 173,440 instructions (694 KB, 18.4% of the image). They sit in 0x800F8E38..0x801933D4, the only code region.
* The rest of the image is data or zero-filled memory, or code that is loaded from banked ROM at runtime and is not in this image.
* Non-strict regression check: with `RECOMPONE_STRICT_DETECT=0` the new code produced a main.cs with the same md5 as the earlier baseline (4f40783a...e8feb8b3e, 3418 functions). Default behaviour is unchanged.
* Patch check: 0001 and then 0002 apply cleanly to a fresh checkout of 8d0091c, and the resulting Analysis files are identical to the working copy.

### Coverage (heuristic, not a MAME PC check)
* `jr ra` words in the image that lie inside a detected function: 1914 of 1919. The 5 missed are 0x80122d84, 0x80122e20, 0x80122ebc (GTE routines after a data blob, nothing calls them with `jal`), and 0x8023bc60/64 (data).
* `addiu sp,sp,-n` prologue words outside detected functions: 0 of 964.
* This is evidence of no gross loss in the code region. It is NOT a proof against actually executed PCs. Code reached only through function pointers to non-adjacent leaf functions could still be missed. The MAME PC coverage test is still open.

### Known limitations / honest caveats
* About 167 functions overlap or nest. The escape scan promotes branch targets that leave a function into entry points inside other functions. This is the original behaviour, and the code is emitted twice.
* 30 functions fall through into the next function without a terminator. 6 end oddly, such as the never-returning startup function `func_8017BAA4`, which ends in `jal; nop; break`.
* Code reached only by jumps into runtime-loaded RAM (0x8004xxxx, 0x8008xxxx, 0x802C..., which are zero-filled in this image) is NOT emitted. Calls to those addresses should go through `Dispatcher.Call`. I did not test this at run time.
* Strict mode treats `lb/lw/sw...` with base `$zero` as data. A game that really uses such absolute addressing (none seen here) would be cut.

## Compile test (/home/user/work/ttt_build_a01/TttBuild.csproj, a copy; Compile Include -> generated/recomp_a01; Runtime project referenced from the private copy /home/user/work/a01/recompone)
* Try 1, as is: 544 errors, all `CS0103 'Bios' does not exist`. The generated main.cs has no `using` for the Runtime Bios class. Log: reports/a01_ttt_build_try1_missing_using.log. Time 8m01s, no OutOfMemory.
* Try 2, with `<Using Include="RecompOne.Runtime.Bios"/>`: 544 errors, all `CS0117`. `RecompOne.Runtime.Bios.Bios` has no `Break` (542 sites) or `Syscall` (4 sites), which the emitter calls for `break` (gcc divide-by-zero checks) and `syscall`. This is a gap in the Runtime or emitter, not the detector. Log: reports/a01_ttt_build_try2_no_Bios_Break.log. Time 15m54s (CPU contention from other agents).
* Try 3, with a stub `Bios.Break/Syscall` class (test shim BiosShim.cs, throws NotImplemented): **Build succeeded, 0 warnings, 0 errors**, 6m52s, `-m:1`. Log: reports/a01_ttt_build.log. This only shows the generated C# compiles against the runtime API. Nothing has been executed.
* No Roslyn OutOfMemory any more (before: 206 MB source, OOM after 9m15s).

## Status for the lead
* Detector fix done and compiling. This does NOT mean H2: no equivalence test and no execution against MAME has been run.
* Open items for the lead:
  1. Add a JSON config key for strict mode, or set the env var in the run script.
  2. Provide `Bios.Break` / `Bios.Syscall` (or an arcade-specific equivalent) in the Runtime, and a `using` in the generated header.
  3. MAME PC coverage test, to measure what the static detector misses.
  4. Decide how to handle runtime-loaded overlay code.
