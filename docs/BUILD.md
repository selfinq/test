# Build and reproduce

Everything below was run by agent10 on the Claude container (Ubuntu 24.04.4, 4 cores, 15 GB RAM, .NET SDK 10.0.112, Python 3.11.15, bash).
Where a statement says "verified", the command was executed and the result seen. Nothing here was executed on Windows.

## What you need

| Tool | Needed for | Checked by the scripts |
|---|---|---|
| .NET SDK 10 (`dotnet --version` major >= 10) | recompiler, host, tests | yes |
| Python >= 3.8 (`python3`, `python`, or `py -3` on Windows) | ROM tools, `unittest` | yes |
| git | clone RecompOne | yes |
| Your own ROM zip `tektagt.zip` | import | the scripts stop if it is missing; nothing is ever downloaded |
| Network for NuGet (first build) | package restore | no |

The pinned RecompOne commit (`8d0091cf4877b27c2d526106a78c75f6b0ef97b2`) and URL live in `scripts/pins.cfg`, read by both the bash and the PowerShell scripts.
On the Claude container that commit is only reachable through the git proxy; `git clone https://github.com/BlackLabelHQ/recompone` worked there and HEAD was the pin.

## Scripts

Every script exists twice: `scripts/NAME.sh` (bash) and `scripts/NAME.ps1` (PowerShell 5.1 or 7+). Both stop at the first error and print
`FAILED [script] step N: <step name>` plus the command or reason, and exit non-zero.

| Script | What it does |
|---|---|
| `setup` | checks git/python/dotnet; clones RecompOne into `$RECOMPONE_DIR`; applies `patches/*.patch` in file-name order (skips a patch that is already applied, fails if one neither applies nor is applied); builds `RecompOne.Recompiler` in Release |
| `import` | `tools/verify_romset.py` -> `tools/build_regions.py` -> `tools/ttt_decompress.py` -> SHA-256 check of `generated/ttt_image.bin` against `tests/expected_image.json` |
| `regenerate` | runs the patched recompiler (`tools/ttt_raw.json` by default) and checks that output and the "Recompilation finished" line exist |
| `build` | builds `src/System12.Runtime` (if present), then `host/TttHost` (Release) |
| `test` | `python -m unittest discover -s tests -v`, then `dotnet test` for every test project found under `host/`, `tests/`, `src/` |
| `run` | runs `TttRecompHost` (status print + ROM check; see below) and passes its exit code on |
| `publish` | `dotnet publish -c Release -r <rid> --self-contained` (default `win-x64`; `linux-x64` also accepted) and checks the output |

Environment variables (all optional):

| Variable | Default | Meaning |
|---|---|---|
| `ROM_ZIP` | `<repo>/input/roms/tektagt.zip` | ROM zip for `import` |
| `RECOMPONE_DIR` | `<repo>/../work/recompone` | RecompOne checkout (created by `setup`) |
| `RECOMPONE_URL` | from `pins.cfg` | clone URL |
| `RECOMP_CONFIG` | `<repo>/tools/ttt_raw.json` | config for `regenerate` |
| `RECOMP_OUT` | the config's `game.output` | output dir for `regenerate`; also the dir used by `build --with-recomp` (then default `<repo>/generated/recomp`) |
| `EXTRA_MSBUILD` | empty | extra msbuild arguments for every `dotnet` call, e.g. `-p:S12ExcludeFolders=Gpu;Sound` |
| `PYTHON` | autodetected | python executable |
| `RECOMPONE_STRICT_DETECT` | unset | read by the patched recompiler (patch 0002), see below |

## Verified command sequence (bash)

From a clean copy of the workspace (no `generated/`, no `bin/obj`, no RecompOne checkout; ROM zip symlinked in):

    scripts/setup.sh                 # 1m24s: clone, patch, build. 2 pre-existing CS8602 warnings, 0 errors
    scripts/setup.sh --skip-build    # re-run: "already applied: 0001-raw-image-mode.patch", 0.9 s
    scripts/import.sh                # 21 s
    scripts/regenerate.sh            # 49 s
    scripts/build.sh                 # ~20 s (runtime + host)
    scripts/test.sh                  # first run ~1.5 min (NuGet restore of NUnit)
    scripts/run.sh                   # exit 0 with the real ROM zip
    scripts/publish.sh win-x64       # 22 s

Results seen:

* `import`: ROM set `complete = True`, 15/15 members; four regions written; `generated/ttt_image.bin` sha256 `c1d796ff3f42fd0f7866d3d449f5692b814827870f035f8a265947f529d21c7f` equals `tests/expected_image.json`. The image and all four region files are byte-identical (sha256) to the ones already in the workspace.
* `regenerate` with patch 0001 only (the state at the time of the first run): 3418 functions, `main.cs` 206,163,265 bytes, sha256 `cd72d84b...ab05`, identical to the `generated/recomp/main.cs` that was in the workspace. So that output is reproducible from a fresh clone, the patch and the verified ROM.
* `regenerate` with patches 0001 + 0002 + 0003 applied, `RECOMPONE_STRICT_DETECT=1`, `RECOMP_OUT=<scratch dir>`: 1865 functions, `main.cs` 23,032,926 bytes, sha256 `e8b969d2...262d`, identical to agent01's `generated/recomp_a01/main.cs`. Without that variable the same recompiler (0001-0003 applied) produced the old output again: 3418 functions, 206,163,265 bytes, the same `cd72d84b...ab05` hash.
* `test`: Python 19 tests OK; `host/TttHost.Tests` 14 passed, 0 failed, 0 skipped (one of them verifies the real ROM zip when present). `src/System12.Runtime.Tests` was found but dotnet reported "No test is available"; the script prints a warning for such projects and still exits 0, so read the warning list at the end of the output.
* Failure paths exercised: missing ROM zip, tampered zip (missing + corrupted + renamed member), recompiler not built, `System12.Runtime` not building (4 compile errors in a half-copied snapshot of a sibling's folder), host exit codes 1 and 2 from `run`. Each printed the failing step and returned non-zero.
* PowerShell: the `.ps1` twins were run with PowerShell 7.6.6 **on Linux** (installed with `dotnet tool install PowerShell` into a scratch dir): `setup`, `import` (success, tampered zip, missing zip), `regenerate` (same `main.cs` hash as the bash run), `build`, `run` (exit codes 0, 1, 2), `test`, `publish win-x64`. **Windows PowerShell 5.1 and real Windows were not tested.** Two bugs found by this (`$LASTEXITCODE` under strict mode, test-project filter matching the repo path) were fixed and re-run.

PowerShell invocation: `pwsh scripts/setup.ps1` (or `powershell -File scripts\setup.ps1`); switches are `-SkipBuild` (setup), `-WithRecomp -AllowNoRuntime` (build), `-Rid win-x64|linux-x64` (publish). Host arguments go straight after the script name: `pwsh scripts/run.ps1 --rom input/roms/tektagt.zip`.

## The host: `TttRecompHost`

`host/TttHost/TttHost.csproj`: `net10.0`, `RuntimeIdentifiers` `win-x64;linux-x64`, `RollForward=LatestMajor`, assembly name `TttRecompHost` (so `TttRecompHost.exe` on Windows). It is deliberately **not** called `TekkenTagTournament.exe`, because it cannot run the game.

What it does today (verified on Linux):

* prints which parts are linked in (System12.Runtime referenced or not; recompiled code included or not) and which parts are implemented / NOT implemented;
* verifies a ROM zip by content (size, CRC32, SHA-1) with a C# port of `tools/verify_romset.py`. Exit code 0 = set complete, 1 = incomplete or mismatched, 2 = usage error / no ROM / unsafe archive. On the real `input/roms/tektagt.zip` it reports 15/15 and exit 0. On a tampered copy both the Python tool and the C# port reported 12 ok, 1 wrong_name, 1 bad_content, 1 missing and exit 1;
* always ends with "the game was NOT run".

Usage: `TttRecompHost [--rom <zip>] [--manifest <json>] [--json <report>] [--status] [--version]`. The ROM is taken from `--rom`, then `$TTT_ROM_ZIP`, then `input/roms/tektagt.zip` searched upward from the working directory. `tektagt_manifest.json` is copied next to the executable (also into the publish output).

Optional build inputs (MSBuild properties):

* `src/System12.Runtime/System12.Runtime.csproj` is referenced automatically if the file exists. `-p:UseSystem12Runtime=false` leaves it out. (`build` fails if the runtime does not build; `--allow-no-runtime` / `-AllowNoRuntime` builds the host without it and says so.)
* Generated code is **off** by default: `-p:IncludeRecomp=true` compiles `generated/recomp/*.cs`; `-p:RecompDir=<dir>` picks another directory (only one at a time, because `recomp` and `recomp_a01` define the same types). The generated code needs a runtime project: default `../work/recompone/RecompOne.Runtime/RecompOne.Runtime.csproj`, override with `-p:RecompRuntimeProject=`. `-p:RecompExtraSources=a.cs;b.cs` adds files such as a shim. With `scripts/build.sh --with-recomp` the directory comes from `RECOMP_OUT`.
* Seen result: host + agent01's `generated/recomp_a01` (23 MB) + the `BiosShim.cs` test shim from `/home/user/work/ttt_build_a01` built with 0 warnings, 0 errors in 8m45s (4 cores, other agents running), and the status line then reported `Recompiled.Entry: ImageBase=0x80010000, InitialPc=0x80010000`. The same build without the shim failed with 1088 `CS0103: The name 'Bios' does not exist` errors from the generated code (a runtime gap, also reported by agent01). The 206 MB output was **not** tried in the host (Roslyn ran out of memory on it earlier, see `STATUS.json`).

### Windows x64 publish

    dotnet publish host/TttHost/TttHost.csproj -c Release -r win-x64 --self-contained
    # or: scripts/publish.sh win-x64   |   pwsh scripts/publish.ps1

Output folder: `host/TttHost/bin/Release/net10.0/win-x64/publish/`. Verified: it contains `TttRecompHost.exe` (`file`: PE32+ executable (console) x86-64, for MS Windows), `glfw3.dll`, `tektagt_manifest.json`, 233 files, 120 MB (119.4 MB as measured by `publish.ps1`). Most of the size is the self-contained .NET runtime plus what `System12.Runtime` pulls in through `RecompOne.Runtime` (Roslyn, ImGui, Silk.NET, Mono.Cecil, MonoMod); with `-p:UseSystem12Runtime=false` the same publish is 193 files, 78 MB. Trimming and single-file were not tried.

**This Windows build has not been executed on Windows.** It was built on Linux and inspected with `file`/`ls` only. The `linux-x64` self-contained publish (233 files, 123 MB) was executed on Linux (`TttRecompHost --status` printed the status and exited 0). On a PC, copy the publish folder, and run `TttRecompHost.exe --rom <path to your tektagt.zip>` from a command prompt.

## What does NOT work yet

* The game cannot be run. No emulated memory, no CPU context, no boot sequence is started by any C# code in this repository. `TttRecompHost` only prints status and checks ROMs.
* Recompiled code is not validated. The 206 MB output (3418 functions) contains huge functions made from data; the strict detector output (1865 functions, 23 MB) compiled only together with a shim for `Bios.Break` / `Bios.Syscall`, and nothing generated was ever executed. No coverage check against PCs executed by MAME exists yet.
* The 206 MB `generated/recomp/main.cs` does not compile in this environment (Roslyn out of memory after 9m15s, `STATUS.json`). `build` therefore does not include generated code unless you ask for it.
* `src/System12.Runtime` is a work in progress by other agents; its project file currently points at an absolute RecompOne path (`/home/user/work/recompone/...`), so it will not build on another machine until that is changed. The host inherits this when it references the runtime. `src/System12.Runtime.Tests` runs no tests.
* No GPU/GTE rendering, no window, no sound (H8/3002 + C352), no input/JVS/coin/EEPROM, no banked-ROM or protection-register emulation is connected to the host.
* The RecompOne checkout is expected at `$RECOMPONE_DIR` (default `<repo>/../work/recompone`), outside the repository; `setup` is the only thing that creates it. Applying new patches to an already patched checkout works only if the earlier ones are applied in the same order.
* Windows is untested: no script and no executable here was run on Windows, and Windows PowerShell 5.1 was never used (only PowerShell 7.6.6 on Linux). Long paths and spaces in the repository path were not tested.
* The bash scripts need bash >= 4.4 (`mapfile`, empty-array expansion under `set -u`); macOS system bash 3.2 is not supported (not tried).
* ROMs are never downloaded or committed. `input/`, `generated/`, `bin/` and `obj/` are git-ignored.
