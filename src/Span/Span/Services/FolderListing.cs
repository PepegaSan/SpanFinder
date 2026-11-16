using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;
using Span.Helpers;
using Span.Models;

namespace Span.Services
{
    /// <summary>Ergebnis von <see cref="FolderListing.Enumerate"/>.</summary>
    internal sealed class FolderListingResult
    {
        public List<FolderItem> Folders { get; } = new();
        public List<FileItem> Files { get; } = new();

        /// <summary>
        /// Fingerabdruck genau dieser Aufzählung — gleiche Regel wie <see cref="FolderListing.ComputeStamp"/>,
        /// damit der Spalten-Abgleich (MainWindow.ReconcileVisibleColumnsAsync) denselben Wert errechnet,
        /// solange sich der Ordner nicht ändert.
        /// </summary>
        public DirectoryStamp Stamp { get; internal set; }

        /// <summary>Die Aufzählung wurde abgebrochen; die Listen sind unvollständig.</summary>
        public bool Cancelled { get; internal set; }

        /// <summary>
        /// Fehler während der Aufzählung (Zugriff verweigert, Netzwerk weg, ...). Die bis dahin gelesenen
        /// Einträge bleiben in den Listen, wie beim früheren Ablauf.
        /// </summary>
        public Exception? Error { get; internal set; }
    }

    /// <summary>
    /// Liest einen Ordner (lokal oder UNC) in EINEM Durchlauf: Unterordner, Dateien und der
    /// Listen-Fingerabdruck entstehen aus denselben Aufzählungsdaten.
    ///
    /// Vorher lief jedes Laden dreimal über das Verzeichnis (Fingerabdruck, EnumerateDirectories,
    /// EnumerateFiles) und öffnete zusätzlich JEDEN Unterordner, nur um den Pfeil der Miller-Ansicht zu
    /// bestimmen. Gemessen: C:\Windows\WinSxS (33.500 Unterordner) 2.450 ms, davon 2.300 ms für diese Pfeile;
    /// derselbe Ordner in einem Durchlauf ohne sie: 63 ms. Die Pfeile werden jetzt nur noch für sichtbare
    /// Zeilen geprüft (<see cref="ProbeChildEntries"/> für die ersten Zeilen, der Rest beim Anzeigen).
    ///
    /// Kein WinUI-Bezug — wird von den Tests direkt mitkompiliert.
    /// </summary>
    internal static class FolderListing
    {
        /// <summary>
        /// Wie DirectoryInfo.Enumerate* ohne Angaben (EnumerationOptions.Compatible): nichts überspringen,
        /// Zugriffsfehler werfen. Der Standardkonstruktor würde versteckte und Systemeinträge auslassen
        /// und Zugriffsfehler verschlucken.
        /// </summary>
        private static readonly EnumerationOptions AllEntries = new()
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        /// <summary>Wie viele Prüfungen gleichzeitig laufen (Verzeichnis öffnen + ersten Eintrag lesen).</summary>
        private const int ProbeParallelism = 8;
        private static readonly SemaphoreSlim ProbeSlots = new(ProbeParallelism);

        /// <summary>
        /// Ordner aufzählen. Wirft nicht: Fehler landen in <see cref="FolderListingResult.Error"/>,
        /// ein Abbruch in <see cref="FolderListingResult.Cancelled"/>.
        /// </summary>
        /// <param name="ioPath">Pfad für die Dateisystem-API (bei langen Pfaden mit \\?\-Präfix).</param>
        public static FolderListingResult Enumerate(string ioPath, bool showHidden, CancellationToken ct = default)
        {
            var result = new FolderListingResult();
            var folders = result.Folders;
            var files = result.Files;

            int stampCount = 0;
            int stampHash = 0;
            long stampMaxWrite = 0;
            var extensions = new ExtensionPool();

            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            try
            {
                var entries = new FileSystemEnumerable<IFileSystemItem?>(
                    ioPath,
                    (ref FileSystemEntry entry) =>
                    {
                        var attrs = entry.Attributes;
                        if (FileVisibility.ShouldHide(attrs, showHidden))
                            return null;

                        var writeUtc = entry.LastWriteTimeUtc;
                        stampCount++;
                        stampHash ^= string.GetHashCode(entry.FileName, StringComparison.OrdinalIgnoreCase);
                        long writeTicks = writeUtc.UtcTicks;
                        if (writeTicks > stampMaxWrite)
                            stampMaxWrite = writeTicks;

                        var name = entry.FileName.ToString();
                        var path = LongPathHelper.StripPrefix(entry.ToFullPath());
                        bool hidden = (attrs & FileAttributes.Hidden) != 0;

                        if (entry.IsDirectory)
                        {
                            return new FolderItem
                            {
                                Name = name,
                                Path = path,
                                DateModified = writeUtc.LocalDateTime,
                                DateCreated = entry.CreationTimeUtc.LocalDateTime,
                                Attributes = attrs,
                                IsHidden = hidden,
                                MaybeHasCustomIcon = (attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0,
                                // Annahme, bis der Ordner geprüft ist (ProbeChildEntries bzw. beim Anzeigen der Zeile).
                                HasChildEntries = true,
                                ChildProbePending = true,
                            };
                        }

                        return new FileItem
                        {
                            Name = name,
                            Path = path,
                            Size = entry.Length,
                            DateModified = writeUtc.LocalDateTime,
                            DateCreated = entry.CreationTimeUtc.LocalDateTime,
                            Attributes = attrs,
                            FileType = extensions.Get(name),
                            IsHidden = hidden,
                        };
                    },
                    AllEntries);

                int seen = 0;
                foreach (var item in entries)
                {
                    if ((++seen & 0xFF) == 0 && ct.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        return result;
                    }

                    if (item is FolderItem folder)
                        folders.Add(folder);
                    else if (item is FileItem file)
                        files.Add(file);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            result.Stamp = new DirectoryStamp(stampCount, stampHash, stampMaxWrite);
            result.Cancelled = ct.IsCancellationRequested;
            return result;
        }

        /// <summary>
        /// Fingerabdruck eines Ordners (Anzahl, Namens-Hash, jüngste Schreibzeit) ohne die Einträge aufzubauen.
        /// Zählt genau die Einträge, die <see cref="Enumerate"/> auflistet. Wirft bei unzugänglichem Ordner.
        /// </summary>
        public static DirectoryStamp ComputeStamp(string ioPath, bool showHidden)
        {
            // Attribute und Zeitstempel stecken bereits in den Aufzählungsdaten — keine Syscalls je Eintrag.
            var entries = new FileSystemEnumerable<(bool Counted, int NameHash, long WriteTicks)>(
                ioPath,
                (ref FileSystemEntry entry) =>
                    FileVisibility.ShouldHide(entry.Attributes, showHidden)
                        ? (false, 0, 0L)
                        : (true,
                           string.GetHashCode(entry.FileName, StringComparison.OrdinalIgnoreCase),
                           entry.LastWriteTimeUtc.UtcTicks),
                AllEntries);

            int count = 0;
            int hash = 0;
            long maxWrite = 0;
            foreach (var (counted, nameHash, ticks) in entries)
            {
                if (!counted) continue;
                count++;
                hash ^= nameHash;
                if (ticks > maxWrite)
                    maxWrite = ticks;
            }

            return new DirectoryStamp(count, hash, maxWrite);
        }

        /// <summary>
        /// Hat der Ordner mindestens einen Eintrag? Öffnet das Verzeichnis und liest höchstens einen Eintrag.
        /// Unzugängliche Ordner gelten wie bisher als "mit Inhalt" (Pfeil bleibt sichtbar).
        /// </summary>
        public static bool HasAnyEntry(string ioPath)
        {
            try
            {
                using var enumerator = new FileSystemEnumerable<byte>(
                    ioPath, static (ref FileSystemEntry _) => 1, AllEntries).GetEnumerator();
                return enumerator.MoveNext();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Die übergebenen Ordner parallel prüfen, höchstens <paramref name="budgetMs"/> lang. Was bis dahin
        /// nicht fertig ist, bleibt als "ausstehend" markiert und wird beim Anzeigen der Zeile nachgeholt —
        /// ein langsames Laufwerk (kalte Festplatte, Netzwerk) hält die Liste so nicht mehr auf.
        /// </summary>
        public static void ProbeChildEntries(IReadOnlyList<FolderItem> folders, int budgetMs, CancellationToken ct = default,
            Func<string, bool>? probe = null)
        {
            int count = folders.Count;
            if (count == 0 || budgetMs <= 0 || ct.IsCancellationRequested) return;

            var clock = Stopwatch.StartNew();
            probe ??= HasAnyEntry;
            // Workers only write private result slots. After a timeout they must not
            // mutate models which may already be bound to XAML (no change notification).
            var paths = new string?[count];
            var results = new int[count]; // 0 = pending, 1 = empty, 2 = nonempty
            for (int i = 0; i < count; i++)
                if (folders[i].ChildProbePending) paths[i] = LongPathHelper.ForIo(folders[i].Path);

            // Die Arbeit läuft in einer eigenen Task, damit das Zeitlimit hart ist: ein hängender Aufruf
            // (toter Netzwerkpfad) blockiert Parallel.For bis zu seinem Ende, den Aufrufer aber nicht.
            var work = Task.Run(() =>
            {
                try
                {
                    Parallel.For(0, count,
                        new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism },
                        (i, state) =>
                        {
                            if (ct.IsCancellationRequested || clock.ElapsedMilliseconds >= budgetMs)
                            {
                                state.Stop();
                                return;
                            }
                            if (paths[i] == null || !ProbeSlots.Wait(0)) return;
                            try { Volatile.Write(ref results[i], probe(paths[i]!) ? 2 : 1); }
                            finally { ProbeSlots.Release(); }
                        });
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[FolderListing] ProbeChildEntries failed: {ex.Message}");
                }
            });

            try
            {
                work.Wait(Math.Max(1, budgetMs - (int)clock.ElapsedMilliseconds));
            }
            catch (AggregateException)
            {
                // Bereits in der Task protokolliert.
            }
            if (ct.IsCancellationRequested) return;
            for (int i = 0; i < count; i++)
            {
                int result = Volatile.Read(ref results[i]);
                if (result == 0) continue;
                folders[i].HasChildEntries = result == 2;
                folders[i].ChildProbePending = false;
            }
        }

        /// <summary>
        /// Erweiterung wie FileSystemInfo.Extension (ab dem letzten Punkt, sonst leer). Gleiche Erweiterungen
        /// teilen sich eine Zeichenkette: in einem Ordner mit 50.000 Fotos sonst 50.000 mal ".jpg".
        /// </summary>
        private sealed class ExtensionPool
        {
            private readonly Dictionary<int, string> _byHash = new();

            public string Get(string name)
            {
                int dot = name.LastIndexOf('.');
                if (dot < 0) return string.Empty;

                var span = name.AsSpan(dot);
                int hash = string.GetHashCode(span);
                if (_byHash.TryGetValue(hash, out var known) && span.SequenceEqual(known))
                    return known;

                var extension = span.ToString();
                _byHash[hash] = extension; // bei Hash-Kollision gewinnt die jüngste — das Ergebnis bleibt richtig
                return extension;
            }
        }
    }
}
