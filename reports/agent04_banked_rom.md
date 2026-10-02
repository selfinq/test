# Agent 04 - banked ROM window, ROM->RAM DMA, tektagt protection

Status: implemented (C# + Python twin), validated against MAME 0.264 (Ubuntu build, same binary as the rest of the project)
with 1099 forced bus/DMA operations (0 mismatches) and with the recorded bus traffic of the real game
(boot + 20000 frames of game traffic: 905,900 bus events, 70,627 ROM->RAM DMAs, 378,449 predicted read values all equal).
No ROM data is stored in the repo by this work; all ROM-derived inputs are read from `generated/regions/*.bin` (user supplied) and every
test that needs them skips when they are absent.

## 1. Why the earlier tap on 0x1f000000 saw 0 hits

Two independent reasons, both demonstrated:

1. **The Lua tap handle was garbage collected.** In MAME 0.264 `install_read_tap/install_write_tap` return a handle and the tap is
   removed when that handle is collected. Scripts that did `mem:install_write_tap(...)` without keeping the return value
   (`reports/trace/boot.lua`, my `probe2.lua`) stop seeing anything after the first Lua GC cycle (a few hundred frames in).
   Evidence: `probe2.lua` (handles dropped) logged only boot-time events over 6000 frames (counts frozen at e.g. 512 GPU-data writes, 33 GPU-control writes,
   not a single 0x1f08xxxx / 0x1f70xxxx / 0x1fb0xxxx access, identical to the 1500-frame `probe1`); `probe3.lua` (same script, but the returned handles are
   kept in a global table) over the same 6000 frames counted ~1.0M accesses in those ranges, including 23108 x `R` and 23108 x `W` of 0x1f700000 and 49817 x `R 0x1fb00000`.
   The only difference between probe2 and probe3 is retaining the handles, which is consistent with (not a proof of the exact mechanism) tap removal on collection.
   The same early-death also explains why the previous agent saw no DMA-channel-5 activity.
2. **tektagt really never touches the bank register.** With working taps (`ev.lua`), over the whole run there are 0 reads and 0 writes
   to 0x1f000000..3, and MAME's own `mainbank` entry is still 0 after 3000 frames (`mainbank_entry_f3000.txt`). The game loads
   banked data exclusively through DMA channel 5 (+ the `m_has_tektagt_dma` mechanism); the only access to the 0x1fa00000 window is
   one halfword read of 0x1fa00000 after every DMA (always bank 0, value 0x0015; matches `bankedroms.bin[0:2]`).

Addresses the game actually touches in this region (physical, taps, 6000 frames):
`W 0x1f700000..0x1f70ffff` (dmaoffset_w; 0x1f700000 itself and 0x1f70xxxx in the table-building code), `R 0x1f700000`
(protection_3_r, halfword), `R 0x1fa00000` (halfword), `R/W 0x1fb00000`, `R/W 0x1fb80000`, plus PSX DMA channel 5
(`0x1f8010d0/d4/d8`) and DPCR (`0x1f8010f0`). Not touched: 0x1f000000..3 and every other offset of the bank window.

## 2. Behaviour implemented (all ported from `reports/refs/namcos12.cpp`, `psxdma_device` from mame@f8f6b22 `dma.cpp`)

Bus conventions (verified, not assumed): little-endian 32-bit bus. All handlers are 16-bit: a 32-bit access = two handler calls (offset 0 =
low half, then offset 1), a halfword access = one call, a byte access = one call with the byte in its lane (handlers ignore mem_mask).

| Item | Behaviour |
|---|---|
| `0x1f000000-3` read | `nopr` -> 0 |
| `0x1f000000-1` write (`bankoffset_w`) | `mainbank.set_entry(data16)`; 28 entries of 2 MiB for tektagt (`bankedroms` = 0x3800000). Out-of-range entry: MAME throws `memory_bank::set_entry called ... invalid bank entry 28` (fatal error in a normal run) -> C# `BankOutOfRangeException`. 32-bit write: only the low half is mapped; byte write at lane 1 sets `data<<8`. `m_bankoffset` is never written (only used by alt-bank boards: flag `AltBank`). |
| `0x1fa00000-0x1fbfffff` | read-only window = `bankedroms[entry*0x200000 + (a-0x1fa00000)]`, little-endian; writes ignored. Overlaid by protection 1 (0x1fb00000-3) and 2 (0x1fb80000-3). |
| `0x1f700000-0x1f70ffff` write (`dmaoffset_w`) | `m_dmaoffset = (halfword_offset*2) \| (data16 << 16)`: the game encodes the **high 16 bits in the data** and the **low 16 bits in the address** (e.g. `sh 0x8421, 0x1f70de54` -> 0x8421de54). 32-bit write = 2 calls, last wins. |
| `0x1f700000` read (`protection_3_r`) | returns 0 and sets `m_has_tektagt_dma = true` (halfword at +0 or +2, byte +0/+1, or 32-bit). Reads at 0x1f700004+ do not set it. The driver's `mirror(0x800000)` (0x1ff00000) is **not effective** in MAME 0.264: it is shadowed by the boot ROM (forced test: flag not set by a 0x1ff00000 read), so it is not modelled. |
| `0x1fb00000` write (`protection_1_w`) | offset 0: `tektagdmaoffset = data16` (clears upper half!); if `cnt!=2`: `ttt_val[cnt] = data16`. offset 1: `tektagdmaoffset \|= data16<<16`; if `cnt!=2`: `ttt_val[cnt++] \|= data16<<16`. After two values (`cnt==2`) the values freeze but `tektagdmaoffset` keeps following writes (this is the "second DMA offset" register). |
| `0x1fb00000` read (`protection_1_r`) | offset 0: 0x8000 (busy bit 15 set, so a poll loop never waits), offset 1: 0. |
| `0x1fb80000` write (`protection_2_w`) | offset 0 (any value; the game writes 0 or rand()): `cnt = 0`. offset 1: nothing. |
| `0x1fb80000` read (`protection_2_r`) | only if `cnt==2`: (806d2c24,d5545715)->offset0 0x36e2; (804c2c84,d5545615)->offset0 0x2651; (2aaba8e6,00820040)->offset1 0x4186; (2aaba592,01780544)->offset1 0x3c7d; any other pair with `(ttt_val[1]>>16)&0xff == 0xa9` -> 0x552e at **both** offsets; everything else 0. Known pairs return 0 at the other offset (no fall-through to the 0xa9 test). |
| DMA ch5 source (`namcos12_rom_read`) | `offset = has_tektagt_dma ? tektagdmaoffset : dmaoffset` (**byte** offset, signed int). Main ROM if bit 31 set or CPU `exp_base()==0x1f300000`: `offset &= 0xffffff` (intentionally not 0x3fffff); else banked ROM: `offset &= 0x7fffffff` (+`bankoffset*0x200000` only for alt-bank boards when not tektagt). Flag cleared. Then `offset/=2` (halfword index, so an odd offset is floored), `size` clipped to `(rom_halfwords-offset)/2` and to RAM words left (C int division, negative sizes copy nothing), words copied little-endian from consecutive 4-byte groups starting at `offset*2`. |
| PSX DMA wrapper (channel 5, DPCR bit 23, CHCR bit 24) | `madr & (ramsize-1)`; `chcr==0x01000000`: size=BCR (int32); `chcr==0x01000200`: size=(BCR&0xffff)*(BCR>>16 or 0x10000) as uint32->int32; `(chcr&0xffbffeff)==0x11000000`: `max(BCR>>16,1)*(BCR&0xffff)`. Transfer is immediate (inside the CHCR write), then `dma_finished`: CHCR bits 24/28 cleared, DICR flag bit 29 set only if enabled (the `dicr &= 0x00ffffff \| dicr<<8` idiom drops flags whose enable bit is clear). Other CHCR values do nothing on ch5 (no write handler). |

Game-level protocol (from the trace; PCs in the game image): per ROM read the routine at 0x8012e500 does
`lhu 0x1fb80000` (ignored) -> `sw offset,0x1fb00000` -> `lhu 0x1fb00000` (busy poll) -> `sh 0,0x1f700000` -> `lhu 0x1f700000` (arms the flag)
-> (0x8017b994..0x8017b9dc) DPCR, MADR, BCR, `CHCR=0x01000000` -> `lhu 0x1fa00000`, `lhu 0x1fb00000`. Reads come as a 1-word "header"
DMA to 0x803fffa8 followed by the payload DMA at offset+4. A second use is the periodic (every ~2 frames) protection exchange at
0x8010a6f8..0x8010a778 (`sh 0,0x1fb80000`, two `sw` to 0x1fb00000, `lw 0x1fb00000`, `lw 0x1fb80000`) which in all observed cases presents the pair
(2aaba592,01780544) and gets 0x3c7d0000. Another path (not through the flag) writes `sh 0x8421,0x1f70xxxx` and DMAs with CHCR 0x01000000:
that is **main-ROM** DMA through `dmaoffset` bit 31 (111 transfers in the first 1500 frames).

## 3. Files

* `src/System12.Runtime/Banked/BankedRomController.cs` - `BankedRomController` (Read32/16/8, Write32/16/8, `HandlesRead/HandlesWrite`, handler methods
  `BankOffsetW/DmaOffsetW/Protection{1,2}{R,W}/Protection3R`, `ProduceDmaSource(ramAddress, sizeWords)` (stateful, consumes the flag),
  static `ComputeDmaSource(...)` (stateless core), `DmaRomRead(ram, address, size)` convenience). Namespace `System12.Runtime.Banked`, no csproj.
* `src/System12.Runtime/Banked/PsxDmaChannel5.cs` - channel 5 / DPCR / DICR wrapper exactly as `psxdma_device` drives the ROM handler.
* `tests/py_banked/bankrom_ref.py` (Python twin), `test_banked.py` (41 unittest cases), `compare_trace.py` (trace replay), `forced_ops.py`
  (forced-op generator/comparer), `cs_harness/` (C# cross-check: `sh tests/py_banked/cs_harness/run_cs_crosscheck.sh`, builds a throw-away project in `$TMPDIR`).
* `reports/trace_a04/`: `ev.lua` (event tracer), `ft.lua` + `ft_ops.lua` + `ft_result.txt` (forced test + MAME's answers), `run1500/` (plain log, used by the unit test), `run8k/`, `run20k/`, `run8k_seq305020/` (gzipped event logs, first DMA dumps, compare output), `probe2.lua`/`probe3.lua` (+logs: tap-GC evidence), `run_all.sh` (reproduces everything), `nvram_seed/`
  (pristine copy of /tmp/nv2/tektagt used as the starting NVRAM for all runs).
* Run unit tests: `python3 -m unittest discover -s tests/py_banked -v`.

Integration notes for the runtime owner: route physical (kseg-masked, `addr & 0x1fffffff`) accesses for `[0x1f000000,0x1f000004)`, `[0x1f700000,0x1f710000)`
(write) / `0x1f700000..7` (read), `[0x1fa00000,0x1fc00000)` to this class (`HandlesRead/HandlesWrite`); hook PSX DMA channel 5 to `PsxDmaChannel5` (or call
`DmaRomRead` from your own DMA code with the already-masked address); set `ExpBase` from writes to 0x1f801000 (the boot code writes 0x1f000000; the forced test wrote 0x1f300000 and MAME's DMA took the main-ROM path, so the written value is what is compared).
Unaligned halfword/word stores cannot occur (MIPS traps), so the model aligns addresses.

## 4. Validation

### 4.1 Forced differential test (MAME address space vs model)
`ft.lua` runs 1099 operations through `address_space:read/write_u8/u16/u32` of the live machine at frame 5 (real handler/lane splitting) and logs the results:
bank selection (entries 0,1,5,13,27 + random window reads at 8/16/32 bit, 32/16/8-bit bank-register writes, read-only window), 9 protection value pairs (all four
constants, the 0xa9 branch, near misses, freeze after two values, reset by 16-bit/byte writes, half-word write sequences, the high-half-first quirk), flag-setting
reads of 0x1f700000 at every width and offset, and 73 DMAs (banked via flag, banked via `dmaoffset`, main ROM via bit 31 and via `exp_base=0x1f300000`, odd
offsets, ROM-end/RAM-end truncation, offsets past the ROM, negative/zero/huge sizes, address masking of 0xa0200000, CHCR 0x01000200 / 0x11000000 / 0x11400100,
unknown CHCR, DPCR bit 23 clear, DICR enable/ack interplay). Every DMA is read back (pre-filled pattern, so truncation extent is visible) together with CHCR and DICR.
Result: **1099/1099 lines identical** (`python3 tests/py_banked/forced_ops.py cmp`; also reproduced by the C# implementation: `ft: ops=1099 mismatching=0`).
This only covers the bus/DMA behaviour as seen through MAME's address-space API, not CPU instruction execution.

### 4.2 Real game traffic
`ev.lua` taps the bank/DMA-offset/protection windows and the DMA channel-5 registers, replays each event through the model (read values are predictions, DMA results are
checked by hashing 256-word blocks of RAM at the next bus event, and byte-exactly for the first dumped transfers).
| Run (same NVRAM seed, deterministic) | frames | bus events | predicted reads (all equal) | DMAs (flag / `dmaoffset` main ROM / `dmaoffset` banked) | 256-word RAM blocks equal / different |
|---|---|---|---|---|---|
| `run1500` | 1500 | 88,818 | 39,146 (0 wrong) | 7,939 (7,828 / 111 / 0) | 13,135 / 11 |
| `run8k` | 8000 | 304,738 | 126,697 (0 wrong) | 24,164 | 52,237 / 11 |
| `run20k` | 20000 | 905,900 | 378,449 (0 wrong) | 70,627 (69,929 / 698 / 0), 25.2 M words copied, banked source offsets 0 .. 0x32b5054 (53.2 MB of the 58.7 MB region) | 143,704 / 14 |

Details: no write/read of 0x1f000000 and bank entry always 0; the periodic protection exchange only ever presents the four known pairs (8000-frame run: one boot-time exchange each for
806d2c24/d5545715, 804c2c84/d5545615, 2aaba8e6/00820040, then 3598 x 2aaba592/01780544), always answered as the model predicts; the 0xa9 branch is never used by the game (only by the forced test).
The game uses no byte accesses to these windows and no banked-ROM DMA through `dmaoffset` (only through the tektagt flag); `dmaoffset` DMAs are all main-ROM (bit 31).

The 11 non-identical DMA hash blocks that appear in every run are the same early main-ROM transfers (sequence numbers 65, 75, 85, 95, 171, 203, 521, 531, 651, 661, 803) and are, with high confidence, **not** model errors:
the byte dumps (`run1500/dma_dump_*.bin`) differ from the model in exactly every second word, each by the same constant `0x04000000 + source_offset - 4` (the game
CPU relocates a pointer table in place right after the DMA, before the next bus event I look at). `compare_trace.py` classifies these automatically
(`dump_fixup_explained`). All 11 were verified from byte dumps (`run1500/`).

**One remaining difference I cannot fully explain** (appears first in the 20k run, frame 8015): DMA seq 305020 (main ROM offset 0x21b36c via `dmaoffset`, 1300 words to 0x3988a8). Byte dump
(`run8k_seq305020/dma_dump_13.bin`): 1296 of 1300 words are identical to the model, 4 words differ: index 3 (0 -> 0x00300200), 134 (0x10 -> 0x02800210), 651 (0 -> 0x00300200),
782 (0x00400010 -> 0x02c00210). These look like OR-ed flag/pointer fields patched by the game CPU after the transfer (a DMA-engine error would not produce four isolated words),
but I could not prove it: my attempt to catch the CPU stores with debugger watchpoints produced no output (inconclusive, discarded). Treat it as "very likely CPU post-patching, unproven".

### 4.3 Mismatches / surprises found while validating (honest list)
1. Driver `mirror(0x800000)` of the protection-3 read is not effective in MAME 0.264 (boot ROM shadows 0x1ff00000): model does not mirror (my first model did, and the forced test caught it).
2. The 11 CPU-relocated tables above (expected behaviour, not a bug).
3. Unaligned halfword write to 0x1f700001 (invalid on MIPS) behaves differently in MAME than my aligned model; removed from the tests, unreachable from the CPU.
4. MAME quirk reproduced on purpose: `tektagt_state::machine_start` does `std::fill_n(m_ttt_val, 0, ...)` (count 0, fills nothing), so `m_ttt_val` relies on zeroed storage;
   it only matters if the high half is written before the low half (never in the game).
5. MAME's bank out-of-range is a MAME exception (fatal in a normal run); the C# class throws `BankOutOfRangeException`. Not reachable from the game (bank never written).

## 5. Not done / limits
* No CPU-instruction-level or recompiled-code validation (this component has no recompiled caller yet).
* Alt-bank boards (`AltBank`, `bankoffset`) and `exp_base==0x1f300000` games are modelled from the driver and exercised only by the forced test, not by real game traffic.
* The post-DMA RAM check happens at the next bus event, so code that rewrites the destination immediately (the 11 relocated tables) shows up as a difference; I verified those separately.
* "Attract mode" itself was not independently confirmed; the runs cover boot + the first 20000 frames of game execution (game starts running from RAM at frame ~367).
* Concerns for the recompiler: 70k transfers (24k in the first 8000 frames) write into RAM ranges `0x2fe500..0x3a5370` and `0x3dfaf8..0x3fffc8`; `0x2fe500..0x3a8350` lies inside the decompressed boot image
  (`ttt_image.bin`, all 70 transfers that fall fully inside differ from the image bytes). 63 of the 3418 function entries found by the raw-image recompiler
  (`generated/recomp/main.cs`, e.g. 0x300000, 0x300104, 0x300238) are inside ranges that are overwritten by DMA at run time: check whether those are really code.
