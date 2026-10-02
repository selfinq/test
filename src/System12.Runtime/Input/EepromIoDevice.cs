// System12.Runtime.Input - MMIO adapters for the main-CPU side of the System 12 "I/O" registers that belong to this module:
//   0x1f140000-0x1f140fff  AT28C16 EEPROM   (MAME: .rw("at28c16", read, write).umask32(0x00ff00ff))
//   0x1f018000             watchdog kick    (MAME: nopw; the watchdog is NOT enforced by MAME)
//   0x1f008000, 0x1f010000, 0x1f1bff08-0f   (MAME: nopw; function unknown - 0x1f008000: 16-bit 0 written 9 912 times in 2 800 frames by the game, 0x1f010000: never written)
// Original code for this project. Reference: reports/refs/namcos12.cpp maincpu_map().
//
// EEPROM bus semantics (verified against MAME 0.264 Lua taps, reports/trace_a08/probe_eeprom_fresh_log.txt):
//   The 8-bit device sits on lanes 0 and 2 of the 32-bit little-endian bus (umask 0x00ff00ff), so
//       device offset = (physical - 0x1f140000) >> 1
//   and ONLY physical addresses with (addr & 1) == 0 hit the chip. A 32-bit access = two device accesses (lane 0 first, then lane 2,
//   device offsets n and n+1); a 16-bit access = one device access (upper byte lane unmapped); an 8-bit access at an even address
//   = one device access. Odd byte addresses are unmapped lanes: MAME returns 0 / ignores the write - this class does the same
//   but REPORTS it through the trace sink (IMmioTrace.OnNotImplemented) instead of being silent.

using System;
using System.Collections.Generic;
using System.Threading;
using System12.Runtime.Core;

namespace System12.Runtime.Input;

public sealed class EepromIoDevice : MmioDeviceBase, IMmioTraceAware
{
    public const uint WindowBase = 0x1f140000;
    public const uint WindowLength = 0x1000;

    private readonly At28c16 _chip;
    private IMmioTrace? _trace;

    public EepromIoDevice(At28c16 chip) { _chip = chip ?? throw new ArgumentNullException(nameof(chip)); }

    public override string Name => "EepromIo";
    public override IReadOnlyList<MmioRange> Ranges { get; } = new[] { MmioRange.FromLength(WindowBase, WindowLength) };
    public At28c16 Chip => _chip;

    /// <summary>Number of accesses that hit the unmapped odd-byte lanes (MAME: read 0 / write ignored).</summary>
    public long UnmappedLaneAccesses { get; private set; }

    public void SetTraceContext(IMmioTrace trace) => _trace = trace;

    private void Unmapped(bool write, uint offset, int width, uint value)
    {
        UnmappedLaneAccesses++;
        _trace?.OnNotImplemented(Name, write, Base + offset, width, value,
            "odd byte address on the AT28C16 window (umask32 0x00ff00ff leaves lanes 1/3 unmapped; MAME returns 0 / ignores)");
    }

    private static int Dev(uint offset) => (int)(offset >> 1);

    public override byte ReadU8(uint offset)
    {
        if ((offset & 1) != 0) { Unmapped(false, offset, 1, 0); return 0; }
        return _chip.Read(Dev(offset));
    }

    public override ushort ReadU16(uint offset) => _chip.Read(Dev(offset));       // halfword: only the low byte lane is mapped

    public override uint ReadU32(uint offset)
    {
        var lo = _chip.Read(Dev(offset));
        var hi = _chip.Read(Dev(offset) + 1);
        return (uint)(lo | (hi << 16));
    }

    public override void WriteU8(uint offset, byte value)
    {
        if ((offset & 1) != 0) { Unmapped(true, offset, 1, value); return; }
        _chip.Write(Dev(offset), value);
    }

    public override void WriteU16(uint offset, ushort value) => _chip.Write(Dev(offset), (byte)value);

    public override void WriteU32(uint offset, uint value)
    {
        _chip.Write(Dev(offset), (byte)value);
        _chip.Write(Dev(offset) + 1, (byte)(value >> 16));     // dropped by the chip if the first write started a cycle
    }
}

/// <summary>
/// A write-only register window that MAME maps as <c>nopw</c>: writes are accepted and counted, reads are NOT IMPLEMENTED
/// (MAME has no read handler there; the game never read these addresses in 2 800 traced frames,
/// reports/trace_a08/nopw_windows_summary.txt).
/// </summary>
public class NopWriteDevice : MmioDeviceBase
{
    private readonly string _name;
    private readonly MmioRange[] _ranges;
    private long _writes;

    public NopWriteDevice(string name, uint physicalBase, uint length)
    {
        _name = name;
        _ranges = new[] { MmioRange.FromLength(physicalBase, length) };
    }

    public override string Name => _name;
    public override IReadOnlyList<MmioRange> Ranges => _ranges;
    public long WriteCount => Interlocked.Read(ref _writes);
    public uint LastValue { get; private set; }
    public int LastWidth { get; private set; }

    protected virtual void OnWrite(uint offset, int width, uint value) { }

    private void W(uint offset, int width, uint value)
    {
        Interlocked.Increment(ref _writes);
        LastValue = value; LastWidth = width;
        OnWrite(offset, width, value);
    }

    public override void WriteU8(uint offset, byte value) => W(offset, 1, value);
    public override void WriteU16(uint offset, ushort value) => W(offset, 2, value);
    public override void WriteU32(uint offset, uint value) => W(offset, 4, value);
    // reads: MmioDeviceBase default = NotImplementedMmioException (explicit, by design)
}

/// <summary>
/// 0x1f018000 - the watchdog kick register. MAME maps it as nopw (no watchdog device is instantiated for the main CPU), the boot
/// ROM writes 16-bit 0 to it from frame 24 and 1 187 719 writes were counted in 2 800 frames (MAME 0.264 tap, always value 0).
/// Each kick is observable through <see cref="Kicked"/>; an enforcing watchdog is NOT IMPLEMENTED because the reference has none.
/// </summary>
public sealed class WatchdogKickDevice : NopWriteDevice
{
    public const uint Address = 0x1f018000;
    public WatchdogKickDevice() : base("Watchdog", Address, 4) { }
    public event Action<uint>? Kicked;
    protected override void OnWrite(uint offset, int width, uint value) => Kicked?.Invoke(value);
}

public static class ArcadeIoFactory
{
    /// <summary>
    /// The register windows this module owns, ready for <c>System12Memory.MapDevice</c>.
    /// 0x1f008000 / 0x1f010000 / 0x1f1bff08 are plain nopw windows whose purpose is unknown even in MAME.
    /// </summary>
    public static IReadOnlyList<IMmioDevice> CreateMainBusDevices(At28c16 chip, out WatchdogKickDevice watchdog)
    {
        watchdog = new WatchdogKickDevice();
        return new IMmioDevice[]
        {
            new EepromIoDevice(chip),
            watchdog,
            new NopWriteDevice("Nopw1f008000", 0x1f008000, 4),
            new NopWriteDevice("Nopw1f010000", 0x1f010000, 4),
            new NopWriteDevice("Nopw1f1bff08", 0x1f1bff08, 8),
        };
    }
}
