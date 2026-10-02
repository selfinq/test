# Candidate snapshot contract (recompiled runtime -> differential test)

Status: specification + working reference producer (MAME). **No candidate producer exists yet**; nothing in this
document has been exercised against a recompiled runtime. Everything marked *verified* was checked against MAME 0.264
output of `tektagt` (see `reports/agent09_difftest.md`).

The recompiled C# runtime must write the same snapshot format as the MAME reference so that
`tools/difftest/compare.py REF.json CAND.json` can report the first divergence. Formal schema:
`tools/difftest/snapshot.schema.json` (id `ttt-difftest-snapshot/1`). Shared helper code (hashing, chains, validation):
`tools/difftest/snapshotlib.py` - it is the executable definition when this text and the code disagree; report the
disagreement.

## 1. Files

| file | content |
|---|---|
| `<name>.json` | the snapshot (checkpoints + event summary) |
| `<name>.events.jsonl` | optional sidecar, one event per line (needed to localise an event divergence; without it only the hash chains are compared) |

All integers that are machine words are strings `"0x%08x"` (lower case, 8 digits). Hashes are lower-case hex.
`producer`, `meta` and `snapshot_sha256` are informational (see 7); the candidate sets `producer.name = "recomp"`.

## 2. Top level

```json
{ "schema": "ttt-difftest-snapshot/1", "game": "tektagt",
  "producer": {...}, "meta": {...},
  "capture": {"ram_base":"0x80000000","ram_size":4194304,"page_size":4096,"stack_top":"0x80400000","events_enabled":true},
  "regions": [ {"name":"kernel","base":"0x80000000","size":65536}, ... ],
  "checkpoints": [ ... ],
  "events": {"w_count":N,"w_chain":"..","r_count":N,"r_chain":"..","file":"x.events.jsonl","truncated":false} }
```

* `capture.ram_*`, `page_size`, `stack_top` and `regions` MUST be copied from `tools/difftest/regions_tektagt.json`
  (RAM window = the 4 MB at KSEG0 `0x80000000`, regions tile it, 4096 byte pages). The candidate may embed the same
  table; it must not invent its own boundaries. The boundaries are provisional (heuristic, derived from two MAME RAM
  dumps); if they are refined the reference is re-captured (page hashes make that cheap) and the table is versioned
  by the `regions` array inside each snapshot.

## 3. Checkpoints

Pairing is by `id` (string). The candidate only has to emit the ids it can produce; the comparer reports the missing
ones as `unverified` (or as divergence if listed in `require_ids` of the compare config).

### 3.1 Sync points

| `sync` | id pattern | meaning | exactness |
|---|---|---|---|
| `{"type":"pc_hit","pc":"0x80010000","nth":1}` | `pc_80010000_1` | machine state immediately BEFORE the nth execution of the instruction at exactly this 32-bit virtual pc | instruction exact: registers and RAM are compared bit for bit |
| `{"type":"frame","frame":N}` | `frame_00060`, `final`, `first_ram_frame` | state at the producer's N-th frame boundary | NOT reproducible by a candidate without cycle accurate timing; only RAM pages that never change in the reference run are compared (profile `frame`) |

Reference ids: `first_ram_frame` (first frame boundary at which the CPU is executing from KSEG0 RAM),
`pc_80010000_1` (the jump into the decompressed image), `frame_00060 ... frame_01440` every 60 frames, `final`.
For `pc_hit` the match is on the exact pc value: an entry reached through a KUSEG alias (`0x00010000`) is a different pc.

How a static recompilation produces `pc_hit`: every requested pc must be a function entry or basic-block label of
the generated code. Emit a hook as the first statement there:

```csharp
// generated at block label for pc 0x80010000 (only for pcs listed in DT_PCS, e.g. "80010000:1")
if (DiffTest.Hit(0x80010000)) DiffTest.Snapshot("pc_80010000_1", ref cpu, mem);   // counts hits, snapshots on the wanted nth
```
`Hit` increments a per-pc counter and returns true on the wanted nth. The hook must run before any instruction of
that block executes and before the delay-slot of a preceding branch is committed twice (use block entries, not delay
slots). Frame checkpoints are taken by the runtime's vblank/frame loop (`frame_%05d`, `final` when the run stops,
`first_ram_frame` at the first frame boundary after the jump to RAM).

### 3.2 `cpu` (object, all fields optional except pc/gpr; missing fields become `unverified`)

| field | content |
|---|---|
| `pc` | the pc of the checkpoint (for `pc_hit` = the hit pc) |
| `gpr` | 32 words r0..r31 (`zero at v0 v1 a0-a3 t0-t7 s0-s7 t8 t9 k0 k1 gp sp fp ra`), r0 is always `0x00000000` |
| `hi`, `lo` | multiply/divide registers |
| `cop0` | `sr`, `cause`, `epc`, `badvaddr` (MAME names SR, Cause, EPC, BadA). A static recompiler that does not model exceptions faithfully can leave these out, or the compare config masks bits (e.g. `Cause.IP` hardware interrupt bits) |
| `cop2_d`, `cop2_c` | GTE data/control registers 0..31 in architectural order. MAME names: data = vxy0 vz0 vxy1 vz1 vxy2 vz2 rgb otz ir0 ir1 ir2 ir3 sxy0 sxy1 sxy2 sxyp sz0 sz1 sz2 sz3 rgb0 rgb1 rgb2 res1 mac0 mac1 mac2 mac3 irgb orgb lzcs lzcr; control = r11r12 r13r21 r22r23 r31r32 r33 trx try trz l11l12 l13l21 l22l23 l31l32 l33 rbk gbk bbk lr1lr2 lr31g1 lg2lg3 lb1lb2 lb3 rfc gfc bfc ofx ofy h dqa dqb zsf3 zsf4 flag |

Values are the full architectural 32-bit values as MAME's state interface returns them (`gpr`, `hi`, `lo`, `cop0`
verified; the GTE registers are MAME's raw register values - unverified for registers whose upper bits the hardware
does not implement, e.g. `flag`, `irgb`, `lzcr`: if MAME and the runtime disagree only there, list them in
`cpu.ignore_fields` and document why).

### 3.3 `ram`

```json
"ram": { "regions": { "code": {"sha256": "<64 hex>", "pages": ["<16 hex>", ...]}, ... },
         "stack_live": {"base": "0x803fff80", "size": 128, "sha256": "<64 hex>"} }
```
* `regions.<name>.sha256` = SHA-256 over the region's bytes in ascending address order, in MEMORY byte order (a
  little-endian word `0x12345678` contributes `78 56 34 12`) - i.e. the raw bytes of the RAM array. *Verified:* the
  reference hashes the bytes returned by MAME `read_range(0x80000000, ..., 8)`; this equals the little-endian
  word dump already used for the boot-decompressor equivalence evidence.
* `pages[i]` = first 16 hex digits of SHA-256 of bytes `[i*4096, min((i+1)*4096, size))` of the region. Optional but strongly recommended: without
  pages the comparer cannot localise a RAM difference and cannot apply `static_pages` or ignore ranges (result: `unverified`).
* `stack_live` = bytes `[sp & ~3, stack_top)` with `sp` = the checkpoint's r29. The part of the stack region below
  `sp` is garbage by definition; the default compare config ignores region `stack` and compares only `stack_live`.
  If `sp` is outside `[ram_base, stack_top)`, emit `size 0` and the SHA-256 of the empty string.
* The candidate's 4 MB RAM array must be the plain physical RAM (what the guest sees at `0x80000000..0x803fffff`).
  Memory that the candidate keeps elsewhere (scratchpad `0x1f800000`, VRAM, sound RAM) is NOT part of this contract.

### 3.4 `events`

Event hash chain state after all events that happened BEFORE the checkpoint: `{"w_count","w_chain","r_count","r_chain"}`.

### 3.5 `info`

Free form (frame number, anything useful). Never compared.

## 4. Events (I/O and DMA)

An event is one guest access to a device register range, in program order. Two chains are kept: `w` (writes `W` and DMA
starts `D`) and `r` (reads `R`, not produced by default).

```json
{"k":"W","a":"0x1f801810","d":"0xe1001000"}                       // store
{"k":"R","a":"0x1f801074","d":"0x00000009"}                       // load (only when reads are watched)
{"k":"D","ch":2,"chcr":"0x01000401","madr":"0x80123456","bcr":"0x00000000","n":12,"h":"0x00000000deadbeef"}
```
Optional extra fields (`pc`, `f`, `w` = access width in bytes) are allowed and ignored by the comparer.

* `a` = PHYSICAL byte address of the access (`vaddr & 0x1fffffff`), exactly the address of the accessed byte/halfword/word
  (a `sh` to `0x1f801d82` is `0x1f801d82`, not `0x1f801d80`). *Verified:* MAME debugger `wpaddr`.
* `d` = the value of the access, right aligned and zero extended (`sb`: 8 bit, `sh`: 16 bit, `sw`: 32 bit). The access
  width is NOT part of the event (MAME's debugger does not expose it: a `sw 0x0000ffff` and a `sh 0xffff` to the same
  address are the same event).
* **Watched ranges (writes).** `0x1f000000-3` bank register, `0x1f080000-0x1f083fff` shared RAM (main -> sound CPU),
  `0x1f140000-0x1f140fff` EEPROM, `0x1f700000-0x1f70ffff` tektagt DMA offset, `0x1fb00000-3` and `0x1fb80000-3`
  tektagt protection, `0x1f801000-0x1f801fff` PSX I/O (GPU GP0/GP1, DMA, IRQ, timers, SPU/SIO registers).
  Explicitly NOT watched: the write-only "nopw" ranges `0x1f008000`, `0x1f010000`, `0x1f018000`, `0x1f1bff08`
  (about 1 M writes per 400 frames of no information), RAM, ROM, scratchpad.
* **Reads are not logged by default** (MAME read watchpoints slow the run ~10x and the polled registers - GPUSTAT,
  timers, I_STAT, DMA status, sound RAM handshake - depend on emulated time). With `capture_mame.py --reads` the
  reference additionally logs reads of the EEPROM, `0x1f700000`, `0x1fb00000`, `0x1fb80000` and I_MASK
  (`0x1f801074`). The effect of a wrong read value shows up as a register/RAM divergence at a later checkpoint.
* **DMA start (`D`).** Emitted right after the `W` event of a store to a channel's CHCR (`0x1f801088 + 0x10*ch`,
  ch 0..6) whose value has bit 24 set, at the moment of the store (before the transfer runs):
  `ch`, `chcr` (value written), `madr`/`bcr` = the values currently in the channel's MADR (`CHCR-8`) / BCR (`CHCR-4`)
  registers (raw 32 bit as the guest wrote them), and
  * transfer **from RAM** (`chcr & 1`): `sync = (chcr>>9)&3`; words are read from physical RAM at `addr & 0x3ffffc`;
    `sync==2` (linked list, the GPU command list): start at `madr`; for each node read header `hdr`, feed it, then feed
    `hdr>>24` payload words (`addr+4*i`), stop after the node if `(hdr & 0x00800000) != 0`, else continue at `hdr & 0xffffff`
    (max 0x40000 nodes); `sync==1`: count = `(bcr&0xffff)*(bcr>>16)`; `sync==0`: count = `bcr&0xffff` (0 means 0x10000);
    counts are capped at 0x100000 words; `n` = number of words fed, `h` = FNV-1a 64 over the bytes of every fed
    word, least significant byte first (offset basis `0xcbf29ce484222325`, prime `0x100000001b3`, `h = (h ^ byte) * prime mod 2^64`),
  * transfer **into RAM** (`chcr&1 == 0`): `n` = the count by the same formulas (for `sync==2`/`0` the `bcr&0xffff` rule), `h = 0`.
  *Verified on MAME:* ch2 linked-list starts occur from frame ~375 on (13 in the first 480 frames, e.g.
  `D 2 01000401 ...`). The `h` and `n` values themselves have not been cross-checked against an independent implementation yet.
* **Chain.** `canonical text`: `W,<addr8>,<data8>`, `R,<addr8>,<data8>`, `D,<ch>,<chcr8>,<madr8>,<bcr8>,<n>,<h16>`
  (lower case hex, zero padded; `n` decimal). Chain: `h0 = SHA256("ttt-difftest-events/1")` (hex string),
  `h_i = SHA256(hex(h_{i-1}) + "|" + text_i)` (hex of the UTF-8 bytes of that string), one chain for `W`+`D`
  and one for `R`. `events.{w,r}_{count,chain}` at top level cover the whole run; each checkpoint holds the state at its position.

```csharp
static string Step(string prevHex, string text) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prevHex + "|" + text))).ToLowerInvariant();
```

The sidecar `.events.jsonl` contains the same events in order, one JSON object per line.

## 5. What the candidate does NOT have to match

Instruction timing (frame checkpoints compare only static RAM pages), the dead stack, volatile device registers
(not logged), sound CPU / C352 internals (not modelled by the harness), the nopw ranges, `producer`/`meta`, event
`pc`/`f`/`w` fields. If the candidate legitimately differs elsewhere (e.g. cop0 Cause), add the field to a compare
config (`cpu.ignore_fields`, `cpu.masks`, `ram.ignore_ranges`, `events.ignore_ranges`) - every exclusion must carry a
`reason` and is printed in the report, so a PASS never hides one.

## 6. Minimal staged plan for the lead

1. `pc_hit` `pc_80010000_1`: registers + RAM region hashes with pages. Proves loader + decompressor + entry state
   (the first real H2/H3-style equivalence test: the state right before the game's first instruction).
2. Events `W`/`D` (boot + first frames): proves the I/O sequence (GPU init, DMA setup) independent of timing.
3. Further `pc_hit` ids at function entries chosen from the recompiler's function table (any entry reached
   deterministically, e.g. after loading; add `addr:nth` to `--pcs`), then `frame_*` static-page checks.

## 7. Hashing / determinism rules

`snapshot_sha256` = SHA-256 of the canonical JSON (`sort_keys`, separators `(",", ":")`, ASCII escapes, integers only,
no floats) of the snapshot WITHOUT the keys `producer`, `meta`, `snapshot_sha256` and without `events.file`. The
candidate may omit it. It is used to show that two runs of the same producer are byte-identical in content.

## 8. How the reference is produced / compared

```
python3 tools/difftest/capture_mame.py --out ref.json            # frames 1500, every 60, pc_80010000_1, events
python3 tools/difftest/compare.py ref.json cand.json [--config cfg.json] [--context 5] [--json]
python3 -m unittest discover -s tests/difftest                   # synthetic tests of comparer, capture post-processing, nvram restore
```
Exit codes of `compare.py`: 0 no divergence, 1 divergence, 2 unusable input, 3 (`--strict-unverified`) unverified items.
