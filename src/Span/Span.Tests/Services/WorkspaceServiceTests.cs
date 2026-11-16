using Span.Models;
using Span.Services;

namespace Span.Tests.Services;

[TestClass]
public class WorkspaceServiceTests
{
    private string _dir = null!;
    private string FilePath => Path.Combine(_dir, "workspaces.json");
    private static WorkspaceDto Workspace(string id) =>
        new(id, id, new(), 0, DateTime.UtcNow, DateTime.UtcNow);

    [TestInitialize]
    public void Setup() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "SpanWorkspace_" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [DataTestMethod]
    [DataRow("{broken")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("null")]
    [DataRow("[null]")]
    public async Task InvalidFile_SavePreservesOriginalAndRecoveryBackup(string content)
    {
        File.WriteAllText(FilePath, content);
        File.WriteAllText(FilePath + ".corrupt", "earlier backup");
        var service = new WorkspaceService(_dir);

        await service.SaveWorkspaceAsync(Workspace("new"));

        Assert.AreEqual(content, File.ReadAllText(FilePath));
        Assert.AreEqual("earlier backup", File.ReadAllText(FilePath + ".corrupt"));
    }

    [TestMethod]
    public async Task LockedFile_DoesNotOverwriteAndRetriesAfterUnlock()
    {
        var writer = new WorkspaceService(_dir);
        await writer.SaveWorkspaceAsync(Workspace("original"));
        var service = new WorkspaceService(_dir);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await service.SaveWorkspaceAsync(Workspace("blocked"));

        await service.SaveWorkspaceAsync(Workspace("added"));

        var actual = await new WorkspaceService(_dir).GetWorkspacesAsync();
        CollectionAssert.AreEquivalent(new[] { "original", "added" }, actual.Select(w => w.Id).ToArray());
    }

    [TestMethod]
    public async Task RepairedFile_IsReadAgainBeforeSaving()
    {
        File.WriteAllText(FilePath, "broken");
        var service = new WorkspaceService(_dir);
        await service.SaveWorkspaceAsync(Workspace("blocked"));
        File.Delete(FilePath);
        await new WorkspaceService(_dir).SaveWorkspaceAsync(Workspace("recovered"));

        await service.SaveWorkspaceAsync(Workspace("added"));

        var actual = await new WorkspaceService(_dir).GetWorkspacesAsync();
        CollectionAssert.AreEquivalent(new[] { "recovered", "added" }, actual.Select(w => w.Id).ToArray());
    }
}
