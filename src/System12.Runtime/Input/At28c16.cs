// System12.Runtime.Input - AT28C16 2K x 8 parallel EEPROM, modelled exactly as MAME models it.
// Original code for this project. Behaviour reference (read, not copied): MAME src/devices/machine/at28c16.cpp
// (BSD-3-Clause, copyright-holders: smf) at the pinned commit f8f6b22c3f328b9e0b04ff502989f8536c0d66c0.
//
// What MAME models (and therefore what this class models):
//   * 0x800 data bytes + 0x20 "ID" bytes = 0x820 bytes of storage, big address space 12 bits, RAM mapped 0x000-0x81f.
//   * A write programs ONE byte, only if it differs from the stored byte, and starts a 200 us write cycle.
//   * While the write cycle runs ("busy"): every READ (any address) returns  lastWrittenByte ^ 0x80
//     (this is the AT28C16 /DATA-polling behaviour of I/O7: the complement of bit 7 of the byte being written);
//     every WRITE is silently DROPPED (not queued).
//   * A write while the "OE at 12 V" pin is asserted erases the WHOLE array (0x820 bytes = 0xff) and starts a write cycle
//     with lastWrite = 0xff.
//   * With the "A9 at 12 V" pin asserted, accesses at offsets >= 0x7e0 are redirected to the ID bytes (offset + 0x20).
//   * Power-on default (nvram_default): all 0x820 bytes = 0xff, then (optionally) the first 0x800 bytes overwritten from a
//     default-data region. tektagt has no default-data region for the EEPROM (the two "at28c16" ROM_REGIONs in namcos12.cpp
//     belong to other games), so the default is plain 0xff.
//   * Persistence (MAME .nv file) = the raw 0x820 bytes, no header. A file of any other size is a read error.
//
// What MAME does NOT model (so neither does this class; listed so nobody assumes otherwise):
//   * the toggle bit (I/O6) of the real part - only /DATA polling (I/O7) exists; page-mode writes; the 150 us byte-load window;
//     write-protect/software data protection; timing other than the single 200 us busy window.
//   * set_a9_12v()/set_oe_12v() are private in MAME and nothing in namcos12.cpp calls them: the A9/OE 12 V paths are
//     UNREACHABLE from the game in tektagt. They are implemented here (properties) only because the MAME device has them;
//     the System 12 bus never asserts them.
//
// Time: the 200 us cycle needs a clock. Pass a delegate that returns monotonically increasing EMULATED microseconds
// (e.g. cpuCycles * 1e6 / cpuHz). Do not pass wall-clock time if you want deterministic runs.

using System;
using System.IO;

namespace System12.Runtime.Input;

/// <summary>Event raised for every state-changing EEPROM operation (observable for UIs, tests and traces).</summary>
public readonly record struct EepromEvent(EepromEventKind Kind, int Offset, byte Value, long TimeMicroseconds);

public enum EepromEventKind
{
    /// <summary>One byte was programmed (value differed from the stored byte). A 200 us write cycle started.</summary>
    ByteProgrammed,
    /// <summary>Whole array erased to 0xff (write while OE is at 12 V). A 200 us write cycle started.</summary>
    ChipErased,
    /// <summary>A write was dropped because the previous write cycle was still running.</summary>
    WriteDroppedBusy,
    /// <summary>A write stored the value already present: MAME does nothing (no cycle starts).</summary>
    WriteSameValueIgnored,
}

public sealed class At28c16
{
    public const int DataBytes = 0x800;
    public const int IdBytes = 0x20;
    public const int TotalBytes = DataBytes + IdBytes;   // 0x820 = size of the MAME .nv file
    public const int IdOffset = DataBytes - IdBytes;     // 0x7e0
    public const long WriteCycleMicroseconds = 200;
    /// <summary>Highest address of the 12-bit address space; 0x820..0xfff are unmapped in MAME (read 0, write ignored).</summary>
    public const int AddressSpaceSize = 0x1000;

    private readonly byte[] _mem = new byte[TotalBytes];
    private readonly Func<long> _nowUs;
    private int _lastWrite = -1;          // MAME m_last_write: -1 = idle, else the byte being written (0..255)
    private long _busyUntilUs;
    private bool _a9;
    private bool _oe;

    public At28c16(Func<long> nowMicroseconds)
    {
        _nowUs = nowMicroseconds ?? throw new ArgumentNullException(nameof(nowMicroseconds));
        SetDefault();
    }

    /// <summary>MAME m_a9_12v. Never asserted by the tektagt hardware path (see class comment).</summary>
    public bool A9At12V { get => _a9; set => _a9 = value; }

    /// <summary>MAME m_oe_12v. Never asserted by the tektagt hardware path (see class comment).</summary>
    public bool OeAt12V { get => _oe; set => _oe = value; }

    /// <summary>True while a write cycle is running (reads return the polling value, writes are dropped).</summary>
    public bool Busy { get { Poll(); return _lastWrite >= 0; } }

    /// <summary>True once the array content changed since the last <see cref="ClearDirty"/>/load/save.</summary>
    public bool Dirty { get; private set; }

    public long ByteWriteCount { get; private set; }
    public long EraseCount { get; private set; }
    public long DroppedWhileBusyCount { get; private set; }
    public long SameValueWriteCount { get; private set; }

    public event Action<EepromEvent>? Changed;

    public void ClearDirty() => Dirty = false;

    // ------------------------------------------------------------------ MAME read()/write()
    /// <summary>MAME at28c16_device::read(offs_t offset). Offsets 0..0xfff; >0x81f reads 0 (unmapped), as in MAME.</summary>
    public byte Read(int offset)
    {
        if ((uint)offset >= AddressSpaceSize) throw new ArgumentOutOfRangeException(nameof(offset));
        Poll();
        if (_lastWrite >= 0)
            return (byte)(_lastWrite ^ 0x80);                // data polling: any address while busy
        if (_a9 && offset >= IdOffset) offset += IdBytes;
        return offset < TotalBytes ? _mem[offset] : (byte)0; // unmapped RAM hole reads 0
    }

    /// <summary>MAME at28c16_device::write(offs_t offset, uint8_t data).</summary>
    public void Write(int offset, byte data)
    {
        if ((uint)offset >= AddressSpaceSize) throw new ArgumentOutOfRangeException(nameof(offset));
        Poll();
        var now = _nowUs();
        if (_lastWrite >= 0)
        {
            DroppedWhileBusyCount++;
            Changed?.Invoke(new EepromEvent(EepromEventKind.WriteDroppedBusy, offset, data, now));
            return;
        }
        if (_oe)
        {
            Array.Fill(_mem, (byte)0xff);
            _lastWrite = 0xff;
            _busyUntilUs = now + WriteCycleMicroseconds;
            EraseCount++;
            Dirty = true;
            Changed?.Invoke(new EepromEvent(EepromEventKind.ChipErased, offset, data, now));
            return;
        }
        if (_a9 && offset >= IdOffset) offset += IdBytes;
        var current = offset < TotalBytes ? _mem[offset] : (byte)0;
        if (current != data)
        {
            if (offset < TotalBytes) _mem[offset] = data;     // unmapped write is ignored by MAME, but the cycle still starts
            _lastWrite = data;
            _busyUntilUs = now + WriteCycleMicroseconds;
            ByteWriteCount++;
            Dirty = true;
            Changed?.Invoke(new EepromEvent(EepromEventKind.ByteProgrammed, offset, data, now));
        }
        else
        {
            SameValueWriteCount++;
            Changed?.Invoke(new EepromEvent(EepromEventKind.WriteSameValueIgnored, offset, data, now));
        }
    }

    private void Poll()
    {
        if (_lastWrite >= 0 && _nowUs() >= _busyUntilUs) _lastWrite = -1;   // MAME write_complete timer
    }

    // ------------------------------------------------------------------ nvram_default / nvram_read / nvram_write
    /// <summary>MAME nvram_default(): fill 0x820 bytes with 0xff, then copy up to 0x800 bytes of default data if given.</summary>
    public void SetDefault(ReadOnlySpan<byte> defaultData = default)
    {
        Array.Fill(_mem, (byte)0xff);
        if (!defaultData.IsEmpty)
            defaultData[..Math.Min(DataBytes, defaultData.Length)].CopyTo(_mem);
        _lastWrite = -1;
        Dirty = true;
    }

    /// <summary>
    /// MAME nvram_read(): the file must be exactly 0x820 bytes, otherwise it fails and NOTHING is loaded
    /// (MAME then leaves the freshly allocated, zero-initialised RAM in place - see <see cref="ZeroFill"/>).
    /// </summary>
    public bool TryImport(ReadOnlySpan<byte> nvramImage)
    {
        if (nvramImage.Length != TotalBytes) return false;
        nvramImage.CopyTo(_mem);
        _lastWrite = -1;
        Dirty = false;
        return true;
    }

    /// <summary>The contents of the 0x820 byte MAME-compatible .nv image.</summary>
    public byte[] Export() => (byte[])_mem.Clone();

    /// <summary>Read-only view of the 0x800 data bytes (no busy/ID logic) for diagnostics and tests.</summary>
    public ReadOnlySpan<byte> RawMemory => _mem;

    /// <summary>All 0x820 bytes = 0. This is what MAME ends up with when an existing .nv file has the wrong size.</summary>
    public void ZeroFill() { Array.Clear(_mem); _lastWrite = -1; Dirty = true; }
}

public enum EepromLoadStatus
{
    /// <summary>File present, 0x820 bytes, loaded.</summary>
    Loaded,
    /// <summary>No file: power-on default (all 0xff), as MAME nvram_reset().</summary>
    DefaultedNoFile,
    /// <summary>File present but not 0x820 bytes: MAME reports "Error reading NVRAM file" and keeps zero-initialised RAM.</summary>
    CorruptFileZeroed,
}

/// <summary>
/// File persistence in the same layout and format as MAME: <c>&lt;dataDir&gt;/nvram/&lt;game&gt;/at28c16</c>, raw 0x820 bytes,
/// so a file written by MAME 0.264 (<c>-nvram_directory</c>) can be dropped in and vice versa.
/// The data directory defaults to <c>$SYSTEM12_DATA_DIR</c> or, if unset, <c>%LOCALAPPDATA%/System12Recomp</c>
/// (<c>~/.local/share/System12Recomp</c> on Linux) - a user data directory, never the repository or the ROM folder.
/// </summary>
public sealed class At28c16Store
{
    public const string DataDirEnvironmentVariable = "SYSTEM12_DATA_DIR";

    public string FilePath { get; }

    public At28c16Store(string gameName, string? dataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(gameName) || gameName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("invalid game name", nameof(gameName));
        FilePath = Path.Combine(ResolveDataDirectory(dataDirectory), "nvram", gameName, "at28c16");
    }

    public static string ResolveDataDirectory(string? explicitDirectory = null)
    {
        if (!string.IsNullOrEmpty(explicitDirectory)) return explicitDirectory;
        var env = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrEmpty(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "System12Recomp");
    }

    /// <summary>MAME running_machine::nvram_load(): missing file -> default; wrong-size file -> error, zero RAM.</summary>
    public EepromLoadStatus Load(At28c16 device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!File.Exists(FilePath)) { device.SetDefault(); device.ClearDirty(); return EepromLoadStatus.DefaultedNoFile; }
        var bytes = File.ReadAllBytes(FilePath);
        if (device.TryImport(bytes)) return EepromLoadStatus.Loaded;
        device.ZeroFill(); device.ClearDirty();
        return EepromLoadStatus.CorruptFileZeroed;
    }

    /// <summary>Writes the 0x820 byte image atomically (temp file + rename). Clears <see cref="At28c16.Dirty"/>.</summary>
    public void Save(At28c16 device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, device.Export());
        File.Move(tmp, FilePath, overwrite: true);
        device.ClearDirty();
    }

    /// <summary>Saves only if the content changed since load/last save. Returns true when a file was written.</summary>
    public bool SaveIfDirty(At28c16 device)
    {
        if (!device.Dirty) return false;
        Save(device);
        return true;
    }
}
