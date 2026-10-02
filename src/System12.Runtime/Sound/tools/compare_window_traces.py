#!/usr/bin/env python3
"""Compare an H8 instruction window written by SoundTool (`full` mode with WIN0/WIN1) with a MAME window trace
(tools/mame_window_trace.lua), both aligned at the first vblank-handler entry (0x0040F2).
Reports the first PC divergence and every change of the relative cycle offset.
usage: compare_window_traces.py mine/window_mine.txt mame/window_trace.txt
Rows of the C# trace that were logged before an interrupt entry (instruction not executed) are dropped.
The handler addresses below are specific to tektagt's sub.bin."""
import re,sys
HANDLERS={'0040F2','003FA6','004040','0036CE','0035D2','00371C','003736'}
def load(fn,mine):
    rows=[]
    for l in open(fn):
        m=re.match(r'R=(\S+) C=(\S+) T=\s*(\S*) ?([0-9A-F]{6}): (.*)$', l.rstrip('\n'))
        if m:
            t=m.group(3)
            tv=round(float(t)*16934400) if mine else int(t)
            rows.append((m.group(4), m.group(1).split(','), m.group(2), tv, m.group(5)))
    return rows
a=load(sys.argv[1],True); b=load(sys.argv[2],False)
keep=[]
for i,r in enumerate(a):
    if i+1<len(a) and a[i+1][0] in HANDLERS and a[i+1][3]-r[3]==14 and r[0] not in HANDLERS: continue
    keep.append(r)
a=keep
# align: first row of MAME that is a handler entry 0040F2 (vblank) -> same in mine
ia=[i for i,r in enumerate(a) if r[0]=='0040F2'][0]; ib=[i for i,r in enumerate(b) if r[0]=='0040F2'][0]
n=0; last=None
while ia+n<len(a) and ib+n<len(b):
    ra=a[ia+n]; rb=b[ib+n]
    if ra[0]!=rb[0]:
        print("PC diverge at +%d mine %s T %d mame %s T %d"%(n,ra[0],ra[3],rb[0],rb[3]))
        for k in range(max(0,n-6),n+5):
            print("  mine",a[ia+k][0],a[ia+k][3]-a[ia][3],a[ia+k][4],"|| mame",b[ib+k][0],b[ib+k][3]-b[ib][3],b[ib+k][4])
        break
    d=(ra[3]-a[ia][3])-(rb[3]-b[ib][3])
    if d!=last:
        print("rel-offset change at +%d pc=%s %s : %s -> %d (prev: %s)"%(n,ra[0],ra[4],last,d,a[ia+n-1][4] if n else '')); last=d
        if last is not None and abs(last)>200: break
    n+=1
else: print("no divergence in",n)
