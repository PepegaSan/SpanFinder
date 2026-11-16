using Span.Services.FileOperations;

namespace Span.Tests.Services;

[TestClass]
public class FileCopySafetyTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "SpanCopySafety_" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [TestMethod]
    public async Task CancelledReplacement_PreservesExistingFile()
    {
        var source = Path.Combine(_dir, "source");
        var dest = Path.Combine(_dir, "destination");
        File.WriteAllText(source, "new content");
        File.WriteAllText(dest, "original");

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            FileOperationHelpers.CopyFileWithProgressAsync(source, dest, 4096, null, null, new CancellationToken(true)));

        Assert.AreEqual("original", File.ReadAllText(dest));
        Assert.AreEqual(2, Directory.GetFiles(_dir).Length, "No unfinished staging file should remain.");
    }

    [TestMethod]
    public async Task FailedReplacement_PreservesExistingFile()
    {
        var source = Path.Combine(_dir, "source");
        var dest = Path.Combine(_dir, "destination");
        File.WriteAllText(source, "new content");
        File.WriteAllText(dest, "original");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsExceptionAsync<IOException>(() =>
                FileOperationHelpers.CopyFileWithProgressAsync(source, dest, 4096, null, null, default));

        Assert.AreEqual("original", File.ReadAllText(dest));
        Assert.AreEqual(2, Directory.GetFiles(_dir).Length);
    }

    [TestMethod]
    public async Task CancelledDirectoryCopy_ReportsCancellationInsteadOfSuccess()
    {
        var source = Directory.CreateDirectory(Path.Combine(_dir, "source")).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(_dir, "destination")).FullName;
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "original");

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            FileOperationHelpers.CopyDirectoryWithProgressAsync(source, dest, 4096, null, new Progress<long>(), new CancellationToken(true)));

        Assert.AreEqual("original", File.ReadAllText(Path.Combine(dest, "keep.txt")));
    }
}
