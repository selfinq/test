#!/bin/sh
# Reproduce agent04 dynamic validation (MAME 0.264, headless). Usage: sh run_all.sh [frames]
# Needs: input/roms/tektagt.zip (user supplied), generated/regions/*.bin, /usr/games/mame, python3.
set -e
cd "$(dirname "$0")"
FR=${1:-3000}
export SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy
MAME="/usr/games/mame tektagt -rompath ../../input/roms -video none -sound none -nothrottle -window -skip_gameinfo"
# 1. forced differential test (bank / protection / DMA through MAME's address space)
python3 ../../tests/py_banked/forced_ops.py gen
rm -rf w_ft && mkdir -p w_ft/nvram && cp -r nvram_seed/tektagt w_ft/nvram/
$MAME -nvram_directory $PWD/w_ft/nvram -seconds_to_run 60 -autoboot_script ft.lua > w_ft/mame.out 2> w_ft/mame.err
python3 ../../tests/py_banked/forced_ops.py cmp
# 2. game event trace (bank/prot/dma taps; taps are kept referenced so MAME does not garbage-collect them)
rm -rf w_ev && mkdir -p w_ev/nvram && cp -r nvram_seed/tektagt w_ev/nvram/
NFRAMES=$FR DUMPDIR=$PWD/w_ev EVOUT=w_ev/ev.txt $MAME -nvram_directory $PWD/w_ev/nvram -seconds_to_run 3000 -autoboot_script ev.lua > w_ev/mame.out 2> w_ev/mame.err
python3 ../../tests/py_banked/compare_trace.py w_ev/ev.txt --dumpdir w_ev
