using System12.Runtime.Core;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Xunit;

namespace System12.Runtime.Tests;

/// <summary>Helpers shared by tests: a synthetic ROM image (no game data) and a bus brought to "boot-code finished" state.</summary>
internal static class T
{
    /// <summary>Synthetic ROM: word i (at byte 4*i) = 0xC0DE0000 ^ (i * 0x9E3779B1), so every word is distinct.</summary>
    public static byte[] Rom(int bytes)
    {
        var r = new byte[bytes];
        for (var i = 0; i < bytes / 4; i++)
            BitConverter.TryWriteBytes(r.AsSpan(i * 4), RomWord(i));
        return r;
    }

    public static uint RomWord(int i) => 0xC0DE0000u ^ ((uint)i * 0x9E3779B1u);

    /// <summary>What the System 12 boot code is expected to do: RAM_SIZE nibble 3 (4 MB), ROM config 4 MB window, scratchpad on.</summary>
    public static System12Memory Booted(int romBytes = 0x10000)
    {
        var m = new System12Memory();
        m.LoadMainRom(Rom(romBytes));
        m.WriteU32(0xbf801060, 0x00000300);   // RAM_SIZE: window 4 MB
        m.WriteU32(0xbf801010, 0x00160000);   // ROM config: 1<<22 = 4 MB window
        m.WriteU32(0xfffe0130, 0x00000088);   // BIU: RAM | DS -> scratchpad enabled
        return m;
    }
}

public class RamAndSegmentTests
{
    [Fact]
    public void Reset_RamWindowIs2MB_UntilRamSizeProgrammed()
    {
        var m = new System12Memory();
        Assert.Equal(0x200000u, m.RamWindow);
        m.WriteU32(0x80100000, 0x11223344);
        Assert.Equal(0x11223344u, m.ReadU32(0x00100000));
        // 2..4 MB is not visible yet: boot code has to program 0x1f801060 first
        var e = Assert.Throws<BusErrorException>(() => m.ReadU32(0x80200000));
        Assert.Contains("RAM_SIZE", e.Message);
        Assert.Throws<BusErrorException>(() => m.WriteU32(0x80200000, 1));
    }

    [Fact]
    public void RamSize_Nibble3_Gives4MB_AndNoMirrorBeyond()
    {
        var m = new System12Memory();
        m.WriteU32(0x1f801060, 0x300);
        Assert.Equal(0x400000u, m.RamWindow);
        m.WriteU32(0x803ffffc, 0xdeadbeef);
        Assert.Equal(0xdeadbeefu, m.ReadU32(0x003ffffc));
        Assert.Throws<BusErrorException>(() => m.ReadU32(0x80400000));
    }

    [Fact]
    public void RamSize_NibbleF_16MBWindow_MirrorsRam()
    {
        var m = new System12Memory(); // 4 MB RAM
        m.WriteU32(0x1f801060, 0xf00);
        Assert.Equal(0x1000000u, m.RamWindow);
        m.WriteU32(0x80000010, 0xcafef00d);
        Assert.Equal(0xcafef00du, m.ReadU32(0x80400010));
        Assert.Equal(0xcafef00du, m.ReadU32(0xa0c00010));
        Assert.Throws<BusErrorException>(() => m.ReadU32(0x81000000));
    }

    [Fact]
    public void Kuseg_Kseg0_Kseg1_AliasSamePhysicalRam()
    {
        var m = T.Booted();
        m.WriteU32(0x00001000, 0xa5a5a5a5);
        Assert.Equal(0xa5a5a5a5u, m.ReadU32(0x80001000));
        Assert.Equal(0xa5a5a5a5u, m.ReadU32(0xa0001000));
        m.WriteU8(0xa0001001, 0x12);
        Assert.Equal(0xa5a512a5u, m.ReadU32(0x80001000));
        m.WriteU16(0x80001002, 0x3456);
        Assert.Equal(0x3456u, m.ReadU16(0x00001002));
        Assert.Equal(0x56u, m.ReadU8(0xa0001002));   // little endian
        Assert.Equal(0x34u, m.ReadU8(0xa0001003));
    }

    [Theory]
    [InlineData(0x20000000u)]
    [InlineData(0x7ffffff0u)]
    [InlineData(0xc0000000u)]
    [InlineData(0xfffdfff0u)]
    public void AddressesOutsideSegments_AreBusErrors(uint addr)
    {
        var m = T.Booted();
        Assert.Throws<BusErrorException>(() => m.ReadU32(addr));
        Assert.Throws<BusErrorException>(() => m.WriteU32(addr, 0));
    }

    [Fact]
    public void Misaligned16And32_ThrowAddressError_NotSilent()
    {
        var m = T.Booted();
        Assert.Throws<AddressErrorException>(() => m.ReadU32(0x80000002));
        Assert.Throws<AddressErrorException>(() => m.ReadU16(0x80000001));
        Assert.Throws<AddressErrorException>(() => m.WriteU32(0x80000001, 0));
        Assert.Throws<AddressErrorException>(() => m.WriteU16(0x80000003, 0));
        m.WriteU8(0x80000003, 7);   // bytes are always fine
    }

    [Fact]
    public void LoadBytes_And_ZeroRange_MatchByteWiseWrites()
    {
        var m = T.Booted();
        var data = Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 1)).ToArray();
        m.LoadBytes(0x80010001, data);
        for (var i = 0; i < data.Length; i++) Assert.Equal(data[i], m.ReadU8(0xa0010001 + (uint)i));
        m.ZeroRange(0x80010001, 300);
        for (var i = 0; i < 300; i++) Assert.Equal(0, m.ReadU8(0x80010001 + (uint)i));
        // crossing the end of the visible window must fail loudly, not wrap
        Assert.Throws<BusErrorException>(() => m.LoadBytes(0x803fffff, new byte[4]));
    }

    [Fact]
    public void Ctor_RejectsBadRamSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new System12Memory(0x300000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new System12Memory(0x20000000));
        _ = new System12Memory(0x1000000); // COH-716 size
    }

    [Fact]
    public void SetCd_IsNotImplemented()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.SetCd(null!));
        Assert.Contains("NOT IMPLEMENTED", e.Message);
    }
}

public class UnalignedOpsTests
{
    // Bytewise reference of the MIPS I little-endian unaligned instructions.
    private static uint Lwl(uint reg, byte[] mem, uint a)
    {
        // loads bytes a, a-1, ... down to the word boundary into the top bytes of reg
        var n = (int)(a & 3) + 1;
        var r = reg;
        for (var i = 0; i < n; i++)
        {
            var dst = 3 - i;
            var src = a - (uint)i;
            r = (r & ~(0xffu << (dst * 8))) | ((uint)mem[src] << (dst * 8));
        }
        return r;
    }

    private static uint Lwr(uint reg, byte[] mem, uint a)
    {
        var n = 4 - (int)(a & 3);
        var r = reg;
        for (var i = 0; i < n; i++)
            r = (r & ~(0xffu << (i * 8))) | ((uint)mem[a + (uint)i] << (i * 8));
        return r;
    }

    private static void Swl(byte[] mem, uint a, uint v)
    {
        var n = (int)(a & 3) + 1;
        for (var i = 0; i < n; i++) mem[a - (uint)i] = (byte)(v >> ((3 - i) * 8));
    }

    private static void Swr(byte[] mem, uint a, uint v)
    {
        var n = 4 - (int)(a & 3);
        for (var i = 0; i < n; i++) mem[a + (uint)i] = (byte)(v >> (i * 8));
    }

    [Fact]
    public void Lwl_Lwr_Swl_Swr_MatchBytewiseModel_AllAlignments()
    {
        var rnd = new Random(1234);
        for (var iter = 0; iter < 200; iter++)
        {
            var m = T.Booted();
            var model = new byte[64];
            rnd.NextBytes(model);
            m.LoadBytes(0x80000100, model);
            for (uint off = 8; off < 12; off++)
            {
                var cur = (uint)rnd.NextInt64();
                Assert.Equal(Lwl(cur, model, off), m.ReadWordLeft(cur, 0x80000100 + off));
                Assert.Equal(Lwr(cur, model, off), m.ReadWordRight(cur, 0x80000100 + off));
            }
            for (uint off = 20; off < 24; off++)
            {
                var v = (uint)rnd.NextInt64();
                var m2 = (byte[])model.Clone();
                Swl(m2, off, v);
                m.WriteWordLeft(0x80000100 + off, v);
                for (var i = 0; i < 64; i++) Assert.Equal(m2[i], m.ReadU8(0x80000100 + (uint)i));
                Swr(m2, off + 8, v);
                m.WriteWordRight(0x80000100 + off + 8, v);
                for (var i = 0; i < 64; i++) Assert.Equal(m2[i], m.ReadU8(0x80000100 + (uint)i));
                model = m2;
            }
        }
    }
}

public class RomTests
{
    [Fact]
    public void Reset_RomWindowIs512KB_MirroredAcrossIt_RestIsBusError()
    {
        var m = new System12Memory();
        m.LoadMainRom(T.Rom(0x10000));
        Assert.Equal(0x80000u, m.RomWindow);
        Assert.Equal(T.RomWord(0), m.ReadU32(0xbfc00000));
        Assert.Equal(T.RomWord(5), m.ReadU32(0xbfc00014));
        Assert.Equal(T.RomWord(5), m.ReadU32(0xbfc10014));   // 64 KB image mirrors inside the window
        Assert.Equal(T.RomWord(5), m.ReadU32(0x9fc70014));
        var e = Assert.Throws<BusErrorException>(() => m.ReadU32(0xbfc80000));
        Assert.Contains("ROM window", e.Message);
    }

    [Fact]
    public void RomConfig_Enlarges_Window()
    {
        var m = new System12Memory();
        m.LoadMainRom(T.Rom(0x400000));
        Assert.Throws<BusErrorException>(() => m.ReadU32(0xbfc80000));
        m.WriteU32(0xbf801010, 0x00160000);
        Assert.Equal(0x400000u, m.RomWindow);
        Assert.Equal(T.RomWord(0x80000 / 4), m.ReadU32(0xbfc80000));
        Assert.Equal(T.RomWord(0x3ffffc / 4), m.ReadU32(0xbfffffffu & ~3u));
        Assert.Equal(T.RomWord(0x3ffffc / 4), m.ReadU32(0x1ffffffc));
        Assert.Equal(0x16u, m.ReadU32(0xbf801010) >> 16);
    }

    [Fact]
    public void Rom_ByteAndHalfReads_AreLittleEndian()
    {
        var m = T.Booted(0x1000);
        var w = T.RomWord(3);
        Assert.Equal((byte)w, m.ReadU8(0xbfc0000c));
        Assert.Equal((byte)(w >> 24), m.ReadU8(0xbfc0000f));
        Assert.Equal((ushort)(w >> 16), m.ReadU16(0xbfc0000e));
    }

    [Fact]
    public void Rom_Write_IsNotImplemented()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.WriteU32(0xbfc00000, 1));
        Assert.Contains("NOT IMPLEMENTED", e.Message);
        Assert.Contains("ROM", e.Message);
    }

    [Fact]
    public void NoRomLoaded_Read_ThrowsActionable()
    {
        var m = new System12Memory();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0xbfc00000));
        Assert.Contains("LoadMainRom", e.Message);
    }

    [Fact]
    public void LoadMainRom_RejectsBadSizes_AndMissingFile()
    {
        var m = new System12Memory();
        Assert.Throws<ArgumentException>(() => m.LoadMainRom(new byte[3000]));
        Assert.Throws<ArgumentException>(() => m.LoadMainRom(new byte[0x800000]));
        Assert.Throws<FileNotFoundException>(() => m.LoadMainRomFile("/nonexistent/maincpu_rom.bin"));
    }

    [Fact]
    public void LoadMainRomFile_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "s12_synth_rom_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, T.Rom(0x2000));
            var m = new System12Memory();
            m.LoadMainRomFile(path);
            Assert.Equal(0x2000, m.MainRomLength);
            Assert.Equal(T.RomWord(7), m.ReadU32(0xbfc0001c));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Optional evidence test against the user's real dump (never embedded in the repo). Runs only when
    /// S12_MAIN_ROM points to maincpu_rom.bin or generated/regions/maincpu_rom.bin exists in the workspace; otherwise it
    /// asserts nothing about game data and passes trivially - the output says which case happened.
    /// </summary>
    [Fact]
    public void RealRom_IfPresent_BootFactsHold()
    {
        var path = Environment.GetEnvironmentVariable("S12_MAIN_ROM");
        if (string.IsNullOrEmpty(path)) path = "/home/user/test/generated/regions/maincpu_rom.bin";
        if (!File.Exists(path)) { Console.WriteLine("RealRom_IfPresent: no ROM file, test did not exercise game data"); return; }
        var m = new System12Memory();
        m.LoadMainRomFile(path);
        Assert.Equal(0x400000, m.MainRomLength);
        m.WriteU32(0x1f801010, 0x00160000);
        // STATUS.json facts: entry word @ROM 0x2401c = 0x80010000 (boot loader copies to 0x80010000)
        Assert.Equal(0x80010000u, m.ReadU32(0xbfc00000 + 0x2401c));
        Console.WriteLine("RealRom_IfPresent: verified entry word 0x80010000 at ROM+0x2401c from " + path);
    }
}

public class ScratchpadAndBiuTests
{
    [Fact]
    public void Scratchpad_DisabledAtReset_IsBusError()
    {
        var m = new System12Memory();
        var e = Assert.Throws<BusErrorException>(() => m.ReadU32(0x1f800000));
        Assert.Contains("not enabled", e.Message);
        Assert.Throws<BusErrorException>(() => m.WriteU32(0x1f800000, 1));
    }

    [Fact]
    public void Scratchpad_Enabled_ByBiu_RoundTrips_AndMirrorsKseg0Only()
    {
        var m = T.Booted();
        Assert.Equal(0x88u, m.ReadU32(0xfffe0130));
        m.WriteU32(0x1f800000, 0x01020304);
        m.WriteU8(0x1f8003ff, 0x7f);
        m.WriteU16(0x1f800010, 0xbeef);
        Assert.Equal(0x01020304u, m.ReadU32(0x9f800000));
        Assert.Equal(0x7fu, m.ReadU8(0x1f8003ff));
        Assert.Equal(0xbeefu, m.ReadU16(0x1f800010));
        Assert.Throws<BusErrorException>(() => m.ReadU32(0xbf800000));      // kseg1: not reachable
        Assert.Throws<BusErrorException>(() => m.ReadU32(0x1f800400));      // hole
        Assert.Throws<BusErrorException>(() => m.WriteU32(0x1f800fff & ~3u, 0));
    }

    [Fact]
    public void Scratchpad_RamBitOnly_ReadsFault_WritesDropped()
    {
        var m = new System12Memory();
        m.WriteU32(0xfffe0130, 0x08);
        m.WriteU32(0x1f800000, 0x12345678);    // dropped (reference: nop_write)
        Assert.Throws<BusErrorException>(() => m.ReadU32(0x1f800000));
    }

    [Fact]
    public void Biu_SubWordAccess_IsNotImplemented()
    {
        var m = T.Booted();
        Assert.Throws<NotImplementedMmioException>(() => m.ReadU8(0xfffe0130));
        Assert.Throws<NotImplementedMmioException>(() => m.WriteU16(0xfffe0130, 0));
        Assert.Throws<BusErrorException>(() => m.ReadU32(0xfffe0134));
    }

    [Fact]
    public void IsolatedCache_RoutesDataAccessesToCacheModel_NotBus()
    {
        var m = T.Booted();
        m.WriteU32(0x80002000, 0xfeedface);
        m.WriteU32(0xfffe0130, 0x00000800 | 0x80 | 0x08);   // IS1 + DS + RAM
        m.CacheIsolated = true;
        m.WriteU32(0x80002000, 0x11111111);                 // goes to icache/dcache, not RAM
        Assert.Equal(0x11111111u, m.ReadU32(0x80002000));
        m.CacheIsolated = false;
        Assert.Equal(0xfeedfaceu, m.ReadU32(0x80002000));   // RAM untouched
    }

    [Fact]
    public void IsolatedCache_TagWrite_ClearsValidBits_LikeKernelCacheFlush()
    {
        var m = T.Booted();
        m.WriteU32(0xfffe0130, 0x00000800 | 0x04);          // IS1 + TAG
        m.CacheIsolated = true;
        m.WriteU32(0x00000000, 0);                          // tag := valid 0
        Assert.Equal(0x10u, m.ReadU32(0x00000000));         // match bit set, valid bits clear
        m.WriteU32(0x00000000, 0xf);                        // valid
        Assert.Equal(0x1fu, m.ReadU32(0x00000000));
    }
}

public class HwRegisterTests
{
    [Fact]
    public void PlainRegisters_ReadBackWhatWasWritten_ByteLanesCombine()
    {
        var m = T.Booted();
        m.WriteU32(0x1f801004, 0x11223344);
        Assert.Equal(0x11223344u, m.ReadU32(0xbf801004));
        m.WriteU8(0x1f801005, 0xaa);
        Assert.Equal(0x1122aa44u, m.ReadU32(0x1f801004));
        m.WriteU16(0x1f801006, 0xbbcc);
        Assert.Equal(0xbbccaa44u, m.ReadU32(0x1f801004));
        Assert.Equal(0xaau, m.ReadU8(0x1f801005));
        Assert.Equal(0xbbccu, m.ReadU16(0x1f801006));
    }

    [Fact]
    public void ExpansionBase_IsForcedTo0x1f______()
    {
        var m = T.Booted();
        m.WriteU32(0x1f801000, 0x12345678);
        Assert.Equal(0x1f345678u, m.ExpansionBase);
        Assert.Equal(0x1f345678u, m.ReadU32(0x1f801000));
    }

    [Fact]
    public void Irq_StatMask_FollowReferenceSemantics_AndLineCallback()
    {
        var m = T.Booted();
        var line = new List<bool>();
        m.Irq.OnLineChanged = line.Add;

        m.Irq.Raise(0);                                    // VBLANK latched but masked
        Assert.Equal(1u, m.ReadU32(0x1f801070));
        Assert.Empty(line);
        m.WriteU32(0x1f801074, 0x1);                       // unmask VBLANK
        Assert.Equal(new[] { true }, line);
        Assert.Equal(1u, m.ReadU32(0x1f801074));
        m.WriteU32(0x1f801070, 0xfffffffe);                // ack by writing 0 to bit0
        Assert.Equal(0u, m.ReadU32(0x1f801070));
        Assert.Equal(new[] { true, false }, line);

        // reference quirk (MAME irq.cpp): stat bits that are masked off are also cleared by any write
        m.Irq.Raise(3);                                    // masked source stays pending in I_STAT
        Assert.Equal(8u, m.ReadU32(0x1f801070));
        m.WriteU32(0x1f801070, 0xffffffff);
        Assert.Equal(0u, m.ReadU32(0x1f801070));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Irq.Raise(11));
    }

    [Fact]
    public void RootCounters_RoundTrip_ModeAndTarget()
    {
        var m = T.Booted();
        m.WriteU32(0x1f801108, 0x1234);            // counter 0 target
        m.WriteU32(0x1f801104, 0x0048);            // mode: bit3 reset-on-target, bit6 repeat
        Assert.Equal(0x1234u, m.ReadU32(0x1f801108));
        Assert.Equal(0x0048u, m.ReadU16(0x1f801104));
        Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0x1f80110c));
    }

    [Theory]
    [InlineData(0x1f801044u, "SIO0")]
    [InlineData(0x1f801054u, "SIO1")]
    [InlineData(0x1f801800u, "CD-ROM")]
    [InlineData(0x1f801c00u, "SPU")]
    [InlineData(0x1f801078u, "IRQ")]
    public void UnimplementedPs1Blocks_ThrowNotImplemented_WithActionableText(uint addr, string who)
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0xbf000000 | (addr & 0xffffff)));
        Assert.Contains("NOT IMPLEMENTED", e.Message);
        Assert.Contains(who, e.Message);
        Assert.Equal(addr, e.Physical);
        Assert.Throws<NotImplementedMmioException>(() => m.WriteU32(addr, 1));
    }

    [Fact]
    public void NopWriteRegister_0x1f802040_CountsDroppedWrites_ReadsAreNotImplemented()
    {
        var m = T.Booted();
        m.WriteU32(0x1f802040, 0xff);
        Assert.Equal(1, m.NopWriteCount);
        Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0x1f802040));
    }

    [Fact]
    public void Gpu_DefaultPort_ThrowsNotImplemented_AndFakePortReceivesTraffic()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.WriteU32(0x1f801810, 0xe1000000));
        Assert.Contains("IGpuPort", e.Message);
        var gpu = new FakeGpu();
        m.Gpu = gpu;
        m.WriteU32(0xbf801810, 0x28ffffff);
        m.WriteU32(0xbf801814, 0x03000000);
        gpu.Stat = 0x14802000;
        Assert.Equal(0x14802000u, m.ReadU32(0x1f801814));
        Assert.Equal(new uint[] { 0x28ffffff }, gpu.Gp0);
        Assert.Equal(new uint[] { 0x03000000 }, gpu.Gp1);
        Assert.Throws<NotImplementedMmioException>(() => m.ReadU16(0x1f801814));   // FIFO/status ports are 32-bit only
        Assert.Throws<NotImplementedMmioException>(() => m.WriteU8(0x1f801810, 0));
    }
}

internal sealed class FakeGpu : IGpuPort
{
    public readonly List<uint> Gp0 = new(), Gp1 = new();
    public uint Stat, Data;
    public uint ReadData() => Data;
    public uint ReadStat() => Stat;
    public void WriteGp0(uint w) => Gp0.Add(w);
    public void WriteGp1(uint w) => Gp1.Add(w);
}

public class DmaTests
{
    private static void SetupChannel(System12Memory m, int ch, uint madr, uint bcr, uint chcr, bool enable = true)
    {
        if (enable) m.WriteU32(0x1f8010f0, m.ReadU32(0x1f8010f0) | (8u << (ch * 4)));
        var b = 0x1f801080u + (uint)ch * 0x10;
        m.WriteU32(b, madr);
        m.WriteU32(b + 4, bcr);
        m.WriteU32(b + 8, chcr);
    }

    [Fact]
    public void Ch6_Otc_BuildsReverseLinkedTable_AndSetsDicr()
    {
        var m = T.Booted();
        m.WriteU32(0x1f8010f4, 0x00800000 | (1u << 22));   // master enable + ch6 enable
        SetupChannel(m, 6, 0x00001010, 5, 0x11000002);
        Assert.Equal(0x00100cu, m.ReadU32(0x1010) );
        Assert.Equal(0x001008u, m.ReadU32(0x100c));
        Assert.Equal(0x001004u, m.ReadU32(0x1008));
        Assert.Equal(0x001000u, m.ReadU32(0x1004));
        Assert.Equal(0x00ffffffu, m.ReadU32(0x1000));
        Assert.Equal(0u, m.ReadU32(0x1f8010e8) & (1u << 24));              // start/trigger bits cleared on completion
        Assert.NotEqual(0u, m.ReadU32(0x1f8010f4) & 0x80000000u);          // master flag
        Assert.NotEqual(0u, m.ReadU32(0x1f8010f4) & (1u << 30));           // ch6 flag
        Assert.Equal(8u, m.Irq.Stat & 8u);                                 // IRQ3 latched
    }

    [Fact]
    public void Dma_NotEnabledInDpcr_DoesNothing()
    {
        var m = T.Booted();
        SetupChannel(m, 6, 0x1010, 5, 0x11000002, enable: false);
        Assert.Equal(0u, m.ReadU32(0x1010));
        Assert.Equal(0x11000002u, m.ReadU32(0x1f8010e8));
    }

    [Fact]
    public void Ch2_LinkedList_FeedsGp0_InOrder()
    {
        var m = T.Booted();
        var gpu = new FakeGpu();
        m.Gpu = gpu;
        // node A @0x1000: 2 words, next 0x2000 ; node B @0x2000: 1 word, end
        m.WriteU32(0x1000, (2u << 24) | 0x2000);
        m.WriteU32(0x1004, 0xaaaa0001);
        m.WriteU32(0x1008, 0xaaaa0002);
        m.WriteU32(0x2000, (1u << 24) | 0xffffff);
        m.WriteU32(0x2004, 0xbbbb0001);
        SetupChannel(m, 2, 0x1000, 0, 0x01000401);
        Assert.Equal(new uint[] { 0xaaaa0001, 0xaaaa0002, 0xbbbb0001 }, gpu.Gp0);
    }

    [Fact]
    public void Ch2_BlockWrite_And_GpuRead()
    {
        var m = T.Booted();
        var gpu = new FakeGpu { Data = 0x5a5a5a5a };
        m.Gpu = gpu;
        for (uint i = 0; i < 4; i++) m.WriteU32(0x3000 + i * 4, 0x100 + i);
        SetupChannel(m, 2, 0x3000, (1u << 16) | 4, 0x01000201);   // sync 1: 1 block of 4 words, RAM->GPU
        Assert.Equal(new uint[] { 0x100, 0x101, 0x102, 0x103 }, gpu.Gp0);
        SetupChannel(m, 2, 0x4000, (1u << 16) | 3, 0x01000200);   // GPU->RAM
        Assert.Equal(0x5a5a5a5au, m.ReadU32(0x4008));
        Assert.Equal(0u, m.ReadU32(0x400c));
    }

    [Fact]
    public void Ch5_RomDma_HookReceivesMaskedAddressAndWordCount()
    {
        var m = T.Booted();
        (uint off, uint words, uint chcr)? seen = null;
        m.Dma.SetToRamHandler(5, (mem, off, words, chcr) =>
        {
            seen = (off, words, chcr);
            for (uint i = 0; i < words; i++) BitConverter.TryWriteBytes(mem.RamSpan.Slice((int)off + (int)i * 4, 4), 0xabc00000u + i);
        });
        SetupChannel(m, 5, 0x80010000, 0x398350 / 4, 0x01000000);
        Assert.NotNull(seen);
        Assert.Equal(0x10000u, seen!.Value.off);             // KSEG0 bit masked by RAM size, like the reference
        Assert.Equal(0x398350u / 4, seen.Value.words);       // burst mode: whole BCR is the word count
        Assert.Equal(0xabc00000u, m.ReadU32(0x80010000));
        Assert.Equal(0xabc00000u + 100, m.ReadU32(0x80010000 + 400));
    }

    [Fact]
    public void Ch5_WithoutHandler_IsNotImplemented_WithInstallHint()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => SetupChannel(m, 5, 0x1000, 16, 0x01000000));
        Assert.Contains("channel 5", e.Message);
        Assert.Contains("SetToRamHandler(5", e.Message);
    }

    [Fact]
    public void Ch3_Cd_And_Ch4_Spu_AreNotImplemented()
    {
        var m = T.Booted();
        Assert.Throws<NotImplementedMmioException>(() => SetupChannel(m, 3, 0x1000, 16, 0x11000000));
        Assert.Throws<NotImplementedMmioException>(() => SetupChannel(m, 4, 0x1000, 16, 0x01000201));
    }

    [Fact]
    public void Dicr_FlagsClearByWritingOne_MasterFollowsFlags()
    {
        var m = T.Booted();
        m.Dma.SetToRamHandler(5, (_, _, _, _) => { });
        m.WriteU32(0x1f8010f4, 0x00800000 | (1u << 21));   // master + ch5 enable
        SetupChannel(m, 5, 0x1000, 4, 0x01000000);
        var d = m.ReadU32(0x1f8010f4);
        Assert.Equal(1u << 29, d & (1u << 29));
        Assert.Equal(0x80000000u, d & 0x80000000u);
        m.WriteU32(0x1f8010f4, (1u << 29) | 0x00a00000);   // ack ch5 flag
        d = m.ReadU32(0x1f8010f4);
        Assert.Equal(0u, d & (1u << 29));
        Assert.Equal(0u, d & 0x80000000u);
    }

    [Fact]
    public void Mdec_Dma_RoundTripsThroughRecompOneMdec()
    {
        var m = T.Booted();
        // Control reset then status read: exercises the RecompOne.Runtime.Mdec instance through MMIO
        m.WriteU32(0x1f801824, 0x80000000);
        Assert.NotEqual(0u, m.ReadU32(0x1f801824) & 0x80000000u);   // data-out FIFO empty
        Assert.Equal(0u, m.ReadU32(0x1f801820));
    }
}

/// <summary>A tiny device with a ROM-like word array, used to test routing, offsets and widths.</summary>
internal sealed class TestDevice : MmioDeviceBase
{
    private readonly MmioRange[] _r;
    public readonly List<string> Log = new();
    public uint Reg;
    public TestDevice(string name, params MmioRange[] r) { Name = name; _r = r; }
    public override string Name { get; }
    public override IReadOnlyList<MmioRange> Ranges => _r;
    public override uint ReadU32(uint offset) { Log.Add($"r32 {offset:x}"); return offset == 0 ? Reg : 0x1000 + offset; }
    public override ushort ReadU16(uint offset) { Log.Add($"r16 {offset:x}"); return (ushort)(0x2000 + offset); }
    public override void WriteU32(uint offset, uint value) { Log.Add($"w32 {offset:x} {value:x}"); if (offset == 0) Reg = value; }
    // ReadU8 / WriteU8 / WriteU16 deliberately not overridden: base throws NOT IMPLEMENTED
}

public class DeviceTests
{
    [Fact]
    public void MappedDevice_ReceivesOffsets_AtRequestedWidths_FromAllSegments()
    {
        var m = T.Booted();
        var d = new TestDevice("Dev", MmioRange.FromLength(0x1f140000, 0x1000));
        m.MapDevice(d);
        m.WriteU32(0xbf140000, 0xcafe);
        Assert.Equal(0xcafeu, m.ReadU32(0x1f140000));
        Assert.Equal(0x1000u + 0x10, m.ReadU32(0x9f140010));
        Assert.Equal(0x2000u + 0x12, m.ReadU16(0x1f140012));
        Assert.Equal(new[] { "w32 0 cafe", "r32 0", "r32 10", "r16 12" }, d.Log);
        Assert.Contains(m.MappedDevices, x => x.Name == "Dev");
    }

    [Fact]
    public void Device_UnimplementedWidth_ThrowsNotImplemented_WithDeviceName()
    {
        var m = T.Booted();
        m.MapDevice(new TestDevice("EepromIo", MmioRange.FromLength(0x1f140000, 0x1000)));
        var e = Assert.Throws<NotImplementedMmioException>(() => m.ReadU8(0x1f140003));
        Assert.Equal("EepromIo", e.Device);
        Assert.Equal(0x1f140003u, e.Physical);
        Assert.Contains("NOT IMPLEMENTED", e.Message);
        Assert.Throws<NotImplementedMmioException>(() => m.WriteU16(0x1f140002, 1));
    }

    [Fact]
    public void MapDevice_RejectsOverlaps_WithRamRomScratchpadHwAndOtherDevices()
    {
        var m = T.Booted();
        m.MapDevice(new TestDevice("A", MmioRange.FromLength(0x1f080000, 0x4000)));
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("B", MmioRange.FromLength(0x1f083000, 0x2000))));   // other device
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("C", MmioRange.FromLength(0x00100000, 0x10))));     // RAM window
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("D", MmioRange.FromLength(0x1fc00000, 0x10))));     // main ROM
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("E", MmioRange.FromLength(0x1f800000, 0x10))));     // scratchpad
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("F", MmioRange.FromLength(0x1f801080, 0x10))));     // DMA
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("G", MmioRange.FromLength(0x1f801070, 0x8))));      // IRQ
        Assert.Throws<ArgumentException>(() => m.MapDevice(new TestDevice("H", new MmioRange(0x1f000010, 0x1f000010))));       // empty
        // free hw holes are allowed: SIO, SPU, CD
        m.MapDevice(new TestDevice("Sio1", MmioRange.FromLength(0x1f801050, 0x10)));
        Assert.Contains(m.MappedDevices, x => x.Name == "Sio1");
    }

    [Fact]
    public void Device_CanProvideSio_Previously_NotImplemented()
    {
        var m = T.Booted();
        Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0x1f801054));
        var d = new TestDevice("Sio1", MmioRange.FromLength(0x1f801050, 0x10)) { Reg = 0x77 };
        m.MapDevice(d);
        Assert.Equal(0x77u, m.ReadU32(0x1f801050));
    }

    [Theory]
    [InlineData(0x1f000000u, "BankedRom.bankoffset")]
    [InlineData(0x1f080000u, "SharedRam")]
    [InlineData(0x1f140000u, "Eeprom")]
    [InlineData(0x1f700000u, "BankedRom.dmaoffset")]
    [InlineData(0x1fb00000u, "BankedRom")]
    [InlineData(0x1fb80000u, "BankedRom")]
    public void UnmappedArcadeMmio_Throws_NamingTheMissingDevice(uint phys, string expected)
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0xa0000000 | phys));
        Assert.Equal(expected, e.Device);
        Assert.Contains("NOT IMPLEMENTED", e.Message);
        Assert.Contains("Needs the", e.Message);
        Assert.Throws<NotImplementedMmioException>(() => m.WriteU16(0xa0000000 | phys, 1));
    }

    [Fact]
    public void UnmappedUnknownAddress_SaysSo()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => m.ReadU32(0xbf555550));
        Assert.Equal("UNMAPPED", e.Device);
        Assert.Contains("no device mapped", e.Message);
    }

    [Fact]
    public void ExceptionMessage_NamesGuestFunction_FromGeneratedStyleCaller()
    {
        var m = T.Booted();
        var e = Assert.Throws<NotImplementedMmioException>(() => GeneratedShape.func_80012345(new CpuContext(), m));
        Assert.Equal("func_80012345", e.GuestFunction);
        Assert.Contains("[guest func_80012345]", e.Message);
    }

    [Fact]
    public void Policy_LogAndContinue_LogsOnce_CountsAll_ReadsZero()
    {
        var m = T.Booted();
        var log = new List<string>();
        m.Log = log.Add;
        m.Policy = UnimplementedPolicy.LogAndContinue;
        Assert.Equal(0u, m.ReadU32(0xbf140000));
        Assert.Equal(0u, m.ReadU32(0xbf140000));
        m.WriteU32(0xbf140004, 5);
        m.WriteU32(0xbf801044, 1);                    // SIO0
        Assert.Equal(0u, m.ReadU32(0xfffe0134));   // kseg2 hole -> bus error logged, read 0
        Assert.Equal(3, log.Count(l => l.Contains("NOT IMPLEMENTED")));
        Assert.Single(log, l => l.Contains("BUS ERROR"));
        Assert.Equal(2, m.UnimplementedHits["Eeprom|R32|0x1f140000"]);
        Assert.All(log, l => Assert.DoesNotContain("exception", l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Policy_LogAndContinue_AlsoCoversDeviceThrows()
    {
        var m = T.Booted();
        var log = new List<string>();
        m.Log = log.Add;
        m.Policy = UnimplementedPolicy.LogAndContinue;
        m.MapDevice(new TestDevice("Dev", MmioRange.FromLength(0x1f140000, 0x1000)));
        Assert.Equal(0u, m.ReadU8(0x1f140001));
        Assert.Single(log);
        Assert.Contains("Dev", log[0]);
    }
}

/// <summary>Mimics the shape of recompiler output: static func_XXXXXXXX(CpuContext c, IMemory m) with the (System12Memory) cast.</summary>
internal static class GeneratedShape
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void func_80012345(CpuContext c, IMemory m)
    {
        var mem = (System12Memory)m;
        c.A0 = 0xbf140000;
        c.V0 = mem.ReadU32(c.A0);       // EEPROM region without a device -> NOT IMPLEMENTED
    }

    /// <summary>sum of n words starting at addr using the exact member set the generated code uses.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void func_80010000(CpuContext c, IMemory m)
    {
        var mem = (System12Memory)m;
        uint sum = 0;
        for (var i = 0u; i < c.A1; i++) { sum += mem.ReadU32(c.A0 + i * 4); mem.WriteU8(c.A2 + i, (byte)sum); mem.WriteU16(c.A2 + 0x100 + i * 2, (ushort)sum); }
        c.V0 = sum;
        c.T0 = mem.ReadU8(c.A2) | (uint)mem.ReadU16(c.A2 + 0x100);
    }
}

public class GeneratedCodeCompatTests
{
    [Fact]
    public void PSMemoryCast_FailsForSystem12Memory_ThisIsWhyThePatchIsNeeded()
    {
        IMemory m = new System12Memory();
        // exactly what the stock recompiler emits: var mem = (PSMemory)m;
        Assert.Throws<InvalidCastException>(() => { var mem = (PSMemory)m; _ = mem; });
    }

    [Fact]
    public void GeneratedShape_RunsAgainstSystem12Memory()
    {
        var m = T.Booted();
        for (uint i = 0; i < 16; i++) m.WriteU32(0x80001000 + i * 4, i + 1);
        var c = new CpuContext { A0 = 0x80001000, A1 = 16, A2 = 0x80002000 };
        GeneratedShape.func_80010000(c, m);
        Assert.Equal(136u, c.V0);
        Assert.Equal(1u, c.T0);                                  // first iteration wrote sum=1 at A2 and A2+0x100
        Assert.Equal(136u, m.ReadU16(0x80002100 + 15 * 2));      // last iteration wrote the running sum
        Assert.Equal((byte)136, m.ReadU8(0x80002000 + 15));
    }
}
