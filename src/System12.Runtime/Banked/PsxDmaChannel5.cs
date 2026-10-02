// PSX DMA channel 5 (+ DPCR/DICR) exactly as psxdma_device (src/devices/cpu/psx/dma.cpp, mame@f8f6b22)
// drives the System 12 ROM-read handler. Channel 5 has only a read handler (namcos12_rom_read).
using System;

namespace System12.Runtime.Banked;

public sealed class PsxDmaChannel5
{
    private readonly BankedRomController _rom;
    private readonly byte[] _ram;

    public PsxDmaChannel5(BankedRomController rom, byte[] ram)
    {
        if (ram.Length != rom.RamSize) throw new ArgumentException("RAM size mismatch");
        _rom = rom;
        _ram = ram;
    }

    public uint Dpcr { get; private set; }
    public uint Dicr { get; private set; }
    public uint Madr { get; private set; }
    public uint Bcr { get; private set; }
    public uint Chcr { get; private set; }
    public bool IrqAsserted { get; private set; }
    public DmaSource? Last { get; private set; }

    public void WriteDpcr(uint v) => Dpcr = v;
    public void WriteMadr(uint v) => Madr = v;
    public void WriteBcr(uint v) => Bcr = v;

    public void WriteDicr(uint data)
    {
        Dicr = (Dicr & 0x80000000) | (Dicr & ~data & 0x7f000000) | (data & 0xff803f);
        if ((Dicr & 0x80000000) != 0 && (Dicr & 0x7f000000) == 0)
        {
            Dicr &= ~0x80000000u;
            IrqAsserted = false;
        }
    }

    public void WriteChcr(uint data)
    {
        Chcr = data;
        Last = null;
        if ((Chcr & (1u << 0x18)) == 0 || (Dpcr & (1u << (3 + 5 * 4))) == 0) return;

        uint adrMask = (uint)_rom.RamSize - 1;
        uint address = Madr & adrMask;
        int size = unchecked((int)Bcr);
        if ((Chcr & 0x200) != 0)
        {
            uint ba = Bcr >> 16;
            if (ba == 0) ba = 0x10000;
            size = unchecked((int)((uint)(size & 0xffff) * ba));      // unsigned multiply stored to int32
        }

        if (Chcr == 0x01000000u)
            Run(address, size);
        else if ((Chcr & 0xffbffeffu) == 0x11000000u)                 // CD-style size calculation
        {
            int our = (int)(Bcr >> 16);
            our = our > 1 ? our : 1;
            our = unchecked(our * (int)(Bcr & 0xffff));
            Run(address, our);
        }
        else if (Chcr == 0x01000200u)
            Run(address, size);
        // any other mode: "unknown mode" in MAME (ignored, CHCR left busy)
    }

    private void Run(uint address, int size)
    {
        Last = _rom.DmaRomRead(_ram, address, size);
        Finished();
    }

    private void Finished()
    {
        Chcr &= ~((1u << 0x18) | (1u << 0x1c));
        Dicr |= 1u << (24 + 5);
        int nInt = (int)((Dicr >> 24) & 0x7f);
        int nMask = (int)((Dicr >> 16) & 0xff);
        if ((nMask & 0x80) != 0 && (nInt & nMask) != 0)
        {
            Dicr |= 0x80000000;
            IrqAsserted = true;
        }
        Dicr &= 0x00ffffffu | (Dicr << 8);
    }
}
