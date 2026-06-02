using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Span.Helpers;

/// <summary>
/// Shell identity for unpackaged / personal installs (taskbar pin, Start menu).
/// </summary>
internal static class AppBranding
{
    public const string AppUserModelId = "PepegaSan.SpanFinder.Personal";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void ApplyShellIdentity()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[AppBranding] SetAppUserModelID failed: {ex.Message}");
        }
    }

    public static string GetAppIcoPath()
    {
        var path = Path.Combine(PackageHelper.GetInstalledPath(), "Assets", "app.ico");
        return File.Exists(path) ? path : string.Empty;
    }
}
