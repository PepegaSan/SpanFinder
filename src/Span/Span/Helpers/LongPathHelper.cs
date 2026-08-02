using System;
using System.IO;

namespace Span.Helpers
{
    /// <summary>
    /// Helpers for Win32 extended-length paths (\\?\ / \\?\UNC\).
    /// </summary>
    public static class LongPathHelper
    {
        public const int MaxPathThreshold = 260;

        /// <summary>
        /// Always add the extended-length prefix (for Win32 APIs that require it).
        /// </summary>
        public static string EnsureExtendedPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + path[2..];
            return @"\\?\" + path;
        }

        /// <summary>
        /// Add prefix only when the path is at/over MAX_PATH, otherwise return as-is.
        /// Safe for most System.IO APIs on .NET that already support long paths.
        /// </summary>
        public static string ForIo(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path;
            if (path.Length >= MaxPathThreshold)
                return EnsureExtendedPrefix(path);
            return path;
        }

        /// <summary>Strip \\?\ / \\?\UNC\ for display and navigation state.</summary>
        public static string StripPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path[8..];
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path[4..];
            return path;
        }

        public static bool Exists(string path)
        {
            var p = ForIo(path);
            return Directory.Exists(p) || File.Exists(p);
        }

        public static bool DirectoryExists(string path) => Directory.Exists(ForIo(path));
        public static bool FileExists(string path) => File.Exists(ForIo(path));
    }
}
