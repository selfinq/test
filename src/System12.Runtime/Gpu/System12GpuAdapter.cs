// System 12 GPU adapter (new code, written against RecompOne commit 8d0091c; MIT-compatible).
//
// RecompOne's Gpu front end (GP0/GP1 decode, DMA feed, PGXP hooks) is reused unchanged where possible.  This file
// holds (1) the System 12 facts that were established from MAME (namcos12.cpp / psx.cpp at f8f6b22 and MAME 0.264
// runtime probes, see docs/system12/GPU.md), (2) a capability check that tells you honestly whether the linked
// RecompOne build can address the arcade's 2 MB VRAM, and (3) wiring helpers (software backend, video mode).
//
// WHAT THIS CANNOT DO: RecompOne declares VramShadow.Height = 512 as a compile-time `const` and the GP0/GP1 decoders
// mask Y with 0x1FF.  A runtime adapter cannot change that.  `System12GpuAdapter.Create()` therefore reports
// `ArcadeVramSupported == false` on an unpatched RecompOne, and rows >= 512 are unreachable through the Gpu front end.
// patches/0004-gpu-vram.md describes the minimal fork (a compile-time define) that fixes this.

using RecompOne.Runtime.Hle;
using RcGpu = RecompOne.Runtime.Gpu;

namespace System12.Runtime.Gpu;

/// <summary>Facts about the System 12 video hardware, each tagged with where it comes from.</summary>
public static class System12GpuProfile
{
    /// <summary>namcos12.cpp: CXD8654Q on the COH-700 CPU board (tektagt = coh700b). COH-716 uses CXD8561CQ.</summary>
    public const string GpuChip = "Sony CXD8654Q (COH-700 board)";

    /// <summary>namcos12.cpp: RAM(config, m_gpu_ram).set_bits(16).set_default_size("2M")  /  2x KM4132G271Q SGRAM.</summary>
    public const int VramBytes = 2 * 1024 * 1024;
    public const int VramWidth = 1024;
    public const int VramHeight = VramBytes / (VramWidth * 2); // 1024 rows

    /// <summary>namcos12.cpp: m_gpu->set_vclkn(53.693175_MHz_XTAL); GPU clock = 100 MHz / 2.</summary>
    public const double VclkNHz = 53_693_175.0;

    /// <summary>
    /// MAME psx.cpp (f8f6b22) device_start: every GPU except CXD8538Q uses "type 2" register layouts:
    /// draw area Y @bit10 (10 bit), draw offset Y @bit11 (11 bit), display start Y @bit10 (10 bit),
    /// GP0(E1) bit 11 = texture page Y bit 9 (+512), bit 4 = +256, bits 12/13 = rectangle X/Y flip.
    /// </summary>
    public const int MameGpuType = 2;

    /// <summary>GP1(10h) index 7 response in MAME for CXD8654Q (n_gpuinfo = m_n_gputype).</summary>
    public const uint GpuInfoType = 2;
}

/// <summary>Display timing for a GP1(08h) display-mode word, following MAME psx.cpp updatevisiblearea().</summary>
public readonly record struct ArcadeVideoMode(int Width, int Height, double RefreshHz, bool Pal, bool Rgb24, bool Interlaced)
{
    /// <summary>NTSC 240-line refresh in MAME f8f6b22 (53.693175 MHz derived).</summary>
    public const double Ntsc240Hz = 59.8260978565;
    /// <summary>NTSC 480-line (interlaced field pair) refresh in MAME f8f6b22.</summary>
    public const double Ntsc480Hz = 59.9400523286;
    public const double PalHz = 50.0;
    /// <summary>
    /// MAME 0.264, tektagt, 512x240 NTSC mode, measured from emulated time over 600 frames (frames 600..1200 of a
    /// fresh boot): 10.029068 s => 59.826098 Hz. (The Lua screen.refresh property still prints 60.0000, which is only
    /// the configured default, not the real frame rate.)
    /// </summary>
    public const double Mame0264MeasuredHz = 59.826098;

    /// <summary>
    /// Decode the low byte of a GP1(08h) command exactly as MAME does:
    /// bits0-1 width, bit2 480 lines, bit3 PAL, bit4 24 bit, bit5 interlace, bit6 width368.
    /// </summary>
    public static ArcadeVideoMode FromGp1Mode(uint gp1_08)
    {
        var hres = (int)(gp1_08 & 3);
        var h368 = (gp1_08 & 0x40) != 0;
        var tall = (gp1_08 & 4) != 0;
        var pal = (gp1_08 & 8) != 0;
        var rgb24 = (gp1_08 & 0x10) != 0;
        var inter = (gp1_08 & 0x20) != 0;

        int width = hres switch
        {
            0 => h368 ? 368 : 256,
            1 => h368 ? 384 : 320,
            2 => 512,
            _ => 640
        };
        int height;
        double hz;
        if (pal)
        {
            height = tall ? 512 : 256;
            hz = PalHz;
        }
        else
        {
            height = tall ? 480 : 240;
            hz = tall ? Ntsc480Hz : Ntsc240Hz;
        }
        return new ArcadeVideoMode(width, height, hz, pal, rgb24, inter);
    }

    /// <summary>NTSC 240-line frame = 263 lines of 3412.5 vclk cycles (53.693175 MHz): derived here, reproduces MAME's 59.8260978565 Hz to 10 digits.</summary>
    public const int Ntsc240LinesPerFrame = 263;
    public double FramePeriodMs => 1000.0 / RefreshHz;
}

/// <summary>
/// Thin owner of a RecompOne <see cref="RcGpu"/> configured for System 12 where RecompOne allows it.
/// </summary>
public sealed class System12GpuAdapter
{
    /// <summary>True only when the linked RecompOne VramShadow is 1024 rows high (patch 0004 applied).</summary>
    public static bool ArcadeVramSupported => VramShadow.Height >= System12GpuProfile.VramHeight;

    public RcGpu Gpu { get; }
    public SoftwareGpuBackend? Backend { get; }
    public ArcadeVideoMode VideoMode { get; private set; } = ArcadeVideoMode.FromGp1Mode(0);

    private System12GpuAdapter(RcGpu gpu, SoftwareGpuBackend? backend)
    {
        Gpu = gpu;
        Backend = backend;
    }

    /// <summary>
    /// Create a Gpu front end (<paramref name="gpu"/> = an existing instance, e.g. Runtime.Gpu, or null to create one) and
    /// optionally install <see cref="SoftwareGpuBackend"/> as RecompOne's global HLE backend.
    /// WARNING: GpuHle.Backend/GpuHle.Active are process-wide statics in RecompOne.
    /// </summary>
    public static System12GpuAdapter Create(RcGpu? gpu = null, bool installSoftwareBackend = true)
    {
        gpu ??= new RcGpu();
        SoftwareGpuBackend? be = null;
        if (installSoftwareBackend)
        {
            be = new SoftwareGpuBackend();
            GpuHle.Backend = be;
            GpuHle.Active = true;
        }
        return new System12GpuAdapter(gpu, be);
    }

    /// <summary>Human-readable compatibility statement for logs and the self test.</summary>
    public static string DescribeCompatibility()
    {
        return ArcadeVramSupported
            ? $"RecompOne VramShadow is {VramShadow.Width}x{VramShadow.Height}: arcade 2 MB VRAM addressable."
            : $"RecompOne VramShadow is {VramShadow.Width}x{VramShadow.Height} (const): arcade VRAM rows 512..1023 are NOT reachable " +
              "through RcGpu. Apply patches/0004-gpu-vram.md.";
    }

    // Pass-through helpers: the future MMIO layer should route 0x1f801810/14 through these so that the adapter sees
    // the display mode.  (RecompOne keeps the mode in private fields.)
    public void WriteGp0(uint word) => Gpu.WriteGp0(word);

    public void WriteGp1(uint word)
    {
        if ((word >> 24) == 0x08) VideoMode = ArcadeVideoMode.FromGp1Mode(word & 0xFF);
        Gpu.WriteGp1(word);
    }

    public uint ReadStat() => Gpu.ReadStat();
    public uint ReadData() => Gpu.ReadData();

}
