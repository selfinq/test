#!/usr/bin/env bash
# Runs the unit tests of the differential-test harness (no MAME needed). With DIFFTEST_MAME=1 also the MAME integration tests.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
exec python3 -W error::ResourceWarning -m unittest discover -s tests/difftest -v
