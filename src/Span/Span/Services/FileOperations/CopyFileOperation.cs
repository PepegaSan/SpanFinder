using System.Threading;
using Span.Models;
using static Span.Services.LocalizationService;

namespace Span.Services.FileOperations;

/// <summary>
/// Represents a file or directory copy operation with progress reporting and pause support.
/// Supports local ↔ remote (FTP/SFTP) transfers via FileSystemRouter stream-based copying.
///
/// Lokal → lokal läuft über die Shell-Kopier-Engine (<see cref="ShellTransferWithProgress"/>, wie der Explorer):
/// "Ersetzen" führt Ordner zusammen statt den Zielordner zu löschen, Attribute und Zeitstempel bleiben erhalten,
/// überschriebene Dateien landen im Papierkorb. Die eigene Stream-Kopie bleibt für Remote-Pfade und als Fallback.
/// </summary>
public class CopyFileOperation : IFileOperation, IPausableOperation
{
    private readonly List<string> _sourcePaths;
    private readonly string _destinationDirectory;
    private readonly FileSystemRouter? _router;
    private readonly List<string> _copiedPaths = new();
    private bool _hasRemotePaths;
    private ConflictResolution _conflictResolution = ConflictResolution.Prompt;
    private bool _applyToAll = false;
    private ManualResetEventSlim? _pauseEvent;

    // Shell-Engine-Pfad: Buchführung für das Rückgängigmachen zusammengeführter Ordner. Dort darf Undo nur die
    // tatsächlich hinzugekommenen Elemente entfernen — nicht den ganzen Zielordner wie bei einer frischen Kopie.
    private readonly HashSet<string> _mergedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mergeUndoUnsafe = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preExistingUnderMerged = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _nestedCreatedPaths = new();
    private const int MergeSnapshotLimit = 100_000;

    /// <summary>Für die Sitzung auf die eigene Kopierschleife zurückfallen, wenn IFileOperation nicht erzeugbar ist.</summary>
    private static volatile bool s_shellEngineUnavailable;

    private sealed record ShellPlanEntry(int Index, string SourcePath, string DestinationPath, string? NewName, bool MergeIntoExisting);
    private enum ShellRun { Unavailable, Completed, Cancelled }

    public CopyFileOperation(List<string> sourcePaths, string destinationDirectory)
        : this(sourcePaths, destinationDirectory, null)
    {
    }

    public CopyFileOperation(List<string> sourcePaths, string destinationDirectory, FileSystemRouter? router)
    {
        _sourcePaths = sourcePaths ?? throw new ArgumentNullException(nameof(sourcePaths));
        _destinationDirectory = destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory));
        _router = router;
        _hasRemotePaths = _sourcePaths.Any(FileSystemRouter.IsRemotePath)
                          || FileSystemRouter.IsRemotePath(_destinationDirectory);
    }

    /// <summary>Gets the source paths for this copy operation.</summary>
    public IReadOnlyList<string> SourcePaths => _sourcePaths;

    /// <summary>Gets the destination directory for this copy operation.</summary>
    public string DestinationDirectory => _destinationDirectory;

    /// <inheritdoc/>
    public string Description => _sourcePaths.Count == 1
        ? string.Format(L("Op_CopySingle"), FileOperationHelpers.GetFileName(_sourcePaths[0]), FileOperationHelpers.GetFileName(_destinationDirectory))
        : string.Format(L("Op_CopyMultiple"), _sourcePaths.Count, FileOperationHelpers.GetFileName(_destinationDirectory));

    /// <inheritdoc/>
    public bool CanUndo => !_hasRemotePaths;

    /// <inheritdoc/>
    public void SetPauseEvent(ManualResetEventSlim pauseEvent)
    {
        _pauseEvent = pauseEvent;
    }

    private void WaitIfPaused(CancellationToken cancellationToken)
        => FileOperationHelpers.WaitIfPaused(_pauseEvent, cancellationToken);

    /// <inheritdoc/>
    public async Task<OperationResult> ExecuteAsync(
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new OperationResult { Success = true };
        long totalBytes = 0;
        long processedBytes = 0;
        var startTime = DateTime.Now;
        var errors = new List<string>();

        try
        {
            // 전체 바이트 계산 (각 항목 크기를 캐싱하여 이중 계산 방지)
            var itemSizes = new long[_sourcePaths.Count];
            for (int idx = 0; idx < _sourcePaths.Count; idx++)
            {
                itemSizes[idx] = await GetFileOrDirectorySizeAsync(_sourcePaths[idx], cancellationToken);
                totalBytes += itemSizes[idx];
            }

            // ── Lokal → lokal über die Shell-Kopier-Engine (Explorer-Verhalten: Ordner zusammenführen, Attribute
            //    erhalten, Papierkorb für Überschriebenes). Remote-Elemente und der Fall "Engine nicht verfügbar"
            //    laufen weiter durch die Schleife darunter.
            var handledByShell = new HashSet<int>();
            if (!FileSystemRouter.IsRemotePath(_destinationDirectory) && !s_shellEngineUnavailable
                && Directory.Exists(_destinationDirectory))
            {
                var plan = PlanShellBatch(errors, handledByShell);
                if (plan.Count > 0)
                {
                    var run = await RunShellCopyAsync(plan, result, errors, progress, totalBytes, startTime, cancellationToken);
                    if (run == ShellRun.Unavailable)
                    {
                        foreach (var entry in plan) handledByShell.Remove(entry.Index); // alte Schleife übernimmt
                    }
                    else if (run == ShellRun.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        result.Success = false;
                        result.ErrorMessage = L("Op_Cancelled_Copy");
                        return result;
                    }
                }
            }

            for (int i = 0; i < _sourcePaths.Count; i++)
            {
                if (handledByShell.Contains(i)) continue;

                WaitIfPaused(cancellationToken);
                if (cancellationToken.IsCancellationRequested) break;

                var sourcePath = _sourcePaths[i];
                bool srcIsRemote = FileSystemRouter.IsRemotePath(sourcePath);
                bool destIsRemote = FileSystemRouter.IsRemotePath(_destinationDirectory);

                var fileName = FileOperationHelpers.GetFileName(sourcePath);
                var destPath = FileOperationHelpers.CombinePath(_destinationDirectory, fileName, destIsRemote);

                try
                {
                    if (srcIsRemote && destIsRemote && FileOperationHelpers.IsSameRemotePath(sourcePath, destPath))
                        throw new IOException("Source and destination are the same remote path.");
                    if (srcIsRemote || destIsRemote)
                    {
                        // ── Remote copy path ──
                        if (_router == null)
                        {
                            errors.Add(string.Format(L("Op_NoRemoteRouter"), sourcePath));
                            continue;
                        }

                        // Conflict check: only for local destinations
                        if (!destIsRemote && (File.Exists(destPath) || Directory.Exists(destPath)))
                        {
                            if (!_applyToAll)
                            {
                                destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                            }
                            else
                            {
                                switch (_conflictResolution)
                                {
                                    case ConflictResolution.Skip:
                                        continue;
                                    case ConflictResolution.Replace:
                                        // Dateien ersetzt die Kopie selbst; Ordner werden zusammengeführt statt wie früher
                                        // komplett gelöscht (alle nicht betroffenen Dateien im Zielordner waren weg).
                                        if (Directory.Exists(destPath)) RememberPreExisting(destPath);
                                        break;
                                    case ConflictResolution.KeepBoth:
                                        destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                                        break;
                                }
                            }
                        }

                        // Check if source is a directory (remote)
                        bool srcIsDir = false;
                        if (srcIsRemote)
                        {
                            var srcProvider = FileOperationHelpers.GetRemoteProvider(_router, sourcePath);
                            if (srcProvider != null)
                            {
                                var remotePath = FileSystemRouter.ExtractRemotePath(sourcePath);
                                srcIsDir = await srcProvider.IsDirectoryAsync(remotePath, cancellationToken);
                            }
                        }
                        else
                        {
                            srcIsDir = Directory.Exists(sourcePath);
                        }

                        long localProcessed = processedBytes;
                        int fileIndex = i;

                        if (srcIsDir)
                        {
                            if (srcIsRemote && destIsRemote && FileOperationHelpers.IsRemoteDescendantPath(sourcePath, destPath))
                                throw new IOException("Cannot copy a remote folder into itself.");
                            await CopyDirectoryViaStreamAsync(
                                sourcePath, destPath, srcIsRemote, destIsRemote,
                                new Progress<long>(bytes =>
                                {
                                    FileOperationHelpers.ReportProgress(progress, fileName, fileIndex, _sourcePaths.Count, localProcessed + bytes, totalBytes, startTime);
                                }),
                                cancellationToken);
                        }
                        else
                        {
                            await CopyFileViaStreamAsync(
                                sourcePath, destPath, srcIsRemote, destIsRemote,
                                new Progress<long>(bytes =>
                                {
                                    FileOperationHelpers.ReportProgress(progress, fileName, fileIndex, _sourcePaths.Count, localProcessed + bytes, totalBytes, startTime);
                                }),
                                cancellationToken);
                        }

                        // 캐싱된 크기 사용 (이중 계산 방지)
                        processedBytes += itemSizes[i];
                    }
                    else
                    {
                        // ── Local copy path (unchanged) ──

                        // Handle conflict
                        if (File.Exists(destPath) || Directory.Exists(destPath))
                        {
                            if (!_applyToAll)
                            {
                                destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                            }
                            else
                            {
                                switch (_conflictResolution)
                                {
                                    case ConflictResolution.Skip:
                                        continue;
                                    case ConflictResolution.Replace:
                                        // Dateien ersetzt die Kopie selbst; Ordner werden zusammengeführt statt wie früher
                                        // komplett gelöscht (alle nicht betroffenen Dateien im Zielordner waren weg).
                                        if (Directory.Exists(destPath)) RememberPreExisting(destPath);
                                        break;
                                    case ConflictResolution.KeepBoth:
                                        destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                                        break;
                                }
                            }
                        }

                        // Copy file or directory
                        if (File.Exists(sourcePath))
                        {
                            var fileSize = new FileInfo(sourcePath).Length;
                            long localProcessed = processedBytes;
                            int fileIndex = i;
                            await FileOperationHelpers.CopyFileWithProgressAsync(
                                sourcePath,
                                destPath,
                                FileOperationHelpers.DefaultBufferSize,
                                _pauseEvent,
                                new Progress<long>(bytes =>
                                {
                                    FileOperationHelpers.ReportProgress(progress, fileName, fileIndex, _sourcePaths.Count, localProcessed + bytes, totalBytes, startTime);
                                }),
                                cancellationToken);

                            processedBytes += fileSize;
                        }
                        else if (Directory.Exists(sourcePath))
                        {
                            // 캐싱된 크기 사용 (이중 계산 방지)
                            var dirSize = itemSizes[i];
                            long localProcessed = processedBytes;
                            int fileIndex = i;
                            await FileOperationHelpers.CopyDirectoryWithProgressAsync(
                                sourcePath,
                                destPath,
                                FileOperationHelpers.DefaultBufferSize,
                                _pauseEvent,
                                new Progress<long>(bytes =>
                                {
                                    FileOperationHelpers.ReportProgress(progress, fileName, fileIndex, _sourcePaths.Count, localProcessed + bytes, totalBytes, startTime);
                                }),
                                cancellationToken);

                            // The destination may contain pre-existing files from a merge.
                            cancellationToken.ThrowIfCancellationRequested();

                            processedBytes += dirSize;
                        }
                        else
                        {
                            errors.Add(string.Format(L("Op_PathNotFound"), sourcePath));
                            continue;
                        }
                    }

                    _copiedPaths.Add(destPath);
                    result.AffectedPaths.Add(destPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PathTooLongException)
                {
                    errors.Add(string.Format(L("Op_PathTooLong"), fileName));
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(L("Op_FailedTo_Copy"), fileName, ex.Message));
                }
            }

            // 취소 시 예외 없이 즉시 종료
            if (cancellationToken.IsCancellationRequested)
            {
                result.Success = false;
                result.ErrorMessage = L("Op_Cancelled_Copy");
                return result;
            }

            FileOperationHelpers.FinalizeResultWithErrors(result, errors, "Op_SomeNotCopied");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = L("Op_Cancelled_Copy");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = string.Format(L("Op_UnexpectedError"), ex.Message);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<OperationResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        var result = new OperationResult { Success = true };
        var errors = new List<string>();

        try
        {
            foreach (var copiedPath in _copiedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (_mergedDestinations.Contains(copiedPath))
                    {
                        // Zusammengeführter Ordner: nur das entfernen, was die Kopie hinzugefügt hat.
                        UndoMergedCopy(copiedPath, errors);
                        result.AffectedPaths.Add(copiedPath);
                        continue;
                    }

                    if (FileSystemRouter.IsRemotePath(copiedPath))
                    {
                        // Remote undo: delete via provider
                        var provider = FileOperationHelpers.GetRemoteProvider(_router, copiedPath);
                        if (provider != null)
                        {
                            var remotePath = FileSystemRouter.ExtractRemotePath(copiedPath);
                            await provider.DeleteAsync(remotePath, recursive: true, cancellationToken);
                        }
                    }
                    else if (File.Exists(copiedPath))
                    {
                        File.Delete(copiedPath);
                    }
                    else if (Directory.Exists(copiedPath))
                    {
                        Directory.Delete(copiedPath, recursive: true);
                    }

                    result.AffectedPaths.Add(copiedPath);
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(L("Op_FailedTo_Delete"), FileOperationHelpers.GetFileName(copiedPath), ex.Message));
                }
            }

            FileOperationHelpers.FinalizeResultWithErrors(result, errors, "Op_SomeNotUndone");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = L("Op_Cancelled_Copy");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = string.Format(L("Op_UnexpectedErrorUndo"), ex.Message);
        }

        return result;
    }

    public void SetConflictResolution(ConflictResolution resolution, bool applyToAll)
    {
        _conflictResolution = resolution;
        _applyToAll = applyToAll;
    }

    // ── Shell-Kopier-Engine (lokal → lokal) ──

    /// <summary>
    /// Lokale Quellen für die Shell-Engine vorbereiten und dabei die Konfliktauflösung anwenden:
    /// Überspringen → nicht aufnehmen, Beide behalten → eindeutiger Name (auch gegen Namen, die dieselbe Operation
    /// schon vergibt), Ersetzen → Datei überschreiben bzw. Ordner zusammenführen. Elemente in <paramref name="handled"/>
    /// überspringt die klassische Schleife.
    /// </summary>
    private List<ShellPlanEntry> PlanShellBatch(List<string> errors, HashSet<int> handled)
    {
        var plan = new List<ShellPlanEntry>();
        var plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effective = _applyToAll ? _conflictResolution : ConflictResolution.KeepBoth;
        if (effective == ConflictResolution.Prompt) effective = ConflictResolution.KeepBoth;

        for (int i = 0; i < _sourcePaths.Count; i++)
        {
            var sourcePath = _sourcePaths[i];
            if (FileSystemRouter.IsRemotePath(sourcePath)) continue;
            handled.Add(i);

            bool isDir = Directory.Exists(sourcePath);
            if (!isDir && !File.Exists(sourcePath))
            {
                errors.Add(string.Format(L("Op_PathNotFound"), sourcePath));
                continue;
            }

            var destPath = Path.Combine(_destinationDirectory, FileOperationHelpers.GetFileName(sourcePath));
            bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
            // Kopie in denselben Ordner (Duplizieren): immer unter neuem Namen, wie im Explorer.
            bool sameItem = destExists && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);
            var resolution = sameItem ? ConflictResolution.KeepBoth : effective;

            if (destExists && resolution == ConflictResolution.Skip) continue;

            string? newName = null;
            bool merge = false;
            if ((destExists && resolution == ConflictResolution.KeepBoth) || plannedDestinations.Contains(destPath))
            {
                destPath = FileOperationHelpers.GetUniqueFileName(destPath, plannedDestinations);
                newName = Path.GetFileName(destPath);
            }
            else if (destExists && resolution == ConflictResolution.Replace && isDir && Directory.Exists(destPath))
            {
                merge = true;
                RememberPreExisting(destPath);
            }

            plannedDestinations.Add(destPath);
            plan.Add(new ShellPlanEntry(i, sourcePath, destPath, newName, merge));
        }

        return plan;
    }

    private async Task<ShellRun> RunShellCopyAsync(
        List<ShellPlanEntry> plan, OperationResult result, List<string> errors,
        IProgress<FileOperationProgress>? progress, long totalBytes, DateTime startTime,
        CancellationToken cancellationToken)
    {
        var items = plan.Select(p => new ShellTransferWithProgress.Item(p.SourcePath, p.NewName)).ToList();
        int count = _sourcePaths.Count;

        var outcome = await Task.Run(() => ShellTransferWithProgress.Execute(
            ShellTransferWithProgress.Kind.Copy, items, _destinationDirectory,
            p => FileOperationHelpers.ReportProgress(progress, p.CurrentName, Math.Min(p.CompletedItems, count - 1), count,
                (long)(totalBytes * (p.Percent / 100.0)), totalBytes, startTime),
            _pauseEvent, cancellationToken), cancellationToken);

        if (outcome.EngineUnavailable)
        {
            s_shellEngineUnavailable = true;
            Span.Helpers.DebugLogger.Log($"[CopyFileOperation] Shell copy engine unavailable ({outcome.Error}) — using managed copy");
            return ShellRun.Unavailable;
        }

        var bySource = new Dictionary<string, ShellPlanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan) bySource.TryAdd(entry.SourcePath, entry);

        foreach (var item in outcome.Items)
        {
            if (!bySource.TryGetValue(item.SourcePath, out var entry)) continue;
            if (item.Succeeded)
            {
                var dest = item.DestinationPath ?? entry.DestinationPath;
                _copiedPaths.Add(dest);
                result.AffectedPaths.Add(dest);
            }
            else if (!outcome.Cancelled)
            {
                errors.Add(string.Format(L("Op_FailedTo_Copy"), FileOperationHelpers.GetFileName(entry.SourcePath),
                    ShellTransferWithProgress.DescribeHResult(item.HResult)));
            }
        }

        foreach (var (path, hr) in outcome.NestedFailures)
            errors.Add(string.Format(L("Op_FailedTo_Copy"), FileOperationHelpers.GetFileName(path), ShellTransferWithProgress.DescribeHResult(hr)));

        _nestedCreatedPaths.AddRange(outcome.CreatedPaths);
        if (outcome.Error != null && !outcome.Cancelled)
            errors.Add(outcome.Error);

        return outcome.Cancelled ? ShellRun.Cancelled : ShellRun.Completed;
    }

    /// <summary>
    /// Vor dem Zusammenführen festhalten, was im Zielordner schon lag: Undo lässt diese Elemente stehen (eine
    /// überschriebene Datei bleibt in der neuen Fassung, das Original liegt im Papierkorb). Riesige Zielordner werden
    /// nicht erfasst — dort wird der Ordner beim Undo lieber gar nicht angefasst.
    /// </summary>
    private void RememberPreExisting(string destDir)
    {
        _mergedDestinations.Add(destDir);
        try
        {
            int count = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(destDir, "*", options))
            {
                if (++count > MergeSnapshotLimit) { _mergeUndoUnsafe.Add(destDir); break; }
                _preExistingUnderMerged.Add(entry);
            }
        }
        catch
        {
            _mergeUndoUnsafe.Add(destDir);
        }
    }

    private void UndoMergedCopy(string destDir, List<string> errors)
    {
        if (_mergeUndoUnsafe.Contains(destDir))
        {
            errors.Add(string.Format(L("Op_FailedTo_Delete"), FileOperationHelpers.GetFileName(destDir), "merged folder too large to undo safely"));
            return;
        }

        var prefix = destDir.TrimEnd('\\') + "\\";
        var created = _nestedCreatedPaths
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length) // Dateien vor ihren Ordnern, tiefere Ordner vor flacheren
            .ToList();

        foreach (var path in created)
        {
            if (_preExistingUnderMerged.Contains(path)) continue;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path);
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(L("Op_FailedTo_Delete"), FileOperationHelpers.GetFileName(path), ex.Message));
            }
        }
    }

    // ── Remote stream-based copy ──

    private async Task CopyFileViaStreamAsync(
        string sourcePath, string destPath,
        bool srcIsRemote, bool destIsRemote,
        IProgress<long> progress,
        CancellationToken ct)
    {
        Stream sourceStream;
        bool sourceUsedProgress = false; // 다운로드에서 이미 진행률을 보고했는지 추적

        if (srcIsRemote)
        {
            var provider = FileOperationHelpers.GetRemoteProvider(_router, sourcePath)
                ?? throw new InvalidOperationException($"원격 소스에 대한 연결을 찾을 수 없습니다: {sourcePath}");
            var remotePath = FileSystemRouter.ExtractRemotePath(sourcePath);

            if (destIsRemote)
            {
                // Remote→Remote: über eine temporäre Datei statt durch den Arbeitsspeicher (MemoryStream ist auf
                // 2 GB begrenzt und kostete die volle Dateigröße an RAM). 진행률은 업로드 단계에서만 보고.
                sourceStream = await SpoolRemoteToTempFileAsync(provider, remotePath, ct);
            }
            else
            {
                // Remote→Local: direkt in die Zieldatei streamen (vorher komplett in einen MemoryStream).
                await DownloadRemoteToLocalAsync(provider, remotePath, destPath, progress, ct);
                return;
            }
        }
        else
        {
            sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOperationHelpers.DefaultBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        try
        {
            if (destIsRemote)
            {
                var provider = FileOperationHelpers.GetRemoteProvider(_router, destPath)
                    ?? throw new InvalidOperationException($"원격 대상에 대한 연결을 찾을 수 없습니다: {destPath}");
                var remotePath = FileSystemRouter.ExtractRemotePath(destPath);

                // Non-seekable source: buffer into memory first
                Stream uploadStream = sourceStream;
                if (!sourceStream.CanSeek)
                {
                    var memStream = new MemoryStream();
                    var buffer = new byte[FileOperationHelpers.DefaultBufferSize];
                    int bytesRead;
                    while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
                    {
                        WaitIfPaused(ct);
                        await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    }
                    memStream.Position = 0;
                    uploadStream = memStream;
                }

                // 업로드 (진행률은 한 번만 보고)
                if (provider is FtpProvider ftpUp)
                {
                    await ftpUp.UploadWithProgressAsync(remotePath, uploadStream,
                        new Progress<long>(bytes => progress?.Report(bytes)), ct);
                }
                else if (provider is SftpProvider sftpUp)
                {
                    await sftpUp.UploadWithProgressAsync(remotePath, uploadStream,
                        new Progress<long>(bytes => progress?.Report(bytes)), ct);
                }
                else
                {
                    await provider.WriteAsync(remotePath, uploadStream, ct);
                    if (uploadStream.CanSeek)
                        progress?.Report(uploadStream.Length);
                }

                if (uploadStream != sourceStream)
                    uploadStream.Dispose();
            }
            else
            {
                // Destination is local
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    FileOperationHelpers.DefaultBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[FileOperationHelpers.DefaultBufferSize];
                long copiedBytes = 0;
                int bytesRead;
                long lastReportTime = Environment.TickCount64;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
                {
                    WaitIfPaused(ct);
                    await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    copiedBytes += bytesRead;
                    // 다운로드에서 이미 진행률을 보고했으면 여기서 중복 보고 안 함
                    // 시간 간격 throttle로 UI 스레드 부하 감소
                    if (!sourceUsedProgress && progress != null)
                    {
                        long now = Environment.TickCount64;
                        if (now - lastReportTime >= FileOperationHelpers.ProgressReportIntervalMs)
                        {
                            progress.Report(copiedBytes);
                            lastReportTime = now;
                        }
                    }
                }
                // 최종 진행률 보고
                if (sourceUsedProgress && sourceStream.CanSeek)
                    progress?.Report(sourceStream.Length);
                else if (!sourceUsedProgress)
                    progress?.Report(copiedBytes);
            }
        }
        finally
        {
            sourceStream.Dispose();
        }
    }

    /// <summary>Remote-Datei zunächst separat herunterladen; das lokale Ziel erst nach vollständigem Erfolg ersetzen.</summary>
    internal static async Task DownloadRemoteToLocalAsync(
        IFileSystemProvider provider, string remotePath, string destPath, IProgress<long>? progress, CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        var stagingPath = Path.Combine(destDir ?? ".", ".span-download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var destStream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                FileOperationHelpers.DefaultBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (provider is FtpProvider ftp)
                    await ftp.DownloadWithProgressAsync(remotePath, destStream, new Progress<long>(bytes => progress?.Report(bytes)), ct);
                else if (provider is SftpProvider sftp)
                    await sftp.DownloadWithProgressAsync(remotePath, destStream, new Progress<long>(bytes => progress?.Report(bytes)), ct);
                else
                {
                    using var remoteStream = await provider.OpenReadAsync(remotePath, ct);
                    await remoteStream.CopyToAsync(destStream, FileOperationHelpers.DefaultBufferSize, ct);
                }

                progress?.Report(destStream.Length);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(stagingPath); } catch { }
            throw;
        }
    }

    /// <summary>Remote-Datei in eine temporäre Datei (DeleteOnClose) laden — Zwischenspeicher für Remote→Remote.</summary>
    internal static async Task<Stream> SpoolRemoteToTempFileAsync(IFileSystemProvider provider, string remotePath, CancellationToken ct)
    {
        var spoolPath = Path.Combine(Path.GetTempPath(), "span_xfer_" + Guid.NewGuid().ToString("N") + ".tmp");
        var spool = new FileStream(spoolPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            FileOperationHelpers.DefaultBufferSize, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            if (provider is FtpProvider ftp)
                await ftp.DownloadWithProgressAsync(remotePath, spool, null, ct);
            else if (provider is SftpProvider sftp)
                await sftp.DownloadWithProgressAsync(remotePath, spool, null, ct);
            else
            {
                using var remoteStream = await provider.OpenReadAsync(remotePath, ct);
                await remoteStream.CopyToAsync(spool, FileOperationHelpers.DefaultBufferSize, ct);
            }
            spool.Position = 0;
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    private async Task CopyDirectoryViaStreamAsync(
        string sourcePath, string destPath,
        bool srcIsRemote, bool destIsRemote,
        IProgress<long> overallProgress,
        CancellationToken ct)
    {
        long bytesCopied = 0;

        // Create destination directory
        if (destIsRemote)
        {
            var provider = FileOperationHelpers.GetRemoteProvider(_router, destPath)
                ?? throw new InvalidOperationException($"원격 대상에 대한 연결을 찾을 수 없습니다: {destPath}");
            await provider.CreateDirectoryAsync(FileSystemRouter.ExtractRemotePath(destPath), ct);
        }
        else
        {
            Directory.CreateDirectory(destPath);
        }

        // Get items from source
        IReadOnlyList<IFileSystemItem> items;
        if (srcIsRemote)
        {
            var provider = FileOperationHelpers.GetRemoteProvider(_router, sourcePath)
                ?? throw new InvalidOperationException($"원격 소스에 대한 연결을 찾을 수 없습니다: {sourcePath}");
            var remotePath = FileSystemRouter.ExtractRemotePath(sourcePath);
            items = await provider.GetItemsAsync(remotePath, ct);
        }
        else
        {
            var localItems = new List<IFileSystemItem>();
            // EnumerateFiles/EnumerateDirectories: lazy enumeration으로 취소 즉시 반응
            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                localItems.Add(new FileItem
                {
                    Name = Path.GetFileName(file),
                    Path = file,
                    Size = new FileInfo(file).Length
                });
            }
            foreach (var dir in Directory.EnumerateDirectories(sourcePath))
            {
                localItems.Add(new FolderItem
                {
                    Name = Path.GetFileName(dir),
                    Path = dir
                });
            }
            items = localItems;
        }

        foreach (var item in items)
        {
            WaitIfPaused(ct);
            ct.ThrowIfCancellationRequested();

            string childSrcPath;
            if (srcIsRemote)
            {
                // item.Path from provider is a remote-relative path; reconstruct full URI
                childSrcPath = FileOperationHelpers.CombineRemoteUri(sourcePath, item.Name);
            }
            else
            {
                childSrcPath = item.Path; // already full local path
            }

            var childDestPath = FileOperationHelpers.CombinePath(destPath, item.Name, destIsRemote);

            if (item is FolderItem)
            {
                await CopyDirectoryViaStreamAsync(childSrcPath, childDestPath, srcIsRemote, destIsRemote,
                    new Progress<long>(bytes =>
                    {
                        bytesCopied += bytes;
                        overallProgress?.Report(bytesCopied);
                    }),
                    ct);
            }
            else
            {
                await CopyFileViaStreamAsync(childSrcPath, childDestPath, srcIsRemote, destIsRemote,
                    new Progress<long>(bytes =>
                    {
                        overallProgress?.Report(bytesCopied + bytes);
                    }),
                    ct);
                if (item is FileItem fi)
                    bytesCopied += fi.Size;
            }
        }
    }

    // ── Helper methods ──

    private async Task<long> GetFileOrDirectorySizeAsync(string path, CancellationToken ct)
    {
        if (FileSystemRouter.IsRemotePath(path))
        {
            // Remote: try to get file size via provider (FTP/SFTP 모두 지원)
            try
            {
                var provider = FileOperationHelpers.GetRemoteProvider(_router, path);
                var remotePath = FileSystemRouter.ExtractRemotePath(path);

                if (provider is FtpProvider ftpProvider)
                {
                    bool isDir = await ftpProvider.IsDirectoryAsync(remotePath, ct);
                    if (!isDir)
                    {
                        var size = await ftpProvider.GetFileSizeAsync(remotePath, ct);
                        return size > 0 ? size : 0;
                    }
                }
                else if (provider is SftpProvider sftpProvider)
                {
                    bool isDir = await sftpProvider.IsDirectoryAsync(remotePath, ct);
                    if (!isDir)
                    {
                        var size = await sftpProvider.GetFileSizeAsync(remotePath, ct);
                        return size > 0 ? size : 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Span.Helpers.DebugLogger.Log($"[CopyFileOperation] 원격 파일 크기 조회 실패 ({path}): {ex.Message}");
            }
            return 0;
        }

        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (Directory.Exists(path))
        {
            try
            {
                return await Task.Run(() =>
                    new DirectoryInfo(path)
                        .EnumerateFiles("*", SizeWalkOptions)
                        .Sum(f => f.Length), ct);
            }
            catch { return 0; }
        }

        return 0;
    }

    /// <summary>
    /// Größenberechnung: Junctions/Symlinks nicht verfolgen (eine Junction auf einen Vorfahren lief sonst bis
    /// PathTooLong im Kreis) und unzugängliche Unterordner überspringen statt die ganze Summe auf 0 fallen zu lassen.
    /// </summary>
    internal static readonly EnumerationOptions SizeWalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

}

/// <summary>
/// Defines conflict resolution strategies when copying files.
/// </summary>
public enum ConflictResolution
{
    Prompt,
    Replace,
    Skip,
    KeepBoth
}
