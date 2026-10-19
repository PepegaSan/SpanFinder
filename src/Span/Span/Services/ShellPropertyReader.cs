using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Span.Services
{
    /// <summary>
    /// Details 추가 컬럼(크기·길이·작성자·제목)에 쓰는 셸 속성 값. 표시 문자열과 정렬 키를 함께 담는다.
    /// </summary>
    public sealed class ShellPropertyValues
    {
        public static readonly ShellPropertyValues Empty = new();

        public string Dimensions { get; init; } = string.Empty;
        /// <summary>정렬 키: 가로 × 세로 픽셀 수.</summary>
        public long PixelCount { get; init; }
        public string Duration { get; init; } = string.Empty;
        /// <summary>정렬 키: 100ns 단위.</summary>
        public long DurationTicks { get; init; }
        public string Authors { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
    }

    /// <summary>
    /// Windows 속성 시스템(IPropertyStore)에서 탐색기 Details 컬럼과 같은 값을 읽는다.
    /// 백그라운드에서 동시 3개까지만 읽고, 경로+수정 시각+크기 기준으로 결과를 캐시한다.
    /// 처리기가 파일 핸들을 쥐고 있으면 이름 변경/삭제가 막히므로 저장소는 읽은 즉시 해제한다.
    /// </summary>
    public static class ShellPropertyReader
    {
        private const int MaxConcurrentReads = 3;
        private const int MaxCacheEntries = 50_000;
        private const int GPS_BESTEFFORT = 0x40;

        private static readonly SemaphoreSlim s_gate = new(MaxConcurrentReads);
        private static readonly ConcurrentDictionary<string, (DateTime Modified, long Size, ShellPropertyValues Values)> s_cache
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

        // System.Image.* / System.Video.* / System.Media.Duration / System.Author / System.Title / System.Music.Artist
        private static readonly Guid FmtImage = new("6444048F-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtVideo = new("64440491-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtMedia = new("64440490-4C8B-11D1-8B70-080036B11A03");
        private static readonly Guid FmtSummary = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
        private static readonly Guid FmtMusic = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");

        private static readonly PropertyKey PKEY_Image_HorizontalSize = new(FmtImage, 3);
        private static readonly PropertyKey PKEY_Image_VerticalSize = new(FmtImage, 4);
        private static readonly PropertyKey PKEY_Video_FrameWidth = new(FmtVideo, 3);
        private static readonly PropertyKey PKEY_Video_FrameHeight = new(FmtVideo, 4);
        private static readonly PropertyKey PKEY_Media_Duration = new(FmtMedia, 3);
        private static readonly PropertyKey PKEY_Author = new(FmtSummary, 4);
        private static readonly PropertyKey PKEY_Title = new(FmtSummary, 2);
        private static readonly PropertyKey PKEY_Music_Artist = new(FmtMusic, 2);

        /// <summary>캐시에 있으면 즉시 반환 (UI 스레드에서 호출 가능).</summary>
        public static ShellPropertyValues? TryGetCached(string path, DateTime modified, long size)
        {
            if (s_cache.TryGetValue(path, out var entry) && entry.Modified == modified && entry.Size == size)
                return entry.Values;
            return null;
        }

        public static async Task<ShellPropertyValues> GetAsync(string path, DateTime modified, long size, CancellationToken ct)
        {
            var cached = TryGetCached(path, modified, size);
            if (cached != null) return cached;

            await s_gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                cached = TryGetCached(path, modified, size);
                if (cached != null) return cached;

                var values = await Task.Run(() => Read(path), ct).ConfigureAwait(false);
                if (s_cache.Count >= MaxCacheEntries) s_cache.Clear();
                s_cache[path] = (modified, size, values);
                return values;
            }
            finally
            {
                s_gate.Release();
            }
        }

        private static ShellPropertyValues Read(string path)
        {
            IPropertyStore? store = null;
            try
            {
                var iid = IID_IPropertyStore;
                int hr = SHGetPropertyStoreFromParsingName(
                    Helpers.LongPathHelper.StripPrefix(path), IntPtr.Zero, GPS_BESTEFFORT, ref iid, out store);
                if (hr < 0 || store == null) return ShellPropertyValues.Empty;

                uint width = GetUInt32(store, PKEY_Image_HorizontalSize);
                uint height = GetUInt32(store, PKEY_Image_VerticalSize);
                if (width == 0 || height == 0)
                {
                    width = GetUInt32(store, PKEY_Video_FrameWidth);
                    height = GetUInt32(store, PKEY_Video_FrameHeight);
                }

                long duration = (long)Math.Min(GetUInt64(store, PKEY_Media_Duration), long.MaxValue);

                string authors = GetString(store, PKEY_Author);
                if (authors.Length == 0) authors = GetString(store, PKEY_Music_Artist);

                return new ShellPropertyValues
                {
                    Dimensions = width > 0 && height > 0 ? $"{width} x {height}" : string.Empty,
                    PixelCount = (long)width * height,
                    Duration = Helpers.DetailsColumns.FormatDuration(duration),
                    DurationTicks = duration,
                    Authors = authors,
                    Title = GetString(store, PKEY_Title),
                };
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[ShellPropertyReader] {path}: {ex.Message}");
                return ShellPropertyValues.Empty;
            }
            finally
            {
                if (store != null) Marshal.FinalReleaseComObject(store);
            }
        }

        private static uint GetUInt32(IPropertyStore store, PropertyKey key)
        {
            var k = key;
            if (store.GetValue(ref k, out var pv) < 0) return 0;
            try
            {
                if (pv.vt == 0) return 0;
                return PropVariantToUInt32(ref pv, out var value) >= 0 ? value : 0;
            }
            finally { PropVariantClear(ref pv); }
        }

        private static ulong GetUInt64(IPropertyStore store, PropertyKey key)
        {
            var k = key;
            if (store.GetValue(ref k, out var pv) < 0) return 0;
            try
            {
                if (pv.vt == 0) return 0;
                return PropVariantToUInt64(ref pv, out var value) >= 0 ? value : 0;
            }
            finally { PropVariantClear(ref pv); }
        }

        /// <summary>문자열 또는 문자열 벡터("A; B")를 읽는다.</summary>
        private static string GetString(IPropertyStore store, PropertyKey key)
        {
            var k = key;
            if (store.GetValue(ref k, out var pv) < 0) return string.Empty;
            try
            {
                if (pv.vt == 0) return string.Empty;
                if (PropVariantToStringAlloc(ref pv, out var psz) < 0 || psz == IntPtr.Zero) return string.Empty;
                try { return (Marshal.PtrToStringUni(psz) ?? string.Empty).Trim(); }
                finally { Marshal.FreeCoTaskMem(psz); }
            }
            finally { PropVariantClear(ref pv); }
        }

        // ── Interop ──

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PropertyKey
        {
            public readonly Guid FormatId;
            public readonly uint PropertyId;
            public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
        }

        /// <summary>PROPVARIANT: 8바이트 헤더 + 포인터 두 개 크기의 공용체 (x64 24바이트, x86 16바이트).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            private ushort _reserved1, _reserved2, _reserved3;
            private IntPtr _p1;
            private IntPtr _p2;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(
            string pszPath, IntPtr pbc, int flags, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [DllImport("propsys.dll")]
        private static extern int PropVariantToUInt32(ref PropVariant propvar, out uint pulRet);

        [DllImport("propsys.dll")]
        private static extern int PropVariantToUInt64(ref PropVariant propvar, out ulong pullRet);

        [DllImport("propsys.dll")]
        private static extern int PropVariantToStringAlloc(ref PropVariant propvar, out IntPtr ppszOut);
    }
}
