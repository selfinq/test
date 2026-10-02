#!/usr/bin/env python3
"""Generate tests/difftest/vectors.json: input/expected-output vectors for every hash defined by the snapshot format,
so that an independent implementation (the C# runtime) can be checked against the Python one without MAME.
    python3 tools/difftest/make_vectors.py > tests/difftest/vectors.json
"""
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import snapshotlib as sl  # noqa: E402


def main():
    v = {"format": sl.SCHEMA, "note": "inputs and expected outputs of the hashes in docs/difftest/CANDIDATE_SNAPSHOT.md"}
    # 1. event chains
    evs = [{"k": "W", "a": "0x1f801810", "d": "0xe1001000"}, {"k": "R", "a": "0x1f801074", "d": "0x00000009"},
           {"k": "W", "a": "0x1f8010a8", "d": "0x01000401"},
           {"k": "D", "ch": 2, "chcr": "0x01000401", "madr": "0x803a67d8", "bcr": "0x00000000", "n": 12, "h": "0x00000000deadbeef"},
           {"k": "W", "a": "0x1f801d82", "d": "0x0000ffff"}]
    ch = sl.EventChains()
    states = []
    for e in evs:
        ch.add(e)
        states.append(ch.state())
    v["events"] = {"events": evs, "canonical_text": [sl.event_compared_text(e) for e in evs], "chain_seed_hex": sl.chain_init(),
                   "state_after_each_event": states}
    # 2. FNV-1a 64 over words
    words = [0x00000000, 0x12345678, 0xffffffff, 0x80000001]
    v["fnv1a64"] = {"words": ["0x%08x" % w for w in words], "hash": "0x%016x" % sl.fnv1a64_words(words)}
    # 3. DMA summaries on a tiny RAM image (little endian words), RAM mask 0x3ffffc
    ram = {}
    ram[0x1000] = (3 << 24) | 0x002000                    # node 0: 3 payload words, next = 0x2000
    ram[0x1004], ram[0x1008], ram[0x100c] = 0xaaaa0001, 0xbbbb0002, 0xcccc0003
    ram[0x2000] = (1 << 24) | 0xffffff                    # node 1: 1 payload word, terminator
    ram[0x2004] = 0x11223344
    for i in range(8):
        ram[0x3000 + 4 * i] = 0x01010101 * (i + 1)
    rd = lambda a: ram.get(a & 0x3ffffc, 0)
    cases = []
    for name, chcr, madr, bcr in (("linked list from 0x80001000", 0x01000401, 0x80001000, 0),
                                  ("burst 8 words", 0x01000001, 0x00003000, 8),
                                  ("slice 2 blocks x 3", 0x01000201, 0x80003000, (2 << 16) | 3),
                                  ("into RAM, forward (ch5 style)", 0x01000000, 0x00003000, 4),
                                  ("into RAM, step -4 (ch6 OTC style)", 0x11000002, 0x0000301c, 8)):
        n, h = sl.dma_summary(rd, chcr, madr, bcr)
        cases.append({"name": name, "chcr": "0x%08x" % chcr, "madr": "0x%08x" % madr, "bcr": "0x%08x" % bcr, "n": n, "h": "0x%016x" % h})
    v["dma"] = {"ram_words": {"0x%08x" % k: "0x%08x" % w for k, w in sorted(ram.items())}, "cases": cases}
    # 4. RAM hashing on a tiny RAM with 3 regions, 256 byte pages
    cfg = {"page_size": 256, "ram_base": "0x80000000", "ram_size": 2048, "stack_top": "0x80000800",
           "regions": [{"name": "a", "base": "0x80000000", "size": 512}, {"name": "b", "base": "0x80000200", "size": 1000},
                       {"name": "stack", "base": "0x800005e8", "size": 536}]}
    sl.check_regions(cfg["regions"], 0x80000000, 2048)
    data = bytes((i * 7 + (i >> 8)) & 0xff for i in range(2048))
    v["ram"] = {"config": cfg, "ram_hex": data.hex(), "sp": "0x800007f1", "expected": sl.hash_ram(data, cfg, 0x800007f1)}
    # 5. snapshot hash
    snap = {"schema": sl.SCHEMA, "game": "tektagt", "producer": {"name": "x"}, "meta": {"y": 1}, "capture": {"ram_base": "0x80000000",
            "ram_size": 2048, "page_size": 256}, "regions": [], "checkpoints": [], "events": {"w_count": 0, "w_chain": "0" * 64,
            "r_count": 0, "r_chain": "0" * 64, "file": "f"}}
    v["snapshot_hash"] = {"snapshot": snap, "sha256": sl.snapshot_hash(snap), "canonical_json": sl.canonical_json(
        {k: (dict((a, b) for a, b in x.items() if a != "file") if k == "events" else x) for k, x in snap.items()
         if k not in sl.HASH_EXCLUDED_TOP})}
    json.dump(v, sys.stdout, indent=1, sort_keys=True)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
