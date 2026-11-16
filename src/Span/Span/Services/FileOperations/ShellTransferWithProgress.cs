using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Span.Services.FileOperations;

/// <summary>
/// Lokales Kopieren und Verschieben über die Kopier-Engine der Shell (IFileOperation) — dieselbe Engine, die der
/// Windows Explorer benutzt. Gegenüber der früheren eigenen Kopierschleife bringt sie mit:
///   - Ordner werden bei "Ersetzen" zusammengeführt statt vorher gelöscht (vorher verschwand bei einem Ordner-
///     konflikt der gesamte Zielordner samt aller nicht betroffenen Dateien, ohne Papierkorb),
///   - Attribute, Erstelldatum, Alternate Data Streams und Zeitstempel bleiben erhalten (die Stream-Kopie
///     verlor sie für Dateien über 1 MB), überschriebene Dateien wandern in den Papierkorb (FOF_ALLOWUNDO),
///   - Abbruch räumt die halb geschriebene Zieldatei weg, Junctions werden wie im Explorer behandelt,
///   - Verschieben über Laufwerksgrenzen funktioniert auch beim Rückgängigmachen.
///
/// Es wird keine Shell-Oberfläche angezeigt (FOF_SILENT | FOF_NOERRORUI): Fortschritt, Pause, Abbruch und Fehler
/// laufen wie beim Löschen über den eigenen Fortschritts-Sink; das Fenster behält den Fokus. Der Drag-&-Drop-
/// Quellpfad (Ziehen in andere Programme) ist nicht betroffen — dort führt das Zielprogramm die Operation aus.
/// Remote-Pfade (FTP/SFTP) und der Fall "Engine nicht erzeugbar" bleiben beim bisherigen Code.
///
/// COM: wie <see cref="ShellDeleteWithProgress"/> auf einem eigenen STA-Thread; die COM-Deklarationen kommen von dort.
/// </summary>
internal static class ShellTransferWithProgress
{
    internal enum Kind { Copy, Move }

    /// <summary>Ein zu übertragendes Element. NewName ≠ null: unter diesem Namen am Ziel anlegen ("Beide behalten").</summary>
    internal sealed record Item(string SourcePath, string? NewName);

    /// <summary>Ergebnis je angefordertem Element. HResult ≥ 0 = Erfolg; DestinationPath meldet die Shell (null bei Fehler).</summary>
    internal sealed record ItemResult(string SourcePath, string? DestinationPath, int HResult)
    {
        public bool Succeeded => HResult >= 0;
    }

    internal sealed record ProgressInfo(int Percent, string CurrentName, int CompletedItems);

    /// <summary>
    /// Gesamtergebnis. EngineUnavailable: IFileOperation konnte nicht erzeugt werden — der Aufrufer nimmt den alten
    /// Kopierpfad. CreatedPaths: alle am Ziel angelegten Pfade (auch verschachtelte) für das Rückgängigmachen
    /// zusammengeführter Ordner. NestedFailures: fehlgeschlagene Elemente unterhalb der angeforderten Ordner.
    /// </summary>
    internal sealed record Outcome(
        bool EngineUnavailable,
        string? Error,
        bool Cancelled,
        IReadOnlyList<ItemResult> Items,
        IReadOnlyList<string> CreatedPaths,
        IReadOnlyList<(string Path, int HResult)> NestedFailures);

    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_NOCOPYSECURITYATTRIBS = 0x0800;
    private const uint FOF_NO_CONNECTED_ELEMENTS = 0x2000;

    private const int E_ABORT = unchecked((int)0x80004004);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int COPYENGINE_E_USER_CANCELLED = unchecked((int)0x80270000);
    private const int HRESULT_FILE_NOT_FOUND = unchecked((int)0x80070002);
    private const int HRESULT_PATH_NOT_FOUND = unchecked((int)0x80070003);

    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>
    /// Führt die Übertragung synchron auf einem STA-Thread aus. Von einem Hintergrund-Thread aufrufen (Task.Run).
    /// </summary>
    internal static Outcome Execute(
        Kind kind,
        IReadOnlyList<Item> items,
        string destinationDirectory,
        Action<ProgressInfo>? onProgress,
        ManualResetEventSlim? pauseEvent,
        CancellationToken ct)
    {
        Outcome outcome = new(true, "not started", false,
            Array.Empty<ItemResult>(), Array.Empty<string>(), Array.Empty<(string, int)>());

        var thread = new Thread(() => outcome = ExecuteCore(kind, items, destinationDirectory, onProgress, pauseEvent, ct));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        return outcome;
    }

    private static Outcome ExecuteCore(
        Kind kind,
        IReadOnlyList<Item> items,
        string destinationDirectory,
        Action<ProgressInfo>? onProgress,
        ManualResetEventSlim? pauseEvent,
        CancellationToken ct)
    {
        var created = new List<string>();
        var nestedFailures = new List<(string, int)>();
        ShellDeleteWithProgress.IFileOperation? op = null;
        ShellDeleteWithProgress.IShellItem? destFolder = null;
        uint cookie = 0;
        Sink? sink = null;

        try
        {
            object comObj;
            try
            {
                var clsid = new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"); // CLSID_FileOperation
                var iid = typeof(ShellDeleteWithProgress.IFileOperation).GUID;
                comObj = ShellDeleteWithProgress.CoCreateInstanceWrapper(clsid, iid);
            }
            catch (Exception ex)
            {
                return new Outcome(true, ex.Message, false, Array.Empty<ItemResult>(), created, nestedFailures);
            }
            op = (ShellDeleteWithProgress.IFileOperation)comObj;

            // FOF_NOCONFIRMATION: Rückfragen automatisch mit "Ja" beantworten — Kollisionen sind vom Aufrufer schon
            // aufgelöst (Überspringen herausgefiltert, "Beide behalten" mit eindeutigem Namen), nur "Ersetzen"
            // trifft absichtlich auf vorhandene Elemente (Dateien überschreiben, Ordner zusammenführen).
            // FOF_ALLOWUNDO: überschriebene Dateien in den Papierkorb statt endgültig weg.
            // FOF_NO_CONNECTED_ELEMENTS: keine "_files"-Begleitordner von HTML-Dateien mitziehen.
            uint flags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
                       | FOF_NO_CONNECTED_ELEMENTS | FOF_ALLOWUNDO;
            if (kind == Kind.Copy)
                flags |= FOF_NOCOPYSECURITYATTRIBS; // wie der Explorer: Kopien erben die ACL des Zielordners
            op.SetOperationFlags(flags);

            destFolder = ShellDeleteWithProgress.CreateShellItem(destinationDirectory);
            if (destFolder == null)
            {
                return new Outcome(false, $"Destination folder not found: {destinationDirectory}", false,
                    FailAll(items, HRESULT_PATH_NOT_FOUND), created, nestedFailures);
            }

            sink = new Sink(items, onProgress, pauseEvent, ct, created, nestedFailures);
            op.Advise(sink, out cookie);

            int queued = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;

                var src = ShellDeleteWithProgress.CreateShellItem(item.SourcePath);
                if (src == null)
                {
                    sink.MarkFailed(item.SourcePath, HRESULT_FILE_NOT_FOUND);
                    continue;
                }

                try
                {
                    if (kind == Kind.Copy)
                        op.CopyItem(src, destFolder, item.NewName, null);
                    else
                        op.MoveItem(src, destFolder, item.NewName, null);
                    queued++;
                }
                finally
                {
                    Marshal.ReleaseComObject(src);
                }
            }

            if (ct.IsCancellationRequested)
                return new Outcome(false, null, true, sink.Finish(cancelled: true), created, nestedFailures);

            if (queued > 0)
                op.PerformOperations();

            bool cancelled = sink.Cancelled || ct.IsCancellationRequested;
            return new Outcome(false, null, cancelled, sink.Finish(cancelled), created, nestedFailures);
        }
        catch (COMException ex) when (ex.HResult == E_ABORT || ex.HResult == COPYENGINE_E_USER_CANCELLED)
        {
            return new Outcome(false, null, true,
                sink?.Finish(cancelled: true) ?? FailAll(items, COPYENGINE_E_USER_CANCELLED), created, nestedFailures);
        }
        catch (Exception ex)
        {
            // PerformOperations meldet einen Elementfehler auch als HRESULT des Aufrufs. Liegen Element-Ergebnisse
            // vor, tragen die die Fehlermeldung — sonst stünde dieselbe Meldung doppelt in der Liste.
            bool cancelled = ct.IsCancellationRequested;
            bool hasItemFailures = sink?.HasRecordedFailures == true;
            return new Outcome(false, cancelled || hasItemFailures ? null : ex.Message, cancelled,
                sink?.Finish(cancelled) ?? FailAll(items, ex.HResult), created, nestedFailures);
        }
        finally
        {
            try { if (op != null && cookie != 0) op.Unadvise(cookie); } catch { }
            try { if (destFolder != null) Marshal.ReleaseComObject(destFolder); } catch { }
            try { if (op != null) Marshal.ReleaseComObject(op); } catch { }
        }
    }

    private static List<ItemResult> FailAll(IReadOnlyList<Item> items, int hr)
    {
        var list = new List<ItemResult>(items.Count);
        foreach (var item in items)
            list.Add(new ItemResult(item.SourcePath, null, hr));
        return list;
    }

    /// <summary>Lesbarer Text für einen Shell-/Win32-Fehlercode (COPYENGINE_E_* kennt .NET nicht).</summary>
    internal static string DescribeHResult(int hr)
    {
        switch (unchecked((uint)hr))
        {
            case 0x80270000:
            case 0x80270001: return "cancelled";
            case 0x80270002: return "requires elevation";
            case 0x80270003: return "source and destination are the same file";
            case 0x80270009: return "destination is a subfolder of the source";
            case 0x8027000D: return "file is too large for the destination file system";
            case 0x80270021: return "access denied (source)";
            case 0x80270022: return "access denied (destination)";
            case 0x80270023: return "path not found (source)";
            case 0x80270024: return "path not found (destination)";
            case 0x80270027: return "file is in use by another process (source)";
            case 0x80270028: return "file is in use by another process (destination)";
            case 0x80270032:
            case 0x80270033: return "not enough disk space";
        }

        try
        {
            var ex = Marshal.GetExceptionForHR(hr);
            if (ex != null && !string.IsNullOrEmpty(ex.Message) && !ex.Message.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase))
                return ex.Message;
        }
        catch { }
        return $"0x{hr:X8}";
    }

    private static string NormalizeKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    /// <summary>
    /// Fortschritts-Sink: Fortschritt und aktueller Name (gedrosselt), Pause und Abbruch (E_ABORT wie beim Löschen),
    /// Ergebnis je angefordertem Element, angelegte Pfade und Fehler verschachtelter Elemente.
    /// </summary>
    private sealed class Sink : ShellDeleteWithProgress.IFileOperationProgressSink
    {
        private const long ReportIntervalMs = 100;
        private const int MaxNestedFailures = 50;

        private readonly IReadOnlyList<Item> _items;
        private readonly Dictionary<string, int> _indexBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly ItemResult?[] _results;
        private readonly Action<ProgressInfo>? _onProgress;
        private readonly ManualResetEventSlim? _pauseEvent;
        private readonly CancellationToken _ct;
        private readonly List<string> _created;
        private readonly List<(string, int)> _nestedFailures;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private long _lastReportMs = -1000;
        private int _lastPercent = -1;
        private int _completed;

        internal bool Cancelled { get; private set; }
        internal bool HasRecordedFailures { get; private set; }

        internal Sink(IReadOnlyList<Item> items, Action<ProgressInfo>? onProgress, ManualResetEventSlim? pauseEvent,
            CancellationToken ct, List<string> created, List<(string, int)> nestedFailures)
        {
            _items = items;
            _results = new ItemResult?[items.Count];
            _onProgress = onProgress;
            _pauseEvent = pauseEvent;
            _ct = ct;
            _created = created;
            _nestedFailures = nestedFailures;
            for (int i = 0; i < items.Count; i++)
                _indexBySource.TryAdd(NormalizeKey(items[i].SourcePath), i);
        }

        internal void MarkFailed(string sourcePath, int hr)
        {
            if (_indexBySource.TryGetValue(NormalizeKey(sourcePath), out int idx))
            {
                _results[idx] = new ItemResult(_items[idx].SourcePath, null, hr);
                HasRecordedFailures = true;
            }
        }

        internal IReadOnlyList<ItemResult> Finish(bool cancelled)
        {
            var list = new List<ItemResult>(_items.Count);
            for (int i = 0; i < _items.Count; i++)
                list.Add(_results[i] ?? new ItemResult(_items[i].SourcePath, null, cancelled ? COPYENGINE_E_USER_CANCELLED : E_FAIL));
            return list;
        }

        /// <summary>Abbruch- und Pausenpunkt — bei jedem Rückruf der Engine.</summary>
        private int Gate()
        {
            if (_ct.IsCancellationRequested) { Cancelled = true; return E_ABORT; }
            if (_pauseEvent != null)
            {
                FileOperationHelpers.WaitIfPaused(_pauseEvent, _ct);
                if (_ct.IsCancellationRequested) { Cancelled = true; return E_ABORT; }
            }
            return 0;
        }

        private bool ShouldReport()
        {
            long now = _sw.ElapsedMilliseconds;
            if (now - _lastReportMs < ReportIntervalMs) return false;
            _lastReportMs = now;
            return true;
        }

        private int PreItem(ShellDeleteWithProgress.IShellItem? psiItem)
        {
            int abort = Gate();
            if (abort != 0) return abort;

            try
            {
                if (_onProgress != null && psiItem != null && ShouldReport())
                {
                    var name = GetName(psiItem, SIGDN_PARENTRELATIVEPARSING);
                    if (!string.IsNullOrEmpty(name))
                        _onProgress(new ProgressInfo(_lastPercent < 0 ? 0 : _lastPercent, name, _completed));
                }
            }
            catch { /* nur Anzeige */ }
            return 0;
        }

        private int PostItem(ShellDeleteWithProgress.IShellItem? psiItem, int hr, ShellDeleteWithProgress.IShellItem? newItem)
        {
            try
            {
                var source = psiItem != null ? GetName(psiItem, SIGDN_FILESYSPATH) : null;
                var dest = newItem != null ? GetName(newItem, SIGDN_FILESYSPATH) : null;

                if (hr >= 0 && !string.IsNullOrEmpty(dest))
                    _created.Add(dest);

                if (source != null && _indexBySource.TryGetValue(NormalizeKey(source), out int idx))
                {
                    // Angefordertes Element: erstes Ergebnis zählt, ein späterer Fehler überschreibt einen Erfolg.
                    if (_results[idx] == null || (hr < 0 && _results[idx]!.HResult >= 0))
                        _results[idx] = new ItemResult(_items[idx].SourcePath, dest, hr);
                    if (hr < 0) HasRecordedFailures = true;
                    _completed++;
                }
                else if (hr < 0 && source != null && _nestedFailures.Count < MaxNestedFailures)
                {
                    _nestedFailures.Add((source, hr));
                    HasRecordedFailures = true;
                }
            }
            catch { /* Buchführung darf die Engine nicht stören */ }
            return 0;
        }

        private static string? GetName(ShellDeleteWithProgress.IShellItem item, uint sigdn)
        {
            try
            {
                item.GetDisplayName(sigdn, out IntPtr ptr);
                if (ptr == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            catch { return null; }
        }

        public int StartOperations() => Gate();
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, string pszNewName) => Gate();
        public int PostRenameItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, string pszNewName, int hrRename, ShellDeleteWithProgress.IShellItem psiNewlyCreated) => 0;
        public int PreMoveItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName) => PreItem(psiItem);
        public int PostMoveItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName, int hrMove, ShellDeleteWithProgress.IShellItem psiNewlyCreated) => PostItem(psiItem, hrMove, psiNewlyCreated);
        public int PreCopyItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName) => PreItem(psiItem);
        public int PostCopyItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName, int hrCopy, ShellDeleteWithProgress.IShellItem psiNewlyCreated) => PostItem(psiItem, hrCopy, psiNewlyCreated);
        public int PreDeleteItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem) => Gate();
        public int PostDeleteItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiItem, int hrDelete, ShellDeleteWithProgress.IShellItem psiNewlyCreated) => 0;
        public int PreNewItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName) => 0;
        public int PostNewItem(uint dwFlags, ShellDeleteWithProgress.IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, ShellDeleteWithProgress.IShellItem psiNewItem) => 0;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
            int abort = Gate();
            if (abort != 0) return abort;

            if (iWorkTotal > 0 && _onProgress != null)
            {
                int pct = (int)Math.Min(100, iWorkSoFar * 100.0 / iWorkTotal);
                if (pct != _lastPercent && ShouldReport())
                {
                    _lastPercent = pct;
                    _onProgress(new ProgressInfo(pct, string.Empty, _completed));
                }
            }
            return 0;
        }

        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
