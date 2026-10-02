#!/usr/bin/env python3
"""Coverage heuristics of a function map vs the raw image. usage: fnmap_cover.py fnmap.tsv ttt_image.bin"""
import struct,sys,collections
d=open(sys.argv[2],'rb').read(); base=0x80010000
n=len(d)//4; w=struct.unpack('<%dI'%n,d[:n*4])
fn=sorted((int(r[0],16),int(r[1],16)) for r in (l.split('\t') for l in open(sys.argv[1])))
cov=bytearray(n)
for a,b in fn:
    for k in range((a-base)//4,min((b-base)//4,n)): cov[k]=1
jr=[i for i,x in enumerate(w) if x==0x03e00008]
unc=[i for i in jr if not cov[i]]
print("covered words",sum(cov),"of",n)
print("jr ra total",len(jr),"uncovered",len(unc),sorted(collections.Counter(i>>14 for i in unc).items()))
pro=[i for i,x in enumerate(w) if (x&0xFFFF8000)==0x27bd8000]
up=[i for i in pro if not cov[i]]
print("addiu sp,-n total",len(pro),"uncovered",len(up),[hex(base+4*i) for i in up[:12]])
print("uncovered jr ra:",[hex(base+4*i) for i in unc[:40]])
