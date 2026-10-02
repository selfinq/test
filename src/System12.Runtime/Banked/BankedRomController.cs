// Banked ROM window, ROM->RAM DMA source and Tekken Tag Tournament protection registers
// of the Namco System 12 CPU board (tektagt, coh700b).
//
// Behavioural port of MAME 0.264 (mamedev/mame@f8f6b22) src/mame/namco/namcos12.cpp:
//   namcos12_state::bankoffset_w / dmaoffset_w / namcos12_rom_read (+ maincpu_map)
//   tektagt_state::tektagt_protection_{1,2,3}_{r,w}
// The Python twin is tests/py_banked/bankrom_ref.py; both are cross-checked against MAME traces
// (reports/agent04_banked_rom.md).
//
// Bus conventions (verified against MAME traces): little-endian 32-bit bus; every handler here is a
// 16-bit handler, so a 32-bit access is split into two handler calls (offset 0 = low half, then
// offset 1 = high half), a 16-bit access calls one handler, and a byte access calls the handler
// once with the byte shifted into its lane (the handlers ignore mem_mask).
using System;

namespace System12.Runtime.Banked;

/// <summary>memory_bank::set_entry() on an unconfigured entry is a fatalerror in MAME.</summary>
public sealed class BankOutOfRangeException : InvalidOperationException
{
    public BankOutOfRangeException(int entry, int entries)
        : base($"bank entry {entry} is outside 0..{entries - 1}") { }
}

/// <summary>Result of one ROM-to-RAM DMA (namcos12_rom_read).</summary>
public readonly struct DmaSource
{
    /// <summary>RAM byte address the data goes to (the address the DMA code passed in).</summary>
    public uint RamAddress { get; init; }
    /// <summary>Bytes to copy to RAM[RamAddress..]; length is Words*4.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    public int RequestedWords { get; init; }
    public int Words { get; init; }
    public bool IsMainRom { get; init; }
    /// <summary>Byte offset inside the source region (already halfword aligned, as the driver does offset/2).</summary>
    public int SourceByteOffset { get; init; }
    public bool TruncatedByRom { get; init; }
    public bool TruncatedByRam { get; init; }
}

public sealed class BankedRomController
{
    public const uint BankRegister = 0x1F000000;
    public const uint BankWindowBase = 0x1FA00000;
    public const int BankWindowSize = 0x200000;
    public const uint DmaOffsetBase = 0x1F700000;
    public const uint DmaOffsetSize = 0x10000;
    // read-only handler; the driver also says mirror(0x800000) => 0x1FF00000, but MAME 0.264 shadows that with the
    // boot ROM at 0x1FC00000 (verified by forced test), so reads there are plain main-ROM reads, not this handler.
    public const uint Protection3Address = 0x1F700000;
    public const uint Protection1Address = 0x1FB00000;
    public const uint Protection2Address = 0x1FB80000;

    private readonly ReadOnlyMemory<byte> _banked;
    private readonly ReadOnlyMemory<byte> _main;
    private readonly uint[] _tttVal = new uint[2];

    public BankedRomController(ReadOnlyMemory<byte> bankedRom, ReadOnlyMemory<byte> mainRom = default,
        int ramSize = 0x400000, bool altBank = false, uint expBase = 0x1F000000)
    {
        _banked = bankedRom;
        _main = mainRom;
        RamSize = ramSize;
        AltBank = altBank;
        ExpBase = expBase;
        BankEntries = bankedRom.Length / 0x200000;
        // machine_start(): all state zero. (tektagt's std::fill_n(m_ttt_val, 0, ..) fills zero elements, so
        // m_ttt_val relies on zero-initialised storage; same here.)
    }

    public int RamSize { get; }
    /// <summary>namcos12_altbank_state only; false for tektagt.</summary>
    public bool AltBank { get; }
    /// <summary>psxcpu exp_base() (last value written to 0x1f801000); 0x1f300000 selects main ROM DMA.</summary>
    public uint ExpBase { get; set; }
    public int BankEntries { get; }

    public int BankEntry { get; private set; }
    /// <summary>m_bankoffset: never written by bankoffset_w (only meaningful for alt-bank boards).</summary>
    public ushort BankOffsetAlt { get; set; }
    public uint DmaOffset { get; private set; }
    public uint TektagDmaOffset { get; private set; }
    public int TttCount { get; private set; }
    public uint TttVal0 => _tttVal[0];
    public uint TttVal1 => _tttVal[1];
    public bool HasTektagtDma { get; private set; }

    /// <summary>tektagt_state::machine_reset(): only clears m_has_tektagt_dma.</summary>
    public void Reset() => HasTektagtDma = false;

    // ------------------------------------------------------------------ 16-bit handlers
    public void BankOffsetW(ushort data)
    {
        if (data >= BankEntries) throw new BankOutOfRangeException(data, BankEntries);
        BankEntry = data;
    }

    public void DmaOffsetW(int offset, ushort data) => DmaOffset = (uint)(offset * 2) | ((uint)data << 16);

    public void Protection1W(int offset, ushort data)
    {
        if (offset == 0)
        {
            TektagDmaOffset = data;
            if (TttCount != 2) _tttVal[TttCount] = data;
        }
        else if (offset == 1)
        {
            TektagDmaOffset |= (uint)data << 16;
            if (TttCount != 2) _tttVal[TttCount++] |= (uint)data << 16;
        }
    }

    public ushort Protection1R(int offset) => offset == 0 ? (ushort)0x8000 : (ushort)0;

    public void Protection2W(int offset, ushort data)
    {
        if (offset == 0) TttCount = 0;       // "start prot value write" trigger (0 or rand())
    }

    public ushort Protection2R(int offset)
    {
        if (TttCount == 2)
        {
            uint v0 = _tttVal[0], v1 = _tttVal[1];
            if (v0 == 0x806d2c24 && v1 == 0xd5545715) { if (offset == 0) return 0x36e2; }
            else if (v0 == 0x804c2c84 && v1 == 0xd5545615) { if (offset == 0) return 0x2651; }
            else if (v0 == 0x2aaba8e6 && v1 == 0x00820040) { if (offset == 1) return 0x4186; }
            else if (v0 == 0x2aaba592 && v1 == 0x01780544) { if (offset == 1) return 0x3c7d; }
            else if (((v1 >> 16) & 0xff) == 0xa9) return 0x552e;       // any offset
        }
        return 0;
    }

    public ushort Protection3R()
    {
        HasTektagtDma = true;                // consumed by the next namcos12_rom_read()
        return 0;
    }

    // ------------------------------------------------------------------ address decode
    private static bool InProt3(uint a) => a is Protection3Address or Protection3Address + 2;
    private static bool InProt1(uint a) => a - Protection1Address < 4;
    private static bool InProt2(uint a) => a - Protection2Address < 4;
    private static bool InWindow(uint a) => a - BankWindowBase < BankWindowSize;

    /// <summary>True when a read at this physical address is served by this controller.</summary>
    public static bool HandlesRead(uint a) =>
        InWindow(a) || a - BankRegister < 4 || InProt3(a & ~1u);

    /// <summary>True when a write at this physical address is a handler of this controller.</summary>
    public static bool HandlesWrite(uint a) =>
        a == BankRegister || a == BankRegister + 1 || a - DmaOffsetBase < DmaOffsetSize || InProt1(a) || InProt2(a);

    public ushort Read16(uint addr)
    {
        addr &= ~1u;
        if (addr - BankRegister < 4) return 0;                  // nopr
        if (InProt3(addr)) return Protection3R();
        if (InProt1(addr)) return Protection1R((int)((addr - Protection1Address) >> 1));
        if (InProt2(addr)) return Protection2R((int)((addr - Protection2Address) >> 1));
        if (InWindow(addr))
        {
            int p = BankEntry * 0x200000 + (int)(addr - BankWindowBase);
            var s = _banked.Span;
            return (ushort)(s[p] | (s[p + 1] << 8));
        }
        return 0;                                               // unmapped
    }

    public uint Read32(uint addr)
    {
        addr &= ~3u;
        if (addr - BankRegister < 4) return 0;
        if (InProt1(addr) || InProt2(addr) || addr == Protection3Address
            || InWindow(addr))
        {
            uint lo = Read16(addr);          // handler offset 0 first, then offset 1
            uint hi = Read16(addr + 2);
            return lo | (hi << 16);
        }
        return 0;
    }

    public byte Read8(uint addr) => (byte)(Read16(addr & ~1u) >> (8 * (int)(addr & 1)));

    public void Write16(uint addr, ushort data)
    {
        addr &= ~1u;
        if (addr == BankRegister) BankOffsetW(data);
        else if (addr - DmaOffsetBase < DmaOffsetSize) DmaOffsetW((int)((addr - DmaOffsetBase) >> 1), data);
        else if (InProt1(addr)) Protection1W((int)((addr - Protection1Address) >> 1), data);
        else if (InProt2(addr)) Protection2W((int)((addr - Protection2Address) >> 1), data);
        // bank window is read-only; anything else is unmapped for writes
    }

    public void Write32(uint addr, uint data)
    {
        addr &= ~3u;
        if (addr == BankRegister) Write16(addr, (ushort)data);   // 0x1f000002.. is not mapped for writes
        else if (addr - DmaOffsetBase < DmaOffsetSize || InProt1(addr) || InProt2(addr))
        {
            Write16(addr, (ushort)data);
            Write16(addr + 2, (ushort)(data >> 16));
        }
    }

    public void Write8(uint addr, byte data) => Write16(addr & ~1u, (ushort)(data << (8 * (int)(addr & 1))));

    // ------------------------------------------------------------------ DMA source (namcos12_rom_read)
    /// <summary>
    /// What psxdma channel 5's read handler does: computes the bytes that go to RAM[address..] for a DMA of
    /// <paramref name="sizeWords"/> 32-bit words. <paramref name="address"/> must already be masked with
    /// (ramSize-1) by the DMA code. Consumes m_has_tektagt_dma.
    /// </summary>
    public DmaSource ProduceDmaSource(uint address, int sizeWords)
    {
        uint reg = HasTektagtDma ? TektagDmaOffset : DmaOffset;
        var r = ComputeDmaSource(_banked, _main, RamSize, reg, HasTektagtDma, AltBank, BankOffsetAlt, ExpBase,
            address, sizeWords);
        HasTektagtDma = false;
        return r;
    }

    /// <summary>
    /// Stateless core of namcos12_rom_read(). <paramref name="offsetRegister"/> is m_tektagdmaoffset when
    /// <paramref name="viaTektagt"/> is set, otherwise m_dmaoffset: a BYTE offset into the selected region
    /// (bit 31 set, or expBase==0x1f300000, selects the 4 MiB main ROM; otherwise the banked ROMs).
    /// </summary>
    public static DmaSource ComputeDmaSource(ReadOnlyMemory<byte> banked, ReadOnlyMemory<byte> main, int ramSize,
        uint offsetRegister, bool viaTektagt, bool altBank, uint bankOffset, uint expBase, uint address, int sizeWords)
    {
        int offset = unchecked((int)offsetRegister);
        bool isMainRom = ((offset >> 31) & 1) != 0 || expBase == 0x1F300000;
        ReadOnlyMemory<byte> region;
        if (isMainRom)
        {
            if (main.IsEmpty) throw new InvalidOperationException("main ROM region not supplied");
            region = main;
            offset &= 0xffffff;     // the driver does not mask with 0x3fffff on purpose (sws2000 etc. overrun slightly)
        }
        else
        {
            region = banked;
            offset &= 0x7fffffff;
            if (!viaTektagt && altBank) offset += (int)bankOffset * 0x200000;
        }

        int ramWord = (int)(address / 4);
        offset /= 2;                                          // halfword index
        int size = sizeWords;
        bool trRom = false, trRam = false;
        int romleft = (region.Length / 2 - offset) / 2;       // C++ int division: truncates toward zero
        if (size > romleft) { size = romleft; trRom = true; }
        int ramleft = ramSize / 4 - ramWord;
        if (size > ramleft) { size = ramleft; trRam = true; }
        int words = Math.Max(size, 0);
        int start = offset * 2;
        return new DmaSource
        {
            RamAddress = (uint)ramWord * 4,
            Data = words == 0 ? ReadOnlyMemory<byte>.Empty : region.Slice(start, words * 4),
            RequestedWords = sizeWords,
            Words = words,
            IsMainRom = isMainRom,
            SourceByteOffset = start,
            TruncatedByRom = trRom,
            TruncatedByRam = trRam,
        };
    }

    /// <summary>Convenience: run <see cref="ProduceDmaSource"/> and copy the result into <paramref name="ram"/>.</summary>
    public DmaSource DmaRomRead(Span<byte> ram, uint address, int sizeWords)
    {
        var r = ProduceDmaSource(address, sizeWords);
        r.Data.Span.CopyTo(ram.Slice((int)r.RamAddress));
        return r;
    }
}
