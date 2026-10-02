# CONTINUE
Last reproducible state: base RecompOne builds; ROM manifest/verifier tested on synthetic zips; no real ROMs.
Next concrete step: H0/H2 - extend RecompOne (separate checkout) with a System 12 importer taking explicit bytes+addresses from generated/regions/*.bin; validate main ROM layout against MAME (trace the boot, dump RAM after boot copy).
install MAME, pin its version against f8f6b22, run `-listxml/-listroms/-verifyroms tektagt` and save outputs in `reports/`.
Then write the System 12 importer (explicit bytes/addresses; see PHASE1_AUDIT gaps), test it on synthetic images first.

Done since: tools/build_regions.py builds the 4 regions from the driver load rules (synthetic tests only; byte order inside LOAD32_WORD must be confirmed against MAME on real data).

## State after raw-image recompilation run
- Reproduce: `python3 tools/build_regions.py input/roms/tektagt.zip` then `python3 tools/ttt_decompress.py generated/regions/maincpu_rom.bin 0x28000 generated/ttt_image.bin`;
  apply `patches/0001-raw-image-mode.patch` to a RecompOne checkout at 8d0091c; `dotnet run --project RecompOne.Recompiler -c Release -- tools/ttt_raw.json`.
- Known problems: (1) function detector walks through data after unconditional jumps (huge functions, 761k [Unknown] words);
  (2) no coverage check against MAME PCs yet; (3) the System 12 runtime does not exist (Entry.cs is a NotImplemented stub).
- Next concrete test: gather PCs executed by MAME during attract mode (needs a per-instruction hook; Lua frame samples are too sparse) and assert each lies in a detected function.
