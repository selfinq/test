# Shared helpers for scripts/*.sh. Source it; do not execute it.
# Every script: fails on the first error, and prints the exact step that failed.
set -Eeuo pipefail

SCRIPT_NAME="$(basename "${0:-script}")"
_COMMON_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$_COMMON_DIR/.." && pwd)"

# shellcheck disable=SC1091
source "$_COMMON_DIR/pins.cfg"

# Overridable locations (environment variables).
ROM_ZIP="${ROM_ZIP:-$REPO/input/roms/tektagt.zip}"
RECOMPONE_DIR="${RECOMPONE_DIR:-$REPO/../work/recompone}"
RECOMP_OUT_USER="${RECOMP_OUT:-}"   # empty when the user did not set it
RECOMP_OUT="${RECOMP_OUT:-$REPO/generated/recomp}"
HOST_PROJECT="$REPO/host/TttHost/TttHost.csproj"
RUNTIME_PROJECT="$REPO/src/System12.Runtime/System12.Runtime.csproj"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

CURRENT_STEP="(before first step)"
STEP_NO=0

step() {
  STEP_NO=$((STEP_NO + 1))
  CURRENT_STEP="$*"
  echo
  echo "==> [$SCRIPT_NAME] step $STEP_NO: $*"
}

die() {
  echo >&2
  echo "FAILED [$SCRIPT_NAME] step $STEP_NO: $CURRENT_STEP" >&2
  echo "  reason: $*" >&2
  exit 1
}

_on_err() {
  local rc=$? line=$1 cmd=$2
  echo >&2
  echo "FAILED [$SCRIPT_NAME] step $STEP_NO: $CURRENT_STEP" >&2
  echo "  command: $cmd" >&2
  echo "  exit code: $rc (script line $line)" >&2
  exit "$rc"
}
trap '_on_err $LINENO "$BASH_COMMAND"' ERR

ok() { echo "OK [$SCRIPT_NAME] $*"; }

need_cmd() { command -v "$1" >/dev/null 2>&1 || die "'$1' not found on PATH. $2"; }

find_python() {
  if [[ -n "${PYTHON:-}" ]]; then return 0; fi
  for c in python3 python; do
    if command -v "$c" >/dev/null 2>&1; then PYTHON="$c"; break; fi
  done
  [[ -n "${PYTHON:-}" ]] || die "python 3 not found (tried python3, python). Install Python >= 3.$PYTHON_MIN_MINOR."
  "$PYTHON" -c "import sys; sys.exit(0 if sys.version_info >= (3, $PYTHON_MIN_MINOR) else 1)" \
    || die "$PYTHON is too old: $("$PYTHON" --version 2>&1); need >= 3.$PYTHON_MIN_MINOR."
}

check_dotnet() {
  need_cmd dotnet "Install the .NET $DOTNET_MIN_MAJOR SDK (https://dotnet.microsoft.com/download)."
  local v major
  v="$(dotnet --version)" || die "'dotnet --version' failed"
  major="${v%%.*}"
  [[ "$major" =~ ^[0-9]+$ ]] || die "cannot parse dotnet version '$v'"
  (( major >= DOTNET_MIN_MAJOR )) || die "dotnet SDK $v found, need >= $DOTNET_MIN_MAJOR.x"
  echo "dotnet SDK $v"
}

sha256_of() { "$PYTHON" -c "import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())" "$1"; }

recompiler_dll() { echo "$RECOMPONE_DIR/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll"; }
