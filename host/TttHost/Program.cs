using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TttHost;

/// <summary>
/// TttRecompHost: status + ROM-set check. It does NOT run the game (nothing that could run it exists yet).
/// Exit codes: 0 = ROM set verified complete; 1 = ROM set incomplete/mismatched; 2 = usage error, no ROM, or unsafe archive.
/// In every case the process also prints that the game was not executed.
/// </summary>
public static class Program
{
    private const string Usage = """
        usage: TttRecompHost [--rom <tektagt.zip>] [--manifest <tektagt_manifest.json>] [--json <report.json>] [--status] [--version]
          --rom       ROM zip you supply yourself (default: $TTT_ROM_ZIP, then ./input/roms/tektagt.zip found upward from cwd)
          --manifest  member manifest (default: next to the executable, then tools/tektagt_manifest.json found upward from cwd)
          --status    print the implementation status only and exit 0
          --json      also write the verification report as JSON
        exit codes: 0 ROM set verified | 1 ROM set incomplete or mismatched | 2 usage error, no ROM, unsafe archive
        The game is NOT run by this program (see the status section of the output).
        """;

    public static int Main(string[] args)
    {
        string rom = null, manifestPath = null, jsonOut = null;
        bool statusOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": rom = Next(args, ref i); break;
                case "--manifest": manifestPath = Next(args, ref i); break;
                case "--json": jsonOut = Next(args, ref i); break;
                case "--status": statusOnly = true; break;
                case "--version": Console.WriteLine($"TttRecompHost {Version} (status build, does not run the game)"); return 0;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default: Console.Error.WriteLine($"unknown argument: {args[i]}\n{Usage}"); return 2;
            }
            if (i < 0) return 2;
        }

        PrintStatus();
        if (statusOnly) return 0;

        Console.WriteLine();
        Console.WriteLine("ROM check");
        Console.WriteLine("---------");
        rom ??= Environment.GetEnvironmentVariable("TTT_ROM_ZIP");
        rom ??= FindUpward("input/roms/tektagt.zip");
        if (string.IsNullOrEmpty(rom))
        {
            Console.WriteLine("ROM zip: NOT FOUND. Pass --rom <your tektagt.zip>, set TTT_ROM_ZIP, or place it at input/roms/tektagt.zip.");
            Console.WriteLine("This project never downloads ROMs.");
            return Finish(2);
        }
        if (!File.Exists(rom)) { Console.WriteLine($"ROM zip: file not found: {rom}"); return Finish(2); }

        manifestPath ??= Path.Combine(AppContext.BaseDirectory, "tektagt_manifest.json");
        if (!File.Exists(manifestPath)) manifestPath = FindUpward("tools/tektagt_manifest.json");
        if (manifestPath == null || !File.Exists(manifestPath))
        {
            Console.WriteLine("manifest: NOT FOUND (expected tektagt_manifest.json next to the executable or tools/tektagt_manifest.json). Use --manifest.");
            return Finish(2);
        }

        Manifest manifest;
        try { manifest = Manifest.Load(manifestPath); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidDataException or InvalidOperationException)
        {
            Console.WriteLine($"manifest unreadable ({manifestPath}): {e.Message}");
            return Finish(2);
        }
        Console.WriteLine($"ROM zip : {Path.GetFullPath(rom)}");
        Console.WriteLine($"manifest: {Path.GetFullPath(manifestPath)} (set '{manifest.Set}', {manifest.Members.Count} members)");

        RomReport report;
        try { report = RomVerifier.Verify(rom, manifest); }
        catch (Exception e) when (e is RomArchiveException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"ROM zip rejected: {e.Message}");
            return Finish(2);
        }

        Console.WriteLine($"ok members: {report.Ok.Count}/{manifest.Members.Count}");
        foreach (var w in report.WrongName) Console.WriteLine($"  wrong name : '{w["found"]}' has the content of '{w["is"]}'");
        foreach (var b in report.BadContent) Console.WriteLine($"  bad content: '{b["name"]}' (crc32 {b["crc32"]}, sha1 {b["sha1"]})");
        foreach (var m in report.Missing) Console.WriteLine($"  missing    : {m}");
        foreach (var u in report.Unknown) Console.WriteLine($"  unknown    : {u["name"]}");
        Console.WriteLine(report.Complete ? "ROM set: COMPLETE (every member matches size, CRC32 and SHA-1)" : "ROM set: INCOMPLETE or MISMATCHED");

        if (jsonOut != null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"report written: {jsonOut}");
        }
        return Finish(report.Complete ? 0 : 1);
    }

    private static int Finish(int code)
    {
        Console.WriteLine();
        Console.WriteLine("RESULT: the game was NOT run. This executable cannot run it yet.");
        return code;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"{args[i]} needs a value\n{Usage}"); i = -2; return null; }
        return args[++i];
    }

    private static string FindUpward(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, relative);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    private static void PrintStatus()
    {
        Console.WriteLine($"TttRecompHost {Version}  [{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}]");
        Console.WriteLine("Namco System 12 'Tekken Tag Tournament' (tektagt) static recompilation: STATUS BUILD");
        Console.WriteLine("*** This program does NOT run the game. No gameplay, video, audio or input exist yet. ***");
        Console.WriteLine();
        Console.WriteLine("Linked at build time");
        Console.WriteLine("--------------------");
        Console.WriteLine("System12.Runtime assembly : " + RuntimeLine());
        Console.WriteLine("Recompiled game code      : " + RecompLine());
        Console.WriteLine();
        Console.WriteLine("Implemented");
        Console.WriteLine("-----------");
        Console.WriteLine("  [x] ROM set verification by content (size/CRC32/SHA-1), port of tools/verify_romset.py");
        Console.WriteLine();
        Console.WriteLine("NOT implemented");
        Console.WriteLine("---------------");
        foreach (var s in NotImplemented) Console.WriteLine("  [ ] " + s);
    }

    private static readonly string[] NotImplemented =
    {
        "loading the ROM regions / boot image into emulated memory (done only by the Python tools so far)",
        "validated recompiled game code (function detection is known to be wrong: huge functions; no coverage check)",
        "System 12 memory map, banked ROM, tektagt DMA/protection registers",
        "MIPS R3000-class execution of recompiled code, interrupts, timers",
        "GPU/GTE (PlayStation-style) rendering and any window/swap chain",
        "sound: H8/3002 sound CPU and Namco C352 PCM",
        "input, JVS, coin, EEPROM/NVRAM",
        "any gameplay, any Windows run (nothing here was executed on Windows)",
    };

    private static string RuntimeLine()
    {
#if HAVE_S12_RUNTIME
        try
        {
            var a = Assembly.Load("System12.Runtime");
            return $"REFERENCED ({a.GetName().Name} {a.GetName().Version}, {a.GetExportedTypes().Length} public types; its contents are not exercised by this program)";
        }
        catch (Exception e) { return $"referenced but failed to load: {e.GetType().Name}: {e.Message}"; }
#else
        return "not referenced (src/System12.Runtime/System12.Runtime.csproj did not exist at build time, or UseSystem12Runtime=false)";
#endif
    }

    private static string RecompLine()
    {
#if HAVE_RECOMP
        var t = typeof(Program).Assembly.GetType("Recompiled.Entry");
        if (t == null) return "included, but no Recompiled.Entry type was found";
        string F(string n) => t.GetField(n)?.GetRawConstantValue() is uint v ? $"0x{v:X8}" : "?";
        return $"INCLUDED (Recompiled.Entry: ImageBase={F("ImageBase")}, InitialPc={F("InitialPc")}); Entry.Run() is not called by this program";
#else
        return "not included (build with -p:IncludeRecomp=true to compile generated/recomp/*.cs)";
#endif
    }
}
