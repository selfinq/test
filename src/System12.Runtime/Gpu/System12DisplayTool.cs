// Offline tool entry (called from tools/run_selftest.sh when S12_RUN_ARGS is set): converts a raw 1024x1024 VRAM dump
// produced by mame/vram_dump.lua into an RGBA image through SoftwareGpuBackend.ReadDisplayRgba, so that it can be
// compared with the MAME snapshot (tools/compare_display.py).
//   args: <vram.bin> <startX> <startY> <widthPixels> <heightLines> <rgb24:0|1> <out.rgba>
namespace System12.Runtime.Gpu;

public static class System12DisplayTool
{
    public static int Run(string[] a)
    {
        if (a.Length != 7)
        {
            Console.Error.WriteLine("usage: <vram.bin> <startX> <startY> <w> <h> <rgb24 0|1> <out.rgba>");
            return 2;
        }
        var raw = File.ReadAllBytes(a[0]);
        if (raw.Length != SoftwareGpuBackend.Width * SoftwareGpuBackend.Height * 2)
        {
            Console.Error.WriteLine($"unexpected VRAM dump size {raw.Length}");
            return 2;
        }
        var be = new SoftwareGpuBackend();
        Buffer.BlockCopy(raw, 0, be.Vram, 0, raw.Length); // little endian host == MAME uint16 layout
        var rgba = be.ReadDisplayRgba(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), a[5] == "1");
        File.WriteAllBytes(a[6], rgba);
        return 0;
    }
}
