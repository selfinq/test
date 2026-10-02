// System12.Runtime - Core hook interface for memory-mapped devices.
// Original code for this project (not derived from RecompOne).
namespace System12.Runtime.Core;

/// <summary>
/// A device that claims one or more PHYSICAL address ranges on the System 12 main CPU bus.
/// Register with <see cref="System12Memory.MapDevice"/>. Addresses passed in are PHYSICAL
/// (kseg0/kseg1/kuseg already folded: addr &amp; 0x1fffffff). <c>offset</c> is
/// <c>physicalAddress - range.Start</c> of the range that matched.
///
/// Width contract: the bus issues the access at the width the CPU used (8/16/32).
/// Devices that only support a subset of widths must throw <see cref="NotImplementedMmioException"/>
/// (or call <see cref="MmioDeviceBase"/> defaults, which do this) - never silently return 0.
/// Unaligned 16/32-bit accesses never reach devices (the CPU would raise an address error);
/// LWL/LWR/SWL/SWR are decomposed by the bus into aligned 32-bit read-modify-write on the device,
/// so a device with side-effecting registers sees them as normal 32-bit accesses.
/// </summary>
public interface IMmioDevice
{
    /// <summary>Short name used in traces and exceptions, e.g. "BankedRom", "EepromIo".</summary>
    string Name { get; }

    /// <summary>Physical ranges claimed by this device (inclusive start, exclusive end).</summary>
    IReadOnlyList<MmioRange> Ranges { get; }

    byte ReadU8(uint offset);
    ushort ReadU16(uint offset);
    uint ReadU32(uint offset);

    void WriteU8(uint offset, byte value);
    void WriteU16(uint offset, ushort value);
    void WriteU32(uint offset, uint value);
}

/// <summary>Physical address range [Start, End). Both are physical (&lt; 0x20000000), End may be 0x20000000.</summary>
public readonly record struct MmioRange(uint Start, uint End)
{
    public uint Length => End - Start;
    public bool Contains(uint physical) => physical >= Start && physical < End;
    public bool Overlaps(MmioRange o) => Start < o.End && o.Start < End;
    public override string ToString() => $"[0x{Start:x8},0x{End:x8})";
    public static MmioRange FromLength(uint start, uint length) => new(start, start + length);
}

/// <summary>
/// Optional: a device that wants to know the CPU-visible (virtual) address / PC context
/// (for trace logs) implements this. The bus sets it before dispatching the access.
/// </summary>
public interface IMmioTraceAware
{
    void SetTraceContext(IMmioTrace trace);
}

/// <summary>Sink for MMIO access tracing and for NOT IMPLEMENTED reports.</summary>
public interface IMmioTrace
{
    void OnAccess(string device, bool write, uint physical, int width, uint value);
    void OnNotImplemented(string device, bool write, uint physical, int width, uint value, string detail);
}

/// <summary>
/// Thrown when guest code touches a device register that is not implemented. Carries an actionable
/// message: device, direction, physical address, width, value and the hint of what to implement.
/// </summary>
public sealed class NotImplementedMmioException : Exception
{
    public string Device { get; }
    public bool IsWrite { get; }
    public uint Physical { get; }
    public int Width { get; }
    public uint Value { get; }

    /// <summary>Name of the recompiled guest function (func_XXXXXXXX) found on the managed stack; set by the bus.</summary>
    public string? GuestFunction { get; set; }

    public override string Message => GuestFunction == null ? base.Message : base.Message + $" [guest {GuestFunction}]";

    public NotImplementedMmioException(string device, bool write, uint physical, int width, uint value, string detail)
        : base($"NOT IMPLEMENTED: {device} {(write ? "write" : "read")}{width * 8} @0x{physical:x8}"
               + (write ? $" value=0x{value:x}" : "") + $" - {detail}")
    {
        Device = device; IsWrite = write; Physical = physical; Width = width; Value = value;
    }
}

/// <summary>
/// Convenience base class: every access throws NOT IMPLEMENTED unless the derived device overrides it.
/// Derive and override only the widths/registers you really emulate.
/// </summary>
public abstract class MmioDeviceBase : IMmioDevice
{
    public abstract string Name { get; }
    public abstract IReadOnlyList<MmioRange> Ranges { get; }

    /// <summary>Physical base of the first range, used to format exception addresses.</summary>
    protected uint Base => Ranges.Count > 0 ? Ranges[0].Start : 0;

    protected Exception NotImpl(bool write, uint offset, int width, uint value = 0, string? detail = null)
        => new NotImplementedMmioException(Name, write, Base + offset, width, value,
            detail ?? $"register offset 0x{offset:x} not implemented in {GetType().Name}");

    public virtual byte ReadU8(uint offset) => throw NotImpl(false, offset, 1);
    public virtual ushort ReadU16(uint offset) => throw NotImpl(false, offset, 2);
    public virtual uint ReadU32(uint offset) => throw NotImpl(false, offset, 4);
    public virtual void WriteU8(uint offset, byte value) => throw NotImpl(true, offset, 1, value);
    public virtual void WriteU16(uint offset, ushort value) => throw NotImpl(true, offset, 2, value);
    public virtual void WriteU32(uint offset, uint value) => throw NotImpl(true, offset, 4, value);
}
