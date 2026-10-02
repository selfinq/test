#!/usr/bin/env bash
# run: run the host (TttRecompHost). TODAY this prints the status and verifies the ROM zip; it does NOT run the game.
# usage: scripts/run.sh [host args...]   e.g. scripts/run.sh --rom input/roms/tektagt.zip
# host exit codes: 0 ROM set verified | 1 ROM incomplete/mismatched | 2 usage/no ROM/unsafe archive
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
HOST_DLL="$REPO/host/TttHost/bin/Release/net10.0/TttRecompHost.dll"
step "check dotnet"
check_dotnet
if [[ ! -f "$HOST_DLL" ]]; then
  step "host not built: building it first"
  "$_COMMON_DIR/build.sh"
fi
step "run TttRecompHost $*"
RC=0
dotnet "$HOST_DLL" "$@" || RC=$?
echo
echo "host exit code: $RC (0 = ROM set verified, 1 = ROM incomplete/mismatched, 2 = usage / no ROM / unsafe archive)"
exit "$RC"
