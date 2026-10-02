// SPDX-License-Identifier: BSD-3-Clause
//
// Self-contained tests for the System 12 sound subsystem (no test framework dependency).
//   SoundSelfTests.RunAll(log)                     - synthetic-data unit tests (C352 mixer, H8 decoder/CPU/peripherals)
//   SoundSelfTests.RunFirmwareRegression(sub, wav) - optional, needs the user's sub.bin and c352.bin region files
// Both return the number of failed checks. Expected values are derived by hand from the documented chip behaviour
// (and, where stated, from traces captured from MAME 0.264); they are NOT produced by running the code under test.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace System12.Runtime.Sound;

public static class SoundSelfTests
{
    private static int _fail, _pass;
    private static Action<string> _log = _ => { };

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        _log("FAIL: " + what);
    }

    private static void Eq<T>(T actual, T expected, string what) where T : IEquatable<T>
        => Check(actual.Equals(expected), $"{what}: expected {expected}, got {actual}");

    public static int RunAll(Action<string>? log = null)
    {
        _fail = 0; _pass = 0; _log = log ?? (_ => { });
        C352Tests();
        DecoderTests();
        CpuTests();
        TimingTests();
        PeripheralTests();
        _log($"SoundSelfTests: {_pass} checks passed, {_fail} failed");
        return _fail;
    }

    // =====================================================================================================
    // C352
    // =====================================================================================================

    private static void Reg(C352 c, int voice, int reg, ushort v) => c.Write(voice * 8 + reg, v);
    private const int VolF = 0, VolR = 1, Freq = 2, Flags = 3, Bank = 4, Start = 5, End = 6, Loop = 7;

    private static C352 MakeChip(byte[] data)
    {
        var rom = new byte[0x1000000];
        Array.Copy(data, rom, data.Length);
        return new C352(rom);
    }

    private static void Run(C352 c, int n, out short l, out short r)
    {
        l = r = 0;
        for (int i = 0; i < n; i++) c.GenerateSample(out l, out r, out _, out _);
    }

    private static void C352Tests()
    {
        Eq(C352.System12SampleRate, 88200, "C352 output rate = 25.4016 MHz / 288");
        Eq(S12SoundBoard.StatesPerAudioFrame, 192, "H8 states per C352 frame");

        // mu-law table (c352_device::device_start): increments 1/2/4/8/16 per code range, upper half is the 1's complement
        var c0 = MakeChip(new byte[1]);
        Eq((int)c0.MuLaw(0), 0, "mulaw[0]");
        Eq((int)c0.MuLaw(1), 32, "mulaw[1]");
        Eq((int)c0.MuLaw(15), 15 << 5, "mulaw[15]");
        Eq((int)c0.MuLaw(16), 16 << 5, "mulaw[16]");
        Eq((int)c0.MuLaw(24), 32 << 5, "mulaw[24]");
        Eq((int)c0.MuLaw(48), 128 << 5, "mulaw[48]");
        Eq((int)c0.MuLaw(100), 544 << 5, "mulaw[100]");
        Eq((int)c0.MuLaw(127), 976 << 5, "mulaw[127] = 31232");
        Eq((int)c0.MuLaw(128), -32, "mulaw[128] = ~mulaw[0] & 0xffe0");
        Eq((int)c0.MuLaw(255), unchecked((short)(~(976 << 5) & 0xffe0)), "mulaw[255] = ~mulaw[127] & 0xffe0 (-31264)");
        bool mono = true;
        for (int i = 1; i < 128; i++) if (c0.MuLaw((byte)i) <= c0.MuLaw((byte)(i - 1))) mono = false;
        Check(mono, "mulaw increasing over 0..127");

        // register file
        var c1 = MakeChip(new byte[1]);
        c1.Write(3, 0xabcd);
        Eq((int)c1.Read(3), 0xabcd, "reg write/read");
        c1.Write(3, 0x1200, 0xff00);
        Eq((int)c1.Read(3), 0x12cd, "reg write with high-byte mask");
        c1.Write8(6, 0x34); // byte offset 6 = word 3, high byte
        Eq((int)c1.Read(3), 0x34cd, "Write8 even byte = high half");
        c1.Write8(7, 0x56);
        Eq((int)c1.Read(3), 0x3456, "Write8 odd byte = low half");
        c1.Write(0x200, 0x0e9f);
        Eq((int)c1.Read(0x200), 0x0e9f, "control register");
        Eq((int)c1.Read(0x201), 0, "unmapped register reads 0");

        // key-on is executed only by a full-width write to 0x202
        var c2 = MakeChip(new byte[] { 1, 2, 3, 4 });
        Reg(c2, 0, Bank, 0); Reg(c2, 0, Start, 1); Reg(c2, 0, End, 3);
        Reg(c2, 0, Flags, C352.FlgKeyOn);
        c2.Write(0x202, 0x20, 0x00ff);
        Check(!c2.IsBusy(0), "partial-mask write to 0x202 does nothing");
        c2.Write(0x202, 0x20);
        Check(c2.IsBusy(0), "key-on sets BUSY");
        Eq((int)(c2.FlagsOf(0) & C352.FlgKeyOn), 0, "key-on clears KEYON");
        Eq(c2.PositionOf(0), 1u, "key-on loads pos = bank<<16 | start");

        // steady-state linear playback, no interpolation: out = ((sample<<8 as s16 * vol) >> 8) >> 3
        var data = new byte[2000];
        Array.Fill(data, (byte)0x40);
        var c3 = MakeChip(data);
        Reg(c3, 0, VolF, 0x8080); Reg(c3, 0, VolR, 0x0000);
        Reg(c3, 0, Freq, 0xffff); Reg(c3, 0, End, 1999); Reg(c3, 0, Start, 0);
        Reg(c3, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter));
        c3.Write(0x202, 0x20);
        Run(c3, 400, out short l3, out short r3);
        Eq((int)l3, ((0x4000 * 0x80) >> 8) >> 3, "linear 8-bit, vol 0x80, left");
        Eq((int)r3, ((0x4000 * 0x80) >> 8) >> 3, "linear 8-bit, vol 0x80, right");
        var snap = c3.Snapshot(0);
        Eq((int)snap.Vol0, 0x80, "volume ramp reached target (front left)");
        Eq((int)snap.Vol2, 0, "rear volume stays 0");

        // phase invert front-left
        var c4 = MakeChip(data);
        Reg(c4, 0, VolF, 0x8080); Reg(c4, 0, Freq, 0xffff); Reg(c4, 0, End, 1999);
        Reg(c4, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter | C352.FlgPhaseFL));
        c4.Write(0x202, 0x20);
        Run(c4, 400, out short l4, out short r4);
        Eq((int)l4, -(((0x4000 * 0x80) >> 8) >> 3), "PHASEFL inverts the left channel only (arithmetic shift of negative)");
        Eq((int)r4, ((0x4000 * 0x80) >> 8) >> 3, "right channel unaffected by PHASEFL");

        // mu-law playback
        var mu = new byte[2000];
        Array.Fill(mu, (byte)0x7f);
        var c5 = MakeChip(mu);
        Reg(c5, 0, VolF, 0x8080); Reg(c5, 0, Freq, 0xffff); Reg(c5, 0, End, 1999);
        Reg(c5, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter | C352.FlgMuLaw));
        c5.Write(0x202, 0x20);
        Run(c5, 400, out short l5, out _);
        Eq((int)l5, ((31232 * 0x80) >> 8) >> 3, "mu-law code 0x7f");

        // one-shot sample ends: BUSY cleared, KEYOFF set, silence
        var c6 = MakeChip(new byte[] { 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40 });
        Reg(c6, 0, VolF, 0x8080); Reg(c6, 0, Freq, 0xffff); Reg(c6, 0, End, 7);
        Reg(c6, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter));
        c6.Write(0x202, 0x20);
        Run(c6, 40, out short l6, out short r6);
        Check(!c6.IsBusy(0), "one-shot voice stops at wave_end");
        Check((c6.FlagsOf(0) & C352.FlgKeyOff) != 0, "KEYOFF flag set at wave_end");
        Eq((int)l6, 0, "silence after the sample ended"); Eq((int)r6, 0, "silence after the sample ended (R)");
        c6.Write(0x202, 0x20); // executes the pending key-off
        Eq((int)(c6.FlagsOf(0) & C352.FlgKeyOff), 0, "key-off executed by 0x202");

        // forward loop stays inside [loop, end]
        var c7 = MakeChip(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Reg(c7, 0, VolF, 0x0101); Reg(c7, 0, Freq, 0xffff); Reg(c7, 0, Start, 0); Reg(c7, 0, End, 5); Reg(c7, 0, Loop, 2);
        Reg(c7, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter | C352.FlgLoop));
        c7.Write(0x202, 0x20);
        bool inside = true; bool wrapped = false; uint prev = 0;
        for (int i = 0; i < 200; i++)
        {
            c7.GenerateSample(out _, out _, out _, out _);
            uint p = c7.PositionOf(0) & 0xffff;
            if (p > 5) inside = false;
            if (i > 8 && p < prev) wrapped = true;
            prev = p;
        }
        Check(inside, "looped voice never leaves [start,end]");
        Check(wrapped, "looped voice jumped back to wave_loop");
        Check((c7.FlagsOf(0) & C352.FlgLoopHist) != 0, "LOOPHIST set after the first loop");
        Check(c7.IsBusy(0), "looped voice keeps playing");

        // ping-pong (LOOP | REVERSE): forward to wave_end, then backward to wave_loop, then forward ...
        var c8 = MakeChip(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        Reg(c8, 0, VolF, 0x0101); Reg(c8, 0, Freq, 0xffff); Reg(c8, 0, Start, 2); Reg(c8, 0, End, 8); Reg(c8, 0, Loop, 4);
        Reg(c8, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter | C352.FlgLoop | C352.FlgReverse));
        c8.Write(0x202, 0x20);
        uint maxP = 0, minAfter = 99; bool sawDir = false, sawFwdAgain = false;
        for (int i = 0; i < 100; i++)
        {
            c8.GenerateSample(out _, out _, out _, out _);
            uint p = c8.PositionOf(0) & 0xffff;
            if (p > maxP) maxP = p;
            bool ldir = (c8.FlagsOf(0) & C352.FlgLDir) != 0;
            if (ldir) { sawDir = true; if (p < minAfter) minAfter = p; }
            if (sawDir && !ldir) sawFwdAgain = true;
        }
        Eq(maxP, 8u, "ping-pong reaches wave_end, then reverses");
        Eq(minAfter, 4u, "ping-pong turns around at wave_loop");
        Check(sawDir && sawFwdAgain, "ping-pong direction flag toggles both ways");

        // noise: 16-bit LFSR, seed 0x1234 (x -> (x>>1) ^ (0xfff6 if x&1))
        var c9 = MakeChip(new byte[1]);
        Eq((int)c9.NoiseState, 0x1234, "noise seed");
        Reg(c9, 0, VolF, 0x0101); Reg(c9, 0, Freq, 0xffff);
        Reg(c9, 0, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter | C352.FlgNoise));
        c9.Write(0x202, 0x20);
        c9.GenerateSample(out _, out _, out _, out _);
        Eq((int)c9.NoiseState, 0x091a, "noise step 1");
        c9.GenerateSample(out _, out _, out _, out _);
        Eq((int)c9.NoiseState, 0x048d, "noise step 2");
        c9.GenerateSample(out _, out _, out _, out _);
        Eq((int)c9.NoiseState, 0xfdb0, "noise step 3 (feedback taps applied when bit 0 = 1)");

        // linear interpolation between consecutive samples (filter bit clear), freq = 0x8000 (two output frames per fetch)
        var c10 = MakeChip(new byte[] { 0x00, 0x40, 0x40, 0x40, 0x40 });
        Reg(c10, 0, VolF, 0x0101); Reg(c10, 0, Freq, 0x8000); Reg(c10, 0, End, 4);
        Reg(c10, 0, Flags, C352.FlgKeyOn);
        c10.Write(0x202, 0x20);
        // frame 1: fetch rom[0]=0 (counter 0x7fff); frame 2: no fetch; frame 3: fetch rom[1]=0x4000, counter 0x7fff
        Run(c10, 3, out _, out _);
        var s10 = c10.Snapshot(0);
        Eq((int)s10.Sample, 0x4000, "interp: current sample");
        Eq((int)s10.LastSample, 0, "interp: previous sample");
        Eq(s10.Counter, 0x7fffu, "interp: fractional counter");
        // the mixed value uses last + (counter*(sample-last) >> 16) = 0x1fff, scaled by the (still ramping) volume
        c10.GenerateSample(out _, out _, out _, out _);
        var s10b = c10.Snapshot(0);
        Eq(s10b.Counter, 0xffffu, "interp: counter after the non-fetch frame");

        // sample ROM banking: pos = bank << 16 | start
        var big = new byte[0x1000000]; big[0x20005] = 0x40;
        var c12 = new C352(big);
        Reg(c12, 0, Bank, 2); Reg(c12, 0, Start, 5); Reg(c12, 0, End, 5);
        Reg(c12, 0, Flags, C352.FlgKeyOn);
        c12.Write(0x202, 0x20);
        Eq(c12.PositionOf(0), 0x20005u, "banked start address");

        // 32 independent voices
        var c13 = MakeChip(data);
        for (int v = 0; v < 32; v++)
        {
            Reg(c13, v, VolF, 0x0101); Reg(c13, v, Freq, 0xffff); Reg(c13, v, End, 1999);
            Reg(c13, v, Flags, (ushort)(C352.FlgKeyOn | C352.FlgFilter));
        }
        c13.Write(0x202, 0x20);
        int busy = 0; for (int v = 0; v < 32; v++) if (c13.IsBusy(v)) busy++;
        Eq(busy, 32, "all 32 voices keyed on");
    }

    // =====================================================================================================
    // H8 decoder + CPU
    // =====================================================================================================

    private sealed class TestBus : IH8Bus
    {
        public readonly byte[] M = new byte[1 << 24];
        public long NowStates;
        public H8Cpu? Cpu;
        public Action<uint, long>? OnWrite, OnRead;
        public byte Read8(uint a) { OnRead?.Invoke(a, Cpu?.States ?? 0); return M[a & 0xffffff]; }
        public ushort Read16(uint a) { OnRead?.Invoke(a, Cpu?.States ?? 0); return (ushort)((M[a & 0xffffff] << 8) | M[(a + 1) & 0xffffff]); }
        public void Write8(uint a, byte v) { OnWrite?.Invoke(a, Cpu?.States ?? 0); M[a & 0xffffff] = v; }
        public void Write16(uint a, ushort v) { OnWrite?.Invoke(a, Cpu?.States ?? 0); M[a & 0xffffff] = (byte)(v >> 8); M[(a + 1) & 0xffffff] = (byte)v; }
        public void Tick() { }
    }

    private static byte[] B(params int[] b) { var r = new byte[b.Length]; for (int i = 0; i < b.Length; i++) r[i] = (byte)b[i]; return r; }

    private static string Dis(params int[] bytes)
    {
        var buf = new byte[16];
        for (int i = 0; i < bytes.Length; i++) buf[i] = (byte)bytes[i];
        var insn = H8Decoder.Decode(buf, 0x1000);
        return H8Decoder.Disassemble(insn, 0x1000);
    }

    private static void DecoderTests()
    {
        // texts below were observed in MAME 0.264 debugger traces of the real firmware (so they match MAME's disassembler)
        Eq(Dis(0x04, 0x80), "orc     #h'80, ccr", "orc");
        Eq(Dis(0x7a, 0x07, 0x00, 0x08, 0x80, 0x00), "mov.l   #h'00088000, sp", "mov.l #imm32, sp");
        Eq(Dis(0xf8, 0x03), "mov.b   #h'03, r0l", "mov.b #imm8");
        Eq(Dis(0x38, 0xf2), "mov.b   r0l, @h'fffff2", "mov.b r0l,@aa:8");
        Eq(Dis(0x7f, 0xd3, 0x72, 0x40), "bclr    #4, @h'ffffd3", "bclr #imm,@aa:8");
        Eq(Dis(0x01, 0x00, 0x69, 0xc0), "mov.l   er0, @er4", "mov.l ER,@ER");
        Eq(Dis(0x0b, 0x94), "adds.l  #4, er4", "adds.l #4");
        Eq(Dis(0x7a, 0x24, 0x00, 0xff, 0xff, 0x00), "cmp.l   #h'00ffff00, er4", "cmp.l #imm32");
        Eq(Dis(0x01, 0x00, 0x6d, 0xf0), "mov.l   er0, @-sp", "push er0");
        Eq(Dis(0x01, 0x00, 0x6d, 0x70), "mov.l   @sp+, er0", "pop er0");
        Eq(Dis(0x6f, 0xa0, 0x00, 0x06), "mov.w   r0, @(h'6, er2)", "mov.w r0,@(d16,er2)");
        Eq(Dis(0x54, 0x70), "rts", "rts");
        Eq(Dis(0x56, 0x70), "rte", "rte");
        Eq(Dis(0x7b, 0x5c, 0x59, 0x8f), "eepmov.b", "eepmov.b");
        Eq(Dis(0x01, 0x80), "sleep", "sleep");
        Eq(Dis(0x4c, 0x02), "bge     h'001004", "bge rel8");
        Eq(Dis(0x58, 0x60, 0x01, 0x00), "bne     h'001104", "bne rel16");
        Eq(Dis(0x7c, 0x10, 0x73, 0x10), "btst    #1, @er1", "btst #imm,@er");
        Eq(Dis(0x50, 0x90), "mulxu.b r1l, r0", "mulxu.b");
        Eq(Dis(0x52, 0x10), "mulxu.w r1, er0", "mulxu.w");
        Eq(Dis(0x5e, 0x00, 0x20, 0x00), "jsr     h'002000", "jsr @aa:24");
        // H8S-only and illegal encodings must be rejected
        Check(Dis(0x01, 0x10, 0x6d, 0x71) == "(invalid)", "ldm.l (H8S) is not decoded");
        Check(Dis(0x17, 0x3f) == "(invalid)", "not.l with bit 3 of the register nibble set is illegal");
        Eq((int)H8Decoder.Decode(new byte[] { 0x7a, 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0).Len, 6, "mov.l #imm32 length");
        Eq((int)H8Decoder.Decode(new byte[] { 0x78, 0x10, 0x6b, 0x23, 0, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0, 0 }, 0).Len, 8, "mov.w @(d24,ERs) length");
    }

    private static H8Cpu NewCpu(out TestBus bus, byte[] code, uint at = 0x1000)
    {
        bus = new TestBus();
        Array.Copy(code, 0, bus.M, at, code.Length);
        var cpu = new H8Cpu(bus, Array.Empty<byte>());
        bus.Cpu = cpu;
        cpu.PC = at;
        cpu.ER[7] = 0x8000;
        cpu.CCR = 0;
        return cpu;
    }

    private static void StepN(H8Cpu c, int n) { for (int i = 0; i < n; i++) c.Step(); }

    private const byte C = H8Cpu.FlagC, V = H8Cpu.FlagV, Z = H8Cpu.FlagZ, N = H8Cpu.FlagN, H = H8Cpu.FlagH;

    private static void CpuTests()
    {
        H8Cpu c;
        TestBus bus;

        // --- arithmetic flags (hand derived) ---
        c = NewCpu(out bus, B(0x08, 0x98)); c.ER[0] = 0x7f; c.ER[1] = 0x01; c.Step();      // add.b r1l,r0l
        Eq(c.ER[0], 0x80u, "add.b 7f+01"); Eq((int)c.CCR, H | N | V, "add.b 7f+01 flags");
        c = NewCpu(out bus, B(0x08, 0x98)); c.ER[0] = 0xff; c.ER[1] = 0x01; c.Step();
        Eq(c.ER[0], 0u, "add.b ff+01"); Eq((int)c.CCR, H | Z | C, "add.b ff+01 flags");
        c = NewCpu(out bus, B(0x18, 0x98)); c.ER[0] = 0x00; c.ER[1] = 0x01; c.Step();      // sub.b r1l,r0l
        Eq(c.ER[0], 0xffu, "sub.b 00-01"); Eq((int)c.CCR, H | N | C, "sub.b 00-01 flags");
        c = NewCpu(out bus, B(0x1c, 0x98)); c.ER[0] = 5; c.ER[1] = 5; c.Step();            // cmp.b
        Eq(c.ER[0], 5u, "cmp.b leaves dest"); Eq((int)c.CCR, Z, "cmp.b equal -> Z");
        c = NewCpu(out bus, B(0x17, 0x88)); c.ER[0] = 0x80; c.Step();                      // neg.b r0l
        Eq(c.ER[0], 0x80u, "neg.b 80"); Eq((int)c.CCR, N | V | C, "neg.b 80 flags");
        c = NewCpu(out bus, B(0x79, 0x10, 0x00, 0x01)); c.ER[0] = 0x7fff; c.Step();       // add.w #1,r0
        Eq(c.ER[0], 0x8000u, "add.w 7fff+1"); Eq((int)c.CCR, H | N | V, "add.w overflow flags");
        c = NewCpu(out bus, B(0x7a, 0x30, 0x00, 0x00, 0x00, 0x01)); c.ER[0] = 0x80000000; c.Step(); // sub.l #1,er0
        Eq(c.ER[0], 0x7fffffffu, "sub.l 80000000-1"); Eq((int)c.CCR, H | V, "sub.l signed overflow, H from low nibble borrow");
        c = NewCpu(out bus, B(0x0e, 0x98)); c.ER[0] = 0x00; c.ER[1] = 0x00; c.CCR = (byte)(C | Z); c.Step(); // addx.b: 0+0+1, Z sticky only cleared by non-zero
        Eq(c.ER[0], 1u, "addx.b carry in"); Eq((int)c.CCR, 0, "addx.b clears Z for non-zero result and C");
        c = NewCpu(out bus, B(0x0e, 0x98)); c.ER[0] = 0x00; c.ER[1] = 0x00; c.CCR = Z; c.Step();
        Eq((int)c.CCR, Z, "addx.b keeps Z when the result is 0");
        // inc/dec: N Z V only
        c = NewCpu(out bus, B(0x0b, 0x50)); c.ER[0] = 0xffff; c.CCR = C; c.Step();         // inc.w #1,r0
        Eq(c.ER[0] & 0xffff, 0u, "inc.w ffff"); Eq((int)c.CCR, Z | C, "inc.w sets Z, keeps C");
        c = NewCpu(out bus, B(0x1b, 0x70)); c.ER[0] = 1; c.Step();                         // dec.l #1,er0
        Eq(c.ER[0], 0u, "dec.l"); Eq((int)c.CCR, Z, "dec.l flags");
        c = NewCpu(out bus, B(0x0b, 0x94)); c.ER[4] = 0x00fffffe; c.CCR = (byte)(N | Z | V | C); c.Step(); // adds.l #4,er4: no flags
        Eq(c.ER[4], 0x01000002u, "adds.l #4"); Eq((int)c.CCR, N | Z | V | C, "adds.l leaves CCR untouched");

        // --- logic / extend / not ---
        c = NewCpu(out bus, B(0x79, 0x60, 0x00, 0xff)); c.ER[0] = 0x1234; c.Step();        // and.w #00ff,r0
        Eq(c.ER[0], 0x0034u, "and.w"); Eq((int)c.CCR, 0, "and.w flags");
        c = NewCpu(out bus, B(0xc8, 0x80)); c.ER[0] = 0x01; c.Step();                       // or.b #80,r0l
        Eq(c.ER[0], 0x81u, "or.b"); Eq((int)c.CCR, N, "or.b N");
        c = NewCpu(out bus, B(0x17, 0xd0)); c.ER[0] = 0x0080; c.Step();                     // exts.w r0
        Eq(c.ER[0], 0xff80u, "exts.w"); Eq((int)c.CCR, N, "exts.w N");
        c = NewCpu(out bus, B(0x17, 0x50)); c.ER[0] = 0xff80; c.Step();                     // extu.w r0
        Eq(c.ER[0], 0x0080u, "extu.w"); Eq((int)c.CCR, 0, "extu.w flags");
        c = NewCpu(out bus, B(0x17, 0xf0)); c.ER[0] = 0x00008000; c.Step();                 // exts.l er0
        Eq(c.ER[0], 0xffff8000u, "exts.l");
        c = NewCpu(out bus, B(0x17, 0x10)); c.ER[0] = 0x0f0f; c.Step();                     // not.w r0
        Eq(c.ER[0] & 0xffff, 0xf0f0u, "not.w");

        // --- shifts and rotates (one bit, H8/300H) ---
        c = NewCpu(out bus, B(0x10, 0x08)); c.ER[0] = 0x81; c.Step(); Eq(c.ER[0], 0x02u, "shll.b"); Eq((int)c.CCR, C, "shll.b carry");
        c = NewCpu(out bus, B(0x10, 0x88)); c.ER[0] = 0x40; c.Step(); Eq(c.ER[0], 0x80u, "shal.b"); Eq((int)c.CCR, N | V, "shal.b overflow");
        c = NewCpu(out bus, B(0x11, 0x08)); c.ER[0] = 0x01; c.Step(); Eq(c.ER[0], 0u, "shlr.b"); Eq((int)c.CCR, Z | C, "shlr.b");
        c = NewCpu(out bus, B(0x11, 0x88)); c.ER[0] = 0x81; c.Step(); Eq(c.ER[0], 0xc0u, "shar.b"); Eq((int)c.CCR, N | C, "shar.b");
        c = NewCpu(out bus, B(0x12, 0x88)); c.ER[0] = 0x81; c.Step(); Eq(c.ER[0], 0x03u, "rotl.b"); Eq((int)c.CCR, C, "rotl.b");
        c = NewCpu(out bus, B(0x13, 0x88)); c.ER[0] = 0x01; c.Step(); Eq(c.ER[0], 0x80u, "rotr.b"); Eq((int)c.CCR, N | C, "rotr.b");
        c = NewCpu(out bus, B(0x12, 0x08)); c.ER[0] = 0x80; c.CCR = C; c.Step(); Eq(c.ER[0], 0x01u, "rotxl.b with carry in"); Eq((int)c.CCR, C, "rotxl.b carry out");
        c = NewCpu(out bus, B(0x13, 0x08)); c.ER[0] = 0x01; c.CCR = 0; c.Step(); Eq(c.ER[0], 0u, "rotxr.b"); Eq((int)c.CCR, Z | C, "rotxr.b");
        c = NewCpu(out bus, B(0x11, 0x10)); c.ER[0] = 0x0003; c.Step(); Eq(c.ER[0], 0x0001u, "shlr.w"); Eq((int)c.CCR, C, "shlr.w");
        c = NewCpu(out bus, B(0x12, 0x10)); c.ER[0] = 0x8000; c.CCR = C; c.Step(); Eq(c.ER[0], 0x0001u, "rotxl.w with carry in");
        c = NewCpu(out bus, B(0x13, 0x10)); c.ER[0] = 0x0001; c.CCR = C; c.Step(); Eq(c.ER[0], 0x8000u, "rotxr.w with carry in"); Eq((int)c.CCR, N | C, "rotxr.w flags");
        c = NewCpu(out bus, B(0x11, 0xb0)); c.ER[0] = 0x80000001; c.Step(); Eq(c.ER[0], 0xc0000000u, "shar.l"); Eq((int)c.CCR, N | C, "shar.l");

        // --- multiply / divide ---
        c = NewCpu(out bus, B(0x50, 0x90)); c.ER[0] = 0x00ab0010; c.ER[1] = 0x20; c.CCR = N; c.Step();    // mulxu.b r1l,r0
        Eq(c.ER[0], 0x00ab0200u, "mulxu.b writes r0 only (upper 16 bits untouched), uses r0l"); Eq((int)c.CCR, N, "mulxu.b leaves CCR");
        c = NewCpu(out bus, B(0x51, 0x90)); c.ER[0] = 0x0107; c.ER[1] = 0x10; c.Step();                 // divxu.b r1l,r0
        Eq(c.ER[0], 0x0710u, "divxu.b: quotient in r0l, remainder in r0h"); Eq((int)c.CCR, 0, "divxu.b flags");
        c = NewCpu(out bus, B(0x51, 0x90)); c.ER[0] = 0x0107; c.ER[1] = 0x00; c.Step();
        Eq(c.ER[0], 0x0107u, "divxu.b by zero leaves dest"); Eq((int)c.CCR, Z, "divxu.b by zero -> Z");
        c = NewCpu(out bus, B(0x51, 0x90)); c.ER[0] = 0x0107; c.ER[1] = 0x80; c.Step();
        Eq((int)c.CCR & N, N, "divxu.b N = divisor MSB");
        c = NewCpu(out bus, B(0x52, 0x10)); c.ER[0] = 0xffff0100; c.ER[1] = 0x0300; c.Step();          // mulxu.w r1,er0
        Eq(c.ER[0], 0x00030000u, "mulxu.w: er0 = r0 * r1 (low word of er0 used)");
        c = NewCpu(out bus, B(0x53, 0x10)); c.ER[0] = 100000; c.ER[1] = 7; c.Step();                    // divxu.w r1,er0
        Eq(c.ER[0], (5u << 16) | 14285u, "divxu.w: quotient low word, remainder high word");
        c = NewCpu(out bus, B(0x01, 0xc0, 0x50, 0x90)); c.ER[0] = 0xfffe; c.ER[1] = 0x03; c.Step();    // mulxs.b r1l,r0: (-2 as r0l=0xfe) * 3
        Eq(c.ER[0] & 0xffff, 0xfffau, "mulxs.b signed"); Eq((int)c.CCR, N, "mulxs.b N");
        c = NewCpu(out bus, B(0x01, 0xd0, 0x51, 0x90)); c.ER[0] = 0xffe2; c.ER[1] = 0x03; c.Step();    // divxs.b r1l,r0: -30 / 3
        Eq(c.ER[0] & 0xffff, 0x00f6u, "divxs.b: quotient -10 (0xf6), remainder 0"); Eq((int)c.CCR, N, "divxs.b N");

        // --- BCD ---
        c = NewCpu(out bus, B(0xf8, 0x19, 0x88, 0x28, 0x0f, 0x08)); StepN(c, 3);                         // 19 + 28 -> 47 BCD
        Eq(c.ER[0] & 0xff, 0x47u, "daa after add.b 19+28");
        c = NewCpu(out bus, B(0xf8, 0x50, 0x88, 0x60, 0x0f, 0x08)); StepN(c, 3);                         // 50 + 60 = 110 -> daa: 10, C
        Eq(c.ER[0] & 0xff, 0x10u, "daa 50+60"); Eq((int)c.CCR & C, C, "daa carry");

        // --- data movement / addressing modes ---
        c = NewCpu(out bus, B(0x7a, 0x00, 0x12, 0x34, 0x56, 0x78)); c.Step();
        Eq(c.ER[0], 0x12345678u, "mov.l #imm32"); Eq((int)c.CCR, 0, "mov.l flags");
        c = NewCpu(out bus, B(0x6d, 0xf0, 0x6d, 0x71)); c.ER[0] = 0xbeef; c.Step();                      // push r0 (word)
        Eq(c.ER[7], 0x7ffeu, "mov.w r0,@-sp"); Eq((int)(bus.M[0x7ffe] << 8 | bus.M[0x7fff]), 0xbeef, "word stored big-endian");
        c.Step();                                                                                        // pop r1
        Eq(c.ER[1] & 0xffff, 0xbeefu, "mov.w @sp+,r1"); Eq(c.ER[7], 0x8000u, "sp restored");
        c = NewCpu(out bus, B(0x01, 0x00, 0x6d, 0xf0)); c.ER[0] = 0x11223344; c.Step();               // mov.l er0,@-sp
        Eq(c.ER[7], 0x7ffcu, "mov.l er0,@-sp"); Eq((int)bus.M[0x7ffc], 0x11, "long stored big-endian (msb first)");
        c = NewCpu(out bus, B(0x6f, 0x10, 0x00, 0x10)); bus.M[0x2010] = 0xab; bus.M[0x2011] = 0xcd; c.ER[1] = 0x2000; c.Step(); // mov.w @(0x10,er1),r0
        Eq(c.ER[0] & 0xffff, 0xabcdu, "mov.w @(d16,er1),r0");
        c = NewCpu(out bus, B(0x6b, 0x00, 0x30, 0x00)); bus.M[0x3000] = 0x12; bus.M[0x3001] = 0x34; c.Step(); // mov.w @aa:16,r0
        Eq(c.ER[0] & 0xffff, 0x1234u, "mov.w @aa:16,r0");
        c = NewCpu(out bus, B(0x6b, 0x00, 0x80, 0x00)); bus.M[0xff8000] = 0x55; bus.M[0xff8001] = 0x66; c.Step(); // @aa:16 sign-extends
        Eq(c.ER[0] & 0xffff, 0x5566u, "@aa:16 with bit 15 set maps to 0xff8000 (sign extension)");
        c = NewCpu(out bus, B(0x28, 0x20)); bus.M[0xffff20] = 0x9c; c.Step();                           // mov.b @aa:8,r0l  (0xffff00|aa)
        Eq(c.ER[0] & 0xff, 0x9cu, "mov.b @aa:8"); Eq((int)c.CCR, N, "mov.b N");
        c = NewCpu(out bus, B(0x7a, 0x01, 0x00, 0x00, 0x20, 0x00, 0x6c, 0x18, 0x6c, 0x18)); bus.M[0x2000] = 0x11; bus.M[0x2001] = 0x22; // mov.l #2000,er1 ; mov.b @er1+,r0l (x2)
        StepN(c, 3);
        Eq(c.ER[0] & 0xff, 0x22u, "mov.b @er1+,r0l (second byte)"); Eq(c.ER[1], 0x2002u, "post-increment by the access size");

        // --- bit operations ---
        c = NewCpu(out bus, B(0x70, 0x38)); c.ER[0] = 0; c.CCR = (byte)(N | C); c.Step();               // bset #3,r0l
        Eq(c.ER[0], 0x08u, "bset #3,r0l"); Eq((int)c.CCR, N | C, "bset leaves CCR");
        c = NewCpu(out bus, B(0x72, 0x38)); c.ER[0] = 0xff; c.Step(); Eq(c.ER[0], 0xf7u, "bclr #3,r0l");
        c = NewCpu(out bus, B(0x71, 0x38)); c.ER[0] = 0x08; c.Step(); Eq(c.ER[0], 0u, "bnot #3,r0l");
        c = NewCpu(out bus, B(0x73, 0x38)); c.ER[0] = 0x08; c.Step(); Eq((int)c.CCR, 0, "btst bit set -> Z=0");
        c = NewCpu(out bus, B(0x73, 0x38)); c.ER[0] = 0x00; c.Step(); Eq((int)c.CCR, Z, "btst bit clear -> Z=1");
        c = NewCpu(out bus, B(0x60, 0x98)); c.ER[0] = 0; c.ER[1] = 0x0b; c.Step(); Eq(c.ER[0] & 0xff, 0x08u, "bset r1l,r0l uses the low 3 bits of r1l");
        c = NewCpu(out bus, B(0x67, 0x38)); c.CCR = C; c.ER[0] = 0; c.Step(); Eq(c.ER[0], 0x08u, "bst #3 copies C");
        c = NewCpu(out bus, B(0x67, 0xb8)); c.CCR = 0; c.ER[0] = 0; c.Step(); Eq(c.ER[0], 0x08u, "bist #3 copies !C");
        c = NewCpu(out bus, B(0x77, 0x38)); c.ER[0] = 0x08; c.Step(); Eq((int)c.CCR, C, "bld");
        c = NewCpu(out bus, B(0x77, 0xb8)); c.ER[0] = 0x08; c.Step(); Eq((int)c.CCR, 0, "bild");
        c = NewCpu(out bus, B(0x76, 0x38)); c.ER[0] = 0x00; c.CCR = C; c.Step(); Eq((int)c.CCR, 0, "band");
        c = NewCpu(out bus, B(0x74, 0x38)); c.ER[0] = 0x08; c.CCR = 0; c.Step(); Eq((int)c.CCR, C, "bor");
        c = NewCpu(out bus, B(0x75, 0x38)); c.ER[0] = 0x08; c.CCR = C; c.Step(); Eq((int)c.CCR, 0, "bxor");
        c = NewCpu(out bus, B(0x7f, 0x20, 0x70, 0x10)); bus.M[0xffff20] = 0; c.Step(); Eq((int)bus.M[0xffff20], 0x02, "bset #1,@aa:8");
        c = NewCpu(out bus, B(0x7d, 0x10, 0x72, 0x10)); c.ER[1] = 0x3000; bus.M[0x3000] = 0xff; c.Step(); Eq((int)bus.M[0x3000], 0xfd, "bclr #1,@er1");
        c = NewCpu(out bus, B(0x7c, 0x10, 0x73, 0x10)); c.ER[1] = 0x3000; bus.M[0x3000] = 0x02; c.Step(); Eq((int)c.CCR, 0, "btst #1,@er1 set");

        // --- branches: all 16 conditions against the 16 N/Z/V/C combinations (independent truth table) ---
        for (int cond = 0; cond < 16; cond++)
        {
            for (int f = 0; f < 16; f++)
            {
                bool cf = (f & 1) != 0, vf = (f & 2) != 0, zf = (f & 4) != 0, nf = (f & 8) != 0;
                bool expect = cond switch
                {
                    0 => true, 1 => false, 2 => !(cf || zf), 3 => cf || zf, 4 => !cf, 5 => cf, 6 => !zf, 7 => zf,
                    8 => !vf, 9 => vf, 10 => !nf, 11 => nf, 12 => nf == vf, 13 => nf != vf, 14 => !(zf || (nf != vf)), _ => zf || (nf != vf),
                };
                c = NewCpu(out bus, B(0x40 | cond, 0x10));
                c.CCR = (byte)f;
                c.Step();
                Check((c.PC == 0x1012) == expect, $"Bcc cond {cond} flags {f:x}: taken={c.PC == 0x1012}, expected {expect}");
            }
        }
        c = NewCpu(out bus, B(0x58, 0x60, 0x01, 0x00)); c.CCR = 0; c.Step(); Eq(c.PC, 0x1104u, "bne d:16 taken");

        // --- calls ---
        c = NewCpu(out bus, B(0x5e, 0x00, 0x20, 0x00)); bus.M[0x2000] = 0x54; bus.M[0x2001] = 0x70; c.Step();   // jsr @0x2000 ; rts
        Eq(c.PC, 0x2000u, "jsr target"); Eq(c.ER[7], 0x7ffcu, "jsr pushes 4 bytes");
        Eq((int)bus.M[0x7ffc], 0, "jsr stacks 32 bits, upper byte 0"); Eq((int)bus.M[0x7ffe], 0x10, "return address 0x001004 (mid byte)");
        c.Step(); Eq(c.PC, 0x1004u, "rts"); Eq(c.ER[7], 0x8000u, "rts pops 4 bytes");
        c = NewCpu(out bus, B(0x55, 0x10)); c.Step(); Eq(c.PC, 0x1012u, "bsr rel8"); Eq(c.ER[7], 0x7ffcu, "bsr pushes");
        c = NewCpu(out bus, B(0x59, 0x10)); c.ER[1] = 0x00123456; c.Step(); Eq(c.PC, 0x123456u, "jmp @er1");
        c = NewCpu(out bus, B(0x5b, 0x40)); bus.M[0x40] = 0; bus.M[0x41] = 0x12; bus.M[0x42] = 0x34; bus.M[0x43] = 0x56; c.Step();
        Eq(c.PC, 0x123456u, "jmp @@aa:8 reads a 32-bit pointer");

        // --- block move ---
        c = NewCpu(out bus, B(0x7b, 0x5c, 0x59, 0x8f)); c.ER[5] = 0x3000; c.ER[6] = 0x4000; c.ER[4] = 4;
        for (int i = 0; i < 4; i++) bus.M[0x3000 + i] = (byte)(0xa0 + i);
        c.Step();
        Check(bus.M[0x4000] == 0xa0 && bus.M[0x4003] == 0xa3 && (c.ER[4] & 0xff) == 0 && c.ER[5] == 0x3004 && c.ER[6] == 0x4004, "eepmov.b copies r4l bytes er5 -> er6");

        // --- CCR instructions ---
        c = NewCpu(out bus, B(0x06, 0x7f)); c.CCR = 0xff; c.Step(); Eq((int)c.CCR, 0x7f, "andc");
        c = NewCpu(out bus, B(0x04, 0x01)); c.CCR = 0x00; c.Step(); Eq((int)c.CCR, 0x01, "orc");
        c = NewCpu(out bus, B(0x05, 0x03)); c.CCR = 0x01; c.Step(); Eq((int)c.CCR, 0x02, "xorc");
        c = NewCpu(out bus, B(0x07, 0x55)); c.Step(); Eq((int)c.CCR, 0x55, "ldc #imm");
        c = NewCpu(out bus, B(0x03, 0x08)); c.ER[0] = 0xa5; c.Step(); Eq((int)c.CCR, 0xa5, "ldc r0l,ccr");
        c = NewCpu(out bus, B(0x02, 0x08)); c.CCR = 0x5a; c.Step(); Eq(c.ER[0] & 0xff, 0x5au, "stc ccr,r0l");
        c = NewCpu(out bus, B(0x01, 0x40, 0x6b, 0x00, 0x30, 0x00)); bus.M[0x3000] = 0x81; bus.M[0x3001] = 0x00; c.Step(); Eq((int)c.CCR, 0x81, "ldc.w @aa:16,ccr loads the high byte of the word");

        // --- sleep / illegal ---
        c = NewCpu(out bus, B(0x01, 0x80)); c.Step(); Check(c.Sleeping, "sleep");
        c = NewCpu(out bus, B(0x01, 0x10, 0x6d, 0x71)); c.Step(); Check(c.Halted, "H8S-only opcode halts the core"); Eq(c.LastIllegalPc, 0x1000u, "illegal pc reported");

        // --- interrupts: entry layout, nesting mask, rte, and the no-interrupt-after-ldc rule ---
        {
            c = NewCpu(out bus, B(0x00, 0x00, 0x00, 0x00));
            var irq = new OneShotIrq { Vector = 20 };
            c.Interrupts = irq;
            bus.M[20 * 4 + 2] = 0x20; bus.M[20 * 4 + 3] = 0x00;     // vector 20 -> 0x002000
            bus.M[0x2000] = 0x56; bus.M[0x2001] = 0x70;             // rte
            c.CCR = 0x00;
            c.Step();
            Eq(c.PC, 0x2000u, "interrupt vectors through the 32-bit table");
            Eq(c.ER[7], 0x7ffcu, "interrupt pushes CCR+PC (4 bytes)");
            Eq((int)bus.M[0x7ffc], 0x00, "stacked CCR"); Eq((int)bus.M[0x7ffd], 0x00, "stacked PC bits 23-16");
            Eq((int)(bus.M[0x7ffe] << 8 | bus.M[0x7fff]), 0x1000, "stacked PC bits 15-0 = next instruction");
            Check((c.CCR & H8Cpu.FlagI) != 0, "I set on entry");
            c.Step();
            Eq(c.PC, 0x1000u, "rte returns"); Eq(c.ER[7], 0x8000u, "rte pops"); Eq((int)c.CCR, 0, "rte restores CCR");
        }
        {
            // andc unmasks interrupts; the instruction after it still runs before the interrupt is taken
            c = NewCpu(out bus, B(0x06, 0x7f, 0x00, 0x00, 0x00, 0x00));
            var irq = new OneShotIrq { Vector = 20, RequireUnmasked = true };
            c.Interrupts = irq;
            bus.M[20 * 4 + 2] = 0x20;
            c.CCR = H8Cpu.FlagI;
            c.Step();                                        // andc #7f,ccr
            Eq(c.PC, 0x1002u, "andc executed");
            c.Step();                                        // nop: interrupt not sampled yet
            Eq(c.PC, 0x1004u, "one more instruction runs after andc");
            c.Step();                                        // now the interrupt is taken
            Eq(c.PC, 0x2000u, "interrupt taken after the protected instruction");
            Eq((int)(bus.M[0x7ffe] << 8 | bus.M[0x7fff]), 0x1004, "stacked PC after the protected instruction");
        }
    }

    private sealed class OneShotIrq : IH8InterruptSource
    {
        public int Vector; public bool RequireUnmasked; private bool _taken;
        public byte EntryMask => H8Cpu.FlagI;
        public int PendingVector(byte ccr) => _taken || (RequireUnmasked && (ccr & H8Cpu.FlagI) != 0) ? 0 : Vector;
        public void Taken(int vector) => _taken = true;
    }

    // =====================================================================================================
    // Timing (cycle counts measured from MAME 0.264 traces of the real firmware)
    // =====================================================================================================

    private static void TimingTests()
    {
        // The C352 clear loop at 0x4690 of the firmware: 14 instructions + bne = 70 states per iteration (MAME: writes 4.1337 us apart at 16.9344 MHz)
        var loop = new byte[]
        {
            0x79,0x00,0x00,0x00,   0x6f,0xa0,0x00,0x06,   0x6f,0xd0,0x00,0x38,   0x6f,0xd0,0x00,0x3e,   0x6f,0xd0,0x00,0x34,
            0x79,0x00,0xff,0xff,   0x6f,0xd0,0x00,0x00,   0x0d,0x50,
            0x79,0x10,0x00,0x80,   0x6f,0xd0,0x00,0x36,   0x79,0x15,0x00,0xc0,   0x79,0x12,0x00,0x10,
            0x7a,0x04,0x00,0x28,0x02,0x00,   0x1d,0x42,   0x46,0xc8,
        };
        var c = NewCpu(out var bus, loop);
        c.ER[2] = 0x280000; c.ER[5] = 0x8c000;
        long t0 = c.States; StepN(c, 15); long d = c.States - t0;
        Eq(d, 70L, "14-instruction C352 init loop = 70 states per iteration");
        Eq(c.PC, 0x1000u, "loop branches back");

        // Access timing inside an instruction (MAME order: extra words, prefetch_start, data access)
        var times = new System.Collections.Generic.List<long>();
        c = NewCpu(out bus, B(0x6f, 0xa0, 0x00, 0x06));                 // mov.w r0,@(6,er2): write at T+6
        c.ER[2] = 0x2000; bus.OnWrite = (a, t) => times.Add(t);
        long s = c.States; c.Step();
        Eq(times.Count, 1, "one write"); Eq(times[0] - s, 6L, "mov.w r0,@(d16,er) writes at T+6"); Eq(c.States - s, 6L, "...and costs 6");
        times.Clear();
        c = NewCpu(out bus, B(0x38, 0xf2)); bus.OnWrite = (a, t) => times.Add(t); s = c.States; c.Step();           // mov.b r0l,@aa:8
        Eq(times[0] - s, 4L, "mov.b r0l,@aa:8 writes at T+4"); Eq(c.States - s, 4L, "...and costs 4");
        times.Clear();
        var rtimes = new System.Collections.Generic.List<long>();
        c = NewCpu(out bus, B(0x7f, 0xbc, 0x72, 0x70));                 // bclr #7,@aa:8: read T+4, write T+8, total 8
        bus.OnRead = (a, t) => { if (a == 0xffffbc) rtimes.Add(t); }; bus.OnWrite = (a, t) => times.Add(t); s = c.States; c.Step();
        Eq(rtimes[0] - s, 4L, "bclr @aa:8 reads at T+4"); Eq(times[0] - s, 8L, "bclr @aa:8 writes at T+8"); Eq(c.States - s, 8L, "bclr @aa:8 costs 8");
        c = NewCpu(out bus, B(0x46, 0x10)); s = c.States; c.CCR = 0; c.Step(); Eq(c.States - s, 4L, "bne (taken or not) costs 4");
        c = NewCpu(out bus, B(0x51, 0x90)); c.ER[1] = 3; s = c.States; c.Step(); Eq(c.States - s, 14L, "divxu.b costs 14");
        c = NewCpu(out bus, B(0x54, 0x70)); s = c.States; c.Step(); Eq(c.States - s, 10L, "rts costs 10");
    }

    // =====================================================================================================
    // On-chip peripherals
    // =====================================================================================================

    private sealed class StubBoard : IH83002Board
    {
        public readonly byte[] Mem = new byte[1 << 24];
        public readonly System.Collections.Generic.List<(int sci, byte tx)> SyncTx = new();
        public int RxByte = -1;
        public byte ReadPortValue = 0xff;
        public byte ExtRead8(uint a) => Mem[a & 0xffffff];
        public ushort ExtRead16(uint a) => (ushort)((Mem[a & 0xffffff] << 8) | Mem[(a + 1) & 0xffffff]);
        public void ExtWrite8(uint a, byte v) => Mem[a & 0xffffff] = v;
        public void ExtWrite16(uint a, ushort v) { Mem[a & 0xffffff] = (byte)(v >> 8); Mem[(a + 1) & 0xffffff] = (byte)v; }
        public byte ReadPort(int port) => ReadPortValue;
        public void WritePort(int port, byte data, byte ddr) { }
        public ushort ReadAdc(int channel) => (ushort)(0x100 + channel);
        public void SciAsyncTx(int sci, byte value) { }
        public int SciSyncTransfer(int sci, byte tx, bool txEnabled, bool rxEnabled) { SyncTx.Add((sci, tx)); return RxByte; }
    }

    private static void SetVec(byte[] rom, int vec, int addr)
    {
        rom[vec * 4 + 2] = (byte)(addr >> 8); rom[vec * 4 + 3] = (byte)addr;
    }

    private static void PeripheralTests()
    {
        // program: sp = 0x88000; andc #0x3f,ccr (enable interrupts); sleep; (handler at 0x200 counts and returns to the sleep)
        var main = new byte[] { 0x7a, 0x07, 0x00, 0x08, 0x80, 0x00, 0x06, 0x3f, 0x01, 0x80, 0x40, 0xfc };
        var board = new StubBoard();
        var rom = new byte[0x80000];
        rom[2] = 0x01; rom[3] = 0x00;                // reset vector -> 0x000100
        Array.Copy(main, 0, rom, 0x100, main.Length);
        SetVec(rom, 26, 0x200);                   // ITU0 overflow
        SetVec(rom, 13, 0x210);                   // IRQ1
        var handler = new byte[] { 0x0b, 0x56, 0x7f, 0x67, 0x72, 0x20, 0x56, 0x70 };           // inc.w #1,r6 ; bclr #2,@TSR0 ; rte
        Array.Copy(handler, 0, rom, 0x200, handler.Length);
        var h2 = new byte[] { 0x0b, 0x55, 0x56, 0x70 };                                         // inc.w #1,r5 ; rte  (counts IRQ1)
        Array.Copy(h2, 0, rom, 0x210, h2.Length);
        var soc = new H83002(rom, board);
        soc.Cpu.Interrupts = soc.Intc;
        soc.RunUntil(100);                        // run init code, ends asleep with I clear
        Check(soc.Cpu.Sleeping, "init code reached sleep");
        // ITU0: phi/8, TCNT = 0xfff0 -> overflow after 16 ticks of 8 states
        long now = soc.Cpu.States;
        soc.Write8(0xffff64, 0x03);               // TCR0: clock = phi/8
        soc.Write8(0xffff66, 0x04);               // TIER0: OVIE
        soc.Write16(0xffff68, 0xfff0);            // TCNT0
        soc.Write8(0xffff60, 0x01);               // TSTR: start ch0
        long start = soc.Cpu.States;
        long expectOvf = ((((start + 8) >> 3) + 16 - 1) << 3);   // MAME recalc_event formula for delay = 0x10000 - 0xfff0
        soc.RunUntil(expectOvf - 1);
        Eq(soc.Cpu.ER[6] & 0xffff, 0u, "no ITU0 interrupt before the overflow time");
        soc.RunUntil(expectOvf + 200);
        Eq(soc.Cpu.ER[6] & 0xffff, 1u, "ITU0 overflow interrupt taken exactly once");

        // IRQ1: ISCR edge, IER enable, vblank pulse
        soc.Write8(0xfffff4, 0x02); soc.Write8(0xfffff5, 0x02);
        soc.Intc.SetIrq(1, true); soc.RunUntil(soc.Cpu.States + 100); soc.Intc.SetIrq(1, false);
        Eq(soc.Cpu.ER[5] & 0xffff, 1u, "IRQ1 (edge) taken once per assertion");
        soc.Intc.SetIrq(1, true); soc.Intc.SetIrq(1, false); soc.RunUntil(soc.Cpu.States + 100);
        Eq(soc.Cpu.ER[5] & 0xffff, 2u, "IRQ1 pulse latched even if gone before the CPU samples it");

        // SCI1 synchronous transmit timing (MAME 0.264 rules: tick grid of 'divider' states, 17 ticks)
        soc.Write8(0xffffb8, 0x80); soc.Write8(0xffffb9, 0x03); soc.Write8(0xffffba, 0x20);   // sync, BRR=3 (divider 8), TE
        soc.Write8(0xffffbb, 0xa5);
        soc.Write8(0xffffbc, 0x00);               // clear TDRE -> transmission starts
        Eq(board.SyncTx.Count, 1, "SCI1 handed the byte to the peer");
        Eq((int)board.SyncTx[0].tx, 0xa5, "SCI1 byte");
        long first = (soc.Cpu.States / 8 + 1) * 8; long end = first + 128;
        Check((soc.Read8(0xffffbc) & 0x04) == 0, "TEND clear while transmitting");
        soc.RunUntil(end - 1); soc.Tick();
        Check((soc.Read8(0xffffbc) & 0x04) == 0, "TEND still clear one state before the end of the byte");
        soc.RunUntil(end); soc.Tick();
        Check((soc.Read8(0xffffbc) & 0x04) != 0, "TEND set at first tick + 16 ticks");

        // ADC: software start, 266 states (CKS=0) for a single conversion; ADF set, ADST cleared
        soc.Write8(0xffffe9, 0x00);
        long a0 = soc.Cpu.States;
        soc.Write8(0xffffe8, 0x20 | 0x01);        // ADST, channel 1
        soc.RunUntil(a0 + 265); soc.Tick();
        Check((soc.Read8(0xffffe8) & 0x80) == 0, "ADF clear before the conversion time");
        soc.RunUntil(a0 + 266); soc.Tick();
        Check((soc.Read8(0xffffe8) & 0x80) != 0, "ADF set after 266 states");
        Check((soc.Read8(0xffffe8) & 0x20) == 0, "ADST cleared after a single conversion");
        Eq((int)soc.Read8(0xffffe2), 0x101 >> 2, "ADDRB high byte = channel 1 sample >> 2");

        // port 7 / DSW pass-through (input pins)
        board.ReadPortValue = 0xa5;
        Eq((int)soc.Read8(0xffffce), 0xa5, "P7PIN reads the board pins");
    }

    // =====================================================================================================
    // Optional regression with the user's ROM regions
    // =====================================================================================================

    /// <summary>
    /// Boots the unmodified sub.bin on the emulated board for 6.2 s and compares the C352 register write list with the list captured from
    /// MAME 0.264 (1,201 writes, no main-CPU involvement) - content hash and cycle-exact first-write time.
    /// </summary>
    public static int RunFirmwareRegression(byte[] subBin, byte[] c352Bin, Action<string>? log = null)
    {
        _fail = 0; _pass = 0; _log = log ?? (_ => { });
        var board = new S12SoundBoard(subBin, c352Bin);
        board.Mcu.Intc.MameIcrQuirk = true;       // MAME's ICR byte handling; needed for identical interrupt nesting (see H8Intc)
        var sb = new StringBuilder();
        int count = 0; long firstStates = -1;
        board.C352WriteTrace = (st, addr, data, mask) =>
        {
            if (firstStates < 0) firstStates = st;
            sb.Append(addr.ToString("x")).Append(' ').Append(data.ToString("x4")).Append(' ').Append(mask.ToString("x4")).Append('\n');
            count++;
        };
        board.RunUntil((long)(6.2 * H83002.ClockHz));
        Eq(count, 1201, "C352 writes in the first 6.2 s of boot (MAME 0.264: 1201)");
        Eq(firstStates, 224014L, "first C352 write at state 224014 (MAME: 0.013228340 s)");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString()))).ToLowerInvariant();
        Eq(hash, "11d202c1537c2c902c4896e2ee9fc6565d282727023543d1b4d588faf72c965c", "SHA-256 of the C352 write list (addr data mask) equals MAME's");
        Check(!board.Mcu.Cpu.Halted, "CPU did not halt on an unimplemented instruction");
        _log($"firmware regression: {_pass} checks passed, {_fail} failed");
        return _fail;
    }
}
