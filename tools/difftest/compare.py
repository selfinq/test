#!/usr/bin/env python3
"""Compare two ttt-difftest snapshots (reference = MAME, candidate = recompiled runtime) and report the
FIRST divergence with context.

    python3 tools/difftest/compare.py REF.json CAND.json [--config cfg.json] [--context 5] [--json]
                                      [--strict-unverified] [--events-ref F] [--events-cand F]

Exit codes: 0 no divergence (items may still be 'unverified'/'ignored', both are listed), 1 divergence,
2 unusable input, 3 only with --strict-unverified: nothing diverged but something could not be verified.

What is compared is decided by the normalisation config (see DEFAULT_CONFIG / compare_config.default.json):
  * per checkpoint sync type a profile: cpu exact|ignore, ram exact|static_pages|ignore, events chain|ignore.
    'pc_hit' checkpoints are instruction exact (the state may be compared bit for bit); 'frame' checkpoints are
    taken at a host frame boundary, so a candidate without cycle accurate timing cannot reproduce registers or
    volatile memory there: only pages that never change in the reference run ('static_pages') are compared.
  * per region overrides, ignore ranges (with a mandatory human readable reason), register ignore lists and bit masks,
    live-stack comparison (the dead stack below SP is garbage and never compared).
  * events: ignore ranges / kinds, and whether a shorter candidate stream is acceptable ('prefix_ok').
Every exclusion that was applied is reported in the output so a PASS can never silently hide one.
"""
import argparse
import copy
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import snapshotlib as sl  # noqa: E402

DEFAULT_CONFIG = {
    "profiles": {
        "pc_hit": {"cpu": "exact", "ram": "exact", "events": "chain"},
        "frame": {"cpu": "ignore", "ram": "static_pages", "events": "ignore"},
    },
    "cpu": {"ignore_fields": [], "masks": {}},
    "ram": {
        "regions": {"stack": "ignore"},
        "stack_live": "exact",
        "ignore_ranges": [],
    },
    "events": {
        "compare_sidecar": True,
        "length": "prefix_ok",
        "ignore_kinds": [],
        "ignore_ranges": [],
    },
    "require_ids": [],
    "ids": None,
}


def merge(base, over):
    out = copy.deepcopy(base)
    for k, v in (over or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict) and k not in ("masks",):
            out[k] = merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def load_config(path):
    if not path:
        return copy.deepcopy(DEFAULT_CONFIG)
    with open(path) as f:
        return merge(DEFAULT_CONFIG, json.load(f))


# ---------------------------------------------------------------------------------- helpers

def _hexs(v, w=8):
    return "0x%0*x" % (w, v)


def cpu_items(cpu):
    """Ordered list of (path, int) for every register present."""
    items = []
    if "pc" in cpu:
        items.append(("pc", sl.parse_int(cpu["pc"])))
    if "gpr" in cpu:
        for n, v in zip(sl.GPR_NAMES, cpu["gpr"]):
            items.append(("gpr." + n, sl.parse_int(v)))
    for k in ("hi", "lo"):
        if k in cpu:
            items.append((k, sl.parse_int(cpu[k])))
    for k, v in (cpu.get("cop0") or {}).items():
        items.append(("cop0." + k, sl.parse_int(v)))
    for grp in ("cop2_d", "cop2_c"):
        if grp in cpu:
            for i, v in enumerate(cpu[grp]):
                items.append(("%s.%d" % (grp, i), sl.parse_int(v)))
    return items


def _ignored_field(path, ignore_fields):
    for p in ignore_fields:
        if path == p or path.startswith(p + "."):
            return True
    return False


def _range_overlap(a0, a1, b0, b1):
    return a0 < b1 and b0 < a1


def _ranges(cfg_list):
    out = []
    for r in cfg_list:
        if not r.get("reason"):
            raise ValueError("ignore range %r needs a 'reason' (exclusions must be justified)" % (r,))
        b = sl.parse_int(r["base"])
        out.append((b, b + sl.parse_int(r["size"]), r["reason"]))
    return out


def static_page_map(snap):
    """{region: set(page index)} of pages whose hash is identical in every reference checkpoint that has pages."""
    seen = {}
    for cp in snap.get("checkpoints", []):
        for rn, e in ((cp.get("ram") or {}).get("regions") or {}).items():
            pg = e.get("pages")
            if pg is None:
                continue
            cur = seen.setdefault(rn, {})
            for i, h in enumerate(pg):
                cur.setdefault(i, set()).add(h)
    return {rn: {i for i, hs in d.items() if len(hs) == 1} for rn, d in seen.items()}


# ---------------------------------------------------------------------------------- checkpoint comparison

def compare_cpu(rcp, ccp, mode, cfg):
    items = []
    if mode == "ignore":
        return [{"path": "cpu", "status": "ignored", "note": "profile ignores registers for this sync type"}]
    rc, cc = rcp.get("cpu"), ccp.get("cpu")
    if rc is None:
        return []
    if cc is None:
        return [{"path": "cpu", "status": "unverified", "note": "candidate provides no cpu state"}]
    cm = {p: v for p, v in cpu_items(cc)}
    for path, rv in cpu_items(rc):
        if _ignored_field(path, cfg["cpu"]["ignore_fields"]):
            items.append({"path": path, "status": "ignored", "note": "listed in cpu.ignore_fields"})
            continue
        if path not in cm:
            items.append({"path": path, "status": "unverified", "note": "candidate does not provide this register"})
            continue
        cv = cm[path]
        mask = sl.parse_int(cfg["cpu"]["masks"].get(path, 0xffffffff))
        if (rv & mask) != (cv & mask):
            it = {"path": path, "status": "diverge", "ref": _hexs(rv), "cand": _hexs(cv)}
            if mask != 0xffffffff:
                it["mask"] = _hexs(mask)
                it["differing_bits"] = _hexs((rv ^ cv) & mask)
            items.append(it)
        else:
            items.append({"path": path, "status": "match"})
    return items


def compare_ram(rcp, ccp, mode, cfg, regions, static_pages, page_size):
    items = []
    rram, cram = rcp.get("ram"), ccp.get("ram")
    if rram is None or mode == "ignore":
        return [{"path": "ram", "status": "ignored", "note": "profile ignores RAM"}] if mode == "ignore" else []
    if cram is None:
        return [{"path": "ram", "status": "unverified", "note": "candidate provides no RAM state"}]
    ign = _ranges(cfg["ram"]["ignore_ranges"])
    reg_by_name = {r["name"]: r for r in regions}
    for rn, re_ in (rram.get("regions") or {}).items():
        rmode = cfg["ram"]["regions"].get(rn, mode)
        path = "ram.%s" % rn
        r = reg_by_name.get(rn)
        base = sl.parse_int(r["base"]) if r else 0
        size = r["size"] if r else 0
        if rmode == "ignore":
            items.append({"path": path, "status": "ignored", "note": "region mode ignore (ram.regions)"})
            continue
        ce = (cram.get("regions") or {}).get(rn)
        if ce is None:
            items.append({"path": path, "status": "unverified", "note": "candidate provides no hash for this region"})
            continue
        region_ign = [(a, b, why) for a, b, why in ign if _range_overlap(a, b, base, base + size)]
        if rmode == "exact" and re_["sha256"] == ce["sha256"]:
            items.append({"path": path, "status": "match"})
            continue
        rp, cp_ = re_.get("pages"), ce.get("pages")
        if rmode == "exact" and (rp is None or cp_ is None):
            st = "unverified" if region_ign else "diverge"
            items.append({"path": path, "status": st, "ref": re_["sha256"][:16], "cand": ce["sha256"][:16],
                          "note": "region hash differs; no page hashes to localise or apply ignore ranges" if st == "unverified"
                          else "region hash differs (no page hashes available to localise)"})
            continue
        if rp is None or cp_ is None:
            items.append({"path": path, "status": "unverified", "note": "static_pages needs page hashes in both snapshots"})
            continue
        if len(rp) != len(cp_):
            items.append({"path": path, "status": "diverge", "note": "different page counts %d vs %d" % (len(rp), len(cp_))})
            continue
        stat = static_pages.get(rn, set()) if rmode == "static_pages" else None
        diff_pages, ignored_pages, skipped = [], 0, 0
        for i, (a, b) in enumerate(zip(rp, cp_)):
            if stat is not None and i not in stat:
                skipped += 1
                continue
            if a == b:
                continue
            p0 = base + i * page_size
            p1 = min(p0 + page_size, base + size)
            if any(x <= p0 and p1 <= y for x, y, _ in region_ign):
                ignored_pages += 1
                continue
            diff_pages.append(i)
        if diff_pages:
            i0 = diff_pages[0]
            a0 = base + i0 * page_size
            items.append({"path": path, "status": "diverge", "ref": re_["sha256"][:16], "cand": ce["sha256"][:16],
                          "first_page": i0, "first_addr": _hexs(a0), "page_range": [_hexs(a0), _hexs(a0 + page_size - 1)],
                          "differing_pages": len(diff_pages),
                          "differing_addrs": [_hexs(base + i * page_size) for i in diff_pages[:8]],
                          "page_hash_ref": rp[i0], "page_hash_cand": cp_[i0]})
        else:
            note = []
            if skipped:
                note.append("%d volatile pages skipped (static_pages)" % skipped)
            if ignored_pages:
                note.append("%d differing pages inside ignore ranges: %s" % (
                    ignored_pages, "; ".join(sorted({w for _, _, w in region_ign}))))
            items.append({"path": path, "status": "ignored" if (skipped or ignored_pages) else "match", "note": ", ".join(note)})
    # live stack
    rs, cs = rram.get("stack_live"), cram.get("stack_live")
    if rs is not None and cfg["ram"]["stack_live"] == "exact" and mode != "static_pages":
        if cs is None:
            items.append({"path": "ram.stack_live", "status": "unverified", "note": "candidate provides no stack_live"})
        elif (rs["base"], rs["size"], rs["sha256"]) != (cs["base"], cs["size"], cs["sha256"]):
            items.append({"path": "ram.stack_live", "status": "diverge",
                          "ref": "base=%s size=%d sha=%s" % (rs["base"], rs["size"], rs["sha256"][:16]),
                          "cand": "base=%s size=%d sha=%s" % (cs["base"], cs["size"], cs["sha256"][:16])})
        else:
            items.append({"path": "ram.stack_live", "status": "match"})
    return items


def compare_events_chain(rcp, ccp, mode, cfg):
    if mode == "ignore":
        return [{"path": "events", "status": "ignored", "note": "profile ignores event chains for this sync type"}]
    re_, ce = rcp.get("events"), ccp.get("events")
    if re_ is None:
        return []
    if ce is None:
        return [{"path": "events", "status": "unverified", "note": "candidate provides no event counters"}]
    ev = cfg["events"]
    if ev["ignore_kinds"] or ev["ignore_ranges"]:
        return [{"path": "events", "status": "ignored",
                 "note": "event filters configured: whole-stream chain not comparable, see sidecar comparison"}]
    items = []
    for k in ("w", "r"):
        a = (re_[k + "_count"], re_[k + "_chain"])
        b = (ce[k + "_count"], ce[k + "_chain"])
        if a == b:
            items.append({"path": "events." + k, "status": "match"})
        else:
            items.append({"path": "events." + k, "status": "diverge", "ref": "count=%d chain=%s" % (a[0], a[1][:16]),
                          "cand": "count=%d chain=%s" % (b[0], b[1][:16])})
    return items


# ---------------------------------------------------------------------------------- event stream comparison

def ev_text(e):
    try:
        return sl.event_compared_text(e)
    except Exception:  # malformed event line
        return "?" + json.dumps(e, sort_keys=True)


def _ev_filtered(e, cfg):
    ev = cfg["events"]
    if e.get("k") in ev["ignore_kinds"]:
        return True
    if e.get("k") in ("R", "W") and ev["ignore_ranges"]:
        a = sl.parse_int(e["a"])
        for x, y, _ in _ranges(ev["ignore_ranges"]):
            if x <= a < y:
                return True
    return False


def compare_event_streams(ref_ev, cand_ev, cfg, context):
    """Returns dict(status, ...) where status in match|diverge. Indices are positions in the UNFILTERED streams."""
    ri = [(i, e) for i, e in enumerate(ref_ev) if not _ev_filtered(e, cfg)]
    ci = [(i, e) for i, e in enumerate(cand_ev) if not _ev_filtered(e, cfg)]
    n = min(len(ri), len(ci))
    for k in range(n):
        if ev_text(ri[k][1]) != ev_text(ci[k][1]):
            return _ev_div(ref_ev, cand_ev, ri[k][0], ci[k][0], k, len(ri), len(ci), context, "differs")
    if len(ri) != len(ci):
        if cfg["events"]["length"] == "prefix_ok":
            short = "candidate" if len(ci) < len(ri) else "reference"
            return {"status": "match", "compared": n, "ref_events": len(ri), "cand_events": len(ci),
                    "note": "%s stream is shorter; compared the common prefix of %d events" % (short, n)}
        return _ev_div(ref_ev, cand_ev, ri[n][0] if n < len(ri) else len(ref_ev),
                       ci[n][0] if n < len(ci) else len(cand_ev), n, len(ri), len(ci), context, "length")
    return {"status": "match", "compared": n, "ref_events": len(ri), "cand_events": len(ci)}


def _ev_div(ref_ev, cand_ev, rgi, cgi, k, nr, nc, context, why):
    def window(evs, gi):
        lo = max(0, gi - context)
        return [{"index": j, "text": ev_text(evs[j]), "marker": j == gi} for j in range(lo, min(len(evs), gi + context + 1))]
    return {"status": "diverge", "reason": why, "filtered_position": k, "ref_index": rgi, "cand_index": cgi,
            "ref_events": nr, "cand_events": nc,
            "ref_text": ev_text(ref_ev[rgi]) if rgi < len(ref_ev) else "<end of stream>",
            "cand_text": ev_text(cand_ev[cgi]) if cgi < len(cand_ev) else "<end of stream>",
            "ref_context": window(ref_ev, rgi), "cand_context": window(cand_ev, cgi)}


# ---------------------------------------------------------------------------------- top level

def worst(items):
    order = {"diverge": 3, "unverified": 2, "ignored": 1, "match": 0}
    return max((order[i["status"]] for i in items), default=0)


STATUS_BY_RANK = {3: "diverge", 2: "unverified", 1: "ignored", 0: "match"}


def compare(ref, cand, cfg=None, ref_events=None, cand_events=None, context=5):
    cfg = cfg or copy.deepcopy(DEFAULT_CONFIG)
    rep = {"verdict": None, "config_effective": cfg, "checkpoints": [], "events": None, "first_divergence": None,
           "notes": [], "summary": {}}
    regions = ref.get("regions", [])
    page_size = (ref.get("capture") or {}).get("page_size", 4096)
    if ref.get("regions") != cand.get("regions"):
        rep["notes"].append("region tables differ between snapshots; comparing by region name only")
    if (ref.get("capture") or {}).get("page_size") != (cand.get("capture") or {}).get("page_size"):
        rep["notes"].append("page sizes differ (%s vs %s): page hashes are not comparable" % (
            (ref.get("capture") or {}).get("page_size"), (cand.get("capture") or {}).get("page_size")))
    static = static_page_map(ref)
    cand_by_id = {c["id"]: c for c in cand.get("checkpoints", [])}
    want = cfg.get("ids")
    req = set(cfg.get("require_ids") or [])
    last_good = None
    first_cp_div = None
    for rcp in ref.get("checkpoints", []):
        cid = rcp["id"]
        if want is not None and cid not in want:
            continue
        ccp = cand_by_id.get(cid)
        stype = rcp["sync"]["type"]
        prof = cfg["profiles"].get(stype, {"cpu": "exact", "ram": "exact", "events": "chain"})
        entry = {"id": cid, "sync": rcp["sync"], "profile": prof, "items": []}
        if ccp is None:
            st = "diverge" if cid in req else "unverified"
            entry["items"].append({"path": "checkpoint", "status": st,
                                   "note": "checkpoint %r missing in candidate%s" % (cid, " (listed in require_ids)" if cid in req else "")})
        else:
            if ccp["sync"] != rcp["sync"]:
                entry["items"].append({"path": "sync", "status": "unverified",
                                       "note": "sync keys differ: ref %s cand %s" % (rcp["sync"], ccp["sync"])})
            entry["items"] += compare_cpu(rcp, ccp, prof["cpu"], cfg)
            entry["items"] += compare_ram(rcp, ccp, prof["ram"], cfg, regions, static, page_size)
            entry["items"] += compare_events_chain(rcp, ccp, prof["events"], cfg)
        entry["status"] = STATUS_BY_RANK[worst(entry["items"])]
        pos = None
        if rcp.get("events"):
            pos = rcp["events"]["w_count"] + rcp["events"]["r_count"]
        entry["event_position"] = pos
        if entry["status"] == "diverge" and first_cp_div is None:
            first_cp_div = entry
            entry["last_good_checkpoint"] = last_good
        if entry["status"] in ("match", "ignored"):
            last_good = cid
        rep["checkpoints"].append(entry)

    # event sidecars
    ev_div = None
    if cfg["events"]["compare_sidecar"] and ref_events is not None and cand_events is not None:
        rep["events"] = compare_event_streams(ref_events, cand_events, cfg, context)
        if rep["events"]["status"] == "diverge":
            ev_div = rep["events"]
    elif cfg["events"]["compare_sidecar"]:
        rep["events"] = {"status": "unverified", "note": "event sidecar missing for %s" % (
            "both" if ref_events is None and cand_events is None else ("reference" if ref_events is None else "candidate"))}

    # headline: earliest of checkpoint divergence and event divergence (by reference event position)
    first = None
    if first_cp_div is not None:
        d = next(i for i in first_cp_div["items"] if i["status"] == "diverge")
        first = {"kind": "checkpoint", "checkpoint": first_cp_div["id"], "sync": first_cp_div["sync"],
                 "item": d, "all_diverging_items": [i["path"] for i in first_cp_div["items"] if i["status"] == "diverge"],
                 "last_good_checkpoint": first_cp_div.get("last_good_checkpoint")}
        idx = [c["id"] for c in rep["checkpoints"]].index(first_cp_div["id"])
        first["next_checkpoints"] = [c["id"] for c in rep["checkpoints"][idx + 1: idx + 3]]
    if ev_div is not None:
        pos = first_cp_div["event_position"] if first_cp_div else None
        if first is None or pos is None or ev_div["ref_index"] < pos:
            first = {"kind": "event", "event": ev_div,
                     "before_checkpoint": first_cp_div["id"] if first_cp_div else None}
    rep["first_divergence"] = first
    all_items = [i for c in rep["checkpoints"] for i in c["items"]]
    if rep["events"]:
        all_items.append({"status": rep["events"]["status"]})
    rep["summary"] = {s: sum(1 for i in all_items if i["status"] == s) for s in ("match", "ignored", "unverified", "diverge")}
    rep["summary"]["checkpoints_compared"] = len(rep["checkpoints"])
    rep["verdict"] = "DIVERGE" if first else ("UNVERIFIED" if rep["summary"]["unverified"] else "MATCH")
    return rep


# ---------------------------------------------------------------------------------- text report

def format_report(rep, verbose=False):
    L = []
    s = rep["summary"]
    L.append("VERDICT: %s   (%d checkpoints; items: %d match, %d ignored, %d unverified, %d diverge)" % (
        rep["verdict"], s["checkpoints_compared"], s["match"], s["ignored"], s["unverified"], s["diverge"]))
    for n in rep["notes"]:
        L.append("note: " + n)
    fd = rep["first_divergence"]
    if fd:
        L.append("")
        L.append("FIRST DIVERGENCE (%s)" % fd["kind"])
        if fd["kind"] == "checkpoint":
            L.append("  checkpoint : %s   sync=%s" % (fd["checkpoint"], json.dumps(fd["sync"], sort_keys=True)))
            it = fd["item"]
            L.append("  item       : %s" % it["path"])
            for k in ("ref", "cand", "mask", "differing_bits", "first_addr", "page_range", "differing_pages",
                      "differing_addrs", "page_hash_ref", "page_hash_cand", "note"):
                if k in it:
                    L.append("  %-11s: %s" % (k, it[k]))
            L.append("  all diverging items in this checkpoint: %s" % ", ".join(fd["all_diverging_items"]))
            L.append("  last good checkpoint before it: %s" % (fd["last_good_checkpoint"] or "<none>"))
            if fd["next_checkpoints"]:
                L.append("  next checkpoints: %s" % ", ".join(fd["next_checkpoints"]))
        else:
            e = fd["event"]
            L.append("  event stream %s at ref event #%d / cand event #%d (position %d in the filtered streams)" % (
                e["reason"], e["ref_index"], e["cand_index"], e["filtered_position"]))
            L.append("  ref : %s" % e["ref_text"])
            L.append("  cand: %s" % e["cand_text"])
            if fd.get("before_checkpoint"):
                L.append("  (a later checkpoint %s also diverges)" % fd["before_checkpoint"])
            L.append("  reference context:")
            for w in e["ref_context"]:
                L.append("    %s #%d %s" % (">>" if w["marker"] else "  ", w["index"], w["text"]))
            L.append("  candidate context:")
            for w in e["cand_context"]:
                L.append("    %s #%d %s" % (">>" if w["marker"] else "  ", w["index"], w["text"]))
    L.append("")
    L.append("per checkpoint:")
    for c in rep["checkpoints"]:
        L.append("  %-18s %-10s %s" % (c["id"], c["status"].upper(), json.dumps(c["sync"], sort_keys=True)))
        for it in c["items"]:
            if verbose or it["status"] in ("diverge", "unverified") or (it["status"] == "ignored" and not it["path"].startswith(("gpr.", "cop"))):
                extra = it.get("note") or ""
                if it["status"] == "diverge" and "ref" in it:
                    extra = "ref=%s cand=%s %s" % (it["ref"], it["cand"], extra)
                L.append("      %-10s %-22s %s" % (it["status"], it["path"], extra))
    if rep["events"]:
        e = rep["events"]
        L.append("event streams: %s %s" % (e["status"], e.get("note", "") or
                                          ("(%d ref / %d cand events)" % (e.get("ref_events", 0), e.get("cand_events", 0)))))
    eff = rep["config_effective"]
    excl = []
    if eff["cpu"]["ignore_fields"]:
        excl.append("cpu fields: " + ", ".join(eff["cpu"]["ignore_fields"]))
    if eff["cpu"]["masks"]:
        excl.append("cpu masks: " + json.dumps(eff["cpu"]["masks"], sort_keys=True))
    for r in eff["ram"]["ignore_ranges"]:
        excl.append("ram ignore %s+%s: %s" % (r["base"], r["size"], r["reason"]))
    for r in eff["events"]["ignore_ranges"]:
        excl.append("event ignore %s+%s: %s" % (r["base"], r["size"], r["reason"]))
    if eff["events"]["ignore_kinds"]:
        excl.append("event kinds ignored: " + ",".join(eff["events"]["ignore_kinds"]))
    excl.append("profiles: " + json.dumps(eff["profiles"], sort_keys=True))
    excl.append("ram region modes: " + json.dumps(eff["ram"]["regions"], sort_keys=True))
    L.append("")
    L.append("normalisation applied:")
    L += ["  - " + x for x in excl]
    return "\n".join(L)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ref")
    ap.add_argument("cand")
    ap.add_argument("--config")
    ap.add_argument("--context", type=int, default=5, help="events of context around an event divergence")
    ap.add_argument("--json", action="store_true", help="print the full report as JSON")
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--strict-unverified", action="store_true", help="exit 3 when nothing diverged but items are unverified")
    ap.add_argument("--events-ref", help="reference events sidecar (default: from the snapshot)")
    ap.add_argument("--events-cand", help="candidate events sidecar (default: from the snapshot)")
    a = ap.parse_args(argv)
    try:
        ref, cand = sl.load_snapshot(a.ref), sl.load_snapshot(a.cand)
        for name, s in (("reference", ref), ("candidate", cand)):
            errs = sl.validate(s)
            if errs:
                print("%s snapshot invalid:\n  %s" % (name, "\n  ".join(errs[:20])), file=sys.stderr)
                return 2
        cfg = load_config(a.config)
        rp = a.events_ref or sl.resolve_events_path(a.ref, ref)
        cp = a.events_cand or sl.resolve_events_path(a.cand, cand)
        rev = sl.load_events(rp) if rp else None
        cev = sl.load_events(cp) if cp else None
        rep = compare(ref, cand, cfg, rev, cev, a.context)
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as e:
        print("compare: %s: %s" % (type(e).__name__, e), file=sys.stderr)
        return 2
    print(json.dumps(rep, indent=1, sort_keys=True) if a.json else format_report(rep, a.verbose))
    if rep["verdict"] == "DIVERGE":
        return 1
    if rep["verdict"] == "UNVERIFIED" and a.strict_unverified:
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
