// System12.Runtime - Core. Original code for this project (not derived from RecompOne).
namespace System12.Runtime.Core;

/// <summary>
/// The CXD8661R reports a bus error (DBE) for accesses to unmapped regions (the reference model sets
/// m_berr and returns 0). Recompiled code cannot take CPU exceptions from inside a memory access, so by
/// default the bus throws this exception with an actionable message; see <see cref="UnimplementedPolicy"/>.
/// </summary>
public sealed class BusErrorException : Exception
{
    public uint Address { get; }
    public bool IsWrite { get; }
    public int Width { get; }
    public string? GuestFunction { get; set; }

    public override string Message => GuestFunction == null ? base.Message : base.Message + $" [guest {GuestFunction}]";

    public BusErrorException(uint address, bool write, int width, string reason)
        : base($"BUS ERROR: {(write ? "write" : "read")}{width * 8} @0x{address:x8} - {reason}")
    {
        Address = address; IsWrite = write; Width = width;
    }
}

/// <summary>Misaligned 16/32-bit access (MIPS would raise AdEL/AdES). Generated code must use LWL/LWR for unaligned data.</summary>
public sealed class AddressErrorException : Exception
{
    public uint Address { get; }
    public bool IsWrite { get; }
    public int Width { get; }
    public string? GuestFunction { get; set; }

    public override string Message => GuestFunction == null ? base.Message : base.Message + $" [guest {GuestFunction}]";

    public AddressErrorException(uint address, bool write, int width)
        : base($"ADDRESS ERROR: misaligned {(write ? "write" : "read")}{width * 8} @0x{address:x8}")
    {
        Address = address; IsWrite = write; Width = width;
    }
}

/// <summary>What the bus does when guest code touches something unimplemented / unmapped.</summary>
public enum UnimplementedPolicy
{
    /// <summary>Default: throw NotImplementedMmioException / BusErrorException (stops at the first unknown access).</summary>
    Throw = 0,

    /// <summary>
    /// Exploration mode: report each distinct access (device, address, direction) once via the log sink with the
    /// guest function name, count every hit in <c>System12Memory.UnimplementedHits</c>, then reads return 0 and
    /// writes are dropped. Never silent; for discovering all missing MMIO in one run. Not for correctness runs.
    /// </summary>
    LogAndContinue = 1,
}
