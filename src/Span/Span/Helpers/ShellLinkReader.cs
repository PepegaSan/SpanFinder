using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Span.Helpers
{
    /// <summary>
    /// Liest das Ziel einer .lnk-Verknüpfung über IShellLinkW (Unicode, Netzwerkpfade vollständig).
    /// Der binäre Parser in FileSystemService liest ANSI und bei Netzwerkzielen nur die Freigabe —
    /// für Dateilisten wie "Zuletzt verwendet" reicht das nicht.
    /// </summary>
    public static class ShellLinkReader
    {
        private const uint STGM_READ = 0;
        private const int MaxPath = 32768;

        public static string? GetTarget(string lnkPath)
        {
            object? link = null;
            try
            {
                link = new ShellLinkCoClass();
                if (((IPersistFile)link).Load(lnkPath, STGM_READ) < 0) return null;

                var sb = new StringBuilder(MaxPath);
                if (((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0) return null; // S_FALSE = kein Dateisystemziel
                var target = sb.ToString();
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ShellLinkReader] {lnkPath}: {ex.Message}");
                return null;
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass { }

        /// <summary>Nur die erste Methode wird gebraucht; die vtable-Reihenfolge beginnt mit GetPath.</summary>
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig]
            int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            [PreserveSig] int GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        }
    }
}
