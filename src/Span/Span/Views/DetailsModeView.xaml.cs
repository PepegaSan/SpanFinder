using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Span.Helpers;
using Span.Services;
using Span.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Span.Views
{
    /// <summary>
    /// Details 뷰 모드 UserControl.
    /// 파일/폴더를 컬럼 헤더(이름, 날짜, 유형, 크기, Git 상태) 기반의 리스트로 표시한다.
    /// GridSplitter 컬럼 리사이즈, 정렬, 그룹화, 필터링, 인라인 이름 변경,
    /// 러버 밴드 선택, 밀도 설정, 컬럼 표시/숨기기 기능을 포함한다.
    /// </summary>
    public sealed partial class DetailsModeView : UserControl
    {
        public ContextMenuService? ContextMenuService { get; set; }
        public IContextMenuHost? ContextMenuHost { get; set; }
        public IntPtr OwnerHwnd { get; set; }
        public bool IsRightPane { get; set; }

        /// <summary>
        /// 외부(MainWindow)에서 rename TextBox 포커스를 위해 컨테이너에 접근할 수 있도록 노출.
        /// </summary>
        internal Microsoft.UI.Xaml.Controls.ListViewBase ItemsHost => DetailsListView;

        /// <summary>
        /// true면 Loaded에서 auto-resolve 건너뜀 (코드에서 ViewModel을 직접 설정한 인스턴스).
        /// XAML 정의 인스턴스(첫 번째 탭)는 false (기본값) → 기존 동작 유지.
        /// </summary>
        public bool IsManualViewModel { get; set; }

        private ExplorerViewModel? _viewModel;

        /// <summary>hit-test 기반 D&amp;D에서 마지막으로 하이라이트된 Grid 추적</summary>
        private Grid? _lastHighlightedGrid;

        /// <summary>
        /// true로 설정하면 ViewModel 할당 시 SortItems를 건너뛴다 (탭 전환 최적화).
        /// 이미 정렬된 데이터를 불필요하게 Clear+Add하는 O(N) 작업 방지.
        /// </summary>
        public bool SuppressSortOnAssign { get; set; }

        public ExplorerViewModel? ViewModel
        {
            get => _viewModel;
            set
            {
                if (_viewModel != null) _viewModel.PropertyChanged -= OnExplorerPropertyChanged;
                _viewModel = value;
                if (_viewModel != null) _viewModel.PropertyChanged += OnExplorerPropertyChanged;
                RootGrid.DataContext = _viewModel;

                if (_viewModel != null && _isLoaded && !SuppressSortOnAssign)
                {
                    ApplyCurrentView();
                }
                SuppressSortOnAssign = false;
            }
        }

        private string _currentSortBy = "Name";
        private bool _isAscending = true;
        private bool _isLoaded = false;
        private ApplicationDataContainer? _localSettings;
        private SettingsService? _settings;

        // 컬럼 너비 동기화 디바운스 — GridSplitter 드래그 시 인접 컬럼 콜백이
        // 순차 발생하여 중간 상태가 렌더링되는 것을 방지
        private bool _columnWidthUpdatePending = false;

        /// <summary>
        /// 헤더 컬럼 하나의 상태 (DetailsColumns.All과 같은 순서). 보이는 컬럼만 헤더에
        /// 구분선 열 + 데이터 열로 추가되므로, 숨긴 컬럼 옆의 구분선이 0px 열을 넓히는 일이 없다.
        /// </summary>
        private sealed class ColumnSlot
        {
            public ColumnSlot(DetailsColumnInfo info)
            {
                Info = info;
                Width = info.DefaultWidth;
            }

            public DetailsColumnInfo Info { get; }
            public bool Visible;
            /// <summary>데이터 열 너비 (구분선 제외). 숨겨도 유지되어 다시 켤 때 복원된다.</summary>
            public double Width;
            /// <summary>항목 셀 너비 = 앞 구분선 + 데이터 열.</summary>
            public double CellTotalWidth;
            public ColumnDefinition? SplitterColumn;
            public ColumnDefinition? DataColumn;
            public long SplitterToken;
            public long DataToken;
            public Button? HeaderButton;
        }

        private readonly ColumnSlot[] _slots = DetailsColumns.All.Select(c => new ColumnSlot(c)).ToArray();
        private ColumnSlot Slot(string id) => _slots[DetailsColumns.IndexOf(id)];
        private bool IsColumnVisible(string id) => Slot(id).Visible;
        private bool AnyShellColumnVisible => _slots.Any(s => s.Visible && s.Info.IsShellProperty);

        /// <summary>마지막으로 측정한 구분선 열 너비 (DPI 반올림 포함). 측정 전에는 GridSplitter.Width.</summary>
        private double _splitterWidth = 2;
        private bool _isRebuildingHeader;

        // 레이아웃 저장: 너비 드래그는 디바운스, 다른 인스턴스(탭/분할 창)의 저장은 SettingChanged로 반영
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutSaveTimer;
        private string? _lastLayoutJson;
        private bool _isSavingLayout;

        // 셸 속성(크기·길이·작성자·제목) 비동기 로드
        private System.Threading.CancellationTokenSource _shellCts = new();
        private readonly Dictionary<FileViewModel, Task> _pendingShellReads = new();
        private FolderViewModel? _shellFolder;

        // Guard against double cleanup (Cleanup() from OnClosed + OnUnloaded from visual tree teardown)
        private bool _isCleanedUp = false;

        // Rubber-band selection
        private Helpers.RubberBandSelectionHelper? _rubberBandHelper;
        private Helpers.RightButtonDragHelper? _rightDragHelper;
        private bool _isSyncingSelection;

        // ── Feature #29: Group By ──
        private string _currentGroupBy = "None";

        // ── Feature #31: Column Filters ──
        private string _nameFilter = string.Empty;
        private string _dateFilter = string.Empty; // "Today", "ThisWeek", "ThisMonth", "ThisYear", "Older", or ""
        private string _typeFilter = string.Empty;
        private string _sizeFilter = string.Empty; // "Empty", "Tiny", "Small", "Medium", "Large", "Huge", or ""

        // Unfiltered items cache (for re-applying filters)
        private List<FileSystemViewModel>? _unfilteredItems;

        private LocalizationService? _loc;

        public DetailsModeView()
        {
            try
            {
                _localSettings = ApplicationData.Current.LocalSettings;
            }
            catch
            {
                // LocalSettings corrupted/unavailable — sort/column settings won't persist but app won't crash
            }

            this.InitializeComponent();

            this.Loaded += (s, e) =>
            {
                _isLoaded = true;
                _isCleanedUp = false; // Allow cleanup on next Unloaded

                // Apply ShowCheckboxes setting
                try
                {
                    _settings = App.Current.Services.GetService(typeof(SettingsService)) as SettingsService;
                    if (_settings != null)
                    {
                        ApplyCheckboxMode(_settings.ShowCheckboxes);
                        _settings.SettingChanged += OnSettingChanged;
                    }
                }
                catch (Exception ex) { Helpers.DebugLogger.Log($"[DetailsModeView] Settings init error: {ex.Message}"); }

                try
                {
                    _loc = App.Current.Services.GetService(typeof(LocalizationService)) as LocalizationService;
                    if (_loc != null) _loc.LanguageChanged += LocalizeUI;
                }
                catch (Exception ex) { Helpers.DebugLogger.Log($"[DetailsModeView] Localization init error: {ex.Message}"); }

                // 컬럼 표시/너비/정렬 복원 → 헤더 구성. ViewModel 할당(정렬) 전에 해야
                // 저장된 정렬 기준으로 한 번만 정렬된다.
                LoadColumnLayout();
                RebuildHeaderColumns();

                if (!IsManualViewModel)
                {
                    // Get ViewModel from MainWindow's DataContext (XAML 정의 인스턴스용)
                    if (this.XamlRoot?.Content is FrameworkElement root &&
                        root.DataContext is MainViewModel mainVm)
                    {
                        ViewModel = IsRightPane ? mainVm.RightExplorer : mainVm.Explorer;
                    }
                }
                else if (_viewModel != null)
                {
                    // Per-tab panel: ViewModel was set before Loaded (_isLoaded was false),
                    // so ApplyCurrentView() was skipped in the setter. Trigger it now.
                    ApplyCurrentView();
                }

                // Ctrl+Wheel view mode cycling is handled globally by MainWindow.OnGlobalPointerWheelChanged

                // 초기 동기화: HeaderGrid의 첫 SizeChanged에서 실행.
                // Loaded/Low dispatch 시점에는 XamlRoot.RasterizationScale이 아직
                // 미설정일 수 있어 SnapToPixel 결과가 달라짐.
                // SizeChanged는 layout + DPI 모두 확정된 후 발생.
                void OnHeaderFirstSize(object s, SizeChangedEventArgs ev)
                {
                    HeaderGrid.SizeChanged -= OnHeaderFirstSize;
                    if (_isCleanedUp) return;
                    OnColumnWidthChanged(this, ColumnDefinition.WidthProperty);
                }
                HeaderGrid.SizeChanged += OnHeaderFirstSize;
            };

            this.Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_loc != null) _loc.LanguageChanged -= LocalizeUI;

                // Save sort settings (always, even if already cleaned up)
                SaveColumnLayout();

                // Skip if Cleanup() was already called from MainWindow.OnClosed
                if (_isCleanedUp) return;

                PerformCleanup();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView.OnUnloaded] Error: {ex.Message}");
            }
        }

        #region Localization

        private void LocalizeUI()
        {
            // 헤더 라벨(정렬 화살표 포함)은 UpdateSortIndicators가 컬럼 목록 기준으로 채운다.
            // 필터 버튼 제거됨 — 검색 기능으로 대체
            UpdateSortIndicators();
        }

        private string ColumnLabel(DetailsColumnInfo column) => _loc?.Get(column.LocKey) ?? column.Id;

        #endregion

        #region Column Width Synchronization

        /// <summary>
        /// Fired when any of the 3 resizable ColumnDefinitions change Width
        /// (via GridSplitter drag or window resize).
        ///
        /// WinUI 3 bugs that forced this approach:
        /// 1. ColumnDefinition.Width binding in DataTemplate doesn't respond
        ///    to INotifyPropertyChanged (Microsoft Issue #10300).
        /// 2. Grid.SizeChanged does NOT fire when GridSplitter rearranges
        ///    internal columns (total Grid size stays the same).
        ///
        /// Solution: RegisterPropertyChangedCallback on each ColumnDefinition,
        /// then set Border.Width directly on each cell element.
        /// </summary>
        private void OnColumnWidthChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (_isRebuildingHeader) return;

            // 각 셀의 총 너비(splitter 포함) 재계산
            RecalcCellTotalWidths();

            // 디바운스: GridSplitter 드래그 시 인접 컬럼 콜백이 순차 발생하므로
            // 모든 컬럼 변경이 완료된 후 한 번만 데이터 행을 업데이트
            if (!_columnWidthUpdatePending)
            {
                _columnWidthUpdatePending = true;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    _columnWidthUpdatePending = false;
                    if (_isCleanedUp) return;
                    UpdateAllVisibleContainerWidths();
                });
            }

            ScheduleColumnLayoutSave();
        }

        /// <summary>
        /// Called when ListView containers are created or recycled.
        /// Sets cell widths to match current header column widths.
        /// This handles virtualization: newly realized containers get correct widths.
        /// </summary>
        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // 긴급 임시 가드: STATUS_STOWED_EXCEPTION 차단 (별도 작업으로 근본 원인 추적)
            try
            {
                if (args.InRecycleQueue)
                {
                    // 재활용 큐: 화면 밖 아이템의 썸네일 해제 (메모리 절약)
                    if (args.Item is ViewModels.FileViewModel recycledFile)
                    {
                        try { recycledFile.UnloadThumbnail(); }
                        catch (Exception ex) { Helpers.DebugLogger.Log($"[Details.CCC] UnloadThumbnail failed: {ex.Message}"); }
                    }
                    return;
                }

                if (args.ItemContainer?.ContentTemplateRoot is Grid grid)
                {
                    ApplyCellWidths(grid);
                    grid.Height = _densityRowHeight;
                }

                if (args.Item is ViewModels.FolderViewModel folderVm)
                {
                    folderVm.RequestFolderSizeCalculation();
                }

                if (args.Item is ViewModels.FileViewModel fileVm && fileVm.IsThumbnailSupported && !fileVm.HasThumbnail)
                {
                    _ = fileVm.LoadThumbnailAsync();
                }

                if (args.Item is ViewModels.FileViewModel shellVm && !shellVm.HasShellProperties && AnyShellColumnVisible)
                {
                    _ = LoadShellPropertiesAsync(shellVm);
                }

                if (args.Item is ViewModels.FileSystemViewModel fsVm)
                {
                    var currentFolder = _viewModel?.CurrentFolder;
                    try
                    {
                        if (IsColumnVisible(DetailsColumns.Git))
                            currentFolder?.InjectGitStateIfNeeded(fsVm);
                        currentFolder?.InjectCloudStateIfNeeded(fsVm);
                        currentFolder?.InjectColorTagIfNeeded(fsVm);
                        currentFolder?.InjectTagIfNeeded(fsVm);
                    }
                    catch (Exception ex) { Helpers.DebugLogger.Log($"[Details.CCC] Inject failed: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[Details.CCC] Outer guard caught: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 보이는 컬럼마다 셀의 총 너비(앞 구분선 + 데이터 열)를 계산해 캐시.
        /// 데이터 열은 GridSplitter가 설정한 픽셀 Width를 그대로 쓴다 — 콜백 시점의 ActualWidth는
        /// 아직 이전 레이아웃 값이라 드래그 중 한 박자 늦게 따라온다.
        /// 이 값은 OnColumnWidthChanged에서 캐시됨 (measure pass 중 호출 불가이므로).
        /// </summary>
        private void RecalcCellTotalWidths()
        {
            try
            {
                foreach (var slot in _slots)
                {
                    if (!slot.Visible || slot.DataColumn == null || slot.SplitterColumn == null)
                    {
                        slot.CellTotalWidth = 0;
                        continue;
                    }

                    var width = slot.DataColumn.Width;
                    double dataWidth = width.IsAbsolute ? width.Value : slot.DataColumn.ActualWidth;
                    if (dataWidth <= 0) dataWidth = slot.Width;
                    dataWidth = Math.Max(dataWidth, slot.Info.MinWidth);
                    slot.Width = dataWidth;

                    double splitterActual = slot.SplitterColumn.ActualWidth;
                    if (splitterActual > 0) _splitterWidth = splitterActual;
                    slot.CellTotalWidth = _splitterWidth + dataWidth;
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[Details] RecalcCellTotalWidths error: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply current column widths to a single item Grid's cell Borders.
        /// Border.Width만 설정 (Margin 없음) → ContainerContentChanging(measure pass) 중에도 안전.
        /// 각 셀의 Width는 splitter 간격을 포함한 총 너비.
        /// 셀 내부 TextBlock Padding으로 텍스트 시작 위치를 조정.
        /// </summary>
        private void ApplyCellWidths(Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is not Border border) continue;
                int index = Grid.GetColumn(border) - DetailsColumns.FirstCellGridColumn;
                if ((uint)index >= (uint)_slots.Length) continue;

                var slot = _slots[index];
                border.Width = slot.Visible ? slot.CellTotalWidth : 0;
                border.Visibility = slot.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Update all currently visible containers' cell widths.
        /// Only iterates realized containers (typically 20-50 items), not all items.
        /// </summary>
        private void UpdateAllVisibleContainerWidths()
        {
            var panel = DetailsListView?.ItemsPanelRoot;
            if (panel == null) return;

            // ItemsPanelRoot의 자식만 순회 (실제 표시된 30-50개) — Items.Count(14K) 전수 순회 방지
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            {
                if (VisualTreeHelper.GetChild(panel, i) is ListViewItem container &&
                    container.ContentTemplateRoot is Grid grid)
                {
                    ApplyCellWidths(grid);
                }
            }
        }

        #endregion

        #region Density

        private double _densityRowHeight = 24.0; // comfortable default

        public void ApplyDensity(string density)
        {
            // 숫자 문자열(0~6) 또는 레거시 이름 지원
            int level = density switch
            {
                "compact" => 0,
                "comfortable" => 2,
                "spacious" => 4,
                _ => int.TryParse(density, out var n) ? Math.Clamp(n, 0, 6) : 2
            };
            _densityRowHeight = 20.0 + level;

            // Update header row height to match
            if (HeaderGrid != null)
                HeaderGrid.Height = _densityRowHeight;

            // Update MinHeight on ListViewItem style
            if (DetailsListView != null)
            {
                DetailsListView.ItemContainerStyle = CreateDetailsItemStyle(_densityRowHeight);
            }

            // Update existing realized containers (ItemsPanelRoot 자식만 — 14K 전수 순회 방지)
            var panel = DetailsListView?.ItemsPanelRoot;
            if (panel == null) return;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            {
                if (VisualTreeHelper.GetChild(panel, i) is ListViewItem container &&
                    container.ContentTemplateRoot is Grid grid)
                {
                    grid.Height = _densityRowHeight;
                }
            }
        }

        private Style CreateDetailsItemStyle(double minHeight)
        {
            var baseStyle = (Style)App.Current.Resources["DetailsItemStyle"];
            var style = new Style(typeof(ListViewItem)) { BasedOn = baseStyle };
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, minHeight));
            return style;
        }

        // ApplyIconFontScale/ApplyFontScaleToGrid 는 FontScaleService + XAML {Binding} 으로 대체됨 (Phase B/C).

        #endregion

        #region Selection Sync

        private void OnDetailsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingSelection) return;
            if (ViewModel?.CurrentFolder == null) return;
            if (sender is ListView listView)
            {
                var folder = ViewModel.CurrentFolder;
                var host = ContextMenuHost as MainWindow;
                if (host != null && host.TryPreserveFolderMultiSelectOnRightClick(
                        listView.SelectedItems, folder,
                        () =>
                        {
                            _isSyncingSelection = true;
                            try
                            {
                                listView.SelectedItems.Clear();
                                foreach (var item in folder.SelectedItems)
                                    listView.SelectedItems.Add(item);
                            }
                            finally { _isSyncingSelection = false; }
                        }))
                    return;

                folder.SyncSelectedItems(listView.SelectedItems);
                host?.ViewModel?.UpdateStatusBar();
            }
        }

        private void ApplyCheckboxMode(bool showCheckboxes)
        {
            if (DetailsListView == null) return;
            DetailsListView.SelectionMode = showCheckboxes
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Extended;
        }

        private void OnSettingChanged(string key, object? value)
        {
            if (key == "ShowCheckboxes" && value is bool show)
            {
                Span.Helpers.DispatcherHelper.SafeEnqueue(DispatcherQueue, () => ApplyCheckboxMode(show));
            }
            else if (key == "ShowGitIntegration" && value is bool gitEnabled)
            {
                Span.Helpers.DispatcherHelper.SafeEnqueue(DispatcherQueue, () =>
                {
                    SetColumnVisible(DetailsColumns.Git, gitEnabled, persist: false);
                    if (gitEnabled)
                        TriggerGitStateLoad();
                });
            }
            else if (key == SettingsService.DetailsColumnsJsonKey && value is string json && !_isSavingLayout)
            {
                // 다른 탭/분할 창의 Details 뷰가 컬럼을 바꿈 → 같은 표시/너비로 맞춘다 (정렬은 창마다 유지)
                Span.Helpers.DispatcherHelper.SafeEnqueue(DispatcherQueue, () => ApplyExternalColumnLayout(json));
            }
        }

        #endregion

        #region Item Interaction

        private void OnDragItemsStarting(object sender, Microsoft.UI.Xaml.Controls.DragItemsStartingEventArgs e)
        {
            if (_rubberBandHelper?.IsActive == true)
            { e.Cancel = true; return; }

            if (!Helpers.ViewDragDropHelper.SetupDragData(e, IsRightPane, sender as Microsoft.UI.Xaml.Controls.ListViewBase))
            { e.Cancel = true; return; }

            (ContextMenuHost as MainWindow)?.NotifyViewDragStarted(e);
        }

        private void OnDragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
        {
            (ContextMenuHost as MainWindow)?.NotifyViewDragCompleted();
        }

        /// <summary>Right-button drag (Explorer): fill the package like a left-button drag.</summary>
        private bool OnRightDragStarting(IReadOnlyList<ViewModels.FileSystemViewModel> items, Windows.ApplicationModel.DataTransfer.DataPackage data)
        {
            if (_rubberBandHelper?.IsActive == true) return false;
            Helpers.ViewDragDropHelper.PopulateDragData(data, items, IsRightPane);
            (ContextMenuHost as MainWindow)?.NotifyViewDragStarted(items);
            return true;
        }

        private void OnRightDragCompleted()
        {
            (ContextMenuHost as MainWindow)?.NotifyViewDragCompleted();
        }

        private void OnListViewDragOver(object sender, DragEventArgs e)
        {
            var mainWindow = ContextMenuHost as MainWindow;
            if (mainWindow == null || sender is not Microsoft.UI.Xaml.Controls.ListViewBase listView) return;

            var pos = e.GetPosition(listView);
            var targetFolder = Helpers.ViewDragDropHelper.FindFolderAtPoint(
                listView, pos, ViewModel?.CurrentFolder);

            // 이전 하이라이트 해제
            if (_lastHighlightedGrid != null)
            {
                _lastHighlightedGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0, 0, 0, 0));
                _lastHighlightedGrid = null;
            }

            if (targetFolder != null)
            {
                var grid = Helpers.ViewDragDropHelper.FindItemGrid(listView, targetFolder);
                mainWindow.HandleViewFolderItemDragOver(e, targetFolder, IsRightPane,
                    grid ?? new Grid());
                if (grid != null) _lastHighlightedGrid = grid;
            }
            else
            {
                var folder = ViewModel?.CurrentFolder;
                if (folder?.Path != null)
                    mainWindow.HandleViewDragOver(e, folder.Path, folder.Name, IsRightPane,
                        sender as UIElement ?? (UIElement)DetailsListView);
            }
        }

        private async void OnListViewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true; // CRITICAL: await 전에 설정하여 OnPaneDrop 중복 실행 방지
            var mainWindow = ContextMenuHost as MainWindow;
            if (mainWindow == null) return;
            mainWindow.HandleViewDragLeave();

            // 하이라이트 해제
            if (_lastHighlightedGrid != null)
            {
                _lastHighlightedGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0, 0, 0, 0));
                _lastHighlightedGrid = null;
            }

            // 폴더 대상 식별
            string? destPath = null;
            if (sender is Microsoft.UI.Xaml.Controls.ListViewBase listView)
            {
                var pos = e.GetPosition(listView);
                var targetFolder = Helpers.ViewDragDropHelper.FindFolderAtPoint(
                    listView, pos, ViewModel?.CurrentFolder);
                if (targetFolder != null)
                    destPath = targetFolder.Path;
            }

            destPath ??= ViewModel?.CurrentFolder?.Path;
            if (string.IsNullOrEmpty(destPath)) return;

            await mainWindow.CompleteDropAsync(e, destPath);
        }

        private void OnListViewDragLeave(object sender, DragEventArgs e)
        {
            if (_lastHighlightedGrid != null)
            {
                _lastHighlightedGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0, 0, 0, 0));
                _lastHighlightedGrid = null;
            }
            (ContextMenuHost as MainWindow)?.HandleViewDragLeave();
        }

        private async void OnItemRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            try
            {
                if (_settings != null && !_settings.ShowContextMenu) return;
                if (sender is Grid grid && ContextMenuService != null && ContextMenuHost != null)
                {
                    e.Handled = true; // Prevent bubbling to empty area handler during await

                    // Shift+우클릭 → 셸 확장 즉시 표시 (Windows 탐색기 동작과 동일)
                    bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                    if (grid.DataContext is FileSystemViewModel target)
                    {
                        await ContextMenuService.ShowFileSystemContextMenuAsync(
                            target, ContextMenuHost, grid, e.GetPosition(grid), forceWinUiFlyout: shiftHeld);
                    }
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] OnItemRightTapped error: {ex.Message}");
            }
        }

        private void OnItemDoubleClick(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            Helpers.ViewItemHelper.OpenFileOrFolder(ViewModel, "DetailsModeView");
        }

        #endregion

        #region Keyboard Navigation

        private void OnDetailsKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var selected = ViewModel?.CurrentFolder?.SelectedChild;
            if (selected != null && selected.IsRenaming) return;
            if (Helpers.ViewItemHelper.HasModifierKey()) return;

            // Shift+WASD 네비게이션
            if (_settings?.EnableWasdNavigation == true &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) &&
                e.Key is Windows.System.VirtualKey.W or Windows.System.VirtualKey.A or
                         Windows.System.VirtualKey.S or Windows.System.VirtualKey.D)
            {
                if ((ContextMenuHost as MainWindow)?.HandleViewWasd(e.Key, ViewModel) == true)
                    e.Handled = true;
                return;
            }

            // 이름 변경 직후의 Enter가 파일 실행으로 이어지는 것을 방지
            if (_justFinishedRename)
            {
                _justFinishedRename = false;
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Windows.System.VirtualKey.Enter:
                    Helpers.ViewItemHelper.OpenFileOrFolder(ViewModel, "DetailsModeView");
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Back:
                    ViewModel?.NavigateUp();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Space:
                    if (_settings?.EnableQuickLook == true)
                    {
                        (ContextMenuHost as MainWindow)?.HandleViewQuickLook(ViewModel?.CurrentFolder?.SelectedChild);
                        e.Handled = true;
                    }
                    else
                    {
                        var spCh = MainWindow.KeyToChar(e.Key);
                        if (spCh != '\0')
                        {
                            (ContextMenuHost as MainWindow)?.HandleViewTypeAhead(spCh, ViewModel, DetailsListView);
                            e.Handled = true;
                        }
                    }
                    break;
                case Windows.System.VirtualKey.Home:
                case Windows.System.VirtualKey.End:
                    // ListView가 Home/End를 네이티브로 처리하므로 추가 처리 불필요
                    break;
                default:
                    var ch = MainWindow.KeyToChar(e.Key);
                    if (ch != '\0')
                    {
                        (ContextMenuHost as MainWindow)?.HandleViewTypeAhead(ch, ViewModel, DetailsListView);
                        e.Handled = true;
                    }
                    break;
            }
        }

        #endregion

        #region Selection Operations

        internal void SelectAll()
        {
            DetailsListView?.SelectAll();
        }

        internal void SelectNone()
        {
            if (DetailsListView == null) return;
            _isSyncingSelection = true;
            try
            {
                DetailsListView.SelectedItems.Clear();
                if (ViewModel?.CurrentFolder != null)
                {
                    ViewModel.CurrentFolder.SelectedChild = null;
                    ViewModel.CurrentFolder.SelectedItems.Clear();
                }
            }
            finally { _isSyncingSelection = false; }
        }

        internal List<FileSystemViewModel> GetSelectedFileSystemItems()
        {
            if (DetailsListView == null) return new List<FileSystemViewModel>();
            return DetailsListView.SelectedItems.OfType<FileSystemViewModel>().ToList();
        }

        internal void InvertSelection()
        {
            if (DetailsListView == null || ViewModel?.CurrentFolder == null) return;
            var allItems = ViewModel.CurrentFolder.Children.ToList();
            var selectedIndices = new HashSet<int>();
            foreach (var item in DetailsListView.SelectedItems)
            {
                int idx = allItems.IndexOf(item as FileSystemViewModel);
                if (idx >= 0) selectedIndices.Add(idx);
            }
            _isSyncingSelection = true;
            try
            {
                DetailsListView.SelectedItems.Clear();
                for (int i = 0; i < allItems.Count; i++)
                {
                    if (!selectedIndices.Contains(i))
                        DetailsListView.SelectedItems.Add(allItems[i]);
                }
            }
            finally { _isSyncingSelection = false; }
        }

        #endregion

        #region F2 Inline Rename

        private int _renameSelectionCycle;
        private string? _renameTargetPath;
        private bool _justFinishedRename;

        /// <summary>
        /// Details 뷰에서 F2 인라인 rename 시작.
        /// MainWindow.HandleRename()이 Miller 전용이므로 Details 전용 구현.
        /// </summary>
        internal void HandleRename()
        {
            var selected = ViewModel?.CurrentFolder?.SelectedChild;
            if (selected == null) return;

            var itemPath = selected.Path;

            // F2 cycling: 이미 rename 중이면 선택 영역 순환
            if (selected.IsRenaming && itemPath == _renameTargetPath)
            {
                _renameSelectionCycle = (_renameSelectionCycle + 1) % 3;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    FocusDetailsRenameTextBox(selected);
                });
                return;
            }

            // 첫 F2: rename 시작
            _renameSelectionCycle = 0;
            _renameTargetPath = itemPath;
            selected.BeginRename();

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                FocusDetailsRenameTextBox(selected);
            });
        }

        private void FocusDetailsRenameTextBox(FileSystemViewModel item)
        {
            var container = DetailsListView.ContainerFromItem(item) as UIElement;
            if (container == null)
            {
                DetailsListView.ScrollIntoView(item);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    var retryContainer = DetailsListView.ContainerFromItem(item) as UIElement;
                    if (retryContainer != null)
                    {
                        var tb = VisualTreeHelpers.FindChild<TextBox>(retryContainer as DependencyObject);
                        if (tb != null) ApplyRenameSelection(tb, item is FolderViewModel);
                    }
                });
                return;
            }

            var textBox = VisualTreeHelpers.FindChild<TextBox>(container as DependencyObject);
            if (textBox != null)
            {
                ApplyRenameSelection(textBox, item is FolderViewModel);
            }
        }

        private void ApplyRenameSelection(TextBox textBox, bool isFolder)
        {
            textBox.Focus(FocusState.Keyboard);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (!isFolder && !string.IsNullOrEmpty(textBox.Text))
                {
                    int dotIndex = textBox.Text.LastIndexOf('.');
                    if (dotIndex > 0)
                    {
                        switch (_renameSelectionCycle)
                        {
                            case 0: textBox.Select(0, dotIndex); break;
                            case 1: textBox.SelectAll(); break;
                            case 2: textBox.Select(dotIndex + 1, textBox.Text.Length - dotIndex - 1); break;
                        }
                    }
                    else textBox.SelectAll();
                }
                else textBox.SelectAll();
            });
        }

        private void OnRenameTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            var vm = textBox.DataContext as FileSystemViewModel;
            if (vm == null) return;

            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                vm.CommitRename();
                _justFinishedRename = true;
                _renameTargetPath = null;
                e.Handled = true;
                FocusSelectedListViewItem();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                vm.CancelRename();
                _justFinishedRename = true;
                _renameTargetPath = null;
                e.Handled = true;
                FocusSelectedListViewItem();
            }
            else if (e.Key == Windows.System.VirtualKey.F2)
            {
                HandleRename();
                e.Handled = true;
            }
        }

        private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            var vm = textBox.DataContext as FileSystemViewModel;
            if (vm == null || !vm.IsRenaming) return;

            if ((ContextMenuHost as MainWindow)?.IsRenameFocusPending == true)
                return;

            vm.CommitRename();
            _renameTargetPath = null;
        }

        private void FocusSelectedListViewItem()
        {
            var selected = ViewModel?.CurrentFolder?.SelectedChild;
            if (selected == null) return;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (DetailsListView.ContainerFromItem(selected) is ListViewItem container)
                {
                    container.Focus(FocusState.Programmatic);
                }
            });
        }

        #endregion

        #region Sorting

        private void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string sortBy)
            {
                if (_currentSortBy == sortBy)
                {
                    _isAscending = !_isAscending;
                }
                else
                {
                    _currentSortBy = sortBy;
                    _isAscending = true;
                }

                SortItems(sortBy, _isAscending);
                UpdateSortIndicators();
                Helpers.DebugLogger.Log($"[DetailsModeView] Sorted by {sortBy} ({(_isAscending ? "Asc" : "Desc")})");
            }
        }

        private void UpdateSortIndicators()
        {
            string Label(string id, string text)
                => _currentSortBy == id ? $"{text} {(_isAscending ? "▲" : "▼")}" : text;

            NameHeaderButton.Content = Label("Name", _loc?.Get("Name") ?? "Name");
            foreach (var slot in _slots)
            {
                if (slot.HeaderButton != null)
                    slot.HeaderButton.Content = Label(slot.Info.Id, ColumnLabel(slot.Info));
            }
        }

        /// <summary>폴더 먼저/파일 나중(혼합 정렬 폴더 제외) + 키 기준 정렬.</summary>
        private static IEnumerable<FileSystemViewModel> OrderItems<TKey>(
            IEnumerable<FileSystemViewModel> items, Func<FileSystemViewModel, TKey> key,
            bool ascending, bool mixFoldersFiles, IComparer<TKey>? comparer = null)
        {
            if (mixFoldersFiles)
                return ascending ? items.OrderBy(key, comparer) : items.OrderByDescending(key, comparer);

            var foldersFirst = items.OrderBy(x => x is FileViewModel ? 1 : 0);
            return ascending ? foldersFirst.ThenBy(key, comparer) : foldersFirst.ThenByDescending(key, comparer);
        }

        private void SortItems(string sortBy, bool ascending)
        {
            if (ViewModel?.CurrentFolder == null)
                return;
            if (ViewModel.CurrentFolder.Children.Count == 0)
            {
                // Leerer oder noch ladender Ordner: eine Gruppen-/Filteransicht des vorigen Ordners nicht stehen lassen
                _unfilteredItems = null;
                ApplyFiltersAndGrouping();
                return;
            }

            var column = ViewModel.CurrentFolder;
            var savedSelection = column.SelectedChild;
            column.IsSorting = true;

            try
            {
                var children = column.Children;
                bool mix = column.IsDownloadsFolder;

                IEnumerable<FileSystemViewModel>? sorted = sortBy switch
                {
                    "Name" => OrderItems(children, x => x.Name, ascending, mix, Helpers.NaturalStringComparer.Instance),
                    DetailsColumns.DateModified => OrderItems(children, x => x.DateModifiedValue, ascending, mix),
                    DetailsColumns.Type => OrderItems(children, x => x.FileType, ascending, mix),
                    DetailsColumns.Size => OrderItems(children, x => x.SizeValue, ascending, mix),
                    DetailsColumns.Git => OrderItems(children, x => x.GitStatusText, ascending, mixFoldersFiles: false),
                    DetailsColumns.Location => OrderItems(children, x => x.LocationPath, ascending, mix, StringComparer.CurrentCultureIgnoreCase),
                    DetailsColumns.DateCreated => OrderItems(children, x => x.DateCreatedValue, ascending, mix),
                    DetailsColumns.Attributes => OrderItems(children, x => x.AttributesText, ascending, mix, StringComparer.Ordinal),
                    DetailsColumns.Dimensions => OrderItems(children, x => x.ShellProperties.PixelCount, ascending, mix),
                    DetailsColumns.Duration => OrderItems(children, x => x.ShellProperties.DurationTicks, ascending, mix),
                    DetailsColumns.Authors => OrderItems(children, x => x.AuthorsText, ascending, mix, StringComparer.CurrentCultureIgnoreCase),
                    DetailsColumns.Title => OrderItems(children, x => x.TitleText, ascending, mix, StringComparer.CurrentCultureIgnoreCase),
                    _ => null
                };
                if (sorted == null) return;

                // 셸 속성 컬럼: 화면에 안 나온 항목은 아직 값이 없다 → 전부 읽은 뒤 다시 정렬 (탐색기와 같이)
                if (DetailsColumns.IsShellColumn(sortBy))
                    LoadShellPropertiesForSort(sortBy);

                var sortedList = sorted.ToList();

                // 원자적 교체: Clear+Add 대신 새 컬렉션 할당으로 28K CollectionChanged 이벤트 → 1회 Reset
                column.ReplaceChildren(sortedList);

                if (savedSelection != null)
                {
                    column.SelectedChild = savedSelection;
                }

                // Cache unfiltered items after sorting
                _unfilteredItems = new System.Collections.Generic.List<FileSystemViewModel>(sortedList);

                // Re-apply filters and grouping
                ApplyFiltersAndGrouping();

                Helpers.DebugLogger.Log($"[DetailsModeView] Sorted {sortedList.Count} items");
            }
            finally
            {
                column.IsSorting = false;
            }
        }

        #endregion

        #region Column Layout Persistence

        /// <summary>
        /// 컬럼 표시/너비 + 정렬/그룹화를 설정(DetailsColumnsJson)에 저장.
        /// SettingsService를 거치므로 패키지 없는 빌드(dev/Personal)에서도 유지된다.
        /// 다운로드 폴더의 자동 날짜 정렬은 저장하지 않고 원래 정렬을 저장한다.
        /// </summary>
        private void SaveColumnLayout()
        {
            _layoutSaveTimer?.Stop();
            if (_settings == null) return;
            try
            {
                var layout = new DetailsColumnLayout
                {
                    SortBy = _isAutoSortedForDownloads ? (_savedSortBeforeDownloads ?? "Name") : _currentSortBy,
                    SortAscending = _isAutoSortedForDownloads ? _savedAscBeforeDownloads : _isAscending,
                    GroupBy = _currentGroupBy
                };
                foreach (var slot in _slots)
                {
                    if (IsPersistedVisibility(slot.Info))
                        layout.Visible[slot.Info.Id] = slot.Visible;
                    layout.Widths[slot.Info.Id] = Math.Round(slot.Width, 1);
                }

                var json = layout.ToJson();
                if (json == _lastLayoutJson) return;
                _lastLayoutJson = json;

                _isSavingLayout = true;
                try { _settings.DetailsColumnsJson = json; }
                finally { _isSavingLayout = false; }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Error saving column layout: {ex.Message}");
            }
        }

        /// <summary>Location(검색)과 Git(저장소)은 상황에 따라 자동 표시되므로 저장하지 않는다.</summary>
        private static bool IsPersistedVisibility(DetailsColumnInfo column)
            => column.Id != DetailsColumns.Location && column.Id != DetailsColumns.Git;

        private void ScheduleColumnLayoutSave()
        {
            if (_settings == null || _isCleanedUp) return;
            if (_layoutSaveTimer == null)
            {
                _layoutSaveTimer = DispatcherQueue.CreateTimer();
                _layoutSaveTimer.Interval = TimeSpan.FromMilliseconds(800);
                _layoutSaveTimer.IsRepeating = false;
                _layoutSaveTimer.Tick += (_, _) => SaveColumnLayout();
            }
            _layoutSaveTimer.Stop();
            _layoutSaveTimer.Start();
        }

        private void LoadColumnLayout()
        {
            try
            {
                var json = _settings?.DetailsColumnsJson;
                var layout = string.IsNullOrEmpty(json)
                    ? ReadLegacyColumnLayout()
                    : DetailsColumnLayout.FromJson(json);
                _lastLayoutJson = json;

                ApplyColumnLayout(layout);
                _currentSortBy = layout.SortBy;
                _isAscending = layout.SortAscending;
                _currentGroupBy = layout.GroupBy;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Error loading column layout: {ex.Message}");
            }
        }

        /// <summary>표시 여부와 너비만 슬롯에 반영 (헤더 재구성은 호출자 몫).</summary>
        private void ApplyColumnLayout(DetailsColumnLayout layout)
        {
            foreach (var slot in _slots)
            {
                slot.Width = layout.GetWidth(slot.Info);
                if (IsPersistedVisibility(slot.Info))
                    slot.Visible = layout.IsVisible(slot.Info);
            }
        }

        private void ApplyExternalColumnLayout(string json)
        {
            if (_isCleanedUp || !_isLoaded || json == _lastLayoutJson) return;
            _lastLayoutJson = json;

            bool shellWasVisible = AnyShellColumnVisible;
            ApplyColumnLayout(DetailsColumnLayout.FromJson(json));
            RebuildHeaderColumns();
            if (!shellWasVisible && AnyShellColumnVisible)
                RequestShellPropertiesForRealizedItems();
        }

        /// <summary>
        /// 이전 버전은 ApplicationData.LocalSettings("DetailsViewSort")에 저장했다 (패키지 빌드에서만 동작).
        /// 새 설정이 없을 때 한 번 옮겨 온다.
        /// </summary>
        private DetailsColumnLayout ReadLegacyColumnLayout()
        {
            var layout = new DetailsColumnLayout();
            try
            {
                if (_localSettings?.Values["DetailsViewSort"] is not ApplicationDataCompositeValue composite)
                    return layout;

                if (composite.TryGetValue("SortColumn", out var sortObj) && sortObj is string sortColumn)
                    layout.SortBy = sortColumn;
                if (composite.TryGetValue("SortAscending", out var ascObj) && ascObj is bool ascending)
                    layout.SortAscending = ascending;
                if (composite.TryGetValue("GroupBy", out var groupObj) && groupObj is string groupBy)
                    layout.GroupBy = groupBy;
                if (composite.TryGetValue("DateColumnVisible", out var d) && d is bool dateVis)
                    layout.Visible[DetailsColumns.DateModified] = dateVis;
                if (composite.TryGetValue("TypeColumnVisible", out var t) && t is bool typeVis)
                    layout.Visible[DetailsColumns.Type] = typeVis;
                if (composite.TryGetValue("SizeColumnVisible", out var sz) && sz is bool sizeVis)
                    layout.Visible[DetailsColumns.Size] = sizeVis;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Error reading legacy sort settings: {ex.Message}");
            }
            return layout;
        }

        #endregion

        // Ctrl+Mouse Wheel view mode cycling is handled globally by MainWindow.OnGlobalPointerWheelChanged

        #region Focus Management

        /// <summary>
        /// Focus the Details ListView (called from MainWindow on view switch)
        /// </summary>
        public void FocusDataGrid()
        {
            DetailsListView?.Focus(FocusState.Programmatic);
        }

        public void FocusListView() => FocusDataGrid();

        /// <summary>
        /// Empty space click → focus ListView → triggers GotFocus bubbling → ActivePane set
        /// </summary>
        private void OnRootTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            DetailsListView?.Focus(FocusState.Programmatic);
        }

        private async void OnEmptyAreaRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (e.Handled) return; // Item handler already handled
            if (_settings != null && !_settings.ShowContextMenu) return;
            if (ContextMenuService == null || ContextMenuHost == null) return;

            var folderPath = ViewModel?.CurrentFolder?.Path;
            if (string.IsNullOrEmpty(folderPath)) return;

            bool shiftHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            var flyout = await ContextMenuService.BuildEmptyAreaMenuAsync(folderPath, ContextMenuHost, forceShellExtensions: shiftHeld);
            flyout.ShowAt(RootGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(RootGrid)
            });
        }

        #endregion

        #region Feature #29: Group By

        private void OnHeaderRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            e.Handled = true; // Prevent empty area context menu

            var flyout = new MenuFlyout();
            string L(string key) => _loc?.Get(key) ?? key;

            // ── Column Visibility (Feature #30) — 탐색기처럼 메뉴 최상위에 컬럼 목록 ──
            flyout.Items.Add(new ToggleMenuFlyoutItem { Text = L("Name"), IsChecked = true, IsEnabled = false });
            foreach (var slot in _slots)
            {
                if (!slot.Info.UserToggleable) continue;
                var id = slot.Info.Id;
                var toggle = new ToggleMenuFlyoutItem { Text = ColumnLabel(slot.Info), IsChecked = slot.Visible };
                toggle.Click += (s, _) => SetColumnVisible(id, !IsColumnVisible(id), persist: true);
                flyout.Items.Add(toggle);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Group By (Feature #29) ──
            var groupBy = new MenuFlyoutSubItem { Text = L("GroupBy") };
            foreach (var (key, label) in new[]
            {
                ("None", L("None")), ("Name", L("Name")), ("Type", L("Type")),
                ("DateModified", L("DateModified")), ("DateCreated", L("Column_DateCreated")),
                ("Size", L("Size")), ("Tag", L("ColorTag"))
            })
            {
                var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = _currentGroupBy == key };
                item.Click += (s, _) => SetGroupBy(key);
                groupBy.Items.Add(item);
            }
            flyout.Items.Add(groupBy);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var reset = new MenuFlyoutItem { Text = L("Details_ResetColumns") };
            reset.Click += (s, _) => ResetColumns();
            flyout.Items.Add(reset);

            flyout.ShowAt(HeaderGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(HeaderGrid)
            });
        }

        /// <summary>
        /// Public entry point for external group by trigger.
        /// </summary>
        public void SetGroupByPublic(string groupBy) => SetGroupBy(groupBy);

        private void SetGroupBy(string groupBy)
        {
            if (_currentGroupBy != groupBy) _collapsedGroups.Clear();
            _currentGroupBy = groupBy;
            ApplyFiltersAndGrouping();
            SaveColumnLayout();
            Helpers.DebugLogger.Log($"[DetailsModeView] Group By set to: {groupBy}");
        }

        // ── Einklappbare Gruppen (Explorer) ──
        private readonly HashSet<string> _collapsedGroups = new(StringComparer.CurrentCultureIgnoreCase);
        private List<Helpers.ItemGroup>? _currentGroups;

        private void OnGroupChevronTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (Helpers.GroupHeaderHelper.GroupOf(sender) is { } group)
                Helpers.GroupHeaderHelper.Toggle(group, _collapsedGroups);
        }

        private void OnGroupChevronDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
            => e.Handled = true; // zwei Einzelklicks auf den Pfeil reichen; nicht zusätzlich über den Kopf umschalten

        private void OnGroupHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (Helpers.GroupHeaderHelper.GroupOf(sender) is { } group)
                Helpers.GroupHeaderHelper.SelectGroup(DetailsListView, group);
        }

        private void OnGroupHeaderDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (Helpers.GroupHeaderHelper.GroupOf(sender) is { } group)
                Helpers.GroupHeaderHelper.Toggle(group, _collapsedGroups);
        }

        private void OnGroupHeaderRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement header)
                Helpers.GroupHeaderHelper.ShowMenu(header, e, _currentGroups, _collapsedGroups);
        }

        #endregion

        #region Feature #30: Column Visibility

        /// <summary>
        /// 검색 결과 모드에서 Location 컬럼을 표시/숨김.
        /// MainWindow에서 재귀 검색 시작/취소 시 호출.
        /// </summary>
        public void ShowLocationColumn(bool show)
        {
            SetColumnVisible(DetailsColumns.Location, show, persist: false);
        }

        /// <summary>
        /// 컬럼 표시를 바꾸고 헤더를 다시 구성한다. persist=false는 자동 표시(Location/Git)용.
        /// </summary>
        private void SetColumnVisible(string id, bool visible, bool persist)
        {
            var slot = Slot(id);
            if (slot.Visible == visible) return;

            bool shellWasVisible = AnyShellColumnVisible;
            slot.Visible = visible;
            RebuildHeaderColumns();

            if (!shellWasVisible && AnyShellColumnVisible)
                RequestShellPropertiesForRealizedItems();
            if (persist)
                SaveColumnLayout();

            Helpers.DebugLogger.Log($"[DetailsModeView] Column '{id}' visibility: {visible}");
        }

        /// <summary>탐색기 기본값(이름·수정한 날짜·유형·크기, 기본 너비)으로 되돌린다.</summary>
        private void ResetColumns()
        {
            foreach (var slot in _slots)
            {
                slot.Width = slot.Info.DefaultWidth;
                if (IsPersistedVisibility(slot.Info))
                    slot.Visible = slot.Info.DefaultVisible;
            }
            RebuildHeaderColumns();
            SaveColumnLayout();
        }

        /// <summary>
        /// 헤더의 컬럼(이름 뒤)을 보이는 컬럼만으로 다시 만든다. 컬럼마다 [구분선 열][데이터 열].
        /// CRITICAL: HeaderGrid.SizeChanged does NOT fire when GridSplitter rearranges
        /// internal columns — the Grid's total size stays the same. We must watch
        /// each ColumnDefinition individually (RegisterPropertyChangedCallback).
        /// </summary>
        private void RebuildHeaderColumns()
        {
            _isRebuildingHeader = true;
            try
            {
                DetachHeaderColumnCallbacks();

                for (int i = HeaderGrid.Children.Count - 1; i >= 0; i--)
                {
                    if (HeaderGrid.Children[i] != NameHeaderButton)
                        HeaderGrid.Children.RemoveAt(i);
                }
                while (HeaderGrid.ColumnDefinitions.Count > 2)
                    HeaderGrid.ColumnDefinitions.RemoveAt(HeaderGrid.ColumnDefinitions.Count - 1);

                var headerStyle = (Style)Resources["HeaderButtonStyle"];
                foreach (var slot in _slots)
                {
                    slot.SplitterColumn = null;
                    slot.DataColumn = null;
                    slot.HeaderButton = null;
                    if (!slot.Visible) continue;

                    var splitterColumn = new ColumnDefinition { Width = GridLength.Auto };
                    var dataColumn = new ColumnDefinition { Width = new GridLength(slot.Width), MinWidth = slot.Info.MinWidth };
                    HeaderGrid.ColumnDefinitions.Add(splitterColumn);
                    HeaderGrid.ColumnDefinitions.Add(dataColumn);
                    int splitterIndex = HeaderGrid.ColumnDefinitions.Count - 2;

                    var splitter = new CommunityToolkit.WinUI.Controls.GridSplitter
                    {
                        Width = 2,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0),
                        ResizeDirection = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Opacity = 0.3
                    };
                    Grid.SetColumn(splitter, splitterIndex);
                    HeaderGrid.Children.Add(splitter);

                    var button = new Button { Tag = slot.Info.Id, Style = headerStyle };
                    button.Click += OnHeaderClick;
                    Grid.SetColumn(button, splitterIndex + 1);
                    HeaderGrid.Children.Add(button);

                    slot.SplitterColumn = splitterColumn;
                    slot.DataColumn = dataColumn;
                    slot.HeaderButton = button;
                    slot.SplitterToken = splitterColumn.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, OnColumnWidthChanged);
                    slot.DataToken = dataColumn.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, OnColumnWidthChanged);
                }

                UpdateSortIndicators();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] RebuildHeaderColumns error: {ex.Message}");
            }
            finally
            {
                _isRebuildingHeader = false;
            }

            RecalcCellTotalWidths();
            UpdateAllVisibleContainerWidths();

            // 새 구분선 열의 실제 너비(DPI 반올림)는 다음 레이아웃 뒤에야 알 수 있다
            void OnHeaderLayoutUpdated(object? sender, object e)
            {
                HeaderGrid.LayoutUpdated -= OnHeaderLayoutUpdated;
                if (_isCleanedUp) return;
                RecalcCellTotalWidths();
                UpdateAllVisibleContainerWidths();
            }
            HeaderGrid.LayoutUpdated += OnHeaderLayoutUpdated;
        }

        private void DetachHeaderColumnCallbacks()
        {
            foreach (var slot in _slots)
            {
                slot.SplitterColumn?.UnregisterPropertyChangedCallback(ColumnDefinition.WidthProperty, slot.SplitterToken);
                slot.DataColumn?.UnregisterPropertyChangedCallback(ColumnDefinition.WidthProperty, slot.DataToken);
                slot.SplitterColumn = null;
                slot.DataColumn = null;
            }
        }

        #endregion

        #region Shell Property Columns

        /// <summary>셸 속성을 읽을 수 있는 파일인지 (로컬 디스크, 클라우드 자리 표시자 아님).</summary>
        private static bool CanReadShellProperties(Models.FileItem file)
        {
            if (string.IsNullOrEmpty(file.Path)) return false;
            if (FileSystemRouter.IsRemotePath(file.Path) || Helpers.ArchivePathHelper.IsArchivePath(file.Path)) return false;
            return !DetailsColumns.IsCloudPlaceholder(file.Attributes);
        }

        /// <summary>
        /// 항목 하나의 셸 속성을 백그라운드에서 읽어 반영. 같은 항목의 중복 요청은 하나의 Task를 공유한다.
        /// 폴더를 바꾸면 _shellCts 취소로 대기 중인 요청이 빠진다.
        /// </summary>
        private Task LoadShellPropertiesAsync(FileViewModel item)
        {
            if (item.HasShellProperties) return Task.CompletedTask;

            // 폴더가 바뀌었으면 이전 폴더의 대기 중 요청은 버린다 (Details 뷰는 탐색 이벤트를 따로 받지 않음)
            var folder = _viewModel?.CurrentFolder;
            if (!ReferenceEquals(folder, _shellFolder))
            {
                CancelShellPropertyLoads();
                _shellFolder = folder;
            }

            if (_pendingShellReads.TryGetValue(item, out var pending)) return pending;

            if (item.Model is not Models.FileItem file || !CanReadShellProperties(file))
            {
                item.SetShellProperties(ShellPropertyValues.Empty);
                return Task.CompletedTask;
            }

            var cached = ShellPropertyReader.TryGetCached(file.Path, file.DateModified, file.Size);
            if (cached != null)
            {
                item.SetShellProperties(cached);
                return Task.CompletedTask;
            }

            var task = LoadAsync(_shellCts.Token);
            if (!task.IsCompleted) _pendingShellReads[item] = task;
            return task;

            async Task LoadAsync(System.Threading.CancellationToken ct)
            {
                try
                {
                    var values = await ShellPropertyReader.GetAsync(file.Path, file.DateModified, file.Size, ct);
                    if (!ct.IsCancellationRequested) item.SetShellProperties(values);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[DetailsModeView] Shell property load failed: {ex.Message}");
                }
                finally
                {
                    _pendingShellReads.Remove(item);
                }
            }
        }

        /// <summary>셸 컬럼을 막 켰을 때: 이미 화면에 있는 행은 ContainerContentChanging이 다시 안 오므로 직접 요청.</summary>
        private void RequestShellPropertiesForRealizedItems()
        {
            var panel = DetailsListView?.ItemsPanelRoot;
            if (panel == null) return;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            {
                if (VisualTreeHelper.GetChild(panel, i) is ListViewItem { Content: FileViewModel file })
                    _ = LoadShellPropertiesAsync(file);
            }
        }

        /// <summary>셸 속성 기준 정렬: 아직 안 읽은 파일을 모두 읽고 나서 같은 기준이면 다시 정렬.</summary>
        private async void LoadShellPropertiesForSort(string sortBy)
        {
            try
            {
                var folder = ViewModel?.CurrentFolder;
                if (folder == null) return;

                var missing = folder.Children.OfType<FileViewModel>().Where(f => !f.HasShellProperties).ToList();
                if (missing.Count == 0) return;

                var ct = _shellCts.Token;
                await Task.WhenAll(missing.Select(LoadShellPropertiesAsync));

                if (ct.IsCancellationRequested || _isCleanedUp) return;
                if (_currentSortBy != sortBy || ViewModel?.CurrentFolder != folder) return;
                SortItems(sortBy, _isAscending);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Shell sort load failed: {ex.Message}");
            }
        }

        private void CancelShellPropertyLoads()
        {
            try { _shellCts.Cancel(); } catch (ObjectDisposedException) { }
            _shellCts.Dispose();
            _shellCts = new System.Threading.CancellationTokenSource();
            _pendingShellReads.Clear();
        }

        #endregion

        #region Feature #31: Column Filters

        private void OnFilterButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string column) return;

            var flyout = new Flyout();
            var panel = new StackPanel { Spacing = 6, MinWidth = 180, Padding = new Thickness(4) };

            switch (column)
            {
                case "Name":
                    BuildNameFilterUI(panel);
                    break;
                case "DateModified":
                    BuildDateFilterUI(panel);
                    break;
                case "Type":
                    BuildTypeFilterUI(panel);
                    break;
                case "Size":
                    BuildSizeFilterUI(panel);
                    break;
            }

            flyout.Content = panel;
            flyout.ShowAt(button);
        }

        private void BuildNameFilterUI(StackPanel panel)
        {
            var header = new TextBlock
            {
                Text = "Filter by Name",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(header);

            var textBox = new TextBox
            {
                PlaceholderText = "Type to filter...",
                Text = _nameFilter,
                MinWidth = 160
            };
            textBox.TextChanged += (s, _) =>
            {
                _nameFilter = textBox.Text;
                ApplyFiltersAndGrouping();
                UpdateFilterIndicator("Name", !string.IsNullOrEmpty(_nameFilter));
            };
            panel.Children.Add(textBox);

            var clearButton = new Button
            {
                Content = "Clear",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };
            clearButton.Click += (s, _) =>
            {
                textBox.Text = string.Empty;
                _nameFilter = string.Empty;
                ApplyFiltersAndGrouping();
                UpdateFilterIndicator("Name", false);
            };
            panel.Children.Add(clearButton);
        }

        private void BuildDateFilterUI(StackPanel panel)
        {
            var header = new TextBlock
            {
                Text = "Filter by Date",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(header);

            var dateRanges = new[] { "", "Today", "ThisWeek", "ThisMonth", "ThisYear", "Older" };
            var dateLabels = new[] { "All", "Today", "This Week", "This Month", "This Year", "Older" };

            for (int i = 0; i < dateRanges.Length; i++)
            {
                var range = dateRanges[i];
                var radio = new RadioButton
                {
                    Content = dateLabels[i],
                    IsChecked = _dateFilter == range,
                    GroupName = "DateFilter",
                    FontSize = 12,
                    MinHeight = 24,
                    Padding = new Thickness(4, 0, 0, 0)
                };
                radio.Checked += (s, _) =>
                {
                    _dateFilter = range;
                    ApplyFiltersAndGrouping();
                    UpdateFilterIndicator("DateModified", !string.IsNullOrEmpty(range));
                };
                panel.Children.Add(radio);
            }
        }

        private void BuildTypeFilterUI(StackPanel panel)
        {
            var header = new TextBlock
            {
                Text = "Filter by Type",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(header);

            // Get unique types from current items
            var items = _unfilteredItems ?? GetCurrentItemsList();
            var types = items
                .Select(x => x is FolderViewModel ? "Folder" : (string.IsNullOrEmpty(x.FileType) ? "Unknown" : x.FileType))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            // "All" option
            var allRadio = new RadioButton
            {
                Content = "All",
                IsChecked = string.IsNullOrEmpty(_typeFilter),
                GroupName = "TypeFilter",
                FontSize = 12,
                MinHeight = 24,
                Padding = new Thickness(4, 0, 0, 0)
            };
            allRadio.Checked += (s, _) =>
            {
                _typeFilter = string.Empty;
                ApplyFiltersAndGrouping();
                UpdateFilterIndicator("Type", false);
            };
            panel.Children.Add(allRadio);

            // Wrap in a ScrollViewer if many types
            var scrollPanel = new StackPanel { Spacing = 2 };

            foreach (var type in types)
            {
                var radio = new RadioButton
                {
                    Content = type,
                    IsChecked = _typeFilter == type,
                    GroupName = "TypeFilter",
                    FontSize = 12,
                    MinHeight = 24,
                    Padding = new Thickness(4, 0, 0, 0)
                };
                var captured = type;
                radio.Checked += (s, _) =>
                {
                    _typeFilter = captured;
                    ApplyFiltersAndGrouping();
                    UpdateFilterIndicator("Type", true);
                };
                scrollPanel.Children.Add(radio);
            }

            var scrollViewer = new ScrollViewer
            {
                Content = scrollPanel,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            panel.Children.Add(scrollViewer);
        }

        private void BuildSizeFilterUI(StackPanel panel)
        {
            var header = new TextBlock
            {
                Text = "Filter by Size",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(header);

            var sizeRanges = new[] { "", "Empty", "Tiny", "Small", "Medium", "Large", "Huge" };
            var sizeLabels = new[] { "All", "Empty (0 B)", "Tiny (< 16 KB)", "Small (< 1 MB)", "Medium (< 128 MB)", "Large (< 1 GB)", "Huge (> 1 GB)" };

            for (int i = 0; i < sizeRanges.Length; i++)
            {
                var range = sizeRanges[i];
                var radio = new RadioButton
                {
                    Content = sizeLabels[i],
                    IsChecked = _sizeFilter == range,
                    GroupName = "SizeFilter",
                    FontSize = 12,
                    MinHeight = 24,
                    Padding = new Thickness(4, 0, 0, 0)
                };
                radio.Checked += (s, _) =>
                {
                    _sizeFilter = range;
                    ApplyFiltersAndGrouping();
                    UpdateFilterIndicator("Size", !string.IsNullOrEmpty(range));
                };
                panel.Children.Add(radio);
            }
        }

        private void UpdateFilterIndicator(string column, bool active)
        {
            // 필터 버튼 UI 제거됨 — 필터 상태는 내부적으로만 관리
        }

        private bool PassesFilter(FileSystemViewModel item)
        {
            // Name filter
            if (!string.IsNullOrEmpty(_nameFilter))
            {
                if (item.Name == null || !item.Name.Contains(_nameFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Date filter
            if (!string.IsNullOrEmpty(_dateFilter))
            {
                var date = item.DateModifiedValue;
                var now = DateTime.Now;
                bool passes = _dateFilter switch
                {
                    "Today" => date.Date == now.Date,
                    "ThisWeek" => date >= now.Date.AddDays(-(int)now.DayOfWeek),
                    "ThisMonth" => date.Year == now.Year && date.Month == now.Month,
                    "ThisYear" => date.Year == now.Year,
                    "Older" => date.Year < now.Year,
                    _ => true
                };
                if (!passes) return false;
            }

            // Type filter
            if (!string.IsNullOrEmpty(_typeFilter))
            {
                var itemType = item is FolderViewModel ? "Folder" :
                    (string.IsNullOrEmpty(item.FileType) ? "Unknown" : item.FileType);
                if (!string.Equals(itemType, _typeFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Size filter
            if (!string.IsNullOrEmpty(_sizeFilter))
            {
                var size = item.SizeValue;
                bool passes = _sizeFilter switch
                {
                    "Empty" => item is FolderViewModel || size == 0,
                    "Tiny" => item is FolderViewModel || (size > 0 && size < 16 * 1024),
                    "Small" => item is FolderViewModel || (size >= 16 * 1024 && size < 1024 * 1024),
                    "Medium" => item is FolderViewModel || (size >= 1024 * 1024 && size < 128 * 1024 * 1024),
                    "Large" => item is FolderViewModel || (size >= 128 * 1024 * 1024 && size < 1024L * 1024 * 1024),
                    "Huge" => item is FolderViewModel || size >= 1024L * 1024 * 1024,
                    _ => true
                };
                if (!passes) return false;
            }

            return true;
        }

        private bool HasAnyFilter => !string.IsNullOrEmpty(_nameFilter) ||
                                     !string.IsNullOrEmpty(_dateFilter) ||
                                     !string.IsNullOrEmpty(_typeFilter) ||
                                     !string.IsNullOrEmpty(_sizeFilter);

        #endregion

        #region Combined Filter + Grouping Application

        /// <summary>
        /// Applies the current filter settings and grouping to the ListView.
        /// Call this after any filter or grouping change.
        /// </summary>
        private void ApplyFiltersAndGrouping()
        {
            if (ViewModel?.CurrentFolder == null) return;

            // Get the base items (from unfiltered cache or current children)
            var allItems = _unfilteredItems ?? GetCurrentItemsList();

            // Apply filters
            var filtered = HasAnyFilter
                ? allItems.Where(PassesFilter).ToList()
                : allItems;

            // Apply grouping
            if (_currentGroupBy != "None" && !string.IsNullOrEmpty(_currentGroupBy))
            {
                var groups = Helpers.GroupByHelper.BuildGroups(
                    filtered, _currentGroupBy, _collapsedGroups, ViewModel.CurrentFolder);
                _currentGroups = groups;

                var cvs = new CollectionViewSource
                {
                    Source = groups,
                    IsSourceGrouped = true
                };

                DetailsListView.ItemsSource = cvs.View;
            }
            else if (HasAnyFilter)
            {
                // Filters active but no grouping — set filtered list directly
                DetailsListView.ItemsSource = filtered;
            }
            else
            {
                // No filters, no grouping — bind back to ViewModel
                DetailsListView.SetBinding(
                    ListView.ItemsSourceProperty,
                    new Binding { Path = new PropertyPath("CurrentItems"), Mode = BindingMode.OneWay });
            }
        }

        /// <summary>
        /// Gets a snapshot of current items for filtering/grouping.
        /// </summary>
        private List<FileSystemViewModel> GetCurrentItemsList()
        {
            if (ViewModel?.CurrentFolder?.Children == null)
                return new List<FileSystemViewModel>();
            return ViewModel.CurrentFolder.Children.ToList();
        }

        /// <summary>
        /// 다운로드 폴더 자동 정렬 상태 추적. 벗어날 때 원래 정렬로 복원.
        /// </summary>
        private bool _isAutoSortedForDownloads;
        private string? _savedSortBeforeDownloads;
        private bool _savedAscBeforeDownloads;

        /// <summary>
        /// Called when ViewModel changes or sort completes. Re-applies all view settings.
        /// Downloads folder: auto-override to DateModified descending.
        /// </summary>
        // Ordner/Children-Sammlung, für die Sortierung + Gruppierung zuletzt angewendet wurden
        private FolderViewModel? _appliedFolder;
        private object? _appliedChildren;
        private bool _isApplyingView;
        private bool _reapplyPending;

        /// <summary>
        /// Ordnerwechsel oder fertig geladene/neu geladene Children: Details-Sortierung und -Gruppierung erneut
        /// anwenden (wie im Explorer bleiben sie beim Navigieren erhalten). Ohne das zeigte eine aktive Gruppierung
        /// nach dem Navigieren weiter den alten Ordner, und die Sortierung fiel auf die Miller-Sortierung zurück.
        /// Eine ausgeblendete Details-Ansicht (Miller aktiv) sortiert nicht mit — die Children sind geteilt.
        /// </summary>
        private void OnExplorerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ExplorerViewModel.CurrentItems)) return;
            if (_isCleanedUp || !_isLoaded || _isApplyingView || _reapplyPending) return;

            var folder = _viewModel?.CurrentFolder;
            if (folder == null || folder.IsSorting || folder.IsBulkUpdating) return;
            if (ReferenceEquals(folder, _appliedFolder) && ReferenceEquals(folder.Children, _appliedChildren)) return;

            _reapplyPending = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _reapplyPending = false;
                if (_isCleanedUp || ActualWidth <= 0) return;
                var current = _viewModel?.CurrentFolder;
                if (current == null || current.IsSorting || current.IsBulkUpdating) return;
                if (ReferenceEquals(current, _appliedFolder) && ReferenceEquals(current.Children, _appliedChildren)) return;
                ApplyCurrentView();
            });
        }

        private void ApplyCurrentView()
        {
            _isApplyingView = true;
            try
            {
                ApplyCurrentViewCore();
            }
            finally
            {
                _isApplyingView = false;
                _appliedFolder = ViewModel?.CurrentFolder;
                _appliedChildren = _appliedFolder?.Children;
            }
        }

        private void ApplyCurrentViewCore()
        {
            // Neuer Ordner: zwischengespeicherte (sortierte) Liste des alten Ordners verwerfen
            if (!ReferenceEquals(ViewModel?.CurrentFolder, _appliedFolder))
                _unfilteredItems = null;

            bool isDownloads = ViewModel?.CurrentFolder is FolderViewModel folder && folder.IsDownloadsFolder;

            if (isDownloads)
            {
                if (!_isAutoSortedForDownloads)
                {
                    _savedSortBeforeDownloads = _currentSortBy;
                    _savedAscBeforeDownloads = _isAscending;
                    _isAutoSortedForDownloads = true;
                }
                _currentSortBy = "DateModified";
                _isAscending = false;
                UpdateSortIndicators();
            }
            else if (_isAutoSortedForDownloads)
            {
                _currentSortBy = _savedSortBeforeDownloads ?? "Name";
                _isAscending = _savedAscBeforeDownloads;
                _isAutoSortedForDownloads = false;
                UpdateSortIndicators();
            }

            SortItems(_currentSortBy, _isAscending);
            TriggerGitStateLoad();
        }

        private System.Threading.CancellationTokenSource? _gitCts;

        /// <summary>
        /// Details 뷰 진입/탭 전환 시 현재 폴더의 Git 상태를 비동기 로드.
        /// 백그라운드에서 git status 실행 → UI 스레드에서 상태 주입.
        /// </summary>
        private async void TriggerGitStateLoad()
        {
            if (_viewModel?.CurrentFolder == null) return;
            if (_settings != null && !_settings.ShowGitIntegration) return;

            var folder = _viewModel.CurrentFolder;

            // Git 레포 폴더 → 자동으로 Git 컬럼 표시, 아닌 폴더 → 자동 숨김
            SetColumnVisible(DetailsColumns.Git, folder.IsGitFolder, persist: false);
            if (!folder.IsGitFolder) return;

            try { _gitCts?.Cancel(); } catch (ObjectDisposedException) { }
            _gitCts = new System.Threading.CancellationTokenSource();
            var ct = _gitCts.Token;

            try
            {
                // 백그라운드에서 git status 실행 (캐시 채움)
                var gitSvc = App.Current.Services.GetService(typeof(GitStatusService)) as GitStatusService;
                if (gitSvc == null || !gitSvc.IsAvailable) return;

                await Task.Run(async () =>
                {
                    await gitSvc.GetFolderStatesAsync(folder.Path, ct);
                }, ct);

                if (ct.IsCancellationRequested) return;

                // UI 스레드에서 Children에 주입 (이미 UI 스레드)
                foreach (var child in folder.Children)
                {
                    if (ct.IsCancellationRequested) break;
                    folder.InjectGitStateIfNeeded(child);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Git state load error: {ex.Message}");
            }
        }

        #endregion

        #region Rubber Band Selection

        private void OnListViewWrapperLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid || _rubberBandHelper != null) return;

            _rubberBandHelper = new Helpers.RubberBandSelectionHelper(
                grid,
                DetailsListView,
                () => _isSyncingSelection,
                val => _isSyncingSelection = val,
                items => ViewModel?.CurrentFolder?.SyncSelectedItems(items),
                () => (ContextMenuHost as MainWindow)?.ViewModel?.UpdateStatusBar());

            _rightDragHelper ??= new Helpers.RightButtonDragHelper(
                DetailsListView, selectDraggedItem: true, OnRightDragStarting, OnRightDragCompleted);
        }

        private void OnListViewWrapperUnloaded(object sender, RoutedEventArgs e)
        {
            _rubberBandHelper?.Detach();
            _rubberBandHelper = null;
            _rightDragHelper?.Detach();
            _rightDragHelper = null;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// CRITICAL: Called from MainWindow.OnClosed BEFORE visual tree teardown.
        /// Prevents WinUI crash by disconnecting bindings early.
        /// </summary>
        public void Cleanup()
        {
            if (_isCleanedUp) return;
            SaveColumnLayout();
            PerformCleanup();
        }

        private void PerformCleanup()
        {
            if (_isCleanedUp) return;
            _isCleanedUp = true;

            try
            {
                Helpers.DebugLogger.Log("[DetailsModeView] Starting cleanup...");

                try { _gitCts?.Cancel(); _gitCts?.Dispose(); } catch (ObjectDisposedException) { }
                _gitCts = null;

                _rubberBandHelper?.Detach();
                _rubberBandHelper = null;
                _rightDragHelper?.Detach();
                _rightDragHelper = null;

                // Unregister column width callbacks (only if Loaded fired and registered them)
                if (_isLoaded)
                {
                    DetachHeaderColumnCallbacks();
                }

                _layoutSaveTimer?.Stop();
                CancelShellPropertyLoads();
                _shellFolder = null;

                if (_settings != null)
                {
                    _settings.SettingChanged -= OnSettingChanged;
                    _settings = null;
                }

                if (DetailsListView != null)
                {
                    DetailsListView.DoubleTapped -= OnItemDoubleClick;
                    DetailsListView.KeyDown -= OnDetailsKeyDown;
                    DetailsListView.ContainerContentChanging -= OnContainerContentChanging;
                    DetailsListView.ItemsSource = null;
                    DetailsListView.SelectedItem = null;
                }

                _unfilteredItems = null;
                if (_viewModel != null) _viewModel.PropertyChanged -= OnExplorerPropertyChanged;
                _viewModel = null;
                _appliedFolder = null;
                _appliedChildren = null;
                RootGrid.DataContext = null;

                Helpers.DebugLogger.Log("[DetailsModeView] Cleanup complete");
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[DetailsModeView] Cleanup error: {ex.Message}");
            }
        }

        #endregion
    }
}
