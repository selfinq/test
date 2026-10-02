#!/usr/bin/env python3
"""Compare ground-truth PCs from tools/mame_trace_window.lua (debugger 'trace' files) against a captured PC set.

usage: mame_pc_validate.py PCSET TRACEFILE [TRACEFILE...]
Trace lines look like 'BFC00000: lui     t0,$0013 ; 0x0013246f'. Reports, per file, the number of distinct
executed PCs in the window and how many are missing from PCSET (false negatives of the capture).
PCs are compared on the low 29 bits only (kuseg/kseg0/kseg1 aliases are the same word).
"""
import re, sys
M = 0x1fffffff
rx = re.compile(r'^([0-9A-Fa-f]{8}):')
cap = {int(l, 16) & M for l in open(sys.argv[1]) if l.strip()}
bad_total = 0
for fn in sys.argv[2:]:
    pcs = set()
    with open(fn, errors='replace') as f:
        for line in f:
            m = rx.match(line)
            if m: pcs.add(int(m.group(1), 16) & M)
    miss = sorted(pcs - cap)
    bad_total += len(miss)
    print(f'{fn}: {len(pcs)} distinct PCs in window, {len(miss)} missing from capture', 
          ' '.join(f'{x:08x}' for x in miss[:12]))
sys.exit(1 if bad_total else 0)
