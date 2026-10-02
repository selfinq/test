#!/usr/bin/env bash
# build: build src/System12.Runtime (if present) and host/TttHost (Release).
# flags: --with-recomp        also compile generated/recomp/*.cs into the host (206 MB; Roslyn ran out of memory once, see docs/BUILD.md)
#        --allow-no-runtime   if System12.Runtime fails to build, build the host without it (the status line says so)
# env:   EXTRA_MSBUILD="-p:S12ExcludeFolders=Gpu;Sound"   (extra msbuild args for every dotnet call)
WITH_RECOMP=0; ALLOW_NO_RT=0
for a in "$@"; do case "$a" in
  --with-recomp) WITH_RECOMP=1 ;; --allow-no-runtime) ALLOW_NO_RT=1 ;;
  -h|--help) sed -n 2,6p "$0"; exit 0 ;; *) echo "unknown argument: $a" >&2; exit 2 ;; esac; done
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
read -r -a EXTRA <<< "${EXTRA_MSBUILD:-}"
HOST_EXTRA=()

step "check dotnet >= $DOTNET_MIN_MAJOR"
check_dotnet

if [[ -f "$RUNTIME_PROJECT" ]]; then
  step "build src/System12.Runtime (Release)"
  if ! dotnet build "$RUNTIME_PROJECT" -c Release --nologo "${EXTRA[@]}"; then
    if (( ALLOW_NO_RT )); then
      echo "WARNING: System12.Runtime failed to build; continuing WITHOUT it (--allow-no-runtime)" >&2
      HOST_EXTRA+=("-p:UseSystem12Runtime=false")
    else
      die "System12.Runtime does not build. Fix it, exclude broken folders (EXTRA_MSBUILD='-p:S12ExcludeFolders=Gpu;Sound'), or pass --allow-no-runtime."
    fi
  fi
else
  step "System12.Runtime project not present ($RUNTIME_PROJECT): host is built without it"
fi

if (( WITH_RECOMP )); then
  [[ -d "$RECOMP_OUT" ]] || die "--with-recomp but $RECOMP_OUT does not exist: run scripts/regenerate.sh first"
  HOST_EXTRA+=("-p:RecompDir=$RECOMP_OUT")
  echo "including generated code from $RECOMP_OUT"
fi

step "build host/TttHost (Release)"
dotnet build "$HOST_PROJECT" -c Release --nologo "${EXTRA[@]}" "${HOST_EXTRA[@]}"
HOST_DLL="$REPO/host/TttHost/bin/Release/net10.0/TttRecompHost.dll"
[[ -f "$HOST_DLL" ]] || die "build reported success but $HOST_DLL does not exist"
ok "host: $HOST_DLL"
echo; echo "build finished OK"
