// System12.Runtime.Input - the arcade-side input model for tektagt (JAMMA path, no JVS).
// Original code for this project. Evidence: reports/refs/namcos12.cpp (in_r, INPUT_PORTS namcos12), MAME 0.264 dynamic probes
// reports/trace_a08/golden_inr.txt (value the H8 reads at 0x300000/0x300002 for every input field).
//
// HOW INPUT REACHES THE GAME (tektagt, MAME 0.264 / driver f8f6b22):
//   host buttons --> [this class: JammaPort]  --(H8/3002 bus 0x300000-0x300003, 16-bit reads, active low)-->
//   H8 sound/IO firmware (ROM teg1.11s) --(per-frame, shared RAM 0x080000-0x083fff on the H8 side = 0x1f080000 on the main bus)-->
//   main CPU game code.   The main CPU never reads a pad/SIO0/JVS register for player controls in this game.
//   (tektagt instantiates no JVS device - the "JVS CABINET OPTIONS" entry in the test menu is greyed out - so the driver's
//   m_jamma flag stays true and the H8 in_r handler serves the JAMMA bytes below.)
//
// The in_r bytes (offset 0..3 at H8 0x300000..0x300003; all ACTIVE LOW, 0 = pressed; big-endian 16-bit reads):
//   byte0 (0x300000, high byte of word0): P2  b7=START b6=BTN3 b5=BTN2 b4=BTN1 b3=UP b2=DOWN b1=LEFT b0=RIGHT
//   byte1 (0x300001, low  byte of word0): P1  same layout
//   byte2 (0x300002, high byte of word1): b7=P1 SERVICE b6=TEST b5=COIN1 b4=COIN2 b3=P1 BTN7 b2=P2 BTN7 b1=P1 BTN8 b0=P2 BTN8
//   byte3 (0x300003, low  byte of word1): b7=P2 BTN6 b6=P2 BTN5 b5=P2 BTN4 b4=unused b3=P1 BTN6 b2=P1 BTN5 b1=P1 BTN4 b0=unused
// BTN7/BTN8 exist in the driver source (master) but MAME 0.264 for tektagt has no ports for them and the game's SWITCH TEST screen
// shows six buttons per player: BTN7/BTN8 are modelled (source says so) but NOT VERIFIED for tektagt.
// DIP switches: H8 port 7 reads the "DSW" port: bit7 = DIP SW2:1 "Service Mode" (0 = on), bit6 = DIP SW2:2 "Freeze" (0 = on), others 1.

using System;
using System.Collections.Generic;

namespace System12.Runtime.Input;

/// <summary>Logical arcade inputs. Values are stable identifiers used by the binding tables and by tests.</summary>
public enum ArcadeInput
{
    P1Start, P1Up, P1Down, P1Left, P1Right, P1Button1, P1Button2, P1Button3, P1Button4, P1Button5, P1Button6, P1Button7, P1Button8,
    P2Start, P2Up, P2Down, P2Left, P2Right, P2Button1, P2Button2, P2Button3, P2Button4, P2Button5, P2Button6, P2Button7, P2Button8,
    Coin1, Coin2, Service1, Test,
}

/// <summary>
/// Snapshot of every arcade control. Bit positions follow the driver's JVS_PLAYER1/2 words (the canonical host-facing form
/// in namcos12.cpp): start 0x80, service(P1 only) 0x40, up 0x20, down 0x10, left 0x08, right 0x04, b1 0x02, b2 0x01,
/// b3 0x8000, b4 0x4000, b5 0x2000, b6 0x1000, b7 0x0800, b8 0x0400.  ACTIVE HIGH here (1 = pressed).
/// </summary>
public readonly struct ArcadeInputState : IEquatable<ArcadeInputState>
{
    public const ushort Start = 0x0080, Service = 0x0040, Up = 0x0020, Down = 0x0010, Left = 0x0008, Right = 0x0004,
        B1 = 0x0002, B2 = 0x0001, B3 = 0x8000, B4 = 0x4000, B5 = 0x2000, B6 = 0x1000, B7 = 0x0800, B8 = 0x0400;

    public readonly ushort Player1;
    public readonly ushort Player2;
    public readonly bool Coin1, Coin2, Test;

    public ArcadeInputState(ushort p1, ushort p2, bool coin1, bool coin2, bool test)
    { Player1 = p1; Player2 = p2; Coin1 = coin1; Coin2 = coin2; Test = test; }

    public static ArcadeInputState Idle => default;

    private static ushort Mask(ArcadeInput i, out int player) // player: 0 none, 1, 2
    {
        player = i is >= ArcadeInput.P1Start and <= ArcadeInput.P1Button8 ? 1 : i is >= ArcadeInput.P2Start and <= ArcadeInput.P2Button8 ? 2 : 0;
        var k = player == 1 ? i - ArcadeInput.P1Start : player == 2 ? i - ArcadeInput.P2Start : 0;
        return k switch
        {
            0 => Start, 1 => Up, 2 => Down, 3 => Left, 4 => Right,
            5 => B1, 6 => B2, 7 => B3, 8 => B4, 9 => B5, 10 => B6, 11 => B7, 12 => B8,
            _ => 0,
        };
    }

    public bool IsPressed(ArcadeInput input)
    {
        switch (input)
        {
            case ArcadeInput.Coin1: return Coin1;
            case ArcadeInput.Coin2: return Coin2;
            case ArcadeInput.Test: return Test;
            case ArcadeInput.Service1: return (Player1 & Service) != 0;
        }
        var m = Mask(input, out var p);
        return ((p == 1 ? Player1 : Player2) & m) != 0;
    }

    public ArcadeInputState With(ArcadeInput input, bool pressed)
    {
        ushort p1 = Player1, p2 = Player2; bool c1 = Coin1, c2 = Coin2, t = Test;
        switch (input)
        {
            case ArcadeInput.Coin1: c1 = pressed; break;
            case ArcadeInput.Coin2: c2 = pressed; break;
            case ArcadeInput.Test: t = pressed; break;
            case ArcadeInput.Service1: p1 = pressed ? (ushort)(p1 | Service) : (ushort)(p1 & ~Service); break;
            default:
                var m = Mask(input, out var p);
                if (p == 1) p1 = pressed ? (ushort)(p1 | m) : (ushort)(p1 & ~m);
                else p2 = pressed ? (ushort)(p2 | m) : (ushort)(p2 & ~m);
                break;
        }
        return new ArcadeInputState(p1, p2, c1, c2, t);
    }

    public ArcadeInputState Or(ArcadeInputState o) =>
        new((ushort)(Player1 | o.Player1), (ushort)(Player2 | o.Player2), Coin1 | o.Coin1, Coin2 | o.Coin2, Test | o.Test);

    public bool Equals(ArcadeInputState o) => Player1 == o.Player1 && Player2 == o.Player2 && Coin1 == o.Coin1 && Coin2 == o.Coin2 && Test == o.Test;
    public override bool Equals(object? obj) => obj is ArcadeInputState s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Player1, Player2, Coin1, Coin2, Test);
    public static bool operator ==(ArcadeInputState a, ArcadeInputState b) => a.Equals(b);
    public static bool operator !=(ArcadeInputState a, ArcadeInputState b) => !a.Equals(b);
    public override string ToString() => $"P1={Player1:x4} P2={Player2:x4} coin1={Coin1} coin2={Coin2} test={Test}";
}

/// <summary>
/// The JAMMA input latch seen by the H8/3002 at 0x300000-0x300003 (namcos12_state::in_r) plus the DSW port. Pure data: no
/// timing. Thread-safe for one writer (host UI thread) / one reader (emulation thread): state is swapped as a whole struct
/// through a volatile reference.
/// </summary>
public sealed class JammaPort
{
    public const uint H8Base = 0x300000;
    public const uint H8Length = 4;
    /// <summary>MAME "DSW" port default: SW2:1 Off, SW2:2 Off, bits 5..0 unused = 1.</summary>
    public const byte DswDefault = 0xff;

    private volatile StateBox _box = new(ArcadeInputState.Idle);
    private sealed class StateBox { public readonly ArcadeInputState S; public StateBox(ArcadeInputState s) { S = s; } }

    public ArcadeInputState State { get => _box.S; set => _box = new StateBox(value); }

    /// <summary>H8 port 7 value. Bit7 low = DIP SW2:1 (service mode) ON; bit6 low = DIP SW2:2 (freeze) ON.</summary>
    public byte Dsw { get; set; } = DswDefault;

    /// <summary>namcos12_state::in_r(offset) for offset 0..3 (master JAMMA branch), already inverted (active low).</summary>
    public static byte JammaByte(in ArcadeInputState s, int offset)
    {
        static int Bit(int v, int n) => (v >> n) & 1;
        int p1 = s.Player1, p2 = s.Player2;
        switch (offset)
        {
            case 0: return (byte)~BitSwap8(p2, 7, 15, 0, 1, 5, 4, 3, 2);
            case 1: return (byte)~BitSwap8(p1, 7, 15, 0, 1, 5, 4, 3, 2);
            case 2:
                return (byte)~((Bit(p1, 6) << 7) | ((s.Test ? 1 : 0) << 6) | ((s.Coin1 ? 1 : 0) << 5) | ((s.Coin2 ? 1 : 0) << 4)
                               | (Bit(p1, 11) << 3) | (Bit(p2, 11) << 2) | (Bit(p1, 10) << 1) | Bit(p2, 10));
            case 3:
                return (byte)~((BitSwap3(p2, 12, 13, 14) << 5) | (BitSwap3(p1, 12, 13, 14) << 1));
            default: throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    /// <summary>MAME bitswap&lt;8&gt;(v, b7..b0): result bit7 = v.bit(a7) ... result bit0 = v.bit(a0).</summary>
    private static int BitSwap8(int v, int a7, int a6, int a5, int a4, int a3, int a2, int a1, int a0) =>
        (((v >> a7) & 1) << 7) | (((v >> a6) & 1) << 6) | (((v >> a5) & 1) << 5) | (((v >> a4) & 1) << 4)
        | (((v >> a3) & 1) << 3) | (((v >> a2) & 1) << 2) | (((v >> a1) & 1) << 1) | ((v >> a0) & 1);

    private static int BitSwap3(int v, int a2, int a1, int a0) => (((v >> a2) & 1) << 2) | (((v >> a1) & 1) << 1) | ((v >> a0) & 1);

    /// <summary>The 16-bit big-endian word the H8 reads at 0x300000 (index 0) or 0x300002 (index 1).</summary>
    public ushort ReadWord(int index)
    {
        var s = State;
        return (ushort)((JammaByte(s, index * 2) << 8) | JammaByte(s, index * 2 + 1));
    }

    /// <summary>H8 bus helper: true if <paramref name="h8Address"/> is inside 0x300000-0x300003.</summary>
    public bool TryRead8(uint h8Address, out byte value)
    {
        if (h8Address >= H8Base && h8Address < H8Base + H8Length) { value = JammaByte(State, (int)(h8Address - H8Base)); return true; }
        value = 0; return false;
    }

    /// <summary>H8 bus helper for 16-bit reads (address must be 0x300000 or 0x300002).</summary>
    public bool TryRead16(uint h8Address, out ushort value)
    {
        if (h8Address == H8Base) { value = ReadWord(0); return true; }
        if (h8Address == H8Base + 2) { value = ReadWord(1); return true; }
        value = 0; return false;
    }
}
