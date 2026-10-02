"""Synthetic snapshot builder for the difftest unit tests (no MAME, no ROM data involved)."""
import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "difftest"))
import snapshotlib as sl  # noqa: E402

PAGE = 256
RAM_BASE = 0x80000000
RAM_SIZE = 0x10000
REGIONS_CFG = {
    "page_size": PAGE, "ram_base": "0x80000000", "ram_size": RAM_SIZE, "stack_top": "0x80010000",
    "regions": [
        {"name": "code", "base": "0x80000000", "size": 0x4000},
        {"name": "data", "base": "0x80004000", "size": 0x4000},
        {"name": "bss", "base": "0x80008000", "size": 0x6000},
        {"name": "stack", "base": "0x8000e000", "size": 0x2000},
    ],
}


def make_ram(seed=1):
    b = bytearray(RAM_SIZE)
    x = seed
    for i in range(0, RAM_SIZE, 4):
        x = (x * 1103515245 + 12345) & 0xffffffff
        b[i:i + 4] = x.to_bytes(4, "little")
    return b


def poke(ram, addr, val=0xA5):
    ram[addr - RAM_BASE] ^= val


def regs(**over):
    g = ["0x%08x" % (0x1000 + i) for i in range(32)]
    g[0] = "0x00000000"
    g[29] = "0x8000fff0"   # sp
    cpu = {"pc": "0x80001000", "gpr": g, "hi": "0x00000001", "lo": "0x00000002",
           "cop0": {"sr": "0x40000401", "cause": "0x00000000", "epc": "0x00000000", "badvaddr": "0x00000000"},
           "cop2_d": ["0x%08x" % i for i in range(32)], "cop2_c": ["0x%08x" % (100 + i) for i in range(32)]}
    for k, v in over.items():
        if k.startswith("gpr_"):
            cpu["gpr"][sl.GPR_NAMES.index(k[4:])] = "0x%08x" % v
        elif k.startswith("cop0_"):
            cpu["cop0"][k[5:]] = "0x%08x" % v
        else:
            cpu[k] = v if isinstance(v, str) else "0x%08x" % v
    return cpu


def checkpoint(cid, sync, ram, cpu=None, ev=None):
    sp = sl.parse_int(cpu["gpr"][29]) if cpu else 0x8000fff0
    cp = {"id": cid, "sync": sync, "ram": sl.hash_ram(bytes(ram), REGIONS_CFG, sp)}
    if cpu is not None:
        cp["cpu"] = cpu
    if ev is not None:
        cp["events"] = ev
    return cp


def snapshot(cps, events=None):
    s = {"schema": sl.SCHEMA, "game": "tektagt", "producer": {"name": "synthetic"},
         "capture": {"page_size": PAGE, "ram_base": "0x80000000", "ram_size": RAM_SIZE},
         "regions": [{"name": r["name"], "base": r["base"], "size": r["size"]} for r in REGIONS_CFG["regions"]],
         "checkpoints": cps, "meta": {}}
    if events is not None:
        ch = sl.EventChains()
        for e in events:
            ch.add(e)
        s["events"] = dict(ch.state(), file="x.events.jsonl", truncated=False)
    s["snapshot_sha256"] = sl.snapshot_hash(s)
    return s


def ev_chain_state(events):
    ch = sl.EventChains()
    for e in events:
        ch.add(e)
    return ch.state()


def W(addr, data):
    return {"k": "W", "a": "0x%08x" % addr, "d": "0x%08x" % data}


def R(addr, data):
    return {"k": "R", "a": "0x%08x" % addr, "d": "0x%08x" % data}


def D(ch, chcr, madr, bcr, n, h):
    return {"k": "D", "ch": ch, "chcr": "0x%08x" % chcr, "madr": "0x%08x" % madr, "bcr": "0x%08x" % bcr, "n": n, "h": "0x%016x" % h}


DEFAULT_EVENTS = [W(0x1f801810, 0xe1001000), W(0x1f8010a0, 0x80123456), D(2, 0x01000401, 0x80123456, 0, 12, 0x1234),
                  W(0x1f801814, 0x03000000)]


def build(ram_pokes=None, cpu_over=None, events=None, drop=(), splits=(1, 2, 4), sp=None):
    """3 checkpoints: frame_0001 (frame), pc_80001000_1 (pc_hit), frame_0002 (frame).
    ram_pokes {idx: [addr,...]} flips a byte in that checkpoint's RAM only; cpu_over {idx: regs kwargs};
    the bss page at 0x80008000 changes between checkpoints 0->1 and 0x80008100 between 1->2 (volatile pages)."""
    ram_pokes, cpu_over = ram_pokes or {}, cpu_over or {}
    events = DEFAULT_EVENTS if events is None else events
    rams = [make_ram(1)]
    rams.append(bytearray(rams[0])); poke(rams[1], 0x80008000)
    rams.append(bytearray(rams[1])); poke(rams[2], 0x80008100)
    ids = [("frame_0001", {"type": "frame", "frame": 1}),
           ("pc_80001000_1", {"type": "pc_hit", "pc": "0x80001000", "nth": 1}),
           ("frame_0002", {"type": "frame", "frame": 2})]
    cps = []
    for i, (cid, sync) in enumerate(ids):
        ram = bytearray(rams[i])
        for a in ram_pokes.get(i, []):
            poke(ram, a)
        kw = dict(cpu_over.get(i, {}))
        if sp is not None:
            kw["gpr_sp"] = sp
        cpu = regs(**kw)
        cps.append(checkpoint(cid, sync, ram, cpu, ev_chain_state(events[:splits[i]])))
    cps = [c for c in cps if c["id"] not in drop]
    return snapshot(cps, events)


def clone(s):
    return copy.deepcopy(s)
