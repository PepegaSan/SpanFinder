using System.Diagnostics;
using Span.Services;

namespace Span.Tests.Services;

[TestClass]
public class FolderSizeIndexTests
{
    private FolderSizeIndex _index = null!;
    private string _tempRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _index = new FolderSizeIndex();
        _tempRoot = Path.Combine(Path.GetTempPath(), "SpanTests_FolderIndex_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private string CreateFileWithSize(string relativePath, int sizeInBytes)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[sizeInBytes]);
        return fullPath;
    }

    private string Sub(string relativePath) => Path.Combine(_tempRoot, relativePath);

    private long Compute(string path) => _index.GetOrCompute(path, CancellationToken.None);

    // ── Depth ─────────────────────────────────────────────

    [TestMethod]
    public void GetOrCompute_CountsFilesDeeperThanEightLevels()
    {
        // The previous implementation stopped at 8 levels and returned too small totals.
        CreateFileWithSize("top.bin", 10);
        CreateFileWithSize(Path.Combine("1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "deep.bin"), 500);

        Assert.AreEqual(510L, Compute(_tempRoot));
    }

    // ── Indexing of subfolders ────────────────────────────

    [TestMethod]
    public void GetOrCompute_IndexesSubfolderTotals()
    {
        CreateFileWithSize("root.bin", 1);
        CreateFileWithSize(Path.Combine("a", "a.bin"), 100);
        CreateFileWithSize(Path.Combine("a", "inner", "i.bin"), 20);
        CreateFileWithSize(Path.Combine("b", "b.bin"), 300);

        Assert.AreEqual(421L, Compute(_tempRoot));
        Assert.AreEqual(120L, _index.TryGet(Sub("a")));
        Assert.AreEqual(20L, _index.TryGet(Sub(Path.Combine("a", "inner"))));
        Assert.AreEqual(300L, _index.TryGet(Sub("b")));
    }

    [TestMethod]
    public void TryGet_IgnoresTrailingSeparator()
    {
        CreateFileWithSize("x.bin", 42);
        Compute(_tempRoot);

        Assert.AreEqual(42L, _index.TryGet(_tempRoot + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void GetOrCompute_KeepsSubfoldersWithinStoreDepthOnly()
    {
        var levels = Enumerable.Range(1, FolderSizeIndex.StoreDepth + 2).Select(i => $"L{i}").ToArray();
        CreateFileWithSize(Path.Combine(levels.Append("f.bin").ToArray()), 7);
        Compute(_tempRoot);

        var deepestKept = Sub(Path.Combine(levels.Take(FolderSizeIndex.StoreDepth).ToArray()));
        var tooDeep = Sub(Path.Combine(levels.Take(FolderSizeIndex.StoreDepth + 1).ToArray()));
        Assert.AreEqual(7L, _index.TryGet(deepestKept));
        Assert.IsNull(_index.TryGet(tooDeep));
    }

    // ── Reuse and change detection ────────────────────────

    [TestMethod]
    public void GetOrCompute_RecomputesSubfolderWhoseWriteTimeChanged()
    {
        CreateFileWithSize(Path.Combine("a", "one.bin"), 100);
        CreateFileWithSize(Path.Combine("b", "two.bin"), 50);
        Assert.AreEqual(150L, Compute(_tempRoot));

        // New file in "a" changes the folder's write time; "b" stays reusable.
        CreateFileWithSize(Path.Combine("a", "three.bin"), 25);
        Directory.SetLastWriteTimeUtc(Sub("a"), DateTime.UtcNow.AddMinutes(5));
        _index.Invalidate(_tempRoot);

        Assert.AreEqual(175L, Compute(_tempRoot));
        Assert.AreEqual(125L, _index.TryGet(Sub("a")));
    }

    // ── Invalidate ────────────────────────────────────────

    [TestMethod]
    public void Invalidate_DropsFolderAndParentsButKeepsSiblings()
    {
        CreateFileWithSize(Path.Combine("a", "inner", "i.bin"), 10);
        CreateFileWithSize(Path.Combine("b", "b.bin"), 20);
        Compute(_tempRoot);

        _index.Invalidate(Sub(Path.Combine("a", "inner")));

        Assert.IsNull(_index.TryGet(Sub(Path.Combine("a", "inner"))));
        Assert.IsNull(_index.TryGet(Sub("a")));
        Assert.IsNull(_index.TryGet(_tempRoot));
        Assert.AreEqual(20L, _index.TryGet(Sub("b")));
    }

    [TestMethod]
    public void Invalidate_ThenCompute_ReturnsFreshTotal()
    {
        var file = CreateFileWithSize(Path.Combine("a", "grow.bin"), 100);
        CreateFileWithSize(Path.Combine("b", "b.bin"), 20);
        Assert.AreEqual(120L, Compute(_tempRoot));

        // Growing a file does not touch the folder's write time; the watcher's invalidation does the job.
        File.WriteAllBytes(file, new byte[400]);
        _index.Invalidate(Sub("a"));

        Assert.AreEqual(420L, Compute(_tempRoot));
    }

    // ── Contents ──────────────────────────────────────────

    [TestMethod]
    public void GetOrCompute_IncludesHiddenFiles()
    {
        var hidden = CreateFileWithSize("hidden.bin", 64);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        CreateFileWithSize("visible.bin", 36);

        Assert.AreEqual(100L, Compute(_tempRoot));
    }

    [TestMethod]
    public void GetOrCompute_DoesNotFollowJunctions()
    {
        CreateFileWithSize(Path.Combine("target", "data.bin"), 1000);
        var link = Sub("link");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Sub("target")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        mklink!.WaitForExit();
        if (!Directory.Exists(link))
            Assert.Inconclusive("Junction could not be created on this file system.");

        Assert.AreEqual(1000L, Compute(_tempRoot), "Junction target must not be counted twice");
    }

    [TestMethod]
    public void GetOrCompute_NonexistentFolder_ReturnsZero()
    {
        Assert.AreEqual(0L, Compute(Sub("missing")));
    }

    [TestMethod]
    public void GetOrCompute_EmptyPath_ReturnsZeroWithoutCaching()
    {
        Assert.AreEqual(0L, Compute(string.Empty));
        Assert.IsNull(_index.TryGet(string.Empty));
    }

    // ── Cancellation / unavailable ────────────────────────

    [TestMethod]
    public void GetOrCompute_CancelledToken_ThrowsAndCachesNothing()
    {
        CreateFileWithSize("x.bin", 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() => _index.GetOrCompute(_tempRoot, cts.Token));
        Assert.IsNull(_index.TryGet(_tempRoot));
    }

    [TestMethod]
    public void MarkUnavailable_StoresMinusOne_AndComputeRetriesIt()
    {
        CreateFileWithSize("x.bin", 5);
        _index.MarkUnavailable(_tempRoot);
        Assert.AreEqual(-1L, _index.TryGet(_tempRoot));

        Assert.AreEqual(5L, Compute(_tempRoot));
    }
}
