using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;

namespace Span.Services
{
    /// <summary>
    /// Bearbeitbare Eigenschaften einer Datei wie im Detailbereich des Explorers. Can… = der Eigenschaften-Handler
    /// des Dateityps kann das Feld schreiben. Bewertung in Sternen (0 = keine).
    /// </summary>
    public sealed class EditableFileProperties
    {
        public bool CanRating { get; init; }
        public bool CanTitle { get; init; }
        public bool CanAuthors { get; init; }
        public bool CanKeywords { get; init; }
        public bool CanComment { get; init; }

        public int Rating { get; init; }
        public string Title { get; init; } = string.Empty;
        /// <summary>Mehrere Werte mit "; " getrennt.</summary>
        public string Authors { get; init; } = string.Empty;
        /// <summary>Markierungen (System.Keywords), mit "; " getrennt.</summary>
        public string Keywords { get; init; } = string.Empty;
        public string Comment { get; init; } = string.Empty;

        public bool CanEditAny => CanRating || CanTitle || CanAuthors || CanKeywords || CanComment;
    }

    /// <summary>
    /// Liest und schreibt Bewertung, Titel, Autoren, Markierungen und Kommentare über das Windows-Eigenschaftensystem
    /// (dieselben Werte wie im Explorer). Bearbeitbar nur, wenn für die Endung ein Eigenschaften-Handler registriert ist
    /// und er das Feld als beschreibbar meldet — ohne Handler (z. B. .txt) meldet der Speicher alles als beschreibbar,
    /// das Öffnen zum Schreiben scheitert dann aber. Nur im Hintergrund aufrufen.
    /// </summary>
    public static class ShellPropertyEditor
    {
        private const int GPS_DEFAULT = 0x0;
        private const int GPS_READWRITE = 0x2;
        private const ushort VT_EMPTY = 0;
        private const ushort VT_UI4 = 19;
        private const ushort VT_LPWSTR = 31;

        private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
        private static readonly Guid FmtSummary = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
        private static readonly Guid FmtMedia = new("64440492-4C8B-11D1-8B70-080036B11A03");

        private static readonly PropertyKey PKEY_Rating = new(FmtMedia, 9);
        private static readonly PropertyKey PKEY_Title = new(FmtSummary, 2);
        private static readonly PropertyKey PKEY_Author = new(FmtSummary, 4);
        private static readonly PropertyKey PKEY_Keywords = new(FmtSummary, 5);
        private static readonly PropertyKey PKEY_Comment = new(FmtSummary, 6);

        private static readonly ConcurrentDictionary<string, bool> s_handlerCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Explorer-Abbildung 1–5 Sterne ↔ System.Rating (1, 25, 50, 75, 99).</summary>
        public static uint RatingFromStars(int stars) => stars switch
        {
            <= 0 => 0,
            1 => 1,
            2 => 25,
            3 => 50,
            4 => 75,
            _ => 99,
        };

        public static int StarsFromRating(uint rating) => rating switch
        {
            0 => 0,
            <= 12 => 1,
            <= 37 => 2,
            <= 62 => 3,
            <= 87 => 4,
            _ => 5,
        };

        /// <summary>"a; b;;c " → ["a", "b", "c"].</summary>
        public static string[] SplitList(string? text)
            => (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

        /// <summary>Werte und Bearbeitbarkeit lesen; null, wenn der Dateityp nichts davon schreiben kann.</summary>
        public static EditableFileProperties? Read(string path)
        {
            if (!HasPropertyHandler(System.IO.Path.GetExtension(path))) return null;

            object? obj = null;
            try
            {
                var iid = IID_IPropertyStore;
                int hr = SHGetPropertyStoreFromParsingName(Helpers.LongPathHelper.StripPrefix(path), IntPtr.Zero, GPS_DEFAULT, ref iid, out obj);
                if (hr < 0 || obj is not IPropertyStore store) return null;
                var caps = obj as IPropertyStoreCapabilities;

                bool Writable(PropertyKey key)
                {
                    if (caps == null) return true;
                    var k = key;
                    return caps.IsPropertyWritable(ref k) == 0;
                }

                var result = new EditableFileProperties
                {
                    CanRating = Writable(PKEY_Rating),
                    CanTitle = Writable(PKEY_Title),
                    CanAuthors = Writable(PKEY_Author),
                    CanKeywords = Writable(PKEY_Keywords),
                    CanComment = Writable(PKEY_Comment),
                    Rating = StarsFromRating(GetUInt32(store, PKEY_Rating)),
                    Title = GetString(store, PKEY_Title),
                    Authors = GetString(store, PKEY_Author),
                    Keywords = GetString(store, PKEY_Keywords),
                    Comment = GetString(store, PKEY_Comment),
                };
                return result.CanEditAny ? result : null;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[ShellPropertyEditor] read {path}: {ex.Message}");
                return null;
            }
            finally
            {
                if (obj != null) Marshal.FinalReleaseComObject(obj);
            }
        }

        /// <summary>
        /// Geänderte Felder schreiben (null = Feld nicht ändern, leer = Wert entfernen). Gibt bei Erfolg null zurück,
        /// sonst den HRESULT des Fehlers (z. B. 0x80070020, wenn ein anderes Programm die Datei geöffnet hat).
        /// </summary>
        public static int? Write(string path, int? stars, string? title, string? authors, string? keywords, string? comment)
        {
            object? obj = null;
            try
            {
                var iid = IID_IPropertyStore;
                int hr = SHGetPropertyStoreFromParsingName(Helpers.LongPathHelper.StripPrefix(path), IntPtr.Zero, GPS_READWRITE, ref iid, out obj);
                if (hr < 0 || obj is not IPropertyStore store) return hr < 0 ? hr : unchecked((int)0x80004005);

                if (stars.HasValue)
                {
                    uint rating = RatingFromStars(stars.Value);
                    var pv = new PropVariant { vt = rating == 0 ? VT_EMPTY : VT_UI4, p1 = (IntPtr)rating };
                    if ((hr = Set(store, PKEY_Rating, ref pv)) < 0) return hr;
                }
                if (title != null && (hr = SetString(store, PKEY_Title, title)) < 0) return hr;
                if (comment != null && (hr = SetString(store, PKEY_Comment, comment)) < 0) return hr;
                if (authors != null && (hr = SetList(store, PKEY_Author, authors)) < 0) return hr;
                if (keywords != null && (hr = SetList(store, PKEY_Keywords, keywords)) < 0) return hr;

                hr = store.Commit();
                return hr < 0 ? hr : null;
            }
            catch (COMException ex)
            {
                Helpers.DebugLogger.Log($"[ShellPropertyEditor] write {path}: {ex.Message}");
                return ex.HResult;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[ShellPropertyEditor] write {path}: {ex.Message}");
                return unchecked((int)0x80004005);
            }
            finally
            {
                if (obj != null) Marshal.FinalReleaseComObject(obj);
            }
        }

        /// <summary>Ist für die Endung ein Eigenschaften-Handler registriert (HKLM\…\PropertySystem\PropertyHandlers)?</summary>
        private static bool HasPropertyHandler(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            return s_handlerCache.GetOrAdd(extension, ext =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\" + ext);
                    return key?.GetValue(null) is string clsid && clsid.Length > 0;
                }
                catch
                {
                    return false;
                }
            });
        }

        private static int Set(IPropertyStore store, PropertyKey key, ref PropVariant pv)
        {
            var k = key;
            return store.SetValue(ref k, ref pv);
        }

        private static int SetString(IPropertyStore store, PropertyKey key, string value)
        {
            var text = value.Trim();
            var pv = new PropVariant();
            if (text.Length > 0)
            {
                pv.vt = VT_LPWSTR;
                pv.p1 = Marshal.StringToCoTaskMemUni(text);
            }
            try { return Set(store, key, ref pv); }
            finally { PropVariantClear(ref pv); }
        }

        private static int SetList(IPropertyStore store, PropertyKey key, string value)
        {
            var items = SplitList(value);
            var pv = new PropVariant();
            if (items.Length > 0)
            {
                int hr = InitPropVariantFromStringVector(items, (uint)items.Length, out pv);
                if (hr < 0) return hr;
            }
            try { return Set(store, key, ref pv); }
            finally { PropVariantClear(ref pv); }
        }

        private static uint GetUInt32(IPropertyStore store, PropertyKey key)
        {
            var k = key;
            if (store.GetValue(ref k, out var pv) < 0) return 0;
            try
            {
                if (pv.vt == VT_EMPTY) return 0;
                return PropVariantToUInt32(ref pv, out var value) >= 0 ? value : 0;
            }
            finally { PropVariantClear(ref pv); }
        }

        /// <summary>Text oder Textliste ("A; B").</summary>
        private static string GetString(IPropertyStore store, PropertyKey key)
        {
            var k = key;
            if (store.GetValue(ref k, out var pv) < 0) return string.Empty;
            try
            {
                if (pv.vt == VT_EMPTY) return string.Empty;
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

        /// <summary>PROPVARIANT: 8 Byte Kopf + Union aus zwei Zeigern (x64 24 Byte).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            private ushort _reserved1, _reserved2, _reserved3;
            public IntPtr p1;
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

        [ComImport, Guid("c8e2d566-186e-4d49-bf41-6909ead56acc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStoreCapabilities
        {
            [PreserveSig] int IsPropertyWritable(ref PropertyKey key);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(
            string pszPath, IntPtr pbc, int flags, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [DllImport("propsys.dll")]
        private static extern int PropVariantToUInt32(ref PropVariant propvar, out uint pulRet);

        [DllImport("propsys.dll")]
        private static extern int PropVariantToStringAlloc(ref PropVariant propvar, out IntPtr ppszOut);

        [DllImport("propsys.dll")]
        private static extern int InitPropVariantFromStringVector(
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] prgsz,
            uint cElems, out PropVariant ppropvar);
    }
}
