using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Span.Helpers;

/// <summary>
/// Detects file paths on the Windows shell clipboard (Explorer / native context menu cut-copy).
/// Span's internal _clipboardPaths is separate; shell cut-copy uses CF_HDROP + Preferred DropEffect.
/// </summary>
internal static class ShellClipboardHelper
{
    private const uint CF_HDROP = 15;
    private const int DROPEFFECT_MOVE = 2;

    private static readonly uint PreferredDropEffectFormat =
        (uint)RegisterClipboardFormat("Preferred DropEffect");

    public static bool HasPasteableFiles()
    {
        try
        {
            if (IsClipboardFormatAvailable(CF_HDROP))
                return true;

            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
                return true;

            return VirtualFileClipboardHelper.IsVirtualFileDataAvailable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads file paths and cut vs copy from the system clipboard. Returns false if none found.
    /// </summary>
    public static bool TryReadFileClipboard(out List<string> paths, out bool isCut)
    {
        paths = new List<string>();
        isCut = false;

        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var task = content.GetStorageItemsAsync().AsTask();
                if (!task.Wait(3000))
                    return false;

                paths = task.Result
                    .Select(i => i.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (paths.Count > 0)
                {
                    isCut = content.RequestedOperation.HasFlag(DataPackageOperation.Move)
                        || IsCutOnClipboard();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ShellClipboard] WinRT read failed: {ex.Message}");
        }

        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!IsClipboardFormatAvailable(CF_HDROP))
                return false;

            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero)
                return false;

            paths = GetPathsFromHDrop(hDrop);
            if (paths.Count == 0)
                return false;

            isCut = IsCutOnClipboard();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool IsCutOnClipboard()
    {
        if (PreferredDropEffectFormat == 0 || !IsClipboardFormatAvailable(PreferredDropEffectFormat))
            return false;

        var hMem = GetClipboardData(PreferredDropEffectFormat);
        if (hMem == IntPtr.Zero)
            return false;

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
            return false;

        try
        {
            return (Marshal.ReadInt32(ptr) & DROPEFFECT_MOVE) != 0;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private static List<string> GetPathsFromHDrop(IntPtr hDrop)
    {
        var paths = new List<string>();
        uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null!, 0);
        var buffer = new char[260];

        for (uint i = 0; i < count; i++)
        {
            uint len = DragQueryFile(hDrop, i, buffer, (uint)buffer.Length);
            if (len == 0)
                continue;

            var path = new string(buffer, 0, (int)len);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }

        return paths;
    }

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern int RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, char[] lpszFile, uint cch);
}
