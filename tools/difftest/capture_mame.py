#!/usr/bin/env python3
"""Deterministic MAME reference capture for tektagt.

    python3 tools/difftest/capture_mame.py --out /path/ref.json [--frames 1500] [--every 60]
                                           [--pcs 80010000:1] [--no-events] [--keep-raw]

Steps: (1) restore_nvram.sh gives the run a fresh copy of the archived NVRAM seed and an empty cfg dir;
(2) MAME 0.264 runs headless with tools/difftest/mame_capture.lua (env DT_* parameters);
(3) the raw output (register lines, raw RAM dumps, event TSV) is converted to the snapshot format:
    RAM -> per-region and per-page SHA-256 + live-stack SHA-256, events -> JSONL sidecar + hash chains.
The result is written to --out (JSON) and <out minus .json>.events.jsonl.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import snapshotlib as sl  # noqa: E402

DEFAULT_ROMPATH = os.path.abspath(os.path.join(HERE, "..", "..", "input", "roms"))


def file_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


EV_W = re.compile(r"^([RW]) ([0-9a-f]{8}) ([0-9a-f]{8})\s*$")
EV_D = re.compile(r"^D (\d+) ([0-9a-f]{8}) ([0-9a-f]{8}) ([0-9a-f]{8}) (\d+) ([0-9a-f]{16})\s*$")
EV_C = re.compile(r"^C (\d+)\s*$")


def parse_debug_log(path, event_max=None):
    """debug.log (MAME debug console) -> (events, marks, truncated); marks[seq] = number of events before checkpoint seq.
    Lines that are neither events nor markers (banner, 'Stopped at ...') are ignored."""
    events, marks, truncated = [], {}, False
    with open(path, errors="replace") as f:
        for ln in f:
            m = EV_W.match(ln)
            if m:
                if event_max is not None and len(events) >= event_max:
                    truncated = True
                    continue
                events.append({"k": m.group(1), "a": "0x" + m.group(2), "d": "0x" + m.group(3)})
                continue
            m = EV_D.match(ln)
            if m:
                if event_max is not None and len(events) >= event_max:
                    truncated = True
                    continue
                events.append({"k": "D", "ch": int(m.group(1)), "chcr": "0x" + m.group(2), "madr": "0x" + m.group(3),
                               "bcr": "0x" + m.group(4), "n": int(m.group(5)), "h": "0x" + m.group(6)})
                continue
            m = EV_C.match(ln)
            if m:
                marks[int(m.group(1))] = len(events)
    return events, marks, truncated


def finalize(raw_dir, out_path, regions_cfg, capture_info, producer_extra=None, keep_raw=False, meta_extra=None,
             event_max=None):
    """Convert a raw capture directory into the snapshot JSON (+ events sidecar). Pure function of the raw
    directory contents, so it is unit-testable without MAME."""
    manifest = json.load(open(os.path.join(raw_dir, "manifest.json")))
    base = sl.parse_int(regions_cfg["ram_base"])
    ev_path = out_path[:-5] + ".events.jsonl" if out_path.endswith(".json") else out_path + ".events.jsonl"
    log = os.path.join(raw_dir, "debug.log")
    have_events = bool(manifest.get("events_enabled")) and os.path.exists(log)
    events, marks, truncated = [], {}, False
    if have_events:
        events, marks, truncated = parse_debug_log(log, event_max)
        with open(ev_path, "w") as out:
            for ev in events:
                out.write(json.dumps(ev, separators=(",", ":")) + "\n")
    chains = sl.EventChains()
    pos = 0
    cps = []
    for ln in open(os.path.join(raw_dir, "checkpoints.jsonl")):
        raw = json.loads(ln)
        ram = open(os.path.join(raw_dir, raw["ram_file"]), "rb").read()
        if len(ram) != regions_cfg["ram_size"]:
            raise ValueError("ram dump size %d != %d" % (len(ram), regions_cfg["ram_size"]))
        sp = sl.parse_int(raw["gpr"][sl.GPR_NAMES.index("sp")])
        cp = {
            "id": raw["id"],
            "sync": raw["sync"],
            "cpu": {"pc": raw["pc"], "gpr": raw["gpr"], "hi": raw["hi"], "lo": raw["lo"],
                    "cop0": raw["cop0"], "cop2_d": raw["cop2_d"], "cop2_c": raw["cop2_c"]},
            "ram": sl.hash_ram(ram, regions_cfg, sp),
            "info": {"frame": raw["frame"]},
        }
        if have_events:
            if raw["seq"] not in marks:
                raise ValueError("checkpoint %s (seq %d) has no marker in debug.log" % (raw["id"], raw["seq"]))
            n = marks[raw["seq"]]
            while pos < n:
                chains.add(events[pos])
                pos += 1
            cp["events"] = chains.state()
        cps.append(cp)
        if not keep_raw:
            os.unlink(os.path.join(raw_dir, raw["ram_file"]))
    snap = {
        "schema": sl.SCHEMA,
        "game": "tektagt",
        "producer": dict({"name": "mame", "version": manifest["mame_version"], "script": "tools/difftest/mame_capture.lua",
                          "debugger": manifest["debugger"]}, **(producer_extra or {})),
        "capture": dict(capture_info, ram_base=sl.hx(base), ram_size=regions_cfg["ram_size"],
                        page_size=regions_cfg["page_size"], stack_top=regions_cfg["stack_top"],
                        events_enabled=bool(have_events)),
        "regions": [{"name": r["name"], "base": r["base"], "size": r["size"]} for r in regions_cfg["regions"]],
        "checkpoints": cps,
    }
    if have_events:
        while pos < len(events):     # drain: total chain covers the whole log
            chains.add(events[pos])
            pos += 1
        snap["events"] = dict(chains.state(), file=os.path.basename(ev_path), truncated=truncated)
    snap["meta"] = dict(meta_extra or {})
    snap["snapshot_sha256"] = sl.snapshot_hash(snap)
    with open(out_path, "w") as f:
        json.dump(snap, f, indent=1, sort_keys=False)
        f.write("\n")
    return snap


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, help="snapshot JSON to write")
    ap.add_argument("--frames", type=int, default=1500, help="last frame (final checkpoint)")
    ap.add_argument("--every", type=int, default=60, help="periodic frame checkpoint interval (0=off)")
    ap.add_argument("--pcs", default="80010000:1",
                    help="instruction-exact checkpoints 'addr:nth[,addr:nth]' (needs -debug -debugger none); '' = none")
    ap.add_argument("--reads", action="store_true", help="also watch reads of EEPROM/protection/I_MASK (MAME ~10x slower)")
    ap.add_argument("--no-events", action="store_true", help="do not record I/O + DMA events")
    ap.add_argument("--event-max", type=int, default=5000000, help="keep at most this many events (rest dropped, flagged truncated)")
    ap.add_argument("--workdir", help="work directory (default: temp dir, removed unless --keep-raw)")
    ap.add_argument("--keep-raw", action="store_true", help="keep raw RAM dumps and work dir")
    ap.add_argument("--mame", default="/usr/games/mame")
    ap.add_argument("--rompath", default=DEFAULT_ROMPATH)
    ap.add_argument("--seed-dir", default=os.path.join(HERE, "nvram_seed"))
    ap.add_argument("--regions", default=os.path.join(HERE, "regions_tektagt.json"))
    ap.add_argument("--timeout", type=int, default=900)
    a = ap.parse_args(argv)

    regions_cfg = sl.load_regions_config(a.regions)
    work = os.path.abspath(a.workdir) if a.workdir else tempfile.mkdtemp(prefix="difftest_")
    os.makedirs(work, exist_ok=True)
    r = subprocess.run([os.path.join(HERE, "restore_nvram.sh"), work, a.seed_dir], capture_output=True, text=True)
    if r.returncode:
        sys.exit("restore_nvram failed: " + r.stderr)
    raw = os.path.join(work, "raw")
    shutil.rmtree(raw, ignore_errors=True)
    os.makedirs(raw)
    use_dbg = bool(a.pcs) or not a.no_events
    env = dict(os.environ, SDL_VIDEODRIVER="dummy", SDL_AUDIODRIVER="dummy", DT_OUT=raw, DT_EVERY=str(a.every),
               DT_MAX_FRAME=str(a.frames), DT_PCS=a.pcs, DT_EVENTS="0" if a.no_events else "1", DT_READS="1" if a.reads else "0",
               DT_RAM_BASE=regions_cfg["ram_base"], DT_RAM_SIZE=str(regions_cfg["ram_size"]))
    cmd = [a.mame, "tektagt", "-rompath", a.rompath, "-video", "none", "-sound", "none", "-nothrottle",
           "-seconds_to_run", str(a.frames // 30 + 60), "-autoboot_script", os.path.join(HERE, "mame_capture.lua"),
           "-nvram_directory", os.path.join(work, "nvram"), "-cfg_directory", os.path.join(work, "cfg"),
           "-inipath", os.path.join(work, "ini"), "-homepath", os.path.join(work, "home"),
           "-snapshot_directory", os.path.join(work, "snap"), "-state_directory", os.path.join(work, "sta"),
           "-diff_directory", os.path.join(work, "diff"), "-comment_directory", os.path.join(work, "cmt"),
           "-input_directory", os.path.join(work, "inp")]
    if use_dbg:
        cmd += ["-debug", "-debugger", "none", "-debuglog"]
    os.makedirs(os.path.join(work, "home"), exist_ok=True)
    p = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=a.timeout, cwd=raw)   # debug.log lands in cwd
    open(os.path.join(work, "mame.stderr.txt"), "w").write(p.stderr)
    open(os.path.join(work, "mame.stdout.txt"), "w").write(p.stdout)
    if not os.path.exists(os.path.join(raw, "done.txt")):
        sys.exit("MAME run did not complete (rc=%s); see %s/mame.stderr.txt" % (p.returncode, work))
    manifest = json.load(open(os.path.join(raw, "manifest.json")))
    if use_dbg and not manifest["debugger"]:
        sys.exit("pc checkpoints/events requested but the MAME debugger interface is unavailable")
    nv = os.path.join(work, "nvram", "tektagt", "at28c16")
    seed_sha = open(os.path.join(a.seed_dir, "SEED.sha256")).read().split()[0]
    capture_info = {"frames": a.frames, "every": a.every, "pcs": [s for s in a.pcs.split(",") if s],
                    "nvram_seed_sha256": seed_sha, "event_max": a.event_max, "reads_watched": bool(a.reads)}
    meta = {"nvram_after_run_sha256": file_sha256(nv) if os.path.exists(nv) else None,
            "mame_binary": a.mame, "mame_binary_sha256": file_sha256(a.mame), "mame_rc": p.returncode}
    snap = finalize(raw, a.out, regions_cfg, capture_info, meta_extra=meta, keep_raw=a.keep_raw, event_max=a.event_max)
    print("wrote %s  snapshot_sha256=%s  checkpoints=%d  events=%s" % (
        a.out, snap["snapshot_sha256"], len(snap["checkpoints"]), snap.get("events", {}).get("w_count", "-")))
    if not a.workdir and not a.keep_raw:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
