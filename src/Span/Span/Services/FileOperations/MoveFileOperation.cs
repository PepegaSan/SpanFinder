using System.Threading;
using Span.Models;
using static Span.Services.LocalizationService;

namespace Span.Services.FileOperations;

/// <summary>
/// Represents a file or directory move (cut/paste) operation with undo support and pause capability.
///
/// Lokal → lokal läuft über die Shell-Kopier-Engine (<see cref="ShellTransferWithProgress"/>, wie der Explorer):
/// gleiche Partition = Umbenennen, andere Partition = Kopieren + Löschen, "Ersetzen" führt Ordner zusammen, und
/// das Rückgängigmachen funktioniert auch über Laufwerksgrenzen (Directory.Move warf dort). Die eigene
/// Stream-Kopie bleibt für Remote-Pfade (FTP/SFTP) und als Fallback, falls die Engine nicht verfügbar ist.
/// </summary>
public class MoveFileOperation : IFileOperation, IPausableOperation
{
    private readonly List<string> _sourcePaths;
    private readonly string _destinationDirectory;
    private readonly FileSystemRouter? _router;
    private readonly Dictionary<string, string> _moveMap = new(); // source -> destination
    private bool _hasRemotePaths;
    private ConflictResolution _conflictResolution = ConflictResolution.Prompt;
    private bool _applyToAll = false;
    private ManualResetEventSlim? _pauseEvent;

    // Shell-Engine-Pfad: zusammengeführte Zielordner und die darunter abgelegten Elemente (für Undo).
    private readonly HashSet<string> _mergedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _nestedCreatedPaths = new();

    /// <summary>Für die Sitzung auf File.Move/Directory.Move zurückfallen, wenn IFileOperation nicht erzeugbar ist.</summary>
    private static volatile bool s_shellEngineUnavailable;

    private sealed record ShellPlanEntry(int Index, string SourcePath, string DestinationPath, string? NewName, bool MergeIntoExisting);
    private enum ShellRun { Unavailable, Completed, Cancelled }

    public MoveFileOperation(List<string> sourcePaths, string destinationDirectory)
        : this(sourcePaths, destinationDirectory, null)
    {
    }

    public MoveFileOperation(List<string> sourcePaths, string destinationDirectory, FileSystemRouter? router)
    {
        _sourcePaths = sourcePaths ?? throw new ArgumentNullException(nameof(sourcePaths));
        _destinationDirectory = destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory));
        _router = router;
        _hasRemotePaths = _sourcePaths.Any(FileSystemRouter.IsRemotePath)
                          || FileSystemRouter.IsRemotePath(_destinationDirectory);
    }

    /// <summary>Gets the destination directory for this move operation.</summary>
    public string DestinationDirectory => _destinationDirectory;

    /// <summary>Gets the source paths for this move operation (used for post-move source column refresh).</summary>
    public IReadOnlyList<string> SourcePaths => _sourcePaths;

    /// <inheritdoc/>
    public string Description => _sourcePaths.Count == 1
        ? string.Format(L("Op_MoveSingle"), FileOperationHelpers.GetFileName(_sourcePaths[0]), FileOperationHelpers.GetFileName(_destinationDirectory))
        : string.Format(L("Op_MoveMultiple"), _sourcePaths.Count, FileOperationHelpers.GetFileName(_destinationDirectory));

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
        var errors = new List<string>();
        long totalBytes = 0;
        long processedBytes = 0;
        var startTime = DateTime.Now;

        try
        {
            // Calculate total bytes for progress
            foreach (var path in _sourcePaths)
            {
                totalBytes += GetFileOrDirectorySize(path);
            }

            // ── Lokal → lokal über die Shell-Kopier-Engine (siehe Klassenkommentar). Remote-Elemente und der Fall
            //    "Engine nicht verfügbar" laufen weiter durch die Schleife darunter.
            var handledByShell = new HashSet<int>();
            if (!FileSystemRouter.IsRemotePath(_destinationDirectory) && !s_shellEngineUnavailable
                && Directory.Exists(_destinationDirectory))
            {
                var plan = PlanShellBatch(errors, handledByShell);
                if (plan.Count > 0)
                {
                    var run = await RunShellMoveAsync(plan, result, errors, progress, totalBytes, startTime, cancellationToken);
                    if (run == ShellRun.Unavailable)
                    {
                        foreach (var entry in plan) handledByShell.Remove(entry.Index); // alte Schleife übernimmt
                    }
                    else if (run == ShellRun.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        result.Success = false;
                        result.ErrorMessage = L("Op_Cancelled_Move");
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

                // Cutting and pasting into the same remote folder must never copy
                // over the source and then delete it. Remote paths are case-sensitive.
                if (srcIsRemote && destIsRemote && FileOperationHelpers.IsSameRemotePath(sourcePath, destPath))
                    continue;

                try
                {
                    progress?.Report(new FileOperationProgress
                    {
                        CurrentFile = fileName,
                        CurrentFileIndex = i + 1,
                        TotalFileCount = _sourcePaths.Count,
                        TotalBytes = totalBytes,
                        ProcessedBytes = processedBytes,
                        Percentage = totalBytes > 0 ? (int)(processedBytes * 100 / totalBytes) : (i + 1) * 100 / _sourcePaths.Count
                    });

                    if (srcIsRemote || destIsRemote)
                    {
                        // ── Remote move: stream copy + delete source ──
                        if (_router == null)
                        {
                            errors.Add(string.Format(L("Op_NoRemoteRouter"), sourcePath));
                            continue;
                        }

                        // Conflict check: only for local destinations
                        if (!destIsRemote && (File.Exists(destPath) || Directory.Exists(destPath)))
                        {
                            switch (_conflictResolution)
                            {
                                case ConflictResolution.Skip:
                                    continue;
                                case ConflictResolution.Replace:
                                    // Download commits through a temporary sibling file;
                                    // preserve an existing target until transfer succeeds.
                                    if (Directory.Exists(destPath)) _mergedDestinations.Add(destPath);
                                    break;
                                case ConflictResolution.KeepBoth:
                                default:
                                    destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                                    break;
                            }
                        }

                        // Check if source is a directory
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

                        // Copy via stream
                        if (srcIsDir)
                        {
                            if (srcIsRemote && destIsRemote && FileOperationHelpers.IsRemoteDescendantPath(sourcePath, destPath))
                                throw new IOException("Cannot move a remote folder into itself.");
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

                        // A cancelled recursive copy (including an empty folder) must
                        // never reach the synchronous local source deletion below.
                        cancellationToken.ThrowIfCancellationRequested();
                        // Delete source after successful copy
                        if (srcIsRemote)
                        {
                            var provider = FileOperationHelpers.GetRemoteProvider(_router, sourcePath);
                            if (provider != null)
                            {
                                var remotePath = FileSystemRouter.ExtractRemotePath(sourcePath);
                                await provider.DeleteAsync(remotePath, recursive: true, cancellationToken);
                            }
                        }
                        else
                        {
                            if (File.Exists(sourcePath)) File.Delete(sourcePath);
                            else if (Directory.Exists(sourcePath)) Directory.Delete(sourcePath, recursive: true);
                        }
                    }
                    else
                    {
                        // ── Local move ──

                        // Ausschneiden + Einfügen in denselben Ordner: Ziel = Quelle. Ohne diese Prüfung lief die
                        // Konfliktbehandlung gegen die Datei selbst — KeepBoth benannte sie in "Name (1)" um,
                        // Replace löschte die Quelle vor dem Move. Der Explorer behandelt das als No-op.
                        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Handle conflict
                        if (File.Exists(destPath) || Directory.Exists(destPath))
                        {
                            switch (_conflictResolution)
                            {
                                case ConflictResolution.Skip:
                                    continue;
                                case ConflictResolution.Replace:
                                    // Replace files only after the source has been read/moved successfully.
                                    if (Directory.Exists(destPath)) _mergedDestinations.Add(destPath);
                                    break;
                                case ConflictResolution.KeepBoth:
                                default:
                                    destPath = FileOperationHelpers.GetUniqueFileName(destPath);
                                    break;
                            }
                        }

                        bool isCrossVolume = IsCrossVolumeMove(sourcePath, destPath);

                        if (isCrossVolume)
                        {
                            // Cross-volume: stream copy + delete (pausable)
                            var fileSize = GetFileOrDirectorySize(sourcePath);
                            long localProcessed = processedBytes;
                            int fileIndex = i;

                            if (File.Exists(sourcePath))
                            {
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

                                cancellationToken.ThrowIfCancellationRequested();

                                File.Delete(sourcePath);
                            }
                            else if (Directory.Exists(sourcePath))
                            {
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

                                cancellationToken.ThrowIfCancellationRequested();

                                Directory.Delete(sourcePath, recursive: true);
                            }

                            processedBytes += fileSize;
                        }
                        else
                        {
                            // Same-volume: fast move (nearly instant, not pausable)
                            if (File.Exists(sourcePath))
                            {
                                File.Move(sourcePath, destPath, overwrite: _conflictResolution == ConflictResolution.Replace);
                                processedBytes += new FileInfo(destPath).Length;
                            }
                            else if (Directory.Exists(sourcePath))
                            {
                                Directory.Move(sourcePath, destPath);
                                processedBytes += GetFileOrDirectorySize(destPath);
                            }
                            else
                            {
                                errors.Add(string.Format(L("Op_PathNotFound"), sourcePath));
                                continue;
                            }
                        }
                    }

                    _moveMap[sourcePath] = destPath;
                    result.AffectedPaths.Add(destPath);
                    FileOperationHelpers.NotifyPathMoved(sourcePath, destPath);
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
                    errors.Add(string.Format(L("Op_FailedTo_Move"), fileName, ex.Message));
                }
            }

            // 취소 시 예외 없이 즉시 종료
            if (cancellationToken.IsCancellationRequested)
            {
                result.Success = false;
                result.ErrorMessage = L("Op_Cancelled_Move");
                return result;
            }

            FileOperationHelpers.FinalizeResultWithErrors(result, errors, "Op_SomeNotMoved");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = L("Op_Cancelled_Move");
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
            // Move back in reverse order. Zusammengeführte Ordner: nur die tatsächlich verschobenen Elemente zurück,
            // was vorher schon am Ziel lag, bleibt dort.
            var simple = new List<(string Source, string Dest)>();
            foreach (var (source, dest) in _moveMap.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_mergedDestinations.Contains(dest))
                {
                    UndoMergedMove(source, dest, errors);
                    result.AffectedPaths.Add(source);
                    FileOperationHelpers.NotifyPathMoved(dest, source);
                }
                else
                {
                    simple.Add((source, dest));
                }
            }

            // Zurück über die Shell-Engine: funktioniert auch über Laufwerksgrenzen, wo Directory.Move
            // ("Source and destination path must have identical roots") immer scheiterte.
            var remaining = simple;
            if (simple.Count > 0 && !s_shellEngineUnavailable)
                remaining = await UndoMovesViaShellAsync(simple, result, errors, cancellationToken);

            foreach (var (source, dest) in remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(dest))
                    {
                        File.Move(dest, source);
                    }
                    else if (Directory.Exists(dest))
                    {
                        Directory.Move(dest, source);
                    }

                    result.AffectedPaths.Add(source);
                    FileOperationHelpers.NotifyPathMoved(dest, source);
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(L("Op_FailedTo_MoveBack"), FileOperationHelpers.GetFileName(dest), ex.Message));
                }
            }

            FileOperationHelpers.FinalizeResultWithErrors(result, errors, "Op_SomeNotUndone");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = L("Op_Cancelled_Move");
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
    /// Lokale Quellen für die Shell-Engine vorbereiten und die Konfliktauflösung anwenden (siehe CopyFileOperation).
    /// Verschieben in denselben Ordner ist ein No-op wie im Explorer.
    /// </summary>
    private List<ShellPlanEntry> PlanShellBatch(List<string> errors, HashSet<int> handled)
    {
        var plan = new List<ShellPlanEntry>();
        var plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effective = _conflictResolution is ConflictResolution.Replace or ConflictResolution.Skip
            ? _conflictResolution
            : ConflictResolution.KeepBoth;

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
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                continue;

            bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
            if (destExists && effective == ConflictResolution.Skip) continue;

            string? newName = null;
            bool merge = false;
            if ((destExists && effective == ConflictResolution.KeepBoth) || plannedDestinations.Contains(destPath))
            {
                destPath = FileOperationHelpers.GetUniqueFileName(destPath, plannedDestinations);
                newName = Path.GetFileName(destPath);
            }
            else if (destExists && effective == ConflictResolution.Replace && isDir && Directory.Exists(destPath))
            {
                merge = true;
                _mergedDestinations.Add(destPath);
            }

            plannedDestinations.Add(destPath);
            plan.Add(new ShellPlanEntry(i, sourcePath, destPath, newName, merge));
        }

        return plan;
    }

    private async Task<ShellRun> RunShellMoveAsync(
        List<ShellPlanEntry> plan, OperationResult result, List<string> errors,
        IProgress<FileOperationProgress>? progress, long totalBytes, DateTime startTime,
        CancellationToken cancellationToken)
    {
        var items = plan.Select(p => new ShellTransferWithProgress.Item(p.SourcePath, p.NewName)).ToList();
        int count = _sourcePaths.Count;

        var outcome = await Task.Run(() => ShellTransferWithProgress.Execute(
            ShellTransferWithProgress.Kind.Move, items, _destinationDirectory,
            p => FileOperationHelpers.ReportProgress(progress, p.CurrentName, Math.Min(p.CompletedItems, count - 1), count,
                (long)(totalBytes * (p.Percent / 100.0)), totalBytes, startTime),
            _pauseEvent, cancellationToken), cancellationToken);

        if (outcome.EngineUnavailable)
        {
            s_shellEngineUnavailable = true;
            Span.Helpers.DebugLogger.Log($"[MoveFileOperation] Shell copy engine unavailable ({outcome.Error}) — using File.Move");
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
                _moveMap[entry.SourcePath] = dest;
                result.AffectedPaths.Add(dest);
                FileOperationHelpers.NotifyPathMoved(entry.SourcePath, dest);

                // Nach dem Zusammenführen bleibt der leere Quellordner nicht stehen (Explorer-Verhalten).
                if (entry.MergeIntoExisting)
                {
                    try
                    {
                        if (Directory.Exists(entry.SourcePath) && !Directory.EnumerateFileSystemEntries(entry.SourcePath).Any())
                            Directory.Delete(entry.SourcePath);
                    }
                    catch { /* bleibt eben stehen */ }
                }
            }
            else if (!outcome.Cancelled)
            {
                errors.Add(string.Format(L("Op_FailedTo_Move"), FileOperationHelpers.GetFileName(entry.SourcePath),
                    ShellTransferWithProgress.DescribeHResult(item.HResult)));
            }
        }

        foreach (var (path, hr) in outcome.NestedFailures)
            errors.Add(string.Format(L("Op_FailedTo_Move"), FileOperationHelpers.GetFileName(path), ShellTransferWithProgress.DescribeHResult(hr)));

        _nestedCreatedPaths.AddRange(outcome.CreatedPaths);
        if (outcome.Error != null && !outcome.Cancelled)
            errors.Add(outcome.Error);

        return outcome.Cancelled ? ShellRun.Cancelled : ShellRun.Completed;
    }

    /// <summary>
    /// Verschobene Elemente über die Shell-Engine zurückbewegen, gruppiert nach Ursprungsordner.
    /// Gibt die Paare zurück, die noch zu behandeln sind (alle, wenn die Engine nicht verfügbar ist).
    /// </summary>
    private async Task<List<(string Source, string Dest)>> UndoMovesViaShellAsync(
        List<(string Source, string Dest)> pairs, OperationResult result, List<string> errors, CancellationToken cancellationToken)
    {
        var remaining = new List<(string Source, string Dest)>();

        foreach (var group in pairs.GroupBy(p => Path.GetDirectoryName(p.Source) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = group.Key;
            if (string.IsNullOrEmpty(parent))
            {
                remaining.AddRange(group);
                continue;
            }

            var pending = group.Where(p => File.Exists(p.Dest) || Directory.Exists(p.Dest)).ToList();
            foreach (var gone in group.Except(pending))
                result.AffectedPaths.Add(gone.Source); // am Ziel inzwischen weg — nichts zurückzubewegen

            if (pending.Count == 0) continue;

            try { Directory.CreateDirectory(parent); }
            catch (Exception ex)
            {
                foreach (var p in pending)
                    errors.Add(string.Format(L("Op_FailedTo_MoveBack"), FileOperationHelpers.GetFileName(p.Dest), ex.Message));
                continue;
            }

            var items = pending.Select(p => new ShellTransferWithProgress.Item(p.Dest, Path.GetFileName(p.Source))).ToList();
            var outcome = await Task.Run(() => ShellTransferWithProgress.Execute(
                ShellTransferWithProgress.Kind.Move, items, parent, null, null, cancellationToken), cancellationToken);

            if (outcome.EngineUnavailable)
            {
                s_shellEngineUnavailable = true;
                remaining.AddRange(pending);
                continue;
            }

            foreach (var r in outcome.Items)
            {
                var pair = pending.FirstOrDefault(p => string.Equals(p.Dest, r.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (pair.Dest == null) continue;
                if (r.Succeeded)
                {
                    result.AffectedPaths.Add(pair.Source);
                    FileOperationHelpers.NotifyPathMoved(pair.Dest, pair.Source);
                }
                else
                {
                    errors.Add(string.Format(L("Op_FailedTo_MoveBack"), FileOperationHelpers.GetFileName(pair.Dest),
                        ShellTransferWithProgress.DescribeHResult(r.HResult)));
                }
            }
            if (outcome.Error != null)
                errors.Add(outcome.Error);
        }

        return remaining;
    }

    /// <summary>
    /// Undo eines zusammengeführten Ordners: die hineinverschobenen Elemente einzeln an ihren alten Platz zurück,
    /// danach leer gewordene Unterordner entfernen. Was vorher schon am Ziel lag, bleibt unberührt.
    /// </summary>
    private void UndoMergedMove(string source, string dest, List<string> errors)
    {
        var prefix = dest.TrimEnd('\\') + "\\";
        var created = _nestedCreatedPaths
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .ToList();

        try { Directory.CreateDirectory(source); }
        catch (Exception ex)
        {
            errors.Add(string.Format(L("Op_FailedTo_MoveBack"), FileOperationHelpers.GetFileName(dest), ex.Message));
            return;
        }

        foreach (var path in created)
        {
            try
            {
                var relative = path.Substring(prefix.Length);
                var target = Path.Combine(source, relative);
                if (File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(path, target);
                }
                else if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(target);
                    if (!Directory.EnumerateFileSystemEntries(path).Any())
                        Directory.Delete(path);
                }
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(L("Op_FailedTo_MoveBack"), FileOperationHelpers.GetFileName(path), ex.Message));
            }
        }
    }

    // ── Remote stream-based copy (reused from CopyFileOperation pattern) ──

    private async Task CopyFileViaStreamAsync(
        string sourcePath, string destPath,
        bool srcIsRemote, bool destIsRemote,
        IProgress<long> progress,
        CancellationToken ct)
    {
        Stream sourceStream;
        if (srcIsRemote)
        {
            var provider = FileOperationHelpers.GetRemoteProvider(_router, sourcePath)
                ?? throw new InvalidOperationException($"원격 소스에 대한 연결을 찾을 수 없습니다: {sourcePath}");
            var remotePath = FileSystemRouter.ExtractRemotePath(sourcePath);

            if (!destIsRemote)
            {
                // Remote→Local: direkt in die Zieldatei streamen statt die ganze Datei in den Speicher zu laden.
                await CopyFileOperation.DownloadRemoteToLocalAsync(provider, remotePath, destPath, progress, ct);
                return;
            }

            // Remote→Remote: Zwischenspeicher als temporäre Datei (MemoryStream: 2-GB-Grenze, volle Dateigröße an RAM).
            sourceStream = await CopyFileOperation.SpoolRemoteToTempFileAsync(provider, remotePath, ct);
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

                if (!sourceStream.CanSeek)
                {
                    var memStream = new MemoryStream();
                    var buffer = new byte[FileOperationHelpers.DefaultBufferSize];
                    long copiedBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
                    {
                        WaitIfPaused(ct);
                        await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        copiedBytes += bytesRead;
                        progress?.Report(copiedBytes);
                    }
                    memStream.Position = 0;
                    await provider.WriteAsync(remotePath, memStream, ct);
                    memStream.Dispose();
                }
                else
                {
                    progress?.Report(sourceStream.Length);
                    await provider.WriteAsync(remotePath, sourceStream, ct);
                }
            }
            else
            {
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
                    // 시간 간격 throttle로 UI 스레드 부하 감소
                    if (progress != null)
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
                progress?.Report(copiedBytes);
            }
        }
        finally
        {
            sourceStream.Dispose();
        }
    }

    private async Task CopyDirectoryViaStreamAsync(
        string sourcePath, string destPath,
        bool srcIsRemote, bool destIsRemote,
        IProgress<long> overallProgress,
        CancellationToken ct)
    {
        long bytesCopied = 0;

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
                childSrcPath = FileOperationHelpers.CombineRemoteUri(sourcePath, item.Name);
            }
            else
            {
                childSrcPath = item.Path;
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

    // ── Local copy (unchanged) ──

    private static bool IsCrossVolumeMove(string source, string destination)
    {
        var sourceRoot = Path.GetPathRoot(source);
        var destRoot = Path.GetPathRoot(destination);
        return !string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helper methods ──

    private static long GetFileOrDirectorySize(string path)
    {
        if (FileSystemRouter.IsRemotePath(path))
            return 0; // Remote: size unknown

        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (Directory.Exists(path))
        {
            try
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", CopyFileOperation.SizeWalkOptions)
                    .Sum(f => f.Length);
            }
            catch { return 0; }
        }
        return 0;
    }

}
