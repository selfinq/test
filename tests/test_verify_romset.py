import hashlib, io, json, os, sys, tempfile, unittest, zipfile, zlib
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import verify_romset as v

def mk(name, data):
    return {"name": name, "size": len(data), "crc32": f"{zlib.crc32(data)&0xFFFFFFFF:08x}",
            "sha1": hashlib.sha1(data).hexdigest()}

A, B = b"synthetic-A" * 10, b"synthetic-B" * 10
MAN = {"members": [mk("a.bin", A), mk("b.bin", B)]}

def zipped(files):
    p = tempfile.NamedTemporaryFile(suffix=".zip", delete=False); p.close()
    with zipfile.ZipFile(p.name, "w") as z:
        for n, d in files.items(): z.writestr(n, d)
    return p.name

class T(unittest.TestCase):
    def test_complete(self):
        self.assertTrue(v.verify(zipped({"a.bin": A, "b.bin": B}), MAN)["complete"])
    def test_missing(self):
        r = v.verify(zipped({"a.bin": A}), MAN)
        self.assertFalse(r["complete"]); self.assertEqual(r["missing"], ["b.bin"])
    def test_bad_content(self):
        r = v.verify(zipped({"a.bin": A, "b.bin": B[:-1] + b"x"}), MAN)
        self.assertFalse(r["complete"]); self.assertEqual(len(r["bad_content"]), 1)
    def test_wrong_name_detected_by_content(self):
        r = v.verify(zipped({"a.bin": A, "renamed.bin": B}), MAN)
        self.assertFalse(r["complete"]); self.assertEqual(r["wrong_name"][0]["is"], "b.bin")
    def test_unknown_member(self):
        r = v.verify(zipped({"a.bin": A, "b.bin": B, "x.bin": b"zz"}), MAN)
        self.assertEqual(len(r["unknown"]), 1)
    def test_unsafe_path_rejected(self):
        with self.assertRaises(ValueError): v.verify(zipped({"../evil": b"x"}), MAN)
    def test_oversize_member_not_hashed_as_known(self):
        r = v.verify(zipped({"a.bin": A, "big.bin": b"0" * 100000}), MAN)
        self.assertEqual(r["unknown"][0]["name"], "big.bin")

if __name__ == "__main__": unittest.main()
