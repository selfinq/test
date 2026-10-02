// System12.Runtime - Core. Original code for this project (not derived from RecompOne).
// Behaviour reference: MAME src/devices/cpu/psx/dma.cpp (BSD-3-Clause) - read, not copied.
namespace System12.Runtime.Core;

/// <summary>Device-to-RAM transfer (CHCR bit0 = 0). <paramref name="ramOffset"/> is the byte offset into RAM
/// (MADR already masked with RamBytes-1, like the reference). <paramref name="words"/> is the 32-bit word count.</summary>
public delegate void DmaToRamHandler(System12Memory mem, uint ramOffset, uint words, uint chcr);

/// <summary>RAM-to-device transfer (CHCR bit0 = 1).</summary>
public delegate void DmaFromRamHandler(System12Memory mem, uint ramOffset, uint words, uint chcr);

/// <summary>
/// PSX DMA controller (0x1f801080..0x1f8010f7): 7 channels x (MADR, BCR, CHCR) + DPCR + DICR.
/// Transfers execute synchronously when CHCR bit 24 is written with the channel enabled in DPCR
/// (the reference model also completes almost all transfers instantly).
/// Built in: ch0 MDEC-in, ch1 MDEC-out, ch2 GPU (block, linked list, GPUREAD), ch6 OTC.
/// Hooks: ch5 (the Namco ROM/"PIO" DMA, installed by the BankedRom agent), and any other channel.
/// Anything without a handler throws NOT IMPLEMENTED (the reference silently logs; we do not).
/// </summary>
public sealed class PsxDma
{
    private readonly System12Memory _mem;
    private readonly IrqController _irq;

    private readonly uint[] _madr = new uint[7];
    private readonly uint[] _bcr = new uint[7];
    private readonly uint[] _chcr = new uint[7];
    private readonly DmaToRamHandler?[] _toRam = new DmaToRamHandler?[7];
    private readonly DmaFromRamHandler?[] _fromRam = new DmaFromRamHandler?[7];
    private uint _dpcr;
    private uint _dicr;

    public PsxDma(System12Memory mem, IrqController irq)
    {
        _mem = mem; _irq = irq;
        _toRam[1] = (m, off, words, _) => { var mdec = m.Mdec; for (uint i = 0; i < words; i++) m.WriteRamU32Dma(off + i * 4, mdec.ReadData()); };
        _fromRam[0] = (m, off, words, _) => { var mdec = m.Mdec; for (uint i = 0; i < words; i++) mdec.Write0(m.ReadRamU32Dma(off + i * 4)); };
        _toRam[2] = (m, off, words, _) => { for (uint i = 0; i < words; i++) m.WriteRamU32Dma(off + i * 4, m.Gpu.ReadData()); };
        // ch2 RAM->GPU block mode; linked-list mode (sync 2) is special-cased in Run.
        _fromRam[2] = (m, off, words, _) => { for (uint i = 0; i < words; i++) m.Gpu.WriteGp0(m.ReadRamU32Dma(off + i * 4)); };
    }

    public uint Dpcr => _dpcr;
    public uint Dicr => _dicr;
    public uint Madr(int ch) => _madr[ch];
    public uint Bcr(int ch) => _bcr[ch];
    public uint Chcr(int ch) => _chcr[ch];

    /// <summary>Install the handler for device-to-RAM transfers on <paramref name="channel"/> (0..6).</summary>
    public void SetToRamHandler(int channel, DmaToRamHandler? handler) => _toRam[channel] = handler;

    /// <summary>Install the handler for RAM-to-device transfers on <paramref name="channel"/> (0..6).</summary>
    public void SetFromRamHandler(int channel, DmaFromRamHandler? handler) => _fromRam[channel] = handler;

    public void Reset()
    {
        Array.Clear(_madr); Array.Clear(_bcr); Array.Clear(_chcr);
        _dpcr = 0; _dicr = 0;
    }

    /// <summary>Register read at word-aligned physical <paramref name="phys"/> in 0x1f801080..0x1f8010ff.</summary>
    public uint Read(uint phys)
    {
        var idx = (int)((phys - 0x1f801080u) >> 4);
        var reg = (int)((phys >> 2) & 3);
        if (idx < 7)
            return reg switch { 0 => _madr[idx], 1 => _bcr[idx], _ => _chcr[idx] };
        // idx 7: 0x1f8010f0 DPCR, 0x1f8010f4 DICR
        return reg switch
        {
            0 => _dpcr,
            1 => _dicr,
            _ => throw new NotImplementedMmioException("DMA", false, phys, 4, 0,
                "register 0x1f8010f8/fc is not defined by the reference model"),
        };
    }

    /// <summary>Register write at word-aligned physical <paramref name="phys"/>; only bits in <paramref name="lane"/> are written.</summary>
    public void Write(uint phys, uint data, uint lane)
    {
        var idx = (int)((phys - 0x1f801080u) >> 4);
        var reg = (int)((phys >> 2) & 3);
        if (idx < 7)
        {
            switch (reg)
            {
                case 0: _madr[idx] = (_madr[idx] & ~lane) | (data & lane); break;
                case 1: _bcr[idx] = (_bcr[idx] & ~lane) | (data & lane); break;
                default:
                    _chcr[idx] = (_chcr[idx] & ~lane) | (data & lane);
                    if ((_chcr[idx] & (1u << 24)) != 0 && (_dpcr & (1u << (3 + idx * 4))) != 0)
                        Run(idx);
                    break;
            }
            return;
        }

        switch (reg)
        {
            case 0: _dpcr = (_dpcr & ~lane) | (data & lane); break;
            case 1:
            {
                var d = data & lane;
                var combined = (_dicr & ~lane) | d;
                _dicr = (_dicr & 0x80000000u) | (_dicr & ~d & 0x7f000000u) | (combined & 0x00ff803fu);
                if ((_dicr & 0x80000000u) != 0 && (_dicr & 0x7f000000u) == 0)
                    _dicr &= ~0x80000000u; // reference also de-asserts its IRQ output; I_STAT is a latch, nothing to clear
                break;
            }
            default:
                throw new NotImplementedMmioException("DMA", true, phys, 4, data,
                    "register 0x1f8010f8/fc is not defined by the reference model");
        }
    }

    private void Run(int ch)
    {
        var chcr = _chcr[ch];
        var toDevice = (chcr & 1) != 0;
        var sync = (int)((chcr >> 9) & 3);
        var ramOff = _madr[ch] & (_mem.RamBytes - 1);
        var bcr = _bcr[ch];

        // Size rule of the reference: sync mode 1 (block): size * count (count 0 = 0x10000); otherwise the whole BCR word.
        uint words;
        if (sync == 1)
        {
            var blocks = bcr >> 16;
            if (blocks == 0) blocks = 0x10000;
            words = (bcr & 0xffff) * blocks;
        }
        else words = bcr;

        if (ch == 6)
        {
            // OTC: always device->RAM "reverse clear" regardless of CHCR direction bit pattern 0x11000002.
            RunOtc(ramOff, words);
        }
        else if (ch == 2 && toDevice && sync == 2)
        {
            RunGpuLinkedList(_madr[ch]);
        }
        else if (toDevice)
        {
            var h = _fromRam[ch] ?? throw NotImpl(ch, true, chcr, words);
            h(_mem, ramOff, words, chcr);
        }
        else
        {
            var h = _toRam[ch] ?? throw NotImpl(ch, false, chcr, words);
            h(_mem, ramOff, words, chcr);
        }

        // completion (reference: dma_finished)
        _chcr[ch] &= ~((1u << 24) | (1u << 28));
        _dicr |= 1u << (24 + ch);
        InterruptUpdate();
    }

    private Exception NotImpl(int ch, bool toDevice, uint chcr, uint words)
    {
        var name = ch switch { 0 => "MDEC-in", 1 => "MDEC-out", 2 => "GPU", 3 => "CD-ROM (absent on System 12)", 4 => "SPU (absent on System 12)", 5 => "ROM/PIO (Namco banked-ROM DMA)", _ => "OTC" };
        return new NotImplementedMmioException("DMA", true, 0x1f801080u + (uint)ch * 0x10 + 8, 4, chcr,
            $"channel {ch} [{name}] {(toDevice ? "RAM->device" : "device->RAM")} transfer madr=0x{_madr[ch]:x8} bcr=0x{_bcr[ch]:x8} words={words} has no handler; "
            + $"install one with System12Memory.Dma.Set{(toDevice ? "FromRam" : "ToRam")}Handler({ch}, ...)");
    }

    private void RunOtc(uint ramOff, uint words)
    {
        if (words == 0) return;
        var mask = _mem.RamBytes - 1;
        var addr = ramOff;
        words--;
        while (words > 0)
        {
            var next = (addr - 4) & 0xffffff;
            _mem.WriteRamU32Dma(addr & mask, next);
            addr = next;
            words--;
        }
        _mem.WriteRamU32Dma(addr & mask, 0xffffff);
    }

    private void RunGpuLinkedList(uint madr)
    {
        var mask = _mem.RamBytes - 1;
        var addr = madr & 0xffffff;
        if (addr == 0xffffff) return;
        uint total = 0;
        for (;;)
        {
            if (addr == 0xffffff) return;
            if (total > 65535) throw new InvalidOperationException(
                $"GPU linked-list DMA exceeded 65535 words (madr=0x{madr:x8}); list probably cyclic");
            addr &= mask;
            var header = _mem.ReadRamU32Dma(addr);
            var n = header >> 24;
            for (uint i = 0; i < n; i++) _mem.Gpu.WriteGp0(_mem.ReadRamU32Dma((addr + 4 + i * 4) & mask));
            var next = header & 0xffffff;
            // reference stops on a self-referencing node (endless-loop guard)
            if (next != 0xffffff && (addr == (next & mask) || addr == _mem.ReadRamU32Dma(next & mask))) return;
            addr = next;
            total += n + 1;
        }
    }

    private void InterruptUpdate()
    {
        var flags = (_dicr >> 24) & 0x7f;
        var mask = (_dicr >> 16) & 0xff;
        if ((mask & 0x80) != 0 && (flags & mask) != 0)
        {
            _dicr |= 0x80000000u;
            _irq.Raise(3);
        }
        _dicr &= 0x00ffffffu | (_dicr << 8);
    }
}
