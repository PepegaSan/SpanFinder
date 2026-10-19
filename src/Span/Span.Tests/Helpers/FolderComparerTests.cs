using System;
using System.IO;
using System.Linq;
using Span.Helpers;

namespace Span.Tests.Helpers;

/// <summary>
/// Ordnervergleich zwischen zwei Bereichen: was hier ausgewählt wird (fehlt dort / dort älter / abweichend)
/// und was nur gezählt wird (nur dort / dort neuer).
/// </summary>
[TestClass]
public class FolderComparerTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private static CompareEntry File(string name, long size = 10, double minutes = 0)
        => new(name, false, size, T0.AddMinutes(minutes));

    private static CompareEntry Dir(string name) => new(name, true, 0, T0);

    [TestMethod]
    public void Identical_NoDifferences()
    {
        var a = new[] { File("a.txt"), Dir("sub") };
        var b = new[] { File("a.txt"), Dir("sub") };
        Assert.IsTrue(FolderComparer.Compare(a, b).IsIdentical);
    }

    [TestMethod]
    public void OnlyHere_IsSelected_OnlyThere_IsCounted()
    {
        var r = FolderComparer.Compare(new[] { File("a.txt"), Dir("new") }, new[] { File("b.txt") });
        CollectionAssert.AreEquivalent(new[] { "a.txt", "new" }, r.OnlyHere);
        CollectionAssert.AreEqual(new[] { "b.txt" }, r.OnlyThere);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "new" }, r.ToSelectHere.ToList());
    }

    [TestMethod]
    public void NewerHere_Selected_NewerThere_Counted()
    {
        var r = FolderComparer.Compare(
            new[] { File("x", minutes: 5), File("y", minutes: 0) },
            new[] { File("x", minutes: 0), File("y", minutes: 5) });
        CollectionAssert.AreEqual(new[] { "x" }, r.NewerHere);
        CollectionAssert.AreEqual(new[] { "y" }, r.NewerThere);
        CollectionAssert.AreEqual(new[] { "x" }, r.ToSelectHere.ToList());
    }

    [TestMethod]
    public void TwoSecondTolerance_SameTimeOnFat()
    {
        var here = new CompareEntry("f", false, 10, T0.AddSeconds(1.9));
        var r = FolderComparer.Compare(new[] { here }, new[] { File("f") });
        Assert.IsTrue(r.IsIdentical);
    }

    [TestMethod]
    public void SameTime_DifferentSize_IsDifferent()
    {
        var r = FolderComparer.Compare(new[] { File("f", size: 1) }, new[] { File("f", size: 2) });
        CollectionAssert.AreEqual(new[] { "f" }, r.Different);
        CollectionAssert.AreEqual(new[] { "f" }, r.ToSelectHere.ToList());
    }

    [TestMethod]
    public void FileVersusFolder_IsDifferent()
    {
        var r = FolderComparer.Compare(new[] { File("x") }, new[] { Dir("x") });
        CollectionAssert.AreEqual(new[] { "x" }, r.Different);
    }

    [TestMethod]
    public void FoldersOnBothSides_NotCompared()
    {
        // Ordnerinhalt wird nicht verglichen — unterschiedliche Zeit zählt nicht
        var r = FolderComparer.Compare(
            new[] { new CompareEntry("sub", true, 0, T0) },
            new[] { new CompareEntry("sub", true, 0, T0.AddDays(3)) });
        Assert.IsTrue(r.IsIdentical);
    }

    [TestMethod]
    public void NamesMatchCaseInsensitive()
    {
        var r = FolderComparer.Compare(new[] { File("Readme.TXT") }, new[] { File("readme.txt") });
        Assert.IsTrue(r.IsIdentical);
    }

    [TestMethod]
    public void ReadFolder_ReadsFilesAndFolders_HiddenFollowsSetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "span-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "12345");
        var hidden = Path.Combine(root, "hidden.txt");
        System.IO.File.WriteAllText(hidden, "x");
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);
        try
        {
            var visible = FolderComparer.ReadFolder(root, showHidden: false);
            CollectionAssert.AreEquivalent(new[] { "a.txt", "sub" }, visible.Select(e => e.Name).ToList());
            Assert.AreEqual(5, visible.Single(e => e.Name == "a.txt").Size);
            Assert.IsTrue(visible.Single(e => e.Name == "sub").IsDirectory);

            var all = FolderComparer.ReadFolder(root, showHidden: true);
            Assert.AreEqual(3, all.Count);
        }
        finally
        {
            System.IO.File.SetAttributes(hidden, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
