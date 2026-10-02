using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TttHost;

public sealed record ManifestMember(string Name, string Region, long Size, string Crc32, string Sha1, string Load);

public sealed class Manifest
{
    public string Set { get; init; } = "";
    public List<ManifestMember> Members { get; init; } = new();

    public static Manifest Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var m = new Manifest { Set = root.GetProperty("set").GetString() ?? "" };
        foreach (var e in root.GetProperty("members").EnumerateArray())
        {
            m.Members.Add(new ManifestMember(
                e.GetProperty("name").GetString()!,
                e.GetProperty("region").GetString() ?? "",
                e.GetProperty("size").GetInt64(),
                e.GetProperty("crc32").GetString()!.ToLowerInvariant(),
                e.GetProperty("sha1").GetString()!.ToLowerInvariant(),
                e.TryGetProperty("load", out var l) ? l.GetString() ?? "" : ""));
        }
        if (m.Members.Count == 0) throw new InvalidDataException("manifest has no members");
        return m;
    }
}

public sealed class RomReport
{
    [JsonPropertyName("ok")] public List<string> Ok { get; } = new();
    [JsonPropertyName("wrong_name")] public List<Dictionary<string, string>> WrongName { get; } = new();
    [JsonPropertyName("bad_content")] public List<Dictionary<string, object>> BadContent { get; } = new();
    [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();
    [JsonPropertyName("unknown")] public List<Dictionary<string, object>> Unknown { get; } = new();
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}

/// <summary>Raised for unusable or unsafe archives (maps to exit code 2, like tools/verify_romset.py).</summary>
public sealed class RomArchiveException : Exception
{
    public RomArchiveException(string message) : base(message) { }
}

/// <summary>
/// C# port of tools/verify_romset.py. Identity of a member is its content (size, CRC32, SHA-1);
/// names are only labels. Reads the zip in memory-bounded chunks, never extracts to disk, never executes anything.
/// </summary>
public static class RomVerifier
{
    public const int MaxMembers = 256;
    private const int Chunk = 1 << 20;

    private static (long size, string crc, string sha1) HashEntry(ZipArchiveEntry entry, long limit)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = new Crc32();
        long n = 0;
        var buf = new byte[Chunk];
        using var s = entry.Open();
        while (true)
        {
            int r = s.Read(buf, 0, buf.Length);
            if (r <= 0) break;
            n += r;
            if (n > limit) throw new RomArchiveException($"{entry.FullName}: exceeds size limit {limit}");
            sha.AppendData(buf, 0, r);
            crc.Append(buf.AsSpan(0, r));
        }
        return (n, crc.Value.ToString("x8"), Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    public static bool IsUnsafeName(string n) =>
        n.StartsWith('/') || n.StartsWith('\\') || n.Contains(':') ||
        n.Replace('\\', '/').Split('/').Contains("..");

    public static RomReport Verify(string zipPath, Manifest manifest)
    {
        var members = manifest.Members;
        long maxSize = members.Max(m => m.Size);
        var report = new RomReport();
        var bySha = members.ToDictionary(m => m.Sha1);
        var seen = new HashSet<string>();
        try
        {
            using var zf = ZipFile.OpenRead(zipPath);
            var entries = zf.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            if (entries.Count > MaxMembers) throw new RomArchiveException("too many members");
            foreach (var e in entries)
            {
                string n = e.FullName;
                if (IsUnsafeName(n)) throw new RomArchiveException($"unsafe member path: '{n}'");
                if (e.Length > maxSize)
                {
                    report.Unknown.Add(new() { ["name"] = n, ["reason"] = $"larger than any expected chip ({e.Length})" });
                    continue;
                }
                var (size, crc, sha) = HashEntry(e, maxSize);
                bySha.TryGetValue(sha, out var exp);
                var named = members.FirstOrDefault(m => m.Name == n);
                if (exp != null && exp.Name == n && size == exp.Size && crc == exp.Crc32)
                {
                    report.Ok.Add(n); seen.Add(n);
                }
                else if (exp != null)
                {
                    report.WrongName.Add(new() { ["found"] = n, ["is"] = exp.Name }); seen.Add(exp.Name);
                }
                else if (named != null)
                {
                    report.BadContent.Add(new() { ["name"] = n, ["size"] = size, ["crc32"] = crc, ["sha1"] = sha, ["expected_sha1"] = named.Sha1 });
                    seen.Add(n);
                }
                else
                {
                    report.Unknown.Add(new() { ["name"] = n, ["size"] = size, ["sha1"] = sha });
                }
            }
        }
        catch (InvalidDataException ex) { throw new RomArchiveException($"not a readable zip: {ex.Message}"); }
        report.Missing = members.Where(m => !seen.Contains(m.Name)).Select(m => m.Name).ToList();
        report.Complete = report.Ok.Count == members.Count && report.BadContent.Count == 0 && report.WrongName.Count == 0;
        return report;
    }
}
