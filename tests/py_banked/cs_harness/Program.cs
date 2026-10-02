// Differential cross-check of the C# BankedRomController / PsxDmaChannel5 against recorded MAME output.
//   dotnet run -- ft  <ft_ops.lua> <ft_result.txt> <bankedroms.bin> <maincpu_rom.bin>
//   dotnet run -- ev  <ev_log.txt> <dumpdir|-> <bankedroms.bin> <maincpu_rom.bin>
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;
using System;
using System.Collections.Generic;
using System12.Runtime.Banked;

static class P
{
    const uint PatBase = 0xA5A5A5A5;
    static uint Pat(int i) => unchecked(PatBase + (uint)i * 0x01010101u);

    static int Main(string[] a)
    {
        var banked = File.ReadAllBytes(a[3]);
        var main = File.ReadAllBytes(a[4]);
        return a[0] == "ft" ? Ft(a[1], a[2], banked, main) : Ev(a[1], a[2], banked, main);
    }

    static int Ft(string opsPath, string resPath, byte[] banked, byte[] main)
    {
        var ctrl = new BankedRomController(banked, main);
        var ram = new byte[ctrl.RamSize];
        var dma = new PsxDmaChannel5(ctrl, ram);
        var exp = new List<string>();
        int i = 0;
        foreach (var line in File.ReadLines(opsPath))
        {
            var m = Regex.Match(line, @"^\s*\{""(\w+)"",(.*)\},\s*$");
            if (!m.Success) continue;
            i++;
            string k = m.Groups[1].Value;
            var args = m.Groups[2].Value.Split(',').Select(x => x == "false" ? -1L : long.Parse(x)).ToArray();
            if (k[0] == 'w')
            {
                uint ad = (uint)args[0], v = (uint)args[1];
                if (ad == 0x1F801000) ctrl.ExpBase = v;
                else if (ad == 0x1F8010F4) dma.WriteDicr(v);
                else if (k == "w32") ctrl.Write32(ad, v);
                else if (k == "w16") ctrl.Write16(ad, (ushort)v);
                else ctrl.Write8(ad, (byte)v);
                exp.Add($"{i} ok");
            }
            else if (k[0] == 'r')
            {
                uint ad = (uint)args[0];
                uint v = k == "r32" ? ctrl.Read32(ad) : k == "r16" ? ctrl.Read16(ad) : ctrl.Read8(ad);
                exp.Add($"{i} {v:x8}");
            }
            else
            {
                uint madr = (uint)args[0], bcr = (uint)args[1], chcr = (uint)args[2], dpcr = (uint)args[3];
                int nread = (int)args[4];
                int dest = (int)((madr & (uint)(ctrl.RamSize - 1)) & ~3u);
                nread = Math.Min(nread, (ctrl.RamSize - dest) / 4);
                for (int j = 0; j < nread; j++) BitConverter.GetBytes(Pat(j)).CopyTo(ram, dest + j * 4);
                dma.WriteDpcr(dpcr); dma.WriteMadr(madr); dma.WriteBcr(bcr); dma.WriteChcr(chcr);
                var sb = new StringBuilder();
                for (int j = 0; j < nread; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(BitConverter.ToUInt32(ram, dest + j * 4).ToString("x"));
                }
                exp.Add($"{i} chcr={dma.Chcr:x8} dicr={dma.Dicr:x8} data={sb}");
            }
        }
        var got = File.ReadAllLines(resPath).Where(l => l.Length > 0).ToArray();
        int bad = 0;
        for (int j = 0; j < Math.Min(exp.Count, got.Length); j++) if (exp[j] != got[j]) bad++;
        Console.WriteLine($"ft: ops={exp.Count} mame_lines={got.Length} mismatching={bad}");
        return bad == 0 && exp.Count == got.Length ? 0 : 1;
    }

    static uint BlockHash(ReadOnlySpan<byte> words)
    {
        uint h = 0;
        for (int i = 0; i < words.Length; i += 4) h = unchecked(h * 31 + BitConverter.ToUInt32(words.Slice(i, 4)));
        return h;
    }

    static int Ev(string logPath, string dumpDir, byte[] banked, byte[] main)
    {
        var ctrl = new BankedRomController(banked, main);
        var ram = new byte[ctrl.RamSize];
        var dma = new PsxDmaChannel5(ctrl, ram);
        var evRe = new Regex(@"^(\d+) f(\d+) ([RW]) ([0-9a-f]{8}) ([0-9a-f]{8}) ([0-9a-f]{8}) pc=");
        var byseq = new Dictionary<long, DmaSource>();
        long reads = 0, readBad = 0, blockOk = 0, blockBad = 0, dmas = 0, dumpOk = 0, dumpDiff = 0, missing = 0;
        foreach (var line in File.ReadLines(logPath))
        {
            var m = evRe.Match(line);
            if (m.Success)
            {
                long seq = long.Parse(m.Groups[1].Value);
                bool wr = m.Groups[3].Value == "W";
                uint addr = uint.Parse(m.Groups[4].Value, NumberStyles.HexNumber);
                uint data = uint.Parse(m.Groups[5].Value, NumberStyles.HexNumber);
                uint mask = uint.Parse(m.Groups[6].Value, NumberStyles.HexNumber);
                int size, lane;
                switch (mask)
                {
                    case 0xFFFFFFFF: size = 4; lane = 0; break;
                    case 0x0000FFFF: size = 2; lane = 0; break;
                    case 0xFFFF0000: size = 2; lane = 2; break;
                    case 0xFF: size = 1; lane = 0; break;
                    case 0xFF00: size = 1; lane = 1; break;
                    case 0xFF0000: size = 1; lane = 2; break;
                    default: size = 1; lane = 3; break;
                }
                uint ad = addr + (uint)lane;
                if (wr)
                {
                    uint v = (data & mask) >> (8 * lane);
                    if (addr == 0x1F8010D0) dma.WriteMadr(data);
                    else if (addr == 0x1F8010D4) dma.WriteBcr(data);
                    else if (addr == 0x1F8010F0) dma.WriteDpcr(data);
                    else if (addr == 0x1F8010D8)
                    {
                        dma.WriteChcr(data);
                        if (dma.Last is DmaSource s) byseq[seq] = s;
                    }
                    else if (size == 4) ctrl.Write32(ad, v);
                    else if (size == 2) ctrl.Write16(ad, (ushort)v);
                    else ctrl.Write8(ad, (byte)v);
                }
                else
                {
                    uint pred = size == 4 ? ctrl.Read32(ad) : size == 2 ? ctrl.Read16(ad) : ctrl.Read8(ad);
                    reads++;
                    if (pred != ((data & mask) >> (8 * lane))) readBad++;
                }
            }
            else if (line.StartsWith("DMASTART"))
            {
                dmas++;
                long seq = long.Parse(Regex.Match(line, @"seq=(\d+)").Groups[1].Value);
                if (!byseq.ContainsKey(seq)) missing++;
            }
            else if (line.StartsWith("DMAPOST"))
            {
                long seq = long.Parse(Regex.Match(line, @"seq=(\d+)").Groups[1].Value);
                int nominal = int.Parse(Regex.Match(line, @"size=(\d+)").Groups[1].Value);
                int nblk = int.Parse(Regex.Match(line, @"nblk=(\d+)").Groups[1].Value);
                var hs = Regex.Match(line, @"h=(\S*)").Groups[1].Value.Split(',');
                if (!byseq.TryGetValue(seq, out var s)) continue;
                for (int b = 0; b < nblk; b++)
                {
                    int want = Math.Min(256, nominal - b * 256);
                    if (b * 256 + want > s.Words) continue;
                    var h = BlockHash(s.Data.Span.Slice(b * 1024, want * 4));
                    if (h.ToString("x8") == hs[b]) blockOk++; else blockBad++;
                }
            }
            else if (line.StartsWith("DMADUMP") && dumpDir != "-")
            {
                long seq = long.Parse(Regex.Match(line, @"seq=(\d+)").Groups[1].Value);
                string f = Path.Combine(dumpDir, Regex.Match(line, @"file=(\S+)").Groups[1].Value);
                if (byseq.TryGetValue(seq, out var s) && File.Exists(f))
                {
                    if (File.ReadAllBytes(f).AsSpan().SequenceEqual(s.Data.Span)) dumpOk++; else dumpDiff++;
                }
            }
        }
        Console.WriteLine($"ev: reads={reads} readMismatch={readBad} dmaStarted={dmas} modelMissing={missing} blocksOk={blockOk} blocksBad={blockBad} dumpsOk={dumpOk} dumpsDiffer={dumpDiff}");
        return readBad == 0 && missing == 0 ? 0 : 1;
    }
}
