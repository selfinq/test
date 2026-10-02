#!/usr/bin/env python3
"""Run the MAME reference capture N times from the archived NVRAM seed and prove (or refute) determinism.

    python3 tools/difftest/determinism_check.py --outdir DIR [--runs 2] [--frames 1500] [--parallel] [capture options...]

For every run: snapshot_sha256, the hash of every checkpoint and of the events sidecar. Exit 0 only if all runs are
identical in every one of these; otherwise prints the first divergence found by compare.py (strict config:
all registers, all RAM regions incl. dead stack, all events) so the cause can be located.
Extra arguments are passed to capture_mame.py.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import snapshotlib as sl  # noqa: E402


def sha_file(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--runs", type=int, default=2)
    ap.add_argument("--frames", type=int, default=1500)
    ap.add_argument("--parallel", action="store_true", help="run the captures concurrently (also stresses host timing)")
    a, extra = ap.parse_known_args(argv)
    os.makedirs(a.outdir, exist_ok=True)
    procs = []
    outs = []
    for i in range(a.runs):
        out = os.path.join(a.outdir, "run%d.json" % (i + 1))
        outs.append(out)
        cmd = [sys.executable, os.path.join(HERE, "capture_mame.py"), "--out", out, "--frames", str(a.frames),
               "--workdir", os.path.join(a.outdir, "work%d" % (i + 1))] + extra
        log = open(os.path.join(a.outdir, "run%d.log" % (i + 1)), "w")
        p = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)
        if a.parallel:
            procs.append((p, log))
        else:
            p.wait()
            log.close()
            if p.returncode:
                sys.exit("capture %d failed, see %s" % (i + 1, log.name))
    for p, log in procs:
        p.wait()
        log.close()
        if p.returncode:
            sys.exit("a capture failed, see %s" % log.name)
    snaps = [sl.load_snapshot(o) for o in outs]
    rows = []
    for o, s in zip(outs, snaps):
        ev = sl.resolve_events_path(o, s)
        rows.append({"file": o, "snapshot_sha256": s["snapshot_sha256"],
                     "recomputed_sha256": sl.snapshot_hash(s),
                     "checkpoint_hashes": [(c["id"], sl.checkpoint_hash(c)[:16]) for c in s["checkpoints"]],
                     "events_sha256": sha_file(ev) if ev else None,
                     "nvram_after_run_sha256": s.get("meta", {}).get("nvram_after_run_sha256")})
    ident = all(r["snapshot_sha256"] == rows[0]["snapshot_sha256"] and r["checkpoint_hashes"] == rows[0]["checkpoint_hashes"]
                and r["events_sha256"] == rows[0]["events_sha256"] for r in rows)
    rep = {"runs": len(rows), "frames": a.frames, "parallel": a.parallel, "identical": ident, "rows": rows}
    with open(os.path.join(a.outdir, "determinism.json"), "w") as f:
        json.dump(rep, f, indent=1)
    for r in rows:
        print("%s  snapshot_sha256=%s  events_sha256=%s  nvram_after=%s" % (
            os.path.basename(r["file"]), r["snapshot_sha256"], (r["events_sha256"] or "-")[:16],
            (r["nvram_after_run_sha256"] or "-")[:16]))
    print("checkpoints per run: %d, identical: %s" % (len(rows[0]["checkpoint_hashes"]), ident))
    if not ident:
        strict = os.path.join(a.outdir, "strict_config.json")
        with open(strict, "w") as f:
            json.dump({"profiles": {"frame": {"cpu": "exact", "ram": "exact", "events": "chain"}},
                       "ram": {"regions": {"stack": "exact"}}}, f)
        for i in range(1, len(outs)):
            print("--- compare run1 vs run%d (strict) ---" % (i + 1))
            subprocess.run([sys.executable, os.path.join(HERE, "compare.py"), outs[0], outs[i], "--config", strict])
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
