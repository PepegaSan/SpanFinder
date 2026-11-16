using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Span.Helpers;
using Span.Services;

namespace Span.ViewModels
{
    /// <summary>
    /// Prüft im Hintergrund, ob Ordner Inhalt haben (Pfeil der Miller-Ansicht), für Zeilen, die gerade
    /// sichtbar werden. Die Ergebnisse gehen gebündelt an den UI-Thread — ein Dispatcher-Aufruf für viele
    /// Zeilen statt einem je Zeile.
    ///
    /// Das Laden eines Ordners prüft nur noch die ersten Zeilen (FolderViewModel.ProbeLeadingFolders);
    /// alles Weitere landet hier, sobald die Zeile erscheint. Vorher öffnete jedes Laden jeden Unterordner.
    /// </summary>
    internal static class ChildEntryProbeQueue
    {
        private static readonly ConcurrentQueue<(FolderViewModel Folder, string Path, int Version, DispatcherQueue? Dispatcher)> s_pending = new();
        private static int s_workers;

        /// <summary>Gleichzeitige Prüfungen. Mehr bringt auf Festplatten nichts und stört das eigentliche Laden.</summary>
        private const int MaxWorkers = 4;

        /// <summary>So viele Ergebnisse gehen höchstens in einem Dispatcher-Aufruf an den UI-Thread.</summary>
        private const int BatchSize = 64;

        /// <summary>Spätestens nach dieser Zeit wird ein angefangener Stapel ausgeliefert.</summary>
        private const int BatchMaxAgeMs = 40;

        public static void Enqueue(FolderViewModel folder, string path, int version, DispatcherQueue? dispatcher)
        {
            s_pending.Enqueue((folder, path, version, dispatcher));
            TryStartWorker();
        }

        private static void TryStartWorker()
        {
            while (true)
            {
                int active = Volatile.Read(ref s_workers);
                if (active >= MaxWorkers) return;
                if (Interlocked.CompareExchange(ref s_workers, active + 1, active) == active) break;
            }

            _ = Task.Run(Work);
        }

        private static void Work()
        {
            try
            {
                var batch = new List<(FolderViewModel Folder, string Path, int Version, bool HasChildEntries)>();
                DispatcherQueue? batchDispatcher = null;
                long batchStarted = Environment.TickCount64;

                while (s_pending.TryDequeue(out var entry))
                {
                    bool hasChildEntries = FolderListing.HasAnyEntry(LongPathHelper.ForIo(entry.Path));

                    if (batch.Count > 0 && !ReferenceEquals(batchDispatcher, entry.Dispatcher))
                        Flush(ref batch, batchDispatcher);

                    if (batch.Count == 0)
                        batchStarted = Environment.TickCount64;
                    batchDispatcher = entry.Dispatcher;
                    batch.Add((entry.Folder, entry.Path, entry.Version, hasChildEntries));

                    if (batch.Count >= BatchSize || Environment.TickCount64 - batchStarted >= BatchMaxAgeMs)
                        Flush(ref batch, batchDispatcher);
                }

                Flush(ref batch, batchDispatcher);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ChildEntryProbeQueue] worker failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref s_workers);

                // Ein Eintrag kann zwischen dem letzten TryDequeue und dem Abmelden gekommen sein.
                if (!s_pending.IsEmpty)
                    TryStartWorker();
            }
        }

        private static void Flush(ref List<(FolderViewModel Folder, string Path, int Version, bool HasChildEntries)> batch, DispatcherQueue? dispatcher)
        {
            if (batch.Count == 0) return;

            var results = batch;
            batch = new List<(FolderViewModel Folder, string Path, int Version, bool HasChildEntries)>();

            // Ohne Dispatcher (Fenster schon geschlossen) gibt es niemanden mehr, der den Pfeil sieht.
            if (dispatcher == null) return;

            dispatcher.TryEnqueue(() =>
            {
                foreach (var (folder, path, version, hasChildEntries) in results)
                {
                    try { folder.ApplyChildProbeResult(path, version, hasChildEntries); }
                    catch (Exception ex) { DebugLogger.Log($"[ChildEntryProbeQueue] apply failed: {ex.Message}"); }
                }
            });
        }
    }
}
