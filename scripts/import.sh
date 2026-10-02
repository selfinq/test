#!/usr/bin/env bash
# import: verify ROM zip (by content) -> build regions -> decompress boot image -> check image hash.
# env: ROM_ZIP (default <repo>/input/roms/tektagt.zip), PYTHON. Never downloads anything.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
step "locate python and ROM zip"
find_python
[[ -f "$ROM_ZIP" ]] || die "ROM zip not found: $ROM_ZIP (supply your own tektagt.zip; set ROM_ZIP or put it at input/roms/tektagt.zip; this project never downloads ROMs)"
echo "ROM zip: $ROM_ZIP"
mkdir -p generated

step "verify ROM set by content (tools/verify_romset.py)"
"$PYTHON" tools/verify_romset.py "$ROM_ZIP" --json generated/romset_verify.json > generated/romset_verify.stdout.json \
  || die "ROM set incomplete or mismatched (exit $?); see generated/romset_verify.stdout.json"
"$PYTHON" -c "import json; r=json.load(open('generated/romset_verify.json')); print('complete =', r['complete'], '| ok members =', len(r['ok']))"

step "build memory regions (tools/build_regions.py)"
"$PYTHON" tools/build_regions.py "$ROM_ZIP" --out generated/regions

step "decompress boot image (tools/ttt_decompress.py)"
SRC_OFF="$("$PYTHON" -c "import json,os; p='tests/expected_image.json'; print(json.load(open(p))['source_rom_offset'] if os.path.exists(p) else '0x28000')")"
echo "source ROM offset: $SRC_OFF"
"$PYTHON" tools/ttt_decompress.py generated/regions/maincpu_rom.bin "$SRC_OFF" generated/ttt_image.bin

step "check image against tests/expected_image.json (MAME RAM hash)"
if [[ -f tests/expected_image.json ]]; then
  EXP="$("$PYTHON" -c "import json; print(json.load(open('tests/expected_image.json'))['image_sha256'])")"
  GOT="$(sha256_of generated/ttt_image.bin)"
  [[ "$EXP" == "$GOT" ]] || die "generated/ttt_image.bin sha256 $GOT != expected $EXP"
  ok "image sha256 matches expected ($GOT)"
else
  echo "tests/expected_image.json missing: hash check skipped"
fi
echo; echo "import finished OK"
