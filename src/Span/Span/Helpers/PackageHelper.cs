using System;
using System.Reflection;

namespace Span.Helpers;

internal static class PackageHelper
{
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id.Name;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static Version GetAppVersion()
    {
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(pkg.Major, pkg.Minor, pkg.Build, pkg.Revision);
        }
        catch
        {
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 5, 3);
        }
    }

    public static string FormatVersionLabel()
    {
        var v = GetAppVersion();
        return $"v{v.Major}.{v.Minor}.{v.Build} (Build {BuildInfo.BuildDate})";
    }

    public static string GetInstalledPath()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.InstalledPath;
        }
        catch
        {
            return AppContext.BaseDirectory.TrimEnd('\\', '/');
        }
    }
}
