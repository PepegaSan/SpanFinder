using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml.Media;
using Span.Models;
using Span.Services;
using Span.ViewModels;

namespace Span.Helpers
{
    /// <summary>
    /// Group header class for grouped items in GridView/ListView.
    /// Einklappbar wie im Explorer: eingeklappt bleibt die Gruppe (Kopf mit Anzahl) in der Liste, enthält aber
    /// keine Elemente. Die Gruppe wird dabei nur geleert/gefüllt statt die ItemsSource zu ersetzen,
    /// damit die Scrollposition erhalten bleibt.
    /// </summary>
    public class ItemGroup : ObservableCollection<FileSystemViewModel>
    {
        private readonly List<FileSystemViewModel> _all;

        /// <summary>Anzeigename der Gruppe (ohne Sortierpräfix und ohne Anzahl).</summary>
        public string Key { get; }

        /// <summary>Alle Elemente der Gruppe, auch wenn sie eingeklappt ist.</summary>
        public IReadOnlyList<FileSystemViewModel> AllItems => _all;

        public string CountText => $"({_all.Count})";

        public bool IsCollapsed { get; private set; }

        /// <summary>Segoe Fluent: ChevronDown (offen) / ChevronRight (eingeklappt).</summary>
        public string ChevronGlyph => IsCollapsed ? "\uE76C" : "\uE70D";

        public ItemGroup(string key, IEnumerable<FileSystemViewModel> items, bool collapsed = false)
        {
            Key = key;
            _all = items.ToList();
            IsCollapsed = collapsed;
            if (!collapsed)
            {
                foreach (var item in _all) Items.Add(item);
            }
        }

        public void SetCollapsed(bool collapsed)
        {
            if (IsCollapsed == collapsed) return;
            IsCollapsed = collapsed;

            // Ein Reset statt tausender Einzel-Events beim Ein-/Ausklappen großer Gruppen.
            Items.Clear();
            if (!collapsed)
            {
                foreach (var item in _all) Items.Add(item);
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsCollapsed)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChevronGlyph)));
        }
    }

    /// <summary>
    /// Shared group key logic for Icon/List/Details views.
    /// Returns keys prefixed with sort order (e.g. "01|Today") for correct ordering.
    /// Use <see cref="StripSortPrefix"/> to get the display label.
    /// </summary>
    public static class GroupByHelper
    {
        /// <summary>
        /// Returns a sort-prefixed group key like "03|Earlier this week".
        /// The prefix ensures chronological/logical ordering when sorted alphabetically.
        /// </summary>
        public static string GetGroupKey(FileSystemViewModel item, string groupBy)
        {
            switch (groupBy)
            {
                case "Name":
                    var firstChar = !string.IsNullOrEmpty(item.Name)
                        ? char.ToUpperInvariant(item.Name[0]).ToString()
                        : "#";
                    return char.IsLetter(firstChar[0]) ? firstChar : "#";

                case "Type":
                    if (item is FolderViewModel) return LocalizationService.L("Group_Folder");
                    return string.IsNullOrEmpty(item.FileType)
                        ? LocalizationService.L("Group_Unknown")
                        : item.FileType.ToUpperInvariant();

                case "DateModified":
                    return GetDateGroupKey(item.DateModifiedValue);

                case "DateCreated":
                    return GetDateGroupKey(item.DateCreatedValue);

                case "Size":
                    return GetSizeGroupKey(item);

                case "Tag":
                    return GetTagGroupKey(item.ColorTag);

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Baut die Gruppen für Details/Liste/Symbole. Gruppen, deren Name in <paramref name="collapsed"/>
        /// steht, kommen eingeklappt. Für "Tag" werden die Farb-Tags aller Elemente vorher geladen
        /// (sonst kennen nur die bereits sichtbaren Zeilen ihren Tag).
        /// </summary>
        public static List<ItemGroup> BuildGroups(
            IEnumerable<FileSystemViewModel> items, string groupBy,
            ISet<string>? collapsed = null, FolderViewModel? folder = null)
        {
            var list = items as IList<FileSystemViewModel> ?? items.ToList();
            if (groupBy == "Tag" && folder != null)
            {
                foreach (var item in list) folder.InjectColorTagIfNeeded(item);
            }

            return list
                .GroupBy(item => GetGroupKey(item, groupBy))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var label = StripSortPrefix(g.Key);
                    return new ItemGroup(label, g, collapsed?.Contains(label) == true);
                })
                .ToList();
        }

        /// <summary>
        /// Strips the "NN|" sort prefix from a group key, returning the display label.
        /// If no prefix is present, returns the key as-is.
        /// </summary>
        public static string StripSortPrefix(string key)
        {
            if (key.Length > 3 && key[2] == '|')
                return key.Substring(3);
            return key;
        }

        private static string GetTagGroupKey(ItemColorTag tag) => tag switch
        {
            ItemColorTag.Red => "01|" + LocalizationService.L("ColorTag_Red"),
            ItemColorTag.Orange => "02|" + LocalizationService.L("ColorTag_Orange"),
            ItemColorTag.Yellow => "03|" + LocalizationService.L("ColorTag_Yellow"),
            ItemColorTag.Green => "04|" + LocalizationService.L("ColorTag_Green"),
            ItemColorTag.Blue => "05|" + LocalizationService.L("ColorTag_Blue"),
            ItemColorTag.Purple => "06|" + LocalizationService.L("ColorTag_Purple"),
            ItemColorTag.Gray => "07|" + LocalizationService.L("ColorTag_Gray"),
            _ => "09|" + LocalizationService.L("Group_NoTag"),
        };

        private static string GetDateGroupKey(DateTime date)
        {
            // Kein Datum (z. B. Erstelldatum bei Remote-/Suchergebnissen) — nicht als "vor langer Zeit" einsortieren
            if (date == DateTime.MinValue || date.Year < 1980)
                return "08|" + LocalizationService.L("Group_Unknown");

            var now = DateTime.Now;
            var today = now.Date;

            // Today
            if (date.Date == today)
                return "01|" + LocalizationService.L("Group_Today");

            // Yesterday
            if (date.Date == today.AddDays(-1))
                return "02|" + LocalizationService.L("Group_Yesterday");

            // Earlier this week (same week, but not today/yesterday)
            // Week starts on Sunday for DayOfWeek enum
            var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
            if (date.Date >= startOfWeek)
                return "03|" + LocalizationService.L("Group_ThisWeek");

            // Last week
            var startOfLastWeek = startOfWeek.AddDays(-7);
            if (date.Date >= startOfLastWeek)
                return "04|" + LocalizationService.L("Group_LastWeek");

            // Earlier this month
            var startOfMonth = new DateTime(now.Year, now.Month, 1);
            if (date.Date >= startOfMonth)
                return "05|" + LocalizationService.L("Group_ThisMonth");

            // Last month
            var startOfLastMonth = startOfMonth.AddMonths(-1);
            if (date.Date >= startOfLastMonth)
                return "06|" + LocalizationService.L("Group_LastMonth");

            // Older
            return "07|" + LocalizationService.L("Group_Older");
        }

        private static string GetSizeGroupKey(FileSystemViewModel item)
        {
            if (item is FolderViewModel)
                return "01|" + LocalizationService.L("Group_Folders");

            var size = item.SizeValue;
            if (size == 0) return "02|" + LocalizationService.L("Group_Empty");
            if (size < 16 * 1024) return "03|" + LocalizationService.L("Group_Tiny");
            if (size < 1024 * 1024) return "04|" + LocalizationService.L("Group_Small");
            if (size < 128 * 1024 * 1024) return "05|" + LocalizationService.L("Group_Medium");
            if (size < 1024L * 1024 * 1024) return "06|" + LocalizationService.L("Group_Large");
            return "07|" + LocalizationService.L("Group_Huge");
        }
    }

    /// <summary>
    /// Compact relative age labels for Miller columns (One Commander style):
    /// pastel badge background + dark text (not colored glyphs).
    /// </summary>
    internal static class RelativeAgeHelper
    {
        // Soft badge fills (OC-like); pair with dark foreground in XAML.
        private static readonly SolidColorBrush HoursBrush =
            new(Windows.UI.Color.FromArgb(255, 232, 210, 150)); // peach / pale yellow
        private static readonly SolidColorBrush DaysBrush =
            new(Windows.UI.Color.FromArgb(255, 140, 210, 205)); // aqua / cyan
        private static readonly SolidColorBrush WeeksBrush =
            new(Windows.UI.Color.FromArgb(255, 230, 175, 120)); // soft orange
        // Older ages stay cool-toned but keep a distinct hue — plain grays vanished on gray-blue cards.
        private static readonly SolidColorBrush MonthsBrush =
            new(Windows.UI.Color.FromArgb(255, 196, 178, 240)); // soft lilac
        private static readonly SolidColorBrush YearsBrush =
            new(Windows.UI.Color.FromArgb(255, 150, 180, 226)); // steel blue

        public readonly record struct RelativeAge(string Text, Brush Brush);

        public static RelativeAge Format(DateTime modified)
        {
            if (modified == DateTime.MinValue || modified.Year < 1980)
                return new RelativeAge(string.Empty, MonthsBrush);

            var age = DateTime.Now - modified;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;

            if (age.TotalHours < 1)
            {
                var mins = Math.Max(1, (int)age.TotalMinutes);
                return new RelativeAge(
                    string.Format(LocalizationService.L("Age_Minutes"), mins),
                    HoursBrush);
            }

            if (age.TotalDays < 1)
            {
                var hours = Math.Max(1, (int)age.TotalHours);
                return new RelativeAge(
                    string.Format(LocalizationService.L("Age_Hours"), hours),
                    HoursBrush);
            }

            if (age.TotalDays < 7)
            {
                var days = Math.Max(1, (int)age.TotalDays);
                return new RelativeAge(
                    string.Format(LocalizationService.L("Age_Days"), days),
                    DaysBrush);
            }

            if (age.TotalDays < 30)
            {
                var weeks = Math.Max(1, (int)(age.TotalDays / 7));
                return new RelativeAge(
                    string.Format(LocalizationService.L("Age_Weeks"), weeks),
                    WeeksBrush);
            }

            if (age.TotalDays < 365)
            {
                var months = Math.Max(1, (int)(age.TotalDays / 30));
                return new RelativeAge(
                    string.Format(LocalizationService.L("Age_Months"), months),
                    MonthsBrush);
            }

            var years = Math.Max(1, (int)(age.TotalDays / 365));
            return new RelativeAge(
                string.Format(LocalizationService.L("Age_Years"), years),
                YearsBrush);
        }
    }
}
