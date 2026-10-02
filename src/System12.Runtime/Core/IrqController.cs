// System12.Runtime - Core. Original code for this project (not derived from RecompOne).
// Behaviour reference: MAME src/devices/cpu/psx/irq.cpp (BSD-3-Clause) - read, not copied.
namespace System12.Runtime.Core;

/// <summary>
/// PSX interrupt controller (I_STAT 0x1f801070, I_MASK 0x1f801074). RecompOne's static Interrupts class is NOT used:
/// it is a PS1-BIOS HLE (BiosB event tables, RAM &lt; 2 MB, fixed exception stack 0xE000) and cannot work with the
/// System 12 boot ROM, which installs its own exception handler.
/// Sources: 0 VBLANK, 1 GPU, 2 CD, 3 DMA, 4/5/6 root counters, 7 SIO0, 8 SIO1, 9 SPU, 10 PIO.
/// </summary>
public sealed class IrqController
{
    public const uint ValidBits = 0x7ff;

    private uint _stat;
    private uint _mask;

    /// <summary>Called whenever (stat &amp; mask) != 0 changes. The CPU model maps this to Cause.IP2.</summary>
    public Action<bool>? OnLineChanged { get; set; }

    public uint Stat => _stat;
    public uint Mask => _mask;
    public bool LineAsserted => (_stat & _mask) != 0;

    public void Reset() { _stat = 0; _mask = 0; Update(); }

    /// <summary>Device side: latch interrupt source <paramref name="source"/> (0..10) into I_STAT.</summary>
    public void Raise(int source)
    {
        if ((uint)source > 10) throw new ArgumentOutOfRangeException(nameof(source));
        _stat |= 1u << source;
        Update();
    }

    /// <summary>I_STAT write with lane mask. Matches MAME: stat = (stat &amp; ~lane) | (stat &amp; mask &amp; data).</summary>
    public void WriteStat(uint data, uint laneMask)
    {
        _stat = (_stat & ~laneMask) | (_stat & _mask & data);
        Update();
    }

    public void WriteMask(uint data, uint laneMask)
    {
        _mask = (_mask & ~laneMask) | (data & laneMask);
        Update();
    }

    private bool _last;
    private void Update()
    {
        var now = LineAsserted;
        if (now != _last) { _last = now; OnLineChanged?.Invoke(now); }
    }
}
