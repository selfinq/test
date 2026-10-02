"""Shared helpers for the tektagt differential-test harness (snapshot format ttt-difftest-snapshot/1).

Everything here is pure standard library so that the comparer and the unit tests run anywhere.
The format is documented in docs/difftest/CANDIDATE_SNAPSHOT.md and tools/difftest/snapshot.schema.json.
"""
import hashlib
import json
import os

SCHEMA = "ttt-difftest-snapshot/1"
EVENT_CHAIN_SEED = b"ttt-difftest-events/1"
PAGE_HASH_HEX = 16          # page hashes are the first 16 hex digits of SHA-256

GPR_NAMES = ["zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
             "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra"]

# keys that are NOT part of the canonical snapshot hash (producer specific / host specific)
HASH_EXCLUDED_TOP = ("producer", "meta", "snapshot_sha256")


def parse_int(v):
    if isinstance(v, int):
        return v
    return int(str(v), 0)


def hx(v, width=8):
    return "0x%0*x" % (width, v & ((1 << (4 * width)) - 1))


def canonical_json(obj):
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def snapshot_hash(snap):
    """Canonical hash of a snapshot: sha256 over canonical JSON without producer/meta/snapshot_sha256
    and without events.file (a file name, not content)."""
    d = {k: v for k, v in snap.items() if k not in HASH_EXCLUDED_TOP}
    if isinstance(d.get("events"), dict):
        d["events"] = {k: v for k, v in d["events"].items() if k != "file"}
    return sha256_hex(canonical_json(d).encode())


# ------------------------------------------------------------------ regions

def load_regions_config(path):
    with open(path) as f:
        cfg = json.load(f)
    check_regions(cfg["regions"], parse_int(cfg["ram_base"]), cfg["ram_size"])
    return cfg


def check_regions(regions, ram_base, ram_size):
    """Regions must tile [ram_base, ram_base+ram_size) in ascending order without gaps/overlaps."""
    a = ram_base
    for r in regions:
        if parse_int(r["base"]) != a:
            raise ValueError("region %s base %s does not continue at %s" % (r["name"], r["base"], hx(a)))
        if r["size"] <= 0:
            raise ValueError("region %s has non-positive size" % r["name"])
        a += r["size"]
    if a != ram_base + ram_size:
        raise ValueError("regions end at %s, RAM ends at %s" % (hx(a), hx(ram_base + ram_size)))


def region_of(regions, addr):
    for r in regions:
        b = parse_int(r["base"])
        if b <= addr < b + r["size"]:
            return r
    return None


# ------------------------------------------------------------------ RAM hashing (producer side)

def hash_ram(ram, regions_cfg, sp):
    """ram: bytes of the whole RAM window. Returns the 'ram' object of a checkpoint.
    Layout: {"regions": {name: {"sha256", "pages": [16 hex...]}}, "stack_live": {"base","size","sha256"}}"""
    base = parse_int(regions_cfg["ram_base"])
    page = regions_cfg["page_size"]
    out = {"regions": {}}
    for r in regions_cfg["regions"]:
        b = parse_int(r["base"]) - base
        chunk = ram[b:b + r["size"]]
        pages = [sha256_hex(chunk[o:o + page])[:PAGE_HASH_HEX] for o in range(0, len(chunk), page)]
        out["regions"][r["name"]] = {"sha256": sha256_hex(chunk), "pages": pages}
    top = parse_int(regions_cfg["stack_top"])
    s = sp & ~3
    if base <= s < top:
        live = ram[s - base:top - base]
        out["stack_live"] = {"base": hx(s), "size": len(live), "sha256": sha256_hex(live)}
    else:
        out["stack_live"] = {"base": hx(s), "size": 0, "sha256": sha256_hex(b"")}
    return out


# ------------------------------------------------------------------ events

def event_compared_text(ev):
    """Canonical text of the compared fields of one event (pc / width / frame are never compared).
    R/W: 'W,<addr8>,<data8>'   (exact byte address, value right aligned = zero extended access value)
    D:   'D,<ch>,<chcr8>,<madr8>,<bcr8>,<n>,<hash16>'  (DMA start summary)"""
    k = ev["k"]
    if k in ("R", "W"):
        return "%s,%08x,%08x" % (k, parse_int(ev["a"]), parse_int(ev["d"]) & 0xffffffff)
    if k == "D":
        return "D,%d,%08x,%08x,%08x,%d,%016x" % (ev["ch"], parse_int(ev["chcr"]), parse_int(ev["madr"]),
                                                  parse_int(ev["bcr"]), ev["n"], parse_int(ev["h"]))
    raise ValueError("unknown event kind %r" % (k,))


def chain_step(prev_hex, text):
    return sha256_hex((prev_hex + "|" + text).encode())


def chain_init():
    return sha256_hex(EVENT_CHAIN_SEED)


class EventChains:
    """Two independent hash chains: 'w' (writes + DMA starts) and 'r' (reads). Reads are separated
    because device reads can depend on emulated time/sound CPU and are often excluded from a compare."""

    def __init__(self):
        self.w = chain_init()
        self.r = chain_init()
        self.wn = 0
        self.rn = 0

    def add(self, ev):
        t = event_compared_text(ev)
        if ev["k"] == "R":
            self.r = chain_step(self.r, t)
            self.rn += 1
        else:
            self.w = chain_step(self.w, t)
            self.wn += 1

    def state(self):
        return {"w_count": self.wn, "w_chain": self.w, "r_count": self.rn, "r_chain": self.r}


# ------------------------------------------------------------------ validation

CP_REQUIRED = ("id", "sync")
HEX_RE_LEN = {"pc": 8}


def _is_hex(s, nd=None):
    if not isinstance(s, str) or not s.startswith("0x"):
        return False
    try:
        int(s, 16)
    except ValueError:
        return False
    return nd is None or len(s) == 2 + nd


def validate(snap):
    """Structural validation without third-party packages. Returns a list of error strings (empty = ok).
    The authoritative description is tools/difftest/snapshot.schema.json (checked with jsonschema when available)."""
    errs = []
    if not isinstance(snap, dict):
        return ["snapshot is not an object"]
    if snap.get("schema") != SCHEMA:
        errs.append("schema must be %r, got %r" % (SCHEMA, snap.get("schema")))
    for k in ("game", "capture", "regions", "checkpoints"):
        if k not in snap:
            errs.append("missing top-level key %r" % k)
    if errs:
        return errs
    regs = snap["regions"]
    names = set()
    for r in regs:
        if not all(k in r for k in ("name", "base", "size")):
            errs.append("region needs name/base/size: %r" % (r,))
        else:
            names.add(r["name"])
    ids = set()
    for i, cp in enumerate(snap["checkpoints"]):
        w = "checkpoints[%d]" % i
        for k in CP_REQUIRED:
            if k not in cp:
                errs.append("%s: missing %r" % (w, k))
        if cp.get("id") in ids:
            errs.append("%s: duplicate id %r" % (w, cp.get("id")))
        ids.add(cp.get("id"))
        s = cp.get("sync", {})
        if s.get("type") not in ("frame", "pc_hit"):
            errs.append("%s: sync.type must be frame|pc_hit" % w)
        elif s["type"] == "frame" and not isinstance(s.get("frame"), int):
            errs.append("%s: sync.frame must be int" % w)
        elif s["type"] == "pc_hit" and not (_is_hex(s.get("pc"), 8) and isinstance(s.get("nth"), int)):
            errs.append("%s: sync pc_hit needs pc (0x%%08x) and nth" % w)
        cpu = cp.get("cpu")
        if cpu is not None:
            if not _is_hex(cpu.get("pc"), 8):
                errs.append("%s.cpu.pc must be 0x%%08x" % w)
            g = cpu.get("gpr")
            if not (isinstance(g, list) and len(g) == 32 and all(_is_hex(x, 8) for x in g)):
                errs.append("%s.cpu.gpr must be 32 hex words" % w)
            for k in ("hi", "lo"):
                if k in cpu and not _is_hex(cpu[k], 8):
                    errs.append("%s.cpu.%s must be 0x%%08x" % (w, k))
            for k in ("cop2_d", "cop2_c"):
                if k in cpu and not (isinstance(cpu[k], list) and len(cpu[k]) == 32):
                    errs.append("%s.cpu.%s must have 32 entries" % (w, k))
        ram = cp.get("ram")
        if ram is not None:
            for n, e in ram.get("regions", {}).items():
                if n not in names:
                    errs.append("%s.ram.regions: unknown region %r" % (w, n))
                if not (isinstance(e.get("sha256"), str) and len(e["sha256"]) == 64):
                    errs.append("%s.ram.regions.%s.sha256 must be 64 hex" % (w, n))
                if "pages" in e and not all(isinstance(p, str) and len(p) == PAGE_HASH_HEX for p in e["pages"]):
                    errs.append("%s.ram.regions.%s.pages must be %d-hex strings" % (w, n, PAGE_HASH_HEX))
        ev = cp.get("events")
        if ev is not None:
            for k in ("w_count", "w_chain", "r_count", "r_chain"):
                if k not in ev:
                    errs.append("%s.events missing %r" % (w, k))
    return errs


def load_snapshot(path):
    with open(path) as f:
        return json.load(f)


def load_events(path):
    """Read the events sidecar (JSON lines). Returns list of dicts."""
    out = []
    with open(path) as f:
        for ln in f:
            ln = ln.strip()
            if ln:
                out.append(json.loads(ln))
    return out


def resolve_events_path(snapshot_path, snap):
    ev = snap.get("events") or {}
    fn = ev.get("file")
    if not fn:
        return None
    p = fn if os.path.isabs(fn) else os.path.join(os.path.dirname(os.path.abspath(snapshot_path)), fn)
    return p if os.path.exists(p) else None
