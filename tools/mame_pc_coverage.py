#!/usr/bin/env python3
"""Compare a set of MAME-executed PCs against a recompiler function map.

usage: mame_pc_coverage.py PCSET FUNCS [--image generated/ttt_image.bin] [--image-end 0x803a8350] [--json OUT]
                           [--list-outside N] [--per-func OUT.tsv]

PCSET  text file, one hex PC per line (virtual or physical; only the low 29 bits are compared,
       so kseg0/kseg1/kuseg aliases of the same RAM word are the same PC). '#' comments allowed.
FUNCS  either a text file with function start addresses (hex, one per line, optional 'func_' prefix),
       or a generated main.cs / recomp directory: every 'public static void func_XXXXXXXX(' is a start.
Functions occupy [start, next_start); the last one ends at --image-end (default: end of the
0x80010000 + 0x398350 game image). A PC is "outside" if it is below the first function start,
at/above image end, or in a different segment than every function (boot ROM, low RAM, etc.).
With --image (the raw game image loaded at 0x80010000, little-endian MIPS) it also decodes every
executed `jal` and reports call targets that are not function starts in FUNCS (a direct check of
the function detector against what the real game calls).
Extents are only as good as the start list: the recompiler output carries no instruction
addresses, so [start,next_start) over-approximates a function (it may include trailing data).
"""
import argparse, json, re, sys, os, bisect, collections

MASK = 0x1fffffff
IMG_BASE, IMG_SIZE = 0x80010000, 0x398350

def read_pcs(path):
    s = set()
    with open(path) as f:
        for line in f:
            line = line.split('#')[0].strip()
            if not line:
                continue
            s.add(int(line.split()[0], 16))
    return s

def read_funcs(path):
    starts = set()
    if os.path.isdir(path):
        path = os.path.join(path, 'main.cs')
    rx = re.compile(r'public static void func_([0-9A-Fa-f]{8})\(')
    cs = path.endswith('.cs')
    with open(path, errors='replace') as f:
        for line in f:
            if cs:
                if 'public static void func_' in line:
                    m = rx.search(line)
                    if m:
                        starts.add(int(m.group(1), 16))
            else:
                line = line.split('#')[0].strip()
                if line:
                    starts.add(int(line.replace('func_', '').split()[0], 16))
    return sorted(starts)

def region(p):
    q = p & MASK
    if 0x1fc00000 <= q < 0x1fc80000: return 'boot_rom'
    if q < IMG_BASE & MASK: return 'low_ram(<0x10000)'
    if q < (IMG_BASE + IMG_SIZE) & MASK: return 'image'
    if q < 0x00800000: return 'ram_above_image'
    return 'other'

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('pcset'); ap.add_argument('funcs')
    ap.add_argument('--image-end', type=lambda x: int(x, 0), default=IMG_BASE + IMG_SIZE)
    ap.add_argument('--json'); ap.add_argument('--per-func'); ap.add_argument('--image')
    ap.add_argument('--list-outside', type=int, default=40, help='print up to N outside PCs (as ranges)')
    a = ap.parse_args()

    pcs = sorted({p & MASK for p in read_pcs(a.pcset)})
    starts = read_funcs(a.funcs)
    s_m = [s & MASK for s in starts]
    end_m = a.image_end & MASK
    hits = collections.Counter()
    outside = []
    for p in pcs:
        i = bisect.bisect_right(s_m, p) - 1
        if i < 0 or p >= end_m:
            outside.append(p); continue
        hits[i] += 1
    n_in = sum(hits.values())
    by_region = collections.Counter(region(p) for p in outside)
    # outside PCs inside the image region (the interesting ones for the detector) vs boot/low-RAM
    out_image = [p for p in outside if region(p) == 'image']
    res = {
        'pcset': a.pcset, 'funcs': a.funcs,
        'unique_pcs': len(pcs), 'n_functions': len(starts),
        'first_func': f'{starts[0]:08x}' if starts else None, 'last_func': f'{starts[-1]:08x}' if starts else None,
        'image_end': f'{a.image_end:08x}',
        'pcs_in_function': n_in, 'pcs_outside': len(outside),
        'outside_by_region': dict(by_region),
        'functions_executed': len(hits), 'functions_never_executed': len(starts) - len(hits),
    }
    print(json.dumps(res, indent=1))
    # collapse outside PCs into word-contiguous ranges
    def ranges(lst):
        out = []; 
        for p in lst:
            if out and p == out[-1][1] + 4: out[-1][1] = p
            else: out.append([p, p])
        return out
    for name, lst in (('outside, image region', out_image), ('outside, other', [p for p in outside if region(p) != 'image'])):
        r = ranges(lst)
        print(f'\n{name}: {len(lst)} PCs in {len(r)} contiguous ranges (physical addrs, first {a.list_outside}):')
        for lo, hi in r[:a.list_outside]:
            print(f'  {lo:08x}-{hi:08x} ({(hi-lo)//4+1} words)')
    if a.image:
        img = open(a.image, 'rb').read()
        sset = set(s_m)
        jal_sites = jal_ok = 0
        bad = collections.Counter(); notexec = 0
        pcset = set(pcs)
        for p in pcs:
            off = p - (IMG_BASE & MASK)
            if 0 <= off < len(img) - 3:
                w = int.from_bytes(img[off:off+4], 'little')
                if w >> 26 == 3:      # jal
                    jal_sites += 1
                    t = (((p | 0x80000000) & 0xf0000000) | ((w & 0x3ffffff) << 2)) & MASK
                    if t in sset: jal_ok += 1
                    else: bad[t] += 1
                    if t not in pcset: notexec += 1
        # executed-run starts that look like function prologues (addiu sp,sp,-N = 0x27bdXXXX, N>0) but are
        # not in the function list: candidate entries reached by jr/jalr (function pointers, jump tables)
        mid_entries = []
        for p in pcs:
            off = p - (IMG_BASE & MASK)
            if (p - 4) in pcset or not (0 <= off < len(img) - 3): continue
            w = int.from_bytes(img[off:off+4], 'little')
            if (w >> 16) == 0x27bd and (w & 0x8000) and p not in sset:
                mid_entries.append(p)
        res['executed_prologue_run_starts_not_function_starts'] = len(mid_entries)
        res['prologue_not_func_start_examples'] = [f'{x|0x80000000:08x}' for x in mid_entries[:20]]
        res['executed_jal_sites'] = jal_sites
        res['jal_targets_that_are_function_starts'] = jal_ok
        res['distinct_jal_targets_not_function_starts'] = len(bad)
        res['jal_sites_whose_target_not_executed'] = notexec
        print(json.dumps({k: res[k] for k in res if 'jal' in k or 'prologue' in k}, indent=1))
        if bad:
            print('jal targets not in function list (physical, first %d):' % a.list_outside,
                  ' '.join(f'{t|0x80000000:08x}' for t in sorted(bad)[:a.list_outside]))
    if a.json:
        res['outside_pcs'] = [f'{p:08x}' for p in outside]
        with open(a.json, 'w') as f: json.dump(res, f)
    if a.per_func:
        with open(a.per_func, 'w') as f:
            f.write('start\tnext_start\texecuted_pcs\n')
            for i, s in enumerate(starts):
                nxt = starts[i+1] if i+1 < len(starts) else a.image_end
                f.write(f'{s:08x}\t{nxt:08x}\t{hits.get(i,0)}\n')
    return 0 if not out_image else 1

if __name__ == '__main__':
    sys.exit(main())
