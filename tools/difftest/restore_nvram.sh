#!/usr/bin/env bash
# restore_nvram.sh WORKDIR [SEEDDIR]
# Creates WORKDIR/nvram (copy of the archived NVRAM seed) and an EMPTY WORKDIR/cfg, so that every
# MAME run starts from exactly the same persistent state. Any previous WORKDIR/nvram, WORKDIR/cfg
# and WORKDIR/ini are deleted first. The seed itself is never written to (verified by sha256).
set -euo pipefail
work="${1:?usage: restore_nvram.sh WORKDIR [SEEDDIR]}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
seed="${2:-$here/nvram_seed}"
want="$(awk '{print $1}' "$seed/SEED.sha256")"
have="$(sha256sum "$seed/tektagt/at28c16" | awk '{print $1}')"
if [ "$want" != "$have" ]; then
  echo "restore_nvram: seed corrupted (want $want, have $have)" >&2; exit 2
fi
rm -rf "$work/nvram" "$work/cfg" "$work/ini"
mkdir -p "$work/nvram/tektagt" "$work/cfg" "$work/ini"
cp "$seed/tektagt/at28c16" "$work/nvram/tektagt/at28c16"
chmod u+rw "$work/nvram/tektagt/at28c16"
echo "restore_nvram: $work/nvram/tektagt/at28c16 sha256=$have"
