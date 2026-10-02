#!/usr/bin/env bash
# setup: check toolchain, clone RecompOne at the pinned commit, apply patches/*.patch in order, build the recompiler.
# env: RECOMPONE_DIR (default <repo>/../work/recompone), RECOMPONE_URL, PYTHON
# flags: --skip-build
SKIP_BUILD=0
for a in "$@"; do case "$a" in --skip-build) SKIP_BUILD=1 ;; -h|--help) sed -n 2,5p "$0"; exit 0 ;; *) echo "unknown argument: $a" >&2; exit 2 ;; esac; done
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

step "check toolchain (git, python, dotnet >= $DOTNET_MIN_MAJOR)"
need_cmd git "Install git."
find_python; echo "python: $("$PYTHON" --version 2>&1)"
check_dotnet

step "obtain RecompOne at $RECOMPONE_COMMIT in $RECOMPONE_DIR"
if [[ ! -e "$RECOMPONE_DIR" ]]; then
  mkdir -p "$(dirname "$RECOMPONE_DIR")"
  git clone "$RECOMPONE_URL" "$RECOMPONE_DIR"
fi
git -C "$RECOMPONE_DIR" rev-parse --git-dir >/dev/null 2>&1 || die "$RECOMPONE_DIR exists but is not a git checkout"
HEAD_NOW="$(git -C "$RECOMPONE_DIR" rev-parse HEAD)"
if [[ "$HEAD_NOW" != "$RECOMPONE_COMMIT" ]]; then
  echo "HEAD is $HEAD_NOW, pin is $RECOMPONE_COMMIT"
  if [[ -n "$(git -C "$RECOMPONE_DIR" status --porcelain --untracked-files=no)" ]]; then
    die "checkout is at the wrong commit AND has local modifications; refusing to switch. Use a clean checkout or set RECOMPONE_DIR."
  fi
  git -C "$RECOMPONE_DIR" cat-file -e "${RECOMPONE_COMMIT}^{commit}" 2>/dev/null || git -C "$RECOMPONE_DIR" fetch origin "$RECOMPONE_COMMIT"
  git -C "$RECOMPONE_DIR" checkout --detach "$RECOMPONE_COMMIT"
fi
ok "RecompOne HEAD = $(git -C "$RECOMPONE_DIR" rev-parse HEAD)"

step "apply patches/*.patch in order"
shopt -s nullglob
PATCHES=("$REPO"/patches/*.patch)
(( ${#PATCHES[@]} > 0 )) || die "no patches found in $REPO/patches"
for p in "${PATCHES[@]}"; do
  if git -C "$RECOMPONE_DIR" apply --check "$p" 2>/dev/null; then
    git -C "$RECOMPONE_DIR" apply "$p"
    ok "applied $(basename "$p")"
  elif git -C "$RECOMPONE_DIR" apply --reverse --check "$p" 2>/dev/null; then
    ok "already applied: $(basename "$p")"
  else
    CURRENT_STEP="apply $(basename "$p")"
    die "$(basename "$p") neither applies nor is already applied to $RECOMPONE_DIR (is the checkout modified by something else?)"
  fi
done

if (( SKIP_BUILD )); then
  step "build skipped (--skip-build)"
else
  step "build RecompOne.Recompiler (Release)"
  dotnet build "$RECOMPONE_DIR/RecompOne.Recompiler/RecompOne.Recompiler.csproj" -c Release --nologo
  [[ -f "$(recompiler_dll)" ]] || die "build reported success but $(recompiler_dll) does not exist"
  ok "recompiler: $(recompiler_dll)"
fi
echo; echo "setup finished OK"
