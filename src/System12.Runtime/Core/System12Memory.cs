// System12.Runtime - Core.
// Original code for this project EXCEPT: ReadWordLeft/ReadWordRight/WriteWordLeft/WriteWordRight and the RAM fast-path idiom are
// adapted from RecompOne.Runtime/Memory/PSMemory.cs, MIT License, Copyright (c) 2026 flaffy (full text in NOTICE).
// Address-map behaviour references (read, not copied): MAME src/devices/cpu/psx/psx.cpp, irq.cpp, dma.cpp and
// src/mame/namco/namcos12.cpp (BSD-3-Clause as stated in those files). See NOTICE.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RecompOne.Runtime.Memory;

namespace System12.Runtime.Core;

/// <summary>
/// Physical memory bus of the Namco System 12 main CPU (Sony CXD8661R, R3000A-class) for statically recompiled code.
///
/// Implements <see cref="IMemory"/> so it can be handed to generated functions as <c>IMemory m</c>.
/// IMPORTANT: generated code does <c>var mem = (PSMemory)m;</c>; that cast fails for this class (PSMemory is sealed and
/// hard-wires PS1 devices). The recompiler must emit <c>(System12Memory)m</c> instead; see patches/0003-memory-cast.md.
///
/// Model (all of it follows the MAME CXD8661R/psxcpu + namcos12 maps; see reports/agent05_runtime_core.md):
///  * kuseg (0x0...), kseg0 (0x8...), kseg1 (0xa...) alias the same 512 MB physical space; 0x2xxxxxxx-0x7xxxxxxx and
///    0xc0000000-0xfffdffff are bus errors; 0xfffe0130 is the cache-control (BIU) register.
///  * RAM 4 MB, but the visible window and mirroring are set by RAM_SIZE (0x1f801060): reset value gives a 2 MB window.
///  * Main ROM 4 MB at 0x1fc00000; visible window set by ROM config (0x1f801010): reset value gives 512 KB; the rest bus-errors.
///  * Scratchpad 1 KB at 0x1f800000 is only reachable when BIU.RAM and BIU.DS are set, through kuseg/kseg0.
///  * Hardware registers 0x1f801000-0x1f802fff are routed to PS1-compatible blocks (IRQ, DMA, root counters, MDEC, GPU port)
///    or to <see cref="IMmioDevice"/>s; everything else throws NOT IMPLEMENTED (or logs, see <see cref="Policy"/>).
/// </summary>
public sealed class System12Memory : IMemory
{
    // ------------------------------------------------------------------ constants
    public const uint DefaultRamBytes = 0x400000;
    public const uint MainRomBase = 0x1fc00000;
    public const uint MainRomMaxBytes = 0x400000;
    public const uint ScratchpadBase = 0x1f800000;
    public const uint ScratchpadBytes = 0x400;
    public const uint BiuAddress = 0xfffe0130;

    private const uint BiuLock = 0x01, BiuInv = 0x02, BiuTag = 0x04, BiuRam = 0x08, BiuDs = 0x80, BiuIs1 = 0x800;
    private const uint TagMatchMask = 0xfffff000, TagMatch = 0x10, TagValid = 0x0f;
    private const int IcacheEntries = 0x400, DcacheEntries = 0x100;

    // ------------------------------------------------------------------ state
    private readonly byte[] _ram;
    private readonly uint _ramBytes;
    private uint _ramWindow;   // size of the RAM window in physical space (from RAM_SIZE)
    private uint _ramMask;     // mask applied inside the window (min(ram, window) - 1)
    private uint _fastLimit;   // _ramWindow, or 0 while the cache is isolated (forces the slow path)

    private byte[] _rom = Array.Empty<byte>();
    private uint _romWindow;   // visible ROM window (from ROM config)

    private readonly byte[] _scratch = new byte[ScratchpadBytes]; // doubles as the data cache (like the reference)
    private readonly uint[] _icache = new uint[IcacheEntries];
    private readonly uint[] _icacheTag = new uint[IcacheEntries / 4];
    private uint _biu;
    private bool _cacheIsolated;

    private uint _expBase = 0x1f000000, _expConfig, _romConfig = 0x00130000, _comDelay, _ramConfig = 0x800;
    private readonly uint[] _hwPlain = new uint[0x400];   // word storage for the plain-RAM registers of 0x1f801000-0x1f801fff
    private readonly uint[] _hw2Plain = new uint[0x20];   // 0x1f802000-0x1f80207f

    private IMmioDevice[] _devices = Array.Empty<IMmioDevice>();
    private MmioRange[] _deviceRanges = Array.Empty<MmioRange>();
    private uint _devMin = uint.MaxValue, _devMax;

    private IGpuPort _gpu = new NotImplementedGpuPort();

    // ------------------------------------------------------------------ public components
    /// <summary>I_STAT/I_MASK. Raise(0) = VBLANK etc. The CPU model subscribes to OnLineChanged.</summary>
    public IrqController Irq { get; } = new();

    /// <summary>DMA controller; install channel handlers (e.g. ch5 Namco ROM DMA) here.</summary>
    public PsxDma Dma { get; }

    /// <summary>PS1 MDEC from RecompOne (pure class, no host dependencies). CXD8661R contains an MDEC in the reference model.</summary>
    public RecompOne.Runtime.Mdec Mdec { get; } = new();

    /// <summary>
    /// PS1 root counters from RecompOne. Valid register layout for the CXD8661R, but time base is host wall-clock
    /// (non-deterministic approximation) - replace with a deterministic model before accuracy work. Call
    /// <see cref="PollRootCounters"/> periodically to raise their IRQs (sources 4..6).
    /// </summary>
    public RecompOne.Runtime.Hardware.Timers RootCounters { get; } = new();

    /// <summary>GPU command/status port (0x1f801810/14). Default throws NOT IMPLEMENTED.</summary>
    public IGpuPort Gpu { get => _gpu; set => _gpu = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>What to do on unimplemented MMIO / bus errors. Default: throw.</summary>
    public UnimplementedPolicy Policy { get; set; } = UnimplementedPolicy.Throw;

    /// <summary>Log sink for LogAndContinue and for one-line notices. Default: stderr.</summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>Optional access tracer (devices only).</summary>
    public IMmioTrace? Trace { get; set; }

    /// <summary>Hit counters for LogAndContinue ("device|read/write|0xaddr" -> count).</summary>
    public IReadOnlyDictionary<string, long> UnimplementedHits => _hits;
    private readonly Dictionary<string, long> _hits = new();

    /// <summary>Count of writes silently dropped because the reference map declares them nop-writes (0x1f802040).</summary>
    public long NopWriteCount { get; private set; }

    // ------------------------------------------------------------------ construction
    /// <param name="ramBytes">RAM size, power of two, 1..16 MB. Default 4 MB (COH-700 PCB). COH-716 uses 16 MB.</param>
    public System12Memory(uint ramBytes = DefaultRamBytes)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("System12Memory assumes a little-endian host");
        if (ramBytes < 0x100000 || ramBytes > 0x1000000 || (ramBytes & (ramBytes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(ramBytes), "RAM size must be a power of two between 1 MB and 16 MB");
        _ramBytes = ramBytes;
        _ram = new byte[ramBytes];
        Dma = new PsxDma(this, Irq);
        Reset();
    }

    /// <summary>Size of RAM in bytes.</summary>
    public uint RamBytes => _ramBytes;

    /// <summary>Raw RAM (physical offset 0). For DMA handlers, snapshots and tests.</summary>
    public Span<byte> RamSpan => _ram;

    /// <summary>Currently visible RAM window (reset: 2 MB until the boot code programs RAM_SIZE).</summary>
    public uint RamWindow => _ramWindow;

    /// <summary>Currently visible ROM window (reset: 512 KB until the boot code programs the ROM config).</summary>
    public uint RomWindow => _romWindow;

    public uint BiuRegister => _biu;
    public uint ExpansionBase => _expBase;
    public uint RamConfigRegister => _ramConfig;
    public uint RomConfigRegister => _romConfig;
    public int MainRomLength => _rom.Length;

    /// <summary>
    /// Mirrors Status.IsC (COP0 SR bit 16). While set, data accesses go to the cache model, not the bus. The component
    /// that emulates MTC0 SR must call this (the stock recompiler only stores c.SR; see report).
    /// </summary>
    public bool CacheIsolated
    {
        get => _cacheIsolated;
        set { _cacheIsolated = value; _fastLimit = value ? 0 : _ramWindow; }
    }

    /// <summary>Power-on state of the bus: RAM_SIZE/ROM config reset values, cache control 0, devices keep their mapping.</summary>
    public void Reset()
    {
        _expBase = 0x1f000000; _expConfig = 0; _romConfig = 0x00130000; _comDelay = 0; _ramConfig = 0x800; _biu = 0;
        Array.Clear(_hwPlain); Array.Clear(_hw2Plain); Array.Clear(_scratch); Array.Clear(_icache); Array.Clear(_icacheTag);
        _cacheIsolated = false;
        UpdateRamWindow();
        UpdateRomWindow();
        Irq.Reset();
        Dma.Reset();
    }

    /// <summary>
    /// Load the main program ROM image (the user's own dump, e.g. generated/regions/maincpu_rom.bin). Little-endian
    /// byte image exactly as the CPU sees it from 0x1fc00000. Size must be a power of two, 4 bytes..4 MB. Never embedded.
    /// </summary>
    public void LoadMainRom(ReadOnlySpan<byte> image)
    {
        if (image.Length < 4 || image.Length > MainRomMaxBytes || (image.Length & (image.Length - 1)) != 0)
            throw new ArgumentException($"main ROM size must be a power of two in 4..0x{MainRomMaxBytes:x} bytes, got 0x{image.Length:x}", nameof(image));
        _rom = image.ToArray();
        UpdateRomWindow();
    }

    public void LoadMainRomFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"main ROM image not found: {path}. Supply your own dump (python3 tools/build_regions.py input/roms/tektagt.zip).", path);
        LoadMainRom(File.ReadAllBytes(path));
    }

    // ------------------------------------------------------------------ device registration
    /// <summary>
    /// Plug an <see cref="IMmioDevice"/> into the bus. Ranges are physical, must lie in 0x1f000000..0x1fbfffff or
    /// the not-yet-implemented parts of 0x1f801040-5f (SIO), 0x1f801800 (CD), 0x1f801c00-0x1f801fff (SPU), 0x1f802000+, and
    /// must not overlap RAM, ROM, scratchpad, the implemented PS1 blocks, or another device.
    /// </summary>
    public void MapDevice(IMmioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (var r in device.Ranges)
        {
            if (r.End <= r.Start || r.End > 0x20000000)
                throw new ArgumentException($"{device.Name}: invalid range {r}");
            var reserved = ReservedConflict(r);
            if (reserved != null)
                throw new ArgumentException($"{device.Name}: range {r} overlaps {reserved}");
            for (var i = 0; i < _devices.Length; i++)
                if (_deviceRanges[i].Overlaps(r))
                    throw new ArgumentException($"{device.Name}: range {r} overlaps device {_devices[i].Name} range {_deviceRanges[i]}");
        }

        var devs = new List<IMmioDevice>(_devices);
        var rngs = new List<MmioRange>(_deviceRanges);
        foreach (var r in device.Ranges) { devs.Add(device); rngs.Add(r); _devMin = Math.Min(_devMin, r.Start); _devMax = Math.Max(_devMax, r.End); }
        _devices = devs.ToArray();
        _deviceRanges = rngs.ToArray();
        if (device is IMmioTraceAware ta && Trace != null) ta.SetTraceContext(Trace);
    }

    public IReadOnlyList<(string Name, MmioRange Range)> MappedDevices =>
        _devices.Select((d, i) => (d.Name, _deviceRanges[i])).ToList();

    private static string? ReservedConflict(MmioRange r)
    {
        (uint s, uint e, string n)[] reserved =
        {
            (0x00000000, 0x01000000, "RAM window"),
            (ScratchpadBase, ScratchpadBase + 0x1000, "scratchpad / bus-error hole"),
            (0x1f801000, 0x1f801030, "memory-control registers"),
            (0x1f801060, 0x1f801070, "RAM_SIZE and plain registers"),
            (0x1f801070, 0x1f801080, "IRQ controller"),
            (0x1f801080, 0x1f801100, "DMA controller"),
            (0x1f801100, 0x1f801130, "root counters"),
            (0x1f801810, 0x1f801818, "GPU port"),
            (0x1f801820, 0x1f801828, "MDEC"),
            (0x1f802020, 0x1f802034, "plain registers 0x1f802020"),
            (0x1f802040, 0x1f802044, "nop-write register 0x1f802040"),
            (MainRomBase, 0x20000000, "main ROM"),
        };
        foreach (var (s, e, n) in reserved)
            if (r.Start < e && s < r.End) return $"{n} [0x{s:x8},0x{e:x8})";
        return null;
    }

    // ------------------------------------------------------------------ IMemory: fast paths
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SegOk(uint a) => ((0x31u >> (int)(a >> 29)) & 1) != 0; // kuseg(0), kseg0(4), kseg1(5)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadU8(uint address)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit)
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask));
        return (byte)ReadSlow(address, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadU16(uint address)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit && (address & 1) == 0)
            return Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask)));
        return (ushort)ReadSlow(address, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadU32(uint address)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit && (address & 3) == 0)
            return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask)));
        return ReadSlow(address, 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteU8(uint address, byte value)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit)
        {
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask)) = value;
            return;
        }
        WriteSlow(address, 1, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteU16(uint address, ushort value)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit && (address & 1) == 0)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask)), value);
            return;
        }
        WriteSlow(address, 2, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteU32(uint address, uint value)
    {
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && phys < _fastLimit && (address & 3) == 0)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram), (nint)(phys & _ramMask)), value);
            return;
        }
        WriteSlow(address, 4, value);
    }

    // ------------------------------------------------------------------ IMemory: unaligned helpers
    // Adapted from RecompOne PSMemory.cs (MIT, Copyright (c) 2026 flaffy) - see NOTICE.
    public uint ReadWordLeft(uint current, uint address)
    {
        var shift = (int)((address & 3) * 8);
        var word = ReadU32(address & ~3u);
        return (current & (0x00FFFFFFu >> shift)) | (word << (24 - shift));
    }

    public uint ReadWordRight(uint current, uint address)
    {
        var shift = (int)((address & 3) * 8);
        var word = ReadU32(address & ~3u);
        return (current & (0xFFFFFF00u << (24 - shift))) | (word >> shift);
    }

    public void WriteWordLeft(uint address, uint value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) * 8);
        var mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0xFFFFFF00u << shift)) | (value >> (24 - shift)));
    }

    public void WriteWordRight(uint address, uint value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) * 8);
        var mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0x00FFFFFFu >> (24 - shift))) | (value << shift));
    }

    public void LoadBytes(uint address, byte[] data)
    {
        // RAM span fast path when the whole range is plain RAM; otherwise go through the bus byte by byte.
        var phys = address & 0x1fffffffu;
        if (SegOk(address) && !_cacheIsolated && (ulong)phys + (ulong)data.Length <= _ramWindow
            && (phys & ~_ramMask) == ((phys + (uint)Math.Max(data.Length - 1, 0)) & ~_ramMask))
        {
            data.CopyTo(_ram.AsSpan((int)(phys & _ramMask)));
            return;
        }
        for (var i = 0; i < data.Length; i++) WriteU8(address + (uint)i, data[i]);
    }

    public void ZeroRange(uint address, uint length)
    {
        for (uint i = 0; i < length; i++) WriteU8(address + i, 0);
    }

    public void SetCd(RecompOne.Runtime.Cdrom.CdController cd) =>
        throw new NotImplementedMmioException("CD-ROM", true, 0x1f801800, 1, 0,
            "System 12 has no CD-ROM drive; IMemory.SetCd is a PS1 hook and must not be called for this machine");

    // ------------------------------------------------------------------ DMA helpers (RAM only, offset already masked)
    internal uint ReadRamU32Dma(uint ramOffset) => BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan((int)(ramOffset & (_ramBytes - 1) & ~3u)));
    internal void WriteRamU32Dma(uint ramOffset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan((int)(ramOffset & (_ramBytes - 1) & ~3u)), value);

    /// <summary>Raise root-counter IRQs (sources 4..6); call periodically (e.g. from the frame/vblank hook).</summary>
    public void PollRootCounters() => RootCounters.Poll(src => Irq.Raise(src));

    // ------------------------------------------------------------------ window updates
    private void UpdateRamWindow()
    {
        _ramWindow = ((_ramConfig >> 8) & 0xf) switch
        {
            0x0 or 0x1 => 0x100000u,
            0x4 or 0x8 or 0x9 => 0x200000u,
            0x2 or 0x3 or 0x5 or 0x6 or 0xc => 0x400000u,
            0x7 or 0xa or 0xb or 0xd or 0xe => 0x800000u,
            _ => 0x1000000u, // 0xf: namco system 10/12
        };
        _ramMask = Math.Min(_ramBytes, _ramWindow) - 1;
        _fastLimit = _cacheIsolated ? 0 : _ramWindow;
    }

    private void UpdateRomWindow()
    {
        var w = 1u << (int)((_romConfig >> 16) & 0x1f);
        _romWindow = Math.Min(w, 0x400000u);
    }

    // ------------------------------------------------------------------ slow read
    private uint ReadSlow(uint a, int w)
    {
        try { return ReadCore(a, w); }
        catch (NotImplementedMmioException e) when (OnDeviceNotImpl(e)) { return 0; }
    }

    private uint ReadCore(uint a, int w)
    {
        if ((a & (uint)(w - 1)) != 0) throw Annotate(new AddressErrorException(a, false, w));
        if (_cacheIsolated) return CacheRead(a, w);

        var seg = a >> 29;
        if (seg >= 6)
        {
            if ((a & ~3u) == BiuAddress)
            {
                if (w != 4) throw NotImpl("BIU", false, a, w, 0, "cache-control register is 32-bit only");
                return _biu;
            }
            return BusErrorRead(a, w, "kseg2 (0xc0000000-0xfffdffff): only 0xfffe0130 (cache control) exists");
        }
        if (seg != 0 && seg != 4 && seg != 5)
            return BusErrorRead(a, w, "address 0x20000000-0x7fffffff is unmapped (physical space is 512 MB)");

        var phys = a & 0x1fffffffu;
        if (phys < _ramWindow)
        {
            var o = (int)(phys & _ramMask);
            return w switch
            {
                1 => _ram[o],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_ram.AsSpan(o)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan(o)),
            };
        }
        if (phys < 0x1f000000)
            return BusErrorRead(a, w, $"outside the visible RAM window (0x{_ramWindow:x}; RAM_SIZE=0x{_ramConfig:x}); boot code must program 0x1f801060");

        if (phys >= MainRomBase)
        {
            var o = phys - MainRomBase;
            if (o >= _romWindow)
                return BusErrorRead(a, w, $"outside the visible ROM window (0x{_romWindow:x}; ROM config=0x{_romConfig:x}); boot code must program 0x1f801010");
            if (_rom.Length == 0)
                throw NotImpl("MainRom", false, a, w, 0, "no main ROM loaded; call LoadMainRom/LoadMainRomFile with your own dump");
            var r = (int)(o & (uint)(_rom.Length - 1));
            return w switch
            {
                1 => _rom[r],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(r)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(_rom.AsSpan(r)),
            };
        }

        if (phys >= _devMin && phys < _devMax)
        {
            for (var i = 0; i < _deviceRanges.Length; i++)
            {
                var rg = _deviceRanges[i];
                if (!rg.Contains(phys)) continue;
                var d = _devices[i];
                var off = phys - rg.Start;
                uint v;
                try
                {
                    v = w switch { 1 => d.ReadU8(off), 2 => d.ReadU16(off), _ => d.ReadU32(off) };
                }
                catch (NotImplementedMmioException e) when (OnDeviceNotImpl(e))
                {
                    return 0;
                }
                Trace?.OnAccess(d.Name, false, phys, w, v);
                return v;
            }
        }

        if (phys >= ScratchpadBase && phys < ScratchpadBase + 0x1000)
        {
            if (phys >= ScratchpadBase + ScratchpadBytes)
                return BusErrorRead(a, w, "0x1f800400-0x1f800fff is a bus-error hole");
            if (seg == 5 || (_biu & (BiuRam | BiuDs)) != (BiuRam | BiuDs))
                return BusErrorRead(a, w, seg == 5
                    ? "scratchpad is not reachable through kseg1"
                    : $"scratchpad not enabled (BIU=0x{_biu:x}; needs RAM|DS bits 0x88 in 0xfffe0130)");
            var o = (int)(phys - ScratchpadBase);
            return w switch
            {
                1 => _scratch[o],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_scratch.AsSpan(o)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(_scratch.AsSpan(o)),
            };
        }

        if (phys >= 0x1f801000 && phys < 0x1f803000)
            return HwRead(phys, w);

        throw NotImpl(ArcadeRegionName(phys, out var hint), false, a, w, 0, hint);
    }

    // ------------------------------------------------------------------ slow write
    private void WriteSlow(uint a, int w, uint v)
    {
        try { WriteCore(a, w, v); }
        catch (NotImplementedMmioException e) when (OnDeviceNotImpl(e)) { }
    }

    private void WriteCore(uint a, int w, uint v)
    {
        if ((a & (uint)(w - 1)) != 0) throw Annotate(new AddressErrorException(a, true, w));
        if (_cacheIsolated) { CacheWrite(a, w == 4 ? v : v << (int)((a & 3) * 8)); return; }

        var seg = a >> 29;
        if (seg >= 6)
        {
            if ((a & ~3u) == BiuAddress)
            {
                if (w != 4) throw NotImpl("BIU", true, a, w, v, "cache-control register is 32-bit only");
                _biu = v;
                return;
            }
            BusErrorWrite(a, w, v, "kseg2 (0xc0000000-0xfffdffff): only 0xfffe0130 (cache control) exists");
            return;
        }
        if (seg != 0 && seg != 4 && seg != 5)
        {
            BusErrorWrite(a, w, v, "address 0x20000000-0x7fffffff is unmapped (physical space is 512 MB)");
            return;
        }

        var phys = a & 0x1fffffffu;
        if (phys < _ramWindow)
        {
            var o = (int)(phys & _ramMask);
            switch (w)
            {
                case 1: _ram[o] = (byte)v; break;
                case 2: BinaryPrimitives.WriteUInt16LittleEndian(_ram.AsSpan(o), (ushort)v); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan(o), v); break;
            }
            return;
        }
        if (phys < 0x1f000000)
        {
            BusErrorWrite(a, w, v, $"outside the visible RAM window (0x{_ramWindow:x}; RAM_SIZE=0x{_ramConfig:x}); boot code must program 0x1f801060");
            return;
        }

        if (phys >= MainRomBase)
        {
            if (phys - MainRomBase >= _romWindow)
                BusErrorWrite(a, w, v, $"outside the visible ROM window (0x{_romWindow:x}; ROM config=0x{_romConfig:x})");
            else
                throw NotImpl("MainRom", true, a, w, v, "write to ROM (flash command sequences are not modelled)");
            return;
        }

        if (phys >= _devMin && phys < _devMax)
        {
            for (var i = 0; i < _deviceRanges.Length; i++)
            {
                var rg = _deviceRanges[i];
                if (!rg.Contains(phys)) continue;
                var d = _devices[i];
                var off = phys - rg.Start;
                try
                {
                    switch (w)
                    {
                        case 1: d.WriteU8(off, (byte)v); break;
                        case 2: d.WriteU16(off, (ushort)v); break;
                        default: d.WriteU32(off, v); break;
                    }
                }
                catch (NotImplementedMmioException e) when (OnDeviceNotImpl(e))
                {
                    return;
                }
                Trace?.OnAccess(d.Name, true, phys, w, v);
                return;
            }
        }

        if (phys >= ScratchpadBase && phys < ScratchpadBase + 0x1000)
        {
            if (phys >= ScratchpadBase + ScratchpadBytes)
            {
                BusErrorWrite(a, w, v, "0x1f800400-0x1f800fff is a bus-error hole");
                return;
            }
            if (seg == 5) { BusErrorWrite(a, w, v, "scratchpad is not reachable through kseg1"); return; }
            var enabled = (_biu & (BiuRam | BiuDs)) == (BiuRam | BiuDs);
            if (!enabled)
            {
                if ((_biu & BiuRam) != 0 && (_biu & BiuDs) == 0)
                    return; // reference: RAM bit set, DS clear -> reads bus-error, writes are dropped
                BusErrorWrite(a, w, v, $"scratchpad not enabled (BIU=0x{_biu:x}; needs RAM|DS bits 0x88 in 0xfffe0130)");
                return;
            }
            var o = (int)(phys - ScratchpadBase);
            switch (w)
            {
                case 1: _scratch[o] = (byte)v; break;
                case 2: BinaryPrimitives.WriteUInt16LittleEndian(_scratch.AsSpan(o), (ushort)v); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(o), v); break;
            }
            return;
        }

        if (phys >= 0x1f801000 && phys < 0x1f803000)
        {
            HwWrite(phys, w, v);
            return;
        }

        throw NotImpl(ArcadeRegionName(phys, out var hint), true, a, w, v, hint);
    }

    // ------------------------------------------------------------------ hardware registers 0x1f801000-0x1f802fff
    private static bool IsSideEffectRead(uint word) => word is 0x1f801810 or 0x1f801814 or 0x1f801820 or 0x1f801824;

    private uint HwRead(uint phys, int w)
    {
        var word = phys & ~3u;
        var shift = (int)((phys & 3) * 8);
        if (IsSideEffectRead(word) && w != 4)
            throw NotImpl(word < 0x1f801820 ? "GPU" : "MDEC", false, phys, w, 0, "sub-word access to a FIFO/status port is not modelled; use 32-bit accesses");
        var v = HwRead32(word, phys, w);
        return w == 4 ? v : (v >> shift) & (w == 1 ? 0xffu : 0xffffu);
    }

    private uint HwRead32(uint word, uint orig, int w)
    {
        switch (word)
        {
            case 0x1f801000: return _expBase;
            case 0x1f801008: return _expConfig;
            case 0x1f801010: return _romConfig;
            case 0x1f801020: return _comDelay;
            case 0x1f801060: return _ramConfig;
            case 0x1f801070: return Irq.Stat;
            case 0x1f801074: return Irq.Mask;
            case 0x1f801810: return _gpu.ReadData();
            case 0x1f801814: return _gpu.ReadStat();
            case 0x1f801820: return Mdec.ReadData();
            case 0x1f801824: return Mdec.ReadStatus();
        }
        if (IsPlainHw(word)) return _hwPlain[(word - 0x1f801000) >> 2];
        if (word >= 0x1f802020 && word < 0x1f802034) return _hw2Plain[(word - 0x1f802000) >> 2];
        if (word >= 0x1f801080 && word < 0x1f801100 && !((word & 0xf0) == 0xf0 && (word & 0xc) >= 8)) return Dma.Read(word);
        if (word >= 0x1f801100 && word < 0x1f801130)
        {
            if ((word & 0xf) == 0xc)
                throw NotImpl("RootCounters", false, orig, w, 0, "root-counter register +0xC is not defined by the reference model");
            RootCounters.TryRead(word, out var tv);
            return tv;
        }
        throw NotImpl(HwName(word), false, orig, w, 0, HwHint(word));
    }

    private void HwWrite(uint phys, int w, uint v)
    {
        var word = phys & ~3u;
        var shift = (int)((phys & 3) * 8);
        if (IsSideEffectRead(word) && w != 4)
            throw NotImpl(word < 0x1f801820 ? "GPU" : "MDEC", true, phys, w, v, "sub-word access to a FIFO/command port is not modelled; use 32-bit accesses");
        var lane = (w == 4 ? 0xffffffffu : w == 2 ? 0xffffu : 0xffu) << shift;
        var data = (w == 4 ? v : v << shift) & lane;

        switch (word)
        {
            case 0x1f801000: _expBase = 0x1f000000 | (Comb(_expBase, data, lane) & 0xffffff); return;
            case 0x1f801008: _expConfig = Comb(_expConfig, data, lane) & 0xaf1fffff; return;
            case 0x1f801010:
            {
                var old = _romConfig;
                _romConfig = Comb(_romConfig, data, lane);
                if (((_romConfig ^ old) & 0x001f0000) != 0) UpdateRomWindow();
                return;
            }
            case 0x1f801020: _comDelay = Comb(_comDelay, data, lane); return;
            case 0x1f801060:
            {
                var old = _ramConfig;
                _ramConfig = Comb(_ramConfig, data, lane);
                if (((_ramConfig ^ old) & 0xff00) != 0) UpdateRamWindow();
                return;
            }
            case 0x1f801070: Irq.WriteStat(data, lane); return;
            case 0x1f801074: Irq.WriteMask(data, lane); return;
            case 0x1f801810: _gpu.WriteGp0(v); return;
            case 0x1f801814: _gpu.WriteGp1(v); return;
            case 0x1f801820: Mdec.Write0(v); return;
            case 0x1f801824: Mdec.WriteControl(v); return;
            case 0x1f802040: // reference map: nopw (4 bytes); reads are unmapped
                NopWriteCount++;
                return;
        }
        if (IsPlainHw(word)) { ref var r = ref _hwPlain[(word - 0x1f801000) >> 2]; r = Comb(r, data, lane); return; }
        if (word >= 0x1f802020 && word < 0x1f802034) { ref var r = ref _hw2Plain[(word - 0x1f802000) >> 2]; r = Comb(r, data, lane); return; }
        if (word >= 0x1f801080 && word < 0x1f801100 && !((word & 0xf0) == 0xf0 && (word & 0xc) >= 8)) { Dma.Write(word, data, lane); return; }
        if (word >= 0x1f801100 && word < 0x1f801130)
        {
            if ((word & 0xf) == 0xc)
                throw NotImpl("RootCounters", true, phys, w, v, "root-counter register +0xC is not defined by the reference model");
            RootCounters.TryRead(word, out var old);
            RootCounters.TryWrite(word, Comb(old, data, lane));
            return;
        }
        throw NotImpl(HwName(word), true, phys, w, v, HwHint(word));
    }

    private static uint Comb(uint old, uint data, uint lane) => (old & ~lane) | (data & lane);

    /// <summary>Registers the reference declares as plain RAM (read back what was written).</summary>
    private static bool IsPlainHw(uint word) =>
        word == 0x1f801004 || word == 0x1f80100c || (word >= 0x1f801014 && word < 0x1f801020)
        || (word >= 0x1f801024 && word < 0x1f801030) || (word >= 0x1f801064 && word < 0x1f801070);

    private static string HwName(uint word) => word switch
    {
        >= 0x1f801040 and < 0x1f801050 => "SIO0",
        >= 0x1f801050 and < 0x1f801060 => "SIO1",
        >= 0x1f801800 and < 0x1f801804 => "CD-ROM",
        >= 0x1f801c00 and < 0x1f802000 => "SPU",
        >= 0x1f801078 and < 0x1f801080 => "IRQ",
        _ => "HWREG",
    };

    private static string HwHint(uint word) => word switch
    {
        >= 0x1f801040 and < 0x1f801050 => "SIO0 is not implemented (RecompOne Sio0 is a PS1 pad/memory-card model and is not valid here); map an IMmioDevice over 0x1f801040-0x1f80104f",
        >= 0x1f801050 and < 0x1f801060 => "SIO1 (JVS/serial) is not implemented; map an IMmioDevice over 0x1f801050-0x1f80105f",
        >= 0x1f801800 and < 0x1f801804 => "System 12 has no CD-ROM; the reference CPU leaves the CD handler unset (reads 0). The game should not touch it; investigate the calling code",
        >= 0x1f801c00 and < 0x1f802000 => "System 12 has no PS1 SPU (sound is the C352 on the sub-CPU board); the reference leaves the SPU handler unset (reads 0). Investigate the calling code",
        >= 0x1f801078 and < 0x1f801080 => "IRQ controller only defines I_STAT/I_MASK",
        _ => "no register is defined at this address by the reference CPU map",
    };

    // ------------------------------------------------------------------ cache model (active only while isolated)
    private uint CacheReadWord(uint offset)
    {
        uint data = 0;
        if ((_biu & BiuTag) != 0)
        {
            if ((_biu & BiuIs1) != 0)
            {
                var tag = _icacheTag[(int)((offset / 16) % (IcacheEntries / 4))];
                data |= tag & TagValid;
                if (((tag ^ offset) & TagMatchMask) == 0) data |= TagMatch;
            }
        }
        else if ((_biu & (BiuLock | BiuInv)) != 0)
        {
        }
        else
        {
            if ((_biu & BiuIs1) == BiuIs1) data |= _icache[(int)((offset / 4) % IcacheEntries)];
            if ((_biu & BiuDs) == BiuDs) data |= BinaryPrimitives.ReadUInt32LittleEndian(_scratch.AsSpan((int)((offset / 4) % DcacheEntries) * 4));
        }
        return data;
    }

    private void CacheWrite(uint offset, uint data)
    {
        if ((_biu & BiuTag) != 0)
        {
            if ((_biu & BiuIs1) != 0)
                _icacheTag[(int)((offset / 16) % (IcacheEntries / 4))] = (data & TagValid) | (offset & TagMatchMask);
        }
        else if ((_biu & (BiuLock | BiuInv)) != 0)
        {
            if ((_biu & BiuIs1) != 0)
                _icacheTag[(int)((offset / 16) % (IcacheEntries / 4))] = offset & TagMatchMask;
        }
        else
        {
            if ((_biu & BiuIs1) != 0) _icache[(int)((offset / 4) % IcacheEntries)] = data;
            if ((_biu & BiuDs) != 0) BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan((int)((offset / 4) % DcacheEntries) * 4), data);
        }
    }

    private uint CacheRead(uint a, int w) => w switch
    {
        1 => (byte)(CacheReadWord(a) >> (int)((a & 3) * 8)),
        2 => (ushort)(CacheReadWord(a) >> (int)((a & 2) * 8)),
        _ => CacheReadWord(a),
    };

    // ------------------------------------------------------------------ errors / policy
    private uint BusErrorRead(uint a, int w, string reason)
    {
        var ex = new BusErrorException(a, false, w, reason);
        Annotate(ex);
        if (!ReportOrThrow(ex)) return 0;
        throw ex;
    }

    private void BusErrorWrite(uint a, int w, uint v, string reason)
    {
        var ex = new BusErrorException(a, true, w, reason + $" (value 0x{v:x})");
        Annotate(ex);
        if (!ReportOrThrow(ex)) return;
        throw ex;
    }

    private NotImplementedMmioException NotImpl(string device, bool write, uint addr, int w, uint v, string detail)
    {
        var ex = new NotImplementedMmioException(device, write, addr & 0x1fffffffu, w, v, detail);
        Annotate(ex);
        Trace?.OnNotImplemented(device, write, addr & 0x1fffffffu, w, v, detail);
        return ex;
    }

    /// <summary>Exception filter for device exceptions: annotate with the guest function; true = swallow (LogAndContinue).</summary>
    private bool OnDeviceNotImpl(NotImplementedMmioException e)
    {
        e.GuestFunction ??= FindGuestFunction();
        return ReportOrThrow(e) == false;
    }

    /// <summary>Returns true when the caller must throw; false when it was logged under LogAndContinue.</summary>
    private bool ReportOrThrow(Exception e)
    {
        if (Policy == UnimplementedPolicy.Throw) return true;
        var key = e switch
        {
            NotImplementedMmioException n => $"{n.Device}|{(n.IsWrite ? "W" : "R")}{n.Width * 8}|0x{n.Physical:x8}",
            BusErrorException b => $"BUSERR|{(b.IsWrite ? "W" : "R")}{b.Width * 8}|0x{b.Address:x8}",
            _ => e.GetType().Name,
        };
        _hits.TryGetValue(key, out var n0);
        _hits[key] = n0 + 1;
        if (n0 == 0) Log(e.Message);
        return false;
    }

    private T Annotate<T>(T e) where T : Exception
    {
        switch (e)
        {
            case NotImplementedMmioException n: n.GuestFunction ??= FindGuestFunction(); break;
            case BusErrorException b: b.GuestFunction ??= FindGuestFunction(); break;
            case AddressErrorException x: x.GuestFunction ??= FindGuestFunction(); break;
        }
        return e;
    }

    /// <summary>First recompiled-function frame (method name func_XXXXXXXX) on the managed stack, if any.</summary>
    internal static string? FindGuestFunction()
    {
        try
        {
            var st = new StackTrace(2, false);
            for (var i = 0; i < st.FrameCount && i < 64; i++)
            {
                var name = st.GetFrame(i)?.GetMethod()?.Name;
                if (name != null && name.StartsWith("func_", StringComparison.Ordinal)) return name;
            }
        }
        catch { /* diagnostics only */ }
        return null;
    }

    // ------------------------------------------------------------------ names of known arcade regions (for actionable messages)
    private static readonly (uint s, uint e, string name, string hint)[] KnownArcade =
    {
        (0x1f000000, 0x1f000004, "BankedRom.bankoffset", "namcos12 map: 0x1f000000 16-bit write = bankoffset_w (alt-bank ROM board); read is nop. Needs the BankedRomController device"),
        (0x1f008000, 0x1f008004, "NopWrite", "namcos12 map: nopw (write ignored by the reference). Needs the EepromIo/IO device to own it explicitly"),
        (0x1f010000, 0x1f010004, "NopWrite", "namcos12 map: nopw (write ignored by the reference). Needs the EepromIo/IO device to own it explicitly"),
        (0x1f018000, 0x1f018004, "NopWrite", "namcos12 map: nopw (write ignored by the reference). Needs the EepromIo/IO device to own it explicitly"),
        (0x1f080000, 0x1f084000, "SharedRam", "namcos12 map: 0x1f080000-0x1f083fff shared RAM with the H8/3002 sound CPU (16-bit lanes). Needs the sound SharedRam device"),
        (0x1f140000, 0x1f141000, "Eeprom", "namcos12 map: 0x1f140000-0x1f140fff AT28C16 EEPROM, umask32 0x00ff00ff. Needs the EepromIo device"),
        (0x1f1bff08, 0x1f1bff10, "NopWrite", "namcos12 map: nopw '??'. Needs the EepromIo/IO device to own it explicitly"),
        (0x1f700000, 0x1f710000, "BankedRom.dmaoffset", "namcos12 map: 0x1f700000-0x1f70ffff write = dmaoffset_w; tektagt: 0x1f700000 read = protection_3_r (its mirror(0x800000) lands at 0x1ff00000, inside the main-ROM window, where the CPU's own ROM/bus-error mapping wins). Needs the BankedRomController device"),
        (0x1fa00000, 0x1fc00000, "BankedRom", "namcos12 map: 0x1fa00000-0x1fbfffff bank-switched ROM; tektagt: 0x1fb00000 protection_1 r/w, 0x1fb80000 protection_2 r/w. Needs the BankedRomController device"),
    };

    private static string ArcadeRegionName(uint phys, out string hint)
    {
        foreach (var (s, e, n, h) in KnownArcade)
            if (phys >= s && phys < e) { hint = h; return n; }
        hint = phys >= 0x1f000000 && phys < 0x1f800000
            ? "expansion-1 arcade MMIO window with no device mapped here; the reference namcos12 map has no entry at this address either - verify the address in reports/refs/namcos12.cpp"
            : "no region is mapped at this physical address";
        return "UNMAPPED";
    }
}
