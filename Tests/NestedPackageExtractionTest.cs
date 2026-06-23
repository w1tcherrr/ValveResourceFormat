using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;

namespace Tests;

/// <summary>
/// Guards <see cref="NestedPackageEnumerator"/>, the descent mechanism used by both the GUI extraction
/// queue and the CLI to walk vpks nested inside other vpks (including several levels deep) and merge their
/// contents into the same output root as the parent package.
/// </summary>
public class NestedPackageExtractionTest
{
    private string tempDir;
    private readonly List<Package> openedPackages = [];

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vrf_nested_vpk_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var package in openedPackages)
        {
            package.Dispose();
        }

        openedPackages.Clear();

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

        var reported = Enumerate(outerVpkPath);

        // The outer sibling and the nested files are all reported by their own path (merged into one root)
        Assert.That(reported.Keys, Does.Contain("sibling.txt"));

        foreach (var (path, data) in innerFiles)
        {
            Assert.That(reported, Does.ContainKey(path), $"expected nested entry {path}");
            Assert.That(reported[path].Single(), Is.EqualTo(data), $"contents mismatch for {path}");
        }

        // No vpk entry should ever be reported as a leaf; they are descended into
        Assert.That(reported.Keys, Has.None.EndWith(".vpk"));
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

        var reported = Enumerate(outerVpkPath);

        // The leaf survived all 4 levels of descent and merged into the root by its own path
        Assert.That(reported, Does.ContainKey("leaf.txt"));
        Assert.That(reported["leaf.txt"].Single(), Is.EqualTo(leaf));

        // Every intermediate level's marker was reported, proving each level was descended into
        for (var level = 1; level <= Depth - 1; level++)
        {
            Assert.That(reported, Does.ContainKey($"level{level}/marker.txt"),
                $"marker at level {level} should be reported");
        }

        // No vpk entry should be reported as a leaf
        Assert.That(reported.Keys, Has.None.EndWith(".vpk"));
    }

    [Test]
    public void NestedVpksMergeIntoTheSameRoot()
    {
        // Two nested vpks that both contain the same inner path. The enumerator surfaces both so that the
        // consumer merges them into one output root (where the colliding path collapses to a single file).
        var firstInner = BuildVpk([("models/shared.txt", Encoding.UTF8.GetBytes("from first"))]);
        var secondInner = BuildVpk([("models/shared.txt", Encoding.UTF8.GetBytes("from second"))]);

        var outerVpkPath = Path.Combine(tempDir, "outer.vpk");
        using (var outer = new Package())
        {
            outer.AddFile("first.vpk", firstInner);
            outer.AddFile("second.vpk", secondInner);
            outer.Write(outerVpkPath);
        }

        var reported = Enumerate(outerVpkPath);

        // Both nested files are reported under the same root path, ready to be merged by the caller
        Assert.That(reported, Does.ContainKey("models/shared.txt"));

        var contents = reported["models/shared.txt"].Select(Encoding.UTF8.GetString).ToList();
        Assert.That(contents, Has.Count.EqualTo(2));
        Assert.That(contents, Does.Contain("from first"));
        Assert.That(contents, Does.Contain("from second"));
    }

    /// <summary>
    /// Runs the production <see cref="NestedPackageEnumerator"/> over a vpk on disk and returns every reported
    /// leaf entry grouped by its (root-relative) path; a path may appear more than once when nested vpks collide.
    /// </summary>
    private Dictionary<string, List<byte[]>> Enumerate(string vpkPath)
    {
        var results = new Dictionary<string, List<byte[]>>();

        var root = new Package();
        openedPackages.Add(root);
        root.Read(vpkPath);

        NestedPackageEnumerator.EnumerateEntries(
            root,
            root,
            (package, entry, _) =>
            {
                package.ReadEntry(entry, out var data);
                var path = entry.GetFullPath();

                if (!results.TryGetValue(path, out var list))
                {
                    list = [];
                    results[path] = list;
                }

                list.Add(data);
            },
            (parent, vpkEntry, _) =>
            {
                var stream = GameFileLoader.GetPackageEntryStream(parent, vpkEntry);
                var nested = new Package();
                openedPackages.Add(nested);
                nested.SetFileName(vpkEntry.GetFullPath());
                nested.Read(stream);
                return (nested, nested);
            });

        return results;
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
}
