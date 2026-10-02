#!/usr/bin/env python3
"""Independent reimplementation of the System 12 boot-ROM LZ decompressor.

Derived by reading the routine at 0x1fc21048 of the verified main ROM (helpers at
0x1fc20fac/0x1fc21014 only emulate byte access with 16-bit bus cycles). Format:
  control byte: 0 = end; bits consumed LSB first until only the sentinel bit remains
  (value < 2 => fetch a new control byte);
  bit=1: copy one literal byte; bit=0: 16-bit big-endian token,
         offset = tok & 0x7ff (0 => 0x800), length = (tok >> 11) & 0x1f (0 => 0x20),
         copy `length` bytes from dst-offset (byte by byte, so overlap repeats).
"""
import sys


def decompress(src: bytes, pos: int = 0, max_out: int = 1 << 24):
    out = bytearray()
    while True:
        flags = src[pos]; pos += 1
        if flags == 0:
            break
        while flags >= 2:
            if flags & 1:
                out.append(src[pos]); pos += 1
            else:
                tok = (src[pos] << 8) | src[pos + 1]; pos += 2
                off = tok & 0x7FF or 0x800
                n = (tok >> 11) & 0x1F or 0x20
                if off > len(out):
                    raise ValueError(f"back-reference before start (off={off}, have={len(out)})")
                for _ in range(n):
                    out.append(out[-off])
            flags >>= 1
            if len(out) > max_out:
                raise ValueError("output limit exceeded")
    return bytes(out), pos


if __name__ == "__main__":
    rom = open(sys.argv[1], "rb").read()
    start = int(sys.argv[2], 0)
    data, end = decompress(rom, start)
    open(sys.argv[3], "wb").write(data)
    print(f"decompressed {len(data):#x} bytes, consumed {end - start:#x} source bytes")
