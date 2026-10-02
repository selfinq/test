#!/usr/bin/env bash
# regenerate: run the patched RecompOne recompiler on the decompressed image with a raw-mode config.
# env: RECOMPONE_DIR            RecompOne checkout (default <repo>/../work/recompone)
#      RECOMP_CONFIG            config to use (default <repo>/tools/ttt_raw.json; e.g. tools/ttt_raw_a01.json)
#      RECOMP_OUT               output dir; if set and different from the config's game.output, a copy of the config
#                               with that output dir is written to generated/ttt_raw.override.json and used
# NOTE: the default config's output is ~206 MB of C#; this takes about a minute and needs several GB of RAM.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
step "check inputs"
find_python; check_dotnet
CONFIG="${RECOMP_CONFIG:-$REPO/tools/ttt_raw.json}"
[[ -f "$CONFIG" ]] || die "config not found: $CONFIG"
DLL="$(recompiler_dll)"
[[ -f "$DLL" ]] || die "recompiler not built ($DLL): run scripts/setup.sh first"
# resolve the image and output paths of the config (relative to the config's directory)
mapfile -t RES < <("$PYTHON" - "$CONFIG" <<'PY'
import json, os, sys
p = os.path.abspath(sys.argv[1]); d = os.path.dirname(p); c = json.load(open(p))
print(os.path.normpath(os.path.join(d, c["image"])))
print(os.path.normpath(os.path.join(d, c["game"]["output"])))
PY
)
(( ${#RES[@]} == 2 )) || die "cannot read game.output / image from $CONFIG"
IMAGE="${RES[0]}"; CFG_OUT="${RES[1]}"
[[ -f "$IMAGE" ]] || die "image $IMAGE missing: run scripts/import.sh first"
if [[ -n "$RECOMP_OUT_USER" ]]; then
  WANT_OUT="$("$PYTHON" -c "import os,sys; print(os.path.abspath(sys.argv[1]))" "$RECOMP_OUT_USER")"
else
  WANT_OUT="$CFG_OUT"
fi
if [[ "$WANT_OUT" != "$CFG_OUT" ]]; then
  step "write config override for output dir $WANT_OUT"
  OVERRIDE="$REPO/generated/ttt_raw.override.json"
  "$PYTHON" - "$CONFIG" "$IMAGE" "$WANT_OUT" "$OVERRIDE" <<'PY'
import json, sys
src, image, out, dst = sys.argv[1:5]
c = json.load(open(src)); c["image"] = image; c["game"]["output"] = out
json.dump(c, open(dst, "w"), indent=2)
PY
  CONFIG="$OVERRIDE"
fi
OUTDIR="$WANT_OUT"

step "run recompiler: dotnet recompone.dll $CONFIG"
mkdir -p "$OUTDIR"
LOG="$REPO/generated/regenerate.log"
if ! dotnet "$DLL" "$CONFIG" > "$LOG" 2>&1; then
  grep -v '^\[Unknown\]' "$LOG" | tail -40 >&2 || true
  die "recompiler exited non-zero; full log: $LOG"
fi
grep -v '^\[Unknown\]' "$LOG" | head -60 || true

step "check outputs"
[[ -s "$OUTDIR/main.cs" ]] || die "no $OUTDIR/main.cs produced; see $LOG"
grep -q "Recompilation finished" "$LOG" || die "recompiler did not report 'Recompilation finished'; see $LOG"
ok "$(ls "$OUTDIR" | wc -l) file(s) in $OUTDIR; main.cs $(wc -c < "$OUTDIR/main.cs") bytes"
echo "[Unknown] decode warnings in log: $(grep -c '^\[Unknown\]' "$LOG" || true)"
echo; echo "regenerate finished OK"
