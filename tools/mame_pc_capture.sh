#!/bin/bash
# Reproducible PC-set capture from MAME 0.264 (tektagt) starting from the archived NVRAM seed.
# usage: tools/mame_pc_capture.sh NAME FRAMES [RANGES] [INPUT_LUA] [DUMP_EVERY]
#   NAME      output prefix; writes reports/trace_a02/out/NAME.pcs (+ .sha256, .log)
#   FRAMES    stop after this many video frames (60 Hz emulated; 3600 = 60 s)
#   RANGES    candidate virtual-PC ranges "lo-hi,lo-hi" (hex); default covers 4 MB RAM kseg0, low RAM,
#             and the boot-ROM windows 0xbfc00000 / 0x1fc00000 (first 0x30000 bytes)
#   INPUT_LUA optional input-injection script (see tools/mame_input_coinstart.lua)
# env: ROMPATH (default /home/user/test/input/roms), MAME (default /usr/games/mame)
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
NAME=${1:?name}; FRAMES=${2:?frames}
RANGES=${3:-80000000-80400000,00000000-00010000,bfc00000-bfc30000,1fc00000-1fc30000}
INPUT=${4:-}; DUMP=${5:-0}
OUT=$ROOT/reports/trace_a02/out; mkdir -p "$OUT"
W=$(mktemp -d "${TMPDIR:-/tmp}/pcc.XXXXXX")
mkdir -p "$W/nv" "$W/cfg"
cp -r "$ROOT/reports/trace_a02/nvram_seed/tektagt" "$W/nv/"      # fixed NVRAM seed; cfg dir always empty
env PCC_OUT="$OUT/$NAME.pcs" PCC_RANGES="$RANGES" PCC_STOP_FRAME="$FRAMES" PCC_DUMP_EVERY="$DUMP" \
    ${INPUT:+PCC_INPUT="$INPUT"} SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy \
  ${MAME:-/usr/games/mame} tektagt -rompath "${ROMPATH:-$ROOT/input/roms}" -video none -sound none -nothrottle \
    -debug -debugger none -autoboot_script "$ROOT/tools/mame_pc_capture.lua" \
    -nvram_directory "$W/nv" -cfg_directory "$W/cfg" > "$OUT/$NAME.log" 2>&1
sha256sum "$OUT/$NAME.pcs" | tee "$OUT/$NAME.sha256"
rm -rf "$W"
