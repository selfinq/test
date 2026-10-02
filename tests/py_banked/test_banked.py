"""Unit tests for the banked-ROM / DMA / protection model (Python twin of BankedRomController).

Synthetic-data tests always run. Tests marked [real] use generated/regions/*.bin and the stored MAME traces
under reports/trace_a04/ and are skipped when those are absent.
Run:  python3 -m unittest discover -s tests/py_banked -v
"""
import os
import struct
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from bankrom_ref import (BankedRomController, PsxDma5, BankOutOfRange, M32)  # noqa: E402
import compare_trace  # noqa: E402
import forced_ops  # noqa: E402

def rb(p):
    with open(p, "rb") as f:
        return f.read()


ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
REG = os.path.join(ROOT, "generated", "regions")
TR = os.path.join(ROOT, "reports", "trace_a04")


def synth(n):
    """Deterministic non-repeating test bytes."""
    out = bytearray(n)
    x = 0x12345678
    for i in range(0, n, 4):
        x = (x * 1664525 + 1013904223) & M32
        out[i:i + 4] = struct.pack("<I", x)
    return bytes(out)


BANKED = synth(3 * 0x200000)        # 3 banks
MAIN = synth(0x400000)[::-1]


def mk(**kw):
    return BankedRomController(BANKED, MAIN, **kw)


def word(rom, byte_off):
    return struct.unpack_from("<I", rom, byte_off)[0]


class BankWindow(unittest.TestCase):
    def test_default_entry_zero_and_widths(self):
        c = mk()
        self.assertEqual(c.read32(0x1FA00000), word(BANKED, 0))
        self.assertEqual(c.read16(0x1FA00002), struct.unpack_from("<H", BANKED, 2)[0])
        self.assertEqual(c.read8(0x1FA00003), BANKED[3])
        self.assertEqual(c.read32(0x1FBFFFFC), word(BANKED, 0x1FFFFC))

    def test_bank_select_16bit(self):
        c = mk()
        for e in (2, 1, 0, 2):
            c.write16(0x1F000000, e)
            self.assertEqual(c.read32(0x1FA01234 & ~3), word(BANKED, e * 0x200000 + 0x1234))

    def test_bank_write_widths(self):
        c = mk()
        c.write32(0x1F000000, 0x00050002)          # only the low halfword is mapped
        self.assertEqual(c.bank_entry, 2)
        c.write8(0x1F000000, 1)
        self.assertEqual(c.bank_entry, 1)
        c.write8(0x1F000001, 0)                    # lane 1: data = 0 << 8
        self.assertEqual(c.bank_entry, 0)
        with self.assertRaises(BankOutOfRange):
            c.write8(0x1F000001, 1)                # 0x100: invalid entry

    def test_out_of_range(self):
        c = mk()
        with self.assertRaises(BankOutOfRange):
            c.write16(0x1F000000, 3)
        self.assertEqual(c.bank_entry, 0)

    def test_bank_register_reads_zero_and_window_read_only(self):
        c = mk()
        self.assertEqual(c.read32(0x1F000000), 0)
        c.write32(0x1FA00000, 0)
        c.write16(0x1FA00000, 0)
        c.write8(0x1FA00000, 0)
        self.assertEqual(c.read32(0x1FA00000), word(BANKED, 0))

    def test_bank_register_does_not_touch_dma_state(self):
        c = mk()
        c.write16(0x1F000000, 2)
        self.assertEqual(c.bankoffset, 0)
        self.assertEqual(c.dmaoffset, 0)

    def test_protection_window_overlays_bank(self):
        c = mk()
        self.assertEqual(c.read32(0x1FB00000), 0x8000)
        self.assertEqual(c.read32(0x1FB00004), word(BANKED, 0x100004))
        self.assertEqual(c.read32(0x1FB80004), word(BANKED, 0x180004))
        self.assertEqual(c.read32(0x1FB80000), 0)


class Protection(unittest.TestCase):
    def feed(self, c, v0, v1):
        c.write32(0x1FB80000, 0)
        c.write32(0x1FB00000, v0)
        c.write32(0x1FB00000, v1)

    def test_known_values(self):
        c = mk()
        self.feed(c, 0x806D2C24, 0xD5545715)
        self.assertEqual(c.read32(0x1FB80000), 0x000036E2)
        self.feed(c, 0x804C2C84, 0xD5545615)
        self.assertEqual(c.read32(0x1FB80000), 0x00002651)
        self.feed(c, 0x2AABA8E6, 0x00820040)
        self.assertEqual(c.read32(0x1FB80000), 0x41860000)
        self.assertEqual(c.read16(0x1FB80000), 0)
        self.assertEqual(c.read16(0x1FB80002), 0x4186)
        self.feed(c, 0x2AABA592, 0x01780544)
        self.assertEqual(c.read32(0x1FB80000), 0x3C7D0000)

    def test_a9_branch_answers_both_halves(self):
        c = mk()
        self.feed(c, 0x11111111, 0x00A90000)
        self.assertEqual(c.read32(0x1FB80000), 0x552E552E)
        self.feed(c, 0x11111111, 0x00A80000)
        self.assertEqual(c.read32(0x1FB80000), 0)

    def test_known_pair_other_half_is_zero(self):
        c = mk()
        self.feed(c, 0x806D2C24, 0xD5545715)
        self.assertEqual(c.read16(0x1FB80002), 0)      # branch taken, offset!=0 -> 0 (no fall into the a9 test)

    def test_not_ready_before_two_values(self):
        c = mk()
        c.write32(0x1FB80000, 0)
        self.assertEqual(c.read32(0x1FB80000), 0)
        c.write32(0x1FB00000, 0x806D2C24)
        self.assertEqual(c.ttt_cnt, 1)
        self.assertEqual(c.read32(0x1FB80000), 0)
        c.write32(0x1FB00000, 0xD5545715)
        self.assertEqual(c.ttt_cnt, 2)
        self.assertEqual(c.read32(0x1FB80000), 0x36E2)

    def test_values_freeze_after_two_but_dmaoffset_follows(self):
        c = mk()
        self.feed(c, 0x806D2C24, 0xD5545715)
        c.write32(0x1FB00000, 0x01234567)
        self.assertEqual(c.tektagdmaoffset, 0x01234567)
        self.assertEqual(c.ttt_val, [0x806D2C24, 0xD5545715])
        self.assertEqual(c.read32(0x1FB80000), 0x36E2)
        c.write16(0x1FB80000, 0xBEEF)                 # any value: reset trigger
        self.assertEqual(c.ttt_cnt, 0)
        self.assertEqual(c.read32(0x1FB80000), 0)

    def test_reset_register_offset1_and_bytes(self):
        c = mk()
        self.feed(c, 0x806D2C24, 0xD5545715)
        c.write16(0x1FB80002, 0)
        self.assertEqual(c.ttt_cnt, 2)
        c.write8(0x1FB80000, 0x55)
        self.assertEqual(c.ttt_cnt, 0)

    def test_halfword_writes(self):
        c = mk()
        c.write16(0x1FB80000, 0)
        c.write16(0x1FB00000, 0x2C24); c.write16(0x1FB00002, 0x806D)
        c.write16(0x1FB00000, 0x5715); c.write16(0x1FB00002, 0xD554)
        self.assertEqual(c.ttt_cnt, 2)
        self.assertEqual(c.ttt_val, [0x806D2C24, 0xD5545715 & 0xFFFF | 0xD554 << 16])
        self.assertEqual(c.read32(0x1FB80000), 0x36E2)

    def test_quirk_high_half_first_ors_into_stale_value(self):
        c = mk()
        c.write16(0x1FB80000, 0)
        c.write16(0x1FB00002, 0x1234)                  # offset 1 first: ttt_val[0] |= 0x12340000, cnt -> 1
        self.assertEqual(c.ttt_cnt, 1)
        self.assertEqual(c.ttt_val[0], 0x12340000)

    def test_prot1_read_values(self):
        c = mk()
        self.assertEqual(c.read16(0x1FB00000), 0x8000)
        self.assertEqual(c.read16(0x1FB00002), 0)
        self.assertEqual(c.read32(0x1FB00000), 0x00008000)
        self.assertEqual(c.read8(0x1FB00001), 0x80)

    def test_prot3_read_sets_flag(self):
        for a in (0x1F700000,):
            for rd in (c_read32, c_read16, c_read8):
                c = mk()
                self.assertFalse(c.has_tektagt_dma)
                self.assertEqual(rd(c, a), 0)
                self.assertTrue(c.has_tektagt_dma, hex(a))
        c = mk()
        c.read32(0x1F700004)                           # not mapped for reads
        c.read32(0x1FF00000)                           # mirror(0x800000) is shadowed by the boot ROM in MAME
        self.assertFalse(c.has_tektagt_dma)

    def test_reset_clears_flag_only(self):
        c = mk()
        self.feed(c, 1, 2)
        c.read32(0x1F700000)
        c.reset()
        self.assertFalse(c.has_tektagt_dma)
        self.assertEqual(c.ttt_cnt, 2)


def c_read32(c, a): return c.read32(a)
def c_read16(c, a): return c.read16(a)
def c_read8(c, a): return c.read8(a)


class DmaSource(unittest.TestCase):
    def tekt(self, c, off):
        c.write32(0x1FB80000, 0)
        c.write32(0x1FB00000, off)
        c.read16(0x1F700000)

    def test_tektagt_path_banked(self):
        c = mk()
        self.tekt(c, 0x1964944)
        r = c.dma_rom_read(0x802FE500 & 0x3FFFFF, 0x6A0)
        self.assertFalse(r.is_mainrom)
        self.assertEqual(r.data, BANKED[0x1964944:0x1964944 + 0x6A0 * 4])
        self.assertEqual(r.address, 0x2FE500)
        self.assertFalse(c.has_tektagt_dma)

    def test_dmaoffset_w_encodes_address_and_data(self):
        c = mk()
        c.write16(0x1F70DE50, 0x8421)
        self.assertEqual(c.dmaoffset, 0x8421DE50)
        c.write16(0x1F700000, 0)
        self.assertEqual(c.dmaoffset, 0)
        c.write32(0x1F700008, 0x00020001)                # lo at offset 4 then hi at offset 5: last wins
        self.assertEqual(c.dmaoffset, (5 * 2) | (2 << 16))

    def test_dmaoffset_path_main_rom_bit31(self):
        c = mk()
        c.write16(0x1F70DE54, 0x8421)
        r = c.dma_rom_read(0x301A00, 82)
        self.assertTrue(r.is_mainrom)
        self.assertEqual(r.source_byte_offset, 0x21DE54)
        self.assertEqual(r.data, MAIN[0x21DE54:0x21DE54 + 82 * 4])

    def test_dmaoffset_path_banked(self):
        c = mk()
        c.write16(0x1F702468, 0x0123)                    # dmaoffset = 0x01232468
        r = c.dma_rom_read(0x100000, 10)
        self.assertFalse(r.is_mainrom)
        self.assertEqual(r.data, BANKED[0x1232468:0x1232468 + 40])

    def test_flag_consumed_by_first_dma(self):
        c = mk()
        c.write16(0x1F700000, 0x0010)                    # dmaoffset 0x00100000
        self.tekt(c, 0x1964944)
        a = c.dma_rom_read(0x1000, 4)
        b = c.dma_rom_read(0x2000, 4)
        self.assertEqual(a.data, BANKED[0x1964944:0x1964944 + 16])
        self.assertEqual(b.data, BANKED[0x100000:0x100000 + 16])

    def test_exp_base_selects_main_rom(self):
        c = mk(exp_base=0x1F300000)
        c.write16(0x1F700040, 0x0000)
        r = c.dma_rom_read(0x1000, 8)
        self.assertTrue(r.is_mainrom)
        self.assertEqual(r.data, MAIN[0x40:0x40 + 32])
        # exp_base wins over the tektagt path too: tektagdmaoffset has bit31 clear but main ROM is still used
        self.tekt(c, 0x1964944)
        r = c.dma_rom_read(0x1000, 8)
        self.assertTrue(r.is_mainrom)
        self.assertEqual(r.source_byte_offset, 0x964944)         # & 0xffffff

    def test_main_rom_offset_masked_24bit(self):
        c = mk()
        self.tekt(c, 0x80000010 | 0x0F000000)           # bits 24-27 dropped
        r = c.dma_rom_read(0, 4)
        self.assertEqual(r.source_byte_offset, 0x10)

    def test_odd_offset_floors_to_halfword(self):
        c = mk()
        self.tekt(c, 0x1001)
        r = c.dma_rom_read(0, 4)
        self.assertEqual(r.source_byte_offset, 0x1000)
        self.assertEqual(r.data, BANKED[0x1000:0x1010])

    def test_truncation_rom_end(self):
        c = mk()
        n = len(BANKED)
        self.tekt(c, n - 16)
        r = c.dma_rom_read(0, 100)
        self.assertTrue(r.truncated_rom)
        self.assertEqual(r.words, 4)
        self.assertEqual(r.data, BANKED[-16:])
        self.tekt(c, n + 8)                              # beyond the region
        r = c.dma_rom_read(0, 100)
        self.assertEqual(r.words, 0)
        self.assertEqual(r.data, b"")

    def test_truncation_ram_end(self):
        c = mk()
        self.tekt(c, 0x1000)
        r = c.dma_rom_read(0x3FFFE0, 100)
        self.assertTrue(r.truncated_ram)
        self.assertEqual(r.words, 8)

    def test_negative_size(self):
        c = mk()
        self.tekt(c, 0x1000)
        r = c.dma_rom_read(0, -5)
        self.assertEqual(r.words, 0)

    def test_alt_bank_adds_bankoffset(self):
        c = mk(alt_bank=True)
        c.bankoffset = 2
        c.write16(0x1F700000, 0)
        r = c.dma_rom_read(0, 4)
        self.assertEqual(r.source_byte_offset, 2 * 0x200000)
        c2 = mk(alt_bank=True)
        c2.bankoffset = 2
        self.tekt(c2, 0)
        self.assertEqual(c2.dma_rom_read(0, 4).source_byte_offset, 0)   # tektagt path never adds it

    def test_main_rom_required(self):
        c = BankedRomController(BANKED)
        c.write16(0x1F700000, 0x8000)
        with self.assertRaises(ValueError):
            c.dma_rom_read(0, 4)


class Dma5(unittest.TestCase):
    def setUp(self):
        self.c = mk()
        self.ram = bytearray(0x400000)
        self.d = PsxDma5(self.c, self.ram)
        self.c.write32(0x1FB00000, 0x1000)
        self.c.read16(0x1F700000)

    def go(self, madr, bcr, chcr, dpcr=0x33B33333):
        self.d.write_dpcr(dpcr)
        self.d.write_madr(madr)
        self.d.write_bcr(bcr)
        self.d.write_chcr(chcr)

    def test_basic_and_masking(self):
        self.go(0xA0200000, 16, 0x01000000)
        self.assertEqual(bytes(self.ram[0x200000:0x200040]), BANKED[0x1000:0x1040])
        self.assertEqual(self.d.chcr, 0x01000000 & ~(1 << 24))
        self.assertEqual(self.d.dicr, 0)       # flag bit 29 survives only if channel-5 enable (bit 21) is set

    def test_requires_dpcr_bit23(self):
        self.go(0x1000, 16, 0x01000000, dpcr=0x33333333)
        self.assertIsNone(self.d.last)
        self.assertTrue(self.c.has_tektagt_dma)           # untouched

    def test_modes(self):
        self.go(0x2000, (3 << 16) | 8, 0x01000200)         # 24 words
        self.assertEqual(self.d.last.words, 24)
        self.c.read16(0x1F700000)
        self.go(0x2000, (2 << 16) | 8, 0x11000000)         # CD formula: max(ba,1)*bs = 16
        self.assertEqual(self.d.last.words, 16)
        self.c.read16(0x1F700000)
        self.go(0x2000, (0 << 16) | 8, 0x11000000)         # ba 0 -> 1
        self.assertEqual(self.d.last.words, 8)
        self.go(0x2000, 8, 0x01000201)                     # no write handler on ch5: ignored
        self.assertIsNone(self.d.last)

    def test_block_size_unsigned_product_wraps_negative(self):
        # 0xffff blocks of 0x10000 words wraps to a negative int32 in MAME: nothing is copied
        self.go(0x2000, 0xFFFF0000 | 0xFFFF, 0x01000200)
        self.assertEqual(self.d.last.words, 0 if ((0xFFFF * 0xFFFF) & M32) > 0x7FFFFFFF else self.d.last.words)

    def test_dicr_irq(self):
        self.d.write_dicr(0x00A00000)                      # master enable + channel 5 enable
        self.go(0x2000, 4, 0x01000000)
        self.assertTrue(self.d.irq_asserted)
        self.assertTrue(self.d.dicr & 0x80000000)
        self.d.write_dicr(0x3F000000 | 0x00A00000)         # acknowledge flags
        self.assertFalse(self.d.dicr & 0x7F000000)
        self.assertFalse(self.d.dicr & 0x80000000)


@unittest.skipUnless(os.path.exists(os.path.join(REG, "bankedroms.bin")), "real regions not present")
class RealRegions(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.banked = rb(os.path.join(REG, "bankedroms.bin"))
        cls.main = rb(os.path.join(REG, "maincpu_rom.bin"))

    def test_geometry(self):
        c = BankedRomController(self.banked, self.main)
        self.assertEqual(len(self.banked), 0x3800000)
        self.assertEqual(c.bank_entries, 28)
        self.assertEqual(c.read16(0x1FA00000), 0x0015)     # value MAME returned on the bus (trace seq 21)

    def test_every_bank_window_matches_region(self):
        c = BankedRomController(self.banked, self.main)
        for e in range(28):
            c.write16(0x1F000000, e)
            for off in (0, 4, 0x100004, 0x1FFFFC):
                self.assertEqual(c.read32(0x1FA00000 + off), word(self.banked, e * 0x200000 + off))

    def test_dma_real_first_transfer_sizes(self):
        c = BankedRomController(self.banked, self.main)
        c.write32(0x1FB00000, 0x01964948)
        c.read16(0x1F700000)
        r = c.dma_rom_read(0x2FE500, 0x6A0)
        self.assertEqual(r.data, self.banked[0x01964948:0x01964948 + 0x6A0 * 4])

    @unittest.skipUnless(os.path.exists(os.path.join(TR, "ft_result.txt")), "MAME forced-test result not present")
    def test_forced_ops_match_recorded_mame_output(self):
        """The model reproduces, line for line, what MAME 0.264 returned for ~1050 forced bus accesses/DMAs."""
        ops = forced_ops.gen_ops()
        exp = forced_ops.run_model(ops, self.banked, self.main)
        got = [g for g in rb(os.path.join(TR, "ft_result.txt")).decode().split("\n") if g]
        self.assertEqual(len(exp), len(got))
        bad = [(e[:80], g[:80]) for e, g in zip(exp, got) if e != g]
        self.assertEqual(bad, [])

    @unittest.skipUnless(os.path.exists(os.path.join(TR, "run1500", "ev_1500.txt")), "MAME event trace not present")
    def test_game_trace_1500_frames(self):
        st, probs = compare_trace.replay(os.path.join(TR, "run1500", "ev_1500.txt"), self.banked, self.main,
                                         os.path.join(TR, "run1500"))
        self.assertEqual(st["read_mismatch"], 0, probs)
        self.assertEqual(st["model_dma_missing"], 0)
        self.assertEqual(st["dump_bad"], 0, probs)
        self.assertGreater(st["dma_started"], 7000)
        self.assertGreater(st["dma_block_ok"], 10000)
        # the only hash differences are DMAs whose destination the game relocates in place right after the transfer
        self.assertEqual(st["dma_block_bad"], st["dump_fixup_explained"])


if __name__ == "__main__":
    unittest.main()
