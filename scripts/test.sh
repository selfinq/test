#!/usr/bin/env bash
# test: python unittest (tests/) + every dotnet test project found (host/TttHost.Tests, tests/**, src/**/*Tests*.csproj).
# env: EXTRA_MSBUILD (extra msbuild args), PYTHON
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
cd "$REPO"
read -r -a EXTRA <<< "${EXTRA_MSBUILD:-}"

step "python unittest (python -m unittest discover -s tests -v)"
find_python
"$PYTHON" -m unittest discover -s tests -v

step "check dotnet >= $DOTNET_MIN_MAJOR"
check_dotnet

step "locate dotnet test projects"
mapfile -t TEST_PROJECTS < <( { find host tests src -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null \
  | grep -i -E 'test' || true; } | sort -u)
(( ${#TEST_PROJECTS[@]} > 0 )) || die "no dotnet test project found (expected host/TttHost.Tests)"
printf '  %s\n' "${TEST_PROJECTS[@]}"

EMPTY=(); RAN=0
LOGTMP="$(mktemp)"; trap 'rm -f "$LOGTMP"' EXIT
for proj in "${TEST_PROJECTS[@]}"; do
  step "dotnet test $proj"
  dotnet test "$proj" --nologo "${EXTRA[@]}" 2>&1 | tee "$LOGTMP"
  if grep -q "No test is available" "$LOGTMP"; then
    echo "WARNING: no tests were discovered in $proj" >&2
    EMPTY+=("$proj")
  else
    RAN=$((RAN + 1))
  fi
done
echo
echo "python unittest: passed; dotnet test projects that ran tests: $RAN"
if (( ${#EMPTY[@]} > 0 )); then
  echo "WARNING: dotnet test projects with NO tests discovered (nothing was verified there):" >&2
  printf '  %s\n' "${EMPTY[@]}" >&2
fi
echo "test run finished OK"
