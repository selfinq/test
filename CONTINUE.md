# CONTINUE
Last reproducible state: base RecompOne builds; ROM manifest/verifier tested on synthetic zips; no real ROMs.
Next concrete step: place the authorised set at `input/roms/tektagt.zip`, run `tools/verify_romset.py`, then
install MAME, pin its version against f8f6b22, run `-listxml/-listroms/-verifyroms tektagt` and save outputs in `reports/`.
Then write the System 12 importer (explicit bytes/addresses; see PHASE1_AUDIT gaps), test it on synthetic images first.
