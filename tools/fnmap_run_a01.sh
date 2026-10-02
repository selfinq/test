#!/bin/bash
# build private copy of the recompiler with the current Analysis/ sources, run strict mode, analyze
set -e
cp /home/user/work/recompone/RecompOne.Recompiler/Analysis/*.cs /home/user/work/a01/recompone/RecompOne.Recompiler/Analysis/
cd /home/user/work/a01/recompone
dotnet build RecompOne.Recompiler -c Release 2>&1 | grep -E " error |Error\(s\)" | head
RECOMPONE_FNMAP_OUT=/home/user/test/tools/fnmap_a01.tsv RECOMPONE_STRICT_DETECT=${STRICT:-1} dotnet run --no-build --project RecompOne.Recompiler -c Release -- /home/user/test/tools/ttt_raw_a01.json > /home/user/test/reports/a01_run.log 2>&1
grep -v '^\[Unknown\]' /home/user/test/reports/a01_run.log | tail -8
echo unknown words: $(grep -c '^\[Unknown\]' /home/user/test/reports/a01_run.log)
python3 /home/user/test/tools/fnmap_analyze.py /home/user/test/tools/fnmap_a01.tsv /home/user/test/generated/ttt_image.bin
ls -l /home/user/test/generated/recomp_a01/main.cs
