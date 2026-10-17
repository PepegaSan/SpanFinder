using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Span.Services
{
    /// <summary>
    /// 폴더 크기를 백그라운드에서 계산하고 캐시하는 서비스.
    /// Details 뷰에서 폴더 크기 표시에 사용.
    /// Sizes come from a shared <see cref="FolderSizeIndex"/>: one walk measures a folder and its
    /// subfolders, so later requests inside the same branch are answered without touching the disk.
    /// </summary>
    public class FolderSizeService
    {
        private readonly FolderSizeIndex _index = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 폴더 크기 계산 완료 시 발생. (folderPath, sizeInBytes)
        /// </summary>
        public event Action<string, long>? SizeCalculated;

        /// <summary>
        /// 캐시된 크기 반환. 없으면 null.
        /// </summary>
        public long? TryGetCachedSize(string folderPath)
        {
            return _index.TryGet(folderPath);
        }

        /// <summary>
        /// Cached size or a synchronous walk, for callers already off the UI thread (Quick Look).
        /// Shares results with the Details view.
        /// </summary>
        public long GetOrCalculateSize(string folderPath, CancellationToken token)
        {
            return _index.GetOrCompute(folderPath, token);
        }

        /// <summary>
        /// 폴더 크기를 백그라운드에서 계산 요청. 이미 계산 중이면 무시.
        /// </summary>
        public void RequestCalculation(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            if (_index.TryGet(folderPath).HasValue) return;
            if (_pending.ContainsKey(folderPath)) return;

            var cts = new CancellationTokenSource();
            if (!_pending.TryAdd(folderPath, cts))
            {
                cts.Dispose();
                return;
            }

            var token = cts.Token;
            _ = Task.Run(() =>
            {
                try
                {
                    long size = _index.GetOrCompute(folderPath, token);
                    if (!token.IsCancellationRequested)
                        SizeCalculated?.Invoke(folderPath, size);
                }
                catch (OperationCanceledException) { /* 정상 취소 */ }
                catch
                {
                    if (!token.IsCancellationRequested)
                        _index.MarkUnavailable(folderPath); // 접근 불가 표시
                }
                finally
                {
                    _pending.TryRemove(folderPath, out var removed);
                    removed?.Dispose();
                }
            }, token);
        }

        /// <summary>
        /// 특정 폴더의 진행 중인 계산을 취소.
        /// </summary>
        public void CancelCalculation(string folderPath)
        {
            if (_pending.TryRemove(folderPath, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); }
                catch { }
            }
        }

        /// <summary>
        /// 진행 중인 모든 계산을 취소 (앱 종료/탭 닫기 시).
        /// </summary>
        public void CancelAll()
        {
            foreach (var kvp in _pending)
            {
                try { kvp.Value.Cancel(); kvp.Value.Dispose(); }
                catch { }
            }
            _pending.Clear();
        }

        /// <summary>
        /// 특정 폴더의 캐시 무효화.
        /// Parent folders are dropped too, since their totals include this folder.
        /// </summary>
        public void Invalidate(string folderPath)
        {
            _index.Invalidate(folderPath);
        }
    }
}
