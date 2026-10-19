using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Span.Services
{
    /// <summary>
    /// Eine zuletzt verwendete Datei (Windows-Liste "Zuletzt verwendet" oder Span's eigene Liste).
    /// Anzeige-Eigenschaften mit WinUI-Typen stehen in RecentFileItem.WinUI.cs.
    /// </summary>
    public sealed partial class RecentFileItem
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        /// <summary>Ordner der Datei (Anzeige).</summary>
        public string Folder { get; init; } = string.Empty;
        public DateTime LastUsed { get; init; }

        public string LastUsedText => LastUsed.ToString("g");
    }

    /// <summary>
    /// Liest die zuletzt verwendeten Dateien wie die Startseite des Explorers: aus dem Windows-Ordner
    /// "Zuletzt verwendet" (%APPDATA%\Microsoft\Windows\Recent), also auch Dateien, die mit anderen Programmen
    /// geöffnet wurden, zusammen mit Span's eigener Liste (<see cref="SpanRecentFiles"/>). Ist "Zuletzt geöffnete
    /// Elemente anzeigen" in Windows aus, ist der Windows-Ordner leer — die aus Span geöffneten Dateien stehen trotzdem da.
    /// Ordner-Verknüpfungen und nicht mehr vorhandene lokale Dateien werden übersprungen.
    /// </summary>
    public static class RecentFilesService
    {
        public static Task<List<RecentFileItem>> LoadAsync(int max = 20)
            => Task.Run(() => Merge(
                SpanRecentFiles.Default.Load(max),
                Load(Environment.GetFolderPath(Environment.SpecialFolder.Recent), max),
                max));

        /// <summary>Beide Listen vereinen: je Datei der jüngste Eintrag, neueste zuerst.</summary>
        public static List<RecentFileItem> Merge(IEnumerable<RecentFileItem> first, IEnumerable<RecentFileItem> second, int max)
            => first.Concat(second)
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => i.LastUsed).First())
                .OrderByDescending(i => i.LastUsed)
                .Take(max)
                .ToList();

        public static List<RecentFileItem> Load(string recentDir, int max)
        {
            var result = new List<RecentFileItem>();
            try
            {
                if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return result;

                var links = new DirectoryInfo(recentDir).EnumerateFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(max * 4); // Ordner und gelöschte Dateien fallen heraus
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var link in links)
                {
                    var target = Helpers.ShellLinkReader.GetTarget(link.FullName);
                    if (string.IsNullOrEmpty(target) || !seen.Add(target)) continue;

                    // Netzwerkpfade nicht prüfen (getrennte Freigaben würden blockieren), lokale schon
                    bool isUnc = target.StartsWith(@"\\", StringComparison.Ordinal);
                    if (!isUnc && !File.Exists(target)) continue; // Ordner oder gelöscht

                    result.Add(new RecentFileItem
                    {
                        Name = System.IO.Path.GetFileName(target),
                        Path = target,
                        Folder = System.IO.Path.GetDirectoryName(target) ?? string.Empty,
                        LastUsed = link.LastWriteTime,
                    });
                    if (result.Count >= max) break;
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[RecentFiles] load failed: {ex.Message}");
            }
            return result;
        }
    }

    /// <summary>
    /// Span's eigene Liste der zuletzt geöffneten Dateien (recent-files.json), unabhängig davon, ob Windows
    /// zuletzt verwendete Elemente aufzeichnet. Erfasst wird beim Öffnen einer Datei aus Span; neueste zuerst,
    /// höchstens <see cref="MaxEntries"/>. Wird aus Hintergrund-Threads gelesen und geschrieben — daher mit Sperre.
    /// </summary>
    public sealed class SpanRecentFiles
    {
        public const int MaxEntries = 50;

        private static SpanRecentFiles? s_default;

        /// <summary>Speicher unter %LOCALAPPDATA%\Span\recent-files.json.</summary>
        public static SpanRecentFiles Default => s_default ??= new SpanRecentFiles(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Span", "recent-files.json"));

        // Programme und Verknüpfungen gehören nicht in "Zuletzt verwendet" (der Explorer führt sie dort auch nicht)
        private static readonly HashSet<string> s_skippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".msi", ".lnk", ".url", ".appref-ms",
        };

        private sealed class Entry
        {
            public string Path { get; set; } = string.Empty;
            public DateTime LastUsedUtc { get; set; }
        }

        private readonly string _filePath;
        private readonly object _lock = new();
        private List<Entry>? _entries;

        public SpanRecentFiles(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Gehört die Datei in die Liste? Nicht: Programme/Verknüpfungen und Dateien im Temp-Ordner
        /// (z. B. aus einem Archiv zur Vorschau entpackt).
        /// </summary>
        public static bool IsTrackable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (s_skippedExtensions.Contains(System.IO.Path.GetExtension(path))) return false;
            var temp = System.IO.Path.GetTempPath();
            return !path.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }

        public void Record(string path) => Record(path, DateTime.UtcNow);

        public void Record(string path, DateTime usedUtc)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_lock)
            {
                EnsureLoaded();
                _entries!.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                _entries.Insert(0, new Entry { Path = path, LastUsedUtc = usedUtc });
                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
                Save();
            }
        }

        /// <summary>Vorhandene Dateien, neueste zuerst. Netzwerkpfade werden nicht geprüft (getrennte Freigaben blockieren).</summary>
        public List<RecentFileItem> Load(int max)
        {
            List<Entry> snapshot;
            lock (_lock)
            {
                EnsureLoaded();
                snapshot = _entries!.OrderByDescending(e => e.LastUsedUtc).ToList();
            }

            var result = new List<RecentFileItem>();
            foreach (var entry in snapshot)
            {
                bool isUnc = entry.Path.StartsWith(@"\\", StringComparison.Ordinal);
                if (!isUnc && !File.Exists(entry.Path)) continue;
                result.Add(new RecentFileItem
                {
                    Name = System.IO.Path.GetFileName(entry.Path),
                    Path = entry.Path,
                    Folder = System.IO.Path.GetDirectoryName(entry.Path) ?? string.Empty,
                    LastUsed = entry.LastUsedUtc.ToLocalTime(),
                });
                if (result.Count >= max) break;
            }
            return result;
        }

        private void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new List<Entry>();
            try
            {
                if (!File.Exists(_filePath)) return;
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_filePath));
                if (loaded != null)
                    _entries.AddRange(loaded.Where(e => !string.IsNullOrWhiteSpace(e.Path)).Take(MaxEntries));
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[SpanRecentFiles] load failed: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(_entries));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[SpanRecentFiles] save failed: {ex.Message}");
            }
        }
    }
}
