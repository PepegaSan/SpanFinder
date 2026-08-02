using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly Dictionary<string, ItemColorTag> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public event Action<string, ItemColorTag>? ColorTagChanged;
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

        public ItemColorTag GetColorTag(string path)
        {
            if (!IsAnnotatable(path)) return ItemColorTag.None;
            EnsureLoadedSync();
            return _tags.TryGetValue(Normalize(path), out var tag) ? tag : ItemColorTag.None;
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

        public async Task SetColorTagAsync(string path, ItemColorTag tag)
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

                if (tag == ItemColorTag.None)
                    _tags.Remove(key);
                else
                    _tags[key] = tag;

                await SaveUnlockedAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            ColorTagChanged?.Invoke(path, tag);
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
                foreach (var (path, tagName) in data.Tags)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (Enum.TryParse<ItemColorTag>(tagName, ignoreCase: true, out var tag) && tag != ItemColorTag.None)
                        _tags[Normalize(path)] = tag;
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

            foreach (var (path, tag) in _tags)
                data.Tags[path] = tag.ToString();
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
