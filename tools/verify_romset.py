#!/usr/bin/env python3
"""Verify a ROM set zip against a manifest by content (size, CRC32, SHA-1).

Never extracts to disk and never executes anything. Member names are only used
as labels; content decides identity. Exit codes: 0 complete, 1 incomplete or
mismatched, 2 usage/unsafe archive.
"""
import argparse, hashlib, json, sys, zipfile, zlib

MAX_MEMBERS = 256
CHUNK = 1 << 20


def hash_member(zf, info, limit):
    sha, crc, n = hashlib.sha1(), 0, 0
    with zf.open(info) as f:
        while True:
            b = f.read(CHUNK)
            if not b:
                break
            n += len(b)
            if n > limit:
                raise ValueError(f"{info.filename}: exceeds size limit {limit}")
            sha.update(b)
            crc = zlib.crc32(b, crc)
    return n, f"{crc & 0xFFFFFFFF:08x}", sha.hexdigest()


def verify(zip_path, manifest):
    members = manifest["members"]
    max_size = max(m["size"] for m in members)
    report = {"ok": [], "wrong_name": [], "bad_content": [], "missing": [], "unknown": []}
    by_sha = {m["sha1"]: m for m in members}
    seen = set()
    with zipfile.ZipFile(zip_path) as zf:
        infos = [i for i in zf.infolist() if not i.is_dir()]
        if len(infos) > MAX_MEMBERS:
            raise ValueError("too many members")
        for i in infos:
            n = i.filename
            if n.startswith(("/", "\\")) or ".." in n.replace("\\", "/").split("/") or ":" in n:
                raise ValueError(f"unsafe member path: {n!r}")
            if i.file_size > max_size:
                report["unknown"].append({"name": n, "reason": f"larger than any expected chip ({i.file_size})"})
                continue
            size, crc, sha = hash_member(zf, i, max_size)
            exp = by_sha.get(sha)
            named = next((m for m in members if m["name"] == n), None)
            if exp and exp["name"] == n and size == exp["size"] and crc == exp["crc32"]:
                report["ok"].append(n); seen.add(n)
            elif exp:
                report["wrong_name"].append({"found": n, "is": exp["name"]}); seen.add(exp["name"])
            elif named:
                report["bad_content"].append({"name": n, "size": size, "crc32": crc, "sha1": sha,
                                              "expected_sha1": named["sha1"]})
                seen.add(n)
            else:
                report["unknown"].append({"name": n, "size": size, "sha1": sha})
    report["missing"] = [m["name"] for m in members if m["name"] not in seen]
    report["complete"] = (len(report["ok"]) == len(members) and not report["bad_content"]
                          and not report["wrong_name"])
    return report


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("zip"); p.add_argument("--manifest", default="tools/tektagt_manifest.json")
    p.add_argument("--json")
    a = p.parse_args(argv)
    try:
        manifest = json.load(open(a.manifest))
        rep = verify(a.zip, manifest)
    except (OSError, zipfile.BadZipFile, ValueError) as e:
        print(f"error: {e}", file=sys.stderr); return 2
    print(json.dumps(rep, indent=2))
    if a.json:
        json.dump(rep, open(a.json, "w"), indent=2)
    return 0 if rep["complete"] else 1


if __name__ == "__main__":
    sys.exit(main())
