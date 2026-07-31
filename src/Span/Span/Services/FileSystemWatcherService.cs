using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Span.Services
{
    /// <summary>
    /// 활성 탭의 표시 중인 컬럼 경로들을 감시하여 파일 변경 시 자동 새로고침을 트리거하는 서비스.
    /// Created/Deleted/Renamed/Changed 구독 (Changed = 내용/크기/수정시각 변경 감지).
    /// 300ms 디바운싱으로 대량 변경 시 한 번만 리프레시 — Changed 폭주(대용량 쓰기)는
    /// 폴더별 디바운스가 자연스럽게 병합하여 쓰기가 멈춘 뒤 1회만 갱신한다.
    /// </summary>
    public class FileSystemWatcherService : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        private const int DebounceMs = 300;
        private const int ErrorDebounceMs = 1000; // 버퍼 오버플로우 시 더 긴 대기
        private const int BufferSize = 65536;

        /// <summary>
        /// 파일 변경 감지 시 발생. (changedFolderPath)
        /// UI 스레드 마샬링은 호출자 책임.
        /// </summary>
        public event Action<string>? PathChanged;

        /// <summary>
        /// 감시 경로 목록 갱신. 기존 경로는 유지, 새 경로 추가, 사라진 경로 제거.
        /// 네트워크/원격 경로는 자동 제외.
        /// </summary>
        public void SetWatchedPaths(IEnumerable<string> paths)
        {
            var newPaths = new HashSet<string>(
                paths.Where(p => !string.IsNullOrEmpty(p) && !FileSystemRouter.IsRemotePath(p) && IsLocalPath(p)),
                StringComparer.OrdinalIgnoreCase
            );

            lock (_lock)
            {
                // 제거할 경로
                var toRemove = _watchers.Keys.Where(k => !newPaths.Contains(k)).ToList();
                foreach (var path in toRemove)
                {
                    if (_watchers.TryGetValue(path, out var watcher))
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                        _watchers.Remove(path);
                    }
                }

                // 추가할 경로
                foreach (var path in newPaths)
                {
                    if (_watchers.ContainsKey(path)) continue;
                    if (!Directory.Exists(path)) continue;

                    try
                    {
                        var watcher = new FileSystemWatcher(path)
                        {
                            // LastWrite|Size 추가: 파일 내용 변경(수정 시각/크기)도 감지 →
                            // 외부 앱이 파일을 수정/덮어써도 현재 보고 있는 폴더가 즉시 갱신됨.
                            // (이전엔 FileName|DirectoryName만 감시하여 "파일이 변경됐을 때"는
                            //  다른 폴더로 이동했다 돌아와야 반영되던 문제.)
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                | NotifyFilters.LastWrite | NotifyFilters.Size,
                            IncludeSubdirectories = false,
                            InternalBufferSize = BufferSize,
                        };

                        watcher.Created += OnFileSystemEvent;
                        watcher.Deleted += OnFileSystemEvent;
                        watcher.Renamed += OnFileSystemEvent;
                        watcher.Changed += OnFileSystemEvent;
                        watcher.Error += OnWatcherError;
                        watcher.EnableRaisingEvents = true;

                        _watchers[path] = watcher;
                    }
                    catch (Exception ex)
                    {
                        Helpers.DebugLogger.Log($"[FileSystemWatcher] 감시 실패: {path} - {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 모든 감시 중지.
        /// </summary>
        public void StopAll()
        {
            lock (_lock)
            {
                foreach (var watcher in _watchers.Values)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _watchers.Clear();
            }

            foreach (var timer in _debounceTimers.Values)
                timer.Dispose();
            _debounceTimers.Clear();
        }

        /// <summary>
        /// Recreate every active watcher. FileSystemWatcher can miss events (or go silent)
        /// after the process was backgrounded / suspended for a long time; reaffirming on
        /// foreground resume restores reliable change detection.
        /// </summary>
        public void ReaffirmAllWatchers()
        {
            List<string> paths;
            lock (_lock)
            {
                paths = _watchers.Keys.ToList();
            }

            foreach (var path in paths)
                RecreateWatcher(path);

            if (paths.Count > 0)
                Helpers.DebugLogger.Log($"[FileSystemWatcher] Reaffirmed {paths.Count} watcher(s) after resume");
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (sender is not FileSystemWatcher watcher) return;
            var folderPath = watcher.Path;

            DebouncedNotify(folderPath);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (sender is not FileSystemWatcher watcher) return;
            var path = watcher.Path;
            Helpers.DebugLogger.Log($"[FileSystemWatcher] 버퍼 오버플로우: {path} - {e.GetException().Message}");

            // 버퍼 오버플로우 시: watcher 재생성 + 긴 디바운스로 전체 리프레시
            RecreateWatcher(path);
            DebouncedNotify(path, ErrorDebounceMs);
        }

        /// <summary>
        /// 죽은 watcher를 dispose하고 동일 경로로 새로 생성.
        /// 버퍼 오버플로우 후 watcher는 더 이상 이벤트를 발생시키지 않으므로
        /// 반드시 재생성해야 이후 변경 감지가 유지됨.
        /// </summary>
        private void RecreateWatcher(string path)
        {
            lock (_lock)
            {
                if (_watchers.TryGetValue(path, out var oldWatcher))
                {
                    oldWatcher.EnableRaisingEvents = false;
                    oldWatcher.Dispose();
                    _watchers.Remove(path);
                }

                if (!Directory.Exists(path)) return;

                try
                {
                    var newWatcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                        InternalBufferSize = BufferSize,
                    };

                    newWatcher.Created += OnFileSystemEvent;
                    newWatcher.Deleted += OnFileSystemEvent;
                    newWatcher.Renamed += OnFileSystemEvent;
                    newWatcher.Changed += OnFileSystemEvent;
                    newWatcher.Error += OnWatcherError;
                    newWatcher.EnableRaisingEvents = true;

                    _watchers[path] = newWatcher;
                    Helpers.DebugLogger.Log($"[FileSystemWatcher] 재생성 완료: {path}");
                }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[FileSystemWatcher] 재생성 실패: {path} - {ex.Message}");
                }
            }
        }

        private void DebouncedNotify(string folderPath, int delayMs = DebounceMs)
        {
            _debounceTimers.AddOrUpdate(
                folderPath,
                // 신규: 타이머 생성
                _ => new Timer(TimerCallback, folderPath, delayMs, Timeout.Infinite),
                // 기존: 타이머 재설정 (원자적 교체로 경합 조건 방지)
                (_, existing) =>
                {
                    existing.Change(delayMs, Timeout.Infinite);
                    return existing;
                });
        }

        private void TimerCallback(object? state)
        {
            // v1.4.15: ThreadPool Timer callback throw → AppDomain unhandled.
            // PathChanged 구독자 throw가 메인 크래시로 번지지 않도록 봉인.
            try
            {
                if (state is not string folderPath) return;
                if (_debounceTimers.TryRemove(folderPath, out var removed))
                    removed.Dispose();
                PathChanged?.Invoke(folderPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileSystemWatcherService.TimerCallback] {ex.Message}");
            }
        }

        private static bool IsLocalPath(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false; // UNC 경로 제외
            if (path.Length >= 2 && path[1] == ':') return true; // C:\... 등
            return false;
        }

        public void Dispose()
        {
            StopAll();
            GC.SuppressFinalize(this);
        }
    }
}
