using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Span.Services;

namespace Span.Helpers
{
    /// <summary>
    /// Gruppenköpfe in Details/Liste/Symbole wie im Explorer:
    /// Pfeil = ein-/ausklappen, Klick auf den Kopf = alle Elemente der Gruppe auswählen (Strg = hinzufügen),
    /// Doppelklick = ein-/ausklappen, Rechtsklick = "Alle Gruppen reduzieren/erweitern".
    /// Der eingeklappte Zustand gilt pro Ansicht und Gruppenname, bis das Gruppierkriterium wechselt.
    /// </summary>
    internal static class GroupHeaderHelper
    {
        public static ItemGroup? GroupOf(object sender) => (sender as FrameworkElement)?.DataContext as ItemGroup;

        public static void Toggle(ItemGroup group, ISet<string> collapsed)
        {
            bool collapse = !group.IsCollapsed;
            group.SetCollapsed(collapse);
            if (collapse) collapsed.Add(group.Key);
            else collapsed.Remove(group.Key);
        }

        public static void SetAll(IEnumerable<ItemGroup>? groups, ISet<string> collapsed, bool collapse)
        {
            if (groups == null) return;
            foreach (var group in groups)
            {
                group.SetCollapsed(collapse);
                if (collapse) collapsed.Add(group.Key);
                else collapsed.Remove(group.Key);
            }
        }

        /// <summary>
        /// Wählt die Elemente der Gruppe aus. Mit <paramref name="setSyncing"/> wird die Auswahl-Synchronisierung der
        /// Ansicht während der Schleife unterdrückt und danach einmal über <paramref name="afterSelect"/> nachgeholt —
        /// sonst lief für jedes Element SelectionChanged → ViewModel-Sync → Statusleiste (n Elemente = n Durchläufe),
        /// und SelectedItems.Contains machte das Ganze zusätzlich quadratisch.
        /// </summary>
        public static void SelectGroup(ListViewBase host, ItemGroup group, Action<bool>? setSyncing = null, Action? afterSelect = null)
        {
            if (group.IsCollapsed) return;
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            setSyncing?.Invoke(true);
            try
            {
                if (!ctrl) host.SelectedItems.Clear();
                var alreadySelected = new HashSet<object>(host.SelectedItems, ReferenceEqualityComparer.Instance);
                foreach (var item in group)
                {
                    if (alreadySelected.Add(item))
                        host.SelectedItems.Add(item);
                }
            }
            finally
            {
                setSyncing?.Invoke(false);
            }
            afterSelect?.Invoke();
            host.Focus(FocusState.Programmatic);
        }

        public static void ShowMenu(FrameworkElement header, RightTappedRoutedEventArgs e,
            IEnumerable<ItemGroup>? groups, ISet<string> collapsed)
        {
            e.Handled = true; // kein Leerflächen-Menü
            var flyout = new MenuFlyout();
            var collapseAll = new MenuFlyoutItem { Text = LocalizationService.L("Group_CollapseAll") };
            collapseAll.Click += (_, _) => SetAll(groups, collapsed, collapse: true);
            var expandAll = new MenuFlyoutItem { Text = LocalizationService.L("Group_ExpandAll") };
            expandAll.Click += (_, _) => SetAll(groups, collapsed, collapse: false);
            flyout.Items.Add(collapseAll);
            flyout.Items.Add(expandAll);
            flyout.ShowAt(header, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(header) });
        }
    }
}
