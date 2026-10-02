#!/usr/bin/env python3
"""Run tools/mame_pc_capture.sh over all candidate-PC chunks (each from the same NVRAM seed) and merge.

usage: mame_pc_capture_all.py NAME FRAMES [--jobs 2] [--input tools/mame_input_coinstart.lua] [--chunk-words 65536]

Why chunks: every candidate word needs a MAME breakpoint and MAME's debugger scans its breakpoint list
linearly when a breakpoint is disabled, so ~1.2M simultaneous breakpoints are far too slow; 64k-word chunks
are fine. Because the emulation from the fixed NVRAM seed is deterministic (verified, see report), the union
of the chunk runs equals one run observing all candidates.
Output: reports/trace_a02/out/NAME.pcs (merged, virtual PCs, sorted, 8 hex digits per line) + NAME.sha256.
"""
import argparse, os, subprocess, sys, hashlib, concurrent.futures as cf

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
OUT = os.path.join(ROOT, 'reports', 'trace_a02', 'out')

def chunks(words):
    spec = [('low', ['80000000-80010000', '00000000-00010000']),
            ('rom_b', ['bfc00000-bfc30000']), ('rom_1', ['1fc00000-1fc30000'])]
    lo, hi = 0x80010000, 0x80400000
    i = 0
    while lo < hi:
        e = min(lo + words * 4, hi)
        spec.append((f'ram{i:02d}', [f'{lo:08x}-{e:08x}']))
        lo = e; i += 1
    # split oversized fixed chunks
    res = []
    for name, rs in spec:
        cur, n = [], 0
        for r in rs:
            a, b = (int(x, 16) for x in r.split('-'))
            while a < b:
                e = min(a + (words - n) * 4, b)
                cur.append(f'{a:08x}-{e:08x}'); n += (e - a) // 4; a = e
                if n >= words:
                    res.append((f'{name}_{len(res)}', cur)); cur, n = [], 0
        if cur: res.append((f'{name}_{len(res)}', cur))
    return res

def run(name, base, ranges, frames, inp):
    out = os.path.join(OUT, f'{base}.{name}.pcs')
    if os.path.exists(out) and os.path.exists(out[:-4] + '.sha256'):
        return name, 'cached'
    cmd = [os.path.join(ROOT, 'tools', 'mame_pc_capture.sh'), f'{base}.{name}', str(frames), ','.join(ranges)]
    if inp: cmd.append(inp)
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return name, 'ok' if r.returncode == 0 and os.path.exists(out) else 'FAILED rc=%s %s' % (r.returncode, r.stdout[-300:])

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('name'); ap.add_argument('frames', type=int)
    ap.add_argument('--jobs', type=int, default=2); ap.add_argument('--input')
    ap.add_argument('--chunk-words', type=int, default=65536)
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    cl = chunks(a.chunk_words)
    print(f'{len(cl)} chunks', flush=True)
    with cf.ThreadPoolExecutor(a.jobs) as ex:
        futs = [ex.submit(run, n, a.name, r, a.frames, a.input) for n, r in cl]
        bad = 0
        for f in cf.as_completed(futs):
            n, st = f.result(); print(n, st, flush=True); bad += st not in ('ok', 'cached')
    if bad: sys.exit('some chunks failed; rerun to resume (finished chunks are cached)')
    pcs = set()
    for n, _ in cl:
        with open(os.path.join(OUT, f'{a.name}.{n}.pcs')) as f:
            pcs.update(int(l, 16) for l in f if l.strip())
    p = os.path.join(OUT, f'{a.name}.pcs')
    with open(p, 'w') as f:
        for x in sorted(pcs): f.write(f'{x:08x}\n')
    h = hashlib.sha256(open(p, 'rb').read()).hexdigest()
    open(p[:-4] + '.sha256', 'w').write(f'{h}  {os.path.basename(p)}\n')
    print(len(pcs), 'unique PCs ->', p, h)

if __name__ == '__main__':
    main()
