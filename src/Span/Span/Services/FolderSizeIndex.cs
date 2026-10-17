using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;

namespace Span.Services
{
    /// <summary>
    /// Tree-based folder size index shared by the Details view and Quick Look.
    /// One walk measures a folder and all of its subfolders bottom-up (no depth limit) and keeps
    /// the subfolder totals, so later requests inside the same branch — and the parent's own
    /// total — reuse them instead of walking the disk again. A cached subfolder is only reused
    /// while its last-write time is unchanged; invalidating a folder also drops its ancestors,
    /// because their totals include it.
    /// </summary>
    public sealed class FolderSizeIndex
    {
        /// <summary>Levels below the requested folder whose totals are kept (bounds memory on drive-root walks).</summary>
        public const int StoreDepth = 4;

        /// <summary>Safety net: start over if the index ever holds more folders than this.</summary>
        public const int MaxEntries = 250_000;

        private static readonly EnumerationOptions WalkOptions = new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0, // hidden and system files count, like Explorer's folder properties
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        private readonly record struct Entry(long Size, long WriteTicks);

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        // Paths invalidated while walks were running: a walk must not store results it read before that.
        private readonly Dictionary<string, long> _invalidatedAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private long _version;
        private int _activeWalks;

        /// <summary>Cached total in bytes, -1 if the folder was marked unavailable, null if unknown.</summary>
        public long? TryGet(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;
            return _entries.TryGetValue(Normalize(folderPath), out var entry) ? entry.Size : null;
        }

        /// <summary>Remember that the size could not be determined, so it is not retried on every request.</summary>
        public void MarkUnavailable(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            lock (_gate)
            {
                _entries[Normalize(folderPath)] = new Entry(-1, 0);
            }
        }

        /// <summary>
        /// Drop the folder and every parent folder from the index; subfolders stay valid.
        /// </summary>
        public void Invalidate(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            lock (_gate)
            {
                _version++;
                for (string? current = Normalize(folderPath); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    _entries.TryRemove(current, out _);
                    if (_activeWalks > 0) _invalidatedAt[current] = _version;
                }
            }
        }

        /// <summary>
        /// Return the cached total or walk the folder. Runs synchronously — call it off the UI thread.
        /// Completed subfolder totals are kept even when the walk is cancelled.
        /// </summary>
        public long GetOrCompute(string folderPath, CancellationToken token)
        {
            if (string.IsNullOrEmpty(folderPath)) return 0;
            var rootKey = Normalize(folderPath);
            if (_entries.TryGetValue(rootKey, out var cached) && cached.Size >= 0) return cached.Size;

            long startVersion;
            lock (_gate)
            {
                startVersion = _version;
                _activeWalks++;
            }

            var finished = new List<(string Key, Entry Entry)>();
            try
            {
                // Explicit stack instead of recursion: folder depth is unbounded with long paths.
                var stack = new Stack<Frame>();
                stack.Push(new Frame(rootKey, ReadWriteTicks(rootKey), depth: 0));
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var frame = stack.Peek();
                    frame.Subfolders ??= ReadFolder(frame, token);

                    if (frame.Next < frame.Subfolders.Count)
                    {
                        var (childKey, childTicks) = frame.Subfolders[frame.Next++];
                        if (_entries.TryGetValue(childKey, out var known) && known.Size >= 0 && known.WriteTicks == childTicks)
                        {
                            frame.Total += known.Size;
                            continue;
                        }
                        stack.Push(new Frame(childKey, childTicks, frame.Depth + 1));
                        continue;
                    }

                    stack.Pop();
                    if (frame.Depth <= StoreDepth)
                        finished.Add((frame.Key, new Entry(frame.Total, frame.WriteTicks)));
                    if (stack.Count == 0)
                        return frame.Total;
                    stack.Peek().Total += frame.Total;
                }
            }
            finally
            {
                Commit(finished, startVersion);
            }
        }

        private void Commit(List<(string Key, Entry Entry)> finished, long startVersion)
        {
            lock (_gate)
            {
                foreach (var (key, entry) in finished)
                {
                    if (_invalidatedAt.TryGetValue(key, out var invalidated) && invalidated > startVersion)
                        continue;
                    _entries[key] = entry;
                }

                if (--_activeWalks == 0)
                    _invalidatedAt.Clear();
                if (_entries.Count > MaxEntries)
                    _entries.Clear();
            }
        }

        /// <summary>Adds the folder's file sizes to <see cref="Frame.Total"/> and returns its subfolders.</summary>
        private static List<(string Key, long WriteTicks)> ReadFolder(Frame frame, CancellationToken token)
        {
            var subfolders = new List<(string Key, long WriteTicks)>();
            try
            {
                var entries = new FileSystemEnumerable<(string? Folder, long Value)>(
                    frame.Key,
                    static (ref FileSystemEntry entry) => entry.IsDirectory
                        ? (entry.ToFullPath(), entry.LastWriteTimeUtc.UtcTicks)
                        : (null, entry.Length),
                    WalkOptions)
                {
                    // Symbolic links and junctions are not followed; their target is counted where it lives.
                    ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                        !entry.IsDirectory || (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                };

                int seen = 0;
                foreach (var (folder, value) in entries)
                {
                    if ((++seen & 0x3FF) == 0) token.ThrowIfCancellationRequested();
                    if (folder != null) subfolders.Add((folder, value));
                    else frame.Total += value;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException)
            {
                // Missing, inaccessible or malformed folder counts as empty, as before.
            }
            return subfolders;
        }

        private static long ReadWriteTicks(string folderPath)
        {
            try { return Directory.GetLastWriteTimeUtc(folderPath).Ticks; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
        }

        private static string Normalize(string folderPath) => Path.TrimEndingDirectorySeparator(folderPath);

        private sealed class Frame
        {
            public Frame(string key, long writeTicks, int depth)
            {
                Key = key;
                WriteTicks = writeTicks;
                Depth = depth;
            }

            public string Key { get; }
            public long WriteTicks { get; }
            public int Depth { get; }
            public long Total;
            public int Next;
            public List<(string Key, long WriteTicks)>? Subfolders;
        }
    }
}
