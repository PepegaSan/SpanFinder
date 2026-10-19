using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Span.Helpers;

namespace Span.Services
{
    /// <summary>Gemerkte Ansicht eines Ordners. null = nicht festgelegt (Vorlage bzw. globaler Standard gilt).</summary>
    public sealed class FolderViewRecord
    {
        public string? SortBy { get; set; }
        public bool? SortAscending { get; set; }
        public string? GroupBy { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    /// <summary>
    /// Merkt Sortierung und Gruppierung pro Ordner (folder-views.json). Wird beim Laden eines Ordners im
    /// Hintergrund gelesen und bei Benutzeraktionen auf dem UI-Thread geschrieben — daher mit Sperre.
    /// Gespeichert wird verzögert (1 s), höchstens <see cref="MaxRecords"/> Ordner (die zuletzt benutzten).
    /// </summary>
    public sealed class FolderViewStore
    {
        public const int MaxRecords = 3000;

        private static FolderViewStore? s_default;

        /// <summary>Speicher unter %LOCALAPPDATA%\Span\folder-views.json.</summary>
        public static FolderViewStore Default => s_default ??= new FolderViewStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Span", "folder-views.json"));

        private readonly string _filePath;
        private readonly object _lock = new();
        private Dictionary<string, FolderViewRecord>? _records;
        private Timer? _saveTimer;

        public FolderViewStore(string filePath)
        {
            _filePath = filePath;
        }

        public static string NormalizeKey(string path)
        {
            var p = LongPathHelper.StripPrefix(path ?? string.Empty).Trim();
            if (p.Length > 3) p = p.TrimEnd('\\', '/');
            return p;
        }

        public FolderViewRecord? Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                EnsureLoaded();
                return _records!.TryGetValue(NormalizeKey(path), out var r) ? r : null;
            }
        }

        public void SetSort(string path, string sortBy, bool ascending)
            => Update(path, r => { r.SortBy = sortBy; r.SortAscending = ascending; });

        public void SetGroup(string path, string groupBy)
            => Update(path, r => r.GroupBy = groupBy);

        /// <summary>
        /// Ansicht für einen Ordner: gemerkte Werte vor Vorlage. Felder ohne Wert bleiben null (= globaler Standard).
        /// </summary>
        public FolderViewResolution Resolve(string path, FolderKind kind)
        {
            var template = FolderViewTemplates.For(kind);
            var record = Get(path);
            if (record == null) return template;

            return template with
            {
                SortBy = record.SortBy ?? template.SortBy,
                SortAscending = record.SortBy != null ? record.SortAscending : template.SortAscending,
                GroupBy = record.GroupBy ?? template.GroupBy,
            };
        }

        private void Update(string path, Action<FolderViewRecord> change)
        {
            if (string.IsNullOrEmpty(path) || path == "PC") return;
            lock (_lock)
            {
                EnsureLoaded();
                var key = NormalizeKey(path);
                if (!_records!.TryGetValue(key, out var record))
                {
                    record = new FolderViewRecord();
                    _records[key] = record;
                }
                change(record);
                record.LastUsedUtc = DateTime.UtcNow;

                if (_records.Count > MaxRecords)
                {
                    foreach (var old in _records.OrderBy(kv => kv.Value.LastUsedUtc).Take(_records.Count - MaxRecords).ToList())
                        _records.Remove(old.Key);
                }

                _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _saveTimer.Change(1000, Timeout.Infinite);
            }
        }

        /// <summary>Sofort speichern (Timer-Rückruf; auch für Tests).</summary>
        public void Flush()
        {
            string json;
            lock (_lock)
            {
                if (_records == null) return;
                json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true });
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FolderViewStore] save failed: {ex.Message}");
            }
        }

        private void EnsureLoaded()
        {
            if (_records != null) return;
            _records = new Dictionary<string, FolderViewRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_filePath)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, FolderViewRecord>>(File.ReadAllText(_filePath));
                if (loaded == null) return;
                foreach (var kv in loaded)
                {
                    if (kv.Value != null) _records[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FolderViewStore] load failed: {ex.Message}");
            }
        }
    }
}
