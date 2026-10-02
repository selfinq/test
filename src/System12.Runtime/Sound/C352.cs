// SPDX-License-Identifier: BSD-3-Clause
//
// C352 (Namco custom PCM chip) voice mixer.
//
// This file is a C# PORT (not an original design) of MAME's src/devices/sound/c352.cpp / c352.h:
//   license:BSD-3-Clause
//   copyright-holders: R. Belmont, superctr
//   "Thanks to Cap of VivaNonno for info and The_Author for preliminary reverse-engineering;
//    additional code by cync and the hoot development team"
// Reference revision: mamedev/mame f8f6b22c3f328b9e0b04ff502989f8536c0d66c0 (the algorithm is
// byte-for-byte equivalent in tag mame0264, which only differs in stream plumbing and debug logging).
//
// Copyright (c) 1997-2025 the MAME team (see above). Redistribution and use in source and binary forms,
// with or without modification, are permitted provided that the following conditions are met:
//  1. Redistributions of source code must retain the above copyright notice, this list of conditions
//     and the following disclaimer.
//  2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions
//     and the following disclaimer in the documentation and/or other materials provided with the distribution.
//  3. Neither the name of the copyright holders nor the names of its contributors may be used to endorse
//     or promote products derived from this software without specific prior written permission.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
// WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
// PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
// ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES.
//
// Scope / honesty notes:
//  * Everything in this file mirrors the MAME model. The MAME model is itself a reverse-engineered
//    approximation of the real chip; "matches MAME" is the only fidelity claim that can be made here.
//  * Sample-ROM addressing beyond 24 bits (wave_bank > 0xff) is masked to 24 bits; MAME's
//    device_rom_interface<24> does the same masking in its cache. This is not verified on hardware.
//  * Only the register/mixing model is here. Host audio output / resampling is NOT implemented.

using System;

namespace System12.Runtime.Sound;

/// <summary>Namco C352 32-voice PCM mixer (port of MAME's c352_device).</summary>
public sealed class C352
{
    public const int VoiceCount = 32;

    // Flag bits (register 3 of each voice).
    public const ushort FlgBusy = 0x8000;
    public const ushort FlgKeyOn = 0x4000;
    public const ushort FlgKeyOff = 0x2000;
    public const ushort FlgLoopTrg = 0x1000;
    public const ushort FlgLoopHist = 0x0800;
    public const ushort FlgFm = 0x0400;
    public const ushort FlgPhaseRL = 0x0200;
    public const ushort FlgPhaseFL = 0x0100;
    public const ushort FlgPhaseFR = 0x0080;
    public const ushort FlgLDir = 0x0040;
    public const ushort FlgLink = 0x0020;
    public const ushort FlgNoise = 0x0010;
    public const ushort FlgMuLaw = 0x0008;
    public const ushort FlgFilter = 0x0004;
    public const ushort FlgRevLoop = 0x0003;
    public const ushort FlgLoop = 0x0002;
    public const ushort FlgReverse = 0x0001;

    /// <summary>System 12 board: C352 clock 25.4016 MHz, divider 288 (namcos12.cpp: C352(config,"c352",25401600,288)).</summary>
    public const int System12Clock = 25401600;
    public const int System12Divider = 288;
    public const int System12SampleRate = System12Clock / System12Divider; // 88200 Hz

    internal struct Voice
    {
        public uint Pos;
        public uint Counter;
        public short Sample;
        public short LastSample;
        public ushort VolF;
        public ushort VolR;
        public byte Vol0, Vol1, Vol2, Vol3; // curr_vol[0..3]
        public ushort Freq;
        public ushort Flags;
        public ushort WaveBank;
        public ushort WaveStart;
        public ushort WaveEnd;
        public ushort WaveLoop;
    }

    private readonly Voice[] _v = new Voice[VoiceCount];
    private readonly short[] _mulaw = new short[256];
    private readonly byte[] _rom;
    private ushort _random;
    private ushort _control;

    /// <param name="sampleRom">Contents of the "c352" region (16 MiB for System 12). Not copied.</param>
    public C352(byte[] sampleRom)
    {
        _rom = sampleRom ?? throw new ArgumentNullException(nameof(sampleRom));
        BuildMuLawTable();
        Reset();
    }

    /// <summary>Last value written to the control register (offset 0x200); purpose unknown (as in MAME).</summary>
    public ushort Control => _control;

    private void BuildMuLawTable()
    {
        // "generate mulaw table (Output similar to namco's VC emulator)" - c352_device::device_start
        int j = 0;
        for (int i = 0; i < 128; i++)
        {
            _mulaw[i] = unchecked((short)(j << 5));
            if (i < 16) j += 1;
            else if (i < 24) j += 2;
            else if (i < 48) j += 4;
            else if (i < 100) j += 8;
            else j += 16;
        }
        for (int i = 0; i < 128; i++)
            _mulaw[i + 128] = unchecked((short)((~_mulaw[i]) & 0xffe0));
    }

    public short MuLaw(byte code) => _mulaw[code];

    public void Reset()
    {
        Array.Clear(_v);
        _random = 0x1234;
        _control = 0;
    }

    private byte RomByte(uint pos) => _rom[pos & 0xffffff];

    // ---- register interface -------------------------------------------------------------------------
    // 'offset' is the 16-bit WORD offset within the chip window (the H8 address minus 0x280000, divided by 2),
    // exactly as MAME's c352_device::read/write receive it.

    public ushort Read(int offset)
    {
        if (offset < 0x100) return GetReg(offset);
        if (offset == 0x200) return _control;
        return 0;
    }

    public void Write(int offset, ushort data, ushort mask = 0xffff)
    {
        if (offset < 0x100)
        {
            ushort nv = GetReg(offset);
            nv = (ushort)((nv & ~mask) | (data & mask));
            SetReg(offset, nv);
        }
        else if (offset == 0x200)
        {
            _control = (ushort)((_control & ~mask) | (data & mask));
        }
        else if (offset == 0x202) // execute keyons/keyoffs
        {
            if (mask != 0xffff) return; // "16 bit only?" in MAME
            ExecuteKeyOnOff();
        }
    }

    /// <summary>8-bit access as the H8 bus would perform it (16-bit bus, big-endian: even byte = high half).</summary>
    public void Write8(int byteOffset, byte data)
    {
        int word = byteOffset >> 1;
        if ((byteOffset & 1) == 0) Write(word, (ushort)(data << 8), 0xff00);
        else Write(word, data, 0x00ff);
    }

    public byte Read8(int byteOffset)
    {
        ushort w = Read(byteOffset >> 1);
        return (byte)((byteOffset & 1) == 0 ? w >> 8 : w);
    }

    private ushort GetReg(int offset)
    {
        ref Voice v = ref _v[offset >> 3];
        switch (offset & 7)
        {
            case 0: return v.VolF;
            case 1: return v.VolR;
            case 2: return v.Freq;
            case 3: return v.Flags;
            case 4: return v.WaveBank;
            case 5: return v.WaveStart;
            case 6: return v.WaveEnd;
            default: return v.WaveLoop;
        }
    }

    private void SetReg(int offset, ushort val)
    {
        ref Voice v = ref _v[offset >> 3];
        switch (offset & 7)
        {
            case 0: v.VolF = val; break;
            case 1: v.VolR = val; break;
            case 2: v.Freq = val; break;
            case 3: v.Flags = val; break;
            case 4: v.WaveBank = val; break;
            case 5: v.WaveStart = val; break;
            case 6: v.WaveEnd = val; break;
            default: v.WaveLoop = val; break;
        }
    }

    private void ExecuteKeyOnOff()
    {
        for (int i = 0; i < VoiceCount; i++)
        {
            ref Voice v = ref _v[i];
            if ((v.Flags & FlgKeyOn) != 0)
            {
                v.Pos = ((uint)v.WaveBank << 16) | v.WaveStart;
                v.Sample = 0;
                v.LastSample = 0;
                v.Counter = 0xffff;
                v.Flags |= FlgBusy;
                v.Flags = (ushort)(v.Flags & ~(FlgKeyOn | FlgLoopHist));
                v.Vol0 = v.Vol1 = 0;
                v.Vol2 = v.Vol3 = 0;
            }
            if ((v.Flags & FlgKeyOff) != 0)
            {
                v.Flags = (ushort)(v.Flags & ~(FlgBusy | FlgKeyOff));
                v.Counter = 0xffff;
            }
        }
    }

    // ---- sample generation --------------------------------------------------------------------------

    private void FetchSample(ref Voice v)
    {
        v.LastSample = v.Sample;

        if ((v.Flags & FlgNoise) != 0)
        {
            // m_random = (m_random >> 1) ^ ((-(m_random & 1)) & 0xfff6);
            _random = (ushort)((_random >> 1) ^ ((-(_random & 1)) & 0xfff6));
            v.Sample = unchecked((short)_random);
            return;
        }

        sbyte s = unchecked((sbyte)RomByte(v.Pos));
        if ((v.Flags & FlgMuLaw) != 0)
            v.Sample = _mulaw[(byte)s];
        else
            v.Sample = unchecked((short)(s << 8));

        ushort pos = (ushort)(v.Pos & 0xffff);

        if ((v.Flags & FlgLoop) != 0 && (v.Flags & FlgReverse) != 0)
        {
            // backwards>forwards
            if ((v.Flags & FlgLDir) != 0 && pos == v.WaveLoop)
                v.Flags = (ushort)(v.Flags & ~FlgLDir);
            // forwards>backwards
            else if ((v.Flags & FlgLDir) == 0 && pos == v.WaveEnd)
                v.Flags |= FlgLDir;

            v.Pos = unchecked(v.Pos + (uint)(((v.Flags & FlgLDir) != 0) ? -1 : 1));
        }
        else if (pos == v.WaveEnd)
        {
            if ((v.Flags & FlgLink) != 0 && (v.Flags & FlgLoop) != 0)
            {
                v.Pos = ((uint)v.WaveStart << 16) | v.WaveLoop;
                v.Flags |= FlgLoopHist;
            }
            else if ((v.Flags & FlgLoop) != 0)
            {
                v.Pos = (v.Pos & 0xff0000) | v.WaveLoop;
                v.Flags |= FlgLoopHist;
            }
            else
            {
                v.Flags |= FlgKeyOff;
                v.Flags = (ushort)(v.Flags & ~FlgBusy);
                v.Sample = 0;
            }
        }
        else
        {
            v.Pos = unchecked(v.Pos + (uint)(((v.Flags & FlgReverse) != 0) ? -1 : 1));
        }
    }

    private static byte Ramp(byte cur, byte target)
    {
        int delta = cur - target;
        if (delta != 0) cur = (byte)(cur + (delta > 0 ? -1 : 1));
        return cur;
    }

    /// <summary>Produce one output frame (88.2 kHz on System 12). Outputs: front-left, front-right, rear-left, rear-right.
    /// System 12 routes only the first two (namcos12.cpp: add_route(0..1)); rear outputs are not wired.</summary>
    public void GenerateSample(out short fl, out short fr, out short rl, out short rr)
    {
        int o0 = 0, o1 = 0, o2 = 0, o3 = 0;

        for (int j = 0; j < VoiceCount; j++)
        {
            ref Voice v = ref _v[j];
            short s = 0;

            if ((v.Flags & FlgBusy) != 0)
            {
                int nextCounter = (int)v.Counter + v.Freq;

                if ((nextCounter & 0x10000) != 0)
                    FetchSample(ref v);

                if ((((uint)nextCounter ^ v.Counter) & 0x18000) != 0)
                {
                    v.Vol0 = Ramp(v.Vol0, (byte)(v.VolF >> 8));
                    v.Vol1 = Ramp(v.Vol1, (byte)(v.VolF & 0xff));
                    v.Vol2 = Ramp(v.Vol2, (byte)(v.VolR >> 8));
                    v.Vol3 = Ramp(v.Vol3, (byte)(v.VolR & 0xff));
                }

                v.Counter = (uint)nextCounter & 0xffff;

                s = v.Sample;

                // Interpolate samples (arithmetic shift == floor, equals MAME's u32/s16 conversions after truncation to s16)
                if ((v.Flags & FlgFilter) == 0)
                    s = unchecked((short)(v.LastSample + (int)(((long)v.Counter * (v.Sample - v.LastSample)) >> 16)));
            }

            // Left
            o0 += (((v.Flags & FlgPhaseFL) != 0 ? -s : s) * v.Vol0) >> 8;
            o2 += (((v.Flags & FlgPhaseRL) != 0 ? -s : s) * v.Vol2) >> 8;

            // Right
            o1 += (((v.Flags & FlgPhaseFR) != 0 ? -s : s) * v.Vol1) >> 8;
            o3 += (((v.Flags & FlgPhaseFR) != 0 ? -s : s) * v.Vol3) >> 8;
        }

        fl = unchecked((short)(o0 >> 3));
        fr = unchecked((short)(o1 >> 3));
        rl = unchecked((short)(o2 >> 3));
        rr = unchecked((short)(o3 >> 3));
    }

    /// <summary>Render <paramref name="frames"/> frames into interleaved stereo (front L, front R) 16-bit PCM.</summary>
    public void RenderStereo(Span<short> interleaved, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            GenerateSample(out short l, out short r, out _, out _);
            interleaved[2 * i] = l;
            interleaved[2 * i + 1] = r;
        }
    }

    // ---- debug / test accessors ----------------------------------------------------------------------
    public bool IsBusy(int voice) => (_v[voice].Flags & FlgBusy) != 0;
    public ushort FlagsOf(int voice) => _v[voice].Flags;
    public uint PositionOf(int voice) => _v[voice].Pos;

    /// <summary>Noise generator state (16-bit LFSR, seed 0x1234 after reset).</summary>
    public ushort NoiseState => _random;

    public readonly struct VoiceSnapshot
    {
        public readonly uint Pos, Counter;
        public readonly short Sample, LastSample;
        public readonly byte Vol0, Vol1, Vol2, Vol3;
        public readonly ushort Flags;
        internal VoiceSnapshot(in Voice v)
        {
            Pos = v.Pos; Counter = v.Counter; Sample = v.Sample; LastSample = v.LastSample;
            Vol0 = v.Vol0; Vol1 = v.Vol1; Vol2 = v.Vol2; Vol3 = v.Vol3; Flags = v.Flags;
        }
    }

    public VoiceSnapshot Snapshot(int voice) => new VoiceSnapshot(in _v[voice]);
}
