// SPDX-License-Identifier: BSD-3-Clause
//
// Hitachi H8/300H (H8/3002, advanced mode) instruction decoder and disassembler.
//
// Written from the H8/300H programming manual's encoding layout, and VERIFIED against MAME's disassembler
// output (MAME h8d.cpp / h8.lst, license BSD-3-Clause, copyright-holders Olivier Galibert) for every
// instruction address executed by the tektagt sound program (see docs/system12/SOUND.md "Verification").
// Disassembly text deliberately mimics MAME's syntax (e.g. "mov.b   r0l, @h'fffff2") so it can be diffed
// against MAME debugger traces. Only the H8/300H instruction set is decoded; H8S-only encodings
// (EXR, 2-bit shifts, ldm/stm, mac, tas, bit ops on @aa:16 ...) are reported as Invalid.

using System;
using System.Text;

namespace System12.Runtime.Sound;

public enum H8Op : byte
{
    Invalid, Nop, Mov, Add, Adds, Addx, Sub, Subs, Subx, Cmp, Inc, Dec, Daa, Das, Neg, Not, Extu, Exts,
    And, Or, Xor, Andc, Orc, Xorc, Ldc, Stc,
    Shll, Shal, Shlr, Shar, Rotl, Rotr, Rotxl, Rotxr,
    Bset, Bclr, Bnot, Btst, Bst, Bist, Bld, Bild, Band, Biand, Bor, Bior, Bxor, Bixor,
    Mulxu, Mulxs, Divxu, Divxs, Bcc, Jmp, Jsr, Bsr, Rts, Rte, Trapa, Sleep, Eepmov,
}

public enum H8Mode : byte
{
    None,
    Reg,      // register direct; width from the instruction size (8: r0h..r7l, 16: r0..e7, 32: er0..er7)
    Imm,      // immediate (Val)
    Const,    // implicit constant 1/2/4 for adds/subs/inc/dec (Val)
    Ind,      // @ERn
    PostInc,  // @ERn+
    PreDec,   // @-ERn
    Disp,     // @(d,ERn)
    Abs,      // @aa (Val = effective 24-bit address)
    Mem,      // @@aa:8 (Val = table address 0..255)
    Ccr,
    Target,   // branch target absolute (Val)
}

public struct H8Operand
{
    public H8Mode Mode;
    public byte Reg;
    public byte Aux;   // Disp: 16 = 16-bit displacement (Val sign-extended), 32 = 32-bit field (24-bit used); Abs: 16 / 32 bits as encoded
    public int Val;
}

public struct H8Insn
{
    public H8Op Op;
    public byte Len;    // length in bytes
    public byte Size;   // operand size in bytes: 1, 2, 4 (0 = n/a)
    public byte Cond;   // Bcc condition 0..15
    public H8Operand Src, Dst;
    public bool Hi;     // bit instructions: "inverted" variants (bior, bist, ...) ; eepmov: word
}

public static class H8Decoder
{
    /// <summary>Decode one instruction from b[0..]; b must hold at least 10 bytes (zero padded past ROM end).</summary>
    public static H8Insn Decode(byte[] b, uint pc)
    {
        var i = new H8Insn { Len = 2 };
        int b0 = b[0], b1 = b[1];
        int hi = b1 >> 4, lo = b1 & 15;
        ushort W(int o) => (ushort)((b[o] << 8) | b[o + 1]);
        int S16(int o) => (short)W(o);
        uint U32(int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        static H8Operand R(int r) => new H8Operand { Mode = H8Mode.Reg, Reg = (byte)r };
        static H8Operand Imm(int v) => new H8Operand { Mode = H8Mode.Imm, Val = v };
        static H8Operand Cst(int v) => new H8Operand { Mode = H8Mode.Const, Val = v };
        static H8Operand Ind(int r) => new H8Operand { Mode = H8Mode.Ind, Reg = (byte)r };
        static H8Operand Abs(uint a) => new H8Operand { Mode = H8Mode.Abs, Val = (int)a };

        bool Fail() { i.Op = H8Op.Invalid; i.Len = 2; return false; }
        void Set(H8Op op, int len, int size, H8Operand s, H8Operand d) { i.Op = op; i.Len = (byte)len; i.Size = (byte)size; i.Src = s; i.Dst = d; }

        switch (b0)
        {
            case 0x00:
                if (b1 == 0) Set(H8Op.Nop, 2, 0, default, default); else Fail();
                break;

            case 0x01:
                i = Decode01(b);
                break;

            case 0x02: if (hi == 0) Set(H8Op.Stc, 2, 1, new H8Operand { Mode = H8Mode.Ccr }, R(lo)); else Fail(); break;
            case 0x03: if (hi == 0) Set(H8Op.Ldc, 2, 1, R(lo), new H8Operand { Mode = H8Mode.Ccr }); else Fail(); break;
            case 0x04: Set(H8Op.Orc, 2, 1, Imm(b1), new H8Operand { Mode = H8Mode.Ccr }); break;
            case 0x05: Set(H8Op.Xorc, 2, 1, Imm(b1), new H8Operand { Mode = H8Mode.Ccr }); break;
            case 0x06: Set(H8Op.Andc, 2, 1, Imm(b1), new H8Operand { Mode = H8Mode.Ccr }); break;
            case 0x07: Set(H8Op.Ldc, 2, 1, Imm(b1), new H8Operand { Mode = H8Mode.Ccr }); break;

            case 0x08: Set(H8Op.Add, 2, 1, R(hi), R(lo)); break;
            case 0x09: Set(H8Op.Add, 2, 2, R(hi), R(lo)); break;
            case 0x0a:
                if (hi == 0) Set(H8Op.Inc, 2, 1, Cst(1), R(lo));
                else if ((b1 & 0x88) == 0x80) Set(H8Op.Add, 2, 4, R((b1 >> 4) & 7), R(lo & 7));
                else Fail();
                break;
            case 0x0b:
                switch (hi)
                {
                    case 0x0: if ((lo & 8) == 0) Set(H8Op.Adds, 2, 4, Cst(1), R(lo)); else Fail(); break;
                    case 0x5: Set(H8Op.Inc, 2, 2, Cst(1), R(lo)); break;
                    case 0x7: if ((lo & 8) == 0) Set(H8Op.Inc, 2, 4, Cst(1), R(lo)); else Fail(); break;
                    case 0x8: if ((lo & 8) == 0) Set(H8Op.Adds, 2, 4, Cst(2), R(lo)); else Fail(); break;
                    case 0x9: if ((lo & 8) == 0) Set(H8Op.Adds, 2, 4, Cst(4), R(lo)); else Fail(); break;
                    case 0xd: Set(H8Op.Inc, 2, 2, Cst(2), R(lo)); break;
                    case 0xf: if ((lo & 8) == 0) Set(H8Op.Inc, 2, 4, Cst(2), R(lo)); else Fail(); break;
                    default: Fail(); break;
                }
                break;
            case 0x0c: Set(H8Op.Mov, 2, 1, R(hi), R(lo)); break;
            case 0x0d: Set(H8Op.Mov, 2, 2, R(hi), R(lo)); break;
            case 0x0e: Set(H8Op.Addx, 2, 1, R(hi), R(lo)); break;
            case 0x0f:
                if (hi == 0) Set(H8Op.Daa, 2, 1, R(lo), default);
                else if ((b1 & 0x88) == 0x80) Set(H8Op.Mov, 2, 4, R((b1 >> 4) & 7), R(lo & 7));
                else Fail();
                break;

            case 0x10: case 0x11: case 0x12: case 0x13:
            {
                // shifts / rotates by one bit (H8/300H)
                int sz;
                bool alt; // hi nibble bit 3 selects the arithmetic/non-extended flavour
                switch (hi & 7) { case 0: sz = 1; break; case 1: sz = 2; break; case 3: sz = 4; break; default: Fail(); return i; }
                alt = (hi & 8) != 0;
                if (sz == 4 && (lo & 8) != 0) { Fail(); return i; }
                H8Op op = b0 switch
                {
                    0x10 => alt ? H8Op.Shal : H8Op.Shll,
                    0x11 => alt ? H8Op.Shar : H8Op.Shlr,
                    0x12 => alt ? H8Op.Rotl : H8Op.Rotxl,
                    _ => alt ? H8Op.Rotr : H8Op.Rotxr,
                };
                Set(op, 2, sz, R(lo), default);
                break;
            }

            case 0x14: Set(H8Op.Or, 2, 1, R(hi), R(lo)); break;
            case 0x15: Set(H8Op.Xor, 2, 1, R(hi), R(lo)); break;
            case 0x16: Set(H8Op.And, 2, 1, R(hi), R(lo)); break;
            case 0x17:
                switch (hi)
                {
                    case 0x0: Set(H8Op.Not, 2, 1, R(lo), default); break;
                    case 0x1: Set(H8Op.Not, 2, 2, R(lo), default); break;
                    case 0x3: if ((lo & 8) == 0) Set(H8Op.Not, 2, 4, R(lo), default); else Fail(); break;
                    case 0x5: Set(H8Op.Extu, 2, 2, R(lo), default); break;
                    case 0x7: if ((lo & 8) == 0) Set(H8Op.Extu, 2, 4, R(lo), default); else Fail(); break;
                    case 0x8: Set(H8Op.Neg, 2, 1, R(lo), default); break;
                    case 0x9: Set(H8Op.Neg, 2, 2, R(lo), default); break;
                    case 0xb: if ((lo & 8) == 0) Set(H8Op.Neg, 2, 4, R(lo), default); else Fail(); break;
                    case 0xd: Set(H8Op.Exts, 2, 2, R(lo), default); break;
                    case 0xf: if ((lo & 8) == 0) Set(H8Op.Exts, 2, 4, R(lo), default); else Fail(); break;
                    default: Fail(); break;
                }
                break;

            case 0x18: Set(H8Op.Sub, 2, 1, R(hi), R(lo)); break;
            case 0x19: Set(H8Op.Sub, 2, 2, R(hi), R(lo)); break;
            case 0x1a:
                if (hi == 0) Set(H8Op.Dec, 2, 1, Cst(1), R(lo));
                else if ((b1 & 0x88) == 0x80) Set(H8Op.Sub, 2, 4, R((b1 >> 4) & 7), R(lo & 7));
                else Fail();
                break;
            case 0x1b:
                switch (hi)
                {
                    case 0x0: if ((lo & 8) == 0) Set(H8Op.Subs, 2, 4, Cst(1), R(lo)); else Fail(); break;
                    case 0x5: Set(H8Op.Dec, 2, 2, Cst(1), R(lo)); break;
                    case 0x7: if ((lo & 8) == 0) Set(H8Op.Dec, 2, 4, Cst(1), R(lo)); else Fail(); break;
                    case 0x8: if ((lo & 8) == 0) Set(H8Op.Subs, 2, 4, Cst(2), R(lo)); else Fail(); break;
                    case 0x9: if ((lo & 8) == 0) Set(H8Op.Subs, 2, 4, Cst(4), R(lo)); else Fail(); break;
                    case 0xd: Set(H8Op.Dec, 2, 2, Cst(2), R(lo)); break;
                    case 0xf: if ((lo & 8) == 0) Set(H8Op.Dec, 2, 4, Cst(2), R(lo)); else Fail(); break;
                    default: Fail(); break;
                }
                break;
            case 0x1c: Set(H8Op.Cmp, 2, 1, R(hi), R(lo)); break;
            case 0x1d: Set(H8Op.Cmp, 2, 2, R(hi), R(lo)); break;
            case 0x1e: Set(H8Op.Subx, 2, 1, R(hi), R(lo)); break;
            case 0x1f:
                if (hi == 0) Set(H8Op.Das, 2, 1, R(lo), default);
                else if ((b1 & 0x88) == 0x80) Set(H8Op.Cmp, 2, 4, R((b1 >> 4) & 7), R(lo & 7));
                else Fail();
                break;

            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27:
            case 0x28: case 0x29: case 0x2a: case 0x2b: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                Set(H8Op.Mov, 2, 1, Abs(0xffff00u | (uint)b1), R(b0 & 15));
                break;
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35: case 0x36: case 0x37:
            case 0x38: case 0x39: case 0x3a: case 0x3b: case 0x3c: case 0x3d: case 0x3e: case 0x3f:
                Set(H8Op.Mov, 2, 1, R(b0 & 15), Abs(0xffff00u | (uint)b1));
                break;

            case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
            case 0x48: case 0x49: case 0x4a: case 0x4b: case 0x4c: case 0x4d: case 0x4e: case 0x4f:
                Set(H8Op.Bcc, 2, 0, default, new H8Operand { Mode = H8Mode.Target, Val = (int)((pc + 2 + (uint)(sbyte)b1) & 0xffffff) });
                i.Cond = (byte)(b0 & 15);
                break;

            case 0x50: Set(H8Op.Mulxu, 2, 1, R(hi), R(lo)); break;
            case 0x51: Set(H8Op.Divxu, 2, 1, R(hi), R(lo)); break;
            case 0x52: if ((lo & 8) == 0) Set(H8Op.Mulxu, 2, 2, R(hi), R(lo)); else Fail(); break;
            case 0x53: if ((lo & 8) == 0) Set(H8Op.Divxu, 2, 2, R(hi), R(lo)); else Fail(); break;
            case 0x54: if (b1 == 0x70) Set(H8Op.Rts, 2, 0, default, default); else Fail(); break;
            case 0x55:
                Set(H8Op.Bsr, 2, 0, default, new H8Operand { Mode = H8Mode.Target, Val = (int)((pc + 2 + (uint)(sbyte)b1) & 0xffffff) });
                break;
            case 0x56: if (b1 == 0x70) Set(H8Op.Rte, 2, 0, default, default); else Fail(); break;
            case 0x57:
                if ((b1 & 0xcf) == 0) Set(H8Op.Trapa, 2, 0, Imm((b1 >> 4) & 3), default); else Fail();
                break;
            case 0x58:
                if (lo == 0)
                {
                    Set(H8Op.Bcc, 4, 0, default, new H8Operand { Mode = H8Mode.Target, Val = (int)((pc + 4 + (uint)S16(2)) & 0xffffff) });
                    i.Cond = (byte)hi;
                }
                else Fail();
                break;
            case 0x59:
                if ((b1 & 0x8f) == 0) Set(H8Op.Jmp, 2, 0, Ind((b1 >> 4) & 7), default); else Fail();
                break;
            case 0x5a: Set(H8Op.Jmp, 4, 0, new H8Operand { Mode = H8Mode.Target, Val = (int)(U32(0) & 0xffffff) }, default); break;
            case 0x5b: Set(H8Op.Jmp, 2, 0, new H8Operand { Mode = H8Mode.Mem, Val = b1 }, default); break;
            case 0x5c:
                if (b1 == 0) Set(H8Op.Bsr, 4, 0, default, new H8Operand { Mode = H8Mode.Target, Val = (int)((pc + 4 + (uint)S16(2)) & 0xffffff) });
                else Fail();
                break;
            case 0x5d:
                if ((b1 & 0x8f) == 0) Set(H8Op.Jsr, 2, 0, Ind((b1 >> 4) & 7), default); else Fail();
                break;
            case 0x5e: Set(H8Op.Jsr, 4, 0, new H8Operand { Mode = H8Mode.Target, Val = (int)(U32(0) & 0xffffff) }, default); break;
            case 0x5f: Set(H8Op.Jsr, 2, 0, new H8Operand { Mode = H8Mode.Mem, Val = b1 }, default); break;

            case 0x60: Set(H8Op.Bset, 2, 1, R(hi), R(lo)); break;
            case 0x61: Set(H8Op.Bnot, 2, 1, R(hi), R(lo)); break;
            case 0x62: Set(H8Op.Bclr, 2, 1, R(hi), R(lo)); break;
            case 0x63: Set(H8Op.Btst, 2, 1, R(hi), R(lo)); break;
            case 0x64: Set(H8Op.Or, 2, 2, R(hi), R(lo)); break;
            case 0x65: Set(H8Op.Xor, 2, 2, R(hi), R(lo)); break;
            case 0x66: Set(H8Op.And, 2, 2, R(hi), R(lo)); break;
            case 0x67:
                if ((hi & 7) <= 7)
                {
                    Set((b1 & 0x80) != 0 ? H8Op.Bist : H8Op.Bst, 2, 1, Imm((b1 >> 4) & 7), R(lo));
                }
                break;

            case 0x68: case 0x69: case 0x6c: case 0x6d: case 0x6e: case 0x6f:
            {
                int sz = (b0 & 1) == 0 ? 1 : 2;
                bool store = (b1 & 0x80) != 0;
                int er = (b1 >> 4) & 7;
                H8Operand mem;
                int len = 2;
                switch (b0 & 0xfe)
                {
                    case 0x68: mem = Ind(er); break;
                    case 0x6c: mem = new H8Operand { Mode = store ? H8Mode.PreDec : H8Mode.PostInc, Reg = (byte)er }; break;
                    default: mem = new H8Operand { Mode = H8Mode.Disp, Reg = (byte)er, Val = S16(2), Aux = 16 }; len = 4; break;
                }
                if (store) Set(H8Op.Mov, len, sz, R(lo), mem); else Set(H8Op.Mov, len, sz, mem, R(lo));
                break;
            }

            case 0x6a: case 0x6b:
            {
                int sz = b0 == 0x6a ? 1 : 2;
                switch (hi)
                {
                    case 0x0: Set(H8Op.Mov, 4, sz, Abs((uint)S16(2) & 0xffffff), R(lo)); break;
                    case 0x2: Set(H8Op.Mov, 6, sz, Abs(U32(2)), R(lo)); break;
                    case 0x8: Set(H8Op.Mov, 4, sz, R(lo), Abs((uint)S16(2) & 0xffffff)); break;
                    case 0xa: Set(H8Op.Mov, 6, sz, R(lo), Abs(U32(2))); break;
                    default: Fail(); break;
                }
                break;
            }

            case 0x70: case 0x71: case 0x72: case 0x73:
            {
                if ((b1 & 0x80) != 0) { Fail(); break; }
                H8Op op = b0 switch { 0x70 => H8Op.Bset, 0x71 => H8Op.Bnot, 0x72 => H8Op.Bclr, _ => H8Op.Btst };
                Set(op, 2, 1, Imm((b1 >> 4) & 7), R(lo));
                break;
            }
            case 0x74: case 0x75: case 0x76: case 0x77:
            {
                H8Op op = b0 switch
                {
                    0x74 => (b1 & 0x80) != 0 ? H8Op.Bior : H8Op.Bor,
                    0x75 => (b1 & 0x80) != 0 ? H8Op.Bixor : H8Op.Bxor,
                    0x76 => (b1 & 0x80) != 0 ? H8Op.Biand : H8Op.Band,
                    _ => (b1 & 0x80) != 0 ? H8Op.Bild : H8Op.Bld,
                };
                Set(op, 2, 1, Imm((b1 >> 4) & 7), R(lo));
                break;
            }

            case 0x78:
                // 78 s0 6a/6b (2|a)d disp32: mov @(d:24,ERs)
                if ((b1 & 0x8f) == 0 && (b[2] == 0x6a || b[2] == 0x6b) && ((b[3] & 0xf0) == 0x20 || (b[3] & 0xf0) == 0xa0))
                {
                    int sz = b[2] == 0x6a ? 1 : 2;
                    var mem = new H8Operand { Mode = H8Mode.Disp, Reg = (byte)((b1 >> 4) & 7), Val = (int)U32(4), Aux = 32 };
                    if ((b[3] & 0xf0) == 0x20) Set(H8Op.Mov, 8, sz, mem, R(b[3] & 15));
                    else Set(H8Op.Mov, 8, sz, R(b[3] & 15), mem);
                }
                else Fail();
                break;

            case 0x79:
            {
                H8Op op = hi switch { 0 => H8Op.Mov, 1 => H8Op.Add, 2 => H8Op.Cmp, 3 => H8Op.Sub, 4 => H8Op.Or, 5 => H8Op.Xor, 6 => H8Op.And, _ => H8Op.Invalid };
                if (op == H8Op.Invalid) Fail(); else Set(op, 4, 2, Imm(W(2)), R(lo));
                break;
            }
            case 0x7a:
            {
                H8Op op = hi switch { 0 => H8Op.Mov, 1 => H8Op.Add, 2 => H8Op.Cmp, 3 => H8Op.Sub, 4 => H8Op.Or, 5 => H8Op.Xor, 6 => H8Op.And, _ => H8Op.Invalid };
                if (op == H8Op.Invalid || (lo & 8) != 0) Fail(); else Set(op, 6, 4, Imm((int)U32(2)), R(lo));
                break;
            }
            case 0x7b:
                if (W(2) == 0x598f && (W(0) == 0x7b5c || W(0) == 0x7bd4))
                {
                    Set(H8Op.Eepmov, 4, W(0) == 0x7bd4 ? 2 : 1, default, default);
                    i.Hi = W(0) == 0x7bd4;
                }
                else Fail();
                break;

            case 0x7c: case 0x7d: case 0x7e: case 0x7f:
                i = DecodeBitMem(b);
                break;

            case 0x80: case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87:
            case 0x88: case 0x89: case 0x8a: case 0x8b: case 0x8c: case 0x8d: case 0x8e: case 0x8f:
                Set(H8Op.Add, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
            case 0x98: case 0x99: case 0x9a: case 0x9b: case 0x9c: case 0x9d: case 0x9e: case 0x9f:
                Set(H8Op.Addx, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xa0: case 0xa1: case 0xa2: case 0xa3: case 0xa4: case 0xa5: case 0xa6: case 0xa7:
            case 0xa8: case 0xa9: case 0xaa: case 0xab: case 0xac: case 0xad: case 0xae: case 0xaf:
                Set(H8Op.Cmp, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xb0: case 0xb1: case 0xb2: case 0xb3: case 0xb4: case 0xb5: case 0xb6: case 0xb7:
            case 0xb8: case 0xb9: case 0xba: case 0xbb: case 0xbc: case 0xbd: case 0xbe: case 0xbf:
                Set(H8Op.Subx, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xc0: case 0xc1: case 0xc2: case 0xc3: case 0xc4: case 0xc5: case 0xc6: case 0xc7:
            case 0xc8: case 0xc9: case 0xca: case 0xcb: case 0xcc: case 0xcd: case 0xce: case 0xcf:
                Set(H8Op.Or, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xd0: case 0xd1: case 0xd2: case 0xd3: case 0xd4: case 0xd5: case 0xd6: case 0xd7:
            case 0xd8: case 0xd9: case 0xda: case 0xdb: case 0xdc: case 0xdd: case 0xde: case 0xdf:
                Set(H8Op.Xor, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xe0: case 0xe1: case 0xe2: case 0xe3: case 0xe4: case 0xe5: case 0xe6: case 0xe7:
            case 0xe8: case 0xe9: case 0xea: case 0xeb: case 0xec: case 0xed: case 0xee: case 0xef:
                Set(H8Op.And, 2, 1, Imm(b1), R(b0 & 15)); break;
            case 0xf0: case 0xf1: case 0xf2: case 0xf3: case 0xf4: case 0xf5: case 0xf6: case 0xf7:
            case 0xf8: case 0xf9: case 0xfa: case 0xfb: case 0xfc: case 0xfd: case 0xfe: case 0xff:
                Set(H8Op.Mov, 2, 1, Imm(b1), R(b0 & 15)); break;

            default:
                Fail();
                break;
        }
        return i;
    }

    // 0x01 prefix group
    private static H8Insn Decode01(byte[] b)
    {
        var i = new H8Insn { Len = 2 };
        int b1 = b[1], b2 = b[2], b3 = b[3];
        ushort W(int o) => (ushort)((b[o] << 8) | b[o + 1]);
        uint U32(int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        static H8Operand R(int r) => new H8Operand { Mode = H8Mode.Reg, Reg = (byte)r };
        i.Len = 2; i.Op = H8Op.Invalid;

        void Set(H8Op op, int len, int size, H8Operand s, H8Operand d) { i.Op = op; i.Len = (byte)len; i.Size = (byte)size; i.Src = s; i.Dst = d; }

        switch (b1 >> 4)
        {
            case 0x0:
            case 0x4:
            {
                // 01 00 xx: mov.l with @ERs / @ERs+ / @-ERd / @(d:16,ERs) / @aa:16 / @aa:32 / @(d:24,ERs)
                // 01 40 xx: ldc.w / stc.w with the same memory operands (CCR)
                if (b1 != 0x00 && b1 != 0x40) return i;
                bool ccr = b1 == 0x40;
                int sz = ccr ? 2 : 4;
                bool store;
                int regField;
                H8Operand mem;
                int len;
                switch (b2)
                {
                    case 0x69: case 0x6d: case 0x6f:
                    {
                        store = (b3 & 0x80) != 0;
                        if (ccr ? (b3 & 0x0f) != 0 : (b3 & 0x08) != 0) return i;
                        regField = b3 & 7;
                        int er = (b3 >> 4) & 7;
                        if (b2 == 0x69) mem = new H8Operand { Mode = H8Mode.Ind, Reg = (byte)er };
                        else if (b2 == 0x6d) mem = new H8Operand { Mode = store ? H8Mode.PreDec : H8Mode.PostInc, Reg = (byte)er };
                        else mem = new H8Operand { Mode = H8Mode.Disp, Reg = (byte)er, Val = (short)W(4), Aux = 16 };
                        len = b2 == 0x6f ? 6 : 4;
                        break;
                    }
                    case 0x6b:
                    {
                        store = (b3 & 0x80) != 0;
                        int kind = (b3 >> 4) & 7;
                        if (kind != 0 && kind != 2) return i;
                        if (ccr ? (b3 & 0x0f) != 0 : (b3 & 0x08) != 0) return i;
                        regField = b3 & 7;
                        if (kind == 0) { mem = new H8Operand { Mode = H8Mode.Abs, Val = (short)W(4) & 0xffffff }; len = 6; }
                        else { mem = new H8Operand { Mode = H8Mode.Abs, Val = (int)U32(4) }; len = 8; }
                        break;
                    }
                    case 0x78:
                    {
                        // 01 x0 78 s0 6b (2|a)d disp32
                        if ((b3 & 0x0f) != 0 || b[4] != 0x6b || (b[5] & 0x70) != 0x20) return i;
                        if (ccr ? (b[5] & 0x0f) != 0 : (b[5] & 0x08) != 0) return i;
                        store = (b[5] & 0x80) != 0;
                        regField = b[5] & 7;
                        mem = new H8Operand { Mode = H8Mode.Disp, Reg = (byte)((b3 >> 4) & 7), Val = (int)U32(6), Aux = 32 };
                        len = 10;
                        break;
                    }
                    default: return i;
                }
                H8Operand reg = ccr ? new H8Operand { Mode = H8Mode.Ccr } : R(regField);
                if (store) Set(ccr ? H8Op.Stc : H8Op.Mov, len, sz, reg, mem);
                else Set(ccr ? H8Op.Ldc : H8Op.Mov, len, sz, mem, reg);
                break;
            }
            case 0x8:
                if (b1 == 0x80) Set(H8Op.Sleep, 2, 0, default, default);
                break;
            case 0xc:
                if (b1 == 0xc0 && b2 == 0x50) Set(H8Op.Mulxs, 4, 1, R(b3 >> 4), R(b3 & 15));
                else if (b1 == 0xc0 && b2 == 0x52 && (b3 & 8) == 0) Set(H8Op.Mulxs, 4, 2, R(b3 >> 4), R(b3 & 15));
                break;
            case 0xd:
                if (b1 == 0xd0 && b2 == 0x51) Set(H8Op.Divxs, 4, 1, R(b3 >> 4), R(b3 & 15));
                else if (b1 == 0xd0 && b2 == 0x53 && (b3 & 8) == 0) Set(H8Op.Divxs, 4, 2, R(b3 >> 4), R(b3 & 15));
                break;
            case 0xf:
                if (b1 == 0xf0 && (b3 & 0x88) == 0)
                {
                    H8Op op = b2 switch { 0x64 => H8Op.Or, 0x65 => H8Op.Xor, 0x66 => H8Op.And, _ => H8Op.Invalid };
                    if (op != H8Op.Invalid) Set(op, 4, 4, R((b3 >> 4) & 7), R(b3 & 7));
                }
                break;
        }
        return i;
    }

    // 7c/7d/7e/7f: bit operations on memory (@ERd or @aa:8)
    private static H8Insn DecodeBitMem(byte[] b)
    {
        var i = new H8Insn { Len = 2 };
        int b0 = b[0], b1 = b[1], b2 = b[2], b3 = b[3];
        i.Len = 2; i.Op = H8Op.Invalid;
        H8Operand target;
        if (b0 == 0x7c || b0 == 0x7d)
        {
            if ((b1 & 0x8f) != 0) return i;
            target = new H8Operand { Mode = H8Mode.Ind, Reg = (byte)((b1 >> 4) & 7) };
        }
        else target = new H8Operand { Mode = H8Mode.Abs, Val = (int)(0xffff00u | (uint)b1) };

        bool grpRead = b0 == 0x7c || b0 == 0x7e; // btst/bor/... group ; 7d/7f = bset/bnot/bclr/bst group
        H8Op op = H8Op.Invalid;
        H8Operand sel; // bit selector
        if (grpRead)
        {
            switch (b2)
            {
                case 0x63: op = H8Op.Btst; if ((b3 & 0x0f) != 0) return i; sel = new H8Operand { Mode = H8Mode.Reg, Reg = (byte)(b3 >> 4) }; break;
                case 0x73: op = H8Op.Btst; if ((b3 & 0x8f) != 0) return i; sel = new H8Operand { Mode = H8Mode.Imm, Val = (b3 >> 4) & 7 }; break;
                case 0x74: op = (b3 & 0x80) != 0 ? H8Op.Bior : H8Op.Bor; goto imm;
                case 0x75: op = (b3 & 0x80) != 0 ? H8Op.Bixor : H8Op.Bxor; goto imm;
                case 0x76: op = (b3 & 0x80) != 0 ? H8Op.Biand : H8Op.Band; goto imm;
                case 0x77: op = (b3 & 0x80) != 0 ? H8Op.Bild : H8Op.Bld; goto imm;
                imm:
                    if ((b3 & 0x0f) != 0) return i;
                    sel = new H8Operand { Mode = H8Mode.Imm, Val = (b3 >> 4) & 7 };
                    break;
                default: return i;
            }
        }
        else
        {
            switch (b2)
            {
                case 0x60: op = H8Op.Bset; if ((b3 & 0x0f) != 0) return i; sel = new H8Operand { Mode = H8Mode.Reg, Reg = (byte)(b3 >> 4) }; break;
                case 0x61: op = H8Op.Bnot; if ((b3 & 0x0f) != 0) return i; sel = new H8Operand { Mode = H8Mode.Reg, Reg = (byte)(b3 >> 4) }; break;
                case 0x62: op = H8Op.Bclr; if ((b3 & 0x0f) != 0) return i; sel = new H8Operand { Mode = H8Mode.Reg, Reg = (byte)(b3 >> 4) }; break;
                case 0x67:
                    op = (b3 & 0x80) != 0 ? H8Op.Bist : H8Op.Bst;
                    if ((b3 & 0x0f) != 0) return i;
                    sel = new H8Operand { Mode = H8Mode.Imm, Val = (b3 >> 4) & 7 };
                    break;
                case 0x70: op = H8Op.Bset; goto imm2;
                case 0x71: op = H8Op.Bnot; goto imm2;
                case 0x72: op = H8Op.Bclr; goto imm2;
                imm2:
                    if ((b3 & 0x8f) != 0) return i;
                    sel = new H8Operand { Mode = H8Mode.Imm, Val = (b3 >> 4) & 7 };
                    break;
                default: return i;
            }
        }
        i.Op = op; i.Len = 4; i.Size = 1; i.Src = sel; i.Dst = target;
        return i;
    }

    // ---------------------------------------------------------------------------------------------
    // Disassembly (MAME syntax)

    private static readonly string[] R8 = { "r0h", "r1h", "r2h", "r3h", "r4h", "r5h", "r6h", "r7h", "r0l", "r1l", "r2l", "r3l", "r4l", "r5l", "r6l", "r7l" };
    private static readonly string[] R16 = { "r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7", "e0", "e1", "e2", "e3", "e4", "e5", "e6", "e7" };
    private static readonly string[] R32 = { "er0", "er1", "er2", "er3", "er4", "er5", "er6", "sp" };
    private static readonly string[] CondNames = { "bt", "bf", "bhi", "bls", "bcc", "bcs", "bne", "beq", "bvc", "bvs", "bpl", "bmi", "bge", "blt", "bgt", "ble" };

    public static string RegName(int size, int r) => size switch { 1 => R8[r & 15], 2 => R16[r & 15], _ => R32[r & 7] };

    private static string Operand(in H8Operand o, int size, bool bitSel = false)
    {
        switch (o.Mode)
        {
            case H8Mode.Reg: return RegName(size, o.Reg);
            case H8Mode.Imm:
                if (bitSel) return "#" + o.Val.ToString("x");
                return size switch { 1 => $"#h'{o.Val & 0xff:x2}", 2 => $"#h'{o.Val & 0xffff:x4}", _ => $"#h'{(uint)o.Val:x8}" };
            case H8Mode.Const: return "#" + o.Val;
            case H8Mode.Ind: return "@" + R32[o.Reg];
            case H8Mode.PostInc: return "@" + R32[o.Reg] + "+";
            case H8Mode.PreDec: return "@-" + R32[o.Reg];
            case H8Mode.Disp: return $"@(h'{(o.Aux == 16 ? (uint)(o.Val & 0xffff) : (uint)o.Val):x}, {R32[o.Reg]})";
            case H8Mode.Abs: return $"@h'{(uint)o.Val:x6}";
            case H8Mode.Mem: return $"@h'{o.Val:x2}";
            case H8Mode.Ccr: return "ccr";
            case H8Mode.Target: return $"h'{o.Val & 0xffffff:x6}";
            default: return "";
        }
    }

    public static string Mnemonic(in H8Insn i)
    {
        string m = i.Op.ToString().ToLowerInvariant();
        switch (i.Op)
        {
            case H8Op.Bcc: return CondNames[i.Cond];
            case H8Op.Mov: case H8Op.Add: case H8Op.Sub: case H8Op.Cmp: case H8Op.And: case H8Op.Or: case H8Op.Xor:
            case H8Op.Addx: case H8Op.Subx: case H8Op.Inc: case H8Op.Dec: case H8Op.Neg: case H8Op.Not: case H8Op.Extu: case H8Op.Exts:
            case H8Op.Shll: case H8Op.Shal: case H8Op.Shlr: case H8Op.Shar: case H8Op.Rotl: case H8Op.Rotr: case H8Op.Rotxl: case H8Op.Rotxr:
            case H8Op.Mulxu: case H8Op.Mulxs: case H8Op.Divxu: case H8Op.Divxs: case H8Op.Daa: case H8Op.Das:
                return m + (i.Size == 1 ? ".b" : i.Size == 2 ? ".w" : ".l");
            case H8Op.Adds: return "adds.l";
            case H8Op.Subs: return "subs.l";
            case H8Op.Ldc: case H8Op.Stc: return i.Size == 2 ? m + ".w" : m;
            case H8Op.Eepmov: return i.Hi ? "eepmov.w" : "eepmov.b";
            default: return m;
        }
    }

    public static string Disassemble(in H8Insn i, uint pc)
    {
        if (i.Op == H8Op.Invalid) return "(invalid)";
        string m = Mnemonic(i);
        int sz = i.Size;
        string a, d;
        switch (i.Op)
        {
            case H8Op.Mulxu: case H8Op.Mulxs: case H8Op.Divxu: case H8Op.Divxs:
                a = Operand(i.Src, sz); d = Operand(i.Dst, sz == 1 ? 2 : 4); break;
            case H8Op.Bset: case H8Op.Bclr: case H8Op.Bnot: case H8Op.Btst: case H8Op.Bst: case H8Op.Bist: case H8Op.Bld: case H8Op.Bild:
            case H8Op.Band: case H8Op.Biand: case H8Op.Bor: case H8Op.Bior: case H8Op.Bxor: case H8Op.Bixor:
                a = i.Src.Mode == H8Mode.Imm ? Operand(i.Src, 1, true) : Operand(i.Src, 1); d = Operand(i.Dst, 1); break;
            case H8Op.Trapa:
                a = Operand(i.Src, 1, true); d = ""; break;
            case H8Op.Bcc: case H8Op.Bsr:
                a = Operand(i.Dst, 4); d = ""; break;
            case H8Op.Jmp: case H8Op.Jsr:
                a = i.Src.Mode == H8Mode.Ind ? R32[i.Src.Reg] : Operand(i.Src, 4); d = ""; break;
            case H8Op.Adds: case H8Op.Subs:
                a = Operand(i.Src, 4); d = Operand(i.Dst, 4); break;
            case H8Op.Stc: case H8Op.Ldc:
                a = Operand(i.Src, sz); d = Operand(i.Dst, sz); break;
            default:
                a = i.Src.Mode == H8Mode.None ? "" : Operand(i.Src, sz);
                d = i.Dst.Mode == H8Mode.None ? "" : Operand(i.Dst, sz);
                break;
        }
        var sb = new StringBuilder();
        if (a.Length == 0 && d.Length == 0) return m;
        sb.Append(m.PadRight(8));
        sb.Append(a);
        if (d.Length != 0) { sb.Append(", "); sb.Append(d); }
        return sb.ToString();
    }
}
