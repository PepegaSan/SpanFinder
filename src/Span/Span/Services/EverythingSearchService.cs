using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Span.Helpers;
using Span.Models;
using Span.ViewModels;

namespace Span.Services
{
    /// <summary>
    /// Bridges Voidtools Everything (es.exe CLI) into recursive search.
    /// Falls back to <see cref="RecursiveSearchService"/> when Everything is unavailable.
    /// </summary>
    public sealed class EverythingSearchService
    {
        public const int MaxResults = RecursiveSearchService.MaxResults;
        private const int BatchSize = 50;
        private const int ProbeTimeoutMs = 2000;
        private const int SearchTimeoutMs = 60_000;

        private readonly FileSystemService _fileService;
        private readonly ISettingsService _settings;

        public EverythingSearchService(FileSystemService fileService, ISettingsService settings)
        {
            _fileService = fileService;
            _settings = settings;
        }

        /// <summary>
        /// True when settings allow Everything, path is local, and es.exe can talk to Everything IPC.
        /// </summary>
        public bool CanUse(string rootPath, out string? esPath)
        {
            esPath = null;
            if (!_settings.UseEverythingSearch)
                return false;
            if (string.IsNullOrWhiteSpace(rootPath))
                return false;
            if (FileSystemRouter.IsRemotePath(rootPath))
                return false;
            if (ArchivePathHelper.IsArchivePath(rootPath))
                return false;

            esPath = ResolveEsPath();
            if (esPath == null)
                return false;

            return ProbeAvailable(esPath);
        }

        public ChannelReader<List<FileSystemViewModel>> SearchInBackground(
            string esPath,
            string rootPath,
            SearchQuery query,
            bool showHidden,
            IProgress<RecursiveSearchService.SearchProgress>? progress,
            CancellationToken ct)
        {
            var channel = Channel.CreateBounded<List<FileSystemViewModel>>(
                new BoundedChannelOptions(16)
                {
                    SingleWriter = true,
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProduceResultsAsync(channel.Writer, esPath, rootPath, query, showHidden, progress, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[EverythingSearch] Exception: {ex.Message}");
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            return channel.Reader;
        }

        private async Task ProduceResultsAsync(
            ChannelWriter<List<FileSystemViewModel>> writer,
            string esPath,
            string rootPath,
            SearchQuery query,
            bool showHidden,
            IProgress<RecursiveSearchService.SearchProgress>? progress,
            CancellationToken ct)
        {
            var searchText = BuildEverythingQuery(query);
            DebugLogger.Log($"[EverythingSearch] start root={rootPath} query={searchText}");

            var args = new StringBuilder();
            args.Append("-n ").Append(MaxResults);
            args.Append(" -timeout ").Append(SearchTimeoutMs);
            args.Append(" -path ").Append(QuoteArg(rootPath));
            if (!showHidden)
                args.Append(" /a-h");
            if (!string.IsNullOrWhiteSpace(searchText))
                args.Append(' ').Append(searchText);

            var psi = new ProcessStartInfo
            {
                FileName = esPath,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                DebugLogger.Log("[EverythingSearch] failed to start es.exe");
                return;
            }

            int filesFound = 0;
            var batch = new List<FileSystemViewModel>(BatchSize);

            while (!ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null)
                    break;

                line = line.Trim();
                if (line.Length == 0)
                    continue;

                try
                {
                    var vm = CreateViewModel(line, rootPath);
                    if (vm == null)
                        continue;
                    if (!SearchFilter.Matches(query, vm))
                        continue;

                    filesFound++;
                    batch.Add(vm);

                    if (batch.Count >= BatchSize)
                    {
                        await writer.WriteAsync(batch, ct);
                        batch = new List<FileSystemViewModel>(BatchSize);
                        progress?.Report(new RecursiveSearchService.SearchProgress
                        {
                            FilesFound = filesFound,
                            FoldersScanned = 0
                        });
                    }

                    if (filesFound >= MaxResults)
                        break;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[EverythingSearch] item failed ({line}): {ex.Message}");
                }
            }

            if (batch.Count > 0 && !ct.IsCancellationRequested)
                await writer.WriteAsync(batch, ct);

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct);
                }
            }
            catch { }

            var err = string.Empty;
            try { err = await process.StandardError.ReadToEndAsync(ct); } catch { }
            if (!string.IsNullOrWhiteSpace(err))
                DebugLogger.Log($"[EverythingSearch] stderr: {err.Trim()}");

            DebugLogger.Log($"[EverythingSearch] done found={filesFound}");
            progress?.Report(new RecursiveSearchService.SearchProgress
            {
                FilesFound = filesFound,
                FoldersScanned = 0
            });
        }

        private FileSystemViewModel? CreateViewModel(string fullPath, string rootPath)
        {
            fullPath = LongPathHelper.StripPrefix(fullPath);
            try
            {
                if (Directory.Exists(fullPath))
                {
                    var info = new DirectoryInfo(fullPath);
                    var folderItem = new FolderItem
                    {
                        Name = info.Name,
                        Path = fullPath,
                        DateModified = info.LastWriteTime,
                        IsHidden = (info.Attributes & FileAttributes.Hidden) != 0
                    };
                    var folderVm = new FolderViewModel(folderItem, _fileService);
                    folderVm.MarkAsManuallyPopulated();
                    folderVm.LocationPath = GetRelativeParentPath(rootPath, fullPath);
                    return folderVm;
                }

                if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    var fileItem = new FileItem
                    {
                        Name = info.Name,
                        Path = fullPath,
                        Size = info.Length,
                        DateModified = info.LastWriteTime,
                        FileType = info.Extension,
                        IsHidden = (info.Attributes & FileAttributes.Hidden) != 0
                    };
                    var fileVm = new FileViewModel(fileItem);
                    fileVm.LocationPath = GetRelativeParentPath(rootPath, fullPath);
                    return fileVm;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[EverythingSearch] stat failed ({fullPath}): {ex.Message}");
            }

            return null;
        }

        internal static string BuildEverythingQuery(SearchQuery query)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(query.NameFilter))
            {
                var name = query.NameFilter.Replace("\"", string.Empty).Trim();
                if (name.Length > 0)
                    parts.Add(name);
            }

            if (!string.IsNullOrEmpty(query.ExtensionFilter))
            {
                var extPart = NormalizeExtList(query.ExtensionFilter);
                if (extPart.Length > 0)
                    parts.Add("ext:" + extPart);
            }

            if (query.KindFilter is FileKind kind)
            {
                var exts = SearchQueryParser.GetExtensionsForKind(kind)
                    .Select(e => e.TrimStart('.'))
                    .Where(e => e.Length > 0);
                var joined = string.Join(";", exts);
                if (joined.Length > 0)
                    parts.Add("ext:" + joined);
            }

            if (query.SizeFilter is { } size)
            {
                var op = size.Op switch
                {
                    CompareOp.GreaterThan => ">",
                    CompareOp.LessThan => "<",
                    CompareOp.GreaterOrEqual => ">=",
                    CompareOp.LessOrEqual => "<=",
                    CompareOp.Equals => "",
                    _ => ">"
                };
                parts.Add($"size:{op}{size.Bytes}");
            }

            return string.Join(" ", parts);
        }

        private static string NormalizeExtList(string extensionFilter)
        {
            if (extensionFilter.Contains(';'))
            {
                return string.Join(";", extensionFilter.Split(';')
                    .Select(e => e.Trim().TrimStart('.'))
                    .Where(e => e.Length > 0));
            }

            return extensionFilter.Trim().TrimStart('.');
        }

        private string? ResolveEsPath()
        {
            var configured = _settings.EverythingEsPath?.Trim();
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;

            foreach (var candidate in EnumerateDefaultEsPaths())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), "es.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private static IEnumerable<string> EnumerateDefaultEsPaths()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(pf, "Everything", "es.exe");
            yield return Path.Combine(pf86, "Everything", "es.exe");
            yield return Path.Combine(local, "Programs", "Everything", "es.exe");
            yield return Path.Combine(pf, "Everything 1.5a", "es.exe");
            yield return Path.Combine(pf86, "Everything 1.5a", "es.exe");
        }

        private static bool ProbeAvailable(string esPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = esPath,
                    Arguments = $"-n 0 -timeout {ProbeTimeoutMs}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                if (!process.WaitForExit(ProbeTimeoutMs + 500))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                var err = process.StandardError.ReadToEnd();
                if (err.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (err.Contains("Everything IPC", StringComparison.OrdinalIgnoreCase) &&
                    err.Contains("error", StringComparison.OrdinalIgnoreCase))
                    return false;

                return process.ExitCode == 0 || string.IsNullOrWhiteSpace(err);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[EverythingSearch] probe failed: {ex.Message}");
                return false;
            }
        }

        private static string QuoteArg(string value)
        {
            if (value.Contains(' ') || value.Contains('"'))
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            return value;
        }

        private static string GetRelativeParentPath(string rootPath, string itemPath)
        {
            var parentDir = Path.GetDirectoryName(itemPath);
            if (string.IsNullOrEmpty(parentDir))
                return string.Empty;

            if (parentDir.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var root = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (parentDir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return parentDir.Substring(root.Length);

            return parentDir;
        }
    }
}
