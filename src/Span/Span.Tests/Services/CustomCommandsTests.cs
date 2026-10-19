using System.Linq;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>Eigene Befehle: Platzhalter, Anführungszeichen, Einzelstarts, Speichern.</summary>
[TestClass]
public class CustomCommandsTests
{
    private static readonly string[] Files = { @"D:\Videos\Urlaub 2026.mp4", @"D:\Videos\b.mkv" };

    [TestMethod]
    public void BuildArguments_ReplacesAllPlaceholders()
    {
        var args = CustomCommands.BuildArguments("-i {file} -o {folder} {name}.{ext} -- {files}", Files, @"D:\Videos");

        Assert.AreEqual(
            "-i \"D:\\Videos\\Urlaub 2026.mp4\" -o \"D:\\Videos\" Urlaub 2026.mp4 -- \"D:\\Videos\\Urlaub 2026.mp4\" \"D:\\Videos\\b.mkv\"",
            args);
    }

    [TestMethod]
    public void BuildArguments_NoSelection_EmptyPlaceholders()
    {
        Assert.AreEqual("--open", CustomCommands.BuildArguments("--open {files}", new string[0], null));
    }

    [TestMethod]
    public void Quote_TrailingBackslash_IsDoubled()
    {
        // Sonst liest CommandLineToArgvW "D:\" als maskiertes Anführungszeichen
        Assert.AreEqual("\"D:\\\\\"", CustomCommands.Quote(@"D:\"));
        Assert.AreEqual("\"D:\\a b\"", CustomCommands.Quote(@"D:\a b"));
    }

    [TestMethod]
    public void PlanInvocations_PerFile_OnePerFile()
    {
        var cmd = new CustomCommand { Arguments = "-i {file}", PerFile = true };
        var plan = CustomCommands.PlanInvocations(cmd, Files, null);

        CollectionAssert.AreEqual(new[] { "-i \"D:\\Videos\\Urlaub 2026.mp4\"", "-i \"D:\\Videos\\b.mkv\"" }, plan);
    }

    [TestMethod]
    public void PlanInvocations_AllAtOnce_SingleCall()
    {
        var cmd = new CustomCommand { Arguments = "{files}" };
        Assert.AreEqual(1, CustomCommands.PlanInvocations(cmd, Files, null).Count);
    }

    [TestMethod]
    public void NeedsSelection_OnlyWithFilePlaceholders()
    {
        Assert.IsTrue(CustomCommands.NeedsSelection(new CustomCommand { Arguments = "x {FILE}" }));
        Assert.IsTrue(CustomCommands.NeedsSelection(new CustomCommand { Arguments = "{name}.txt" }));
        Assert.IsFalse(CustomCommands.NeedsSelection(new CustomCommand { Arguments = "{folder}" }));
        Assert.IsFalse(CustomCommands.NeedsSelection(new CustomCommand { Arguments = "" }));
    }

    [TestMethod]
    public void Json_RoundTrip_AndCorruptInput()
    {
        var list = new[]
        {
            new CustomCommand { Name = "MP4", Program = @"C:\ffmpeg\ffmpeg.exe", Arguments = "-i {file} {name}.mp4", PerFile = true, Icon = "" },
        };
        var loaded = CustomCommands.Load(CustomCommands.ToJson(list));

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("MP4", loaded[0].Name);
        Assert.IsTrue(loaded[0].PerFile);
        Assert.AreEqual("", loaded[0].Icon);
        Assert.AreEqual(list[0].Id, loaded[0].Id);

        Assert.AreEqual(0, CustomCommands.Load("{kaputt").Count);
        Assert.AreEqual(0, CustomCommands.Load(null).Count);
    }
}
