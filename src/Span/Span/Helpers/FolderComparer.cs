using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Span.Helpers
{
    /// <summary>Ein Eintrag für den Ordnervergleich (Name, Typ, Größe, Änderungszeit in UTC).</summary>
    public sealed record CompareEntry(string Name, bool IsDirectory, long Size, DateTime LastWriteUtc);

    /// <summary>
    /// Ergebnis eines Ordnervergleichs aus Sicht von "hier" (aktiver Bereich) gegen "dort" (anderer Bereich).
    /// Unterordner, die auf beiden Seiten existieren, gelten als gleich — ihr Inhalt wird nicht verglichen.
    /// </summary>
    public sealed class FolderCompareResult
    {
        /// <summary>Nur hier vorhanden (Dateien und Ordner).</summary>
        public List<string> OnlyHere { get; } = new();
        /// <summary>Auf beiden Seiten, hier neuer.</summary>
        public List<string> NewerHere { get; } = new();
        /// <summary>Gleich alt, aber unterschiedliche Größe — oder Datei auf der einen, Ordner auf der anderen Seite.</summary>
        public List<string> Different { get; } = new();
        /// <summary>Nur dort vorhanden.</summary>
        public List<string> OnlyThere { get; } = new();
        /// <summary>Auf beiden Seiten, dort neuer.</summary>
        public List<string> NewerThere { get; } = new();

        /// <summary>Was hier markiert wird: alles, was dort fehlt oder dort veraltet ist (bzw. abweicht).</summary>
        public IEnumerable<string> ToSelectHere => OnlyHere.Concat(NewerHere).Concat(Different);

        public bool IsIdentical => OnlyHere.Count == 0 && NewerHere.Count == 0 && Different.Count == 0
                                   && OnlyThere.Count == 0 && NewerThere.Count == 0;
    }

    /// <summary>
    /// Ordnervergleich wie in Total Commander ("Verzeichnisse synchronisieren light"):
    /// Dateien werden über den Namen (ohne Groß-/Kleinschreibung) zugeordnet und über Änderungszeit
    /// und Größe verglichen.
    /// </summary>
    public static class FolderComparer
    {
        /// <summary>FAT/exFAT speichern Zeiten auf 2 Sekunden genau — kleinere Abweichungen gelten als gleich.</summary>
        public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

        public static FolderCompareResult Compare(IEnumerable<CompareEntry> here, IEnumerable<CompareEntry> there)
        {
            var result = new FolderCompareResult();
            var thereByName = new Dictionary<string, CompareEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in there) thereByName[e.Name] = e;

            var hereNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in here)
            {
                hereNames.Add(h.Name);
                if (!thereByName.TryGetValue(h.Name, out var t))
                {
                    result.OnlyHere.Add(h.Name);
                    continue;
                }

                if (h.IsDirectory != t.IsDirectory)
                {
                    result.Different.Add(h.Name);
                    continue;
                }
                if (h.IsDirectory) continue;

                var delta = h.LastWriteUtc - t.LastWriteUtc;
                if (delta > TimeTolerance)
                    result.NewerHere.Add(h.Name);
                else if (delta < -TimeTolerance)
                    result.NewerThere.Add(h.Name);
                else if (h.Size != t.Size)
                    result.Different.Add(h.Name);
            }

            foreach (var t in thereByName.Values)
            {
                if (!hereNames.Contains(t.Name))
                    result.OnlyThere.Add(t.Name);
            }

            return result;
        }

        /// <summary>
        /// Liest die direkten Einträge eines lokalen Ordners. Ausgeblendete Einträge folgen der
        /// Einstellung "Versteckte Dateien anzeigen", damit nur verglichen wird, was auch sichtbar ist.
        /// </summary>
        public static List<CompareEntry> ReadFolder(string ioPath, bool showHidden)
        {
            var entries = new List<CompareEntry>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var info in new DirectoryInfo(ioPath).EnumerateFileSystemInfos("*", options))
            {
                if (FileVisibility.ShouldHide(info.Attributes, showHidden)) continue;
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new CompareEntry(
                    info.Name,
                    isDir,
                    isDir ? 0 : ((FileInfo)info).Length,
                    info.LastWriteTimeUtc));
            }
            return entries;
        }
    }
}
