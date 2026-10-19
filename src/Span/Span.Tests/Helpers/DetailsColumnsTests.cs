using System;
using System.IO;
using System.Linq;
using Span.Helpers;

namespace Span.Tests.Helpers;

/// <summary>
/// Details 뷰 "열 선택": 컬럼 목록, 특성/길이 표기, 저장 레이아웃(JSON) 왕복.
/// </summary>
[TestClass]
public class DetailsColumnsTests
{
    // ── 컬럼 목록 ──────────────────────────────────────────

    [TestMethod]
    public void All_IdsAreUnique()
    {
        var ids = DetailsColumns.All.Select(c => c.Id).ToList();
        Assert.AreEqual(ids.Count, ids.Distinct().Count());
    }

    [TestMethod]
    public void All_ExplorerDefaultsVisible()
    {
        // 탐색기 기본: 이름(항상) + 수정한 날짜 + 유형 + 크기
        var visible = DetailsColumns.All.Where(c => c.DefaultVisible).Select(c => c.Id).ToArray();
        CollectionAssert.AreEqual(
            new[] { DetailsColumns.DateModified, DetailsColumns.Type, DetailsColumns.Size }, visible);
    }

    [TestMethod]
    public void All_TemplateCellOrderIsFixed()
    {
        // DetailsModeView.xaml의 셀 Grid.Column(= 인덱스 + 2)과 맞아야 한다.
        Assert.AreEqual(0, DetailsColumns.IndexOf(DetailsColumns.Location));
        Assert.AreEqual(1, DetailsColumns.IndexOf(DetailsColumns.DateModified));
        Assert.AreEqual(4, DetailsColumns.IndexOf(DetailsColumns.DateCreated));
        Assert.AreEqual(9, DetailsColumns.IndexOf(DetailsColumns.Title));
        Assert.AreEqual(10, DetailsColumns.IndexOf(DetailsColumns.Git));
        Assert.AreEqual(-1, DetailsColumns.IndexOf("Name"));
    }

    [TestMethod]
    public void IsShellColumn_OnlyPropertyStoreColumns()
    {
        Assert.IsTrue(DetailsColumns.IsShellColumn(DetailsColumns.Dimensions));
        Assert.IsTrue(DetailsColumns.IsShellColumn(DetailsColumns.Duration));
        Assert.IsFalse(DetailsColumns.IsShellColumn(DetailsColumns.DateCreated));
        Assert.IsFalse(DetailsColumns.IsShellColumn("Name"));
    }

    // ── 특성 / 클라우드 / 길이 ─────────────────────────────

    [TestMethod]
    public void FormatAttributes_ExplorerLetters()
    {
        Assert.AreEqual(string.Empty, DetailsColumns.FormatAttributes(0));
        Assert.AreEqual("A", DetailsColumns.FormatAttributes(FileAttributes.Archive));
        Assert.AreEqual("RHSA", DetailsColumns.FormatAttributes(
            FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        Assert.AreEqual("D", DetailsColumns.FormatAttributes(FileAttributes.Directory));
        Assert.AreEqual("AL", DetailsColumns.FormatAttributes(FileAttributes.Archive | FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public void FormatAttributes_NormalOnly_Empty()
    {
        Assert.AreEqual(string.Empty, DetailsColumns.FormatAttributes(FileAttributes.Normal));
    }

    [TestMethod]
    public void IsCloudPlaceholder_RecallFlags()
    {
        Assert.IsFalse(DetailsColumns.IsCloudPlaceholder(FileAttributes.Archive));
        Assert.IsTrue(DetailsColumns.IsCloudPlaceholder(FileAttributes.Offline));
        Assert.IsTrue(DetailsColumns.IsCloudPlaceholder((FileAttributes)0x400000)); // RECALL_ON_DATA_ACCESS
        Assert.IsTrue(DetailsColumns.IsCloudPlaceholder((FileAttributes)0x40000));  // RECALL_ON_OPEN
        Assert.IsFalse(DetailsColumns.IsCloudPlaceholder((FileAttributes)0x80000)); // PINNED (로컬에 있음)
    }

    [TestMethod]
    public void FormatDuration_ExplorerStyle()
    {
        Assert.AreEqual(string.Empty, DetailsColumns.FormatDuration(0));
        Assert.AreEqual("00:03:25", DetailsColumns.FormatDuration(TimeSpan.FromSeconds(205).Ticks));
        Assert.AreEqual("01:00:00", DetailsColumns.FormatDuration(TimeSpan.FromHours(1).Ticks));
        Assert.AreEqual("26:00:01", DetailsColumns.FormatDuration(TimeSpan.FromHours(26).Ticks + TimeSpan.TicksPerSecond));
    }

    // ── 레이아웃 저장 ──────────────────────────────────────

    [TestMethod]
    public void Layout_RoundTrip()
    {
        var layout = new DetailsColumnLayout { SortBy = DetailsColumns.DateCreated, SortAscending = false, GroupBy = "Type" };
        layout.Visible[DetailsColumns.Dimensions] = true;
        layout.Visible[DetailsColumns.Type] = false;
        layout.Widths[DetailsColumns.Dimensions] = 123.5;

        var restored = DetailsColumnLayout.FromJson(layout.ToJson());

        Assert.AreEqual(DetailsColumns.DateCreated, restored.SortBy);
        Assert.IsFalse(restored.SortAscending);
        Assert.AreEqual("Type", restored.GroupBy);
        Assert.IsTrue(restored.IsVisible(DetailsColumns.Find(DetailsColumns.Dimensions)!));
        Assert.IsFalse(restored.IsVisible(DetailsColumns.Find(DetailsColumns.Type)!));
        Assert.AreEqual(123.5, restored.GetWidth(DetailsColumns.Find(DetailsColumns.Dimensions)!));
    }

    [TestMethod]
    public void Layout_MissingValues_UseColumnDefaults()
    {
        var layout = DetailsColumnLayout.FromJson("{}");
        var size = DetailsColumns.Find(DetailsColumns.Size)!;
        var created = DetailsColumns.Find(DetailsColumns.DateCreated)!;

        Assert.IsTrue(layout.IsVisible(size));
        Assert.IsFalse(layout.IsVisible(created));
        Assert.AreEqual(size.DefaultWidth, layout.GetWidth(size));
        Assert.AreEqual("Name", layout.SortBy);
        Assert.AreEqual("None", layout.GroupBy);
    }

    [TestMethod]
    public void Layout_InvalidWidth_FallsBackToDefault()
    {
        var layout = new DetailsColumnLayout();
        var size = DetailsColumns.Find(DetailsColumns.Size)!;
        layout.Widths[size.Id] = 3; // unter MinWidth
        Assert.AreEqual(size.DefaultWidth, layout.GetWidth(size));
        layout.Widths[size.Id] = 99999;
        Assert.AreEqual(size.DefaultWidth, layout.GetWidth(size));
    }

    [TestMethod]
    public void Layout_FromBrokenJson_Defaults()
    {
        Assert.AreEqual("Name", DetailsColumnLayout.FromJson("{not json").SortBy);
        Assert.AreEqual("Name", DetailsColumnLayout.FromJson(null).SortBy);
        Assert.AreEqual("Name", DetailsColumnLayout.FromJson("null").SortBy);
    }
}
