// SPDX-License-Identifier: BSD-3-Clause
//
// Namco System 12 sound/IO sub-board: H8/3002 (running the unmodified sub.bin firmware) + C352 + the devices
// wired to the H8 in MAME's namcos12.cpp (shared RAM window, RTC R4543 on SCI1, "settings" serial sink,
// JAMMA input words at 0x300000/2, dip switches on port 7).
//
// Declared EMULATED COMPONENT: the sound program is *executed* (interpreted), not re-implemented.
// Wiring facts and their sources (MAME 0.264 namcos12.cpp unless noted):
//   H8 clock 16.9344 MHz; C352 clock 25.4016 MHz / 288 => 88.2 kHz = exactly 192 H8 states per output frame.
//   H8 map: ROM 0x000000-0x07ffff, shared RAM 0x080000-0x08ffff (main CPU sees the first 0x4000 bytes at
//   0x1f080000), C352 0x280000-0x287fff, IN0/IN1 0x300000/0x300002 (16-bit, active low), 0x300010/0x300030 nop.
//   Port 6 read = 0xfd|(jvssense<<1), port 7 = DSW, port 8 read = 0x02, port A/B as in s12_mcu_pa/pb handlers,
//   vblank drives IRQ1 + ADC trigger + port B bit 7.
//
// NOT modelled: JVS bus (SCI0 transmit is discarded, nothing answers), the "settings" device (write-only sink),
// RTC write mode, host audio output.

using System;
using System.Collections.Generic;

namespace System12.Runtime.Sound;

public sealed class S12SoundBoard : IH83002Board
{
    public const int StatesPerAudioFrame = H83002.ClockHz / C352.System12SampleRate; // 192

    public readonly H83002 Mcu;
    public readonly C352 Chip;
    private readonly byte[] _shared = new byte[0x10000];

    // --- external inputs (host-driven) -----------------------------------------------------------------------------
    /// <summary>IN0 (0x300000) JAMMA word, active low. 0xffff = nothing pressed.</summary>
    public ushort In0 = 0xffff;
    /// <summary>IN1 (0x300002): coins/service/buttons 4-6, active low.</summary>
    public ushort In1 = 0xffff;
    /// <summary>Port 7 dip switches (bit7 service mode off, bit6 freeze off => 0xff).</summary>
    public byte Dsw = 0xff;
    public readonly short[] AdcValues = new short[8];

    // --- vblank source -------------------------------------------------------------------------------------------
    public bool ExternalVBlank;                        // true: host calls SetVBlank()
    public double FramePeriodSeconds = 0.016715113234; // MAME tektagt screen (256x240, 59.8261 Hz): frame_period = 16.715113234 ms
    public double VBlankSeconds = 0.0025;
    private long _nextVblankStart, _nextVblankEnd, _frameIndex;
    private bool _vblank;

    // --- ports / RTC ------------------------------------------------------------------------------------------------
    private byte _portA, _portB = 0x50;
    private readonly Rtc4543 _rtc = new();
    /// <summary>Port 6 bit 1 ("JVS cable present", 0.264 s12_mcu_p6_r). No JVS device is emulated, so this stays false unless the host sets it.</summary>
    public bool JvsSense;

    /// <summary>
    /// Set the real-time-clock chip's current time (read by the firmware over SCI1 at boot and exported to the main CPU).
    /// The default is a fixed date so that runs are reproducible; a runtime wanting wall-clock time calls SetRtc(DateTime.Now).
    /// Note: the firmware branches on the RTC year (>= 0x70 is treated as 19xx), so the value is observable in its control flow.
    /// </summary>
    public void SetRtc(DateTime time) => _rtc.SetTime(time, Mcu.Cpu.States);

    // --- audio ---------------------------------------------------------------------------------------------------------
    private long _audioFramesDone;
    private short[] _audioBuf = new short[2 * 4096];
    private int _audioCount; // frames in buffer
    public bool CaptureAudio = true;

    /// <summary>Optional trace of C352 register writes: (H8 state count, byte address, data, mask) in MAME bus-mask convention.</summary>
    public Action<long, uint, ushort, ushort>? C352WriteTrace;

    public S12SoundBoard(byte[] subProgram, byte[] c352Rom)
    {
        if (subProgram.Length != 0x80000) throw new ArgumentException("sub program must be 512 KiB");
        Chip = new C352(c352Rom);
        Mcu = new H83002(subProgram, this);
        _rtc.SetTime(new DateTime(2026, 10, 2, 5, 8, 35, DateTimeKind.Unspecified), 0);
        _nextVblankStart = FrameStart(1);
        _nextVblankEnd = long.MaxValue;
    }

    public long Now => Mcu.Cpu.States;

    // frame n starts at floor(n * period * clock): MAME converts attotime to cycles by truncation (verified on the traces)
    private long FrameStart(long n) => (long)Math.Floor(n * FramePeriodSeconds * H83002.ClockHz + 1e-4);

    // ---- main CPU window ------------------------------------------------------------------------------------------------
    /// <summary>Main CPU 16-bit read at byte offset 'off' (even) in the 0x1f080000-0x1f083fff window.</summary>
    public ushort MainRead16(int off) => (ushort)((_shared[off & 0x3ffe] << 8) | _shared[(off & 0x3ffe) + 1]);

    public void MainWrite16(int off, ushort value, ushort mask = 0xffff)
    {
        off &= 0x3ffe;
        ushort cur = (ushort)((_shared[off] << 8) | _shared[off + 1]);
        cur = (ushort)((cur & ~mask) | (value & mask));
        _shared[off] = (byte)(cur >> 8); _shared[off + 1] = (byte)cur;
    }

    /// <summary>
    /// Main-CPU 32-bit bus read at byte offset 'off' (multiple of 4) of the 0x1f080000-0x1f083fff window. The 16-bit shared-RAM handler
    /// answers on both halfword lanes: low half = H8 word at 0x080000+off, high half = H8 word at 0x080000+off+2 (verified: the main CPU
    /// writes its heartbeat to the high half of 0x1f0830f0 and the H8 firmware polls 0x0830f2).
    /// </summary>
    public uint MainRead32(int off) => (uint)(MainRead16(off) | (MainRead16(off + 2) << 16));

    /// <summary>Main-CPU 32-bit bus write with byte-lane mask (e.g. 0xffff0000 for a halfword store to the upper lane).</summary>
    public void MainWrite32(int off, uint data, uint mask = 0xffffffff)
    {
        if ((mask & 0xffff) != 0) MainWrite16(off, (ushort)data, (ushort)mask);
        if ((mask >> 16) != 0) MainWrite16(off + 2, (ushort)(data >> 16), (ushort)(mask >> 16));
    }

    /// <summary>Direct view of the shared RAM (H8 offsets 0x080000..0x08ffff).</summary>
    public byte[] SharedRam => _shared;

    // ---- time control --------------------------------------------------------------------------------------------------
    public void SetVBlank(bool state)
    {
        if (state == _vblank) return;
        _vblank = state;
        Mcu.Intc.SetIrq(1, state);
        Mcu.Adc.Adtrg(state, Mcu.Cpu.States); // MAME: m_adc->adtrg_w(state); conversion starts on the falling edge (vblank end)
        _portB = (byte)((_portB & 0x7f) | (state ? 0x80 : 0));
    }

    /// <summary>Run the board until the H8 reaches <paramref name="untilStates"/>.</summary>
    public void RunUntil(long untilStates)
    {
        while (Mcu.Cpu.States < untilStates)
        {
            long seg = untilStates;
            if (!ExternalVBlank)
            {
                long next = _vblank ? _nextVblankEnd : _nextVblankStart;
                if (next < seg) seg = next;
            }
            if (seg > Mcu.Cpu.States) Mcu.RunUntil(seg);
            if (!ExternalVBlank)
            {
                if (!_vblank && Mcu.Cpu.States >= _nextVblankStart)
                {
                    SetVBlank(true);
                    _nextVblankEnd = _nextVblankStart + (long)(VBlankSeconds * H83002.ClockHz);
                    _frameIndex++;
                    _nextVblankStart = FrameStart(_frameIndex + 1);
                }
                else if (_vblank && Mcu.Cpu.States >= _nextVblankEnd)
                {
                    SetVBlank(false);
                    _nextVblankEnd = long.MaxValue;
                }
            }
        }
        SyncChip();
    }

    /// <summary>Generate C352 output frames up to the current H8 time (mirrors MAME's stream->update() on every register access).</summary>
    public void SyncChip()
    {
        long target = Mcu.Cpu.States / StatesPerAudioFrame;
        while (_audioFramesDone < target)
        {
            Chip.GenerateSample(out short l, out short r, out _, out _);
            if (CaptureAudio)
            {
                if (2 * (_audioCount + 1) > _audioBuf.Length) Array.Resize(ref _audioBuf, _audioBuf.Length * 2);
                _audioBuf[2 * _audioCount] = l; _audioBuf[2 * _audioCount + 1] = r; _audioCount++;
            }
            _audioFramesDone++;
        }
    }

    public int AvailableAudioFrames => _audioCount;

    /// <summary>Drain up to 'maxFrames' interleaved stereo frames (L,R) generated so far.</summary>
    public int ReadAudio(Span<short> interleaved, int maxFrames)
    {
        int n = Math.Min(maxFrames, _audioCount);
        _audioBuf.AsSpan(0, 2 * n).CopyTo(interleaved);
        Array.Copy(_audioBuf, 2 * n, _audioBuf, 0, 2 * (_audioCount - n));
        _audioCount -= n;
        return n;
    }

    // ---- IH83002Board: external bus ----------------------------------------------------------------------------------------
    public byte ExtRead8(uint a)
    {
        ushort w = ExtRead16(a & ~1u);
        return (byte)((a & 1) != 0 ? w : w >> 8);
    }

    public ushort ExtRead16(uint a)
    {
        a &= 0xffffff;
        if (a >= 0x080000 && a < 0x090000)
            return (ushort)((_shared[a - 0x080000] << 8) | _shared[a - 0x080000 + 1]);
        if (a >= 0x280000 && a < 0x288000)
        {
            SyncChip();
            return Chip.Read((int)(a - 0x280000) >> 1);
        }
        if (a == 0x300000) return In0;
        if (a == 0x300002) return In1;
        Mcu.UnmappedReads++;
        return 0;
    }

    public void ExtWrite8(uint a, byte v)
    {
        a &= 0xffffff;
        if (a >= 0x080000 && a < 0x090000) { _shared[a - 0x080000] = v; return; }
        if (a >= 0x280000 && a < 0x288000)
        {
            SyncChip();
            C352WriteTrace?.Invoke(Mcu.Cpu.States, a & ~1u, (a & 1) != 0 ? v : (ushort)(v << 8), (a & 1) != 0 ? (ushort)0x00ff : (ushort)0xff00);
            Chip.Write8((int)(a - 0x280000), v);
            return;
        }
        if (a >= 0x300000 && a < 0x300040) return; // 0x300010 / 0x300030 nop
        Mcu.UnmappedWrites++;
    }

    public void ExtWrite16(uint a, ushort v)
    {
        a &= 0xffffff;
        if (a >= 0x080000 && a < 0x090000) { _shared[a - 0x080000] = (byte)(v >> 8); _shared[a - 0x080000 + 1] = (byte)v; return; }
        if (a >= 0x280000 && a < 0x288000)
        {
            SyncChip();
            C352WriteTrace?.Invoke(Mcu.Cpu.States, a, v, 0xffff);
            Chip.Write((int)(a - 0x280000) >> 1, v, 0xffff);
            return;
        }
        if (a >= 0x300000 && a < 0x300040) return;
        Mcu.UnmappedWrites++;
    }

    // ---- ports --------------------------------------------------------------------------------------------------------------
    public byte ReadPort(int port)
    {
        switch (port)
        {
            case 6: return (byte)((JvsSense ? 2 : 0) | 0xfd);
            case 7: return Dsw;
            case 8: return 0x02;
            case 10: return _portA;
            case 11: return _portB;
            default: return 0xff;
        }
    }

    public void WritePort(int port, byte data, byte ddr)
    {
        switch (port)
        {
            case 10:
                _portA = data;
                UpdateCe();
                break;
            case 11:
                _portB = (byte)((_portB & 0x80) | (data & 0x7f));
                UpdateCe();
                break;
        }
    }

    private void UpdateCe()
    {
        // m_rtc->ce_w(portB.5 && portA.0); settings ce = portB.5 && !portA.0 (settings device is a sink)
        _rtc.SetCe((_portB & 0x20) != 0 && (_portA & 1) != 0);
    }

    public ushort ReadAdc(int channel) => (ushort)(channel < 8 ? AdcValues[channel] : 0);

    // ---- serial ---------------------------------------------------------------------------------------------------------------
    public void SciAsyncTx(int sci, byte value) { /* JVS port: no device attached */ }

    public int SciSyncTransfer(int sci, byte tx, bool txEnabled, bool rxEnabled)
    {
        if (sci != 1) return -1;
        _rtc.Tick(Mcu.Cpu.States);
        int rx = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            // SCI clock falls (RTC sees rising edge: MAME inverts the clock) -> RTC presents the next bit; SCI samples on the rising edge.
            int d = _rtc.ClockRising();
            if (d != 0) rx |= 1 << bit;
        }
        return rxEnabled ? rx : -1;
    }

    // ---- Epson R4543 real-time clock (read mode only) -------------------------------------------------------------
    private sealed class Rtc4543
    {
        private readonly byte[] _regs = new byte[7];
        private bool _ce;
        private int _curbit;
        private DateTime _time;
        private long _stateBase;
        private const long StatesPerSecond = H83002.ClockHz;

        public void SetTime(DateTime t, long nowStates) { _time = t; _stateBase = nowStates; Load(); }
        public void SetCe(bool ce)
        {
            if (ce && !_ce) _curbit = 0;
            _ce = ce;
        }

        public void Tick(long nowStates)
        {
            long secs = (nowStates - _stateBase) / StatesPerSecond;
            if (secs > 0) { _time = _time.AddSeconds(secs); _stateBase += secs * StatesPerSecond; Load(); }
        }

        private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

        private void Load()
        {
            int dow = _time.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)_time.DayOfWeek;
            _regs[0] = Bcd(_time.Second); _regs[1] = Bcd(_time.Minute); _regs[2] = Bcd(_time.Hour);
            _regs[3] = Bcd(dow); _regs[4] = Bcd(_time.Day); _regs[5] = Bcd(_time.Month); _regs[6] = Bcd(_time.Year % 100);
        }

        private int _data;
        public int ClockRising()
        {
            if (!_ce || _curbit == 56) return _data;
            int reg = _curbit / 8, bit = _curbit & 7;
            _data = (_regs[reg] >> bit) & 1;
            _curbit++;
            if (_curbit == 28) _curbit += 4; // day-of-week register has only 4 bits
            return _data;
        }
    }
}
