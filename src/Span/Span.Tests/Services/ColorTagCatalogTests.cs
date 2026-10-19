using System.Linq;
using Span.Helpers;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>
/// Farb-Tags: Standard-Tags (Ids = frühere Enum-Namen), eigene Namen/Farben, Speichern, tag:-Abgleich.
/// </summary>
[TestClass]
public class ColorTagCatalogTests
{
    private static string Localize(string key) => key switch
    {
        "ColorTag_Red" => "Rot",
        "ColorTag_Green" => "Grün",
        "ColorTag_Blue" => "Blau",
        _ => key,
    };

    [TestMethod]
    public void Default_HasSevenBuiltIns_WithLocalizedNames()
    {
        var catalog = new ColorTagCatalog(null, Localize);

        CollectionAssert.AreEqual(
            new[] { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray" },
            catalog.Tags.Select(t => t.Id).ToArray());
        Assert.AreEqual("Rot", catalog.DisplayName("red"));
        Assert.AreEqual("#E81123", catalog.Find("Red")!.Color);
    }

    [TestMethod]
    public void Rename_Recolor_Add_RoundTripThroughJson()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        catalog.Update("Green", "Fertig", "00ff00");
        var custom = catalog.Add("Kunde A", "#123456");

        var reloaded = new ColorTagCatalog(catalog.ToJson(), Localize);

        Assert.AreEqual("Fertig", reloaded.DisplayName("Green"));
        Assert.AreEqual("#00FF00", reloaded.Find("Green")!.Color);
        Assert.AreEqual("Kunde A", reloaded.DisplayName(custom.Id));
        Assert.AreEqual("#123456", reloaded.Find(custom.Id)!.Color);
        Assert.AreEqual(8, reloaded.Tags.Count);
    }

    [TestMethod]
    public void Update_DefaultNameOrEmpty_KeepsBuiltInLocalized()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        catalog.Update("Red", "Wichtig", "#E81123");
        catalog.Update("Red", "", "#E81123");
        Assert.IsNull(catalog.Find("Red")!.Name);

        catalog.Update("Blue", "Blau", "#0078D4"); // Standardname nicht festschreiben (Sprachwechsel)
        Assert.IsNull(catalog.Find("Blue")!.Name);
    }

    [TestMethod]
    public void Add_NewIdsAreUnique_EvenAfterRemove()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        var a = catalog.Add("A", "#111111");
        var b = catalog.Add("B", "#222222");
        catalog.Remove(a.Id);
        var c = catalog.Add("C", "#333333");

        Assert.AreNotEqual(a.Id, c.Id, "Id eines gelöschten Tags wird nicht wiederverwendet");
        Assert.AreNotEqual(b.Id, c.Id);
        Assert.IsNull(catalog.Find(a.Id));
    }

    [TestMethod]
    public void RemovedTag_IsUnknown_AndResetRestoresBuiltIns()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        catalog.Update("Gray", "Archiv", "#000000");
        catalog.Remove("Red");
        Assert.IsNull(catalog.Find("Red"));
        Assert.IsNull(catalog.DisplayName("Red"));

        var custom = catalog.Add("Eigen", "#ABCDEF");
        catalog.ResetBuiltIns();

        Assert.AreEqual("Red", catalog.Tags[0].Id);
        Assert.IsNull(catalog.Find("Gray")!.Name);
        Assert.AreEqual("#605E5C", catalog.Find("Gray")!.Color);
        Assert.IsNotNull(catalog.Find(custom.Id), "eigene Tags bleiben");
    }

    [TestMethod]
    public void EmptyList_StaysEmpty_CorruptJson_FallsBackToDefaults()
    {
        Assert.AreEqual(0, new ColorTagCatalog("[]", Localize).Tags.Count);
        Assert.AreEqual(7, new ColorTagCatalog("{kaputt", Localize).Tags.Count);
    }

    [TestMethod]
    public void Move_ChangesOrder_AndRaisesChanged()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        int changes = 0;
        catalog.Changed += () => changes++;

        catalog.Move("Gray", 0);

        Assert.AreEqual("Gray", catalog.Tags[0].Id);
        Assert.AreEqual(0, catalog.IndexOf("gray"));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void Matches_ByNamePrefix_Id_OrStar()
    {
        var catalog = new ColorTagCatalog(null, Localize);
        var custom = catalog.Add("Kunde Müller", "#123456");

        Assert.IsTrue(catalog.Matches("rot", "Red"));
        Assert.IsTrue(catalog.Matches("RED", "Red"), "Id passt auch in der deutschen Oberfläche");
        Assert.IsTrue(catalog.Matches("kunde", custom.Id));
        Assert.IsTrue(catalog.Matches("Kunde Müller", custom.Id));
        Assert.IsTrue(catalog.Matches("*", "Blue"));
        Assert.IsFalse(catalog.Matches("*", null));
        Assert.IsFalse(catalog.Matches("grün", "Red"));
        Assert.IsFalse(catalog.Matches("rot", "GeloeschterTag"));
    }

    [TestMethod]
    public void NormalizeColor_AcceptsVariants_RejectsGarbage()
    {
        Assert.AreEqual("#0A0B0C", ColorTagCatalog.NormalizeColor("0a0b0c"));
        Assert.AreEqual("#0A0B0C", ColorTagCatalog.NormalizeColor("#FF0A0B0C"));
        Assert.IsNull(ColorTagCatalog.NormalizeColor("grün"));
        Assert.IsNull(ColorTagCatalog.NormalizeColor("#12345"));
    }
}

/// <summary>tag: in der Suchsyntax.</summary>
[TestClass]
public class SearchQueryTagTests
{
    [TestMethod]
    public void Tag_ParsesValue_AndCombinesWithOtherFilters()
    {
        var query = SearchQueryParser.Parse("tag:rot kind:image urlaub");

        Assert.AreEqual("rot", query.TagFilter);
        Assert.AreEqual(Span.Models.FileKind.Image, query.KindFilter);
        Assert.AreEqual("urlaub", query.NameFilter);
        Assert.IsFalse(query.IsEmpty);
    }

    [TestMethod]
    public void Tag_Only_IsNotEmpty()
    {
        Assert.IsFalse(SearchQueryParser.Parse("tag:*").IsEmpty);
        Assert.AreEqual("*", SearchQueryParser.Parse("TAG:*").TagFilter);
    }

    [TestMethod]
    public void Tag_QuotedNameWithSpaces()
    {
        var query = SearchQueryParser.Parse("tag:\"Kunde Müller\" bericht");

        Assert.AreEqual("Kunde Müller", query.TagFilter);
        Assert.AreEqual("bericht", query.NameFilter);
    }

    [TestMethod]
    public void Tag_EmptyValue_IsPlainText()
    {
        var query = SearchQueryParser.Parse("tag:");
        Assert.IsNull(query.TagFilter);
        Assert.AreEqual("tag:", query.NameFilter);
    }

    [TestMethod]
    public void Apostrophe_InPlainName_Unchanged()
    {
        Assert.AreEqual("don't", SearchQueryParser.Parse("don't").NameFilter);
    }
}
