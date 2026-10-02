# HANDOFF — TTT1 arcade (Namco System 12, `tektagt`) static recompilation

For the next AI/engineer. Read this first, then `STATUS.json`, `CONTINUE.md`, `docs/BUILD.md`, `docs/system12/*.md`.

## Goal and hard rules (from the project owner)
- Rebuild a runnable/modifiable **Windows x64** version of Tekken Tag Tournament 1 **arcade (System 12)** by **static recompilation** (RecompOne as base). Not an emulator launcher; any emulated component must be declared (the H8/3002 sound CPU is interpreted — declared).
- Use only the owner's own ROM set `tektagt` (MAME TEG2/VER.C1 set 1). **Never download ROMs/BIOS/ISOs/leaks.** The ROMs are NOT in this bundle.
- Do not push/PR without the owner's authorisation. Do **not** send AI-generated contributions to BlackLabelHQ/RecompOne (upstream rejects them).
- Keep game files, generated code, NVRAM, traces and golden snapshots out of Git (see `.gitignore`).
- Never claim PASS without evidence you ran. Distinguish compiles / boots / playable prototype / validated.

## Where the owner keeps the ROMs
`C:\Users\jhair\Desktop\IGLite\input\roms\tektagt.zip` (56 MB; 15 members, list in `MISSING_INPUTS.md` / `tools/tektagt_manifest.json`). Copy it to `input/roms/tektagt.zip` inside this project.

## Quick start (Windows PowerShell 7+ or bash)
```
scripts/setup.ps1        # clones RecompOne at pinned commit 8d0091c, applies patches/*.patch, builds   (needs .NET 10 SDK, git, python3)
scripts/import.ps1       # verify ROM set by content, build regions, decompress the game image  -> generated/
scripts/regenerate.ps1   # recompile (set RECOMPONE_STRICT_DETECT=1 for the fixed detector)      -> generated/recomp*/
scripts/build.ps1; scripts/test.ps1; scripts/run.ps1
```
`.sh` twins exist. Exact verified commands and caveats: `docs/BUILD.md`. RecompOne location default: `../work/recompone` (override `-p:RecompOneDir=...`).

## Milestone status (honest)
| | Status | Evidence |
|---|---|---|
| H1 ROM set complete | PASS | `tools/verify_romset.py` 15/15 by content; MAME `-verifyroms` good |
| H0 tools+importer | FAIL (partial) | verifier/regions/decompressor done; native System 12 importer in RecompOne = raw-image patch only |
| H2 translated routines + equivalence | FAIL | decompressor matches MAME RAM byte-for-byte; no recompiled routine compared yet |
| H3–H6 | BLOCKED | nothing recompiled boots yet |

## What is proven
- Boot: reset `0xbfc00000` in main ROM (4 MB @ `0x1fc00000`); ROM routine at `0x1fc21048` LZ-decompresses image from ROM `0x28000` to `0x80010000` (`0x398350` bytes); entry word at ROM `0x2401c` = `0x80010000`. `tools/ttt_decompress.py` is byte-identical to MAME RAM at the first frame the game runs from RAM.
- Banked ROM is read **only via PSX DMA channel 5** (offset at `0x1fb00000`, arm by reading `0x1f700000`); the bank register `0x1f000000` is never touched. `BankedRomController` matches MAME (1099 forced ops + 378k game reads). tektagt protection state machine reproduced.
- Inputs come through the **H8 firmware → shared RAM (`0x1f080000`)**, not SIO pads/JVS. EEPROM AT28C16 modelled and diff-verified.
- GPU: 2 MB VRAM (1024x1024); RecompOne has it fixed at 512 rows → `patches/0004-gpu-vram.md`. 59.826 Hz.
- Sound: H8/3002 interpreter + C352 mixer equal MAME 0.264 (timing drifts after ~7 s, hypothesis in `docs/system12/SOUND.md`).

## What is NOT done (priority order)
1. Integrate patches: 0001 (raw image mode), 0002 (strict detector; needs a config key — today only env `RECOMPONE_STRICT_DETECT=1`), 0003 (`memoryType` cast), 0004 (VRAM, opt-in). Generated code calls PS1 `Bios.Break`/`Bios.Syscall`/`Interrupts.Poll` → need arcade hooks in the emitter/runtime (542+4 call sites). Without them strict output fails with CS0103/CS0117 (agent01 compiled it with a test-only throwing shim).
2. Strict output = 1865 functions / 23 MB; unstrict 3418 / 206 MB does not compile (Roslyn OOM). Only 18.4% of the image is code; some code is loaded into RAM later (not in the image). 63 detected entries lie in RAM ranges that DMA overwrites — verify.
3. Write the host that wires `System12Memory` + banked ROM + DMA + EEPROM + input + sound + GPU/MDEC + proper timers/IRQ (RecompOne timers are wall-clock; DMA must complete deferred) and runs the recompiled `Entry`. `host/TttHost` only reports status today ("game NOT run").
4. Differential validation with `tools/difftest` (candidate side must dump `docs/difftest/CANDIDATE_SNAPSHOT.md` format; start GTE regs/hi/lo at 0).
5. Coverage vs MAME-executed PCs (`tools/mame_pc_capture.*`, `tools/mame_pc_validate.py`; agent02 was still finishing when this bundle was made).
6. Then H3 title → H4 character select (MAME reference: coin frames 2600–2606, start 2700–2706, select board at frame 2721) → H5 full fight → H6 audio/stability.

## Pitfalls already paid for
- MAME Lua: keep tap handles in a **global table** (GC silently removes them); taps on `0x1fxxxxxx` can vanish after frame ~360 (`handle:reinstall()`); registers are lowercase (`st["pc"]`); `-video soft` hangs → use `-video none`; use `pkill -x mame` (never `pkill -f`).
- MAME needs a **fixed NVRAM seed** per run; an empty-NVRAM first boot is not reproducible (10 boots → 9 images). Seeds/golden files are NOT in this bundle: create locally with `tools/difftest/make_nvram_seed.sh`.
- Uninitialised GTE regs in MAME 0.264 break determinism; `fixedtime.c` freezes the RTC.
- Roslyn: 200 MB single-file C# is not compilable; keep strict detection on.

## Licences
RecompOne is MIT (see `NOTICE`); `C352.cs` derives from MAME (BSD-3, headers kept). Project code: owner decides.
