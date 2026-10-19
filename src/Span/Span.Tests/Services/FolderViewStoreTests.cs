using System;
using System.IO;
using System.Linq;
using Span.Helpers;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>
/// Ansicht pro Ordner: Ordnertyp-Erkennung, Vorlagen und gemerkte Sortierung/Gruppierung (Vorrang vor Vorlage).
/// </summary>
[TestClass]
public class FolderViewStoreTests
{
    private string _dir = null!;
    private string File => Path.Combine(_dir, "folder-views.json");

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SpanTests_FolderViews_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── Ordnertyp ─────────────────────────────────────────

    [TestMethod]
    public void DetectKind_MostlyImages_Pictures()
    {
        var kind = FolderViewTemplates.DetectKind(false, new[] { "a.jpg", "b.PNG", "c.jpeg", "notes.txt" });
        Assert.AreEqual(FolderKind.Pictures, kind);
    }

    [TestMethod]
    public void DetectKind_TooFewMedia_General()
    {
        Assert.AreEqual(FolderKind.General, FolderViewTemplates.DetectKind(false, new[] { "a.jpg", "b.jpg" }));
        Assert.AreEqual(FolderKind.General, FolderViewTemplates.DetectKind(false, Array.Empty<string>()));
    }

    [TestMethod]
    public void DetectKind_MixedBelowShare_General()
    {
        var kind = FolderViewTemplates.DetectKind(false, new[] { "a.mp3", "b.mp3", "c.mp3", "d.txt", "e.txt", "f.txt" });
        Assert.AreEqual(FolderKind.General, kind, "50 % Audio reicht nicht");
    }

    [TestMethod]
    public void DetectKind_MusicAndVideos()
    {
        Assert.AreEqual(FolderKind.Music, FolderViewTemplates.DetectKind(false, new[] { "1.mp3", "2.flac", "3.m4a" }));
        Assert.AreEqual(FolderKind.Videos, FolderViewTemplates.DetectKind(false, new[] { "1.mp4", "2.mkv", "3.mov" }));
    }

    [TestMethod]
    public void DetectKind_DownloadsWins()
    {
        Assert.AreEqual(FolderKind.Downloads, FolderViewTemplates.DetectKind(true, new[] { "1.jpg", "2.jpg", "3.jpg" }));
    }

    [TestMethod]
    public void Templates()
    {
        var dl = FolderViewTemplates.For(FolderKind.Downloads);
        Assert.AreEqual("DateModified", dl.SortBy);
        Assert.AreEqual(false, dl.SortAscending);
        Assert.AreEqual("DateModified", dl.GroupBy);
        Assert.IsTrue(dl.MixFoldersAndFiles);

        CollectionAssert.Contains(FolderViewTemplates.For(FolderKind.Music).ExtraDetailsColumns.ToArray(), DetailsColumns.Duration);
        Assert.IsNull(FolderViewTemplates.For(FolderKind.General).SortBy);
    }

    // ── Gemerkte Einstellungen ────────────────────────────

    [TestMethod]
    public void Resolve_NoRecord_UsesTemplate()
    {
        var store = new FolderViewStore(File);
        var r = store.Resolve(@"C:\Bilder", FolderKind.Pictures);
        Assert.AreEqual("DateModified", r.SortBy);
        Assert.AreEqual(false, r.SortAscending);
    }

    [TestMethod]
    public void Resolve_RecordOverridesTemplate_KeepsOtherTemplateValues()
    {
        var store = new FolderViewStore(File);
        store.SetSort(@"C:\Users\x\Downloads", "Name", true);

        var r = store.Resolve(@"c:\users\x\downloads\", FolderKind.Downloads);

        Assert.AreEqual("Name", r.SortBy);
        Assert.AreEqual(true, r.SortAscending);
        Assert.AreEqual("DateModified", r.GroupBy, "Gruppierung kommt weiter aus der Vorlage");
        Assert.IsTrue(r.MixFoldersAndFiles);
    }

    [TestMethod]
    public void Resolve_GroupNone_OverridesTemplateGroup()
    {
        var store = new FolderViewStore(File);
        store.SetGroup(@"C:\Downloads", "None");
        Assert.AreEqual("None", store.Resolve(@"C:\Downloads", FolderKind.Downloads).GroupBy);
    }

    [TestMethod]
    public void Flush_ThenReload_PersistsRecords()
    {
        var store = new FolderViewStore(File);
        store.SetSort(@"D:\Projekte", "Size", false);
        store.SetGroup(@"D:\Projekte", "Type");
        store.Flush();

        var reloaded = new FolderViewStore(File).Get(@"D:\Projekte");

        Assert.IsNotNull(reloaded);
        Assert.AreEqual("Size", reloaded.SortBy);
        Assert.AreEqual(false, reloaded.SortAscending);
        Assert.AreEqual("Type", reloaded.GroupBy);
    }

    [TestMethod]
    public void BrokenFile_StartsEmpty()
    {
        System.IO.File.WriteAllText(File, "{kaputt");
        var store = new FolderViewStore(File);
        Assert.IsNull(store.Get(@"C:\x"));
        Assert.AreEqual(FolderViewResolution.None, store.Resolve(@"C:\x", FolderKind.General));
    }

    [TestMethod]
    public void NormalizeKey_TrimsTrailingSeparator_KeepsDriveRoot()
    {
        Assert.AreEqual(@"C:\a", FolderViewStore.NormalizeKey(@"C:\a\"));
        Assert.AreEqual(@"C:\", FolderViewStore.NormalizeKey(@"C:\"));
    }
}
