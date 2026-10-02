// Console self tests for System12.Runtime.Input. No Main here (the owning csproj belongs to another agent): call
// InputSelfTests.RunAll(Console.Out) from any console harness; it returns the number of FAILED checks (0 = all passed).
// Nothing here needs game data. Tests tagged GOLDEN replay a trace recorded from MAME 0.264 and are reported as SKIP (not PASS)
// when the trace file is not present; see reports/trace_a08/ and docs/system12/INPUT_IO.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System12.Runtime.Core;

namespace System12.Runtime.Input;

public static class InputSelfTests
{
    private sealed class R
    {
        public int Pass, Fail, Skip;
        public readonly TextWriter Log;
        public R(TextWriter log) { Log = log; }
        public void Check(string name, bool ok, string detail = "")
        {
            if (ok) Pass++; else Fail++;
            Log.WriteLine($"[{(ok ? "PASS" : "FAIL"),-4}] {name}{(detail.Length > 0 && !ok ? "  -- " + detail : "")}");
        }
        public void Skipped(string name, string why) { Skip++; Log.WriteLine($"[SKIP] {name}  -- {why}"); }
        public void Eq<T>(string name, T expected, T actual) where T : notnull
            => Check(name, expected.Equals(actual), $"expected {expected} got {actual}");
    }

    public static int RunAll(TextWriter log)
    {
        var r = new R(log);
        Eeprom(r);
        EepromMmio(r);
        EepromStore(r);
        EepromGolden(r);
        Jamma(r);
        SharedWord(r);
        Mapper(r);
        Outputs(r);
        log.WriteLine($"InputSelfTests: {r.Pass} passed, {r.Fail} failed, {r.Skip} skipped");
        return r.Fail;
    }

    // ------------------------------------------------------------------ AT28C16
    private static void Eeprom(R r)
    {
        long t = 1_000;
        var e = new At28c16(() => t);
        r.Eq("eeprom: default fill byte 0 is 0xff", (byte)0xff, e.Read(0));
        r.Eq("eeprom: default fill byte 0x7ff is 0xff", (byte)0xff, e.Read(0x7ff));
        r.Check("eeprom: default image is 0x820 bytes of 0xff", e.Export().Length == 0x820 && e.Export().All(b => b == 0xff));

        e.Write(0x10, 0x32);
        r.Check("eeprom: busy right after a write", e.Busy);
        r.Eq("eeprom: busy read of the written address = value ^ 0x80 (data polling)", (byte)0xb2, e.Read(0x10));
        r.Eq("eeprom: busy read of ANY address = value ^ 0x80", (byte)0xb2, e.Read(0x400));
        e.Write(0x11, 0x55);                                  // dropped
        t += 199;
        r.Check("eeprom: still busy after 199 us", e.Busy);
        t += 1;
        r.Check("eeprom: idle at exactly 200 us", !e.Busy);
        r.Eq("eeprom: programmed byte reads back", (byte)0x32, e.Read(0x10));
        r.Eq("eeprom: write issued while busy was dropped", (byte)0xff, e.Read(0x11));
        r.Eq("eeprom: dropped-write counter", 1L, e.DroppedWhileBusyCount);

        e.Write(0x10, 0x32);                                  // same value: MAME does nothing
        r.Check("eeprom: writing the stored value does not start a cycle", !e.Busy);
        r.Eq("eeprom: same-value counter", 1L, e.SameValueWriteCount);

        e.Write(0x12, 0x7f);
        r.Eq("eeprom: polling value for 0x7f is 0xff", (byte)0xff, e.Read(0));
        t += 200;
        e.Write(0x13, 0x80);
        r.Eq("eeprom: polling value for 0x80 is 0x00", (byte)0x00, e.Read(0));
        t += 200;

        // A9 at 12 V: offsets >= 0x7e0 -> ID bytes 0x800..0x81f
        r.Check("eeprom: ID bytes default 0xff", e.Export()[0x800] == 0xff && e.Export()[0x81f] == 0xff);
        e.A9At12V = true;
        e.Write(0x7e0, 0x11);
        t += 200;
        r.Eq("eeprom: A9@12V read 0x7e0 returns ID byte 0", (byte)0x11, e.Read(0x7e0));
        e.A9At12V = false;
        r.Eq("eeprom: ID write did not touch data byte 0x7e0", (byte)0xff, e.Read(0x7e0));
        r.Eq("eeprom: raw ID byte 0 is 0x11", (byte)0x11, e.Export()[0x800]);

        // OE at 12 V: chip erase (also erases ID bytes), lastWrite = 0xff
        e.OeAt12V = true;
        e.Write(0, 0x00);
        r.Eq("eeprom: chip erase polling value is 0x7f", (byte)0x7f, e.Read(5));
        r.Eq("eeprom: chip erase counter", 1L, e.EraseCount);
        t += 200;
        e.OeAt12V = false;
        r.Check("eeprom: erase cleared data and ID bytes", e.Export().All(b => b == 0xff));
        r.Eq("eeprom: unmapped hole (>0x81f) reads 0", (byte)0, e.Read(0x900));
        r.Check("eeprom: offset >= 0x1000 rejected", Throws<ArgumentOutOfRangeException>(() => e.Read(0x1000)));

        // import / export
        var img = new byte[0x820]; for (var i = 0; i < img.Length; i++) img[i] = (byte)i;
        r.Check("eeprom: import of 0x820 bytes succeeds", e.TryImport(img));
        r.Check("eeprom: import round trips", e.Export().SequenceEqual(img));
        r.Check("eeprom: import of wrong size fails and keeps content", !e.TryImport(new byte[0x800]) && e.Export().SequenceEqual(img));
        e.SetDefault(new byte[] { 1, 2, 3 });
        r.Check("eeprom: default data region overlays the first bytes", e.Read(0) == 1 && e.Read(2) == 3 && e.Read(3) == 0xff);

        // events
        var evs = new List<EepromEvent>();
        var e2 = new At28c16(() => t); e2.Changed += evs.Add;
        e2.Write(1, 9); e2.Write(2, 9); t += 300; e2.Write(1, 9); e2.Write(1, 8);
        r.Check("eeprom: event sequence programmed/dropped/same/programmed",
            evs.Select(x => x.Kind).SequenceEqual(new[] { EepromEventKind.ByteProgrammed, EepromEventKind.WriteDroppedBusy, EepromEventKind.WriteSameValueIgnored, EepromEventKind.ByteProgrammed }));
    }

    private static bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

    // ------------------------------------------------------------------ MMIO lane semantics
    private static void EepromMmio(R r)
    {
        long t = 0;
        var chip = new At28c16(() => t);
        var dev = new EepromIoDevice(chip);
        r.Check("eepromio: window is 0x1f140000..0x1f141000", dev.Ranges[0] == new MmioRange(0x1f140000, 0x1f141000));
        // seed device bytes 0x290.. = 'T','E','G','2' (what the game reads at 0x1f140520)
        var img = new byte[0x820]; Array.Fill(img, (byte)0xff);
        img[0x290] = (byte)'T'; img[0x291] = (byte)'E'; img[0x292] = (byte)'G'; img[0x293] = (byte)'2';
        chip.TryImport(img);
        r.Eq("eepromio: lw @+0x520 = byte(0x290) | byte(0x291)<<16", 0x00450054u, dev.ReadU32(0x520));
        r.Eq("eepromio: lw @+0x524 = 'G' | '2'<<16", 0x00320047u, dev.ReadU32(0x524));
        r.Eq("eepromio: lh @+0x520 = 'T'", (ushort)0x54, dev.ReadU16(0x520));
        r.Eq("eepromio: lh @+0x522 = 'E'", (ushort)0x45, dev.ReadU16(0x522));
        r.Eq("eepromio: lb @+0x520 = 'T'", (byte)0x54, dev.ReadU8(0x520));
        r.Eq("eepromio: lb @+0x522 = 'E'", (byte)0x45, dev.ReadU8(0x522));
        r.Eq("eepromio: odd byte lane reads 0 (MAME unmapped)", (byte)0, dev.ReadU8(0x521));
        r.Eq("eepromio: unmapped-lane counter", 1L, dev.UnmappedLaneAccesses);

        // 32-bit store: lane 0 starts a write cycle, lane 2 is dropped by the chip (MAME behaviour)
        dev.WriteU32(0x500, 0x00aa0055);
        r.Eq("eepromio: sw first byte busy poll", (byte)0xd5, dev.ReadU8(0x500));
        t += 200;
        r.Eq("eepromio: sw lane0 stored", (byte)0x55, dev.ReadU8(0x500));
        r.Eq("eepromio: sw lane2 was dropped (busy)", (byte)0xff, dev.ReadU8(0x502));
        dev.WriteU16(0x502, 0xbb77);                         // halfword: only the low byte reaches the chip
        t += 200;
        r.Eq("eepromio: sh stores low byte only", (byte)0x77, dev.ReadU8(0x502));
        dev.WriteU8(0x504, 0x12); t += 200;
        r.Eq("eepromio: sb at +0x504 -> device offset 0x282", (byte)0x12, chip.Export()[0x282]);
        dev.WriteU8(0x505, 0x99);
        r.Check("eepromio: sb to odd lane ignored", !chip.Busy && chip.Export()[0x282] == 0x12);
        r.Check("eepromio: window top byte maps to device offset 0x7ff", dev.ReadU16(0xffe) == chip.Read(0x7ff));

        // game-style routine seen at pc 8017fe80..8017fec4: read, write, poll bit7 until it equals bit7 written, verify
        t += 1000;
        dev.WriteU16(0x600, 0x00c1);
        var polls = 0;
        while ((dev.ReadU16(0x600) & 0x80) != (0xc1 & 0x80)) { polls++; t += 50; if (polls > 100) break; }
        r.Check("eepromio: /DATA polling completes after the 200 us cycle", polls == 4, $"polls={polls}");

        var devs = ArcadeIoFactory.CreateMainBusDevices(chip, out var wd);
        r.Check("eepromio: factory returns 5 non-overlapping devices", devs.Count == 5 && NoOverlap(devs));
        var kicks = 0; wd.Kicked += _ => kicks++;
        wd.WriteU16(0, 0); wd.WriteU32(0, 0);
        r.Check("watchdog: kicks observable, enforcement absent", kicks == 2 && wd.WriteCount == 2);
        r.Check("nopw window: read is NOT IMPLEMENTED (throws)", Throws<NotImplementedMmioException>(() => wd.ReadU32(0)));
    }

    private static bool NoOverlap(IReadOnlyList<IMmioDevice> d)
    {
        var rs = d.SelectMany(x => x.Ranges).ToList();
        for (var i = 0; i < rs.Count; i++) for (var j = i + 1; j < rs.Count; j++) if (rs[i].Overlaps(rs[j])) return false;
        return true;
    }

    // ------------------------------------------------------------------ persistence
    private static void EepromStore(R r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "s12_eeprom_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            long t = 0;
            var chip = new At28c16(() => t);
            var store = new At28c16Store("tektagt", dir);
            r.Check("store: path layout is <dir>/nvram/tektagt/at28c16 (MAME compatible)",
                store.FilePath == Path.Combine(dir, "nvram", "tektagt", "at28c16"));
            r.Eq("store: missing file -> default", EepromLoadStatus.DefaultedNoFile, store.Load(chip));
            r.Check("store: default is all 0xff and clean", chip.Export().All(b => b == 0xff) && !chip.Dirty);
            r.Check("store: SaveIfDirty on a clean chip writes nothing", !store.SaveIfDirty(chip) && !File.Exists(store.FilePath));
            chip.Write(0x20, 0x5a); t += 200;
            r.Check("store: dirty after a write; SaveIfDirty writes", chip.Dirty && store.SaveIfDirty(chip) && !chip.Dirty);
            r.Eq("store: file is exactly 0x820 bytes", 0x820L, new FileInfo(store.FilePath).Length);
            var chip2 = new At28c16(() => t);
            r.Eq("store: reload", EepromLoadStatus.Loaded, store.Load(chip2));
            r.Eq("store: reloaded byte", (byte)0x5a, chip2.Read(0x20));
            File.WriteAllBytes(store.FilePath, new byte[100]);
            var chip3 = new At28c16(() => t);
            r.Eq("store: wrong-size file -> MAME-style zeroed RAM + status", EepromLoadStatus.CorruptFileZeroed, store.Load(chip3));
            r.Check("store: corrupt load leaves zeros", chip3.Export().All(b => b == 0));
            r.Check("store: invalid game name rejected", Throws<ArgumentException>(() => new At28c16Store("../x", dir)));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* ignore */ } }
    }

    // ------------------------------------------------------------------ GOLDEN: replay of the MAME 0.264 fresh-NVRAM boot
    private static string? FindTrace(string file)
    {
        var env = Environment.GetEnvironmentVariable("S12_TRACE_A08_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(env)) candidates.Add(Path.Combine(env, file));
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "reports", "trace_a08", file));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void EepromGolden(R r)
    {
        var log = FindTrace("probe_eeprom_fresh_log.txt");
        var nv = FindTrace("eeprom_fresh_final.bin");
        if (log == null || nv == null) { r.Skipped("golden: replay of MAME fresh-NVRAM EEPROM trace", "trace files not found (set S12_TRACE_A08_DIR)"); return; }
        long now = 0;
        var chip = new At28c16(() => (long)now);
        var dev = new EepromIoDevice(chip);
        int reads = 0, writes = 0, mismatches = 0; string firstBad = "";
        foreach (var line in File.ReadLines(log))
        {
            // f380 t=6352414.2us R addr=1f140520 data=000000ff mask=0000ffff pc=8017d668
            var p = line.Split(' ');
            if (p.Length < 7) continue;
            var us = double.Parse(p[1].AsSpan(2, p[1].Length - 4), CultureInfo.InvariantCulture);
            var isRead = p[2] == "R";
            var addr = Convert.ToUInt32(p[3][5..], 16);
            var data = Convert.ToUInt32(p[4][5..], 16);
            var mask = Convert.ToUInt32(p[5][5..], 16);
            now = (long)Math.Floor(us);
            var lane = (mask & 0xffff0000u) != 0 ? 1 : 0;            // lane 0 = bits 0-7, lane 2 = bits 16-23
            var devOff = (int)((addr - 0x1f140000u) >> 1) + lane;
            var value = (byte)((data >> (lane * 16)) & 0xff);
            if (isRead)
            {
                reads++;
                var got = chip.Read(devOff);
                if (got != value) { mismatches++; if (firstBad.Length == 0) firstBad = $"read dev[{devOff:x}] at t={us}: MAME {value:x2} model {got:x2}"; }
            }
            else { writes++; chip.Write(devOff, value); }
        }
        r.Check($"golden: {reads} reads / {writes} writes of the MAME boot trace replay with 0 read mismatches", mismatches == 0 && reads > 1000 && writes > 500, firstBad);
        var final = File.ReadAllBytes(nv);
        var model = chip.Export();
        var diffs = Enumerable.Range(0, Math.Min(final.Length, model.Length)).Count(i => final[i] != model[i]);
        r.Check("golden: final 0x820-byte image equals the .nv file MAME saved", final.Length == 0x820 && diffs == 0, $"{diffs} differing bytes");
    }

    // ------------------------------------------------------------------ JAMMA words
    // Measured: value the H8 reads at 0x300000 / 0x300002 while exactly one MAME input field is held (golden_inr.txt).
    private static readonly (ArcadeInput In, ushort W0, ushort W1)[] MameInr =
    {
        (ArcadeInput.P1Start, 0xff7f, 0xffff), (ArcadeInput.P2Start, 0x7fff, 0xffff),
        (ArcadeInput.P1Button1, 0xffef, 0xffff), (ArcadeInput.P1Button2, 0xffdf, 0xffff), (ArcadeInput.P1Button3, 0xffbf, 0xffff),
        (ArcadeInput.P1Down, 0xfffb, 0xffff), (ArcadeInput.P1Left, 0xfffd, 0xffff), (ArcadeInput.P1Right, 0xfffe, 0xffff), (ArcadeInput.P1Up, 0xfff7, 0xffff),
        (ArcadeInput.P2Button1, 0xefff, 0xffff), (ArcadeInput.P2Button2, 0xdfff, 0xffff), (ArcadeInput.P2Button3, 0xbfff, 0xffff),
        (ArcadeInput.P2Down, 0xfbff, 0xffff), (ArcadeInput.P2Left, 0xfdff, 0xffff), (ArcadeInput.P2Right, 0xfeff, 0xffff), (ArcadeInput.P2Up, 0xf7ff, 0xffff),
        (ArcadeInput.Coin1, 0xffff, 0xdfff), (ArcadeInput.Coin2, 0xffff, 0xefff),
        (ArcadeInput.P1Button4, 0xffff, 0xfffd), (ArcadeInput.P1Button5, 0xffff, 0xfffb), (ArcadeInput.P1Button6, 0xffff, 0xfff7),
        (ArcadeInput.P2Button4, 0xffff, 0xffdf), (ArcadeInput.P2Button5, 0xffff, 0xffbf), (ArcadeInput.P2Button6, 0xffff, 0xff7f),
        (ArcadeInput.Service1, 0xffff, 0x7fff), (ArcadeInput.Test, 0xffff, 0xbfff),
    };

    private static void Jamma(R r)
    {
        var port = new JammaPort();
        r.Check("jamma: idle words are 0xffff 0xffff", port.ReadWord(0) == 0xffff && port.ReadWord(1) == 0xffff);
        r.Eq("jamma: DSW default 0xff (matches MAME DSW port)", (byte)0xff, port.Dsw);
        var bad = new List<string>();
        foreach (var (i, w0, w1) in MameInr)
        {
            port.State = ArcadeInputState.Idle.With(i, true);
            if (port.ReadWord(0) != w0 || port.ReadWord(1) != w1)
                bad.Add($"{i}: model {port.ReadWord(0):x4} {port.ReadWord(1):x4} MAME {w0:x4} {w1:x4}");
        }
        r.Check($"jamma: all {MameInr.Length} measured MAME 0.264 input->H8-word pairs reproduced", bad.Count == 0, string.Join("; ", bad));

        // every combination of two different inputs = AND of the two active-low words (no cross talk)
        var all = MameInr.Select(x => x).ToArray();
        var xt = 0;
        for (var a = 0; a < all.Length; a++) for (var b = a + 1; b < all.Length; b++)
        {
            port.State = ArcadeInputState.Idle.With(all[a].In, true).With(all[b].In, true);
            if (port.ReadWord(0) != (ushort)(all[a].W0 & all[b].W0) || port.ReadWord(1) != (ushort)(all[a].W1 & all[b].W1)) xt++;
        }
        r.Eq("jamma: no cross talk between any two inputs", 0, xt);

        // byte-level H8 helpers
        port.State = ArcadeInputState.Idle.With(ArcadeInput.P1Start, true);
        r.Check("jamma: TryRead8 offsets", port.TryRead8(0x300001, out var b1) && b1 == 0x7f && port.TryRead8(0x300000, out var b0) && b0 == 0xff && !port.TryRead8(0x300004, out _));
        r.Check("jamma: TryRead16 only at 0x300000/0x300002", port.TryRead16(0x300000, out var w) && w == 0xff7f && !port.TryRead16(0x300001, out _));

        // BTN7/BTN8 follow the driver source (NOT verified on tektagt - the MAME 0.264 binary has no such ports)
        port.State = ArcadeInputState.Idle.With(ArcadeInput.P1Button7, true).With(ArcadeInput.P2Button8, true);
        r.Eq("jamma: [source-derived] P1 BTN7 + P2 BTN8 -> byte2 0xf6", (byte)0xf6, JammaPort.JammaByte(port.State, 2));
    }

    // ------------------------------------------------------------------ shared RAM word (measured values)
    private static void SharedWord(R r)
    {
        // value published at 0x83140 for each single P1 input (probe_fields16k_log.txt)
        var table = new (ArcadeInput In, ushort Shared)[]
        {
            (ArcadeInput.P1Start, 0x8000), (ArcadeInput.Service1, 0x4000), (ArcadeInput.P1Up, 0x2000), (ArcadeInput.P1Down, 0x1000),
            (ArcadeInput.P1Left, 0x0800), (ArcadeInput.P1Right, 0x0400), (ArcadeInput.P1Button1, 0x0200), (ArcadeInput.P1Button2, 0x0100),
            (ArcadeInput.P1Button3, 0x0080), (ArcadeInput.P1Button4, 0x0040), (ArcadeInput.P1Button5, 0x0020), (ArcadeInput.P1Button6, 0x0010),
        };
        var bad = table.Where(x => SharedRamPlayerWord.Encode(ArcadeInputState.Idle.With(x.In, true).Player1) != x.Shared).Select(x => x.In.ToString()).ToList();
        r.Check("sharedword: Encode reproduces the 12 measured P1 words", bad.Count == 0, string.Join(",", bad));
        r.Check("sharedword: Decode(Encode(x)) round trips for all 12 bits", Enumerable.Range(0, 4096).All(m =>
        {
            ushort w = 0; ushort[] bits = { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01, 0x8000, 0x4000, 0x2000, 0x1000 };
            for (var i = 0; i < 12; i++) if ((m >> i & 1) != 0) w |= bits[i];
            return SharedRamPlayerWord.Decode(SharedRamPlayerWord.Encode(w)) == w;
        }));
        r.Eq("sharedword: main-bus address of 0x83140", 0x1f083140u, SharedRamLayout.MainBusAddress(SharedRamLayout.P1Current));
    }

    // ------------------------------------------------------------------ mapper
    private static void Mapper(R r)
    {
        var m = new HostInputMapper(DefaultBindings.Keyboard);
        r.Check("mapper: unbound source ignored (false)", !m.SetDown("Key:Q9", true));
        m.SetDown("Key:Up", true); m.SetDown("Key:LeftCtrl", true);
        var s = m.Sample();
        r.Check("mapper: P1 up + button1", s.IsPressed(ArcadeInput.P1Up) && s.IsPressed(ArcadeInput.P1Button1) && !s.IsPressed(ArcadeInput.P1Button2));
        m.SetDown("Key:Up", false); m.SetDown("Key:LeftCtrl", false);
        s = m.Sample();
        r.Check("mapper: released", s == ArcadeInputState.Idle);

        // tap latch: press+release between samples still reported once
        m.SetDown("Key:Z", true); m.SetDown("Key:Z", false);
        r.Check("mapper: tap between samples is reported once", m.Sample().IsPressed(ArcadeInput.P1Button5) && !m.Sample().IsPressed(ArcadeInput.P1Button5));

        // coin stretching (MinHoldSamples = 2): one short tap -> exactly two samples
        m.SetDown("Key:Num5", true); m.SetDown("Key:Num5", false);
        var c1 = m.Sample().Coin1; var c2 = m.Sample().Coin1; var c3 = m.Sample().Coin1;
        r.Check("mapper: coin tap stretched to 2 samples", c1 && c2 && !c3);
        m.SetDown("Key:Num5", true);
        var held = Enumerable.Range(0, 5).All(_ => m.Sample().Coin1);
        m.SetDown("Key:Num5", false);
        var tail = m.Sample().Coin1;
        r.Check("mapper: held coin stays on, released after >= min hold without extra tail", held && !tail);

        // SOCD
        m.SetDown("Key:Left", true); m.SetDown("Key:Right", true);
        s = m.Sample();
        r.Check("mapper: left+right -> neutral", !s.IsPressed(ArcadeInput.P1Left) && !s.IsPressed(ArcadeInput.P1Right));
        m.Opposing = OpposingDirectionPolicy.PassThrough;
        s = m.Sample();
        r.Check("mapper: pass-through policy reports both", s.IsPressed(ArcadeInput.P1Left) && s.IsPressed(ArcadeInput.P1Right));
        m.ReleaseAll(); m.Opposing = OpposingDirectionPolicy.Neutral;

        // gamepad + axis
        var all = DefaultBindings.Keyboard.Concat(DefaultBindings.Gamepad(0, 1)).Concat(DefaultBindings.Gamepad(1, 2));
        var m2 = new HostInputMapper(all);
        m2.SetAxis("Pad0:LeftStickX", -0.9);
        r.Check("mapper: stick axis left", m2.Sample().IsPressed(ArcadeInput.P1Left));
        m2.SetAxis("Pad0:LeftStickX", 0.1);
        r.Check("mapper: stick back inside dead zone", !m2.Sample().IsPressed(ArcadeInput.P1Left));
        m2.SetDown("Pad1:West", true);
        r.Check("mapper: pad 1 acts as player 2", m2.Sample().IsPressed(ArcadeInput.P2Button1));
        m2.ReleaseAll();

        // text table round trip + strictness
        var text = BindingTable.ToText(DefaultBindings.Keyboard);
        var back = BindingTable.Parse(text);
        r.Check("bindings: text round trip keeps every binding",
            back.Count == DefaultBindings.Keyboard.Count && back.All(b => DefaultBindings.Keyboard.Contains(b)));
        r.Check("bindings: unknown target is an error, not skipped", Throws<FormatException>(() => BindingTable.Parse("P3Start = Key:A")));
        r.Check("bindings: missing '=' is an error", Throws<FormatException>(() => BindingTable.Parse("P1Start Key:A")));
        r.Check("bindings: comments and blanks ok", BindingTable.Parse("# x\n\nP1Start = Key:A # y\n").Count == 1);

        // every arcade input is reachable from the default keyboard table
        var missing = Enum.GetValues<ArcadeInput>().Where(i => !DefaultBindings.Keyboard.Any(b => b.Target == i)).ToList();
        r.Check("bindings: default keyboard table covers every arcade input", missing.Count == 0, string.Join(",", missing));
        var dup = DefaultBindings.Keyboard.GroupBy(b => b.Source).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        r.Check("bindings: no host key bound twice in the default table", dup.Count == 0, string.Join(",", dup));
    }

    // ------------------------------------------------------------------ outputs
    private static void Outputs(R r)
    {
        var words = new Dictionary<int, ushort>();
        ushort W(int a) => words.TryGetValue(a, out var v) ? v : (ushort)0;
        var outs = new ArcadeOutputs();
        var evs = new List<ArcadeOutputEvent>();
        outs.Changed += evs.Add;
        var mon = new SharedRamInputMonitor(W, outs);
        mon.Poll(0);
        words[SharedRamLayout.Coin1Pending] = 1; mon.Poll(1);                // coin 1 pulse
        words[SharedRamLayout.Coin2Pending] = 1; mon.Poll(2);                // coin 2 pulse
        words[SharedRamLayout.Coin1Pending] = 2; mon.Poll(3);                // second coin 1 pulse
        words[SharedRamLayout.Coin1Pending] = 1; mon.Poll(4);                // 1P START consumed one
        words[SharedRamLayout.P1Current] = 0x8000; words[SharedRamLayout.TestSwitch] = 0x8000; mon.Poll(5);
        r.Check("outputs: event sequence matches the measured MAME credit sequence",
            evs.Select(e => (e.Name, e.Index, e.NewValue)).SequenceEqual(new[]
            {
                ("coin_accepted", 0, 1L), ("coin_accepted", 1, 1L), ("coin_accepted", 0, 2L), ("coins_consumed", 0, 1L), ("test_switch", 0, 0x8000L),
            }));
        r.Check("outputs: counters", outs.CoinsAccepted(0) == 2 && outs.CoinsAccepted(1) == 1 && outs.CoinsConsumed(0) == 1 && outs.CoinsConsumed(1) == 0);
        r.Check("outputs: published P1 start decoded", mon.LastPublishedState.IsPressed(ArcadeInput.P1Start) && mon.LastPublishedState.Test);
        r.Check("outputs: lamps / coin coil / lockout declared NotPresentInReference (never raised)",
            ArcadeOutputs.Catalog.Count(c => c.Support == OutputSupport.NotPresentInReference) == 4);
        r.Check("outputs: poll with no change raises nothing", Quiet(mon, evs));
        var img = new byte[0x10000];
        img[0x3900] = 0x00; img[0x3901] = 0x02;                              // H8 0x83900 = 0x0002 (big endian bytes)
        var rd = SharedRamInputMonitor.BigEndianReader(img);
        r.Check("outputs: BigEndianReader matches the S12SoundBoard.SharedRam layout", rd(0x83900) == 2 && rd(0x83901 - 1) == 2 && rd(0x83902) == 0);
        r.Check("outputs: BigEndianReader rejects addresses outside 0x80000..0x8fffe", Throws<ArgumentOutOfRangeException>(() => rd(0x90000)));
    }

    private static bool Quiet(SharedRamInputMonitor mon, List<ArcadeOutputEvent> evs) { var n = evs.Count; mon.Poll(6); mon.Poll(7); return evs.Count == n; }
}
