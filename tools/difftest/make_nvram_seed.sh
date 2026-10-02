#!/usr/bin/env bash
# make_nvram_seed.sh OUTDIR  -- regenerate a seed from an empty NVRAM (first boot, 1500 frames).
# Only for maintainers; the committed seed is what the captures use.
set -euo pipefail
out="${1:?usage: make_nvram_seed.sh OUTDIR}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
rompath="${ROMPATH:-$here/../../input/roms}"
rm -rf "$out"; mkdir -p "$out/nv" "$out/cfg" "$out/ini"
cat > "$out/exit.lua" <<'LUA'
local n = 0
emu.register_frame_done(function() n = n + 1; if n == 1500 then manager.machine:exit() end end)
LUA
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy /usr/games/mame tektagt -rompath "$rompath" -video none -sound none \
  -nothrottle -seconds_to_run 100 -autoboot_script "$out/exit.lua" -nvram_directory "$out/nv" \
  -cfg_directory "$out/cfg" -inipath "$out/ini" >/dev/null 2>&1 || true
sha256sum "$out/nv/tektagt/at28c16"
