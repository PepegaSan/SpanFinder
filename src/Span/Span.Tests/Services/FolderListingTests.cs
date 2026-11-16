using Span.Helpers;
using Span.Models;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>
/// FolderListing liest einen Ordner in einem Durchlauf (Einträge + Fingerabdruck) und prüft
/// "hat Inhalt" nur noch auf Anforderung. Die Liste muss dabei genau der früheren entsprechen.
/// </summary>
[TestClass]
public class FolderListingTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SpanListingTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_dir)) return;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(entry, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string MakeFile(string name, string content = "x", FileAttributes extra = 0)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        if (extra != 0) File.SetAttributes(path, File.GetAttributes(path) | extra);
        return path;
    }

    private string MakeDir(string name, bool withChild = false, FileAttributes extra = 0)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        if (withChild) File.WriteAllText(Path.Combine(path, "inside.txt"), "x");
        if (extra != 0) File.SetAttributes(path, File.GetAttributes(path) | extra);
        return path;
    }

    // ── Aufzählung ──────────────────────────────────────────

    [TestMethod]
    public void Enumerate_SplitsFoldersAndFiles_WithMetadata()
    {
        MakeDir("Sub");
        var file = MakeFile("report.txt", "hello");

        var listing = FolderListing.Enumerate(_dir, showHidden: false);

        Assert.IsNull(listing.Error);
        Assert.IsFalse(listing.Cancelled);
        Assert.AreEqual(1, listing.Folders.Count);
        Assert.AreEqual(1, listing.Files.Count);

        var folder = listing.Folders[0];
        Assert.AreEqual("Sub", folder.Name);
        Assert.AreEqual(Path.Combine(_dir, "Sub"), folder.Path);
        Assert.IsTrue(folder.HasChildEntries, "ungeprüft gilt die Annahme 'hat Inhalt'");
        Assert.IsTrue(folder.ChildProbePending);

        var item = listing.Files[0];
        var info = new FileInfo(file);
        Assert.AreEqual("report.txt", item.Name);
        Assert.AreEqual(file, item.Path);
        Assert.AreEqual(5L, item.Size);
        Assert.AreEqual(".txt", item.FileType);
        Assert.AreEqual(info.LastWriteTime, item.DateModified);
        Assert.AreEqual(info.CreationTime, item.DateCreated);
        Assert.AreEqual(info.Attributes, item.Attributes);
    }

    /// <summary>
    /// Die Liste muss Eintrag für Eintrag dem entsprechen, was DirectoryInfo.EnumerateDirectories/
    /// EnumerateFiles (der frühere Ablauf) liefert — gleiche Reihenfolge innerhalb von Ordnern und Dateien.
    /// </summary>
    [TestMethod]
    public void Enumerate_MatchesDirectoryInfoEnumeration()
    {
        MakeDir("alpha", withChild: true);
        MakeDir("Beta");
        MakeDir("sys-only", extra: FileAttributes.System);
        MakeDir(".hidden-dir", extra: FileAttributes.Hidden);
        MakeFile("a.txt", "1");
        MakeFile("B.JPG", "22");
        MakeFile("archive.tar.gz", "333");
        MakeFile("noextension", "4444");
        MakeFile(".gitignore", "55555");
        MakeFile("hidden.dat", "6", FileAttributes.Hidden);
        MakeFile("readonly.txt", "7", FileAttributes.ReadOnly);
        MakeFile("ümlaut ß.txt", "8");
        MakeFile("한글.txt", "9");

        foreach (var showHidden in new[] { false, true })
        {
            var listing = FolderListing.Enumerate(_dir, showHidden);
            Assert.IsNull(listing.Error);

            var info = new DirectoryInfo(_dir);
            var expectedDirs = info.EnumerateDirectories()
                .Where(d => !FileVisibility.ShouldHide(d.Attributes, showHidden)).ToList();
            var expectedFiles = info.EnumerateFiles()
                .Where(f => !FileVisibility.ShouldHide(f.Attributes, showHidden)).ToList();

            Assert.AreEqual(expectedDirs.Count, listing.Folders.Count, $"Ordner (showHidden={showHidden})");
            Assert.AreEqual(expectedFiles.Count, listing.Files.Count, $"Dateien (showHidden={showHidden})");

            for (int i = 0; i < expectedDirs.Count; i++)
            {
                var want = expectedDirs[i];
                var got = listing.Folders[i];
                Assert.AreEqual(want.Name, got.Name);
                Assert.AreEqual(want.FullName, got.Path);
                Assert.AreEqual(want.LastWriteTime, got.DateModified);
                Assert.AreEqual(want.CreationTime, got.DateCreated);
                Assert.AreEqual(want.Attributes, got.Attributes);
                Assert.AreEqual((want.Attributes & FileAttributes.Hidden) != 0, got.IsHidden);
                Assert.AreEqual(
                    (want.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0,
                    got.MaybeHasCustomIcon);
            }

            for (int i = 0; i < expectedFiles.Count; i++)
            {
                var want = expectedFiles[i];
                var got = listing.Files[i];
                Assert.AreEqual(want.Name, got.Name);
                Assert.AreEqual(want.FullName, got.Path);
                Assert.AreEqual(want.Length, got.Size);
                Assert.AreEqual(want.Extension, got.FileType, $"Erweiterung von {want.Name}");
                Assert.AreEqual(want.LastWriteTime, got.DateModified);
                Assert.AreEqual(want.CreationTime, got.DateCreated);
                Assert.AreEqual(want.Attributes, got.Attributes);
                Assert.AreEqual((want.Attributes & FileAttributes.Hidden) != 0, got.IsHidden);
            }
        }
    }

    [TestMethod]
    public void Enumerate_HidesHiddenEntries_ButShowsSystemOnly()
    {
        MakeFile("visible.txt");
        MakeFile("hidden.txt", extra: FileAttributes.Hidden);
        MakeDir("system-only", extra: FileAttributes.System);
        MakeDir("hidden-dir", extra: FileAttributes.Hidden);

        var hiddenOff = FolderListing.Enumerate(_dir, showHidden: false);
        CollectionAssert.AreEqual(new[] { "visible.txt" }, hiddenOff.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "system-only" }, hiddenOff.Folders.Select(f => f.Name).ToArray());

        var hiddenOn = FolderListing.Enumerate(_dir, showHidden: true);
        Assert.AreEqual(2, hiddenOn.Files.Count);
        Assert.AreEqual(2, hiddenOn.Folders.Count);
        Assert.IsTrue(hiddenOn.Files.Single(f => f.Name == "hidden.txt").IsHidden);
    }

    [TestMethod]
    public void Enumerate_SameExtension_SharesOneString()
    {
        MakeFile("one.jpg");
        MakeFile("two.jpg");
        MakeFile("three.png");

        var files = FolderListing.Enumerate(_dir, showHidden: false).Files;
        var jpgs = files.Where(f => f.FileType == ".jpg").ToList();

        Assert.AreEqual(2, jpgs.Count);
        Assert.AreSame(jpgs[0].FileType, jpgs[1].FileType);
    }

    [TestMethod]
    public void Enumerate_MissingFolder_ReportsErrorInsteadOfThrowing()
    {
        var listing = FolderListing.Enumerate(Path.Combine(_dir, "does-not-exist"), showHidden: false);

        Assert.IsInstanceOfType(listing.Error, typeof(DirectoryNotFoundException));
        Assert.AreEqual(0, listing.Folders.Count);
        Assert.AreEqual(0, listing.Files.Count);
    }

    [TestMethod]
    public void Enumerate_CancelledToken_StopsEarly()
    {
        for (int i = 0; i < 600; i++)
            MakeFile($"f{i:D4}.txt");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listing = FolderListing.Enumerate(_dir, showHidden: false, cts.Token);

        Assert.IsTrue(listing.Cancelled);
        Assert.IsTrue(listing.Files.Count < 600);
    }

    [TestMethod]
    public void Enumerate_LongPath_StripsExtendedPrefixFromItemPaths()
    {
        // Über MAX_PATH hinaus: die Aufzählung läuft mit \\?\-Präfix, die Einträge tragen den Pfad ohne.
        var deep = _dir;
        while (deep.Length < 270)
            deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(LongPathHelper.EnsureExtendedPrefix(deep));
        File.WriteAllText(LongPathHelper.EnsureExtendedPrefix(Path.Combine(deep, "file.txt")), "x");

        var listing = FolderListing.Enumerate(LongPathHelper.ForIo(deep), showHidden: false);

        Assert.IsNull(listing.Error);
        Assert.AreEqual(1, listing.Files.Count);
        Assert.AreEqual(Path.Combine(deep, "file.txt"), listing.Files[0].Path);
        Assert.IsFalse(listing.Files[0].Path.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    // ── Fingerabdruck ───────────────────────────────────────

    /// <summary>
    /// Der Fingerabdruck der Liste und der des Abgleichs müssen übereinstimmen, solange sich nichts ändert —
    /// sonst lädt der Spalten-Abgleich den Ordner alle 1,2 Sekunden neu.
    /// </summary>
    [TestMethod]
    public void Stamp_OfListing_EqualsComputeStamp()
    {
        MakeDir("dir");
        MakeDir("sys", extra: FileAttributes.System);
        MakeDir("hid", extra: FileAttributes.Hidden);
        MakeFile("a.txt");
        MakeFile("b.txt", extra: FileAttributes.Hidden);
        MakeFile("c.txt", extra: FileAttributes.Hidden | FileAttributes.System);

        foreach (var showHidden in new[] { false, true })
        {
            var listing = FolderListing.Enumerate(_dir, showHidden);
            var stamp = FolderListing.ComputeStamp(_dir, showHidden);

            Assert.AreEqual(stamp, listing.Stamp, $"showHidden={showHidden}");
            Assert.AreEqual(listing.Folders.Count + listing.Files.Count, listing.Stamp.Count,
                "der Fingerabdruck zählt genau die aufgelisteten Einträge");
        }
    }

    [TestMethod]
    public void Stamp_Changes_WhenEntryAddedRenamedOrWritten()
    {
        var file = MakeFile("a.txt");
        var before = FolderListing.ComputeStamp(_dir, showHidden: false);

        MakeFile("b.txt");
        var afterAdd = FolderListing.ComputeStamp(_dir, showHidden: false);
        Assert.AreNotEqual(before, afterAdd);

        File.Move(Path.Combine(_dir, "b.txt"), Path.Combine(_dir, "c.txt"));
        var afterRename = FolderListing.ComputeStamp(_dir, showHidden: false);
        Assert.AreNotEqual(afterAdd, afterRename);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var afterWrite = FolderListing.ComputeStamp(_dir, showHidden: false);
        Assert.AreNotEqual(afterRename, afterWrite);
    }

    [TestMethod]
    public void Stamp_IgnoresHiddenEntries_WhenHiddenAreNotShown()
    {
        MakeFile("a.txt");
        var before = FolderListing.ComputeStamp(_dir, showHidden: false);

        MakeFile("secret.txt", extra: FileAttributes.Hidden);

        Assert.AreEqual(before, FolderListing.ComputeStamp(_dir, showHidden: false));
        Assert.AreNotEqual(before, FolderListing.ComputeStamp(_dir, showHidden: true));
    }

    // ── "Hat Inhalt" ────────────────────────────────────────

    [TestMethod]
    public void HasAnyEntry_DistinguishesEmptyAndFilledFolders()
    {
        var empty = MakeDir("empty");
        var filled = MakeDir("filled", withChild: true);
        var onlyHidden = MakeDir("only-hidden");
        File.WriteAllText(Path.Combine(onlyHidden, "h.txt"), "x");
        File.SetAttributes(Path.Combine(onlyHidden, "h.txt"), FileAttributes.Hidden);

        Assert.IsFalse(FolderListing.HasAnyEntry(empty));
        Assert.IsTrue(FolderListing.HasAnyEntry(filled));
        Assert.IsTrue(FolderListing.HasAnyEntry(onlyHidden), "wie bisher zählt auch ein versteckter Eintrag");
    }

    [TestMethod]
    public void HasAnyEntry_MatchesFormerCheck()
    {
        MakeDir("empty");
        MakeDir("filled", withChild: true);
        MakeDir("nested");
        Directory.CreateDirectory(Path.Combine(_dir, "nested", "child"));

        foreach (var dir in Directory.GetDirectories(_dir))
        {
            bool former = Directory.EnumerateFileSystemEntries(dir).Any();
            Assert.AreEqual(former, FolderListing.HasAnyEntry(dir), dir);
        }
    }

    [TestMethod]
    public void HasAnyEntry_InaccessibleFolder_CountsAsFilled()
    {
        // Wie bisher: was sich nicht öffnen lässt, behält den Pfeil.
        Assert.IsTrue(FolderListing.HasAnyEntry(Path.Combine(_dir, "gone")));
    }

    [TestMethod]
    public void ProbeChildEntries_ResolvesPendingFolders()
    {
        for (int i = 0; i < 20; i++)
            MakeDir($"d{i:D2}", withChild: i % 2 == 0);

        var folders = FolderListing.Enumerate(_dir, showHidden: false).Folders;
        Assert.IsTrue(folders.All(f => f.ChildProbePending));

        FolderListing.ProbeChildEntries(folders, budgetMs: 10_000);

        foreach (var folder in folders)
        {
            Assert.IsFalse(folder.ChildProbePending, folder.Name);
            int index = int.Parse(folder.Name[1..]);
            Assert.AreEqual(index % 2 == 0, folder.HasChildEntries, folder.Name);
        }
    }

    [TestMethod]
    public void ProbeChildEntries_FewFolders_ResolvesResults()
    {
        MakeDir("empty");
        MakeDir("filled", withChild: true);

        var folders = FolderListing.Enumerate(_dir, showHidden: false).Folders;
        FolderListing.ProbeChildEntries(folders, budgetMs: 10_000);

        Assert.IsFalse(folders.Single(f => f.Name == "empty").HasChildEntries);
        Assert.IsTrue(folders.Single(f => f.Name == "filled").HasChildEntries);
        Assert.IsTrue(folders.All(f => !f.ChildProbePending));
    }

    [TestMethod]
    public void ProbeChildEntries_ExhaustedBudget_LeavesFoldersPending()
    {
        for (int i = 0; i < 12; i++)
            MakeDir($"d{i:D2}");

        var folders = FolderListing.Enumerate(_dir, showHidden: false).Folders;
        FolderListing.ProbeChildEntries(folders, budgetMs: 0);

        // Ohne Zeit wird nichts geprüft: die Annahme "hat Inhalt" bleibt und die Prüfung steht weiter aus.
        Assert.IsTrue(folders.All(f => f.ChildProbePending));
        Assert.IsTrue(folders.All(f => f.HasChildEntries));
    }

    [TestMethod]
    public void ProbeChildEntries_SkipsFoldersWithoutPendingCheck()
    {
        // Einträge aus FTP, Archiven oder der Suche sind nicht "ausstehend" und dürfen nicht angefasst werden
        // (ihr Pfad ist keine Verzeichnisangabe).
        var remote = new FolderItem { Name = "remote", Path = "ftp://host/dir" };
        var list = new List<FolderItem> { remote, remote, remote };

        FolderListing.ProbeChildEntries(list, budgetMs: 10_000);

        Assert.IsTrue(remote.HasChildEntries);
        Assert.IsFalse(remote.ChildProbePending);
    }

    [TestMethod]
    public void Enumerate_PreCancelledSmallDirectory_DoesNotReadEntries()
    {
        MakeDir("child");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listing = FolderListing.Enumerate(_dir, false, cts.Token);
        Assert.IsTrue(listing.Cancelled);
        Assert.AreEqual(0, listing.Folders.Count);
        Assert.IsNull(listing.Error);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(12)]
    public async Task ProbeChildEntries_TimeoutDoesNotPublishLateResults(int count)
    {
        var folders = Enumerable.Range(0, count).Select(i => new FolderItem
        {
            Path = Path.Combine(_dir, $"d{i}"), ChildProbePending = true,
        }).ToList();
        using var release = new ManualResetEventSlim();
        int started = 0, finished = 0;
        try
        {
            var call = Task.Run(() => FolderListing.ProbeChildEntries(folders, 100,
                probe: _ =>
                {
                    Interlocked.Increment(ref started);
                    release.Wait(TimeSpan.FromSeconds(5));
                    Interlocked.Increment(ref finished);
                    return false;
                }));
            // A blocked filesystem call must not hold even a one-folder listing open.
            await call.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(folders.All(f => f.ChildProbePending && f.HasChildEntries));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == Volatile.Read(ref started), 3000));
        }
        // The worker has finished: unreported results must still be pending for the UI queue.
        Assert.IsTrue(folders.All(f => f.ChildProbePending && f.HasChildEntries));
    }
}
