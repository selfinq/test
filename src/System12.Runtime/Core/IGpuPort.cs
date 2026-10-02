// System12.Runtime - Core. Original code for this project (not derived from RecompOne).
namespace System12.Runtime.Core;

/// <summary>
/// The four GPU ports at physical 0x1f801810 (GP0 write / GPUREAD) and 0x1f801814 (GP1 write / GPUSTAT).
/// The Gpu agent provides the real implementation (CXD8654Q has 2 MB VRAM on System 12).
/// DMA channel 2 drives the GPU through this interface.
/// </summary>
public interface IGpuPort
{
    uint ReadData();
    uint ReadStat();
    void WriteGp0(uint word);
    void WriteGp1(uint word);
}

/// <summary>Default GPU port: every access throws NOT IMPLEMENTED with the instruction how to plug a GPU in.</summary>
public sealed class NotImplementedGpuPort : IGpuPort
{
    private static Exception E(bool w, uint phys, uint v) => new NotImplementedMmioException("GPU", w, phys, 4, v,
        "no IGpuPort installed; assign System12Memory.Gpu = <your IGpuPort> (see Core/IGpuPort.cs, RecompOneGpuPort)");
    public uint ReadData() => throw E(false, 0x1f801810, 0);
    public uint ReadStat() => throw E(false, 0x1f801814, 0);
    public void WriteGp0(uint word) => throw E(true, 0x1f801810, word);
    public void WriteGp1(uint word) => throw E(true, 0x1f801814, word);
}

/// <summary>
/// Adapter to RecompOne's PS1 GPU (RecompOne.Runtime.Gpu, MIT). Note: that GPU models a 1 MB PS1 VRAM and a
/// PS1 renderer; it is a bring-up convenience, not a validated System 12 GPU (see report).
/// </summary>
public sealed class RecompOneGpuPort(RecompOne.Runtime.Gpu gpu) : IGpuPort
{
    public uint ReadData() => gpu.ReadData();
    public uint ReadStat() => gpu.ReadStat();
    public void WriteGp0(uint word) => gpu.WriteGp0(word);
    public void WriteGp1(uint word) => gpu.WriteGp1(word);
}
