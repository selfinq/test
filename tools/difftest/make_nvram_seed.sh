#!/usr/bin/env bash
# make_nvram_seed.sh OUTDIR  -- regenerate the NVRAM seed from an EMPTY nvram: first boot, 1500 frames, plain MAME (no
# debugger) running mame_capture.lua (which pins the uninitialised GTE/hi/lo state, see there) with the wall clock frozen by
# the fixedtime.c LD_PRELOAD shim (the game stores the R4543 RTC date/time in the EEPROM on its first boot).
# WARNING: a first boot from an EMPTY nvram is NOT bit-for-bit reproducible, even with this script: three runs that only
# differ in command line / environment length gave three different EEPROM images (reports/agent09_difftest.md, 'seed').
# So the result is a NEW seed, not a regeneration of the archived one; the archived file in nvram_seed/ is the artifact
# of record. After creating a new seed re-run the determinism check before using it.
set -euo pipefail
out="${1:?usage: make_nvram_seed.sh OUTDIR}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
rompath="${ROMPATH:-$here/../../input/roms}"
fixed="${DT_FIXED_TIME:-1767225600}"
rm -rf "$out"; mkdir -p "$out/nv" "$out/cfg" "$out/ini" "$out/raw"
gcc -shared -fPIC -O2 -o "$out/fixedtime.so" "$here/fixedtime.c"
( cd "$out/raw" && \
  LD_PRELOAD="$out/fixedtime.so" DT_FIXED_TIME="$fixed" TZ=UTC SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy \
  DT_OUT="$out/raw" DT_EVERY=0 DT_MAX_FRAME=1500 DT_EVENTS=0 DT_PCS= \
  /usr/games/mame tektagt -rompath "$rompath" -video none -sound none -nothrottle -seconds_to_run 100 \
    -autoboot_script "$here/mame_capture.lua" -nvram_directory "$out/nv" -cfg_directory "$out/cfg" -inipath "$out/ini" \
    >/dev/null 2>&1 || true )
rm -f "$out"/raw/ram_*.raw
sha256sum "$out/nv/tektagt/at28c16"
