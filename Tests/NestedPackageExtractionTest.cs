using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;

namespace Tests;

/// <summary>
/// Guards the mechanism used by both the GUI (ExtractNestedPackageRecursive) and the CLI
/// (--recursive_vpk) to descend into vpks nested inside other vpks and extract their contents,
/// including several levels deep.
/// </summary>
public class NestedPackageExtractionTest
{
    private string tempDir;

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vrf_nested_vpk_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void ExtractsContentsOfVpkNestedInsideVpk()
    {
        var innerFiles = new (string Path, byte[] Data)[]
        {
            ("foo/bar.txt", Encoding.UTF8.GetBytes("hello from nested bar")),
            ("baz.txt", Encoding.UTF8.GetBytes("nested baz contents")),
        };

        var innerVpkBytes = BuildVpk(innerFiles);

        var outerVpkPath = Path.Combine(tempDir, "outer.vpk");
        using (var outer = new Package())
        {
            outer.AddFile("nested/inner.vpk", innerVpkBytes);
            outer.AddFile("sibling.txt", Encoding.UTF8.GetBytes("outer sibling"));
            outer.Write(outerVpkPath);
        }

        var extractDir = Path.Combine(tempDir, "extracted");

        using (var outerPackage = new Package())
        {
            outerPackage.Read(outerVpkPath);
            ExtractPackageRecursive(outerPackage, extractDir);
        }

        foreach (var (path, data) in innerFiles)
        {
            var outPath = Path.Combine(extractDir, path);
            Assert.That(File.Exists(outPath), Is.True, $"expected extracted file {path}");
            Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(data), $"contents mismatch for {path}");
        }
    }

    [Test]
    public void ExtractsDeeplyNestedVpks()
    {
        // Build a 4-levels-deep chain: outer.vpk -> a.vpk -> b.vpk -> c.vpk -> leaf.txt
        const int Depth = 4;
        var leaf = Encoding.UTF8.GetBytes("deep leaf payload");

        var currentVpkBytes = BuildVpk([("leaf.txt", leaf)]);

        // Wrap repeatedly; each level also carries a marker file to prove every level is descended into
        for (var level = Depth - 1; level >= 1; level--)
        {
            currentVpkBytes = BuildVpk(
            [
                ($"level{level}/inner.vpk", currentVpkBytes),
                ($"level{level}/marker.txt", Encoding.UTF8.GetBytes($"marker at level {level}")),
            ]);
        }

        var outerVpkPath = Path.Combine(tempDir, "outer.vpk");
        File.WriteAllBytes(outerVpkPath, currentVpkBytes);

        var extractDir = Path.Combine(tempDir, "extracted");

        using (var outerPackage = new Package())
        {
            outerPackage.Read(outerVpkPath);
            ExtractPackageRecursive(outerPackage, extractDir);
        }

        // The leaf survived all 4 levels of descent
        var leafPath = Path.Combine(extractDir, "leaf.txt");
        Assert.That(File.Exists(leafPath), Is.True, "leaf file should be extracted from the deepest vpk");
        Assert.That(File.ReadAllBytes(leafPath), Is.EqualTo(leaf));

        // Every intermediate level's marker was extracted, proving each level was descended into
        for (var level = 1; level <= Depth - 1; level++)
        {
            var markerPath = Path.Combine(extractDir, $"level{level}", "marker.txt");
            Assert.That(File.Exists(markerPath), Is.True, $"marker at level {level} should be extracted");
        }

        // No raw nested .vpk should be left behind
        Assert.That(Directory.EnumerateFiles(extractDir, "*.vpk", SearchOption.AllDirectories), Is.Empty,
            "no raw nested vpk should remain after recursive extraction");
    }

    private byte[] BuildVpk((string Path, byte[] Data)[] files)
    {
        var vpkPath = Path.Combine(tempDir, "build_" + Path.GetRandomFileName() + ".vpk");
        using (var package = new Package())
        {
            foreach (var (path, data) in files)
            {
                package.AddFile(path, data);
            }

            package.Write(vpkPath);
        }

        var bytes = File.ReadAllBytes(vpkPath);
        File.Delete(vpkPath);
        return bytes;
    }

    // Mirrors ExtractNestedPackageRecursive (GUI) / ProcessVPKEntries recursion (CLI)
    private static void ExtractPackageRecursive(Package package, string outputDir)
    {
        Assert.That(package.Entries, Is.Not.Null);

        foreach (var entry in package.Entries.Values.SelectMany(x => x))
        {
            if (entry.TypeName == "vpk")
            {
                using var nestedStream = GameFileLoader.GetPackageEntryStream(package, entry);
                using var nested = new Package();
                nested.SetFileName(entry.GetFullPath());
                nested.Read(nestedStream);
                ExtractPackageRecursive(nested, outputDir);
                continue;
            }

            package.ReadEntry(entry, out var data);
            var outPath = Path.Combine(outputDir, entry.GetFullPath());
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, data);
        }
    }
}
