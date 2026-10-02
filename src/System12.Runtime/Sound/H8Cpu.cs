// SPDX-License-Identifier: BSD-3-Clause
//
// Hitachi H8/300H CPU core (advanced mode, 24-bit addresses) - interpreter.
//
// Declared EMULATED COMPONENT: this core executes the unmodified System 12 sound-CPU program (sub.bin, the
// H8/3002 firmware shipped in the game's ROM set). It is not a high-level re-implementation of that firmware.
//
// Provenance: written for this project from the H8/300H programming manual (instruction semantics and
// condition-code rules) and CROSS-CHECKED, instruction by instruction, against MAME's h8 core
// (src/devices/cpu/h8/h8.cpp, h8.lst; license BSD-3-Clause, copyright-holders Olivier Galibert) by comparing
// register/CCR traces produced by MAME 0.264 for the actual firmware. Flag formulas for add/sub/shift/rotate/
// inc/dec and multiply/divide flag behaviour follow MAME (credited above); where this core and MAME were found
// to disagree, MAME's behaviour was adopted (the test-suite lists what was compared).
//
// NOT implemented: H8S-only instructions (EXR, 2-bit shifts, ldm/stm, mac, tas), normal (16-bit) mode,
// software standby, bus-error/illegal-instruction exceptions beyond halting, cycle-exact timing
// (state counts are an approximation, see EstimateStates), byte accesses through ER7 auto-increment/decrement
// (treated as +/-size, hardware uses 2).

using System;

namespace System12.Runtime.Sound;

public interface IH8Bus
{
    byte Read8(uint addr);
    ushort Read16(uint addr);
    void Write8(uint addr, byte value);
    void Write16(uint addr, ushort value);
}

public interface IH8InterruptSource
{
    /// <summary>Vector number (1..) of the pending interrupt that can be accepted given CCR, or 0.</summary>
    int PendingVector(byte ccr);
    /// <summary>The CPU has started the exception sequence for <paramref name="vector"/>.</summary>
    void Taken(int vector);
    /// <summary>CCR bits to set on interrupt entry (I always, UI depending on SYSCR.UE).</summary>
    byte EntryMask { get; }
}

public sealed class H8Cpu
{
    public const byte FlagC = 0x01, FlagV = 0x02, FlagZ = 0x04, FlagN = 0x08, FlagU = 0x10, FlagH = 0x20, FlagUI = 0x40, FlagI = 0x80;

    public readonly uint[] ER = new uint[8];
    public uint PC;
    public byte CCR;
    public bool Sleeping;
    public bool Halted;            // illegal instruction encountered
    public long States;            // approximate CPU state count
    public long Instructions;
    public IH8Bus Bus;
    public IH8InterruptSource? Interrupts;

    /// <summary>Optional code ROM image mapped at address 0 for decode caching. Addresses below its length are treated as constant.</summary>
    private readonly byte[] _rom;
    private readonly H8Insn[] _cache;
    private readonly bool[] _cached;
    private readonly byte[] _fetch = new byte[16];

    public uint LastIllegalPc;
    public byte[] LastIllegalBytes = Array.Empty<byte>();

    public H8Cpu(IH8Bus bus, byte[] codeRom)
    {
        Bus = bus;
        _rom = codeRom;
        _cache = new H8Insn[codeRom.Length / 2 + 8];
        _cached = new bool[_cache.Length];
    }

    // ---- register helpers ------------------------------------------------------------------------------
    public uint GetReg(int size, int r)
    {
        switch (size)
        {
            case 1: return r < 8 ? (ER[r] >> 8) & 0xff : ER[r & 7] & 0xff;
            case 2: return r < 8 ? ER[r] & 0xffff : ER[r & 7] >> 16;
            default: return ER[r & 7];
        }
    }

    public void SetReg(int size, int r, uint v)
    {
        switch (size)
        {
            case 1:
                if (r < 8) ER[r] = (ER[r] & 0xffff00ff) | ((v & 0xff) << 8);
                else ER[r & 7] = (ER[r & 7] & 0xffffff00) | (v & 0xff);
                break;
            case 2:
                if (r < 8) ER[r] = (ER[r] & 0xffff0000) | (v & 0xffff);
                else ER[r & 7] = (ER[r & 7] & 0x0000ffff) | (v << 16);
                break;
            default: ER[r & 7] = v; break;
        }
    }

    public void Reset()
    {
        Array.Clear(ER);
        CCR = FlagI;
        Sleeping = false; Halted = false;
        // advanced mode vector 0: 32-bit big-endian (upper byte reserved)
        PC = Read32(0) & 0xffffff;
    }

    // ---- bus helpers ---------------------------------------------------------------------------------------
    private uint Read(uint addr, int size)
    {
        addr &= 0xffffff;
        switch (size)
        {
            case 1: return Bus.Read8(addr);
            case 2: return Bus.Read16(addr & ~1u);
            default: return ((uint)Bus.Read16(addr & ~1u) << 16) | Bus.Read16((addr & ~1u) + 2);
        }
    }

    private void Write(uint addr, int size, uint v)
    {
        addr &= 0xffffff;
        switch (size)
        {
            case 1: Bus.Write8(addr, (byte)v); break;
            case 2: Bus.Write16(addr & ~1u, (ushort)v); break;
            default: Bus.Write16(addr & ~1u, (ushort)(v >> 16)); Bus.Write16((addr & ~1u) + 2, (ushort)v); break;
        }
    }

    public uint Read32(uint addr) => Read(addr, 4);

    // ---- instruction fetch/decode --------------------------------------------------------------------------
    public H8Insn DecodeAt(uint pc)
    {
        int ri = (int)(pc >> 1);
        if (pc < _rom.Length && (pc & 1) == 0)
        {
            if (_cached[ri]) return _cache[ri];
            for (int k = 0; k < 16; k++) _fetch[k] = pc + k < _rom.Length ? _rom[pc + k] : (byte)0;
            var d = H8Decoder.Decode(_fetch, pc);
            _cache[ri] = d; _cached[ri] = true;
            return d;
        }
        for (int k = 0; k < 16; k++) _fetch[k] = Bus.Read8((pc + (uint)k) & 0xffffff);
        return H8Decoder.Decode(_fetch, pc);
    }

    // ---- exceptions ----------------------------------------------------------------------------------------
    private void Push32PcCcr(uint returnPc)
    {
        uint sp = ER[7] - 4;
        ER[7] = sp;
        Bus.Write16(sp & 0xffffff, (ushort)((CCR << 8) | ((returnPc >> 16) & 0xff)));
        Bus.Write16((sp + 2) & 0xffffff, (ushort)returnPc);
    }

    private void EnterVector(int vector, byte maskBits)
    {
        Push32PcCcr(PC);
        CCR |= maskBits;
        PC = Read32((uint)vector * 4) & 0xffffff;
        States += 20;
    }

    // ---- main step -------------------------------------------------------------------------------------------
    /// <summary>Execute one instruction (or take one pending interrupt). Returns approximate states consumed.</summary>
    public int Step()
    {
        long before = States;
        bool skipIrq = _noIrqNext; // previous instruction was ldc/andc/orc/xorc: no interrupt sampling at this boundary
        _noIrqNext = false;

        if (Halted) { States += 2; return 2; }

        if (Interrupts != null && !skipIrq)
        {
            int v = Interrupts.PendingVector(CCR);
            if (v != 0)
            {
                Interrupts.Taken(v);
                Sleeping = false;
                EnterVector(v, Interrupts.EntryMask);
                return (int)(States - before);
            }
        }
        if (Sleeping) { States += 2; return 2; }

        uint pc = PC;
        H8Insn i = DecodeAt(pc);
        if (i.Op == H8Op.Invalid)
        {
            Halted = true; LastIllegalPc = pc;
            LastIllegalBytes = new byte[8];
            for (int k = 0; k < 8; k++) LastIllegalBytes[k] = Bus.Read8((pc + (uint)k) & 0xffffff);
            return 2;
        }
        uint next = (pc + i.Len) & 0xffffff;
        PC = next;
        Instructions++;
        int extra = Execute(in i, pc, next);
        States += i.Len + extra + 2;
        return (int)(States - before);
    }

    // After ldc/andc/orc/xorc interrupts are not sampled at the next instruction boundary (MAME: prefetch_noirq).
    private bool _noIrqNext;

    // ---- operand access ---------------------------------------------------------------------------------------
    private uint Ea(in H8Operand o, int size, out bool post)
    {
        post = false;
        switch (o.Mode)
        {
            case H8Mode.Ind: return ER[o.Reg] & 0xffffff;
            case H8Mode.PostInc: post = true; return ER[o.Reg] & 0xffffff;
            case H8Mode.PreDec: ER[o.Reg] -= (uint)size; return ER[o.Reg] & 0xffffff;
            case H8Mode.Disp: return (ER[o.Reg] + (uint)o.Val) & 0xffffff;
            case H8Mode.Abs: return (uint)o.Val & 0xffffff;
            default: throw new InvalidOperationException("bad EA mode " + o.Mode);
        }
    }

    private uint ReadOp(in H8Operand o, int size)
    {
        switch (o.Mode)
        {
            case H8Mode.Reg: return GetReg(size, o.Reg);
            case H8Mode.Imm: case H8Mode.Const: return (uint)o.Val;
            default:
            {
                uint a = Ea(in o, size, out bool post);
                uint v = Read(a, size);
                if (post) ER[o.Reg] += (uint)size;
                return v;
            }
        }
    }

    private void WriteOp(in H8Operand o, int size, uint v)
    {
        switch (o.Mode)
        {
            case H8Mode.Reg: SetReg(size, o.Reg, v); break;
            default:
            {
                uint a = Ea(in o, size, out bool post);
                Write(a, size, v);
                if (post) ER[o.Reg] += (uint)size;
                break;
            }
        }
    }

    // ---- flag helpers -----------------------------------------------------------------------------------------
    private static uint Mask(int size) => size == 1 ? 0xffu : size == 2 ? 0xffffu : 0xffffffffu;
    private static uint Msb(int size) => size == 1 ? 0x80u : size == 2 ? 0x8000u : 0x80000000u;

    private void SetNzv(uint v, int size)
    {
        CCR = (byte)(CCR & ~(FlagN | FlagV | FlagZ));
        v &= Mask(size);
        if (v == 0) CCR |= FlagZ; else if ((v & Msb(size)) != 0) CCR |= FlagN;
    }

    private void SetNz(uint v, int size)
    {
        CCR = (byte)(CCR & ~(FlagN | FlagZ));
        v &= Mask(size);
        if (v == 0) CCR |= FlagZ; else if ((v & Msb(size)) != 0) CCR |= FlagN;
    }

    private uint DoAdd(uint a, uint b, int size, uint carry, bool x)
    {
        uint m = Mask(size);
        ulong res = (ulong)(a & m) + (b & m) + carry;
        uint r = (uint)res & m;
        uint hmask = size == 1 ? 0xfu : size == 2 ? 0xfffu : 0xfffffffu;
        CCR = (byte)(CCR & ~(FlagN | FlagV | FlagC | (x ? 0 : FlagZ)));
        if ((((a & hmask) + (b & hmask) + carry) & (hmask + 1)) != 0) CCR |= FlagH; else CCR = (byte)(CCR & ~FlagH);
        if (x) { if (r != 0) CCR = (byte)(CCR & ~FlagZ); }
        else if (r == 0) CCR |= FlagZ;
        if ((r & Msb(size)) != 0 && (x || r != 0)) CCR |= FlagN;
        if (((~(a ^ b)) & (a ^ r) & Msb(size)) != 0) CCR |= FlagV;
        if ((res >> (size * 8)) != 0) CCR |= FlagC;
        return r;
    }

    private uint DoSub(uint a, uint b, int size, uint carry, bool x)
    {
        uint m = Mask(size);
        long res = (long)(a & m) - (b & m) - carry;
        uint r = (uint)res & m;
        uint hmask = size == 1 ? 0xfu : size == 2 ? 0xfffu : 0xfffffffu;
        CCR = (byte)(CCR & ~(FlagN | FlagV | FlagC | (x ? 0 : FlagZ)));
        if (((((a & hmask) - (b & hmask) - carry)) & (hmask + 1)) != 0) CCR |= FlagH; else CCR = (byte)(CCR & ~FlagH);
        if (x) { if (r != 0) CCR = (byte)(CCR & ~FlagZ); }
        else if (r == 0) CCR |= FlagZ;
        if ((r & Msb(size)) != 0 && (x || r != 0)) CCR |= FlagN;
        if (((a ^ b) & (a ^ r) & Msb(size)) != 0) CCR |= FlagV;
        if (res < 0) CCR |= FlagC;
        return r;
    }

    private uint DoIncDec(uint a, uint b, int size, bool dec)
    {
        uint m = Mask(size);
        uint r = (dec ? a - b : a + b) & m;
        CCR = (byte)(CCR & ~(FlagN | FlagV | FlagZ));
        if (r == 0) CCR |= FlagZ; else if ((r & Msb(size)) != 0) CCR |= FlagN;
        uint ov = dec ? ((a ^ b) & (a ^ r)) : (~(a ^ b) & (a ^ r));
        if ((ov & Msb(size)) != 0) CCR |= FlagV;
        return r;
    }

    private bool Cond(int c)
    {
        bool C = (CCR & FlagC) != 0, Z = (CCR & FlagZ) != 0, N = (CCR & FlagN) != 0, V = (CCR & FlagV) != 0;
        switch (c)
        {
            case 0: return true;
            case 1: return false;
            case 2: return !(C || Z);
            case 3: return C || Z;
            case 4: return !C;
            case 5: return C;
            case 6: return !Z;
            case 7: return Z;
            case 8: return !V;
            case 9: return V;
            case 10: return !N;
            case 11: return N;
            case 12: return N == V;
            case 13: return N != V;
            case 14: return !(Z || (N != V));
            default: return Z || (N != V);
        }
    }

    private uint Shift(H8Op op, uint v, int size)
    {
        uint m = Mask(size), msb = Msb(size);
        v &= m;
        uint cin = (CCR & FlagC) != 0 ? 1u : 0u;
        CCR = (byte)(CCR & ~(FlagN | FlagV | FlagZ | FlagC));
        uint r;
        switch (op)
        {
            case H8Op.Shll:
                if ((v & msb) != 0) CCR |= FlagC;
                r = (v << 1) & m; break;
            case H8Op.Shal:
                if ((v & msb) != 0) CCR |= FlagC;
                { uint top = v & (msb | (msb >> 1)); if (top == (msb >> 1) || top == msb) CCR |= FlagV; }
                r = (v << 1) & m; break;
            case H8Op.Shlr:
                if ((v & 1) != 0) CCR |= FlagC;
                r = v >> 1; break;
            case H8Op.Shar:
                if ((v & 1) != 0) CCR |= FlagC;
                r = (v >> 1) | (v & msb); break;
            case H8Op.Rotl:
                if ((v & msb) != 0) CCR |= FlagC;
                r = ((v << 1) | (v >> (size * 8 - 1))) & m; break;
            case H8Op.Rotr:
                if ((v & 1) != 0) CCR |= FlagC;
                r = ((v >> 1) | (v << (size * 8 - 1))) & m; break;
            case H8Op.Rotxl:
                if ((v & msb) != 0) CCR |= FlagC;
                r = ((v << 1) | cin) & m; break;
            default: // Rotxr
                if ((v & 1) != 0) CCR |= FlagC;
                r = (v >> 1) | (cin * msb); break;
        }
        if (r == 0) CCR |= FlagZ; else if ((r & msb) != 0) CCR |= FlagN;
        return r;
    }

    private static void ApplyBit(H8Op op, ref byte v, int bit)
    {
        switch (op)
        {
            case H8Op.Bset: v |= (byte)(1 << bit); break;
            case H8Op.Bclr: v &= (byte)~(1 << bit); break;
            case H8Op.Bnot: v ^= (byte)(1 << bit); break;
        }
    }

    // ---- execute ---------------------------------------------------------------------------------------------------
    /// <returns>extra states beyond the baseline</returns>
    private int Execute(in H8Insn i, uint pc, uint next)
    {
        int sz = i.Size;
        int extra = 0;
        switch (i.Op)
        {
            case H8Op.Nop: break;

            case H8Op.Mov:
            {
                uint v = ReadOp(in i.Src, sz);
                SetNzv(v, sz);
                WriteOp(in i.Dst, sz, v);
                if (i.Src.Mode > H8Mode.Const || i.Dst.Mode > H8Mode.Const) extra += 2;
                break;
            }
            case H8Op.Add:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                WriteOp(in i.Dst, sz, DoAdd(a, b, sz, 0, false));
                break;
            }
            case H8Op.Adds:
                ER[i.Dst.Reg & 7] += (uint)i.Src.Val; break;
            case H8Op.Subs:
                ER[i.Dst.Reg & 7] -= (uint)i.Src.Val; break;
            case H8Op.Addx:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                WriteOp(in i.Dst, sz, DoAdd(a, b, sz, (CCR & FlagC) != 0 ? 1u : 0u, true));
                break;
            }
            case H8Op.Sub:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                WriteOp(in i.Dst, sz, DoSub(a, b, sz, 0, false));
                break;
            }
            case H8Op.Subx:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                WriteOp(in i.Dst, sz, DoSub(a, b, sz, (CCR & FlagC) != 0 ? 1u : 0u, true));
                break;
            }
            case H8Op.Cmp:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                DoSub(a, b, sz, 0, false);
                break;
            }
            case H8Op.Inc:
            {
                uint a = ReadOp(in i.Dst, sz);
                WriteOp(in i.Dst, sz, DoIncDec(a, (uint)i.Src.Val, sz, false));
                break;
            }
            case H8Op.Dec:
            {
                uint a = ReadOp(in i.Dst, sz);
                WriteOp(in i.Dst, sz, DoIncDec(a, (uint)i.Src.Val, sz, true));
                break;
            }
            case H8Op.Neg:
            {
                uint a = GetReg(sz, i.Src.Reg);
                SetReg(sz, i.Src.Reg, DoSub(0, a, sz, 0, false));
                break;
            }
            case H8Op.Not:
            {
                uint v = ~GetReg(sz, i.Src.Reg) & Mask(sz);
                SetNzv(v, sz); SetReg(sz, i.Src.Reg, v);
                break;
            }
            case H8Op.Extu:
            {
                uint v = GetReg(sz, i.Src.Reg) & (sz == 2 ? 0xffu : 0xffffu);
                SetNzv(v, sz); SetReg(sz, i.Src.Reg, v);
                break;
            }
            case H8Op.Exts:
            {
                uint raw = GetReg(sz, i.Src.Reg);
                uint v = sz == 2 ? (uint)(ushort)(short)(sbyte)raw : (uint)(int)(short)raw;
                SetNzv(v, sz); SetReg(sz, i.Src.Reg, v);
                break;
            }
            case H8Op.And: case H8Op.Or: case H8Op.Xor:
            {
                uint a = ReadOp(in i.Dst, sz), b = ReadOp(in i.Src, sz);
                uint r = i.Op == H8Op.And ? a & b : i.Op == H8Op.Or ? a | b : a ^ b;
                SetNzv(r, sz);
                WriteOp(in i.Dst, sz, r);
                break;
            }
            case H8Op.Andc: CCR &= (byte)i.Src.Val; _noIrqNext = true; break;
            case H8Op.Orc: CCR |= (byte)i.Src.Val; _noIrqNext = true; break;
            case H8Op.Xorc: CCR ^= (byte)i.Src.Val; _noIrqNext = true; break;
            case H8Op.Ldc:
                if (i.Src.Mode == H8Mode.Imm) CCR = (byte)i.Src.Val;
                else if (i.Src.Mode == H8Mode.Reg) CCR = (byte)GetReg(1, i.Src.Reg);
                else CCR = (byte)(ReadOp(in i.Src, 2) >> 8);
                _noIrqNext = true;
                break;
            case H8Op.Stc:
                if (i.Dst.Mode == H8Mode.Reg) SetReg(1, i.Dst.Reg, CCR);
                else WriteOp(in i.Dst, 2, (uint)((CCR << 8) | CCR));
                break;
            case H8Op.Daa: DoDaa(i.Src.Reg); break;
            case H8Op.Das: DoDas(i.Src.Reg); break;

            case H8Op.Shll: case H8Op.Shal: case H8Op.Shlr: case H8Op.Shar:
            case H8Op.Rotl: case H8Op.Rotr: case H8Op.Rotxl: case H8Op.Rotxr:
                SetReg(sz, i.Src.Reg, Shift(i.Op, GetReg(sz, i.Src.Reg), sz));
                break;

            case H8Op.Bset: case H8Op.Bclr: case H8Op.Bnot:
            {
                int bit = (int)(i.Src.Mode == H8Mode.Imm ? (uint)i.Src.Val : GetReg(1, i.Src.Reg)) & 7;
                byte v = (byte)ReadBitTarget(in i.Dst, out uint ea);
                ApplyBit(i.Op, ref v, bit);
                WriteBitTarget(in i.Dst, ea, v);
                break;
            }
            case H8Op.Btst:
            {
                int bit = (int)(i.Src.Mode == H8Mode.Imm ? (uint)i.Src.Val : GetReg(1, i.Src.Reg)) & 7;
                uint v = ReadBitTarget(in i.Dst, out _);
                if ((v & (1u << bit)) != 0) CCR = (byte)(CCR & ~FlagZ); else CCR |= FlagZ;
                break;
            }
            case H8Op.Bst: case H8Op.Bist:
            {
                int bit = i.Src.Val & 7;
                byte v = (byte)ReadBitTarget(in i.Dst, out uint ea);
                bool c = (CCR & FlagC) != 0;
                if (i.Op == H8Op.Bist) c = !c;
                if (c) v |= (byte)(1 << bit); else v &= (byte)~(1 << bit);
                WriteBitTarget(in i.Dst, ea, v);
                break;
            }
            case H8Op.Bld: case H8Op.Bild: case H8Op.Band: case H8Op.Biand: case H8Op.Bor: case H8Op.Bior: case H8Op.Bxor: case H8Op.Bixor:
            {
                int bit = i.Src.Val & 7;
                uint v = ReadBitTarget(in i.Dst, out _);
                bool b = (v & (1u << bit)) != 0;
                bool c = (CCR & FlagC) != 0;
                switch (i.Op)
                {
                    case H8Op.Bld: c = b; break;
                    case H8Op.Bild: c = !b; break;
                    case H8Op.Band: c &= b; break;
                    case H8Op.Biand: c &= !b; break;
                    case H8Op.Bor: c |= b; break;
                    case H8Op.Bior: c |= !b; break;
                    case H8Op.Bxor: c ^= b; break;
                    case H8Op.Bixor: c ^= !b; break;
                }
                CCR = (byte)((CCR & ~FlagC) | (c ? FlagC : 0));
                break;
            }

            case H8Op.Mulxu:
            {
                if (sz == 1) { uint r = (GetReg(2, i.Dst.Reg) & 0xff) * GetReg(1, i.Src.Reg); SetReg(2, i.Dst.Reg, r); }
                else { uint r = (GetReg(4, i.Dst.Reg) & 0xffff) * GetReg(2, i.Src.Reg); SetReg(4, i.Dst.Reg, r); }
                extra += sz == 1 ? 12 : 20;
                break;
            }
            case H8Op.Mulxs:
            {
                if (sz == 1) { int r = (sbyte)GetReg(2, i.Dst.Reg) * (sbyte)GetReg(1, i.Src.Reg); SetReg(2, i.Dst.Reg, (uint)r); SetNz((uint)r, 2); }
                else { int r = (short)GetReg(4, i.Dst.Reg) * (short)GetReg(2, i.Src.Reg); SetReg(4, i.Dst.Reg, (uint)r); SetNz((uint)r, 4); }
                extra += sz == 1 ? 12 : 20;
                break;
            }
            case H8Op.Divxu:
            {
                uint dv = GetReg(sz, i.Src.Reg);
                uint num = sz == 1 ? GetReg(2, i.Dst.Reg) : GetReg(4, i.Dst.Reg);
                CCR = (byte)(CCR & ~(FlagZ | FlagN));
                if ((dv & Msb(sz)) != 0) CCR |= FlagN;
                if (dv == 0) CCR |= FlagZ;
                else
                {
                    uint q = num / dv, r = num % dv;
                    if (sz == 1) SetReg(2, i.Dst.Reg, (q & 0xff) | ((r & 0xff) << 8));
                    else SetReg(4, i.Dst.Reg, (q & 0xffff) | ((r & 0xffff) << 16));
                }
                extra += sz == 1 ? 12 : 20;
                break;
            }
            case H8Op.Divxs:
            {
                int dv = sz == 1 ? (sbyte)GetReg(1, i.Src.Reg) : (short)GetReg(2, i.Src.Reg);
                int num = sz == 1 ? (short)GetReg(2, i.Dst.Reg) : (int)GetReg(4, i.Dst.Reg);
                CCR = (byte)(CCR & ~(FlagZ | FlagN));
                if (dv == 0) CCR |= FlagZ;
                else
                {
                    int q = num / dv, r = num % dv; // C# truncates toward zero, remainder has dividend's sign (== MAME)
                    if (q < 0) CCR |= FlagN;
                    if (sz == 1) SetReg(2, i.Dst.Reg, (uint)((q & 0xff) | ((r & 0xff) << 8)));
                    else SetReg(4, i.Dst.Reg, (uint)((q & 0xffff) | ((r & 0xffff) << 16)));
                }
                extra += sz == 1 ? 12 : 20;
                break;
            }

            case H8Op.Bcc:
                if (Cond(i.Cond)) PC = (uint)i.Dst.Val;
                break;
            case H8Op.Jmp:
                PC = JumpTarget(in i.Src);
                break;
            case H8Op.Jsr:
            {
                uint t = JumpTarget(in i.Src);
                PushReturn(next);
                PC = t;
                break;
            }
            case H8Op.Bsr:
                PushReturn(next);
                PC = (uint)i.Dst.Val;
                break;
            case H8Op.Rts:
            {
                uint sp = ER[7];
                PC = Read(sp, 4) & 0xffffff;
                ER[7] = sp + 4;
                break;
            }
            case H8Op.Rte:
            {
                uint sp = ER[7];
                uint w = Bus.Read16(sp & 0xffffff);
                CCR = (byte)(w >> 8);
                PC = (((w & 0xff) << 16) | Bus.Read16((sp + 2) & 0xffffff)) & 0xffffff;
                ER[7] = sp + 4;
                break;
            }
            case H8Op.Trapa:
                EnterVector(8 + i.Src.Val, Interrupts != null ? Interrupts.EntryMask : (byte)(FlagI | FlagUI));
                break;
            case H8Op.Sleep:
                Sleeping = true;
                break;
            case H8Op.Eepmov:
            {
                if (!i.Hi)
                {
                    while ((ER[4] & 0xff) != 0)
                    {
                        Bus.Write8(ER[6] & 0xffffff, Bus.Read8(ER[5] & 0xffffff));
                        ER[5]++; ER[6]++;
                        ER[4] = (ER[4] & 0xffffff00) | ((ER[4] - 1) & 0xff);
                        extra += 4;
                    }
                }
                else
                {
                    while ((ER[4] & 0xffff) != 0)
                    {
                        Bus.Write8(ER[6] & 0xffffff, Bus.Read8(ER[5] & 0xffffff));
                        ER[5]++; ER[6]++;
                        ER[4] = (ER[4] & 0xffff0000) | ((ER[4] - 1) & 0xffff);
                        extra += 4;
                    }
                }
                break;
            }
            default:
                throw new InvalidOperationException("unhandled op " + i.Op);
        }
        return extra;
    }

    private uint ReadBitTarget(in H8Operand o, out uint ea)
    {
        if (o.Mode == H8Mode.Reg) { ea = 0; return GetReg(1, o.Reg); }
        ea = o.Mode == H8Mode.Ind ? ER[o.Reg] & 0xffffff : (uint)o.Val & 0xffffff;
        return Bus.Read8(ea);
    }

    private void WriteBitTarget(in H8Operand o, uint ea, byte v)
    {
        if (o.Mode == H8Mode.Reg) SetReg(1, o.Reg, v); else Bus.Write8(ea, v);
    }

    private uint JumpTarget(in H8Operand o)
    {
        switch (o.Mode)
        {
            case H8Mode.Ind: return ER[o.Reg] & 0xffffff;
            case H8Mode.Target: return (uint)o.Val & 0xffffff;
            case H8Mode.Mem: return Read32((uint)o.Val) & 0xffffff;
            default: throw new InvalidOperationException();
        }
    }

    private void PushReturn(uint ret)
    {
        uint sp = ER[7] - 4;
        ER[7] = sp;
        Write(sp, 4, ret & 0xffffff);
    }

    private void DoDaa(int r)
    {
        uint v = GetReg(1, r);
        uint t = 0;
        bool c = (CCR & FlagC) != 0, h = (CCR & FlagH) != 0;
        if (c)
        {
            if (h) { if ((v & 0xf0) <= 0x30 && (v & 0x0f) <= 3) t = 0x66; }
            else if ((v & 0xf0) <= 0x20) t = (v & 0x0f) <= 9 ? 0x60u : 0x66u;
        }
        else
        {
            if (h) { if ((v & 0x0f) <= 3) t = (v & 0xf0) <= 0x90 ? 0x06u : 0x66u; }
            else if ((v & 0x0f) <= 9) t = (v & 0xf0) <= 0x90 ? 0x00u : 0x60u;
            else t = (v & 0xf0) <= 0x80 ? 0x06u : 0x66u;
        }
        SetReg(1, r, DoAdd(v, t, 1, 0, false));
    }

    private void DoDas(int r)
    {
        uint v = GetReg(1, r);
        uint t = 0;
        bool c = (CCR & FlagC) != 0, h = (CCR & FlagH) != 0;
        if (c)
        {
            if (h) { if ((v & 0xf0) >= 0x60 && (v & 0x0f) >= 6) t = 0x9a; }
            else if ((v & 0xf0) >= 0x70 && (v & 0x0f) <= 9) t = 0xa0;
        }
        else if (h) { if ((v & 0xf0) <= 0x80 && (v & 0x0f) >= 6) t = 0xfa; }
        SetReg(1, r, DoAdd(v, t, 1, 0, false));
    }
}
