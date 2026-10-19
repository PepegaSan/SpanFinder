using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Span.Helpers;
using Span.Models;

namespace Span.Services
{
    /// <summary>
    /// Persists color tags and folder notes under %LocalAppData%\Span.
    /// Local paths only; remote/archive paths are ignored.
    /// </summary>
    public sealed class ItemAnnotationService
    {
        private const string FileName = "item-annotations.json";
        private const string TempFileName = "item-annotations.json.tmp";
        private const string BackupFileName = "item-annotations.json.bak";

        private readonly string _storagePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase); // Pfad -> Tag-Id (ColorTagCatalog)
        private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public event Action<string, string?>? ColorTagChanged;
        public event Action<string>? NoteChanged;

        public ItemAnnotationService()
        {
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Span");
            Directory.CreateDirectory(_storagePath);
        }

        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loaded) return;
                await LoadUnlockedAsync().ConfigureAwait(false);
                _loaded = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Tag-Id des Elements; null = kein Tag.</summary>
        public string? GetColorTag(string path)
        {
            if (!IsAnnotatable(path)) return null;
            EnsureLoadedSync();
            lock (_tags) return _tags.TryGetValue(Normalize(path), out var tag) ? tag : null;
        }

        /// <summary>Alle getaggten Pfade (Momentaufnahme) — für die <c>tag:</c>-Suche ohne Laufwerksdurchlauf.</summary>
        public List<KeyValuePair<string, string>> GetTaggedPaths()
        {
            EnsureLoadedSync();
            lock (_tags) return _tags.ToList();
        }

        public string GetFolderNote(string path)
        {
            if (!IsAnnotatable(path)) return string.Empty;
            EnsureLoadedSync();
            return _notes.TryGetValue(Normalize(path), out var note) ? note : string.Empty;
        }

        public bool HasFolderNote(string path)
        {
            if (!IsAnnotatable(path)) return false;
            EnsureLoadedSync();
            return _notes.TryGetValue(Normalize(path), out var note) && !string.IsNullOrWhiteSpace(note);
        }

        /// <param name="tag">Tag-Id; null/leer entfernt den Tag.</param>
        public async Task SetColorTagAsync(string path, string? tag)
        {
            if (!IsAnnotatable(path)) return;
            var key = Normalize(path);

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await LoadUnlockedAsync().ConfigureAwait(false);
                    _loaded = true;
                }

                lock (_tags)
                {
                    if (string.IsNullOrEmpty(tag))
                        _tags.Remove(key);
                    else
                        _tags[key] = tag;
                }

                await SaveUnlockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            ColorTagChanged?.Invoke(path, string.IsNullOrEmpty(tag) ? null : tag);
        }

        /// <summary>Ein Tag wurde gelöscht: seine Zuweisungen entfernen. Gibt die betroffenen Pfade zurück.</summary>
        public async Task<List<string>> RemoveColorTagEverywhereAsync(string tagId)
        {
            var removed = new List<string>();
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await LoadUnlockedAsync().ConfigureAwait(false);
                    _loaded = true;
                }

                lock (_tags)
                {
                    foreach (var (path, id) in _tags.ToList())
                    {
                        if (!string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase)) continue;
                        _tags.Remove(path);
                        removed.Add(path);
                    }
                }
                if (removed.Count > 0)
                    await SaveUnlockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
            return removed;
        }

        /// <summary>
        /// Umbenannt/verschoben (in Span): Tag und Notiz wandern mit — bei einem Ordner auch die aller Elemente
        /// darunter. Sonst ginge die nur am Pfad hängende Markierung verloren.
        /// </summary>
        public async Task MovePathAsync(string oldPath, string newPath)
        {
            if (!IsAnnotatable(oldPath) || !IsAnnotatable(newPath)) return;
            var oldKey = Normalize(oldPath);
            var newKey = Normalize(newPath);
            if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;

            var moved = new List<(string From, string To, string Tag)>();
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await LoadUnlockedAsync().ConfigureAwait(false);
                    _loaded = true;
                }

                lock (_tags)
                {
                    foreach (var (path, tag) in _tags.ToList())
                    {
                        var target = Rebase(path, oldKey, newKey);
                        if (target == null) continue;
                        _tags.Remove(path);
                        _tags[target] = tag;
                        moved.Add((path, target, tag));
                    }
                }
                bool notesMoved = false;
                foreach (var (path, note) in _notes.ToList())
                {
                    var target = Rebase(path, oldKey, newKey);
                    if (target == null) continue;
                    _notes.Remove(path);
                    _notes[target] = note;
                    notesMoved = true;
                }
                if (moved.Count > 0 || notesMoved)
                    await SaveUnlockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            foreach (var (from, to, tag) in moved)
            {
                ColorTagChanged?.Invoke(from, null);
                ColorTagChanged?.Invoke(to, tag);
            }
        }

        /// <summary>Pfad gleich <paramref name="oldKey"/> oder darunter auf <paramref name="newKey"/> umsetzen; sonst null.</summary>
        internal static string? Rebase(string path, string oldKey, string newKey)
        {
            if (string.Equals(path, oldKey, StringComparison.OrdinalIgnoreCase)) return newKey;
            if (path.Length > oldKey.Length
                && path.StartsWith(oldKey, StringComparison.OrdinalIgnoreCase)
                && path[oldKey.Length] == Path.DirectorySeparatorChar)
                return newKey + path.Substring(oldKey.Length);
            return null;
        }

        public async Task SetFolderNoteAsync(string path, string? note)
        {
            if (!IsAnnotatable(path)) return;
            var key = Normalize(path);
            var text = note?.TrimEnd() ?? string.Empty;

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await LoadUnlockedAsync().ConfigureAwait(false);
                    _loaded = true;
                }

                if (string.IsNullOrWhiteSpace(text))
                    _notes.Remove(key);
                else
                    _notes[key] = text;

                await SaveUnlockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            NoteChanged?.Invoke(path);
        }

        public static bool IsAnnotatable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (FileSystemRouter.IsRemotePath(path)) return false;
            if (ArchivePathHelper.IsArchivePath(path)) return false;
            return Path.IsPathRooted(path);
        }

        private void EnsureLoadedSync()
        {
            if (_loaded) return;
            _lock.Wait();
            try
            {
                if (_loaded) return;
                LoadUnlockedAsync().GetAwaiter().GetResult();
                _loaded = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task LoadUnlockedAsync()
        {
            var filePath = Path.Combine(_storagePath, FileName);
            var bakPath = Path.Combine(_storagePath, BackupFileName);

            var data = await TryReadAsync(filePath).ConfigureAwait(false)
                ?? await TryReadAsync(bakPath).ConfigureAwait(false);

            _tags.Clear();
            _notes.Clear();
            if (data == null) return;

            if (data.Tags != null)
            {
                foreach (var (path, tagId) in data.Tags)
                {
                    // Werte sind Tag-Ids; die eingebauten heißen wie das frühere Enum ("Red" …)
                    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(tagId) || tagId == "None") continue;
                    _tags[Normalize(path)] = tagId;
                }
            }

            if (data.Notes != null)
            {
                foreach (var (path, note) in data.Notes)
                {
                    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(note)) continue;
                    _notes[Normalize(path)] = note;
                }
            }
        }

        private async Task SaveUnlockedAsync()
        {
            var filePath = Path.Combine(_storagePath, FileName);
            var tmpPath = Path.Combine(_storagePath, TempFileName);
            var bakPath = Path.Combine(_storagePath, BackupFileName);

            var data = new AnnotationStore
            {
                Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };

            lock (_tags)
            {
                foreach (var (path, tag) in _tags)
                    data.Tags[path] = tag;
            }
            foreach (var (path, note) in _notes)
                data.Notes[path] = note;

            var json = JsonSerializer.Serialize(data, JsonOptions);
            await Task.Run(() =>
            {
                File.WriteAllText(tmpPath, json, Encoding.UTF8);
                if (File.Exists(filePath))
                {
                    try { File.Copy(filePath, bakPath, overwrite: true); }
                    catch { /* best effort */ }
                }
                File.Move(tmpPath, filePath, overwrite: true);
            }).ConfigureAwait(false);
        }

        private static async Task<AnnotationStore?> TryReadAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = await Task.Run(() => File.ReadAllText(path, Encoding.UTF8)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AnnotationStore>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ItemAnnotation] Failed to read {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private sealed class AnnotationStore
        {
            public Dictionary<string, string>? Tags { get; set; }
            public Dictionary<string, string>? Notes { get; set; }
        }
    }
}
