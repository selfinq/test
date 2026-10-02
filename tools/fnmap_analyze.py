#!/usr/bin/env python3
"""Analyze a function map TSV (start end ninstr name) written by RECOMPONE_FNMAP_OUT against the raw image.
usage: fnmap_analyze.py fnmap.tsv ttt_image.bin [main.cs]"""
import sys, struct, statistics
rows=[l.rstrip('\n').split('\t') for l in open(sys.argv[1])]
fn=[(int(a,16),int(b,16),int(c),n) for a,b,c,n in rows]
img=open(sys.argv[2],'rb').read(); base=0x80010000
tot=sum(c for _,_,c,_ in fn)
sz=sorted(c for _,_,c,_ in fn)
print("functions",len(fn),"instrs",tot,"bytes",tot*4,"of image",len(img),"(%.1f%%)"%(100*tot*4/len(img)))
print("instr/func: median",statistics.median(sz),"p90",sz[int(len(sz)*.9)],"p99",sz[int(len(sz)*.99)],"max",sz[-1])
print("largest:",[("%08X"%a,c) for a,b,c,n in sorted(fn,key=lambda x:-x[2])[:8]])
ov=0;prev=0
for a,b,c,n in sorted(fn):
    if a<prev: ov+=1
    prev=max(prev,b)
print("overlapping/nested funcs:",ov)
# gaps
fs=sorted(fn); gaps=[]
for (a,b,_,_),(a2,_,_,_) in zip(fs,fs[1:]):
    if a2>b: gaps.append((b,a2-b))
print("gaps",len(gaps),"total gap bytes",sum(g for _,g in gaps))
# histogram by 64KB region of covered bytes
import collections
h=collections.Counter()
for a,b,c,n in fn: h[(a-base)>>16]+=c*4
print("covered bytes per 64KB block:",' '.join("%X:%d"%(k,h[k]//1024) for k in sorted(h)),"(KB)")
