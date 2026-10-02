#!/usr/bin/env python3
"""Collect unique executed H8 instructions from a MAME debugger trace stream.
Usage: uniq_trace.py <fifo_or_file> <out_tsv>
Input lines look like '000100: orc     #h'80, ccr' (MAME 'trace file,:sub,noloop').
Output TSV: pc<TAB>count<TAB>disassembly (sorted by pc)."""
import sys, re
rx = re.compile(r'^([0-9A-Fa-f]{6}): (.*)$')
seen = {}
with open(sys.argv[1], 'r', errors='replace') as f:
    for line in f:
        m = rx.match(line.rstrip('\n'))
        if not m: continue
        pc = int(m.group(1), 16)
        e = seen.get(pc)
        if e: e[0] += 1
        else: seen[pc] = [1, m.group(2)]
with open(sys.argv[2], 'w') as o:
    for pc in sorted(seen):
        o.write(f"{pc:06x}\t{seen[pc][0]}\t{seen[pc][1]}\n")
print(len(seen), "unique pcs")
