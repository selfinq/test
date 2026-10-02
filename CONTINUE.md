# CONTINUE
Last reproducible state: base RecompOne builds; ROM manifest/verifier tested on synthetic zips; no real ROMs.
Next concrete step: H0/H2 - extend RecompOne (separate checkout) with a System 12 importer taking explicit bytes+addresses from generated/regions/*.bin; validate main ROM layout against MAME (trace the boot, dump RAM after boot copy).
install MAME, pin its version against f8f6b22, run `-listxml/-listroms/-verifyroms tektagt` and save outputs in `reports/`.
Then write the System 12 importer (explicit bytes/addresses; see PHASE1_AUDIT gaps), test it on synthetic images first.

Done since: tools/build_regions.py builds the 4 regions from the driver load rules (synthetic tests only; byte order inside LOAD32_WORD must be confirmed against MAME on real data).
