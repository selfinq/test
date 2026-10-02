"""Optional integration test: needs MAME 0.264, the real tektagt ROMs and ~1-2 minutes. Skipped unless
DIFFTEST_MAME=1. Asserts that two short captures from the archived NVRAM seed are identical (determinism) and that
the seed is a fixed point (MAME leaves the NVRAM unchanged after the run)."""
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(HERE, "..", "..", "tools", "difftest")
sys.path.insert(0, TOOLS)
import snapshotlib as sl  # noqa: E402

ROM = os.path.join(HERE, "..", "..", "input", "roms", "tektagt.zip")


@unittest.skipUnless(os.environ.get("DIFFTEST_MAME") == "1" and os.path.exists("/usr/games/mame") and os.path.exists(ROM),
                     "set DIFFTEST_MAME=1 (needs /usr/games/mame and input/roms/tektagt.zip)")
class MameDeterminism(unittest.TestCase):
    def test_two_short_captures_identical(self):
        with tempfile.TemporaryDirectory() as d:
            hashes = []
            for i in (1, 2):
                out = os.path.join(d, "r%d.json" % i)
                r = subprocess.run([sys.executable, os.path.join(TOOLS, "capture_mame.py"), "--out", out, "--frames", "120",
                                    "--every", "60", "--pcs", "", "--workdir", os.path.join(d, "w%d" % i), "--timeout", "600"],
                                   capture_output=True, text=True)
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
                s = sl.load_snapshot(out)
                self.assertEqual(sl.validate(s), [])
                hashes.append(s["snapshot_sha256"])
                self.assertEqual(s["meta"]["nvram_after_run_sha256"], s["capture"]["nvram_seed_sha256"])
            self.assertEqual(hashes[0], hashes[1])


if __name__ == "__main__":
    unittest.main()
