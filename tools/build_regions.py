#!/usr/bin/env python3
"""Build memory regions from a verified ROM zip, following the driver's load rules.

Implements the subset of MAME load flags used by the manifest (LOAD, LOAD16_BYTE,
LOAD16_WORD_SWAP, LOAD32_BYTE, LOAD32_WORD). Refuses to run unless the zip is complete
by content. Bytes inside a LOAD32_WORD word keep file order (little-endian regions).
UNVERIFIED against real data until the differential check with MAME (milestone H1/H2).
"""
import argparse, os, re, sys, zipfile
sys.path.insert(0, os.path.dirname(__file__))
import verify_romset as v


def lanes(load, data):
    """Return [(slice, bytes)] describing where `data` lands for this load rule."""
    kind, off = re.fullmatch(r"(\w+) @0x([0-9a-fA-F]+)", load).groups()
    off, n = int(off, 16), len(data)
    if kind == "LOAD":
        return [(slice(off, off + n), data)]
    if kind == "LOAD16_BYTE":
        return [(slice(off, off + 2 * n, 2), data)]
    if kind == "LOAD32_BYTE":
        return [(slice(off, off + 4 * n, 4), data)]
    if n % 2:
        raise ValueError(f"odd length for {kind}")
    if kind == "LOAD16_WORD_SWAP":
        sw = bytearray(n); sw[0::2], sw[1::2] = data[1::2], data[0::2]
        return [(slice(off, off + n), bytes(sw))]
    if kind == "LOAD32_WORD":
        return [(slice(off, off + 2 * n, 4), data[0::2]), (slice(off + 1, off + 1 + 2 * n, 4), data[1::2])]
    raise ValueError(f"unsupported load kind {kind}")


def build(zip_path, manifest):
    rep = v.verify(zip_path, manifest)
    if not rep["complete"]:
        raise ValueError(f"ROM set not complete/verified: missing={rep['missing']} "
                         f"bad={rep['bad_content']} renamed={rep['wrong_name']}")
    regions = {r: bytearray(s) for r, s in manifest["regions"].items()}
    cov = {r: bytearray(s) for r, s in manifest["regions"].items()}
    with zipfile.ZipFile(zip_path) as zf:
        for m in manifest["members"]:
            reg, c = regions[m["region"]], cov[m["region"]]
            for sl, chunk in lanes(m["load"], zf.read(m["name"])):
                if sl.start + (len(chunk) - 1) * (sl.step or 1) >= len(reg):
                    raise ValueError(f"{m['name']} overruns region {m['region']}")
                if any(c[sl]):
                    raise ValueError(f"overlapping load: {m['name']} in {m['region']}")
                reg[sl] = chunk
                c[sl] = b"\x01" * len(chunk)
    return {r: bytes(b) for r, b in regions.items()}, {r: sum(1 for _ in filter(None, c)) for r, c in cov.items()}


def main(argv=None):
    import json
    p = argparse.ArgumentParser()
    p.add_argument("zip"); p.add_argument("--manifest", default="tools/tektagt_manifest.json")
    p.add_argument("--out", default="generated/regions")
    a = p.parse_args(argv)
    try:
        regions, cover = build(a.zip, json.load(open(a.manifest)))
    except (OSError, zipfile.BadZipFile, ValueError) as e:
        print(f"error: {e}", file=sys.stderr); return 2
    os.makedirs(a.out, exist_ok=True)
    for r, b in regions.items():
        open(os.path.join(a.out, r.replace(":", "_") + ".bin"), "wb").write(b)
        print(f"{r}: {len(b):#x} bytes, {cover[r]:#x} covered by loads")
    return 0


if __name__ == "__main__":
    sys.exit(main())
