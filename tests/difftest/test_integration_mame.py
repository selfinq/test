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


@unittest.skipUnless(os.environ.get("DIFFTEST_MAME") == "1" and os.path.exists("/usr/games/mame") and os.path.exists(ROM),
                     "set DIFFTEST_MAME=1 (needs /usr/games/mame and input/roms/tektagt.zip)")
class LuaDmaHash(unittest.TestCase):
    def test_lua_dma_hash_equals_python_definition(self):
        with open(os.path.join(HERE, "vectors.json")) as f:
            v = json.load(f)
        with tempfile.TemporaryDirectory() as d:
            inp, out = os.path.join(d, "in.lua"), os.path.join(d, "out.txt")
            words = ",".join("[%d]=%d" % (int(a, 16), int(w, 16)) for a, w in v["dma"]["ram_words"].items())
            cases = ",".join("{chcr=%d,madr=%d,bcr=%d}" % (int(c["chcr"], 16), int(c["madr"], 16), int(c["bcr"], 16))
                             for c in v["dma"]["cases"])
            with open(inp, "w") as f:
                f.write("return {words={%s},cases={%s}}\n" % (words, cases))
            for sub in ("nv", "cfg", "ini"):
                os.makedirs(os.path.join(d, sub))
            env = dict(os.environ, SDL_VIDEODRIVER="dummy", SDL_AUDIODRIVER="dummy", DT_SELFTEST_INPUT=inp, DT_SELFTEST_OUT=out,
                       DT_CAPTURE_LUA=os.path.join(TOOLS, "mame_capture.lua"))
            r = subprocess.run(["/usr/games/mame", "tektagt", "-rompath", os.path.join(HERE, "..", "..", "input", "roms"),
                                "-video", "none", "-sound", "none", "-nothrottle", "-seconds_to_run", "30",
                                "-autoboot_script", os.path.join(TOOLS, "dma_hash_selftest.lua"), "-nvram_directory", os.path.join(d, "nv"),
                                "-cfg_directory", os.path.join(d, "cfg"), "-inipath", os.path.join(d, "ini")],
                               env=env, capture_output=True, text=True, timeout=300)
            self.assertTrue(os.path.exists(out), r.stderr[-2000:])
            with open(out) as f:
                got = [ln.split() for ln in f]
            for (i, n, h), c in zip(got, v["dma"]["cases"]):
                self.assertEqual((int(n), "0x" + h), (c["n"], c["h"]), c["name"])
            self.assertEqual(len(got), len(v["dma"]["cases"]))


if __name__ == "__main__":
    unittest.main()
