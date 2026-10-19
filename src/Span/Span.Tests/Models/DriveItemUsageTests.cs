using System.Collections.Generic;
using Span.Models;

namespace Span.Tests.Models;

/// <summary>
/// Belegungsbalken der Laufwerke (Seitenleiste/Start): Prozent, Schwelle "fast voll" und Änderungsmeldungen.
/// </summary>
[TestClass]
public class DriveItemUsageTests
{
    private const long GB = 1L << 30;

    [TestMethod]
    public void NoSize_NoUsageBar()
    {
        var d = new DriveItem { Name = "Net" };
        Assert.IsFalse(d.HasUsage);
        Assert.IsFalse(d.IsAlmostFull);
        Assert.AreEqual(0, d.UsagePercent);
    }

    [TestMethod]
    public void AlmostFull_FromNinetyPercent()
    {
        var d = new DriveItem { TotalSize = 100 * GB, AvailableFreeSpace = 11 * GB };
        Assert.IsFalse(d.IsAlmostFull, "89 % belegt");
        d.AvailableFreeSpace = 10 * GB;
        Assert.IsTrue(d.IsAlmostFull, "90 % belegt");
    }

    [TestMethod]
    public void UpdateSpace_RaisesDependentProperties()
    {
        var d = new DriveItem { TotalSize = 100 * GB, AvailableFreeSpace = 50 * GB };
        var changed = new List<string?>();
        d.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        d.UpdateSpace(100 * GB, 5 * GB);

        CollectionAssert.Contains(changed, nameof(DriveItem.UsagePercent));
        CollectionAssert.Contains(changed, nameof(DriveItem.IsAlmostFull));
        CollectionAssert.Contains(changed, "LocalizedSizeDescription");
        Assert.AreEqual(95, d.UsagePercent);
    }

    [TestMethod]
    public void UpdateSpace_SameValues_NoNotification()
    {
        var d = new DriveItem { TotalSize = 100 * GB, AvailableFreeSpace = 50 * GB };
        int count = 0;
        d.PropertyChanged += (_, _) => count++;

        d.UpdateSpace(100 * GB, 50 * GB);

        Assert.AreEqual(0, count);
    }
}
