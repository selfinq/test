#!/usr/bin/env bash
# regenerate: run the patched RecompOne recompiler on generated/ttt_image.bin with tools/ttt_raw.json.
# env: RECOMPONE_DIR, RECOMP_OUT (output dir, default <repo>/generated/recomp)
# NOTE: the current output is ~206 MB of C#; this takes a while and needs several GB of RAM.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
step "check inputs"
find_python; check_dotnet
[[ -f generated/ttt_image.bin ]] || die "generated/ttt_image.bin missing: run scripts/import.sh first"
[[ -f tools/ttt_raw.json ]] || die "tools/ttt_raw.json missing"
DLL="$(recompiler_dll)"
[[ -f "$DLL" ]] || die "recompiler not built ($DLL): run scripts/setup.sh first"
RECOMP_OUT="$("$PYTHON" -c "import os,sys; print(os.path.abspath(sys.argv[1]))" "$RECOMP_OUT")"
DEFAULT_OUT="$("$PYTHON" -c "import os,sys; print(os.path.abspath(sys.argv[1]))" "$REPO/generated/recomp")"

CONFIG="$REPO/tools/ttt_raw.json"
if [[ "$RECOMP_OUT" != "$DEFAULT_OUT" ]]; then
  step "write config override for output dir $RECOMP_OUT"
  CONFIG="$REPO/generated/ttt_raw.override.json"
  "$PYTHON" - "$REPO" "$RECOMP_OUT" "$CONFIG" <<'PY'
import json, os, sys
repo, out, dst = sys.argv[1:4]
c = json.load(open(os.path.join(repo, "tools", "ttt_raw.json")))
c["image"] = os.path.join(repo, "generated", "ttt_image.bin")
c["game"]["output"] = out
json.dump(c, open(dst, "w"), indent=2)
PY
fi

step "run recompiler: dotnet recompone.dll $CONFIG"
mkdir -p "$RECOMP_OUT"
LOG="$REPO/generated/regenerate.log"
if ! dotnet "$DLL" "$CONFIG" > "$LOG" 2>&1; then
  grep -v '^\[Unknown\]' "$LOG" | tail -40 >&2 || true
  die "recompiler exited non-zero; full log: $LOG"
fi
grep -v '^\[Unknown\]' "$LOG" | head -60 || true

step "check outputs"
[[ -s "$RECOMP_OUT/main.cs" ]] || die "no $RECOMP_OUT/main.cs produced; see $LOG"
grep -q "Recompilation finished" "$LOG" || die "recompiler did not report 'Recompilation finished'; see $LOG"
ok "$(ls "$RECOMP_OUT" | wc -l) file(s) in $RECOMP_OUT; main.cs $(wc -c < "$RECOMP_OUT/main.cs") bytes"
echo "[Unknown] decode warnings in log: $(grep -c '^\[Unknown\]' "$LOG" || true)"
echo; echo "regenerate finished OK"
