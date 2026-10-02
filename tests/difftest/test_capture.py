import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import synth as S  # noqa: E402
import capture_mame as CM  # noqa: E402
import snapshotlib as sl  # noqa: E402

TOOLS = os.path.join(HERE, "..", "..", "tools", "difftest")
RESTORE = os.path.join(TOOLS, "restore_nvram.sh")


def rb(p):
    with open(p, "rb") as f:
        return f.read()


def wb(p, data, mode="wb"):
    with open(p, mode) as f:
        f.write(data)


class Schema(unittest.TestCase):
    def schema(self):
        with open(os.path.join(TOOLS, "snapshot.schema.json")) as f:
            return json.load(f)

    def test_synthetic_snapshot_valid_structurally_and_by_schema(self):
        snap = S.build()
        self.assertEqual(sl.validate(snap), [])
        try:
            import jsonschema
        except ImportError:
            self.skipTest("jsonschema not installed (structural validation above still ran)")
        jsonschema.validate(snap, self.schema())

    def test_invalid_snapshots_rejected(self):
        bad = S.build()
        bad["checkpoints"][0]["cpu"]["gpr"] = bad["checkpoints"][0]["cpu"]["gpr"][:31]
        self.assertTrue(any("32 hex" in e for e in sl.validate(bad)))
        bad = S.build()
        bad["checkpoints"][1]["id"] = bad["checkpoints"][0]["id"]
        self.assertTrue(any("duplicate" in e for e in sl.validate(bad)))
        bad = S.build()
        bad["checkpoints"][0]["sync"] = {"type": "pc_hit", "pc": "80001000", "nth": 1}
        self.assertTrue(sl.validate(bad))
        bad = S.build()
        bad["checkpoints"][0]["ram"]["regions"]["nosuch"] = {"sha256": "0" * 64}
        self.assertTrue(any("unknown region" in e for e in sl.validate(bad)))
        try:
            import jsonschema
        except ImportError:
            return
        for mutate in (lambda s: s["checkpoints"][0]["cpu"]["gpr"].pop(),
                       lambda s: s["checkpoints"][0].update(sync={"type": "frame"}),
                       lambda s: s.update(schema="x")):
            b = S.build()
            mutate(b)
            with self.assertRaises(jsonschema.ValidationError):
                jsonschema.validate(b, self.schema())


class Regions(unittest.TestCase):
    def test_real_region_table_tiles_ram(self):
        cfg = sl.load_regions_config(os.path.join(TOOLS, "regions_tektagt.json"))
        self.assertEqual(sum(r["size"] for r in cfg["regions"]), cfg["ram_size"])
        self.assertEqual([r["name"] for r in cfg["regions"]][-1], "stack")

    def test_gap_and_overlap_rejected(self):
        regs = [{"name": "a", "base": "0x80000000", "size": 0x1000}, {"name": "b", "base": "0x80002000", "size": 0x1000}]
        with self.assertRaises(ValueError):
            sl.check_regions(regs, 0x80000000, 0x3000)
        regs = [{"name": "a", "base": "0x80000000", "size": 0x2000}, {"name": "b", "base": "0x80001000", "size": 0x1000}]
        with self.assertRaises(ValueError):
            sl.check_regions(regs, 0x80000000, 0x3000)


class HashRam(unittest.TestCase):
    def test_region_page_and_stack_live_hashes(self):
        ram = S.make_ram(7)
        h = sl.hash_ram(bytes(ram), S.REGIONS_CFG, 0x8000ff00)
        code = bytes(ram[0:0x4000])
        self.assertEqual(h["regions"]["code"]["sha256"], hashlib.sha256(code).hexdigest())
        self.assertEqual(len(h["regions"]["code"]["pages"]), 0x4000 // S.PAGE)
        self.assertEqual(h["regions"]["code"]["pages"][1], hashlib.sha256(code[S.PAGE:2 * S.PAGE]).hexdigest()[:16])
        self.assertEqual(h["stack_live"], {"base": "0x8000ff00", "size": 0x100,
                                           "sha256": hashlib.sha256(bytes(ram[0xff00:0x10000])).hexdigest()})
        # sp outside the stack -> empty live stack, not a crash
        self.assertEqual(sl.hash_ram(bytes(ram), S.REGIONS_CFG, 0x1000)["stack_live"]["size"], 0)
        # unaligned sp is rounded down to a word
        self.assertEqual(sl.hash_ram(bytes(ram), S.REGIONS_CFG, 0x8000ff03)["stack_live"]["base"], "0x8000ff00")


class Events(unittest.TestCase):
    def test_chain_is_order_and_content_sensitive_and_reads_separate(self):
        a, b, c = sl.EventChains(), sl.EventChains(), sl.EventChains()
        for e in (S.W(1, 2), S.W(3, 4), S.R(5, 6)):
            a.add(e)
        for e in (S.W(3, 4), S.W(1, 2), S.R(5, 6)):
            b.add(e)
        for e in (S.W(1, 2), S.W(3, 4), S.R(5, 7)):
            c.add(e)
        self.assertNotEqual(a.w, b.w)
        self.assertEqual(a.w, c.w)          # a read difference leaves the write chain untouched
        self.assertNotEqual(a.r, c.r)
        self.assertEqual((a.wn, a.rn), (2, 1))

    def test_sidecar_pc_and_extra_fields_are_not_compared(self):
        e1 = {"k": "W", "a": "0x1f801810", "d": "0x00000001", "pc": "0x80000000", "f": 3}
        e2 = {"k": "W", "a": "0x1f801810", "d": "0x00000001", "w": 4}
        self.assertEqual(sl.event_compared_text(e1), sl.event_compared_text(e2))
        e3 = dict(e2, d="0x1ffffffff")   # value is truncated to 32 bits like the producers do
        self.assertEqual(sl.event_compared_text(e3), "W,1f801810,ffffffff")


class DebugLog(unittest.TestCase):
    LOG = ("MAME debugger version 0.264 (unknown)\nCurrently targeting tektagt (Tekken Tag Tournament)\n"
           "W 1f801810 e1001000\n\n"
           "R 1f801074 00000009\n"
           "Stopped at watchpoint 1 writing 01000401 to 1F8010A8 (PC=80189EC8)\n"
           "W 1f8010a8 01000401\n"
           "D 2 01000401 80123456 00000000 12 00000000deadbeef\n"
           "C 1\n"
           "garbage line\n"
           "W 1f801814 03000000\n"
           "C 2\n")

    def test_parse(self):
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as f:
            f.write(self.LOG)
        try:
            ev, marks, tr = CM.parse_debug_log(f.name)
        finally:
            os.unlink(f.name)
        self.assertEqual([e["k"] for e in ev], ["W", "R", "W", "D", "W"])
        self.assertEqual(marks, {1: 4, 2: 5})
        self.assertEqual(ev[3], {"k": "D", "ch": 2, "chcr": "0x01000401", "madr": "0x80123456", "bcr": "0x00000000",
                                 "n": 12, "h": "0x00000000deadbeef"})
        self.assertFalse(tr)

    def test_event_max_truncates_and_flags(self):
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as f:
            f.write(self.LOG)
        try:
            ev, marks, tr = CM.parse_debug_log(f.name, event_max=3)
        finally:
            os.unlink(f.name)
        self.assertEqual(len(ev), 3)
        self.assertTrue(tr)


class Finalize(unittest.TestCase):
    def make_raw(self, d, log):
        manifest = {"mame_version": "0.264", "debugger": True, "events_enabled": True, "frames": 2}
        wb(os.path.join(d, "manifest.json"), json.dumps(manifest), "w")
        ram1, ram2 = S.make_ram(3), S.make_ram(4)
        cps = []
        for seq, (cid, ram, sync) in enumerate([("frame_00001", ram1, {"type": "frame", "frame": 1}),
                                                ("pc_80001000_1", ram2, {"type": "pc_hit", "pc": "0x80001000", "nth": 1})], 1):
            name = "ram_%04d.raw" % seq
            wb(os.path.join(d, name), bytes(ram))
            r = S.regs()
            cps.append({"seq": seq, "id": cid, "sync": sync, "frame": seq, "pc": r["pc"], "gpr": r["gpr"], "hi": r["hi"],
                        "lo": r["lo"], "cop0": r["cop0"], "cop2_d": r["cop2_d"], "cop2_c": r["cop2_c"], "ram_file": name})
        wb(os.path.join(d, "checkpoints.jsonl"), "".join(json.dumps(c) + "\n" for c in cps), "w")
        wb(os.path.join(d, "debug.log"), log, "w")

    def test_finalize_builds_valid_snapshot_with_chains_at_markers(self):
        with tempfile.TemporaryDirectory() as d:
            self.make_raw(d, DebugLog.LOG)
            out = os.path.join(d, "snap.json")
            snap = CM.finalize(d, out, S.REGIONS_CFG, {"frames": 2}, meta_extra={"x": 1})
            self.assertEqual(sl.validate(snap), [])
            self.assertTrue(os.path.exists(os.path.join(d, "snap.events.jsonl")))
            self.assertEqual(snap["snapshot_sha256"], sl.snapshot_hash(snap))
            c1, c2 = snap["checkpoints"]
            self.assertEqual((c1["events"]["w_count"], c1["events"]["r_count"]), (3, 1))     # W R W D before marker C 1
            self.assertEqual((c2["events"]["w_count"], c2["events"]["r_count"]), (4, 1))
            self.assertEqual(snap["events"]["w_count"], 4)
            # raw RAM dumps are removed (game data must not linger), the snapshot keeps hashes only
            self.assertFalse(os.path.exists(os.path.join(d, "ram_0001.raw")))
            # recomputing the chain from the sidecar reproduces the stored value
            ch = sl.EventChains()
            for e in sl.load_events(os.path.join(d, "snap.events.jsonl")):
                ch.add(e)
            self.assertEqual(ch.state()["w_chain"], snap["events"]["w_chain"])

    def test_finalize_is_deterministic(self):
        outs = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as d:
                self.make_raw(d, DebugLog.LOG)
                outs.append(CM.finalize(d, os.path.join(d, "s.json"), S.REGIONS_CFG, {"frames": 2})["snapshot_sha256"])
        self.assertEqual(outs[0], outs[1])

    def test_missing_marker_is_an_error(self):
        with tempfile.TemporaryDirectory() as d:
            self.make_raw(d, DebugLog.LOG.replace("C 2\n", ""))
            with self.assertRaises(ValueError):
                CM.finalize(d, os.path.join(d, "s.json"), S.REGIONS_CFG, {})


class Vectors(unittest.TestCase):
    """Cross-implementation vectors (tests/difftest/vectors.json) for the C# side, plus independent sanity checks."""

    def load(self):
        with open(os.path.join(HERE, "vectors.json")) as f:
            return json.load(f)

    @staticmethod
    def fnv_bytes(b):       # textbook FNV-1a 64, independent of snapshotlib
        h = 0xcbf29ce484222325
        for x in b:
            h = ((h ^ x) * 0x100000001b3) % (1 << 64)
        return h

    def test_fnv_constants_against_published_test_vectors(self):
        self.assertEqual(self.fnv_bytes(b""), 0xcbf29ce484222325)
        self.assertEqual(self.fnv_bytes(b"a"), 0xaf63dc4c8601ec8c)
        self.assertEqual(self.fnv_bytes(b"foobar"), 0x85944171f73967e8)
        words = [0x12345678, 0xdeadbeef]
        self.assertEqual(sl.fnv1a64_words(words), self.fnv_bytes(b"".join(w.to_bytes(4, "little") for w in words)))

    def test_vectors_file_is_current(self):
        out = subprocess.run([sys.executable, os.path.join(TOOLS, "make_vectors.py")], capture_output=True, text=True, check=True).stdout
        self.assertEqual(json.loads(out), self.load())

    def test_vectors_reproduce(self):
        v = self.load()
        ch = sl.EventChains()
        for e, want, text in zip(v["events"]["events"], v["events"]["state_after_each_event"], v["events"]["canonical_text"]):
            ch.add(e)
            self.assertEqual(sl.event_compared_text(e), text)
            self.assertEqual(ch.state(), want)
        ram = {int(k, 16): int(w, 16) for k, w in v["dma"]["ram_words"].items()}
        for c in v["dma"]["cases"]:
            n, h = sl.dma_summary(lambda a: ram.get(a & 0x3ffffc, 0), int(c["chcr"], 16), int(c["madr"], 16), int(c["bcr"], 16))
            self.assertEqual((n, "0x%016x" % h), (c["n"], c["h"]), c["name"])
        r = v["ram"]
        self.assertEqual(sl.hash_ram(bytes.fromhex(r["ram_hex"]), r["config"], int(r["sp"], 16)), r["expected"])
        self.assertEqual(sl.snapshot_hash(v["snapshot_hash"]["snapshot"]), v["snapshot_hash"]["sha256"])
        self.assertEqual(sl.sha256_hex(v["snapshot_hash"]["canonical_json"].encode()), v["snapshot_hash"]["sha256"])

    def test_linked_list_walk_stops_on_terminator_and_loop_guard(self):
        ram = {0x100: (0 << 24) | 0x100}            # node pointing to itself, never terminates
        n, h = sl.dma_summary(lambda a: ram.get(a, 0), 0x01000401, 0x100, 0)
        self.assertEqual(n, 0x40000)                 # guard: 0x40000 nodes of 1 word
        ram = {0x100: (2 << 24) | 0x800000, 0x104: 1, 0x108: 2}
        self.assertEqual(sl.dma_summary(lambda a: ram.get(a, 0), 0x01000401, 0x100, 0)[0], 3)


class FixedTimeShim(unittest.TestCase):
    def test_shim_freezes_time_call(self):
        import shutil
        if not shutil.which("gcc"):
            self.skipTest("gcc not available")
        with tempfile.TemporaryDirectory() as d:
            subprocess.run(["gcc", "-shared", "-fPIC", "-O2", "-o", os.path.join(d, "ft.so"), os.path.join(TOOLS, "fixedtime.c")], check=True)
            wb(os.path.join(d, "t.c"), "#include <stdio.h>\n#include <time.h>\nint main(void){time_t a=time(NULL),b;time(&b);printf(\"%ld %ld\\n\",(long)a,(long)b);return 0;}\n", "w")
            subprocess.run(["gcc", "-o", os.path.join(d, "t"), os.path.join(d, "t.c")], check=True)
            plain = subprocess.run([os.path.join(d, "t")], capture_output=True, text=True).stdout.split()
            self.assertGreater(int(plain[0]), 1700000000)
            env = dict(os.environ, LD_PRELOAD=os.path.join(d, "ft.so"), DT_FIXED_TIME="123456789")
            out = subprocess.run([os.path.join(d, "t")], capture_output=True, text=True, env=env).stdout.split()
            self.assertEqual(out, ["123456789", "123456789"])
            env.pop("DT_FIXED_TIME")
            out = subprocess.run([os.path.join(d, "t")], capture_output=True, text=True, env=env).stdout.split()
            self.assertEqual(out, ["1767225600", "1767225600"])


class RestoreNvram(unittest.TestCase):
    def mkseed(self, d, content=b"seed-bytes"):
        os.makedirs(os.path.join(d, "seed", "tektagt"))
        p = os.path.join(d, "seed", "tektagt", "at28c16")
        wb(p, content)
        wb(os.path.join(d, "seed", "SEED.sha256"), hashlib.sha256(content).hexdigest() + "  tektagt/at28c16\n", "w")
        return os.path.join(d, "seed")

    def test_fresh_copy_each_time_and_seed_untouched(self):
        with tempfile.TemporaryDirectory() as d:
            seed = self.mkseed(d)
            work = os.path.join(d, "work")
            r = subprocess.run([RESTORE, work, seed], capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            nv = os.path.join(work, "nvram", "tektagt", "at28c16")
            self.assertEqual(rb(nv), b"seed-bytes")
            wb(nv, b"MAME modified it")           # what a run does on exit
            wb(os.path.join(work, "cfg", "stale.cfg"), "x", "w")
            subprocess.run([RESTORE, work, seed], check=True, capture_output=True)
            self.assertEqual(rb(nv), b"seed-bytes")
            self.assertEqual(os.listdir(os.path.join(work, "cfg")), [])
            self.assertEqual(rb(os.path.join(seed, "tektagt", "at28c16")), b"seed-bytes")

    def test_corrupt_seed_refused(self):
        with tempfile.TemporaryDirectory() as d:
            seed = self.mkseed(d)
            wb(os.path.join(seed, "tektagt", "at28c16"), b"tampered")
            r = subprocess.run([RESTORE, os.path.join(d, "work"), seed], capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("corrupted", r.stderr)
            self.assertFalse(os.path.exists(os.path.join(d, "work", "nvram")))

    def test_archived_seed_matches_its_checksum(self):
        seed = os.path.join(TOOLS, "nvram_seed")
        want = rb(os.path.join(seed, "SEED.sha256")).split()[0].decode()
        have = hashlib.sha256(rb(os.path.join(seed, "tektagt", "at28c16"))).hexdigest()
        self.assertEqual(want, have)
        self.assertEqual(os.path.getsize(os.path.join(seed, "tektagt", "at28c16")), 2080)


if __name__ == "__main__":
    unittest.main()
