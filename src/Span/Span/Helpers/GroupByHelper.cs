using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Span.Services;
using Span.ViewModels;

namespace Span.Helpers
{
    /// <summary>
    /// Group header class for grouped items in GridView/ListView.
    /// </summary>
    public class ItemGroup : List<FileSystemViewModel>
    {
        public string Key { get; }
        public new int Count => base.Count;

        public ItemGroup(string key, IEnumerable<FileSystemViewModel> items) : base(items)
        {
            Key = key;
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

                case "Size":
                    return GetSizeGroupKey(item);

                default:
                    return string.Empty;
            }
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

        private static string GetDateGroupKey(DateTime date)
        {
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
        private static readonly SolidColorBrush MonthsBrush =
            new(Windows.UI.Color.FromArgb(255, 170, 175, 185)); // muted gray-blue
        private static readonly SolidColorBrush YearsBrush =
            new(Windows.UI.Color.FromArgb(255, 140, 145, 155)); // darker muted

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
