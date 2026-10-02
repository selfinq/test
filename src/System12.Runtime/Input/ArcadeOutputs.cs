// System12.Runtime.Input - observable outputs: coin / credit counters and lamps, plus a decoder/monitor for the input block that
// the H8 firmware maintains in shared RAM.   Original code for this project.
//
// FINDINGS (MAME 0.264 + tektagt, Lua probes in reports/trace_a08/):
//  * MAME exposes NO output names at all for tektagt (output:id_to_name(0..200) is empty at frame 300 and after coin/start;
//    namcos12.cpp has start-lamp / LED outputs only in other game classes). There is therefore NO lamp, coin-counter-coil or
//    coin-lockout signal to observe in the reference. Those outputs are declared below with Support = NotPresentInReference and
//    can never be raised by this module - they are NOT IMPLEMENTED, not "off".
//  * What IS observable is the credit bookkeeping in the shared RAM block (H8 view 0x080000+; main bus 0x1f080000+):
//        0x83900 / 0x83902   coin slot 1 / 2 : coins accepted and not yet consumed (16-bit counter written by the H8 firmware each frame)
//        0x832c0 / 0x832c2   mirror of the above, written by a second firmware routine
//        0x83940 / 0x83942   main -> H8 : the game writes the number of coins it consumed (seen: write 1 when 1P START spent a credit;
//                            the H8 then lowers 0x83900 by that amount)
//    Measured sequence (reports/trace_a08/probe_coin4_log.txt): coin1 pulse -> 0x83900=1 ; coin2 pulse -> 0x83902=1 ; second coin1 pulse
//    (held for ONE frame) -> 0x83900=2 ; 1P START -> main writes 0x83940=1 -> 0x83900=1.  The game's own CREDITS display read 3 before
//    START (2 on slot 1 + 1 on slot 2). The counter wrap/overflow behaviour and the coin-mode (coins per credit, free play) logic
//    are inside the game and the firmware and are NOT MODELLED here.
//  * The test program's SWITCH TEST screen "COIN 1: n / COIN 2: n" shows these counters.

using System;
using System.Collections.Generic;

namespace System12.Runtime.Input;

public enum OutputSupport
{
    /// <summary>Derived from state that MAME 0.264 actually produced (shared RAM words).</summary>
    ObservedInReference,
    /// <summary>The reference machine has no such signal for tektagt. NOT IMPLEMENTED; never raised.</summary>
    NotPresentInReference,
}

public sealed record OutputDescriptor(string Name, OutputSupport Support, string Evidence);

/// <param name="Frame">Emulated frame number supplied by the caller of Poll.</param>
public readonly record struct ArcadeOutputEvent(string Name, int Index, long OldValue, long NewValue, long Frame);

/// <summary>Event hub for outputs. Sinks subscribe to <see cref="Changed"/>; nothing blocks emulation.</summary>
public sealed class ArcadeOutputs
{
    public static readonly IReadOnlyList<OutputDescriptor> Catalog = new OutputDescriptor[]
    {
        new("coin_accepted", OutputSupport.ObservedInReference, "shared RAM 0x83900/0x83902 counter increased (H8 firmware)"),
        new("coins_consumed", OutputSupport.ObservedInReference, "shared RAM 0x83900/0x83902 counter decreased after main wrote 0x83940/0x83942"),
        new("test_switch", OutputSupport.ObservedInReference, "shared RAM 0x83380 word (0x8000 while the TEST switch is held)"),
        new("coin_counter_coil", OutputSupport.NotPresentInReference, "no MAME output / H8 port wiring in namcos12.cpp for tektagt"),
        new("coin_lockout", OutputSupport.NotPresentInReference, "no MAME output / H8 port wiring in namcos12.cpp for tektagt"),
        new("lamp_start1", OutputSupport.NotPresentInReference, "tektagt registers no lamp output (m_start_lamp exists only in another game class)"),
        new("lamp_start2", OutputSupport.NotPresentInReference, "same"),
    };

    public event Action<ArcadeOutputEvent>? Changed;

    public long CoinsAccepted(int slot) => _accepted[Check(slot)];
    public long CoinsConsumed(int slot) => _consumed[Check(slot)];
    private readonly long[] _accepted = new long[2];
    private readonly long[] _consumed = new long[2];
    private static int Check(int slot) => slot is 0 or 1 ? slot : throw new ArgumentOutOfRangeException(nameof(slot));

    internal void Raise(string name, int index, long oldValue, long newValue, long frame)
    {
        if (name == "coin_accepted" && newValue > oldValue) _accepted[index] += newValue - oldValue;
        if (name == "coins_consumed" && oldValue > newValue) _consumed[index] += oldValue - newValue;
        Changed?.Invoke(new ArcadeOutputEvent(name, index, oldValue, newValue, frame));
    }
}

/// <summary>Layout of the firmware-maintained input block as seen from the H8 (see <see cref="MainBusAddress"/> for the main-bus address).</summary>
public static class SharedRamLayout
{
    public const int P1Current = 0x83140, P2Current = 0x83142;      // active high, current switch state, rewritten every frame
    public const int P1Latch = 0x83180, P2Latch = 0x83182;          // OR-accumulated since boot in the traces; clearing rule UNKNOWN (never seen read by the game)
    public const int TestSwitch = 0x83380;                          // 0x8000 while TEST is held (word at 0x83384 also read by the game: meaning UNKNOWN)
    public const int Coin1Pending = 0x83900, Coin2Pending = 0x83902;
    public const int Coin1PendingMirror = 0x832c0, Coin2PendingMirror = 0x832c2;
    public const int Coin1Consume = 0x83940, Coin2Consume = 0x83942;  // written by the main CPU
    public const int Status = 0x83004;                              // handshake word read by the game during boot - meaning UNKNOWN here (sound agent)
    public const int FrameCounter = 0x8300e;                        // increments every frame (heartbeat) - observed, not needed

    /// <summary>H8 address inside 0x080000-0x083fff -> physical main-CPU address (0x1f080000 + (a - 0x80000)).</summary>
    public static uint MainBusAddress(int h8Address) => 0x1f080000u + (uint)(h8Address - 0x80000);
}

/// <summary>
/// Encoding of the player words the firmware publishes at 0x83140/0x83142 (measured, active HIGH):
/// bit15 START, bit14 SERVICE (P1 word only), bit13 UP, bit12 DOWN, bit11 LEFT, bit10 RIGHT, bit9 BTN1, bit8 BTN2, bit7 BTN3,
/// bit6 BTN4, bit5 BTN5, bit4 BTN6. Bits 3..0 never changed in the probes (BTN7/BTN8 presumably - UNVERIFIED).
/// </summary>
public static class SharedRamPlayerWord
{
    public static ushort Encode(ushort playerWord /* ArcadeInputState layout */)
    {
        int w = 0;
        if ((playerWord & ArcadeInputState.Start) != 0) w |= 0x8000;
        if ((playerWord & ArcadeInputState.Service) != 0) w |= 0x4000;
        if ((playerWord & ArcadeInputState.Up) != 0) w |= 0x2000;
        if ((playerWord & ArcadeInputState.Down) != 0) w |= 0x1000;
        if ((playerWord & ArcadeInputState.Left) != 0) w |= 0x0800;
        if ((playerWord & ArcadeInputState.Right) != 0) w |= 0x0400;
        if ((playerWord & ArcadeInputState.B1) != 0) w |= 0x0200;
        if ((playerWord & ArcadeInputState.B2) != 0) w |= 0x0100;
        if ((playerWord & ArcadeInputState.B3) != 0) w |= 0x0080;
        if ((playerWord & ArcadeInputState.B4) != 0) w |= 0x0040;
        if ((playerWord & ArcadeInputState.B5) != 0) w |= 0x0020;
        if ((playerWord & ArcadeInputState.B6) != 0) w |= 0x0010;
        return (ushort)w;
    }

    public static ushort Decode(ushort shared)
    {
        int w = 0;
        if ((shared & 0x8000) != 0) w |= ArcadeInputState.Start;
        if ((shared & 0x4000) != 0) w |= ArcadeInputState.Service;
        if ((shared & 0x2000) != 0) w |= ArcadeInputState.Up;
        if ((shared & 0x1000) != 0) w |= ArcadeInputState.Down;
        if ((shared & 0x0800) != 0) w |= ArcadeInputState.Left;
        if ((shared & 0x0400) != 0) w |= ArcadeInputState.Right;
        if ((shared & 0x0200) != 0) w |= ArcadeInputState.B1;
        if ((shared & 0x0100) != 0) w |= ArcadeInputState.B2;
        if ((shared & 0x0080) != 0) w |= ArcadeInputState.B3;
        if ((shared & 0x0040) != 0) w |= ArcadeInputState.B4;
        if ((shared & 0x0020) != 0) w |= ArcadeInputState.B5;
        if ((shared & 0x0010) != 0) w |= ArcadeInputState.B6;
        return (ushort)w;
    }
}

/// <summary>
/// Watches the credit words in the H8 shared RAM and raises <see cref="ArcadeOutputs"/> events. It does not write anything:
/// the values are produced by the real H8 firmware running on the sound agent's H8/3002 core fed from <see cref="JammaPort"/>.
/// Call <see cref="Poll"/> once per frame with a reader for 16-bit big-endian words at H8 addresses.
/// </summary>
public sealed class SharedRamInputMonitor
{
    private readonly Func<int, ushort> _readH8Word;
    private readonly ArcadeOutputs _out;
    private readonly ushort[] _coin = new ushort[2];
    private ushort _test;
    private bool _primed;

    public SharedRamInputMonitor(Func<int, ushort> readH8Word, ArcadeOutputs outputs)
    {
        _readH8Word = readH8Word ?? throw new ArgumentNullException(nameof(readH8Word));
        _out = outputs ?? throw new ArgumentNullException(nameof(outputs));
    }

    public ArcadeInputState LastPublishedState { get; private set; }

    public void Poll(long frame)
    {
        // 16-bit unsigned difference: a firmware counter wrap would show as a huge positive delta; clamp to int16 sign instead.
        for (var slot = 0; slot < 2; slot++)
        {
            var now = _readH8Word(slot == 0 ? SharedRamLayout.Coin1Pending : SharedRamLayout.Coin2Pending);
            if (_primed && now != _coin[slot])
            {
                short delta = unchecked((short)(now - _coin[slot]));
                _out.Raise(delta > 0 ? "coin_accepted" : "coins_consumed", slot, _coin[slot], now, frame);
            }
            _coin[slot] = now;
        }
        var test = _readH8Word(SharedRamLayout.TestSwitch);
        if (_primed && test != _test) _out.Raise("test_switch", 0, _test, test, frame);
        _test = test;
        _primed = true;

        LastPublishedState = new ArcadeInputState(
            SharedRamPlayerWord.Decode(_readH8Word(SharedRamLayout.P1Current)),
            SharedRamPlayerWord.Decode(_readH8Word(SharedRamLayout.P2Current)),
            coin1: false, coin2: false, test: test != 0);
    }
}
