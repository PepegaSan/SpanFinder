using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Span.Helpers;

namespace Span.Services.Thumbnails;

/// <summary>
/// 메인 측 썸네일 클라이언트 — 워커 풀 (Phase 2: 2개) + 캐시 라우팅 + 폴백.
///
/// API:
///   var uri = await client.GetThumbnailUriAsync(filePath, size, ...);
///   if (uri != null) bitmap.UriSource = uri;  // 캐시 hit 또는 워커 생성
///   else { 기존 인프로세스 경로 폴백 }
///
///   await client.CancelBatchAsync();  // 폴더 이동 시 진행 중 모든 요청 무효화
///
/// Phase 2:
/// - 워커 풀 2개 + 라운드로빈 (P2-1)
/// - 워커 죽음 감지 + 백오프 재spawn 1s/3s/10s (P2-2)
/// - Cancel batch (P2-3)
/// - 폴더 단위 폴백 (P2-5)
/// - 적응형 타임아웃 cold 7s / warm 3s (P2-6)
///
/// Fehlerpolitik (überarbeitet): Drei verschiedene Dinge wurden früher in einem Zähler mit hartem Sitzungs-
/// Abschaltpunkt (3) vermengt — eine langsame Datei, ein toter/hängender Worker, eine kaputte Umgebung.
/// In der Praxis schaltete sich das Feature nach wenigen Minuten normalen Blätterns selbst ab.
///   - langsame Anfrage (Timeout, Worker lebt): nur diese Id wird abgebrochen, der Worker bleibt;
///     erst mehrere Timeouts in Folge gelten als hängender Worker (Neustart mit Backoff, zählt nicht zur Sitzung)
///   - toter Worker / fehlgeschlagener Spawn: Neustart mit Backoff, zählt zur Sitzung
///   - jede erfolgreiche Antwort setzt den Sitzungszähler zurück
/// </summary>
public sealed class ThumbnailClientService : IDisposable
{
    private const int PoolSize = 2;
    private const int FolderFailureThreshold = 3;       // 같은 폴더 N회 실패 시 해당 폴더 인프로세스 폴백
    // v1.4.5: 이슈 #23 최종 수정 — threshold 20→3으로 대폭 축소.
    // .NET 런타임 누락 같은 영구적 실패는 재시도해도 절대 성공 불가 →
    // 매 spawn마다 Process/Pipe/Stream 객체 수백 개가 파이널라이저 큐로 적체 →
    // D3D surface 회수 지연 → WinUI composition 크래시 (STATUS_STOWED_EXCEPTION).
    // 3회면 충분히 환경 문제임을 확정할 수 있음.
    private const int SessionFailureThreshold = 3;     // 누적 실패 N회 → 세션 동안 영구 비활성
    /// <summary>Timeouts in Folge auf demselben lebenden Worker, ab denen er als hängend gilt und neu gestartet wird.</summary>
    private const int HungWorkerTimeoutThreshold = 3;

    private readonly object _lock = new();
    private readonly ThumbnailDiskCache _cache = new();
    private readonly JobObjectHelper? _jobObject;  // I7: null이면 격리 비활성
    private readonly WorkerProcess?[] _workers = new WorkerProcess?[PoolSize];
    /// <summary>Laufender Spawn je Slot — gleichzeitige Aufrufer hängen sich an denselben Task.</summary>
    private readonly Task<WorkerProcess?>?[] _spawning = new Task<WorkerProcess?>?[PoolSize];
    private readonly int[] _workerFailures = new int[PoolSize];
    private readonly int[] _consecutiveTimeouts = new int[PoolSize];
    private readonly DateTime[] _nextRetryAt = new DateTime[PoolSize];
    private readonly bool[] _workerWarm = new bool[PoolSize];
    private int _roundRobinIndex;
    private readonly string _workerExePath;
    private bool _spawnAttempted;
    // C3: cross-thread visibility
    private volatile bool _disabled;
    private int _totalFailures;
    private long _idCounter;

    // 폴더 단위 폴백 (P2-5) — 동일 폴더에서 N회 연속 실패 시 해당 폴더 일시 차단
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _folderFailureCount = new();

    public ThumbnailClientService()
    {
        // I7: JobObject 생성 실패가 앱 시작 막지 않도록 try/catch
        try { _jobObject = new JobObjectHelper(); }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ThumbnailClient] JobObject init failed → 격리 비활성: {ex.Message}");
            _jobObject = null;
            _disabled = true;
        }

        var baseDir = AppContext.BaseDirectory;
        _workerExePath = Path.Combine(baseDir, "Span.Thumbs.exe");
        if (!File.Exists(_workerExePath))
        {
            var alt = Path.Combine(baseDir, "Span.Thumbs", "Span.Thumbs.exe");
            if (File.Exists(alt)) _workerExePath = alt;
        }

        // 시작 시 캐시 정리 (백그라운드, 자기 예외 흡수)
        _ = Task.Run(() =>
        {
            try { _cache.CleanupOldEntries(); }
            catch (Exception ex) { DebugLogger.Log($"[ThumbnailClient] cache cleanup failed: {ex.Message}"); }
        });
    }

    private int _prewarmInFlight;  // I-N6: 중복 spawn 가드

    /// <summary>
    /// A2: feature flag ON 사용자에 한해 첫 워커 prewarm.
    /// 첫 폴더 진입 후 호출 권장 — cold start 5초 부담을 백그라운드로 숨김.
    /// 호출 안 해도 lazy spawn으로 동작 — 단지 첫 요청이 느림.
    ///
    /// C-N1: feature flag OFF 사용자에게 워커 spawn하지 않도록 가드.
    /// I-N6: 빠른 폴더 순환 시 Task.Run 누적 방지.
    /// </summary>
    public void PrewarmWorker()
    {
        if (_disabled) return;

        // C-N1: feature flag OFF면 워커 spawn 안 함 — 베타 미참여 사용자 회귀 방지
        try
        {
            var settings = App.Current.Services.GetService(typeof(SettingsService)) as SettingsService;
            if (settings == null || !settings.UseIsolatedThumbnails) return;
        }
        catch { return; }

        // 이미 살아있는 워커가 있으면 skip
        for (int i = 0; i < PoolSize; i++)
        {
            var w = System.Threading.Volatile.Read(ref _workers[i]);
            if (w != null && w.IsAlive) return;
        }

        // I-N6: spawn 진행 중이면 추가 Task.Run 누적 방지
        if (Interlocked.CompareExchange(ref _prewarmInFlight, 1, 0) != 0) return;

        // I-3R-2: Task.Run 큐잉 자체가 throw하면 (OOM/ThreadPool 고갈) finally 실행 안 됨
        // → _prewarmInFlight 영구 stuck 방지하기 위해 try-catch로 보호
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await EnsureWorkerAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { DebugLogger.Log($"[ThumbnailClient] prewarm failed: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _prewarmInFlight, 0); }
            });
        }
        catch (Exception ex)
        {
            // Task.Run 큐잉 실패 시 카운터 reset (다음 호출에서 재시도 가능)
            Interlocked.Exchange(ref _prewarmInFlight, 0);
            DebugLogger.Log($"[ThumbnailClient] prewarm queue failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 썸네일 요청 — 캐시 hit 시 즉시 경로 반환, miss 시 워커 호출.
    /// 실패 또는 워커 미동작 시 null → 호출자가 인프로세스 폴백.
    /// Abbruch (Aufrufer-Token oder CancelAllInflight) → OperationCanceledException; der Aufrufer fällt dann nicht zurück.
    /// </summary>
    public async Task<Uri?> GetThumbnailUriAsync(
        string filePath,
        int requestedSize,
        string mode,
        bool isCloudOnly,
        bool applyExif,
        string theme,
        uint dpi,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        if (_disabled) return null;

        // ── 1. 폴더 단위 폴백 검사 (P2-5) ──
        var parentDir = SafeGetDirectoryName(filePath);
        if (parentDir != null && _folderFailureCount.TryGetValue(parentDir, out var folderFails)
            && folderFails >= FolderFailureThreshold)
        {
            return null;  // 이 폴더는 인프로세스 폴백
        }

        // ── 2+3. 캐시 키 계산 + 캐시 hit ──
        // Stat der Quelldatei, Cache-Probe und LastAccess-Touch im Hintergrund: bis zum ersten echten await lief
        // das bisher auf dem aufrufenden UI-Thread (bei Netzlaufwerken ein Round-Trip je Thumbnail, dazu ein
        // Schreibzugriff je Cache-Treffer).
        string cachePath;
        bool cacheHit;
        try
        {
            (cachePath, cacheHit) = await Task.Run(() =>
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return (string.Empty, false);
                var cp = _cache.GetCachePath(
                    filePath, fi.Length, fi.LastWriteTimeUtc,
                    requestedSize, mode, theme, dpi, applyExif, isCloudOnly);
                if (!_cache.IsCached(cp)) return (cp, false);
                try { File.SetLastAccessTimeUtc(cp, DateTime.UtcNow); } catch { }
                return (cp, true);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        if (cachePath.Length == 0) return null;
        if (cacheHit) return new Uri(cachePath);

        // ── 4. 워커 보장 (라운드로빈) ──
        var worker = await EnsureWorkerAsync(ct).ConfigureAwait(false);
        if (worker == null) return null;

        // ── 5. 적응형 타임아웃 (P2-6) ──
        // cold start (warm 안 된 워커): 7초, warm: 3초
        // C2: 워커 인덱스는 0..PoolSize-1 보장 (Volatile.Read로 race 안전)
        var workerId = worker.WorkerId;
        bool isWarm = System.Threading.Volatile.Read(ref _workerWarm[workerId]);
        var timeout = isWarm ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(7);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // ── 6. 워커 호출 ──
        // C1+I4+A1: ID + cachePath 모두 메인이 결정 → 워커는 그대로 사용
        var req = new IpcEnvelope
        {
            Type = IpcMessageTypes.Gen,
            Id = Interlocked.Increment(ref _idCounter),
            Path = filePath,
            Size = requestedSize,
            Mode = mode,
            IsCloudOnly = isCloudOnly,
            ApplyExif = applyExif,
            Theme = theme,
            Dpi = dpi,
            CachePath = cachePath,  // 메인 단일 출처
        };

        try
        {
            var resp = await worker.RequestAsync(req, timeoutCts.Token).ConfigureAwait(false);
            if (resp.Type == IpcMessageTypes.Ok && !string.IsNullOrEmpty(resp.CachePath) && File.Exists(resp.CachePath))
            {
                RecordSuccess(workerId, parentDir);
                return new Uri(resp.CachePath);
            }

            // err — 단순 실패. 폴더 카운터만 증가, 워커는 살아있음
            if (parentDir != null)
            {
                _folderFailureCount.AddOrUpdate(parentDir, 1, (_, n) => n + 1);
            }
            return null;
        }
        catch (ThumbnailBatchCancelledException)
        {
            // Ordnerwechsel (CancelAllInflight): weder Timeout noch toter Worker — nichts zählen, nicht zurückfallen.
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 호출자 취소 — 워커 책임 아님
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout. Lebt der Worker, war nur diese Datei langsam (RAW, 4K-Video, PSD): die Token-Registrierung in
            // RequestAsync hat dem Worker bereits ein cancel für diese Id geschickt. Erst mehrere Timeouts in Folge
            // gelten als hängender Worker. Vorher tötete jeder einzelne Timeout den gesunden Worker, und jede
            // Geschwister-Anfrage lief dabei in ObjectDisposedException — drei davon schalteten das Feature ab.
            bool alive = worker.IsAlive;
            DebugLogger.Log($"[ThumbnailClient] worker#{workerId} timeout (warm={isWarm}, alive={alive})");
            if (parentDir != null)
                _folderFailureCount.AddOrUpdate(parentDir, 1, (_, n) => n + 1);
            if (!alive)
                HandleWorkerFailure(worker, countTowardsSessionLimit: true);
            else if (Interlocked.Increment(ref _consecutiveTimeouts[workerId]) >= HungWorkerTimeoutThreshold)
                HandleWorkerFailure(worker, countTowardsSessionLimit: false);
            return null;
        }
        catch (Exception ex) when (
            !worker.IsAlive
            && (ex is InvalidOperationException || ex is IOException || ex is ObjectDisposedException))
        {
            // I6 + I-LK4: EnsureWorkerAsync 직후 ~ RequestAsync 사이에 워커 죽음 race.
            // ReadLoop가 disconnect 시 모든 pending TCS에 IOException SetException.
            // → InvalidOperationException 외에 IOException, ObjectDisposedException도 retry.
            // 워커 spawn 직후 첫 요청 IOException 시 폴더 fallback 진입까지 3-9초 지연 방지.
            DebugLogger.Log($"[ThumbnailClient] worker#{workerId} died before/during request ({ex.GetType().Name}) — single retry");
            HandleWorkerFailure(worker, countTowardsSessionLimit: true);
            return await GetThumbnailUriAsyncInternalRetry(req, parentDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ThumbnailClient] worker#{workerId} call failed: {ex.Message}");
            HandleWorkerFailure(worker, countTowardsSessionLimit: !worker.IsAlive);
            if (parentDir != null)
                _folderFailureCount.AddOrUpdate(parentDir, 1, (_, n) => n + 1);
            return null;
        }
    }

    /// <summary>
    /// I6: 워커가 EnsureWorker ~ RequestAsync 사이에 죽었을 때 1회 재시도.
    /// 무한 재귀 방지 — 여기서 또 실패하면 그냥 폴백.
    /// </summary>
    private async Task<Uri?> GetThumbnailUriAsyncInternalRetry(IpcEnvelope req, string? parentDir, CancellationToken ct)
    {
        var worker = await EnsureWorkerAsync(ct).ConfigureAwait(false);
        if (worker == null) return null;

        var workerId = worker.WorkerId;
        bool isWarm = System.Threading.Volatile.Read(ref _workerWarm[workerId]);
        var timeout = isWarm ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(7);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var resp = await worker.RequestAsync(req, timeoutCts.Token).ConfigureAwait(false);
            if (resp.Type == IpcMessageTypes.Ok && !string.IsNullOrEmpty(resp.CachePath) && File.Exists(resp.CachePath))
            {
                RecordSuccess(workerId, parentDir);
                return new Uri(resp.CachePath);
            }
            if (parentDir != null) _folderFailureCount.AddOrUpdate(parentDir, 1, (_, n) => n + 1);
            return null;
        }
        catch (ThumbnailBatchCancelledException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // retry 실패 — 폴더 카운터만 증가하고 폴백 (재귀 안 함)
            if (parentDir != null) _folderFailureCount.AddOrUpdate(parentDir, 1, (_, n) => n + 1);
            return null;
        }
    }

    private void RecordSuccess(int workerId, string? parentDir)
    {
        // 첫 성공 → warm 표시 (C3: Volatile.Write로 cross-thread visibility)
        System.Threading.Volatile.Write(ref _workerWarm[workerId], true);
        // 폴더/워커 실패 카운터 리셋
        if (parentDir != null) _folderFailureCount.TryRemove(parentDir, out _);
        Interlocked.Exchange(ref _workerFailures[workerId], 0);
        Interlocked.Exchange(ref _consecutiveTimeouts[workerId], 0);
        // Die Umgebung funktioniert offensichtlich — der Sitzungszähler soll nur dauerhaft kaputte Umgebungen
        // (fehlende Runtime) erfassen, nicht die Summe gelegentlicher Neustarts über Stunden.
        Interlocked.Exchange(ref _totalFailures, 0);
    }

    /// <summary>
    /// P2-3: 폴더 이동 등으로 진행 중인 모든 요청 무효화.
    /// 호출자(FileViewModel/MainViewModel)가 폴더 변경 시 호출 권장.
    /// C3: _workers는 Volatile.Read로 race 안전.
    /// </summary>
    public Task CancelAllInflightAsync()
    {
        if (_disabled) return Task.CompletedTask;  // 격리 모드 OFF 시 fast path

        var maxId = Interlocked.Read(ref _idCounter);
        var tasks = new System.Collections.Generic.List<Task>(PoolSize);
        for (int i = 0; i < PoolSize; i++)
        {
            var w = System.Threading.Volatile.Read(ref _workers[i]);
            if (w != null && w.IsAlive)
            {
                try { tasks.Add(w.CancelBatchAsync(0, maxId)); }
                catch { }
            }
        }
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// 폴더 단위 폴백 카운터 리셋 — 호출자가 명시적으로 회복하고 싶을 때.
    /// </summary>
    public void ResetFolderFailures(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        _folderFailureCount.TryRemove(folderPath, out _);
    }

    private async Task<WorkerProcess?> EnsureWorkerAsync(CancellationToken ct)
    {
        // C2: int.MaxValue wrap 시 음수 인덱스 방지 — uint cast로 % 결과를 항상 양수로
        int startIndex = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)PoolSize);

        // 라운드로빈 시도 — 살아있는 워커 우선
        for (int offset = 0; offset < PoolSize; offset++)
        {
            int idx = (startIndex + offset) % PoolSize;
            // C3: _workers는 다른 스레드가 변경할 수 있음 → Volatile.Read로 cached read 차단
            var w = System.Threading.Volatile.Read(ref _workers[idx]);
            if (w != null && w.IsAlive) return w;
        }

        // 모두 죽음 → 라운드로빈 시작점부터 spawn 시도
        for (int offset = 0; offset < PoolSize; offset++)
        {
            int idx = (startIndex + offset) % PoolSize;
            // 백오프 중인 슬롯 skip
            if (DateTime.UtcNow < _nextRetryAt[idx]) continue;

            // Der Spawn selbst läuft mit eigenem Timeout weiter, auch wenn dieser Aufrufer (ein aus dem Bild
            // gescrolltes Element) abbricht — der nächste Aufrufer bekommt den fertigen Worker.
            var spawned = await TrySpawnWorkerAsync(idx).WaitAsync(ct).ConfigureAwait(false);
            if (spawned != null) return spawned;
        }

        return null;
    }

    /// <summary>
    /// Spawn für einen Slot anstoßen oder an den laufenden Spawn anhängen. Vorher startete jeder gleichzeitige
    /// Aufrufer (bis zu 6 Thumbnails + Prewarm beim Kaltstart) einen eigenen Span.Thumbs.exe, und der letzte
    /// überschrieb die anderen im Slot ohne Dispose — verwaiste, selbst-contained Prozesse bis zum Programmende.
    /// </summary>
    private Task<WorkerProcess?> TrySpawnWorkerAsync(int workerId)
    {
        lock (_lock)
        {
            if (_disabled) return Task.FromResult<WorkerProcess?>(null);
            var existing = _workers[workerId];
            if (existing != null && existing.IsAlive) return Task.FromResult<WorkerProcess?>(existing);

            var inflight = _spawning[workerId];
            if (inflight != null) return inflight;

            var task = SpawnWorkerCoreAsync(workerId);
            if (!task.IsCompleted)
                _spawning[workerId] = task;
            return task;
        }
    }

    private async Task<WorkerProcess?> SpawnWorkerCoreAsync(int workerId)
    {
        try
        {
            WorkerProcess created;
            lock (_lock)
            {
                if (_disabled) return null;
                var existing = _workers[workerId];
                try { existing?.Dispose(); } catch { }
                _workers[workerId] = null;

                if (!File.Exists(_workerExePath))
                {
                    if (!_spawnAttempted)
                    {
                        DebugLogger.Log($"[ThumbnailClient] worker exe not found: {_workerExePath}");
                        _spawnAttempted = true;
                    }
                    _disabled = true;
                    return null;
                }

                created = new WorkerProcess(workerId, _workerExePath, _cache.CacheRoot, _jobObject);
            }

            // Eigenes Timeout statt des Tokens eines einzelnen Elements: dessen Abbruch (aus dem Bild gescrollt)
            // tötete den halb gestarteten Worker und zählte als Fehler.
            using var spawnCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ok = await created.StartAsync(spawnCts.Token).ConfigureAwait(false);
            if (!ok)
            {
                created.Dispose();
                HandleSpawnFailure(workerId);
                return null;
            }

            WorkerProcess? stale;
            lock (_lock)
            {
                stale = _workers[workerId];
                _workers[workerId] = created;
                _workerWarm[workerId] = false;  // 새로 spawn → cold
                _consecutiveTimeouts[workerId] = 0;
            }
            try { stale?.Dispose(); } catch { }
            return created;
        }
        finally
        {
            lock (_lock) { _spawning[workerId] = null; }
        }
    }

    /// <summary>Spawn fehlgeschlagen (Prozess stirbt sofort, Pipe verbindet nicht): Backoff, zählt zur Sitzung.</summary>
    private void HandleSpawnFailure(int workerId)
    {
        ApplyBackoff(workerId);
        CountSessionFailure();
    }

    /// <summary>
    /// Toter oder hängender Worker. Nur der ERSTE Melder räumt den Slot — Geschwister-Anfragen, die am selben
    /// Worker scheiterten, dürfen weder doppelt zählen noch einen inzwischen neu gestarteten Worker im Slot entsorgen.
    /// </summary>
    private void HandleWorkerFailure(WorkerProcess failed, bool countTowardsSessionLimit)
    {
        int workerId = failed.WorkerId;
        bool first;
        lock (_lock)
        {
            first = ReferenceEquals(_workers[workerId], failed);
            if (first)
            {
                _workers[workerId] = null;
                _workerWarm[workerId] = false;
                _consecutiveTimeouts[workerId] = 0;
            }
        }
        try { failed.Dispose(); } catch { }
        if (!first) return;

        ApplyBackoff(workerId);
        if (countTowardsSessionLimit)
            CountSessionFailure();
    }

    private void ApplyBackoff(int workerId)
    {
        // M-N3: 동시 실패 처리 시 카운터 race 방지
        var fails = Interlocked.Increment(ref _workerFailures[workerId]);

        // 백오프: 1s → 3s → 10s
        var delay = fails switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(10),
        };
        _nextRetryAt[workerId] = DateTime.UtcNow + delay;
    }

    private void CountSessionFailure()
    {
        // 누적 실패 임계 초과 → 세션 영구 비활성
        if (Interlocked.Increment(ref _totalFailures) >= SessionFailureThreshold)
        {
            DebugLogger.Log($"[ThumbnailClient] disabled — total failures {_totalFailures} exceeded threshold");
            _disabled = true;
        }
    }

    private static string? SafeGetDirectoryName(string filePath)
    {
        try { return Path.GetDirectoryName(filePath); }
        catch { return null; }
    }

    /// <summary>
    /// C6: Dispose는 비-블로킹. graceful shutdown은 best-effort fire-and-forget.
    /// 워커 종료 보장은 JobObject(KILL_ON_JOB_CLOSE)가 처리 — 여기서는 핸들만 정리.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            var w = System.Threading.Volatile.Read(ref _workers[i]);
            if (w == null) continue;
            // fire-and-forget shutdown 메시지 전송 후 즉시 dispose
            try { _ = w.SendShutdownAsync(); } catch { }
            try { w.Dispose(); } catch { }
        }
        try { _jobObject?.Dispose(); } catch { }
    }
}
