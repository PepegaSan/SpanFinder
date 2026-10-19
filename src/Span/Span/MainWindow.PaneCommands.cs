using Span.Models;
using Span.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// Befehle zwischen zwei Bereichen (Dual/Quad), angelehnt an Total Commander / OneCommander:
    /// Auswahl in den anderen Bereich kopieren oder verschieben, anderen Bereich auf diesen Ordner setzen,
    /// Bereiche tauschen und Ordner vergleichen.
    ///
    /// "Anderer Bereich" = der zuletzt aktive andere sichtbare Bereich. Mit zwei Bereichen ist das immer der
    /// gegenüberliegende; im Vierfach-Layout klickt man erst ins Ziel und dann in die Quelle.
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>Zielbereich für die Befehle oder null, wenn nur ein Bereich sichtbar ist.</summary>
        internal ActivePane? GetOtherPane()
        {
            if (!ViewModel.IsSplitViewEnabled) return null;

            var others = ViewModel.GetSplitLayoutPanes().Where(p => p != ViewModel.ActivePane).ToList();
            if (others.Count == 0) return null;

            var previous = ViewModel.PreviousActivePane;
            return previous.HasValue && others.Contains(previous.Value) ? previous.Value : others[0];
        }

        /// <summary>
        /// Zeigt der Bereich gerade einen Ordner (nicht Start, Einstellungen, Protokoll, Papierkorb)?
        /// </summary>
        private bool TryGetBrowsingPane(ActivePane pane, out ExplorerViewModel explorer, out string path)
        {
            explorer = ViewModel.GetExplorerForPane(pane);
            path = explorer?.CurrentPath ?? string.Empty;
            if (explorer == null || !IsUsablePath(path)) return false;

            var mode = pane switch
            {
                ActivePane.Left => ViewModel.CurrentViewMode,
                ActivePane.Right => ViewModel.RightViewMode,
                _ => ViewMode.MillerColumns,
            };
            return mode is not (ViewMode.Home or ViewMode.Settings or ViewMode.ActionLog or ViewMode.RecycleBin);
        }

        /// <summary>Ordnername für Menüs und Meldungen ("C:\" bleibt "C:\").</summary>
        private static string GetFolderLabel(string path)
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>Ordner des anderen Bereichs, wenn er einen Ordner zeigt.</summary>
        internal string? GetOtherPaneFolder()
        {
            var other = GetOtherPane();
            return other.HasValue && TryGetBrowsingPane(other.Value, out _, out var path) ? path : null;
        }

        // =================================================================
        //  In anderen Bereich kopieren / verschieben (Shift+F5 / Shift+F6)
        // =================================================================

        /// <summary>
        /// Tastenkürzel-Weg: wie bei Total Commander erst das Ziel bestätigen (Enter genügt). Im Vierfach-Layout
        /// ist sonst nicht sichtbar, in welchen der drei anderen Bereiche kopiert würde — dort ist das Ziel wählbar.
        /// </summary>
        private async void HandleTransferToOtherPane(bool move)
        {
            try
            {
                var items = GetCurrentSelectedItems();
                if (items.Count == 0) return;
                var paths = items.Select(i => i.Path).ToList();

                var dest = await ConfirmTransferTargetAsync(paths.Count, move);
                if (dest == null || _isClosed) return;
                await TransferToOtherPaneAsync(paths, move, dest);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PaneCommands] transfer failed: {ex.Message}");
            }
        }

        /// <summary>Zeigt den Zielordner (bzw. die Auswahl der anderen Bereiche) und liefert ihn nach Bestätigung.</summary>
        private async Task<string?> ConfirmTransferTargetAsync(int count, bool move)
        {
            var candidates = ViewModel.GetSplitLayoutPanes()
                .Where(p => p != ViewModel.ActivePane)
                .Select(p => TryGetBrowsingPane(p, out _, out var path) ? (Pane: p, Path: path) : (Pane: p, Path: (string?)null))
                .Where(c => c.Path != null)
                .ToList();
            if (candidates.Count == 0)
            {
                ViewModel.ShowToast(_loc.Get("Toast_NoOtherPane"));
                return null;
            }

            var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8, MinWidth = 380 };
            panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = string.Format(_loc.Get(move ? "TransferDialog_MoveText" : "TransferDialog_CopyText"), count),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });

            // Optionsfelder statt Auswahlliste: alle Ziele sofort sichtbar, und Enter löst die Standard-Schaltfläche aus
            // (eine fokussierte ComboBox würde Enter zum Aufklappen verwenden).
            var targets = new Microsoft.UI.Xaml.Controls.RadioButtons();
            var defaultPane = GetOtherPane();
            foreach (var c in candidates)
            {
                targets.Items.Add(new Microsoft.UI.Xaml.Controls.RadioButton
                {
                    Content = $"{GetFolderLabel(c.Path!)}  —  {c.Path}",
                    Tag = c.Path,
                });
                if (c.Pane == defaultPane) targets.SelectedIndex = targets.Items.Count - 1;
            }
            if (targets.SelectedIndex < 0) targets.SelectedIndex = 0;
            panel.Children.Add(targets);

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = _loc.Get(move ? "Shortcut_MoveToOtherPane" : "Shortcut_CopyToOtherPane"),
                Content = panel,
                PrimaryButtonText = _loc.Get(move ? "Move" : "Copy"),
                CloseButtonText = _loc.Get("Cancel"),
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
            };

            var result = await ShowContentDialogSafeAsync(dialog);
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return null;
            return (targets.SelectedItem as Microsoft.UI.Xaml.Controls.RadioButton)?.Tag as string;
        }

        /// <summary>
        /// Kopiert/verschiebt über denselben Weg wie ein Drop (Konfliktdialog, Rückgängig, Aktualisierung aller Bereiche).
        /// Ohne <paramref name="dest"/> geht es in den anderen Bereich (Kontextmenü nennt ihn bereits).
        /// </summary>
        internal async Task TransferToOtherPaneAsync(List<string> paths, bool move, string? dest = null)
        {
            dest ??= GetOtherPaneFolder();
            if (dest == null)
            {
                ViewModel.ShowToast(_loc.Get("Toast_NoOtherPane"));
                return;
            }

            if (paths.All(p => string.Equals(
                    Path.GetDirectoryName(p)?.TrimEnd('\\'), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                ViewModel.ShowToast(_loc.Get("Toast_OtherPaneSameFolder"));
                return;
            }

            Helpers.DebugLogger.Log($"[PaneCommands] {(move ? "move" : "copy")} {paths.Count} item(s) -> {dest}");
            await HandleDropAsync(paths, dest, move ? DragDropMode.Move : DragDropMode.Copy);
        }

        // =================================================================
        //  Anderen Bereich hierher / Bereiche tauschen
        // =================================================================

        private async void HandleOtherPaneToThisFolder()
        {
            try
            {
                var other = GetOtherPane();
                if (other == null
                    || !TryGetBrowsingPane(ViewModel.ActivePane, out _, out var here)
                    || !TryGetBrowsingPane(other.Value, out var otherExplorer, out var there))
                {
                    Helpers.DebugLogger.Log($"[PaneCommands] other pane to here: no usable pane (active={ViewModel.ActivePane}, other={other})");
                    ViewModel.ShowToast(_loc.Get("Toast_NoOtherPane"));
                    return;
                }
                Helpers.DebugLogger.Log($"[PaneCommands] other pane to here: {other} '{there}' -> '{here}'");
                if (string.Equals(here, there, StringComparison.OrdinalIgnoreCase)) return;

                await otherExplorer.NavigateToPath(here);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PaneCommands] other pane to here failed: {ex.Message}");
            }
        }

        private async void HandleSwapPanes()
        {
            try
            {
                var other = GetOtherPane();
                if (other == null
                    || !TryGetBrowsingPane(ViewModel.ActivePane, out _, out _)
                    || !TryGetBrowsingPane(other.Value, out _, out _))
                {
                    ViewModel.ShowToast(_loc.Get("Toast_NoOtherPane"));
                    return;
                }

                await SwapExplorerPathsAsync(ViewModel.ActivePane, other.Value);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PaneCommands] swap failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Ordner vergleichen (Shift+F2)
        // =================================================================

        /// <summary>
        /// Vergleicht den Ordner des aktiven Bereichs mit dem des anderen und wählt hier alles aus, was dort
        /// fehlt oder dort älter ist — danach genügt Shift+F5, um die Unterschiede hinüberzukopieren.
        /// Die Gegenrichtung steht in der Meldung (Befehl im anderen Bereich ausführen, um sie auszuwählen).
        /// </summary>
        private async void HandleCompareFolders()
        {
            try
            {
                var other = GetOtherPane();
                if (other == null
                    || !TryGetBrowsingPane(ViewModel.ActivePane, out var explorer, out var here)
                    || !TryGetBrowsingPane(other.Value, out _, out var there))
                {
                    ViewModel.ShowToast(_loc.Get("Toast_NoOtherPane"));
                    return;
                }

                if (string.Equals(here, there, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.ShowToast(_loc.Get("Toast_OtherPaneSameFolder"));
                    return;
                }

                var ioHere = Helpers.LongPathHelper.ForIo(here);
                var ioThere = Helpers.LongPathHelper.ForIo(there);
                if (!Directory.Exists(ioHere) || !Directory.Exists(ioThere))
                {
                    ViewModel.ShowToast(_loc.Get("Toast_CompareLocalOnly"));
                    return;
                }

                bool showHidden = _settings.ShowHiddenFiles;
                var result = await Task.Run(() => Helpers.FolderComparer.Compare(
                    Helpers.FolderComparer.ReadFolder(ioHere, showHidden),
                    Helpers.FolderComparer.ReadFolder(ioThere, showHidden)));

                var folder = explorer.CurrentFolder;
                if (folder == null) return;
                var names = new HashSet<string>(result.ToSelectHere, StringComparer.OrdinalIgnoreCase);
                var items = folder.Children.Where(c => names.Contains(c.Name)).ToList();
                SelectItemsInActivePane(items);

                var label = GetFolderLabel(there);
                Helpers.DebugLogger.Log($"[PaneCommands] compare '{here}' vs '{there}': onlyHere={result.OnlyHere.Count} newerHere={result.NewerHere.Count} different={result.Different.Count} onlyThere={result.OnlyThere.Count} newerThere={result.NewerThere.Count}");

                if (result.IsIdentical)
                {
                    ViewModel.ShowToast(string.Format(_loc.Get("Toast_CompareIdentical"), label), 4000);
                }
                else
                {
                    ViewModel.ShowToast(string.Format(_loc.Get("Toast_CompareResult"),
                        label, items.Count, result.OnlyThere.Count, result.NewerThere.Count), 7000);
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PaneCommands] compare failed: {ex.Message}");
            }
        }

        /// <summary>Setzt die Auswahl im aktiven Bereich, egal in welcher Ansicht (Miller: letzte Spalte).</summary>
        private void SelectItemsInActivePane(IReadOnlyList<FileSystemViewModel> items)
        {
            var explorer = ViewModel.ActiveExplorer;
            var folder = explorer?.CurrentFolder;
            if (explorer == null || folder == null) return;

            if (items.Count == 0)
            {
                HandleSelectNone();
                return;
            }

            var viewMode = GetActivePaneViewMode();
            Microsoft.UI.Xaml.Controls.ListViewBase? host;
            if (viewMode == ViewMode.MillerColumns)
            {
                var listView = GetListViewForColumn(explorer.Columns.Count - 1);
                if (listView != null) ApplyMillerListViewSelection(listView, items);
                host = listView;
            }
            else
            {
                host = viewMode == ViewMode.Details ? GetActiveDetailsView()?.ItemsHost
                    : viewMode == ViewMode.List ? GetActiveListView()?.ItemsHost
                    : Helpers.ViewModeExtensions.IsIconMode(viewMode) ? GetActiveIconView()?.ItemsHost
                    : null;
                if (host != null) ApplyViewListSelection(host, items);
            }
            if (host == null) return;

            folder.SyncSelectedItems(host.SelectedItems);
            host.ScrollIntoView(items[0]);
            ViewModel.UpdateStatusBar();
            UpdateToolbarButtonStates();
        }

        // ── Kontextmenü ──

        string? Services.IContextMenuHost.OtherPaneFolderName
        {
            get
            {
                var folder = GetOtherPaneFolder();
                return folder == null ? null : GetFolderLabel(folder);
            }
        }

        async void Services.IContextMenuHost.PerformTransferToOtherPane(IReadOnlyList<string> paths, bool move)
        {
            try
            {
                if (paths.Count > 0) await TransferToOtherPaneAsync(paths.ToList(), move);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[PaneCommands] context transfer failed: {ex.Message}");
            }
        }
    }
}
