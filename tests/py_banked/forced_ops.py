"""Forced (Lua-driven) differential test between MAME and the reference model.

gen: builds an op list (deterministic) -> writes reports/trace_a04/ft_ops.lua
run: MAME executes ops via ft.lua (address-space reads/writes, i.e. real bus semantics) -> ft_result.txt
cmp: the model executes the same ops -> compare line by line.
"""
import json
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bankrom_ref import BankedRomController, PsxDma5, M32  # noqa: E402

def rb(p):
    with open(p, "rb") as f:
        return f.read()


def wb(p, text):
    with open(p, "w") as f:
        f.write(text)


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "reports", "trace_a04")
PAT_BASE = 0xA5A5A5A5


def pat(i):
    return (PAT_BASE + i * 0x01010101) & M32


def gen_ops(banked_len=0x3800000, main_len=0x400000):
    rnd = random.Random(0x7E4A6)
    ops = []

    def W(w, a, v):
        ops.append(("w%d" % w, a, v))

    def R(w, a):
        ops.append(("r%d" % w, a))

    # ---- A. bank register + window --------------------------------------------------
    R(32, 0x1F000000); R(16, 0x1F000000); R(32, 0x1F000004)
    entries = banked_len // 0x200000
    for e in (0, 1, 5, 13, entries - 1, 0):
        W(16, 0x1F000000, e)
        R(32, 0x1FA00000); R(32, 0x1FBFFFFC); R(16, 0x1FA00002); R(8, 0x1FA00001); R(8, 0x1FBFFFFF)
        R(32, 0x1FB00004); R(32, 0x1FB80004); R(16, 0x1FB00006)
        for _ in range(24):
            a = 0x1FA00000 + rnd.randrange(0, 0x200000, 4)
            R(32, a)
            R(16, a + 2)
            R(8, a + 1)
            R(8, a + 3)
    W(32, 0x1F000000, 0x00050003)            # upper half is not mapped for writes; entry <- 3
    R(32, 0x1FA00000); R(32, 0x1FA10000)
    W(8, 0x1F000000, 4)                      # byte write, lane 0: data = 4
    R(32, 0x1FA00000); R(32, 0x1FA10000)
    W(8, 0x1F000001, 0)                      # byte write, lane 1: data = 0 << 8 = 0 -> entry 0
    R(32, 0x1FA00000)
    W(16, 0x1F000000, 7)
    W(32, 0x1FA00000, 0xFFFFFFFF); W(16, 0x1FA00000, 0xFFFF); W(8, 0x1FA00000, 0xFF)   # window is read-only
    R(32, 0x1FA00000)
    W(16, 0x1F000000, 0)

    # ---- B. protection state machine ------------------------------------------------
    known = [(0x806D2C24, 0xD5545715), (0x804C2C84, 0xD5545615), (0x2AABA8E6, 0x00820040),
             (0x2AABA592, 0x01780544), (0x12345678, 0x00A9ABCD), (0x00000000, 0x00000000),
             (0x806D2C24, 0xD5545714), (0x1, 0x00A80000), (0xFFFFFFFF, 0xFFFFFFFF)]
    for v0, v1 in known:
        W(32, 0x1FB80000, 0)
        R(32, 0x1FB80000)                    # cnt==0 -> 0
        W(32, 0x1FB00000, v0)
        R(32, 0x1FB80000)                    # cnt==1 -> 0
        W(32, 0x1FB00000, v1)
        for a in (0x1FB80000, 0x1FB80002):
            R(32, a & ~3); R(16, a); R(8, a); R(8, a + 1)
        R(32, 0x1FB00000); R(16, 0x1FB00000); R(16, 0x1FB00002); R(8, 0x1FB00001)
        W(32, 0x1FB00000, 0x11111111)        # cnt==2: tektagdmaoffset only, values frozen
        R(32, 0x1FB80000)
        W(16, 0x1FB80000, rnd.randrange(0, 65536))   # reset trigger (random value allowed)
        R(32, 0x1FB80000)
    # half-word write sequences
    W(16, 0x1FB80000, 0)
    W(16, 0x1FB00000, 0x2C24); W(16, 0x1FB00002, 0x806D)
    W(16, 0x1FB00000, 0x5715); W(16, 0x1FB00002, 0xD554)
    R(32, 0x1FB80000)
    W(16, 0x1FB80002, 0)                     # offset 1 write: no effect
    R(32, 0x1FB80000)
    W(16, 0x1FB80000, 0)
    W(16, 0x1FB00002, 0x1234)                # high half only first: ORs into stale ttt_val[0], cnt -> 1
    W(16, 0x1FB00000, 0x0001); W(16, 0x1FB00002, 0x00A9)
    R(32, 0x1FB80000); R(16, 0x1FB80002)
    W(8, 0x1FB80000, 0x55)                   # byte write to the reset register
    R(32, 0x1FB80000)
    # 0x1f700000 read (sets flag, returns 0), mirror, write region
    R(32, 0x1F700000); R(16, 0x1F700002); R(8, 0x1F700001); R(32, 0x1FF00000); R(32, 0x1F700004); R(32, 0x1FF00004)

    # ---- C. DMA ----------------------------------------------------------------------
    def dma(madr, bcr, chcr, dpcr=0x33B33333, nread=64, dicr=None):
        ops.append(("dma", madr, bcr, chcr, dpcr, nread, dicr))

    def tekt(off):
        W(32, 0x1FB80000, 0); W(32, 0x1FB00000, off); R(16, 0x1F700000)

    def dmaoff(addr_low, hi):
        W(16, 0x1F700000 + addr_low, hi)

    W(32, 0x1F801000, 0x1F000000)
    W(32, 0x1F8010F4, 0)
    # tektagt path, banked
    for off in (0x0, 0x1964944, 0x22AE328, 0x1000, 0x1001, 0x3, 0x2000002, 0x37FFFC0, 0x37FFFF0, 0x3800000,
                0x3800004, 0x5000000, 0x7FFFFFFF):
        tekt(off)
        dma(0x80100000, 80, 0x01000000, nread=96)
    # tektagt offset with bit 31 set -> main ROM
    for off in (0x80000000, 0x8021DE54, 0x80000001, 0x803FFFF0, 0x80400000, 0x80FFFFF0, 0xFFFFFFF0, 0x81000000):
        tekt(off)
        dma(0x80100000, 64, 0x01000000, nread=80)
    # flag consumed by the first DMA: second DMA falls back to m_dmaoffset
    dmaoff(0x0, 0x0000); tekt(0x1964944)
    dma(0x80110000, 16, 0x01000000, nread=24)
    dma(0x80110100, 16, 0x01000000, nread=24)        # uses dmaoffset (0)
    # dmaoffset_w path (no flag)
    for lo, hi in ((0x0, 0x0000), (0x10, 0x0010), (0x7FFE, 0x0123), (0x0, 0x8000), (0x1234, 0x8021), (0xFFFE, 0x7FFF),
                   (0xFFFE, 0xFFFF), (0x2, 0x0001)):
        dmaoff(lo, hi)
        dma(0x80120000, 48, 0x01000000, nread=64)
    W(32, 0x1F700008, 0x00020001)                  # 32-bit write: two handler calls, last (offset 5) wins
    dma(0x80120000, 32, 0x01000000, nread=40)
    # exp_base == 0x1f300000 forces the main ROM
    dmaoff(0x40, 0x0000)
    W(32, 0x1F801000, 0x1F300000)
    dma(0x80130000, 32, 0x01000000, nread=40)
    tekt(0x1964944)
    dma(0x80130100, 32, 0x01000000, nread=40)
    W(32, 0x1F801000, 0x1F000000)
    dma(0x80130200, 32, 0x01000000, nread=40)
    # RAM end truncation, large sizes, address masking
    tekt(0x1964944); dma(0x803FFFE0, 100, 0x01000000, nread=64)
    tekt(0x1964944); dma(0x803FFFFC, 100, 0x01000000, nread=8)
    tekt(0x1964944); dma(0xA0200000, 50, 0x01000000, nread=64)   # kseg1-style address masks to 0x200000
    tekt(0x37FFF00); dma(0x80140000, 0x10000, 0x01000000, nread=1024)
    tekt(0x1964944); dma(0x80140000, 0x7FFFFFF0, 0x01000000, nread=64)   # huge: RAM-limited
    # negative size, zero size
    tekt(0x1964944); dma(0x80150000, 0x80000010, 0x01000000, nread=32)
    tekt(0x1964944); dma(0x80150000, 0, 0x01000000, nread=32)
    # sync-mode variants for channel 5
    tekt(0x1964944); dma(0x80160000, (3 << 16) | 8, 0x01000200, nread=40)
    tekt(0x1964944); dma(0x80160000, (0 << 16) | 4, 0x01000200, nread=24)        # ba==0 -> 0x10000 blocks: huge
    tekt(0x1964944); dma(0x80160000, (2 << 16) | 8, 0x11000000, nread=40)
    tekt(0x1964944); dma(0x80160000, (1 << 16) | 8, 0x11400100, nread=40)
    tekt(0x1964944); dma(0x80160000, (0 << 16) | 8, 0x11000000, nread=24)
    # not enabled / unknown mode: nothing happens
    tekt(0x1964944); dma(0x80170000, 16, 0x01000000, dpcr=0x33333333, nread=24)    # DPCR bit 23 clear
    tekt(0x1964944); dma(0x80170000, 16, 0x01000201, nread=24)
    tekt(0x1964944); dma(0x80170000, 16, 0x00000000, nread=24)
    dma(0x80170000, 16, 0x01000000, nread=24)                                       # flag not set (consumed by wrongly-moded)
    # which reads of the 0x1f700000 window set m_has_tektagt_dma (decide between tektagdmaoffset and dmaoffset)
    for rw, ra in ((32, 0x1F700000), (16, 0x1F700002), (8, 0x1F700001), (8, 0x1F700003), (32, 0x1FF00000),
                   (16, 0x1FF00002), (32, 0x1F700004), (16, 0x1F700006), (32, 0x1F70FFFC), (32, 0x1FF00004)):
        dmaoff(0x0, 0x0020)                                   # dmaoffset = 0x00200000
        W(32, 0x1FB00000, 0x1964944)
        R(rw, ra)
        dma(0x80190000, 8, 0x01000000, nread=12)
        dma(0x80190100, 8, 0x01000000, nread=12)              # flag (if any) was consumed
    # DICR interplay
    W(32, 0x1F8010F4, 0x00A00000)
    tekt(0x1964944); dma(0x80180000, 8, 0x01000000, nread=12)
    W(32, 0x1F8010F4, 0x3F000000)                    # acknowledge
    W(32, 0x1F8010F4, 0x00800000)
    tekt(0x1964944); dma(0x80180000, 8, 0x01000000, nread=12)
    return ops


def lua_text(ops):
    out = ["return {"]
    for op in ops:
        parts = []
        for x in op:
            if x is None:
                parts.append("false")
            elif isinstance(x, str):
                parts.append('"%s"' % x)
            else:
                parts.append("%d" % x)
        out.append("  {" + ",".join(parts) + "},")
    out.append("}")
    return "\n".join(out) + "\n"


def run_model(ops, banked, main):
    ctrl = BankedRomController(banked, main)
    ram = bytearray(ctrl.ram_size)
    dma = PsxDma5(ctrl, ram)
    lines = []
    for i, op in enumerate(ops, 1):
        k = op[0]
        if k.startswith("w"):
            w = int(k[1:]); a, v = op[1], op[2]
            if a == 0x1F801000:
                ctrl.exp_base = v
            elif a == 0x1F8010F4:
                dma.write_dicr(v)
            elif w == 32:
                ctrl.write32(a, v)
            elif w == 16:
                ctrl.write16(a, v)
            else:
                ctrl.write8(a, v)
            lines.append("%d ok" % i)
        elif k.startswith("r"):
            w = int(k[1:]); a = op[1]
            v = {32: ctrl.read32, 16: ctrl.read16, 8: ctrl.read8}[w](a)
            lines.append("%d %08x" % (i, v))
        elif k == "dma":
            _, madr, bcr, chcr, dpcr, nread, dicr = op
            dest = (madr & (ctrl.ram_size - 1)) & ~3
            nread = min(nread, (ctrl.ram_size - dest) // 4)
            for j in range(nread):
                ram[dest + j * 4:dest + j * 4 + 4] = pat(j).to_bytes(4, "little")
            dma.write_dpcr(dpcr); dma.write_madr(madr); dma.write_bcr(bcr)
            dma.write_chcr(chcr)
            words = [int.from_bytes(ram[dest + j * 4:dest + j * 4 + 4], "little") for j in range(nread)]
            lines.append("%d chcr=%08x dicr=%08x data=%s" % (i, dma.chcr, dma.dicr, ",".join("%x" % x for x in words)))
    return lines


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "gen":
        ops = gen_ops()
        wb(os.path.join(OUT, "ft_ops.lua"), lua_text(ops))
        print(len(ops), "ops")
    elif cmd == "cmp":
        ops = gen_ops()
        reg = os.path.join(ROOT, "generated", "regions")
        banked = rb(os.path.join(reg, "bankedroms.bin"))
        main = rb(os.path.join(reg, "maincpu_rom.bin"))
        exp = run_model(ops, banked, main)
        got = rb(os.path.join(OUT, sys.argv[2] if len(sys.argv) > 2 else "ft_result.txt")).decode().split("\n")
        got = [g for g in got if g]
        bad = 0
        n = min(len(exp), len(got))
        print("ops", len(ops), "expected lines", len(exp), "mame lines", len(got))
        for e, g in zip(exp, got):
            if e != g:
                bad += 1
                if bad <= 25:
                    i = int(e.split()[0]) - 1
                    print("MISMATCH op", ops[i][:6])
                    print("   model:", e[:200])
                    print("   mame :", g[:200])
        print("mismatching lines:", bad, "of", n)
