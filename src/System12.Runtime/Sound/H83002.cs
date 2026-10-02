// SPDX-License-Identifier: BSD-3-Clause
//
// Hitachi H8/3002 on-chip peripherals + system bus, as wired on the Namco System 12 motherboard.
//
// Declared EMULATED COMPONENT (see H8Cpu.cs). Peripheral behaviour is modelled after MAME's H8 device family
// (src/devices/cpu/h8/h8_intc.cpp, h8_timer16.cpp, h8_sci.cpp, h8_adc.cpp, h8_port.cpp, h83002.cpp;
// license BSD-3-Clause, copyright-holders Olivier Galibert), re-implemented here at a coarser grain.
// What is and is not modelled is listed in the class comments and in docs/system12/SOUND.md.
//
// General honesty notes:
//  * Timing is expressed in CPU states (phi = 16.9344 MHz on System 12). The CPU core's state counts are an
//    approximation (see H8Cpu), so interrupt/timer phase relative to instruction streams differs from real
//    hardware and from MAME at the instruction level; frequency-domain behaviour (timer periods) is exact.
//  * SCI is modelled at BYTE level (transfer start -> completion after the exact bit-clock duration);
//    individual bit edges are not simulated; attached peripherals see whole bytes.
//  * DMAC, serial 'smart card' modes, input capture/PWM/phase counting of the ITUs, software standby and
//    the refresh controller are register-store only.

using System;

namespace System12.Runtime.Sound;

/// <summary>Everything outside the MCU: external bus, port pins, ADC inputs, serial peers.</summary>
public interface IH83002Board
{
    byte ExtRead8(uint addr);
    ushort ExtRead16(uint addr);
    void ExtWrite8(uint addr, byte value);
    void ExtWrite16(uint addr, ushort value);

    /// <summary>External pin levels of port 'port' (4,6,7,8,9, 10=A, 11=B). Only bits whose DDR is 0 are used.</summary>
    byte ReadPort(int port);
    /// <summary>Port output changed: 'data' = driven value, 'ddr' = output-enable mask.</summary>
    void WritePort(int port, byte data, byte ddr);
    ushort ReadAdc(int channel);

    /// <summary>Byte transmitted by an SCI in asynchronous mode.</summary>
    void SciAsyncTx(int sci, byte value);
    /// <summary>A synchronous (clocked) SCI transfer of 8 bits. Returns the byte received on RXD (or -1 if nothing drives it).</summary>
    int SciSyncTransfer(int sci, byte tx, bool txEnabled, bool rxEnabled);
}

/// <summary>H8/3002 interrupt controller (IRQ0-5, NMI, internal vectors) with MAME-compatible latching semantics.</summary>
public sealed class H8Intc : IH8InterruptSource
{
    public const int VecNmi = 7, VecIrqBase = 12;

    private readonly bool[] _pending = new bool[64];
    public byte Ier, Isr, Iscr;
    public byte IcrA, IcrB;
    public byte Syscr = 0x09;       // reset value (UE=1, NMIEG=0)
    private byte _irqInput;          // pin states (1 = asserted/active)
    private bool _nmiInput;

    public byte EntryMask => (Syscr & 0x08) != 0 ? H8Cpu.FlagI : (byte)(H8Cpu.FlagI | H8Cpu.FlagUI);

    /// <summary>Raised by peripherals when an internal interrupt condition occurs.</summary>
    private int _pendingCount;
    public void Internal(int vector) { if (!_pending[vector]) { _pending[vector] = true; _pendingCount++; } }

    public void SetIrq(int n, bool asserted)
    {
        byte mask = (byte)(1 << n);
        bool cur = (_irqInput & mask) != 0;
        bool set = false;
        bool levelType = (Iscr & mask) == 0;
        if (levelType) set = asserted;
        else set = asserted && !cur; // falling edge on the pin == assertion in MAME's polarity convention
        if (asserted) _irqInput |= mask; else _irqInput &= (byte)~mask;
        if (set) Isr |= mask;
        // level type: ISR reflects the pin level (not latched) is NOT the case on H8H (has ISR): MAME latches too.
    }

    public void SetNmi(bool asserted)
    {
        bool set = asserted && !_nmiInput;
        _nmiInput = asserted;
        if (set && !_pending[VecNmi]) { _pending[VecNmi] = true; _pendingCount++; }
    }

    public void WriteIer(byte v) { Ier = v; }
    public void WriteIscr(byte v)
    {
        Iscr = v;
        // switching a pin from level to edge clears nothing on H8H (has ISR). Level-low pins that are asserted and
        // not yet latched get latched (MAME check_level_irqs).
        for (int i = 0; i < 6; i++)
        {
            byte m = (byte)(1 << i);
            if ((Iscr & m) == 0 && (_irqInput & m) != 0 && (Isr & m) == 0) Isr |= m;
        }
    }
    public void WriteIsr(byte v)
    {
        Isr &= v; // write 0 to clear
        for (int i = 0; i < 6; i++)
        {
            byte m = (byte)(1 << i);
            if ((Iscr & m) == 0 && (_irqInput & m) != 0 && (Isr & m) == 0) Isr |= m; // level still active: stays latched
        }
    }

    private static int IcrBitFor(int vec)
    {
        // Returns the ICR priority bit (0..15, ICRA = bits 0..7, ICRB = bits 8..15) of a vector, or -1.
        // Mapping follows MAME's h8h_intc_device::vector_to_slot (bit = slot ^ 7), which is the reference behaviour used here:
        if (vec == 12) return 7;                 // IRQ0
        if (vec == 13) return 6;                 // IRQ1
        if (vec == 14 || vec == 15) return 5;    // IRQ2,3
        if (vec >= 16 && vec <= 19) return 4;    // IRQ4,5
        if (vec >= 20 && vec <= 23) return 3;    // WDT, refresh, (ADI on other family members)
        if (vec >= 24 && vec <= 27) return 2;    // ITU0
        if (vec >= 28 && vec <= 31) return 1;    // ITU1
        if (vec >= 32 && vec <= 35) return 0;    // ITU2
        if (vec >= 36 && vec <= 39) return 15;   // ITU3
        if (vec >= 40 && vec <= 43) return 14;   // ITU4
        if (vec >= 44 && vec <= 47) return 13;   // DMAC
        if (vec >= 48 && vec <= 51) return 12;   // reserved
        if (vec >= 52 && vec <= 55) return 11;   // SCI0
        if (vec >= 56 && vec <= 59) return 10;   // SCI1
        if (vec >= 60 && vec <= 63) return 9;    // ADI (H8/3002 vector 60)
        return -1;
    }

    private int Priority(int vec)
    {
        if (vec == VecNmi) return 2;
        int b = IcrBitFor(vec);
        if (b < 0) return 0;
        int icr = IcrA | (IcrB << 8);
        return (icr >> b) & 1;
    }

    public int PendingVector(byte ccr)
    {
        if (_pendingCount == 0 && (Isr & Ier) == 0) return 0;
        // Derive IRQ vectors from ISR & IER
        int minLevel;
        bool ue = (Syscr & 0x08) != 0;
        bool i = (ccr & H8Cpu.FlagI) != 0, ui = (ccr & H8Cpu.FlagUI) != 0;
        if (ue) minLevel = i ? 2 : 0;
        else minLevel = i && ui ? 2 : i ? 1 : 0;

        int best = 0, bestLevel = -1;
        for (int v = 1; v < 64; v++)
        {
            bool p = _pending[v];
            if (v >= VecIrqBase && v < VecIrqBase + 6) p = (Isr & Ier & (1 << (v - VecIrqBase))) != 0;
            if (!p) continue;
            int lvl = Priority(v);
            if (lvl < minLevel) continue;
            if (lvl > bestLevel) { best = v; bestLevel = lvl; }
        }
        return best;
    }

    public void Taken(int vector)
    {
        if (_pending[vector]) { _pending[vector] = false; _pendingCount--; }
        if (vector >= VecIrqBase && vector < VecIrqBase + 6)
        {
            int n = vector - VecIrqBase;
            byte m = (byte)(1 << n);
            bool levelType = (Iscr & m) == 0;
            if (!levelType || (_irqInput & m) == 0) Isr &= (byte)~m;
        }
    }
}

/// <summary>One H8 16-bit integrating timer (ITU) channel. Internal clock only (phi/1,2,4,8); GRA/GRB compare, overflow.</summary>
public sealed class H8Itu
{
    private readonly H83002 _soc;
    private readonly int _vecBase;
    public byte Tcr, Tier, Tior;
    public ushort Tcnt;
    public readonly ushort[] Tgr = { 0xffff, 0xffff };
    public readonly ushort[] Brg = { 0xffff, 0xffff };
    private byte _isr;               // bit0 IMFA, bit1 IMFB, bit2 OVF
    private int _ier;                // bit0 A, bit1 B, bit2 V (from TIER)
    private int _clearing = -1;      // -1 none, 0 GRA, 1 GRB, 2 external
    private int _clockDivider;
    private bool _internalClock = true;
    private int _phase;
    private long _counterCycle = 0x10000;
    private bool _active;
    private long _lastUpdate;
    public long EventTime;           // absolute state count of the next interrupt-relevant event (0 = none)

    public H8Itu(H83002 soc, int vecBase) { _soc = soc; _vecBase = vecBase; Reset(); }

    public void Reset()
    {
        Tcr = 0; Tcnt = 0; Tier = 0xf8; Tior = 0x88;
        Tgr[0] = Tgr[1] = 0xffff;
        _isr = 0; _ier = 0; _clearing = -1; _clockDivider = 0; _internalClock = true; _phase = 0; _counterCycle = 0x10000;
        _lastUpdate = 0; EventTime = 0;
    }

    public void SetActive(bool on, long now) { Update(now); _active = on; Recalc(now); }

    public byte ReadSr(long now) { Update(now); return (byte)(0xf8 | (_isr & 7)); }
    public void WriteSr(byte v, long now)
    {
        Update(now);
        if ((v & 1) == 0) _isr &= unchecked((byte)~1);
        if ((v & 2) == 0) _isr &= unchecked((byte)~2);
        if ((v & 4) == 0) _isr &= unchecked((byte)~4);
        Recalc(now);
    }

    public void WriteTcr(byte v, long now)
    {
        Update(now);
        Tcr = v;
        switch (v & 0x60)
        {
            case 0x00: _clearing = -1; break;
            case 0x20: _clearing = 0; break;
            case 0x40: _clearing = 1; break;
            default: _clearing = 2; break;
        }
        int ct = v & 7;
        if (ct < 4)
        {
            _internalClock = true;
            _clockDivider = ct;
            if (ct <= 1) _phase = 0;
            else
            {
                switch (v & 0x18)
                {
                    case 0x00: _phase = 0; break;
                    case 0x08: _phase = 1 << (_clockDivider - 1); break;
                    default: _phase = 0; _clockDivider--; break;
                }
            }
        }
        else { _internalClock = false; _clockDivider = 0; _phase = 0; } // external clock input: not modelled
        Recalc(now);
    }

    public void WriteTier(byte v, long now)
    {
        Update(now);
        Tier = (byte)(v | 0xf8);
        _ier = (v & 1) | (v & 2) | (v & 4);
        Recalc(now);
    }

    public ushort ReadTcnt(long now) { Update(now); return Tcnt; }
    public void WriteTcnt(ushort v, long now) { Update(now); Tcnt = v; Recalc(now); }
    public void WriteTgr(int i, ushort v, long now) { Update(now); Tgr[i] = v; Recalc(now); }

    private void Raise(int bit)
    {
        _isr |= (byte)(1 << bit);
        if ((_ier & (1 << bit)) != 0)
            _soc.Intc.Internal(_vecBase + (bit == 2 ? 2 : bit));
    }

    /// <summary>Advance the counter to 'cur' (absolute state count), recording match/overflow flags (port of MAME update_counter).</summary>
    public void Update(long cur)
    {
        if (!_internalClock) return;
        if (!_active) { _lastUpdate = cur; return; }

        long baseT = _lastUpdate;
        _lastUpdate = cur;
        long newT = cur;
        if (_clockDivider != 0)
        {
            baseT = (baseT + _phase) >> _clockDivider;
            newT = (newT + _phase) >> _clockDivider;
        }
        if (newT == baseT) return;

        long prev = Tcnt;
        long delta = newT - baseT;
        long tt = Tcnt + delta;

        if (prev >= _counterCycle)
        {
            if (tt >= 0x10000) Tcnt = (ushort)((tt - 0x10000) % _counterCycle);
            else Tcnt = (ushort)tt;
        }
        else Tcnt = (ushort)(tt % _counterCycle);

        for (int i = 0; i < 2; i++)
        {
            ushort cmp = (ushort)(Tgr[i] + 1);
            bool match = Tcnt == cmp || (tt == cmp && tt == _counterCycle);
            if (!match)
            {
                if (prev >= _counterCycle)
                    match = (cmp > prev && tt >= cmp) || (cmp <= _counterCycle && Tcnt < _counterCycle && (delta - (0x10000 - prev)) >= cmp);
                else if (cmp <= _counterCycle)
                    match = delta >= _counterCycle || (prev < cmp && tt >= cmp) || (Tcnt <= prev && Tcnt >= cmp);
            }
            if (match) Raise(i);
        }
        if (tt >= 0x10000 && (_counterCycle == 0x10000 || prev >= _counterCycle))
            Raise(2);
    }

    /// <summary>Compute the next absolute time at which an enabled interrupt condition happens (port of recalc_event).</summary>
    public void Recalc(long cur)
    {
        if (!_active || !_internalClock) { EventTime = 0; _soc.Reschedule(); return; }

        long delay = long.MaxValue;
        _counterCycle = _clearing is 0 or 1 ? (long)Tgr[_clearing] + 1 : 0x10000;
        if ((_ier & 4) != 0 && (_counterCycle == 0x10000 || Tcnt >= _counterCycle))
            delay = 0x10000 - Tcnt;
        for (int i = 0; i < 2; i++)
        {
            if ((_ier & (1 << i)) == 0) continue;
            long nd = long.MaxValue;
            ushort cmp = (ushort)(Tgr[i] + 1);
            if (cmp > Tcnt)
            {
                if (Tcnt >= _counterCycle || cmp <= _counterCycle) nd = cmp - Tcnt;
            }
            else if (cmp <= _counterCycle)
            {
                if (Tcnt < _counterCycle) nd = (_counterCycle - Tcnt) + cmp;
                else nd = (0x10000 - Tcnt) + cmp;
            }
            if (nd < delay) delay = nd;
        }
        if (delay != long.MaxValue)
            EventTime = ((((cur + (1L << _clockDivider) - _phase) >> _clockDivider) + delay - 1) << _clockDivider) + _phase;
        else EventTime = 0;
        _soc.Reschedule();
    }

    public void Service(long now)
    {
        while (EventTime != 0 && now >= EventTime)
        {
            long t = EventTime;
            Update(t);
            Recalc(t);
        }
    }
}

/// <summary>Serial communications interface (byte-level model).</summary>
public sealed class H8Sci
{
    private readonly H83002 _soc;
    private readonly int _id;
    private readonly int _vecEri, _vecRxi, _vecTxi, _vecTei;
    public byte Smr, Brr = 0xff, Scr, Tdr = 0xff, Ssr = 0x84, Rdr;
    private byte _ssrRead;
    private byte _tsr;
    private bool _txBusy, _rxBusy;
    private long _txDone, _rxDone;
    public long EventTime;

    public H8Sci(H83002 soc, int id, int vecEri)
    {
        _soc = soc; _id = id;
        _vecEri = vecEri; _vecRxi = vecEri + 1; _vecTxi = vecEri + 2; _vecTei = vecEri + 3;
    }

    private const byte SsrTdre = 0x80, SsrRdrf = 0x40, SsrOrer = 0x20, SsrFer = 0x10, SsrPer = 0x08, SsrTend = 0x04, SsrMpb = 0x02, SsrMpbt = 0x01;
    private const byte ScrTie = 0x80, ScrRie = 0x40, ScrTe = 0x20, ScrRe = 0x10, ScrMpie = 0x08, ScrTeie = 0x04;
    private bool Sync => (Smr & 0x80) != 0;

    /// <summary>States per byte on the wire for the current SMR/BRR (sync: 8 bits; async: start+8+stop).</summary>
    private long ByteStates()
    {
        long div = (2L << (2 * (Smr & 3))) * (Brr + 1);
        if (Sync) return 17 * div;                 // 8 bit-clocks (2 ticks each) + final tick, MAME tx_sync_tick
        int bits = 1 + ((Smr & 0x40) != 0 ? 7 : 8) + ((Smr & 0x20) != 0 ? 1 : 0) + ((Smr & 0x08) != 0 ? 2 : 1);
        return 16 * div * bits;
    }

    public byte ReadSsr() { _ssrRead = Ssr; return Ssr; }
    public byte ReadRdr() => Rdr;

    public void WriteSsr(byte v, long now)
    {
        // clear flags by writing 0 after reading 1 (flags other than TDRE/TEND/MPB follow read-then-write-0)
        if ((Scr & ScrTe) != 0 && (Ssr & _ssrRead & SsrTdre) != 0 && (v & SsrTdre) == 0)
            Ssr &= unchecked((byte)~(SsrTdre | SsrTend));
        Ssr = (byte)(((Ssr & (~_ssrRead | v | SsrTdre | SsrTend | SsrMpb)) & ~SsrMpbt) | (v & SsrMpbt));
        _ssrRead &= Ssr;
        TryStartTx(now);
        TryStartRx(now);
    }

    public void WriteTdr(byte v) { Tdr = v; }

    public void WriteScr(byte v, long now)
    {
        byte delta = (byte)(Scr ^ v);
        Scr = v;
        if ((delta & ScrRe) != 0 && (Scr & ScrRe) == 0) _rxBusy = false;
        if ((delta & ScrTie) != 0 && (Scr & ScrTie) != 0 && (Ssr & SsrTdre) != 0) _soc.Intc.Internal(_vecTxi);
        if ((delta & ScrTeie) != 0 && (Scr & ScrTeie) != 0 && (Ssr & SsrTend) != 0) _soc.Intc.Internal(_vecTei);
        if ((delta & ScrRie) != 0 && (Scr & ScrRie) != 0 && (Ssr & SsrRdrf) != 0) _soc.Intc.Internal(_vecRxi);
        TryStartTx(now);
        TryStartRx(now);
    }

    public void WriteSmr(byte v) { Smr = v; }
    public void WriteBrr(byte v) { Brr = v; }

    private void TryStartTx(long now)
    {
        if (_txBusy) return;
        if ((Ssr & SsrTdre) != 0) return;     // nothing queued
        if ((Scr & ScrTe) == 0) return;
        StartTx(now);
    }

    private void StartTx(long now)
    {
        _tsr = Tdr;
        Ssr |= SsrTdre;
        if ((Scr & ScrTie) != 0) _soc.Intc.Internal(_vecTxi);
        _txBusy = true;
        _txDone = now + ByteStates();
        if (Sync)
        {
            // clocked transfer: peer sees the byte now (bit edges are not simulated individually); RX data returned at completion.
            bool rx = (Scr & ScrRe) != 0;
            int r = _soc.Board.SciSyncTransfer(_id, _tsr, true, rx);
            if (rx && r >= 0) { _pendingRx = (byte)r; _pendingRxValid = true; }
            _rxBusy = false;
        }
        else _soc.Board.SciAsyncTx(_id, _tsr);
        UpdateEvent();
    }

    private byte _pendingRx; private bool _pendingRxValid;

    private void TryStartRx(long now)
    {
        // Sync receive only (RE=1, TE=0): clocks are generated by the SCI.
        if (_rxBusy || _txBusy) return;
        if (!Sync || (Scr & ScrRe) == 0 || (Scr & ScrTe) != 0) return;
        if ((Ssr & (SsrOrer | SsrPer | SsrFer)) != 0) return;
        _rxBusy = true;
        _rxDone = now + ByteStates();
        int r = _soc.Board.SciSyncTransfer(_id, 0xff, false, true);
        if (r >= 0) { _pendingRx = (byte)r; _pendingRxValid = true; } else _pendingRxValid = false;
        UpdateEvent();
    }

    /// <summary>Async RX: a peer delivers a byte (e.g. JVS).</summary>
    public void Receive(byte b, long now)
    {
        if ((Scr & ScrRe) == 0) return;
        RxDone(b);
    }

    private void RxDone(byte b)
    {
        if ((Ssr & SsrRdrf) != 0) Ssr |= SsrOrer;
        else { Ssr |= SsrRdrf; Rdr = b; }
        if ((Scr & ScrRie) != 0)
            _soc.Intc.Internal((Ssr & (SsrOrer | SsrPer | SsrFer)) != 0 ? _vecEri : _vecRxi);
    }

    private void UpdateEvent()
    {
        long t = 0;
        if (_txBusy) t = _txDone;
        if (_rxBusy && (t == 0 || _rxDone < t)) t = _rxDone;
        EventTime = t;
        _soc.Reschedule();
    }

    public void Service(long now)
    {
        if (EventTime == 0 || now < EventTime) return;
        if (_txBusy && now >= _txDone)
        {
            _txBusy = false;
            if (Sync && _pendingRxValid && (Scr & ScrRe) != 0) { RxDone(_pendingRx); _pendingRxValid = false; }
            if ((Scr & ScrTeie) != 0) _soc.Intc.Internal(_vecTei);
            if ((Scr & ScrTe) != 0 && (Ssr & SsrTdre) == 0) StartTx(_txDone);
            else Ssr |= SsrTend;
        }
        if (_rxBusy && now >= _rxDone)
        {
            _rxBusy = false;
            if (_pendingRxValid) RxDone(_pendingRx);
            _pendingRxValid = false;
            TryStartRx(_rxDone);
        }
        UpdateEvent();
    }
}

/// <summary>8-channel (4 used) A/D converter, scan/single, software start. Conversion time per MAME's H8/3337 ADC model.</summary>
public sealed class H8Adc
{
    private readonly H83002 _soc;
    public byte Adcsr, Adcr;
    private bool _adtrg = true;
    public readonly ushort[] Addr = new ushort[4];
    public long EventTime;
    private int _channel;

    public H8Adc(H83002 soc) { _soc = soc; }

    public byte ReadAddrByte(int offset) => (offset & 1) != 0 ? (byte)(Addr[offset >> 1] << 6) : (byte)(Addr[offset >> 1] >> 2);

    /// <summary>ADTRG pin (wired to the screen vblank on System 12). External trigger = falling edge of the pin level when ADCR.TRGE (bit 7) is set.</summary>
    public void Adtrg(bool state, long now)
    {
        if (state == _adtrg) return;
        _adtrg = state;
        if (!_adtrg && (Adcr & 0x80) != 0 && (Adcsr & 0x20) == 0)
        {
            Adcsr |= 0x20;
            Start(now);
        }
    }

    public void WriteAdcsr(byte v, long now)
    {
        byte prev = Adcsr;
        Adcsr = (byte)((v & 0x7f) | (Adcsr & v & 0x80));
        if ((prev & 0x20) == 0 && (Adcsr & 0x20) != 0) Start(now);
        if ((Adcsr & 0x20) == 0) { EventTime = 0; _soc.Reschedule(); }
    }

    private void Start(long now)
    {
        _channel = (Adcsr & 0x10) != 0 ? (Adcsr & 4) : (Adcsr & 7);
        EventTime = now + ((Adcsr & 0x08) != 0 ? 134 : 266);
        _soc.Reschedule();
    }

    public void Service(long now)
    {
        while (EventTime != 0 && now >= EventTime)
        {
            long t = EventTime;
            Addr[_channel & 3] = (ushort)(_soc.Board.ReadAdc(_channel) & 0x3ff);
            bool scan = (Adcsr & 0x10) != 0;
            if (scan && _channel != (Adcsr & 7))
            {
                _channel = (_channel + 1) & 7;
                EventTime = t + ((Adcsr & 0x08) != 0 ? 128 : 256);
                continue;
            }
            Adcsr |= 0x80;
            if ((Adcsr & 0x40) != 0) _soc.Intc.Internal(60);
            if (scan) { _channel = Adcsr & 4; EventTime = t + ((Adcsr & 0x08) != 0 ? 128 : 256); }
            else { Adcsr &= unchecked((byte)~0x20); EventTime = 0; }
        }
        _soc.Reschedule();
    }
}

/// <summary>Watchdog timer in interval mode (WT/IT=0): overflow flag + WOVI interrupt. Watchdog-reset mode not modelled.</summary>
public sealed class H8Wdt
{
    private readonly H83002 _soc;
    private byte _tcsr, _tcnt;
    private long _base;
    public long EventTime;
    private static readonly int[] Shift = { 1, 5, 6, 7, 8, 9, 11, 12 };

    public H8Wdt(H83002 soc) { _soc = soc; }

    private void UpdateCnt(long now)
    {
        if ((_tcsr & 0x20) == 0) { _tcnt = 0; return; }
        int sh = Shift[_tcsr & 7];
        long spos = _base >> sh, epos = now >> sh;
        long next = _tcnt + (epos - spos);
        _tcnt = (byte)next;
        _base = now;
        if (next >= 0x100 && (_tcsr & 0x40) == 0 && (_tcsr & 0x80) == 0)
        {
            _tcsr |= 0x80;
            _soc.Intc.Internal(20);
        }
    }

    public ushort Read(long now)
    {
        UpdateCnt(now);
        return (ushort)(((_tcsr | 0x10) << 8) | _tcnt);
    }

    public void Write(ushort v, long now)
    {
        if ((v & 0xff00) == 0xa500)
        {
            UpdateCnt(now);
            if ((_tcsr & 0x20) == 0 && (v & 0x20) != 0) _base = now;
            _tcsr = (byte)((_tcsr & v & 0x80) | (v & 0x7f));
            Resched(now);
        }
        else if ((v & 0xff00) == 0x5a00)
        {
            if ((_tcsr & 0x20) != 0) { _tcnt = (byte)v; _base = now; }
            Resched(now);
        }
    }

    private void Resched(long now)
    {
        if ((_tcsr & 0x20) != 0)
        {
            int sh = Shift[_tcsr & 7];
            long spos = _base >> sh;
            EventTime = (spos + 0x100 - _tcnt) << sh;
        }
        else EventTime = 0;
        _soc.Reschedule();
    }

    public void Service(long now)
    {
        if (EventTime == 0 || now < EventTime) return;
        UpdateCnt(EventTime);
        Resched(now);
    }
}

/// <summary>The H8/3002 MCU: CPU + on-chip peripherals + bus decode. External devices come from <see cref="IH83002Board"/>.</summary>
public sealed class H83002 : IH8Bus
{
    public const int ClockHz = 16934400;

    public readonly H8Cpu Cpu;
    public readonly H8Intc Intc = new();
    public readonly H8Itu[] Itu = new H8Itu[5];
    public readonly H8Sci[] Sci = new H8Sci[2];
    public readonly H8Adc Adc;
    public readonly H8Wdt Wdt;
    public readonly IH83002Board Board;

    private readonly byte[] _rom;
    private readonly byte[] _onchipRam = new byte[0x200];  // 0xfffd10-0xffff0f (as mapped by MAME)
    private readonly byte[] _regStore = new byte[0x10000];  // fallback store for registers without behaviour (DMAC, TOER, ...)
    private byte _tstr;
    private readonly byte[] _ddr = new byte[12], _dr = new byte[12];
    private long _nextEvent;
    public long UnmappedReads, UnmappedWrites;
    /// <summary>Debug/test hook called with the PC before every executed instruction (null in production).</summary>
    public Action<uint>? StepHook;

    public H83002(byte[] programRom, IH83002Board board)
    {
        _rom = programRom;
        Board = board;
        Cpu = new H8Cpu(this, programRom) { Interrupts = Intc };
        for (int i = 0; i < 5; i++) Itu[i] = new H8Itu(this, 24 + 4 * i);
        Sci[0] = new H8Sci(this, 0, 52);
        Sci[1] = new H8Sci(this, 1, 56);
        Adc = new H8Adc(this);
        Wdt = new H8Wdt(this);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_regStore);
        Array.Copy(PortDefDdr, _ddr, _ddr.Length);
        Array.Clear(_dr);
        for (int p = 4; p <= 11; p++) if (p != 5 && p != 7) PortUpdate(p);
        Cpu.Reset();
        Intc.Syscr = 0x09;
        // MAME default for port 8 DDR 0xf0? pins default to input (DDR=0) except MAME's per-port defaults.
        Reschedule();
    }

    public long Now => Cpu.States;

    // ---- scheduling ----------------------------------------------------------------------------------------------
    public void Reschedule()
    {
        long t = 0;
        void Min(long e) { if (e != 0 && (t == 0 || e < t)) t = e; }
        foreach (var u in Itu) Min(u.EventTime);
        Min(Sci[0].EventTime); Min(Sci[1].EventTime);
        Min(Adc.EventTime); Min(Wdt.EventTime);
        _nextEvent = t == 0 ? long.MaxValue : t;
    }

    public long NextEvent => _nextEvent;

    /// <summary>Service peripheral events due at the current CPU time.</summary>
    public void ServiceEvents()
    {
        long now = Cpu.States;
        if (now < _nextEvent) return;
        foreach (var u in Itu) u.Service(now);
        Sci[0].Service(now); Sci[1].Service(now);
        Adc.Service(now); Wdt.Service(now);
        Reschedule();
    }

    /// <summary>Run the CPU until 'untilStates' is reached (fast-forwards while sleeping).</summary>
    public void RunUntil(long untilStates)
    {
        while (Cpu.States < untilStates)
        {
            if (Cpu.Halted) { Cpu.States = untilStates; break; }
            if (Cpu.Sleeping && Intc.PendingVector(Cpu.CCR) == 0)
            {
                long t = Math.Min(untilStates, _nextEvent);
                if (t > Cpu.States) Cpu.States = t;
                ServiceEvents();
                if (Cpu.States >= untilStates) break;
                continue;
            }
            StepHook?.Invoke(Cpu.PC);
            Cpu.Step();
            if (Cpu.States >= _nextEvent) ServiceEvents();
        }
    }

    // ---- bus -------------------------------------------------------------------------------------------------------
    private static bool IsOnChip(uint a) => a >= 0xffff20 || (a >= 0xfffd10 && a <= 0xffff0f);

    public byte Read8(uint a)
    {
        a &= 0xffffff;
        if (a < _rom.Length) return _rom[a];
        if (a >= 0xfffd10 && a <= 0xffff0f) return _onchipRam[a - 0xfffd10];
        if (a >= 0xffff20) return ReadReg(a);
        return Board.ExtRead8(a);
    }

    public ushort Read16(uint a)
    {
        a &= 0xffffff;
        if (a + 1 < _rom.Length) return (ushort)((_rom[a] << 8) | _rom[a + 1]);
        if (a >= 0xfffd10 && a <= 0xffff0e) return (ushort)((_onchipRam[a - 0xfffd10] << 8) | _onchipRam[a - 0xfffd10 + 1]);
        if (a >= 0xffff20)
        {
            // 16-bit on-chip registers
            if (a >= 0xffff68 && a <= 0xffff9f && Reg16Itu(a, out int ch, out int kind)) return ReadItu16(ch, kind);
            if (a == 0xffffa8) return Wdt.Read(Cpu.States);
            return (ushort)((ReadReg(a) << 8) | ReadReg(a + 1));
        }
        return Board.ExtRead16(a);
    }

    public void Write8(uint a, byte v)
    {
        a &= 0xffffff;
        if (a < _rom.Length) return;
        if (a >= 0xfffd10 && a <= 0xffff0f) { _onchipRam[a - 0xfffd10] = v; return; }
        if (a >= 0xffff20) { WriteReg(a, v); return; }
        Board.ExtWrite8(a, v);
    }

    public void Write16(uint a, ushort v)
    {
        a &= 0xffffff;
        if (a < _rom.Length) return;
        if (a >= 0xfffd10 && a <= 0xffff0e) { _onchipRam[a - 0xfffd10] = (byte)(v >> 8); _onchipRam[a - 0xfffd10 + 1] = (byte)v; return; }
        if (a >= 0xffff20)
        {
            if (a >= 0xffff68 && a <= 0xffff9f && Reg16Itu(a, out int ch, out int kind)) { WriteItu16(ch, kind, v); return; }
            if (a == 0xffffa8 || a == 0xffffaa) { if (a == 0xffffa8) Wdt.Write(v, Cpu.States); return; }
            WriteReg(a, (byte)(v >> 8)); WriteReg(a + 1, (byte)v);
            return;
        }
        Board.ExtWrite16(a, v);
    }

    // ITU 16-bit register addressing: returns channel and register kind (0 = TCNT, 1 = GRA, 2 = GRB, 3 = BRA, 4 = BRB)
    private static readonly uint[] TcntAddr = { 0xffff68, 0xffff72, 0xffff7c, 0xffff86, 0xffff96 };
    private static bool Reg16Itu(uint a, out int ch, out int kind)
    {
        ch = 0; kind = 0;
        for (int i = 0; i < 5; i++)
        {
            uint b = TcntAddr[i];
            if (a == b) { ch = i; kind = 0; return true; }
            if (a == b + 2) { ch = i; kind = 1; return true; }
            if (a == b + 4) { ch = i; kind = 2; return true; }
            if (i >= 3 && a == b + 6) { ch = i; kind = 3; return true; }
            if (i >= 3 && a == b + 8) { ch = i; kind = 4; return true; }
        }
        return false;
    }

    private ushort ReadItu16(int ch, int kind)
    {
        var u = Itu[ch];
        switch (kind)
        {
            case 0: return u.ReadTcnt(Cpu.States);
            case 1: return u.Tgr[0];
            case 2: return u.Tgr[1];
            case 3: return u.Brg[0];
            default: return u.Brg[1];
        }
    }

    private void WriteItu16(int ch, int kind, ushort v)
    {
        var u = Itu[ch];
        switch (kind)
        {
            case 0: u.WriteTcnt(v, Cpu.States); break;
            case 1: u.WriteTgr(0, v, Cpu.States); break;
            case 2: u.WriteTgr(1, v, Cpu.States); break;
            case 3: u.Brg[0] = v; break;
            default: u.Brg[1] = v; break;
        }
    }

    // ---- on-chip register file -------------------------------------------------------------------------------------
    private static int PortIndex(uint reg) => reg switch
    {
        0xffffc5 or 0xffffc7 => 4,
        0xffffc9 or 0xffffcb => 6,
        0xffffcd or 0xffffcf => 8,
        0xffffd0 or 0xffffd2 => 9,
        0xffffd1 or 0xffffd3 => 10,
        0xffffd4 or 0xffffd6 => 11,
        _ => -1,
    };

    // Per-port constants from MAME's H8_PORT(config, ..., default_ddr, mask) declarations of the h83002.
    // mask = bits that do not exist / are forced to 1 on read.
    private static readonly byte[] PortMask = { 0, 0, 0, 0, 0x00, 0, 0x80, 0x00, 0xe0, 0xc0, 0x00, 0x00 };
    private static readonly byte[] PortDefDdr = { 0, 0, 0, 0, 0x00, 0, 0x80, 0x00, 0xf0, 0x00, 0x00, 0x00 };

    private byte PortRead(int p)
    {
        // MAME h8_port_device::port_r
        byte mask = PortMask[p], ddr = _ddr[p], dr = _dr[p];
        byte res = (byte)(mask | (dr & ddr));
        if (((ddr & ~mask) & 0xff) != ((~mask) & 0xff))
            res |= (byte)(Board.ReadPort(p) & ~ddr);
        return res;
    }

    private void PortUpdate(int p)
    {
        byte mask = PortMask[p];
        byte data = (byte)((_dr[p] | ~_ddr[p]) & ~mask);
        byte ddr = (byte)(_ddr[p] & ~mask);
        Board.WritePort(p, data, ddr);
    }

    private byte ReadReg(uint a)
    {
        uint lo = a & 0xff;
        long now = Cpu.States;
        switch (a)
        {
            case 0xffff60: return _tstr;
            case 0xffffb0: return Sci[0].Smr;
            case 0xffffb1: return Sci[0].Brr;
            case 0xffffb2: return Sci[0].Scr;
            case 0xffffb3: return Sci[0].Tdr;
            case 0xffffb4: return Sci[0].ReadSsr();
            case 0xffffb5: return Sci[0].ReadRdr();
            case 0xffffb8: return Sci[1].Smr;
            case 0xffffb9: return Sci[1].Brr;
            case 0xffffba: return Sci[1].Scr;
            case 0xffffbb: return Sci[1].Tdr;
            case 0xffffbc: return Sci[1].ReadSsr();
            case 0xffffbd: return Sci[1].ReadRdr();
            case 0xffffc5: case 0xffffc9: case 0xffffcd: case 0xffffd0: case 0xffffd1: case 0xffffd4:
                return 0xff; // DDR registers are write-only (MAME ff_r)
            case 0xffffc7: case 0xffffcb: case 0xffffcf: case 0xffffd2: case 0xffffd3: case 0xffffd6:
                return PortRead(PortIndex(a));
            case 0xffffce: return (byte)(Board.ReadPort(7));
            case 0xffffe0: case 0xffffe1: case 0xffffe2: case 0xffffe3: case 0xffffe4: case 0xffffe5: case 0xffffe6: case 0xffffe7:
                return Adc.ReadAddrByte((int)(a - 0xffffe0));
            case 0xffffe8: return Adc.Adcsr;
            case 0xffffe9: return Adc.Adcr;
            case 0xfffff2: return Intc.Syscr;
            case 0xfffff4: return Intc.Iscr;
            case 0xfffff5: return Intc.Ier;
            case 0xfffff6: return Intc.Isr;
            case 0xfffff8: return Intc.IcrA;
            case 0xfffff9: return Intc.IcrB;
        }
        // ITU 8-bit registers
        if (a >= 0xffff64 && a <= 0xffff9f)
        {
            for (int i = 0; i < 5; i++)
            {
                uint b = i switch { 0 => 0xffff64u, 1 => 0xffff6eu, 2 => 0xffff78u, 3 => 0xffff82u, _ => 0xffff92u };
                if (a == b) return Itu[i].Tcr;
                if (a == b + 1) return Itu[i].Tior;
                if (a == b + 2) return Itu[i].Tier;
                if (a == b + 3) return Itu[i].ReadSr(now);
                if (a >= b + 4 && a <= (i >= 3 ? b + 13 : b + 9))
                {
                    ushort w = ReadItu16(i, (int)((a - b - 4) / 2));
                    return (a & 1) == 0 ? (byte)(w >> 8) : (byte)w;
                }
            }
        }
        if (a == 0xffffa8 || a == 0xffffa9) { var w = Wdt.Read(now); return (a & 1) == 0 ? (byte)(w >> 8) : (byte)w; }
        _ = lo;
        return _regStore[a & 0xffff];
    }

    private void WriteReg(uint a, byte v)
    {
        long now = Cpu.States;
        switch (a)
        {
            case 0xffff60:
                _tstr = v;
                for (int i = 0; i < 5; i++) Itu[i].SetActive(((v >> i) & 1) != 0, now);
                return;
            case 0xffffb0: Sci[0].WriteSmr(v); return;
            case 0xffffb1: Sci[0].WriteBrr(v); return;
            case 0xffffb2: Sci[0].WriteScr(v, now); return;
            case 0xffffb3: Sci[0].WriteTdr(v); return;
            case 0xffffb4: Sci[0].WriteSsr(v, now); return;
            case 0xffffb8: Sci[1].WriteSmr(v); return;
            case 0xffffb9: Sci[1].WriteBrr(v); return;
            case 0xffffba: Sci[1].WriteScr(v, now); return;
            case 0xffffbb: Sci[1].WriteTdr(v); return;
            case 0xffffbc: Sci[1].WriteSsr(v, now); return;
            case 0xffffc5: case 0xffffc9: case 0xffffcd: case 0xffffd0: case 0xffffd1: case 0xffffd4:
            { int p = PortIndex(a); _ddr[p] = v; PortUpdate(p); return; }
            case 0xffffc7: case 0xffffcb: case 0xffffcf: case 0xffffd2: case 0xffffd3: case 0xffffd6:
            { int p = PortIndex(a); _dr[p] = v; PortUpdate(p); return; }
            case 0xffffe8: Adc.WriteAdcsr(v, now); return;
            case 0xffffe9: Adc.Adcr = v; return;
            case 0xfffff2: Intc.Syscr = v; return;
            case 0xfffff4: Intc.WriteIscr(v); return;
            case 0xfffff5: Intc.WriteIer(v); return;
            case 0xfffff6: Intc.WriteIsr(v); return;
            case 0xfffff8: Intc.IcrA = v; return;
            case 0xfffff9: Intc.IcrB = v; return;
        }
        if (a >= 0xffff64 && a <= 0xffff9f)
        {
            for (int i = 0; i < 5; i++)
            {
                uint b = i switch { 0 => 0xffff64u, 1 => 0xffff6eu, 2 => 0xffff78u, 3 => 0xffff82u, _ => 0xffff92u };
                if (a == b) { Itu[i].WriteTcr(v, now); return; }
                if (a == b + 1) { Itu[i].Tior = v; return; }
                if (a == b + 2) { Itu[i].WriteTier(v, now); return; }
                if (a == b + 3) { Itu[i].WriteSr(v, now); return; }
                if (a >= b + 4 && a <= (i >= 3 ? b + 13 : b + 9))
                {
                    // byte write into a 16-bit register: merge
                    int kind = (int)((a - b - 4) / 2);
                    ushort w = ReadItu16(i, kind);
                    w = (a & 1) == 0 ? (ushort)((w & 0x00ff) | (v << 8)) : (ushort)((w & 0xff00) | v);
                    WriteItu16(i, kind, w);
                    return;
                }
            }
        }
        _regStore[a & 0xffff] = v;
    }
}
