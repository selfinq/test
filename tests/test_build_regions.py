import hashlib, os, sys, tempfile, unittest, zipfile, zlib
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import build_regions as b

def mem(name, data, region, load):
    return {"name": name, "region": region, "load": load, "size": len(data),
            "crc32": f"{zlib.crc32(data)&0xFFFFFFFF:08x}", "sha1": hashlib.sha1(data).hexdigest()}

def zipped(files):
    p = tempfile.NamedTemporaryFile(suffix=".zip", delete=False); p.close()
    with zipfile.ZipFile(p.name, "w") as z:
        for n, d in files.items(): z.writestr(n, d)
    return p.name

class T(unittest.TestCase):
    def run_set(self, rules, region_size, files):
        man = {"regions": {"r": region_size},
               "members": [mem(n, files[n], "r", l) for n, l in rules]}
        return b.build(zipped(files), man)

    def test_load16_byte_interleave(self):
        r, cov = self.run_set([("e", "LOAD16_BYTE @0x0"), ("o", "LOAD16_BYTE @0x1")], 8,
                              {"e": b"\x00\x02\x04\x06", "o": b"\x01\x03\x05\x07"})
        self.assertEqual(r["r"], bytes(range(8))); self.assertEqual(cov["r"], 8)

    def test_load32_byte_four_lanes(self):
        files = {f"f{i}": bytes([i, i + 10]) for i in range(4)}
        r, _ = self.run_set([(f"f{i}", f"LOAD32_BYTE @0x{i:x}") for i in range(4)], 8, files)
        self.assertEqual(r["r"], bytes([0, 1, 2, 3, 10, 11, 12, 13]))

    def test_load32_word_keeps_file_order_in_word(self):
        files = {"lo": b"\xAA\xBB\xCC\xDD", "hi": b"\x11\x22\x33\x44"}
        r, _ = self.run_set([("lo", "LOAD32_WORD @0x0"), ("hi", "LOAD32_WORD @0x2")], 8, files)
        self.assertEqual(r["r"], b"\xAA\xBB\x11\x22\xCC\xDD\x33\x44")

    def test_word_swap(self):
        r, _ = self.run_set([("s", "LOAD16_WORD_SWAP @0x0")], 4, {"s": b"\x01\x02\x03\x04"})
        self.assertEqual(r["r"], b"\x02\x01\x04\x03")

    def test_overlap_rejected(self):
        with self.assertRaises(ValueError):
            self.run_set([("a", "LOAD @0x0"), ("b", "LOAD @0x2")], 8, {"a": b"1234", "b": b"5678"})

    def test_overrun_rejected(self):
        with self.assertRaises(ValueError):
            self.run_set([("a", "LOAD @0x2")], 4, {"a": b"1234"})

    def test_incomplete_set_refused(self):
        man = {"regions": {"r": 4}, "members": [mem("a", b"abcd", "r", "LOAD @0x0"),
                                                  mem("b", b"efgh", "r", "LOAD @0x0")]}
        with self.assertRaises(ValueError):
            b.build(zipped({"a": b"abcd"}), man)

if __name__ == "__main__": unittest.main()
