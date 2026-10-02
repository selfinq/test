# agent03 - System 12 `tektagt` address map and device specification

Status: finished. Nothing committed or pushed; no ROM downloaded; only `docs/system12/` and `reports/trace_a03/` (+ this file) were written.
All dynamic evidence is from MAME 0.264 (`/usr/games/mame`) running the user-supplied `tektagt` set; source evidence is MAME master `f8f6b22`
(`reports/refs/namcos12.cpp`) plus the PSX CPU/IRQ/DMA/RCNT/SIO/AT28C16 sources fetched read-only at `f8f6b22` and at tag `mame0264`
(not stored in the repo).

## Deliverables

| File | Content |
|---|---|
| `docs/system12/ADDRESS_MAP.md` | Main-CPU physical map (42 rows: range, device, R/W, width, notes, evidence line, status), H8 appendix (7 rows), dynamic configuration, ROM mapping, MAME 0.264-vs-master delta, implementation checklist |
| `docs/system12/DEVICES.md` | Per-device behaviour: CPU, RAM/ROM/VRAM, IRQ, vblank, DMA (all channels, CHCR patterns, timing), ROM-DMA + tektagt protection state machine, root counters, GPU/GTE/MDEC, SIO/SPU/CD, EEPROM/shared RAM/strobes, H8 interface, per-device diff vs PS1, verification ledger |
| `docs/system12/address_map.json` | Machine-readable twin (`schema: system12.address_map/1`): segments, memories, 42+7 map rows with numeric `start`/`end`, `dynamic_config`, `rom_dma`, `tektagt_protection` (full response table), `interrupts`, `h8_sound_cpu` (+ input bit map), `differences_from_home_ps1`, `hypotheses` |
| `docs/system12/gen_address_map.py` | Single source for the JSON and the two tables inside ADDRESS_MAP.md (`python3 docs/system12/gen_address_map.py`) |
| `reports/trace_a03/*.lua` + `*.txt` | Probes and their outputs (listed below) |

## Headline results (all with evidence in the docs)

1. **Final map seen by the game.** RAM 4 MB at `0x00000000` (+kseg0/1), final `RAM_SIZE=0xcb4`; program ROM 4 MB at `0x1fc00000` (`ROM_CFG=0x163088`; boot code raises it from the 512 KB reset window); scratchpad via BIU `0x1e988`; bus error everywhere else outside the listed devices. The CPU view of `0x1fc00000..` equals `maincpu:rom` word for word (1,048,576 words, 0 mismatches); RAM alias test and "no mirror above 4 MB" confirmed.
2. **Banked ROM is only reachable through DMA channel 5**, not through the `0x1fa00000` window or the bank register (never written: 0 writes in 1500 frames; the window gets one dummy read per chunk). Two source-select paths: (a) *armed banked read*: `sw offset -> 0x1fb00000` + `lhu 0x1f700000` (arming) + DMA5 `CHCR=0x01000000`; (b) *main-ROM read*: `sh 0x84xx -> 0x1f70xxxx` (bit 31 of `X | D<<16`). **Independently re-implemented and checked: 7634/7634 DMA transfers in 1200 frames identical to MAME's RAM** (`reports/trace_a03/dma_verify.txt`). DMA5 is synchronous (copy + DICR flag + I_STAT bit 3 inside the CHCR store).
3. **Resolves the open item in `PHASE1_AUDIT.md`** ("0 write-tap hits for `0x1f000000`/`0x1fb00000` in 4000 frames, mechanism not identified"): MAME 0.264 silently drops older Lua taps on the `0x1f80xxxx`/`0x1fxxxxxx` range around frame 360; `handle:reinstall()` each frame fixes it (`tapdbg.lua`). With that, `0x1fb00000` shows 7,984 writes and the arming read 7,978. Any earlier trace in `reports/trace/` that relied on such taps after frame ~360 is incomplete.
4. **Protection (PROT1/PROT2) state machine** reproduced exactly (cnt reset by write to `0x1fb80000`, two 32-bit captures, responses `0x36e2/0x2651/0x4186/0x3c7d`, generic `0xa9` rule `0x552e`). Static disassembly shows the challenge values are computed from fixed code addresses of the game image (`0x80010240 ^ 0x55555555 = d5545715`, etc.), so the four table pairs map to four check sites; the site at `0x8014c178` uses the generic rule. Observed in attract mode: only the two pairs from `0x800f8e64`.
5. **Byte-order item closed:** MAME's loaded `maincpu:rom` and `bankedroms` equal `generated/regions/maincpu_rom.bin` / `bankedroms.bin` byte for byte (sha256 `8f59ab62...`, `e0686230...`), i.e. `tools/build_regions.py` LOAD32_WORD/LOAD32_BYTE/LOAD16_BYTE handling is correct for these two regions (`STATUS.json` "not_run" item). `sub`/`c352` regions were not compared.
6. **Board devices:** shared RAM = 1:1 byte window `0x1f080000+x <-> H8 0x080000+x` with 16-bit element semantics (verified from both sides); EEPROM byte `i` at `0x1f140000 + 2*i` (verified with real data: build string at index `0x280` read at `0x1f140500`), AT28C16 busy/data-polling model (200 us); write-only strobes at `0x1f008000` (master only), `0x1f010000` (unused), `0x1f018000` (1.1 M halfword-0 writes in 1500 frames - performance-relevant), ROM-board port `0x1f1bff08-0f` (boot ROM streams ~1 M halfwords into it).
7. **Interrupts/vblank:** I_STAT bit0 once per frame from the GPU vblank edge; used sources are VBLANK (bit0), DMA (bit3), root counter 1 (bit5, hblank-sourced raster timer); mask values `0/1/9/0x29`; frame period 0.016715113 s (59.826 Hz) at 256x240 and 640x240. The screen vblank (both edges) drives H8 IRQ1. MAME's `I_STAT` write rule (`stat &= mask & data`) differs from documented hardware (`stat &= data`).
8. **Hardware present on the die but unused:** SIO0/1, CD, SPU registers (unconnected: read 0 / write ignored); **MDEC is used** (FMV from frame ~641: ch0 `0x01000201`, ch1 `0x01000200`).
9. **The ROM carries its own Sony-style kernel** (copied to low RAM, PCs `0x1810`, `0x296c`, ...). RecompOne's PS1 BIOS HLE is not applicable as is.
10. **MAME 0.264 vs master** differences for tektagt are small and listed (section 8 of ADDRESS_MAP.md): `0x1f008000` mapping, `0x1f700000` mirror, hardware-register aliases, SPU range, H8 input/port-8 implementation.

## Gaps in RecompOne's memory model relevant to this map (observations, no code changed)

`MemoryMap.RetailRamSize` 2 MB (needs 4 MB; `PSMemory(ramSize)` accepts a power of two); `BiosSize` 512 KB (needs 4 MB ROM at `0x1fc00000`); `Resolve()` throws for anything in `0x1f000000-0x1f7fffff`/`0x1fa00000+` (needs the devices in ADDRESS_MAP.md); `Interrupts.WriteStat` is hardware-style (`&= value`) while the MAME reference is `stat & mask & data`; VRAM `const` 1024x512 (needs 1024x1024; see `GPU.md`); no DMA5 ROM engine, EEPROM, shared RAM, protection windows, bank window.

## Probes (all in `reports/trace_a03/`)

`map_dump.lua` (static maps of main+H8, devices, regions) -> `map_dump.txt`; `mainview.lua` (cross-CPU shared RAM, EEPROM lanes, bank window, ROM) -> `mainview.txt`; `access_trace.lua` (aggregate of every non-RAM access, 1800 frames, uses `reinstall`) -> `access_trace_1800.txt`; `final_checks.lua` (ROM window compare, RAM aliases, IRQ/DMA/protection sequences) -> `final_checks.txt`; `dma_verify.lua` -> `dma_verify.txt`; `eeprom_trace.lua` (fresh NVRAM) -> `eeprom_trace.txt`; `dump_regions.lua` (region dump for sha256 compare; dumps stay in scratch); `vbl.lua` -> `vbl.txt`; `tapdbg.lua`, `api*.lua` (tooling checks). Command line used: `SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy /usr/games/mame tektagt -rompath /home/user/test/input/roms -video none -sound none -nothrottle -seconds_to_run N -autoboot_script x.lua -nvram_directory /tmp/nv_a03* -cfg_directory /tmp/cfg_a03*`.
NVRAM provenance matters (EEPROM is written on first boot): `eeprom_trace` and `final_checks`/`vbl` started with empty NVRAM, `access_trace_1800` reused a populated file.

## Not verified / HYPOTHESES (also in the JSON)

* Purpose of `0x1f008000/0x1f010000/0x1f018000` (watchdog-like?) and of `0x1f1bff08-0f` (ROM-board/KEYCUS port?); only their write behaviour and rates are proven.
* Real-hardware CPU speed (MAME models 1 instruction per 4 clocks = 25 M/s; the 25 M/s rate itself was measured), vblank duration (~2.5 ms assumed), real behaviour of unmapped addresses (MAME: 0/ignore).
* Whether the game ever issues protection pairs beyond the two seen in attract mode (static code shows four sites + generic rule; the table covers them by construction).
* Master's `mirror(0x800000)` on the arming read has no effect (shadowed by ROM) - not tested on master (only 0.264 binary available).
* Exact bounds of the kernel image in the boot ROM; GTE bit-exactness MAME vs RecompOne; EEPROM contents written after frame ~800 (observed in a longer run's NVRAM file only).
* H8 sound/input behaviour is described at interface level only (no H8 firmware analysis).
