using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Span.Models;
using Span.ViewModels;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace Span
{
    /// <summary>
    /// Einstellung "Laufwerke als Ordnerbaum": die lokalen Laufwerke in der Seitenleiste als aufklappbarer Baum,
    /// wie der Navigationsbereich des Explorers. Unterordner werden beim Aufklappen nachgeladen, der Baum klappt
    /// sich bis zum geöffneten Ordner des aktiven Bereichs auf und markiert ihn. Klick öffnet, Rechtsklick zeigt
    /// das Laufwerks- bzw. Windows-Ordnermenü, Dateien können auf Knoten abgelegt werden.
    /// Der Favoriten-Baum ist davon unabhängig.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _drivesTreeMode;
        private ExplorerViewModel? _drivesTreeExplorer;
        private bool _drivesTreeRepopulatePending;

        private void ApplyDrivesTreeMode(bool showTree)
        {
            _drivesTreeMode = showTree;
            LocalDrivesList.Visibility = showTree ? Visibility.Collapsed : Visibility.Visible;
            DrivesTreeView.Visibility = showTree ? Visibility.Visible : Visibility.Collapsed;

            ViewModel.PropertyChanged -= OnViewModelPropertyChangedForDrivesTree;
            ViewModel.Drives.CollectionChanged -= OnDrivesChangedForDrivesTree;
            AttachDrivesTreeExplorer(null);

            if (!showTree)
            {
                DrivesTreeView.RootNodes.Clear();
                return;
            }

            ViewModel.PropertyChanged += OnViewModelPropertyChangedForDrivesTree;
            ViewModel.Drives.CollectionChanged += OnDrivesChangedForDrivesTree;
            PopulateDrivesTree();
            AttachDrivesTreeExplorer(ViewModel.ActiveExplorer);
        }

        private void PopulateDrivesTree()
        {
            DrivesTreeView.RootNodes.Clear();
            foreach (var drive in ViewModel.Drives)
            {
                // Keine Vorabprüfung auf Unterordner: schlafende USB-Platten würden sonst geweckt
                DrivesTreeView.RootNodes.Add(new TreeViewNode { Content = drive, HasUnrealizedChildren = true });
            }
            SyncDrivesTreeToPath(ViewModel.ActiveExplorer?.CurrentPath);
        }

        private void OnDrivesChangedForDrivesTree(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!_drivesTreeMode || _drivesTreeRepopulatePending) return;
            _drivesTreeRepopulatePending = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _drivesTreeRepopulatePending = false;
                if (_drivesTreeMode && !_isClosed) PopulateDrivesTree();
            });
        }

        // ── Dem geöffneten Ordner folgen ──

        private void OnViewModelPropertyChangedForDrivesTree(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveExplorer))
                AttachDrivesTreeExplorer(ViewModel.ActiveExplorer);
        }

        private void AttachDrivesTreeExplorer(ExplorerViewModel? explorer)
        {
            if (ReferenceEquals(_drivesTreeExplorer, explorer)) return;
            if (_drivesTreeExplorer != null) _drivesTreeExplorer.PropertyChanged -= OnExplorerPathChangedForDrivesTree;
            _drivesTreeExplorer = explorer;
            if (explorer == null) return;
            explorer.PropertyChanged += OnExplorerPathChangedForDrivesTree;
            SyncDrivesTreeToPath(explorer.CurrentPath);
        }

        private void OnExplorerPathChangedForDrivesTree(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ExplorerViewModel.CurrentPath)) return;
            var path = (sender as ExplorerViewModel)?.CurrentPath;
            Helpers.DispatcherHelper.SafeEnqueue(DispatcherQueue, () => SyncDrivesTreeToPath(path));
        }

        /// <summary>Klappt den Baum bis zum Ordner auf (lädt dabei die nötigen Ebenen) und markiert ihn.</summary>
        private void SyncDrivesTreeToPath(string? path)
        {
            if (!_drivesTreeMode || _isClosed || string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') return;

            try
            {
                var root = DrivesTreeView.RootNodes.FirstOrDefault(n =>
                    n.Content is DriveItem d && path.StartsWith(d.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (root?.Content is not DriveItem drive) return;

                var node = root;
                var rest = path.Substring(drive.Path.TrimEnd('\\').Length)
                    .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var segment in rest)
                {
                    EnsureDrivesTreeChildren(node);
                    node.IsExpanded = true;
                    var child = node.Children.FirstOrDefault(c =>
                        c.Content is SidebarFolderNode f && string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
                    if (child == null) break; // z. B. versteckter Ordner bei ausgeblendeten versteckten Dateien
                    node = child;
                }

                DrivesTreeView.SelectedNode = node;
                var target = node;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (DrivesTreeView.ContainerFromNode(target) is UIElement container)
                        container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
                });
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DrivesTree] sync failed for {path}: {ex.Message}");
            }
        }

        // ── Unterordner nachladen ──

        private void OnDrivesTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            EnsureDrivesTreeChildren(args.Node);
        }

        private void EnsureDrivesTreeChildren(TreeViewNode node)
        {
            if (!node.HasUnrealizedChildren) return;
            node.HasUnrealizedChildren = false;

            var path = GetDrivesTreePath(node);
            if (string.IsNullOrEmpty(path)) return;

            bool showHidden = _settings.ShowHiddenFiles;
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
                var dirs = new DirectoryInfo(Helpers.LongPathHelper.ForIo(path)).EnumerateDirectories("*", options)
                    .Where(d => !Helpers.FileVisibility.ShouldHide(d.Attributes, showHidden))
                    .OrderBy(d => d.Name, Helpers.NaturalStringComparer.Instance)
                    .ToList();
                foreach (var dir in dirs)
                {
                    var content = new SidebarFolderNode
                    {
                        Name = dir.Name,
                        Path = Path.Combine(path, dir.Name),
                        IconGlyph = Services.IconService.Current?.FolderGlyph ?? "",
                    };
                    node.Children.Add(new TreeViewNode { Content = content, HasUnrealizedChildren = true });
                    content.RequestCustomIconLoad();
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DrivesTree] list failed for {path}: {ex.Message}");
            }
        }

        private static string GetDrivesTreePath(TreeViewNode node) => node.Content switch
        {
            DriveItem d => d.Path,
            SidebarFolderNode f => f.Path,
            _ => string.Empty,
        };

        // ── Öffnen ──

        private void OnDrivesTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            var content = (args.InvokedItem as TreeViewNode)?.Content ?? args.InvokedItem;
            try
            {
                if (content is DriveItem drive)
                {
                    ViewModel.OpenDrive(drive);
                    UpdateViewModeVisibility();
                    if (ViewModel.CurrentViewMode == ViewMode.MillerColumns) FocusColumnAsync(0);
                    else FocusActiveView();
                }
                else if (content is SidebarFolderNode folder && Directory.Exists(Helpers.LongPathHelper.ForIo(folder.Path)))
                {
                    // Wie im Favoriten-Baum: aus Start/Papierkorb in die Ordneransicht wechseln
                    var activeViewMode = GetActivePaneViewMode();
                    if (activeViewMode == ViewMode.Home || activeViewMode == ViewMode.RecycleBin)
                    {
                        ViewModel.SwitchViewMode(ViewModel.ResolveViewModeFromHome());
                        UpdateViewModeVisibility();
                    }
                    _ = ViewModel.ActiveExplorer?.NavigateTo(new FolderItem { Name = folder.Name, Path = folder.Path });
                    if (ViewModel.CurrentViewMode == ViewMode.MillerColumns) FocusColumnAsync(0);
                    else FocusActiveView();
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DrivesTree] invoke failed: {ex.Message}");
            }
        }

        // ── Kontextmenü ──

        private void OnDrivesTreeItemRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not TreeViewNode node) return;
            e.Handled = true;

            if (node.Content is DriveItem drive)
            {
                var flyout = _contextMenuService.BuildDriveMenu(drive, this);
                flyout.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(element) });
                return;
            }

            if (node.Content is not SidebarFolderNode folder) return;

            // Wie im Explorer-Navigationsbereich: das Windows-Kontextmenü des Ordners (mit Span-Einträgen unten)
            if (_contextMenuService.TryShowNativeContextMenu(folder.Path, this)) return;

            var menu = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = _loc.Get("Open"), Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => _ = ViewModel.ActiveExplorer?.NavigateTo(new FolderItem { Name = folder.Name, Path = folder.Path });
            var newTab = new MenuFlyoutItem { Text = _loc.Get("OpenInNewTab"), Icon = new FontIcon { Glyph = "" } };
            newTab.Click += (_, _) => ((Services.IContextMenuHost)this).PerformOpenInNewTab(folder.Path);
            var copyPath = new MenuFlyoutItem { Text = _loc.Get("CopyPath"), Icon = new FontIcon { Glyph = "" } };
            copyPath.Click += (_, _) => ((Services.IContextMenuHost)this).PerformCopyPath(folder.Path);
            menu.Items.Add(open);
            menu.Items.Add(newTab);
            menu.Items.Add(copyPath);
            menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(element) });
        }

        // ── Ablegen (Drag & Drop) auf Laufwerke und Ordner ──

        private bool TryGetDrivesTreeDropTarget(object sender, out Panel panel, out string path, out string name, out DriveItem? drive)
        {
            panel = (sender as Panel)!;
            path = name = string.Empty;
            drive = null;
            if (panel?.DataContext is not TreeViewNode node) return false;
            switch (node.Content)
            {
                case DriveItem d: drive = d; path = d.Path; name = d.Name; return true;
                case SidebarFolderNode f: path = f.Path; name = f.Name; return true;
                default: return false;
            }
        }

        private void OnDrivesTreeItemDragOver(object sender, DragEventArgs e)
        {
            if (!TryGetDrivesTreeDropTarget(sender, out var panel, out var path, out var name, out var drive)) return;
            TrackDragOver(e);
            if (!HasDroppableData(e)) return;

            e.Handled = true;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;

            var mode = ResolveDragDropMode(e, path);
            bool blocked = (drive != null && !IsDriveDropAvailable(drive))
                || !IsDropTargetValid(e, path)
                || (mode == DragDropMode.Move && IsSameFolderDrop(e, path));
            if (blocked)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                HideDragTooltip();
                return;
            }

            e.AcceptedOperation = ToAcceptedOperation(mode);
            UpdateDragTooltip(GetDragCaption(mode, name), e, panel);
            panel.Background = GetThemeBrush("SpanBgHoverBrush");
        }

        private async void OnDrivesTreeItemDrop(object sender, DragEventArgs e)
        {
            if (!TryGetDrivesTreeDropTarget(sender, out var panel, out var path, out _, out var drive)) return;
            e.Handled = true;
            panel.Background = _transparentBrush;
            if (drive != null && !IsDriveDropAvailable(drive)) return;
            await CompleteDropAsync(e, path);
        }

        private void OnDrivesTreeItemDragLeave(object sender, DragEventArgs e)
        {
            if (sender is Panel panel) panel.Background = _transparentBrush;
            HideDragTooltip();
        }
    }
}
