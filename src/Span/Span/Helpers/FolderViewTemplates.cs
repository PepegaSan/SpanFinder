using System;
using System.Collections.Generic;
using System.IO;
using Span.Models;

namespace Span.Helpers
{
    /// <summary>Ordnertyp wie im Explorer ("Ordner optimieren für"): bestimmt die Standardansicht.</summary>
    public enum FolderKind
    {
        General,
        Downloads,
        Pictures,
        Music,
        Videos,
    }

    /// <summary>
    /// Ansicht eines Ordners: eigene Einstellung (gemerkt) vor Vorlage (Ordnertyp) vor globalem Standard.
    /// null-Felder bedeuten "globalen Standard verwenden".
    /// </summary>
    public sealed record FolderViewResolution(
        string? SortBy,
        bool? SortAscending,
        string? GroupBy,
        bool MixFoldersAndFiles,
        IReadOnlyList<string> ExtraDetailsColumns)
    {
        public static readonly FolderViewResolution None = new(null, null, null, false, Array.Empty<string>());
    }

    /// <summary>
    /// Vorlagen für Ordner ohne eigene Einstellung — angelehnt an die Explorer-Ordnertypen, ohne sie zu kopieren:
    /// Downloads nach Datum (gruppiert, Ordner und Dateien gemischt), Bilder neueste zuerst,
    /// Musik/Videos mit der Spalte "Länge".
    /// </summary>
    public static class FolderViewTemplates
    {
        /// <summary>Mindestens so viele Mediendateien, und mindestens dieser Anteil aller Dateien.</summary>
        public const int MinMediaFiles = 3;
        public const double MinMediaShare = 0.6;

        /// <summary>
        /// Erkennt den Ordnertyp am Inhalt (wie der Explorer): überwiegend Bilder, Musik oder Videos.
        /// </summary>
        public static FolderKind DetectKind(bool isDownloadsFolder, IEnumerable<string> fileNames)
        {
            if (isDownloadsFolder) return FolderKind.Downloads;

            var images = SearchQueryParser.GetExtensionsForKind(FileKind.Image);
            var audio = SearchQueryParser.GetExtensionsForKind(FileKind.Audio);
            var video = SearchQueryParser.GetExtensionsForKind(FileKind.Video);

            int total = 0, imageCount = 0, audioCount = 0, videoCount = 0;
            foreach (var name in fileNames)
            {
                total++;
                var ext = Path.GetExtension(name);
                if (string.IsNullOrEmpty(ext)) continue;
                if (images.Contains(ext)) imageCount++;
                else if (audio.Contains(ext)) audioCount++;
                else if (video.Contains(ext)) videoCount++;
            }
            if (total == 0) return FolderKind.General;

            bool Dominates(int count) => count >= MinMediaFiles && count >= total * MinMediaShare;
            if (Dominates(imageCount)) return FolderKind.Pictures;
            if (Dominates(audioCount)) return FolderKind.Music;
            if (Dominates(videoCount)) return FolderKind.Videos;
            return FolderKind.General;
        }

        public static FolderViewResolution For(FolderKind kind) => kind switch
        {
            FolderKind.Downloads => new("DateModified", false, "DateModified", true, Array.Empty<string>()),
            FolderKind.Pictures => new("DateModified", false, null, false, Array.Empty<string>()),
            FolderKind.Music => new(null, null, null, false, new[] { DetailsColumns.Duration }),
            FolderKind.Videos => new(null, null, null, false, new[] { DetailsColumns.Duration }),
            _ => FolderViewResolution.None,
        };
    }
}
