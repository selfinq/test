"""Replay a MAME event trace (reports/trace_a04/ev.lua output) through the Python reference model
and compare every predictable value against what MAME actually returned / wrote to RAM.

Usage: python3 compare_trace.py EVLOG [--dumpdir DIR] [--max-mismatch N]
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bankrom_ref import BankedRomController, PsxDma5, M32  # noqa: E402

def rb(p):
    with open(p, "rb") as f:
        return f.read()


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
REG = os.path.join(ROOT, "generated", "regions")

EV_RE = re.compile(r"^(\d+) f(\d+) ([RW]) ([0-9a-f]{8}) ([0-9a-f]{8}) ([0-9a-f]{8}) pc=([0-9a-f]{8})")

LANES = {0xFFFFFFFF: (4, 0), 0x0000FFFF: (2, 0), 0xFFFF0000: (2, 2),
         0x000000FF: (1, 0), 0x0000FF00: (1, 1), 0x00FF0000: (1, 2), 0xFF000000: (1, 3)}


def blockhash(words):
    h = 0
    for w in words:
        h = (h * 31 + w) & M32
    return h


def fixup_delta(got, want):
    """If got == want except that every 2nd word has one constant added, return that constant."""
    if len(got) != len(want):
        return None
    ks = set()
    for i in range(0, len(got), 4):
        a = int.from_bytes(got[i:i + 4], "little"); b = int.from_bytes(want[i:i + 4], "little")
        d = (a - b) & M32
        if d:
            ks.add(d)
            if (i // 4) % 2:
                return None
    return ks.pop() if len(ks) == 1 else None


def replay(path, banked, main=None, dumpdir=None, max_report=20):
    ctrl = BankedRomController(banked, main)
    ram = bytearray(ctrl.ram_size)
    dma = PsxDma5(ctrl, ram)
    st = {"events": 0, "reads_checked": 0, "read_mismatch": 0, "dma_started": 0, "dma_block_ok": 0,
          "dma_block_bad": 0, "dma_unchecked_blocks": 0, "dump_ok": 0, "dump_bad": 0, "model_dma_missing": 0,
          "bank_writes": 0, "other_mismatch": 0, "dma_truncated": 0, "sizes": {}, "chcr_values": {}, "prot_reads": {},
          "unmodelled_lane": 0, "dma_mainrom": 0, "dma_banked_tektagt": 0, "dma_banked_dmaoffset": 0,
          "dump_fixup_explained": 0, "src_min": None, "src_max": None, "dma_words_total": 0}
    problems = []
    last_dma = None        # (seq, DmaResult) from the model
    dma_by_seq = {}
    seen_seq = {}

    def report(msg):
        if len(problems) < max_report:
            problems.append(msg)

    with open(path) as f:
        for line in f:
            m = EV_RE.match(line)
            if m:
                seq, frame, kind, addr, data, mask, pc = m.groups()
                seq = int(seq); addr = int(addr, 16); data = int(data, 16); mask = int(mask, 16)
                st["events"] += 1
                if mask not in LANES:
                    st["unmodelled_lane"] += 1
                    report("seq %d: unmodelled mask %08x" % (seq, mask))
                    continue
                size, lane = LANES[mask]
                a = addr + lane
                if kind == "W":
                    v = (data & mask) >> (8 * lane)
                    if 0x1F8010D0 <= addr < 0x1F8010DC or addr == 0x1F8010F0:
                        if addr == 0x1F8010D0:
                            dma.write_madr(data)
                        elif addr == 0x1F8010D4:
                            dma.write_bcr(data)
                        elif addr == 0x1F8010F0:
                            dma.write_dpcr(data)
                        elif addr == 0x1F8010D8:
                            dma.last = None
                            via_flag = ctrl.has_tektagt_dma
                            dma.write_chcr(data)
                            st["chcr_values"]["%08x" % data] = st["chcr_values"].get("%08x" % data, 0) + 1
                            last_dma = (seq, dma.last)
                            if dma.last is not None:
                                dma_by_seq[seq] = dma.last
                                r = dma.last
                                if r.is_mainrom:
                                    st["dma_mainrom"] += 1
                                elif via_flag:
                                    st["dma_banked_tektagt"] += 1
                                else:
                                    st["dma_banked_dmaoffset"] += 1
                                if not r.is_mainrom:
                                    hi = r.source_byte_offset + r.words * 4
                                    st["src_min"] = r.source_byte_offset if st["src_min"] is None else min(st["src_min"], r.source_byte_offset)
                                    st["src_max"] = hi if st["src_max"] is None else max(st["src_max"], hi)
                                st["dma_words_total"] += r.words
                        continue
                    if a == 0x1F000000:
                        st["bank_writes"] += 1
                    if size == 4:
                        ctrl.write32(a, v)
                    elif size == 2:
                        ctrl.write16(a, v)
                    else:
                        ctrl.write8(a, v)
                else:
                    if size == 4:
                        pred = ctrl.read32(a)
                    elif size == 2:
                        pred = ctrl.read16(a)
                    else:
                        pred = ctrl.read8(a)
                    got = (data & mask) >> (8 * lane)
                    st["reads_checked"] += 1
                    key = "%08x/%d" % (addr & ~3 if not (0x1FA00000 <= addr < 0x1FC00000 and addr not in (0x1FB00000, 0x1FB80000)) else 0x1FA00000, size)
                    st["prot_reads"][key] = st["prot_reads"].get(key, 0) + 1
                    if pred != got:
                        st["read_mismatch"] += 1
                        report("seq %d read %08x size %d: MAME %x model %x (pc=%s)" % (seq, a, size, got, pred, pc))
                continue
            if line.startswith("DMASTART"):
                d = dict(kv.split("=") for kv in line.split()[1:])
                seq = int(d["seq"])
                st["dma_started"] += 1
                if seq not in dma_by_seq:
                    st["model_dma_missing"] += 1
                    report("DMASTART seq %d: MAME ran a DMA but the model did not (chcr=%s)" % (seq, d["chcr"]))
                continue
            if line.startswith("DMAPOST"):
                d = dict(kv.split("=") for kv in line.split()[1:-0 or None] if "=" in kv)
                seq = int(d["seq"]); nblk = int(d["nblk"]); hashes = d["h"].split(",") if d["h"] else []
                res = dma_by_seq.get(seq)
                if res is None:
                    continue
                nominal = int(d["size"])
                st["sizes"][nominal] = st["sizes"].get(nominal, 0) + 1
                if res.words != nominal:
                    st["dma_truncated"] += 1
                words = [int.from_bytes(res.data[i:i + 4], "little") for i in range(0, len(res.data), 4)]
                for b in range(nblk):
                    chunk = words[b * 256:(b + 1) * 256]
                    want_n = min(256, nominal - b * 256)
                    if len(chunk) != want_n:
                        st["dma_unchecked_blocks"] += 1
                        continue
                    if "%08x" % blockhash(chunk) == hashes[b]:
                        st["dma_block_ok"] += 1
                    else:
                        st["dma_block_bad"] += 1
                        report("DMAPOST seq %d block %d hash mismatch (addr %s size %d)" % (seq, b, d["addr"], nominal))
                continue
            if line.startswith("DMADUMP") and dumpdir:
                d = dict(kv.split("=") for kv in line.split()[1:])
                seq = int(d["seq"])
                res = dma_by_seq.get(seq)
                fn = os.path.join(dumpdir, d["file"])
                if res is not None and os.path.exists(fn):
                    got = rb(fn)
                    ok = got == res.data
                    if ok:
                        st["dump_ok"] += 1
                    else:
                        k = fixup_delta(got, res.data)
                        if k is not None and k == (0x04000000 + res.source_byte_offset - 4) & M32:
                            st["dump_fixup_explained"] += 1     # CPU relocated every other word after the DMA
                        else:
                            st["dump_bad"] += 1
                            report("DMADUMP seq %d file %s differs from model" % (seq, d["file"]))
    st["final"] = {"dmaoffset": ctrl.dmaoffset, "tektagdmaoffset": ctrl.tektagdmaoffset, "ttt_cnt": ctrl.ttt_cnt,
                   "bank_entry": ctrl.bank_entry}
    return st, problems


def load_regions():
    banked = rb(os.path.join(REG, "bankedroms.bin"))
    main = rb(os.path.join(REG, "maincpu_rom.bin"))
    return banked, main


if __name__ == "__main__":
    import json
    p = sys.argv[1]
    dd = None
    if "--dumpdir" in sys.argv:
        dd = sys.argv[sys.argv.index("--dumpdir") + 1]
    banked, main = load_regions()
    st, probs = replay(p, banked, main, dd)
    print(json.dumps(st, indent=1, sort_keys=True))
    print("PROBLEMS (first %d):" % len(probs))
    for x in probs:
        print("  ", x)
