#!/usr/bin/env python3
"""Compare the C# display read-out (System12DisplayTool -> raw RGBA) with a MAME screen snapshot (PNG).

usage: compare_display.py <mame_snap.png> <our.rgba> <gpu_fN.txt from vram_dump.lua> <widthPixels> <rgb24:0|1>

MAME's psxgpu update_screen places the visible rectangle inside a width x height bitmap:
  n_left = ((horiz_disstart - overscanleft) * screenwidth) / 2560   (C integer division, NTSC overscanleft = 0x260)
  n_top  =  vert_disstart - overscantop                              (NTSC overscantop = 0x10)
  columns = ((horiz_disend - horiz_disstart) * screenwidth) / 2560, clipped to the bitmap
Everything outside is black.  We rebuild that frame from our RGBA and compare pixel by pixel.
"""
import re, struct, sys, zlib

def read_png(path):
    d = open(path, 'rb').read()
    assert d[:8] == b'\x89PNG\r\n\x1a\n'
    pos, idat, w = 8, b'', None
    while pos < len(d):
        ln, typ = struct.unpack('>I4s', d[pos:pos + 8])
        body = d[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b'IHDR':
            w, h, bd, ct, _, _, il = struct.unpack('>IIBBBBB', body)
            assert bd == 8 and il == 0 and ct in (2, 6), (bd, ct, il)
            bpp = 3 if ct == 2 else 4
        elif typ == b'IDAT':
            idat += body
    raw = zlib.decompress(idat)
    stride = w * bpp
    out = bytearray()
    prev = bytearray(stride)
    p = 0
    for _ in range(h):
        f = raw[p]; line = bytearray(raw[p + 1:p + 1 + stride]); p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1: line[i] = (line[i] + a) & 255
            elif f == 2: line[i] = (line[i] + b) & 255
            elif f == 3: line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        out += line; prev = line
    px = []
    for y in range(h):
        row = []
        for x in range(w):
            o = y * stride + x * bpp
            row.append((out[o], out[o + 1], out[o + 2]))
        px.append(row)
    return w, h, px

def main():
    png, rgba, gpu, wpx, rgb24 = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5] == '1'
    kv = dict(re.findall(r'^(\w+)=(0x[0-9a-f]+|[\d.]+)', open(gpu).read(), re.M))
    hs, he, vs, ve = (int(kv[k], 16) for k in ('n_horiz_disstart', 'n_horiz_disend', 'n_vert_disstart', 'n_vert_disend'))
    pal = (int(kv['n_gpustatus'], 16) >> 20) & 1
    ow, oh, px = read_png(png)
    ol, ot = (0x27e, 0x23) if pal else (0x260, 0x10)
    def cdiv(a, b): return int(a / b)
    n_left = cdiv((hs - ol) * ow, 2560)
    n_cols = cdiv((he - hs) * ow, 2560)
    n_top = vs - ot
    n_lines = ve - vs
    data = open(rgba, 'rb').read()
    hpx = len(data) // 4 // wpx
    x0 = max(0, -n_left); xl = max(0, n_left)
    y0 = max(0, -n_top); yt = max(0, n_top)
    cols = min(n_cols + min(0, n_left), ow - (x0 + n_left)) if True else 0
    lines = min(n_lines + min(0, n_top), oh - (y0 + n_top))
    bad = tot = 0
    first = None
    for y in range(oh):
        for x in range(ow):
            exp = (0, 0, 0)
            sx, sy = x - (x0 + n_left), y - (y0 + n_top)
            if 0 <= sx < cols and 0 <= sy < lines and (sx + x0) < wpx and (sy + y0) < hpx:
                o = ((sy + y0) * wpx + (sx + x0)) * 4
                exp = (data[o], data[o + 1], data[o + 2])
            tot += 1
            if px[y][x] != exp:
                bad += 1
                if first is None: first = (x, y, px[y][x], exp)
    print(f"png {ow}x{oh}, ours {wpx}x{hpx} rgb24={rgb24}; n_left={n_left} n_top={n_top} cols={cols} lines={lines}")
    print(f"compared {tot} pixels, mismatches {bad}" + (f", first at {first}" if first else ""))
    sys.exit(0 if bad == 0 else 1)

main()
