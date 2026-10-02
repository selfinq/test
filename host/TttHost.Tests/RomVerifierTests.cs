using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using TttHost;

namespace TttHost.Tests;

[TestFixture]
public class Crc32Tests
{
    [Test]
    public void StandardCheckValue()
    {
        // CRC-32/ISO-HDLC check value for "123456789" is 0xCBF43926
        Assert.That(Crc32.Of(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void IncrementalEqualsOneShot()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 + 7)).ToArray();
        var c = new Crc32();
        c.Append(data.AsSpan(0, 1234));
        c.Append(data.AsSpan(1234));
        Assert.That(c.Value, Is.EqualTo(Crc32.Of(data)));
    }
}

[TestFixture]
public class RomVerifierSyntheticTests
{
    private string _dir;
    private static readonly byte[] A = Encoding.ASCII.GetBytes("chip A contents");
    private static readonly byte[] B = Encoding.ASCII.GetBytes("chip B contents, longer");

    [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "ttthost_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
    [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Sha1(byte[] d) => Convert.ToHexString(SHA1.HashData(d)).ToLowerInvariant();

    private string WriteManifest()
    {
        string Member(string n, byte[] d) =>
            $"{{\"name\":\"{n}\",\"region\":\"r\",\"role\":\"x\",\"size\":{d.Length},\"crc32\":\"{Crc32.Of(d):x8}\",\"sha1\":\"{Sha1(d)}\",\"load\":\"LOAD @0x0\"}}";
        var p = Path.Combine(_dir, "m.json");
        File.WriteAllText(p, $"{{\"set\":\"synthetic\",\"members\":[{Member("a.bin", A)},{Member("b.bin", B)}]}}");
        return p;
    }

    private string WriteZip(params (string name, byte[] data)[] files)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using (var zf = ZipFile.Open(p, ZipArchiveMode.Create))
            foreach (var (n, d) in files)
            {
                var e = zf.CreateEntry(n);
                using var s = e.Open(); s.Write(d);
            }
        return p;
    }

    [Test]
    public void CompleteSet()
    {
        var r = RomVerifier.Verify(WriteZip(("a.bin", A), ("b.bin", B)), Manifest.Load(WriteManifest()));
        Assert.That(r.Complete, Is.True);
        Assert.That(r.Ok, Is.EquivalentTo(new[] { "a.bin", "b.bin" }));
    }

    [Test]
    public void MissingMember()
    {
        var r = RomVerifier.Verify(WriteZip(("a.bin", A)), Manifest.Load(WriteManifest()));
        Assert.That(r.Complete, Is.False);
        Assert.That(r.Missing, Is.EqualTo(new[] { "b.bin" }));
    }

    [Test]
    public void RenamedMemberIsIdentifiedByContentButNotComplete()
    {
        var r = RomVerifier.Verify(WriteZip(("a.bin", A), ("zzz.bin", B)), Manifest.Load(WriteManifest()));
        Assert.That(r.Complete, Is.False);
        Assert.That(r.WrongName.Single()["is"], Is.EqualTo("b.bin"));
        Assert.That(r.Missing, Is.Empty);
    }

    [Test]
    public void CorruptedContentIsBadContent()
    {
        var bad = (byte[])B.Clone(); bad[3] ^= 0xFF;
        var r = RomVerifier.Verify(WriteZip(("a.bin", A), ("b.bin", bad)), Manifest.Load(WriteManifest()));
        Assert.That(r.Complete, Is.False);
        Assert.That(r.BadContent.Single()["name"], Is.EqualTo("b.bin"));
    }

    [Test]
    public void UnknownExtraFileDoesNotMakeSetCompleteOrIncomplete()
    {
        // same behaviour as tools/verify_romset.py: unknown files are reported, completeness depends on the members
        var r = RomVerifier.Verify(WriteZip(("a.bin", A), ("b.bin", B), ("extra.txt", new byte[] { 1, 2, 3 })), Manifest.Load(WriteManifest()));
        Assert.That(r.Complete, Is.True);
        Assert.That(r.Unknown.Single()["name"], Is.EqualTo("extra.txt"));
    }

    [Test]
    public void OversizedFileIsUnknownWithoutHashing()
    {
        var r = RomVerifier.Verify(WriteZip(("a.bin", A), ("b.bin", B), ("huge.bin", new byte[1000])), Manifest.Load(WriteManifest()));
        Assert.That(r.Unknown.Single()["name"], Is.EqualTo("huge.bin"));
    }

    [TestCase("../evil.bin")]
    [TestCase("dir/../../evil.bin")]
    [TestCase("/abs.bin")]
    [TestCase("c:evil.bin")]
    public void UnsafePathsAreRejected(string name)
    {
        var zip = WriteZip(("a.bin", A), (name, B));
        Assert.Throws<RomArchiveException>(() => RomVerifier.Verify(zip, Manifest.Load(WriteManifest())));
    }

    [Test]
    public void NotAZipIsRejected()
    {
        var p = Path.Combine(_dir, "junk.zip");
        File.WriteAllText(p, "this is not a zip file");
        Assert.Throws<RomArchiveException>(() => RomVerifier.Verify(p, Manifest.Load(WriteManifest())));
    }
}

[TestFixture]
public class RomVerifierRealRomTests
{
    private static string Find(string rel)
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Test]
    public void RealTektagtZipIsCompleteIfPresent()
    {
        var zip = Environment.GetEnvironmentVariable("TTT_ROM_ZIP") ?? Find("input/roms/tektagt.zip");
        var manifest = Find("tools/tektagt_manifest.json");
        if (zip == null || !File.Exists(zip)) Assert.Ignore("no user-supplied ROM zip present (input/roms/tektagt.zip): real-data check not run");
        Assert.That(manifest, Is.Not.Null, "tools/tektagt_manifest.json not found");
        var m = Manifest.Load(manifest);
        var r = RomVerifier.Verify(zip, m);
        Assert.That(r.Ok.Count, Is.EqualTo(m.Members.Count));
        Assert.That(r.Complete, Is.True);
    }
}
