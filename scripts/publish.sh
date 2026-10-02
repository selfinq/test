#!/usr/bin/env bash
# publish: self-contained publish of the host for a runtime id (default win-x64; also linux-x64).
# usage: scripts/publish.sh [rid]      env: EXTRA_MSBUILD
# The result is NOT run by this script when the target is a different OS (a win-x64 build cannot be run on Linux).
RID="${1:-win-x64}"
case "$RID" in win-x64|linux-x64) ;; *) echo "unsupported rid '$RID' (win-x64 | linux-x64)" >&2; exit 2 ;; esac
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
read -r -a EXTRA <<< "${EXTRA_MSBUILD:-}"
step "check dotnet >= $DOTNET_MIN_MAJOR"
check_dotnet
step "dotnet publish -c Release -r $RID --self-contained"
dotnet publish "$HOST_PROJECT" -c Release -r "$RID" --self-contained --nologo "${EXTRA[@]}"
OUT="$REPO/host/TttHost/bin/Release/net10.0/$RID/publish"
step "check publish output $OUT"
EXE="TttRecompHost"; [[ "$RID" == win-* ]] && EXE="TttRecompHost.exe"
[[ -f "$OUT/$EXE" ]] || die "$OUT/$EXE missing"
[[ -f "$OUT/tektagt_manifest.json" ]] || die "$OUT/tektagt_manifest.json missing"
ok "$OUT : $(find "$OUT" -type f | wc -l) files, $(du -sh "$OUT" | cut -f1)"
if [[ "$RID" == win-* ]]; then
  echo "NOTE: this Windows build was produced on $(uname -s) and has NOT been executed on Windows."
fi
echo; echo "publish finished OK"
