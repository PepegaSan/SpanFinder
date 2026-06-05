using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;

namespace Span.Helpers;

/// <summary>
/// Detects file paths on the Windows shell clipboard (Explorer / native context menu cut-copy).
/// Span's internal _clipboardPaths is separate; shell cut-copy uses CF_HDROP + Preferred DropEffect.
/// </summary>
internal static class ShellClipboardHelper
{
    private const uint CF_HDROP = 15;
    private const uint CF_UNICODETEXT = 13;
    private const int DROPEFFECT_COPY = 1;
    private const int DROPEFFECT_MOVE = 2;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int DROPFILES_SIZE = 20;

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

    public static bool TryReadFileClipboard(out List<string> paths, out bool isCut)
        => TryReadFileClipboard(IntPtr.Zero, out paths, out isCut);

    public static bool TryReadFileClipboard(IntPtr hwndOwner, out List<string> paths, out bool isCut)
    {
        paths = new List<string>();
        isCut = false;

        // CF_HDROP first — reliable for multi-file (Explorer / shell); WinRT StorageItems can return only one item
        if (TryReadHDropClipboard(hwndOwner, out paths, out isCut) && paths.Count > 0)
            return true;

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

        return false;
    }

    /// <summary>
    /// Write file paths to the shell clipboard (CF_HDROP + Preferred DropEffect), like Explorer.
    /// Reliable for multi-file; WinRT SetStorageItems often exposes only one path via HDROP.
    /// </summary>
    public static bool TryWriteFileClipboard(IReadOnlyList<string> paths, bool isCut)
        => TryWriteFileClipboard(IntPtr.Zero, paths, isCut);

    public static bool TryWriteFileClipboard(IntPtr hwndOwner, IReadOnlyList<string> paths, bool isCut)
    {
        if (paths.Count == 0)
            return false;

        if (!TryOpenClipboard(hwndOwner))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            var pathBlock = new StringBuilder();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                pathBlock.Append(path);
                pathBlock.Append('\0');
            }
            pathBlock.Append('\0');

            var pathBytes = Encoding.Unicode.GetBytes(pathBlock.ToString());
            var totalSize = DROPFILES_SIZE + pathBytes.Length;
            var hDrop = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)totalSize);
            if (hDrop == IntPtr.Zero)
                return false;

            var dropPtr = GlobalLock(hDrop);
            if (dropPtr == IntPtr.Zero)
                return false;

            try
            {
                Marshal.WriteInt32(dropPtr, 0, DROPFILES_SIZE);
                Marshal.WriteInt32(dropPtr, 16, 1); // fWide = TRUE
                Marshal.Copy(pathBytes, 0, dropPtr + DROPFILES_SIZE, pathBytes.Length);
            }
            finally
            {
                GlobalUnlock(hDrop);
            }

            if (SetClipboardData(CF_HDROP, hDrop) == IntPtr.Zero)
                return false;

            SetPreferredDropEffect(isCut ? DROPEFFECT_MOVE : DROPEFFECT_COPY);

            var textBytes = Encoding.Unicode.GetBytes(string.Join(Environment.NewLine, paths) + '\0');
            var hText = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)textBytes.Length);
            if (hText != IntPtr.Zero)
            {
                var textPtr = GlobalLock(hText);
                if (textPtr != IntPtr.Zero)
                {
                    try
                    {
                        Marshal.Copy(textBytes, 0, textPtr, textBytes.Length);
                    }
                    finally
                    {
                        GlobalUnlock(hText);
                    }

                    SetClipboardData(CF_UNICODETEXT, hText);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ShellClipboard] Write failed: {ex.Message}");
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetPreferredDropEffect(int dropEffect)
    {
        if (PreferredDropEffectFormat == 0)
            return;

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
        if (hMem == IntPtr.Zero)
            return;

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
            return;

        try
        {
            Marshal.WriteInt32(ptr, dropEffect);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        SetClipboardData(PreferredDropEffectFormat, hMem);
    }

    private static bool TryReadHDropClipboard(IntPtr hwndOwner, out List<string> paths, out bool isCut)
    {
        paths = new List<string>();
        isCut = false;

        if (!TryOpenClipboard(hwndOwner))
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

    private static bool TryOpenClipboard(IntPtr hwndOwner)
    {
        IntPtr owner = hwndOwner != IntPtr.Zero ? hwndOwner : IntPtr.Zero;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(owner))
                return true;
            Thread.Sleep(15);
        }

        return OpenClipboard(IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

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
