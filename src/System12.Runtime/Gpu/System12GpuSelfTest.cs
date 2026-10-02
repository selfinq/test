// Console self test for the System 12 GPU adapter. No Main here (the owning csproj is someone else's);
// run it through Gpu/tools/run_selftest.sh, which builds a throw-away console project around these sources.
//
// What is validated (synthetic input only - no game data, no MAME comparison of pixels):
//   CORE   : RecompOne Gpu front end + SoftwareGpuBackend + RecompOne Dma channel 2 (linked list, block write,
//            VRAM read) produce the expected VRAM pixels for GP0 fill / flat triangle / gouraud triangle / image load.
//   MODE   : GP1(08) decoding and RecompOne's DisplayWidth/Height agree with the display modes MAME 0.264 reported
//            for tektagt (values taken from reports/agent06 hw_usage probe).
//   ARCADE : VRAM rows >= 512 (fill, load, textured rect with tpage Y base 512).  These only pass on a RecompOne built
//            with the patch from patches/0004-gpu-vram.md; on an unpatched build they are reported XFAIL (expected failure).

using RecompOne.Runtime;
using RecompOne.Runtime.Memory;
using RcGpu = RecompOne.Runtime.Gpu;

namespace System12.Runtime.Gpu;

public static class System12GpuSelfTest
{
    private sealed class Results
    {
        public int Pass, Fail, XFail, XPass;
        public readonly TextWriter Log;
        public Results(TextWriter log) { Log = log; }

        public void Check(string kind, string name, bool ok, string detail = "")
        {
            var arcadeUnsupported = kind == "ARCADE" && !System12GpuAdapter.ArcadeVramSupported;
            string tag;
            if (arcadeUnsupported) { tag = ok ? "XPASS" : "XFAIL"; if (ok) XPass++; else XFail++; }
            else if (ok) { tag = "PASS"; Pass++; }
            else { tag = "FAIL"; Fail++; }
            Log.WriteLine($"[{tag,-5}] {kind,-6} {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
        }
    }

    // ---- GP0 word helpers
    private static uint Xy(int x, int y) => (uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16);
    private static uint Bgr(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));

    private static void Send(RcGpu g, params uint[] words) { foreach (var w in words) g.WriteGp0(w); }

    private static void SetupDrawArea(RcGpu g, int x1, int y1, int x2, int y2)
    {
        g.WriteGp0(0xE3000000u | (uint)x1 | ((uint)y1 << 10));
        g.WriteGp0(0xE4000000u | (uint)x2 | ((uint)y2 << 10));
        g.WriteGp0(0xE5000000u); // draw offset 0,0
    }

    private sealed class StubMem : IMemory
    {
        public readonly byte[] Ram = new byte[2 * 1024 * 1024];
        public uint ReadU32(uint a) => BitConverter.ToUInt32(Ram, (int)(a & 0x1FFFFC));
        public void WriteU32(uint a, uint v) => BitConverter.TryWriteBytes(Ram.AsSpan((int)(a & 0x1FFFFC)), v);
        public byte ReadU8(uint a) => Ram[a & 0x1FFFFF];
        public ushort ReadU16(uint a) => BitConverter.ToUInt16(Ram, (int)(a & 0x1FFFFE));
        public void WriteU8(uint a, byte v) => Ram[a & 0x1FFFFF] = v;
        public void WriteU16(uint a, ushort v) => BitConverter.TryWriteBytes(Ram.AsSpan((int)(a & 0x1FFFFE)), v);
        public uint ReadWordLeft(uint c, uint a) => throw new NotSupportedException();
        public uint ReadWordRight(uint c, uint a) => throw new NotSupportedException();
        public void WriteWordLeft(uint a, uint v) => throw new NotSupportedException();
        public void WriteWordRight(uint a, uint v) => throw new NotSupportedException();
        public void LoadBytes(uint a, byte[] d) => d.CopyTo(Ram, (int)a);
        public void ZeroRange(uint a, uint l) => Array.Clear(Ram, (int)a, (int)l);
        public void SetCd(RecompOne.Runtime.Cdrom.CdController cd) { }
    }

    /// <returns>number of hard failures (0 = all required checks passed)</returns>
    public static int Run(TextWriter log)
    {
        var r = new Results(log);
        log.WriteLine(System12GpuAdapter.DescribeCompatibility());

        CoreTests(r);
        DmaTests(r);
        ModeTests(r);
        ArcadeRowTests(r);

        log.WriteLine($"SUMMARY pass={r.Pass} fail={r.Fail} xfail={r.XFail} xpass={r.XPass}  (arcade VRAM supported by linked RecompOne: {System12GpuAdapter.ArcadeVramSupported})");
        return r.Fail;
    }

    // ------------------------------------------------------------------------------------------------ CORE
    private static void CoreTests(Results t)
    {
        var a = System12GpuAdapter.Create();
        var g = a.Gpu;
        var vram = a.Backend!;

        // 1. fill rect GP0(02): red 32x16 at (16,32)
        Send(g, 0x02000000u | Bgr(255, 0, 0), Xy(16, 32), Xy(32, 16));
        t.Check("CORE", "fill: inside pixel (16,32) is red 0x001F", vram[16, 32] == 0x001F, $"got 0x{vram[16, 32]:X4}");
        t.Check("CORE", "fill: last pixel (47,47) is red", vram[47, 47] == 0x001F, $"got 0x{vram[47, 47]:X4}");
        t.Check("CORE", "fill: outside (48,32) untouched", vram[48, 32] == 0 && vram[16, 31] == 0 && vram[16, 48] == 0);
        t.Check("CORE", "fill: RecompOne shadow VRAM agrees with backend", g.Vram[32 * 1024 + 16] == 0x001F);

        // 2. flat triangle GP0(20) green (100,100) (140,100) (100,140)
        SetupDrawArea(g, 0, 0, 1023, 511);
        Send(g, 0x20000000u | Bgr(0, 255, 0), Xy(100, 100), Xy(140, 100), Xy(100, 140));
        var filled = 0;
        for (var y = 90; y < 150; y++)
        for (var x = 90; x < 150; x++)
            if (vram[x, y] == 0x03E0) filled++;
        t.Check("CORE", "flat triangle: interior (110,110) green 0x03E0", vram[110, 110] == 0x03E0, $"got 0x{vram[110, 110]:X4}");
        t.Check("CORE", "flat triangle: outside hypotenuse (135,135) untouched", vram[135, 135] == 0);
        t.Check("CORE", "flat triangle: area ~ 40*40/2=800 px", filled is >= 760 and <= 860, $"filled={filled}");
        t.Check("CORE", "flat triangle: nothing outside bbox (99,99) / (141,100)", vram[99, 99] == 0 && vram[141, 100] == 0);

        // 3. gouraud triangle GP0(30): red / green / blue corners, check centre mixes all three
        Send(g, 0x30000000u | Bgr(255, 0, 0), Xy(200, 100), Bgr(0, 255, 0), Xy(260, 100), Bgr(0, 0, 255), Xy(200, 160));
        var c = vram[215, 115];
        int cr = c & 31, cg = (c >> 5) & 31, cb = (c >> 10) & 31;
        t.Check("CORE", "gouraud triangle: interior has all channels > 0", cr > 0 && cg > 0 && cb > 0, $"rgb5=({cr},{cg},{cb})");
        t.Check("CORE", "gouraud triangle: corner (200,100) is red-dominant", (vram[200, 100] & 31) > ((vram[200, 100] >> 5) & 31));

        // 4. variable-size flat rectangle GP0(60)
        Send(g, 0x60000000u | Bgr(0, 0, 255), Xy(300, 50), Xy(10, 6));
        t.Check("CORE", "rect 10x6 blue: corners", vram[300, 50] == 0x7C00 && vram[309, 55] == 0x7C00 && vram[310, 50] == 0 && vram[300, 56] == 0);

        // 5. image load GP0(A0) then read GP0(C0) round trip
        Send(g, 0xA0000000u, Xy(512, 300), Xy(4, 2), 0x22221111u, 0x44443333u, 0x66665555u, 0x88887777u);
        t.Check("CORE", "image load 4x2 at (512,300)", vram[512, 300] == 0x1111 && vram[515, 301] == 0x8888 && vram[513, 301] == 0x6666,
            $"got {vram[512, 300]:X4} {vram[513, 301]:X4} {vram[515, 301]:X4}");
        Send(g, 0xC0000000u, Xy(512, 300), Xy(4, 2));
        var rb = new uint[4];
        for (var i = 0; i < 4; i++) rb[i] = g.ReadData();
        t.Check("CORE", "image read-back via GP0(C0)/GPUREAD equals uploaded words",
            rb[0] == 0x22221111u && rb[1] == 0x44443333u && rb[2] == 0x66665555u && rb[3] == 0x88887777u,
            $"{rb[0]:X8} {rb[1]:X8} {rb[2]:X8} {rb[3]:X8}");

        // 6. VRAM->VRAM copy GP0(80)
        Send(g, 0x80000000u, Xy(512, 300), Xy(600, 400), Xy(4, 2));
        t.Check("CORE", "vram copy (512,300)->(600,400)", vram[600, 400] == 0x1111 && vram[603, 401] == 0x8888);

        // 7. 15-bit textured rect with CLUT-less 15 bit texture, modulation by 0x80 is identity
        Send(g, 0xA0000000u, Xy(0, 0), Xy(8, 8));
        for (var i = 0; i < 32; i++) g.WriteGp0(0x7C007C00u); // 8x8 px of blue (64 px = 32 words)
        Send(g, 0xE1000000u | 0x100u);                         // tpage x=0 y=0, 15 bit
        Send(g, 0x64000000u | Bgr(0x80, 0x80, 0x80), Xy(400, 40), 0u /* u=0 v=0 clut=0 */, Xy(8, 8));
        t.Check("CORE", "textured rect (15 bit, tpage 0,0): pixel = texel", vram[400, 40] == 0x7C00 && vram[407, 47] == 0x7C00,
            $"got {vram[400, 40]:X4}");

        // 8. 4-bit CLUT texture
        // texture page x=64: one halfword holding 4 texels (nibbles low->high): 1,2,0,0
        Send(g, 0xA0000000u, Xy(64, 0), Xy(2, 1), 0x00000021u);
        // CLUT at (0,20): entry0=0, entry1=0x03E0 (green), entry2=0x7C00 (blue), entry3=0
        Send(g, 0xA0000000u, Xy(0, 20), Xy(4, 1), 0x03E00000u, 0x00007C00u);
        // clut word: x=0 (>>0), y=20 -> clut = (20<<6) | 0
        var clut = (uint)(20 << 6);
        Send(g, 0xE1000000u | 0x1u /* tpage x = 64 */ | 0x0u /* 4 bit */);
        Send(g, 0x64000000u | Bgr(0x80, 0x80, 0x80), Xy(500, 40), (clut << 16) | 0u, Xy(2, 1));
        t.Check("CORE", "textured rect (4 bit CLUT): texel0 -> clut[1] green, texel1 -> clut[2] blue",
            vram[500, 40] == 0x03E0 && vram[501, 40] == 0x7C00, $"got {vram[500, 40]:X4} {vram[501, 40]:X4}");

        // 9. semi-transparent rect (mode 1: B+F) over green
        Send(g, 0xE1000000u | (1u << 5));
        Send(g, 0x62000000u | Bgr(0, 0, 80), Xy(110, 110), Xy(2, 2)); // add blue 10/31 on top of the green triangle (0x03E0)
        t.Check("CORE", "semi-transparent add: green + blue(10) = 0x2BE0", vram[110, 110] == (0x03E0 | (10 << 10)), $"got {vram[110, 110]:X4}");
    }

    // ------------------------------------------------------------------------------------------------ DMA
    private static void DmaTests(Results t)
    {
        var a = System12GpuAdapter.Create();
        var g = a.Gpu;
        var vram = a.Backend!;
        var mem = new StubMem();
        var irqs = 0;
        var dma = new Dma(mem, g, null!, new Mdec(), () => irqs++);

        // linked list at 0x1000: node0 (fill red 8x8 @ (0,0) is 16-aligned x) -> node1 (flat triangle) -> end
        void W(uint addr, uint v) => mem.WriteU32(addr, v);
        SetupDrawArea(g, 0, 0, 1023, 511);
        W(0x1000, (3u << 24) | 0x1100u); W(0x1004, 0x02000000u | Bgr(255, 0, 0)); W(0x1008, Xy(64, 64)); W(0x100C, Xy(16, 16));
        W(0x1100, (4u << 24) | 0x00FFFFFFu);
        W(0x1104, 0x20000000u | Bgr(0, 255, 0)); W(0x1108, Xy(20, 100)); W(0x110C, Xy(60, 100)); W(0x1110, Xy(20, 140));
        dma.Run(2, 0x1000, 0, 0x01000401u);
        t.Check("CORE", "DMA2 linked list: fill executed from node0", vram[64, 64] == 0x001F && vram[79, 79] == 0x001F);
        t.Check("CORE", "DMA2 linked list: triangle executed from node1", vram[30, 110] == 0x03E0);

        // block write (sync mode 1): image upload through DMA, bcr = size | blocks<<16
        var words = new uint[] { 0xA0000000u, Xy(700, 300), Xy(4, 2), 0xAAAA1111u, 0xBBBB2222u, 0xCCCC3333u, 0xDDDD4444u };
        for (var i = 0; i < words.Length; i++) W(0x2000 + (uint)i * 4, words[i]);
        dma.Run(2, 0x2000, (uint)words.Length | (1u << 16), 0x01000201u);
        t.Check("CORE", "DMA2 block write: 4x2 image uploaded", vram[700, 300] == 0x1111 && vram[703, 301] == 0xDDDD && vram[701, 300] == 0xAAAA, $"{vram[700, 300]:X4} {vram[701, 300]:X4} {vram[703, 301]:X4}");

        // VRAM -> RAM (chcr 0x01000200): GP0(C0) first, then DMA read
        Send(g, 0xC0000000u, Xy(700, 300), Xy(4, 2));
        dma.Run(2, 0x3000, 4u | (1u << 16), 0x01000200u);
        t.Check("CORE", "DMA2 VRAM->RAM read matches upload",
            mem.ReadU32(0x3000) == 0xAAAA1111u && mem.ReadU32(0x3004) == 0xBBBB2222u && mem.ReadU32(0x300C) == 0xDDDD4444u,
            $"{mem.ReadU32(0x3000):X8} {mem.ReadU32(0x300C):X8}");
    }

    // ------------------------------------------------------------------------------------------------ display modes
    private static void ModeTests(Results t)
    {
        // Values observed in MAME 0.264 (tektagt, frames 1..7000), see docs/system12/GPU.md section "Observed modes".
        (string name, uint gp1_08, uint hs, uint he, uint vs, uint ve, int w, int h, bool rgb24)[] modes =
        {
            ("boot 256x240", 0x00, 0x26c, 0xc6c, 0x10, 0x100, 256, 240, false),
            ("640x240 early", 0x03, 0x28c, 0xc8c, 0x10, 0x100, 640, 240, false),
            ("game 512x240", 0x02, 0x28c, 0xc8c, 0x10, 0x100, 512, 240, false),
            ("FMV 256x240 24bit", 0x10, 0x26c, 0xc6c, 0x10, 0x100, 256, 240, true),
        };
        foreach (var m in modes)
        {
            var a = System12GpuAdapter.Create(installSoftwareBackend: false);
            a.WriteGp1(0x06000000u | m.hs | (m.he << 12));
            a.WriteGp1(0x07000000u | m.vs | (m.ve << 10));
            a.WriteGp1(0x08000000u | m.gp1_08);
            var vm = a.VideoMode;
            t.Check("MODE", $"{m.name}: adapter decode (MAME rules) = {m.w}x{m.h}", vm.Width == m.w && vm.Height == m.h && vm.Rgb24 == m.rgb24,
                $"got {vm.Width}x{vm.Height} rgb24={vm.Rgb24} {vm.RefreshHz:F4}Hz");
            t.Check("MODE", $"{m.name}: RecompOne Gpu.DisplayWidth/Height agree", a.Gpu.DisplayWidth == m.w && a.Gpu.DisplayHeight == m.h,
                $"RecompOne={a.Gpu.DisplayWidth}x{a.Gpu.DisplayHeight}");
            t.Check("MODE", $"{m.name}: RecompOne Display24Bit", a.Gpu.Display24Bit == m.rgb24);
        }
        // refresh table
        t.Check("MODE", "NTSC 240 line refresh = 59.826 (MAME f8f6b22 formula)", Math.Abs(ArcadeVideoMode.FromGp1Mode(0x02).RefreshHz - 59.8260978565) < 1e-9);
        t.Check("MODE", "table value matches MAME 0.264 measured frame rate (512x240) within 1e-5 Hz",
            Math.Abs(ArcadeVideoMode.FromGp1Mode(0x02).RefreshHz - ArcadeVideoMode.Mame0264MeasuredHz) < 1e-5);
        t.Check("MODE", "NTSC 480 line refresh = 59.940", Math.Abs(ArcadeVideoMode.FromGp1Mode(0x06).RefreshHz - 59.9400523286) < 1e-9);
    }

    // ------------------------------------------------------------------------------------------------ ARCADE rows
    private static void ArcadeRowTests(Results t)
    {
        var a = System12GpuAdapter.Create();
        var g = a.Gpu;
        var vram = a.Backend!;
        SetupDrawArea(g, 0, 0, 1023, 1023);

        // fill at y=600
        Send(g, 0x02000000u | Bgr(255, 0, 0), Xy(16, 600), Xy(16, 8));
        t.Check("ARCADE", "fill at y=600 lands on row 600", vram[16, 600] == 0x001F && vram[31, 607] == 0x001F,
            $"row600={vram[16, 600]:X4} row88={vram[16, 88]:X4}");

        // image load at y=700, read back
        Send(g, 0xA0000000u, Xy(40, 700), Xy(2, 1), 0x22221111u);
        Send(g, 0xC0000000u, Xy(40, 700), Xy(2, 1));
        var rb = g.ReadData();
        t.Check("ARCADE", "image load/read at y=700", vram[40, 700] == 0x1111 && rb == 0x22221111u, $"vram={vram[40, 700]:X4} rb={rb:X8}");

        // textured rect whose texture page lives at Y base 512 (GP0(E1) bit 11) - the thing MAME flagged in tektagt
        Send(g, 0xA0000000u, Xy(0, 0), Xy(8, 8));
        for (var i = 0; i < 32; i++) g.WriteGp0(0x001F001Fu);          // red texture at row 0 (decoy)
        Send(g, 0xA0000000u, Xy(0, 512), Xy(8, 8));
        for (var i = 0; i < 32; i++) g.WriteGp0(0x03E003E0u);          // green texture at row 512
        Send(g, 0xE1000000u | 0x800u | 0x100u);                         // tpage Y +512 (bit 11), 15 bit
        Send(g, 0x64000000u | Bgr(0x80, 0x80, 0x80), Xy(200, 200), 0u, Xy(8, 8));
        t.Check("ARCADE", "texture upload at y=512 does not overwrite row 0", vram[0, 0] == 0x001F && vram[0, 512] == 0x03E0,
            $"row0={vram[0, 0]:X4} row512={vram[0, 512]:X4}");
        t.Check("ARCADE", "textured rect with tpage Y base 512 samples green (not the red decoy at row 0)",
            vram[200, 200] == 0x03E0 && vram[0, 0] == 0x001F, $"got {vram[200, 200]:X4}, row0={vram[0, 0]:X4}");

        // Y base 768 = bit 4 (+256) | bit 11 (+512)
        Send(g, 0xA0000000u, Xy(0, 768), Xy(8, 8));
        for (var i = 0; i < 32; i++) g.WriteGp0(0x7C007C00u);          // blue at row 768
        Send(g, 0xE1000000u | 0x800u | 0x10u | 0x100u);                 // bit 11 + bit 4, 15 bit
        Send(g, 0x64000000u | Bgr(0x80, 0x80, 0x80), Xy(220, 200), 0u, Xy(8, 8));
        t.Check("ARCADE", "textured rect with tpage Y base 768 (bits 4+11) samples blue", vram[220, 200] == 0x7C00, $"got {vram[220, 200]:X4}");
        t.Check("ARCADE", "GPUSTAT shows tpage Y bit4 and bit15 (MAME type-2 status)", (g.ReadStat() & 0x8010u) == 0x8010u, $"stat={g.ReadStat():X8}");

        // display start Y >= 512 (FMV double buffer in MAME probe: n_displaystarty = 0x200 / 0x2f0)
        g.WriteGp1(0x05000000u | (0u) | (0x2F0u << 10));
        t.Check("ARCADE", "GP1(05) display start Y 0x2f0 kept", g.DisplayY == 0x2F0, $"got 0x{g.DisplayY:X}");
    }
}
