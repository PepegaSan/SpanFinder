using Moq;
using Span.Services;
using Span.Services.FileOperations;

namespace Span.Tests.Integration;

[TestClass]
public class RemoteTransferSafetyTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "SpanTransferSafety_" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledDirectoryUpload_NeverDeletesLocalSource(bool empty)
    {
        var source = Directory.CreateDirectory(Path.Combine(_dir, "source")).FullName;
        if (!empty) File.WriteAllText(Path.Combine(source, "keep.txt"), "original");
        using var cts = new CancellationTokenSource();
        var remote = new Mock<IFileSystemProvider>(MockBehavior.Strict);
        remote.Setup(p => p.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel()).Returns(Task.CompletedTask);
        var router = new FileSystemRouter();
        router.RegisterConnection("sftp://test", remote.Object);

        var result = await new MoveFileOperation(new() { source }, "sftp://test/target", router)
            .ExecuteAsync(cancellationToken: cts.Token);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(Directory.Exists(source));
        if (!empty) Assert.AreEqual("original", File.ReadAllText(Path.Combine(source, "keep.txt")));
    }

    [TestMethod]
    public async Task CancelledFileUpload_NeverDeletesLocalSource()
    {
        var source = Path.Combine(_dir, "keep.txt");
        File.WriteAllText(source, "original");
        using var cts = new CancellationTokenSource();
        var remote = new Mock<IFileSystemProvider>(MockBehavior.Strict);
        remote.Setup(p => p.WriteAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel()).Returns(Task.CompletedTask);
        var router = new FileSystemRouter();
        router.RegisterConnection("sftp://test", remote.Object);

        var result = await new MoveFileOperation(new() { source }, "sftp://test/target", router)
            .ExecuteAsync(cancellationToken: cts.Token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("original", File.ReadAllText(source));
    }

    [DataTestMethod]
    [DataRow("sftp://test/folder")]
    [DataRow("sftp://TEST/folder")]
    public async Task MoveToSameRemoteFolder_DoesNotTouchSource(string destination)
    {
        var remote = new Mock<IFileSystemProvider>(MockBehavior.Strict);
        var router = new FileSystemRouter();
        router.RegisterConnection("sftp://test", remote.Object);
        var result = await new MoveFileOperation(new() { "sftp://test/folder/keep.txt" }, destination, router).ExecuteAsync();
        Assert.AreEqual(0, result.AffectedPaths.Count);
        remote.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteFolderIntoItself_IsRejectedBeforeCreatingDestination(bool move)
    {
        var remote = new Mock<IFileSystemProvider>(MockBehavior.Strict);
        remote.Setup(p => p.IsDirectoryAsync("/source", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var router = new FileSystemRouter();
        router.RegisterConnection("sftp://test", remote.Object);
        IFileOperation operation = move
            ? new MoveFileOperation(new() { "sftp://test/source" }, "sftp://test/source/nested", router)
            : new CopyFileOperation(new() { "sftp://test/source" }, "sftp://test/source/nested", router);

        var result = await operation.ExecuteAsync();

        Assert.IsFalse(result.Success);
        remote.Verify(p => p.IsDirectoryAsync("/source", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        remote.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("sftp://test/source", "sftp://test/source/child", true)]
    [DataRow("sftp://test/source", "sftp://test/source-other/child", false)]
    [DataRow("sftp://test/source", "sftp://different/source/child", false)]
    [DataRow("sftp://test/Source", "sftp://test/source/child", false)]
    public void RemoteDescendantCheck_RespectsHostCaseAndPathBoundaries(string source, string target, bool expected)
        => Assert.AreEqual(expected, FileOperationHelpers.IsRemoteDescendantPath(source, target));

    [TestMethod]
    public async Task FailedDownload_PreservesExistingDestination()
    {
        var dest = Path.Combine(_dir, "keep.txt");
        File.WriteAllText(dest, "original");
        var remote = new Mock<IFileSystemProvider>();
        remote.Setup(p => p.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Connection lost"));

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            CopyFileOperation.DownloadRemoteToLocalAsync(remote.Object, "/keep.txt", dest, null, default));

        Assert.AreEqual("original", File.ReadAllText(dest));
        Assert.AreEqual(1, Directory.GetFiles(_dir).Length, "Temporary download must be removed.");
    }

    [TestMethod]
    public async Task SuccessfulDownload_ReplacesExistingDestination()
    {
        var dest = Path.Combine(_dir, "keep.txt");
        File.WriteAllText(dest, "original");
        var remote = new Mock<IFileSystemProvider>();
        remote.Setup(p => p.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("replacement"u8.ToArray()));
        await CopyFileOperation.DownloadRemoteToLocalAsync(remote.Object, "/keep.txt", dest, null, default);
        Assert.AreEqual("replacement", File.ReadAllText(dest));
        Assert.AreEqual(1, Directory.GetFiles(_dir).Length);
    }

    [DataTestMethod]
    [DataRow("sftp://server-other/data")]
    [DataRow("sftp://server:2222/data")]
    public void ConnectionLookup_DoesNotMatchDifferentHostOrPort(string path)
    {
        var router = new FileSystemRouter();
        var remote = new Mock<IFileSystemProvider>();
        router.RegisterConnection("sftp://server", remote.Object);
        Assert.IsNull(router.GetConnectionForPath(path));
        Assert.AreSame(remote.Object, router.GetConnectionForPath("sftp://server/data"));
    }
}
