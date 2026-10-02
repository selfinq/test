// System12.Runtime.Input - host keyboard/gamepad -> arcade input mapping. Plain data tables, no UI or windowing library.
// Original code for this project. The default key layout is modelled on the conventions of MAME's default keyboard
// assignments (arrows + Ctrl/Alt/Space for player 1, RDFG + AS-QWE for player 2, 5/6 coins, 1/2 start, F2 test) but is this
// project's own table and can be replaced by the front end or a text file (see BindingTable.Parse / ToText).
//
// Host identifiers are strings so that any front end (SDL, Silk.NET, WinForms, Avalonia, a test) can feed the mapper:
//   keyboard : "Key:LeftCtrl", "Key:Z", "Key:Num5", "Key:F2" ...    (names are the front end's, matched ordinally, case-insensitive)
//   gamepad  : "Pad0:South", "Pad0:DPadUp", "Pad0:LeftStickY-" ...  (SDL GameController style names; "<axis>-"/"<axis>+" are
//                              produced by SetAxis() from analogue values with a dead zone)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace System12.Runtime.Input;

public readonly record struct Binding(ArcadeInput Target, string Source);

public static class DefaultBindings
{
    /// <summary>Keyboard defaults (two players on one keyboard + cabinet switches).</summary>
    public static IReadOnlyList<Binding> Keyboard { get; } = new Binding[]
    {
        // player 1
        new(ArcadeInput.P1Up, "Key:Up"), new(ArcadeInput.P1Down, "Key:Down"), new(ArcadeInput.P1Left, "Key:Left"), new(ArcadeInput.P1Right, "Key:Right"),
        new(ArcadeInput.P1Button1, "Key:LeftCtrl"), new(ArcadeInput.P1Button2, "Key:LeftAlt"), new(ArcadeInput.P1Button3, "Key:Space"),
        new(ArcadeInput.P1Button4, "Key:LeftShift"), new(ArcadeInput.P1Button5, "Key:Z"), new(ArcadeInput.P1Button6, "Key:X"),
        new(ArcadeInput.P1Button7, "Key:C"), new(ArcadeInput.P1Button8, "Key:V"),
        new(ArcadeInput.P1Start, "Key:Num1"),
        // player 2
        new(ArcadeInput.P2Up, "Key:R"), new(ArcadeInput.P2Down, "Key:F"), new(ArcadeInput.P2Left, "Key:D"), new(ArcadeInput.P2Right, "Key:G"),
        new(ArcadeInput.P2Button1, "Key:A"), new(ArcadeInput.P2Button2, "Key:S"), new(ArcadeInput.P2Button3, "Key:Q"),
        new(ArcadeInput.P2Button4, "Key:W"), new(ArcadeInput.P2Button5, "Key:E"), new(ArcadeInput.P2Button6, "Key:T"),
        new(ArcadeInput.P2Button7, "Key:Y"), new(ArcadeInput.P2Button8, "Key:U"),
        new(ArcadeInput.P2Start, "Key:Num2"),
        // cabinet
        new(ArcadeInput.Coin1, "Key:Num5"), new(ArcadeInput.Coin2, "Key:Num6"),
        new(ArcadeInput.Service1, "Key:Num9"), new(ArcadeInput.Test, "Key:F2"),
    };

    /// <summary>Gamepad defaults for one pad as player <paramref name="player"/> (1 or 2). Test/service stay on the keyboard.</summary>
    public static IReadOnlyList<Binding> Gamepad(int padIndex, int player)
    {
        if (player is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(player));
        string P(string code) => $"Pad{padIndex}:{code}";
        ArcadeInput T(ArcadeInput p1) => player == 1 ? p1 : p1 + (ArcadeInput.P2Start - ArcadeInput.P1Start);
        var list = new List<Binding>
        {
            new(T(ArcadeInput.P1Up), P("DPadUp")), new(T(ArcadeInput.P1Up), P("LeftStickY-")),
            new(T(ArcadeInput.P1Down), P("DPadDown")), new(T(ArcadeInput.P1Down), P("LeftStickY+")),
            new(T(ArcadeInput.P1Left), P("DPadLeft")), new(T(ArcadeInput.P1Left), P("LeftStickX-")),
            new(T(ArcadeInput.P1Right), P("DPadRight")), new(T(ArcadeInput.P1Right), P("LeftStickX+")),
            new(T(ArcadeInput.P1Button1), P("West")),    // physical layout of a 6-button arcade panel is a front-end choice
            new(T(ArcadeInput.P1Button2), P("North")),
            new(T(ArcadeInput.P1Button3), P("RightShoulder")),
            new(T(ArcadeInput.P1Button4), P("South")),
            new(T(ArcadeInput.P1Button5), P("East")),
            new(T(ArcadeInput.P1Button6), P("RightTrigger")),
            new(T(ArcadeInput.P1Start), P("Start")),
        };
        list.Add(new Binding(player == 1 ? ArcadeInput.Coin1 : ArcadeInput.Coin2, P("Back")));
        return list;
    }
}

/// <summary>Text (de)serialisation of a binding table: one line per target, <c>Target = source, source, ...</c>; '#' starts a comment.</summary>
public static class BindingTable
{
    public static string ToText(IEnumerable<Binding> bindings)
    {
        var sb = new StringBuilder();
        foreach (var g in bindings.GroupBy(b => b.Target).OrderBy(g => g.Key))
            sb.Append(g.Key).Append(" = ").AppendJoin(", ", g.Select(b => b.Source)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parses the format written by <see cref="ToText"/>. Unknown targets throw FormatException (never silently skipped).</summary>
    public static List<Binding> Parse(string text)
    {
        var result = new List<Binding>();
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) throw new FormatException($"line {lineNo}: expected 'Target = source[, source]'");
            var name = line[..eq].Trim();
            if (!Enum.TryParse<ArcadeInput>(name, ignoreCase: false, out var target) || !Enum.IsDefined(target))
                throw new FormatException($"line {lineNo}: unknown arcade input '{name}'");
            foreach (var src in line[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                result.Add(new Binding(target, src));
        }
        return result;
    }
}

public enum OpposingDirectionPolicy
{
    /// <summary>Left+Right (or Up+Down) held together report neither (what an 8-way microswitch stick cannot produce anyway).</summary>
    Neutral,
    /// <summary>Report both bits (a real cabinet cannot; the game's behaviour with both set is unknown).</summary>
    PassThrough,
}

/// <summary>
/// Turns host key/button/axis events into <see cref="ArcadeInputState"/> snapshots. Call <see cref="Sample"/> once per
/// emulated frame (the H8 firmware samples the inputs once per VBLANK).
/// <list type="bullet">
/// <item>A press and release between two samples is still reported once ("tap latch") so fast taps are not lost.</item>
/// <item>Coin/test/service/start are stretched to at least <see cref="MinHoldSamples"/> samples. Evidence: in MAME 0.264 the H8 firmware
/// registered a coin held for 1 frame (credit counter +1), so 1 is sufficient there; the default of 2 leaves margin for 30 Hz hosts.</item>
/// </list>
/// </summary>
public sealed class HostInputMapper
{
    private readonly Dictionary<string, List<ArcadeInput>> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ArcadeInput> _tapped = new();
    private readonly Dictionary<ArcadeInput, int> _hold = new();
    private ArcadeInputState _last;

    public int MinHoldSamples { get; set; } = 2;
    public OpposingDirectionPolicy Opposing { get; set; } = OpposingDirectionPolicy.Neutral;
    /// <summary>Analogue magnitude above which an axis counts as pressed.</summary>
    public double AxisThreshold { get; set; } = 0.5;
    /// <summary>Raised from <see cref="Sample"/> whenever a logical input changes state (observable input events).</summary>
    public event Action<ArcadeInput, bool>? InputChanged;

    public HostInputMapper(IEnumerable<Binding> bindings)
    {
        foreach (var b in bindings)
        {
            if (!_bySource.TryGetValue(b.Source, out var l)) _bySource[b.Source] = l = new List<ArcadeInput>();
            if (!l.Contains(b.Target)) l.Add(b.Target);
        }
    }

    public IReadOnlyCollection<string> BoundSources => _bySource.Keys;

    /// <summary>Host key / pad button changed state. Sources with no binding are ignored (returns false) so the caller can route them elsewhere.</summary>
    public bool SetDown(string source, bool down)
    {
        if (!_bySource.TryGetValue(source, out var targets)) return false;
        if (down)
        {
            if (_down.Add(source)) foreach (var t in targets) _tapped.Add(t);
        }
        else _down.Remove(source);
        return true;
    }

    /// <summary>Analogue axis. <paramref name="axis"/> e.g. "Pad0:LeftStickX", value -1..1; drives "Pad0:LeftStickX-" and "...+" sources.</summary>
    public void SetAxis(string axis, double value)
    {
        SetDown(axis + "-", value <= -AxisThreshold);
        SetDown(axis + "+", value >= AxisThreshold);
    }

    public void ReleaseAll() { _down.Clear(); _tapped.Clear(); _hold.Clear(); }

    private bool Active(ArcadeInput i)
    {
        foreach (var kv in _bySource)
            if (kv.Value.Contains(i) && _down.Contains(kv.Key)) return true;
        return false;
    }

    /// <summary>Compute the state for the next emulated frame (consumes the tap latch).</summary>
    public ArcadeInputState Sample()
    {
        var s = ArcadeInputState.Idle;
        foreach (var i in Enum.GetValues<ArcadeInput>())
        {
            var raw = Active(i) || _tapped.Contains(i);
            _hold.TryGetValue(i, out var run);                   // consecutive samples this input has been reported on
            var need = IsStretched(i) ? Math.Max(1, MinHoldSamples) : 1;
            bool on;
            if (raw) { run++; on = true; }
            else if (run > 0 && run < need) { run++; on = true; } // released early: keep reporting until the minimum hold is met
            else { run = 0; on = false; }
            if (run == 0) _hold.Remove(i); else _hold[i] = run;
            if (on) s = s.With(i, true);
        }
        _tapped.Clear();
        if (Opposing == OpposingDirectionPolicy.Neutral)
        {
            if (s.IsPressed(ArcadeInput.P1Left) && s.IsPressed(ArcadeInput.P1Right)) s = s.With(ArcadeInput.P1Left, false).With(ArcadeInput.P1Right, false);
            if (s.IsPressed(ArcadeInput.P1Up) && s.IsPressed(ArcadeInput.P1Down)) s = s.With(ArcadeInput.P1Up, false).With(ArcadeInput.P1Down, false);
            if (s.IsPressed(ArcadeInput.P2Left) && s.IsPressed(ArcadeInput.P2Right)) s = s.With(ArcadeInput.P2Left, false).With(ArcadeInput.P2Right, false);
            if (s.IsPressed(ArcadeInput.P2Up) && s.IsPressed(ArcadeInput.P2Down)) s = s.With(ArcadeInput.P2Up, false).With(ArcadeInput.P2Down, false);
        }
        if (InputChanged != null)
            foreach (var i in Enum.GetValues<ArcadeInput>())
                if (s.IsPressed(i) != _last.IsPressed(i)) InputChanged(i, s.IsPressed(i));
        _last = s;
        return s;
    }

    private static bool IsStretched(ArcadeInput i) =>
        i is ArcadeInput.Coin1 or ArcadeInput.Coin2 or ArcadeInput.Test or ArcadeInput.Service1 or ArcadeInput.P1Start or ArcadeInput.P2Start;
}
