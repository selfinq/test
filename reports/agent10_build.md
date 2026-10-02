# Agent 10: reproducible, Windows-friendly build

Everything below was run in this session on the Claude container (Ubuntu 24.04.4, 4 cores, 15 GB, dotnet SDK 10.0.112, Python 3.11.15). Nothing was run on Windows.
Full verified commands and the "does NOT work yet" list are in `docs/BUILD.md`; this file is the evidence summary.

## Delivered (only under scripts/, host/, docs/BUILD.md, this report)

* `scripts/pins.cfg`, `common.sh`, `common.ps1`, and paired `setup`, `import`, `regenerate`, `build`, `test`, `run`, `publish` in `.sh` and `.ps1`.
  Every script stops at the first error and prints `FAILED [script] step N: <step>` plus command/reason. Pinned RecompOne commit 8d0091cf4877b27c2d526106a78c75f6b0ef97b2.
* `host/TttHost` (assembly `TttRecompHost`, net10.0, RIDs win-x64 + linux-x64, `RollForward=LatestMajor`): status print + C# port of `tools/verify_romset.py` (own CRC32, no NuGet dependency). Conditional reference to `src/System12.Runtime`, opt-in include of generated code.
* `host/TttHost.Tests` (NUnit): CRC32 vector, synthetic zip cases (complete, missing, renamed, corrupted, extra/unknown, oversized, 4 unsafe-path cases, not-a-zip), real ROM zip when present.
* `docs/BUILD.md`.

## Evidence

Bash, run in a clean copy of the workspace (no generated/, no bin/obj, fresh clone of RecompOne into a scratch `../work/recompone`):

| Step | Result |
|---|---|
| `setup.sh` | clone at the pin, `applied 0001-raw-image-mode.patch`, build OK (2 pre-existing warnings, 0 errors), 1m24s. Re-run: `already applied`, 0.9 s |
| `import.sh` | verify 15/15, regions built, image sha256 c1d796ff...21c7 == `tests/expected_image.json`; image and region files byte-identical to the workspace ones; 21 s |
| `regenerate.sh` (0001 only) | 3418 functions, main.cs 206,163,265 B, sha256 cd72d84b...ab05 == the existing `generated/recomp/main.cs` (reproducible); 49 s |
| `regenerate.sh` (0001-0003 applied, `RECOMPONE_STRICT_DETECT=1`, `RECOMP_OUT` in scratch) | 1865 functions, 23,032,926 B, sha256 e8b969d2...262d == agent01's `generated/recomp_a01/main.cs`; 5 s. Without the variable: back to cd72d84b |
| `build.sh` | runtime + host OK, ~20 s. (First attempt failed loudly because my snapshot copy of `src/` was taken while another agent was writing a file: 4 CS0246 errors, step and reason printed.) |
| `test.sh` | Python 19 tests OK; `TttHost.Tests` 14 passed / 0 failed / 0 skipped; `System12.Runtime.Tests` discovers no tests (warned, not failed) |
| `run.sh` | real ROM: exit 0; `--rom /nonexistent.zip`: exit 2; tampered zip: exit 1 |
| `publish.sh win-x64` | `host/TttHost/bin/Release/net10.0/win-x64/publish`: 233 files, 120 MB, `TttRecompHost.exe` = PE32+ x86-64 console (checked with `file`), has `tektagt_manifest.json`. 22 s. Also in the real workspace |
| `publish.sh linux-x64` | 233 files, 123 MB; the published binary was executed on Linux with `--status` (exit 0) |
| host + generated code | `build.sh --with-recomp` with `RECOMP_OUT=generated/recomp_a01` and `-p:RecompExtraSources=BiosShim.cs` (agent01's test shim): 0 warnings, 0 errors, 8m45s; status line reported `Recompiled.Entry: ImageBase=0x80010000`. Without the shim: 1088 x `CS0103 'Bios'` (generated code, not the host), 15 min |
| ROM check parity | Python tool and C# host agree on a tampered copy of the real zip: 12 ok, 1 wrong_name, 1 bad_content, 1 missing, exit 1 |
| Failure paths | missing ROM, tampered ROM, recompiler not built, runtime not building: each printed the failing step and exited non-zero |

PowerShell: the `.ps1` twins were executed with PowerShell 7.6.6 on Linux (`dotnet tool install PowerShell` in scratch): setup (fresh clone), import (ok / tampered / missing), regenerate (same cd72d84b hash), build, run (0/1/2), test, publish win-x64. Two bugs found and fixed this way: `$LASTEXITCODE` unset under strict mode; test-project filter matched the repo path "test". **Windows PowerShell 5.1 and Windows itself: not tested.**

## Notes for the lead / other agents

* `src/System12.Runtime/System12.Runtime.csproj` hard-codes `/home/user/work/recompone/RecompOne.Runtime/...`; it will not build on the user's PC. Use a relative path or a property (`$(RecompOneDir)`).
* Publish size is dominated by RecompOne.Runtime (Roslyn, ImGui, Silk.NET, MonoMod): 120 MB with the runtime referenced, 78 MB (193 files) with `-p:UseSystem12Runtime=false`.
* `RECOMPONE_STRICT_DETECT=1` (patch 0002) is passed through by `regenerate`; there is no JSON key yet.
* The host's `IncludeRecomp` is off by default because the 206 MB output is known not to compile (Roslyn OOM).
* `tests/` may get more dotnet test projects; `test.sh/.ps1` pick up any `*.csproj` containing "test" in its relative path under `host/`, `tests/`, `src/`.
* Someone committed my files to git before I finished (git log shows other agents' commits that include `scripts/`, `host/`); I did not run any git commit/push.

## Not done / limits

* Not executed on Windows (neither the exe nor the scripts); no Windows PowerShell 5.1 run; paths with spaces untested; trimming/single-file untested.
* The host does not run the game. It is named `TttRecompHost` for that reason.
