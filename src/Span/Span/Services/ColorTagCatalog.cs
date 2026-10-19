using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Span.Services
{
    /// <summary>
    /// Ein Farb-Tag. Die Id wird an Dateien/Ordnern gespeichert (item-annotations.json) und ändert sich nie —
    /// Name und Farbe sind frei änderbar.
    /// </summary>
    public sealed class ColorTagDefinition
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>Eigener Name; leer = Standardname in der App-Sprache (nur eingebaute Tags).</summary>
        public string? Name { get; set; }

        /// <summary>Farbe als "#RRGGBB".</summary>
        public string Color { get; set; } = "#808080";
    }

    /// <summary>
    /// Die Farb-Tags (OneCommander-Stil): eingebaute Farben mit änderbarem Namen und Farbe, dazu eigene Tags.
    /// Die Ids der eingebauten Tags sind die früheren Enum-Namen ("Red", "Green", …), damit vorhandene
    /// Zuweisungen in item-annotations.json gültig bleiben. Gespeichert wird in den Einstellungen (ColorTagsJson).
    /// </summary>
    public sealed class ColorTagCatalog
    {
        /// <summary>Höchstzahl der Tags — Gruppenköpfe sortieren mit zweistelligem Präfix.</summary>
        public const int MaxTags = 40;

        private static readonly (string Id, string Color)[] s_builtIns =
        {
            ("Red", "#E81123"),
            ("Orange", "#F7630C"),
            ("Yellow", "#FFB900"),
            ("Green", "#107C10"),
            ("Blue", "#0078D4"),
            ("Purple", "#881798"),
            ("Gray", "#605E5C"),
        };

        private static ColorTagCatalog? s_current;

        /// <summary>Katalog der App (beim Start aus den Einstellungen gesetzt).</summary>
        public static ColorTagCatalog Current
        {
            get => s_current ??= new ColorTagCatalog(null, key => key);
            set => s_current = value;
        }

        private readonly List<ColorTagDefinition> _tags = new();
        private readonly Func<string, string> _localize;

        /// <summary>Name, Farbe, Reihenfolge oder Anzahl geändert.</summary>
        public event Action? Changed;

        /// <param name="json">Gespeicherte Liste; null/ungültig = Standard-Tags.</param>
        /// <param name="localize">Lokalisierung für die Standardnamen (Schlüssel "ColorTag_Red" usw.).</param>
        public ColorTagCatalog(string? json, Func<string, string> localize)
        {
            _localize = localize;
            if (!TryLoad(json))
                _tags.AddRange(s_builtIns.Select(b => new ColorTagDefinition { Id = b.Id, Color = b.Color }));
        }

        public IReadOnlyList<ColorTagDefinition> Tags => _tags;

        public static bool IsBuiltIn(string id) => s_builtIns.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

        public ColorTagDefinition? Find(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var tag in _tags)
                if (string.Equals(tag.Id, id, StringComparison.OrdinalIgnoreCase)) return tag;
            return null;
        }

        public int IndexOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _tags.Count; i++)
                if (string.Equals(_tags[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public string DisplayName(ColorTagDefinition tag)
        {
            if (!string.IsNullOrWhiteSpace(tag.Name)) return tag.Name!.Trim();
            return IsBuiltIn(tag.Id) ? _localize("ColorTag_" + tag.Id) : tag.Id;
        }

        /// <summary>Anzeigename zur Id; null, wenn es den Tag nicht (mehr) gibt.</summary>
        public string? DisplayName(string? id)
        {
            var tag = Find(id);
            return tag == null ? null : DisplayName(tag);
        }

        public ColorTagDefinition Add(string name, string color)
        {
            if (_tags.Count >= MaxTags) throw new InvalidOperationException("Too many color tags");
            // Nie wiederverwendet: alte Zuweisungen eines gelöschten Tags sollen nicht an einem neuen Tag landen
            var tag = new ColorTagDefinition { Id = "Tag-" + Guid.NewGuid().ToString("N")[..8], Name = name?.Trim(), Color = NormalizeColor(color) ?? "#808080" };
            _tags.Add(tag);
            Changed?.Invoke();
            return tag;
        }

        /// <summary>Name und Farbe ändern. Leerer Name bei eingebauten Tags = wieder Standardname.</summary>
        public void Update(string id, string? name, string color)
        {
            var tag = Find(id);
            if (tag == null) return;
            var trimmed = name?.Trim();
            // Standardname nicht als eigenen Namen festschreiben — sonst wechselt er nicht mehr mit der Sprache
            if (IsBuiltIn(tag.Id) && (string.IsNullOrEmpty(trimmed) || trimmed == _localize("ColorTag_" + tag.Id)))
                trimmed = null;
            else if (string.IsNullOrEmpty(trimmed))
                trimmed = tag.Name;
            tag.Name = trimmed;
            tag.Color = NormalizeColor(color) ?? tag.Color;
            Changed?.Invoke();
        }

        public bool Remove(string id)
        {
            int i = IndexOf(id);
            if (i < 0) return false;
            _tags.RemoveAt(i);
            Changed?.Invoke();
            return true;
        }

        public void Move(string id, int newIndex)
        {
            int i = IndexOf(id);
            if (i < 0) return;
            newIndex = Math.Clamp(newIndex, 0, _tags.Count - 1);
            if (newIndex == i) return;
            var tag = _tags[i];
            _tags.RemoveAt(i);
            _tags.Insert(newIndex, tag);
            Changed?.Invoke();
        }

        /// <summary>Eingebaute Tags mit Standardname und -farbe wiederherstellen (fehlende wieder vorn), eigene bleiben.</summary>
        public void ResetBuiltIns()
        {
            for (int b = s_builtIns.Length - 1; b >= 0; b--)
            {
                var (id, color) = s_builtIns[b];
                var tag = Find(id);
                if (tag == null)
                {
                    _tags.Insert(0, new ColorTagDefinition { Id = id, Color = color });
                }
                else
                {
                    tag.Name = null;
                    tag.Color = color;
                }
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Passt ein <c>tag:</c>-Suchtext auf den Tag? "*" = irgendein Tag; sonst Name beginnt mit dem Text
        /// oder die Id ist gleich (tag:red findet "Rot" auch in der deutschen Oberfläche). Groß/klein egal.
        /// </summary>
        public bool Matches(string filter, string? tagId)
        {
            var tag = Find(tagId);
            if (tag == null) return false;
            filter = filter.Trim();
            if (filter == "*") return true;
            if (string.Equals(tag.Id, filter, StringComparison.OrdinalIgnoreCase)) return true;
            return DisplayName(tag).StartsWith(filter, StringComparison.CurrentCultureIgnoreCase);
        }

        public string ToJson() => JsonSerializer.Serialize(_tags);

        private bool TryLoad(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var loaded = JsonSerializer.Deserialize<List<ColorTagDefinition>>(json);
                if (loaded == null) return false;
                foreach (var tag in loaded)
                {
                    if (string.IsNullOrWhiteSpace(tag.Id) || Find(tag.Id) != null) continue;
                    tag.Color = NormalizeColor(tag.Color) ?? "#808080";
                    _tags.Add(tag);
                    if (_tags.Count >= MaxTags) break;
                }
                return true; // auch leer gültig: der Benutzer hat alle Tags gelöscht
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[ColorTagCatalog] load failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>"#RRGGBB" (auch ohne #, auch "#AARRGGBB" — Alpha wird ignoriert); null bei ungültiger Eingabe.</summary>
        public static string? NormalizeColor(string? color)
        {
            if (!TryParseColor(color, out var r, out var g, out var b)) return null;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        public static bool TryParseColor(string? color, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(color)) return false;
            var hex = color.Trim().TrimStart('#');
            if (hex.Length == 8) hex = hex.Substring(2);
            if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return false;
            r = (byte)(value >> 16);
            g = (byte)(value >> 8);
            b = (byte)value;
            return true;
        }
    }
}
