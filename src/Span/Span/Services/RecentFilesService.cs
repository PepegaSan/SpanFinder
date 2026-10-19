using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Span.Services
{
    /// <summary>
    /// Eine zuletzt verwendete Datei (Windows-Liste "Zuletzt verwendet").
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
    /// geöffnet wurden. Ist "Zuletzt geöffnete Elemente anzeigen" in Windows aus, ist der Ordner leer — dann
    /// bleibt auch die Liste leer (dieselbe Datenschutz-Einstellung wie im Explorer).
    /// Ordner-Verknüpfungen und nicht mehr vorhandene lokale Dateien werden übersprungen.
    /// </summary>
    public static class RecentFilesService
    {
        public static Task<List<RecentFileItem>> LoadAsync(int max = 20)
            => Task.Run(() => Load(Environment.GetFolderPath(Environment.SpecialFolder.Recent), max));

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
}
