# Patch 0003 - memory type used by generated code (`(PSMemory)m`)

Status: written and tested on RecompOne `8d0091c` + 0001 + 0002. Not for upstream (upstream rejects AI-generated PRs); it is a local patch.
File: `patches/0003-memory-cast.patch` (apply after 0001 and 0002: `git apply patches/0003-memory-cast.patch`).

## Problem (evidence)

`RecompOne.Recompiler/CodeGen/FunctionEmmiter.cs:111` emits, in every function that touches memory:

    public static void func_XXXXXXXX(CpuContext c, IMemory m) { var mem = (PSMemory)m; ... mem.ReadU32(..) ... }

`PSMemory` is `public sealed class PSMemory : IMemory`; its constructor hard-wires the PS1 devices (Gpu, Spu, Mdec, Timers, Dma, Sio0), a 2 MB
RAM and 512 KB BIOS window, and it cannot be derived from. So a different memory class passed as `m` makes every generated function throw
`InvalidCastException` (proved by the test `PSMemoryCast_FailsForSystem12Memory_ThisIsWhyThePatchIsNeeded`).
The generated code only uses these members (counted on the 206 MB `generated/recomp/main.cs`): ReadU8/16/32, WriteU8/16/32,
ReadWordLeft/Right, WriteWordLeft/Right - all of them are on `IMemory`, so a cast is not needed for *correctness*, only for *speed*
(the original author's comment: the interface call is slow).

## Options considered

| option | verdict |
|---|---|
| derive `System12Memory : PSMemory` | impossible: `PSMemory` is sealed and its ctor creates PS1 hardware (and calls `Pgxp.PgxpMemory.Init`) |
| compose a `PSMemory` inside `System12Memory` | the cast `(PSMemory)m` still fails (the object passed is the outer class) |
| emit `var mem = m;` (interface dispatch) | works with zero runtime change but loses devirtualisation (see benchmark) |
| **emit `(System12Memory)m`, configurable** | chosen: 1 config key, 3 lines changed, default behaviour unchanged |

Benchmark (this machine, 105 M mixed read/write accesses on 4 MB of RAM, Release, `TieredCompilation` off / PGO off - i.e. without guarded
devirtualisation, as for cold generated functions): interface 34-92 M accesses/s, sealed-class cast 106-184 M accesses/s (two runs, noisy; roughly 2x). With default
tiered PGO the JIT devirtualises the monomorphic interface call itself and the difference disappears (62-69 vs 60-62 M/s). Cold, huge generated
functions are exactly the case where PGO does not help, so the cast is kept.

## The patch

* `Config/ConfigLoader.cs`: new key `"memoryType"` (string, default `"PSMemory"`).
* `CodeGen/FunctionEmmiter.cs`: `FunctionEmitter.MemoryType` (static, default `"PSMemory"`); emits `var mem = ({MemoryType})m;`.
* `Program.cs`: copies `config.MemoryType` into `FunctionEmitter.MemoryType` after the config is loaded.

Use (add to `tools/ttt_raw.json`; the type is written fully qualified so no extra `using` is needed in the generated file):

    "memoryType": "System12.Runtime.Core.System12Memory"

Verified: with a synthetic 4-instruction image the recompiler emitted `var mem = (System12.Runtime.Core.System12Memory)m;`, the output
compiled together with `src/System12.Runtime/Core/*.cs` and the generated `func_80010000` ran against a `System12Memory` instance
(`lw t0,0(a0); sw t0,4(a0)` copied 0xdeadbeef correctly). Default config (no `memoryType`) still emits `(PSMemory)m`.

## Without the patch (sed workaround on an existing generated tree)

    sed -i 's/var mem = (PSMemory)m;/var mem = (System12.Runtime.Core.System12Memory)m;/' generated/recomp/main.cs

## Related gaps found while doing this (not fixed here, they need decisions by the recompiler owner)

1. `RecompOne.Runtime.Interrupts.Poll(c, m)` is emitted at every back-edge (`FunctionEmmiter.cs:~83`). That class is the PS1 BIOS HLE
   (BiosB event tables, RAM < 2 MB check in `Callable`, exception stack at 0xE000, host `FrameClock`), so it must not run on System 12, whose
   boot ROM installs its own exception handler. The back-edge hook has to be made configurable too (e.g. `"pollHook": "System12.Runtime.Core.Cpu.Poll"`).
   `System12Memory.Irq` already provides I_STAT/I_MASK and an `OnLineChanged` callback for whoever implements delivery.
2. COP0: `InstructionEmitter` maps `mtc0 $12` to `c.SR = val;` (a plain field). Status bit 16 (IsC, cache isolate) therefore never reaches the
   memory object. The BIOS flushes caches by isolating them; with the stock emitter those stores would go to RAM. Needs a hook, e.g. emit
   `m.CacheIsolated = ...` (`System12Memory.CacheIsolated` exists for this) or make the emitter call a static `Cop0.WriteSr`.
3. `SYSCALL`/`BREAK` are emitted as `Bios.Syscall(c, m)` / `Bios.Break(c, m)` (PS1 HLE) - same class of problem.
