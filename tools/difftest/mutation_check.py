#!/usr/bin/env python3
"""Mutation check of the comparer's unit tests: breaks compare.py in 10 targeted ways (in a temporary copy) and
verifies that tests/difftest fails for every mutant. Exit 0 = all mutants killed.
    python3 tools/difftest/mutation_check.py
"""
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MUTANTS = [
    ("register difference never reported", 'if (rv & mask) != (cv & mask):', 'if False:'),
    ("live stack not compared", 'cfg["ram"]["stack_live"] == "exact"', 'cfg["ram"]["stack_live"] == "never"'),
    ("partially covered page hidden by ignore range", 'if any(x <= p0 and p1 <= y for x, y, _ in region_ign):',
     'if any(_range_overlap(x, y, p0, p1) for x, y, _ in region_ign):'),
    ("event divergence always headline", 'if first is None or pos is None or ev_div["ref_index"] < pos:', 'if True:'),
    ("static_pages filter not applied", 'if stat is not None and i not in stat:', 'if False:'),
    ("cpu bit masks ignored", 'mask = sl.parse_int(cfg["cpu"]["masks"].get(path, 0xffffffff))', 'mask = 0xffffffff'),
    ("event chain mismatch reported as match", 'if a == b:\n            items.append({"path": "events." + k, "status": "match"})',
     'if True:\n            items.append({"path": "events." + k, "status": "match"})'),
    ("missing required checkpoint tolerated", 'st = "diverge" if cid in req else "unverified"', 'st = "unverified"'),
    ("ignore range accepted without reason", 'if not r.get("reason"):', 'if False:'),
    ("shorter event stream always accepted", 'if cfg["events"]["length"] == "prefix_ok":', 'if True:'),
]


def main():
    survived = []
    for name, old, new in MUTANTS:
        with tempfile.TemporaryDirectory() as d:
            for sub in ("tools/difftest", "tests/difftest"):
                shutil.copytree(os.path.join(ROOT, sub), os.path.join(d, sub), ignore=shutil.ignore_patterns("__pycache__"))
            p = os.path.join(d, "tools/difftest/compare.py")
            with open(p) as f:
                src = f.read()
            if old not in src:
                print("mutation target not found (compare.py changed?): %s" % name)
                return 2
            with open(p, "w") as f:
                f.write(src.replace(old, new, 1))
            r = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", "tests/difftest"], cwd=d, capture_output=True, text=True)
            ok = r.returncode != 0
            print("%-48s %s" % (name, "killed" if ok else "SURVIVED"))
            if not ok:
                survived.append(name)
    print("%d/%d mutants killed" % (len(MUTANTS) - len(survived), len(MUTANTS)))
    return 1 if survived else 0


if __name__ == "__main__":
    sys.exit(main())
