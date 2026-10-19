using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Span.Services;

namespace Span.Helpers
{
    /// <summary>
    /// Pinsel der Farb-Tags (einer pro Tag, geteilt von allen Zeilen). Wird geleert, wenn sich der Katalog ändert.
    /// Nur auf dem UI-Thread verwenden.
    /// </summary>
    public static class ColorTagBrushes
    {
        private static readonly Dictionary<string, SolidColorBrush> s_cache = new(StringComparer.OrdinalIgnoreCase);
        private static ColorTagCatalog? s_catalog;

        /// <summary>Pinsel zur Tag-Id; null, wenn es den Tag nicht gibt.</summary>
        public static SolidColorBrush? Get(string? tagId)
        {
            var catalog = ColorTagCatalog.Current;
            if (!ReferenceEquals(catalog, s_catalog))
            {
                if (s_catalog != null) s_catalog.Changed -= Invalidate;
                s_catalog = catalog;
                catalog.Changed += Invalidate;
                s_cache.Clear();
            }

            var tag = catalog.Find(tagId);
            if (tag == null) return null;
            if (s_cache.TryGetValue(tag.Id, out var brush)) return brush;

            brush = Create(tag.Color);
            s_cache[tag.Id] = brush;
            return brush;
        }

        public static SolidColorBrush Create(string color)
        {
            ColorTagCatalog.TryParseColor(color, out var r, out var g, out var b);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }

        private static void Invalidate() => s_cache.Clear();
    }
}
