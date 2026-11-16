using Span.Models;
using System.Text.Json;

namespace Span.Services;

/// <summary>
/// 파일 조작 이력 서비스 구현. JSON 파일로 영속화하며, FIFO 방식으로 최대 1000개 항목 유지.
/// 로그 기록은 스레드풀에서 비동기로 디스크에 저장된다.
///
/// Schreibvorgänge werden gebündelt und serialisiert: ein Vorgang mit N Elementen ruft LogOperation N-mal auf,
/// und vorher startete jeder Aufruf ein eigenes Task.Run(SaveToFile) auf dieselbe Datei — IOException oder
/// zerrissenes JSON, das beim nächsten Start die ganze Historie verwarf.
/// </summary>
public class ActionLogService : IActionLogService
{
    private const int MaxEntries = 1000;
    private const int SaveDelayMs = 150;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _logFilePath;
    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private List<ActionLogEntry> _entries = new();
    private bool _loaded = false;
    private int _saveScheduled;

    public ActionLogService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SPAN Finder");
        Directory.CreateDirectory(appDataDir);
        _logFilePath = Path.Combine(appDataDir, "action_log.json");
    }

    public void LogOperation(ActionLogEntry entry)
    {
        entry.Timestamp = DateTime.Now;

        lock (_lock)
        {
            EnsureLoaded();
            _entries.Add(entry);

            // FIFO: trim to max entries
            if (_entries.Count > MaxEntries)
                _entries = _entries.Skip(_entries.Count - MaxEntries).ToList();
        }

        ScheduleSave();
    }

    public List<ActionLogEntry> GetEntries(int count = 50)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries.TakeLast(count).Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        ScheduleSave();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (File.Exists(_logFilePath))
            {
                var json = File.ReadAllText(_logFilePath);
                _entries = JsonSerializer.Deserialize<List<ActionLogEntry>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            Helpers.DebugLogger.Log($"[ActionLogService] Load error: {ex.Message}");
            _entries = new();
        }
    }

    /// <summary>Einen Schreibvorgang einplanen; weitere Einträge innerhalb der Verzögerung fahren mit.</summary>
    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SaveDelayMs).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _saveScheduled, 0); }
            SaveToFile();
        });
    }

    private void SaveToFile()
    {
        try
        {
            List<ActionLogEntry> snapshot;
            lock (_lock)
            {
                snapshot = new List<ActionLogEntry>(_entries);
            }

            var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
            lock (_fileLock)
            {
                // Erst .tmp, dann atomar ersetzen — ein Kill mitten im Schreiben hinterließ sonst eine abgeschnittene
                // Datei, die beim nächsten Start nicht mehr lesbar war.
                var tmp = _logFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _logFilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Helpers.DebugLogger.Log($"[ActionLogService] Save error: {ex.Message}");
        }
    }
}
