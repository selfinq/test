# MISSING_INPUTS

Expected location: `input/roms/tektagt.zip` (not present; nothing was searched for or downloaded).
Set: MAME `tektagt` (World, TEG2/VER.C1, set 1), per `namcos12.cpp` at MAME commit f8f6b22c3f328b9e0b04ff502989f8536c0d66c0.
Machine-readable list with sizes, CRC32 and SHA-1: `tools/tektagt_manifest.json`.
Check with: `python3 tools/verify_romset.py input/roms/tektagt.zip` (exit 0 only when all 15 match by content).

| Member | Region | Role | Size | SHA-1 |
|---|---|---|---|---|
| teg2verc1.2e | maincpu:rom | main program (even bytes) | 0x200000 | 9e01ae64710d85eb9899d6fa6fd0a2152aee8c11 |
| teg2verc1.2j | maincpu:rom | main program (odd bytes) | 0x200000 | 529a11a1bbb8655534d7ec371f1c09e9e387ed11 |
| teg1_rom0e.ic9 | bankedroms | banked data | 0x800000 | d662dbd89ef62c5ac3150a018fc2d35ef2ee94ac |
| teg1_rom0o.ic13 | bankedroms | banked data | 0x800000 | 8c0bf7f6351c5a2a0996df371a901cf90c68cd8c |
| teg1_rom1e.ic10 | bankedroms | banked data | 0x800000 | 4df20c74ba63f7362caf15e9b8949fab655704fb |
| teg1_rom1o.ic14 | bankedroms | banked data | 0x800000 | f19f1599acbd6fd48793a2ee5a500ca817d9df56 |
| teg1_rom2e.ic11 | bankedroms | banked data | 0x800000 | e3cdb60a4445406877b2e273385f34bfb0974220 |
| teg1_rom2o.ic15 | bankedroms | banked data | 0x800000 | f27e8210ee236c327aa3e1ce4dd408abc6580a1b |
| flel.ic4 | bankedroms | flash | 0x200000 | 6f31acb642c57daccbfdb87b790037e261c8c73c |
| fleu.ic5 | bankedroms | flash | 0x200000 | 6bec8560ad4c122dc909daa83aa9089ba5b281f7 |
| flol.ic6 | bankedroms | flash | 0x200000 | ae35ae28375f2a3e52d72b77ec09750c326cc269 |
| flou.ic7 | bankedroms | flash | 0x200000 | 2f307bc4070fadb510c0473bc91d917b2d845ca5 |
| teg1.11s | sub | H8/3002 sound program | 0x80000 | da164702fc21b9f46a9e32c89e7b1d36070ddf79 |
| teg1_wave0.ic1 | c352 | samples | 0x800000 | f76b0576cc28fe49d3c1c402988b933933e52e15 |
| teg1_wave1.ic12 | c352 | samples | 0x800000 | 601b7e7361ea744b34e3fa1fc39d88641de7f4c6 |

No parent set is required by the driver for `tektagt` (it is the parent of the other tektagt* sets).
Note: the two program ROMs carry the driver comment "Modified to work with alt ROM board?".
Blocked until provided: H1 onward (importer validation on real data, translation, boot).
