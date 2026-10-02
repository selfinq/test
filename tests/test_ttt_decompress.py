import hashlib, json, os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
from ttt_decompress import decompress

ROOT = os.path.join(os.path.dirname(__file__), "..")


class Synthetic(unittest.TestCase):
    def test_literals_then_end(self):
        # flags 0b1000_0111 -> sentinel + 3 literal bits, then terminator 0
        data, pos = decompress(bytes([0b00001111, 0x41, 0x42, 0x43, 0x00]))
        self.assertEqual(data, b"ABC"); self.assertEqual(pos, 5)

    def test_backref_with_overlap(self):
        # literal 'A', then token offset=1,len=4 (big-endian tok = (4<<11)|1) -> 'AAAAA'
        tok = (4 << 11) | 1
        data, _ = decompress(bytes([0b00000101, 0x41, tok >> 8, tok & 0xFF, 0x00]))
        self.assertEqual(data, b"AAAAA")

    def test_zero_offset_and_length_mean_max(self):
        lit = bytes(range(256)) * 8  # 2048 literal bytes via many control bytes
        stream = bytearray()
        for i in range(0, len(lit), 7):
            chunk = lit[i:i + 7]
            stream.append((1 << len(chunk)) | ((1 << len(chunk)) - 1))
            stream += chunk
        stream += bytes([0b00000010, 0x00, 0x00, 0x00])  # one match, tok=0 -> off 0x800, len 0x20
        data, _ = decompress(bytes(stream))
        self.assertEqual(len(data), len(lit) + 0x20)
        self.assertEqual(data[len(lit):], lit[:0x20])

    def test_backref_before_start_rejected(self):
        with self.assertRaises(ValueError):
            decompress(bytes([0b00000010, 0x08, 0x05, 0x00]))


@unittest.skipUnless(os.path.exists(os.path.join(ROOT, "generated/regions/maincpu_rom.bin")),
                     "needs generated/regions from the verified ROM set")
class RealRom(unittest.TestCase):
    def test_image_matches_mame_ram(self):
        exp = json.load(open(os.path.join(ROOT, "tests/expected_image.json")))
        rom = open(os.path.join(ROOT, "generated/regions/maincpu_rom.bin"), "rb").read()
        data, used = decompress(rom, int(exp["source_rom_offset"], 16))
        self.assertEqual(len(data), exp["image_size"])
        self.assertEqual(used - int(exp["source_rom_offset"], 16), int(exp["source_bytes_consumed"], 16))
        self.assertEqual(hashlib.sha256(data).hexdigest(), exp["mame_ram_sha256"])


if __name__ == "__main__":
    unittest.main()
