using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using Span.Models;
using Span.ViewModels;
using Span.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Span
{
    /// <summary>
    /// MainWindow의 분할 뷰 및 미리보기 패널 관리 부분 클래스.
    /// 좌/우 패널 활성 상태 관리, 분할 뷰 토글, 미리보기 패널 초기화·업데이트,
    /// 인라인 미리보기 컬럼(Miller Columns 모드), 선택 기반 미리보기 갱신,
    /// 활성 Explorer/ScrollViewer 접근자 등을 담당한다.
    /// </summary>
    public sealed partial class MainWindow
    {
        #region Active Pane Helpers

        /// <summary>
        /// 현재 활성 패널의 Miller Columns ItemsControl을 반환한다.
        /// 분할 뷰에서 우측 패널이 활성이면 Right 컨트롤, 아니면 활성 탭의 컨트롤을 반환한다.
        /// </summary>
        private ItemsControl GetActiveMillerColumnsControl()
        {
            if (ViewModel.IsSplitViewEnabled)
            {
                return ViewModel.ActivePane switch
                {
                    ActivePane.Right => MillerColumnsControlRight,
                    ActivePane.TopRight => MillerColumnsControlTopRight,
                    ActivePane.BottomRight => MillerColumnsControlBottomRight,
                    _ => GetLeftMillerColumnsControl(),
                };
            }

            return GetLeftMillerColumnsControl();
        }

        private ItemsControl GetLeftMillerColumnsControl()
        {
            if (_activeMillerTabId != null && _tabMillerPanels.TryGetValue(_activeMillerTabId, out var panel))
                return panel.items;
            return MillerColumnsControl;
        }

        /// <summary>
        /// Returns the ScrollViewer for the currently active pane.
        /// </summary>
        private ScrollViewer GetActiveMillerScrollViewer()
        {
            if (ViewModel.IsSplitViewEnabled)
            {
                return ViewModel.ActivePane switch
                {
                    ActivePane.Right => MillerScrollViewerRight,
                    ActivePane.TopRight => MillerScrollViewerTopRight,
                    ActivePane.BottomRight => MillerScrollViewerBottomRight,
                    _ => GetLeftMillerScrollViewer(),
                };
            }

            return GetLeftMillerScrollViewer();
        }

        private ScrollViewer GetLeftMillerScrollViewer()
        {
            if (_activeMillerTabId != null && _tabMillerPanels.TryGetValue(_activeMillerTabId, out var panel))
                return panel.scroller;
            return MillerScrollViewer;
        }

        /// <summary>
        /// View mode for file operations (copy, delete, paste, selection).
        /// Dual/Quad share one explorer view mode; CurrentViewMode is the source of truth.
        /// </summary>
        private ViewMode GetActivePaneViewMode() => ViewModel.CurrentViewMode;

        /// <summary>
        /// Map an explorer instance to its Miller ItemsControl (independent of ActivePane).
        /// </summary>
        private ItemsControl? GetMillerColumnsControlForExplorer(ExplorerViewModel explorer)
        {
            if (ReferenceEquals(explorer, ViewModel.RightExplorer)) return MillerColumnsControlRight;
            if (ReferenceEquals(explorer, ViewModel.TopRightExplorer)) return MillerColumnsControlTopRight;
            if (ReferenceEquals(explorer, ViewModel.BottomRightExplorer)) return MillerColumnsControlBottomRight;
            return GetLeftMillerColumnsControl();
        }

        /// <summary>
        /// Map an explorer instance to its Miller ScrollViewer (independent of ActivePane).
        /// </summary>
        private ScrollViewer? GetMillerScrollViewerForExplorer(ExplorerViewModel explorer)
        {
            if (ReferenceEquals(explorer, ViewModel.RightExplorer)) return MillerScrollViewerRight;
            if (ReferenceEquals(explorer, ViewModel.TopRightExplorer)) return MillerScrollViewerTopRight;
            if (ReferenceEquals(explorer, ViewModel.BottomRightExplorer)) return MillerScrollViewerBottomRight;
            return GetLeftMillerScrollViewer();
        }

        #endregion

        #region x:Bind Visibility / Brush Helpers

        // --- x:Bind visibility/brush helpers ---

        public Visibility IsSplitVisible(bool isSplitViewEnabled)
            => isSplitViewEnabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IsNotSplitVisible(bool isSplitViewEnabled)
            => isSplitViewEnabled ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Unified bar (address bar + toolbar): hidden only in Settings/ActionLog mode.
        /// RecycleBin은 주소바 영역을 유지한다 (Home과 동일).
        /// </summary>
        public Visibility IsNotSettingsMode(Models.ViewMode mode)
            => (mode != Models.ViewMode.Settings && mode != Models.ViewMode.ActionLog) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Split/Preview buttons: hidden in Settings/ActionLog/RecycleBin mode
        /// </summary>
        public Visibility IsNotSpecialMode(Models.ViewMode mode)
            => (mode != Models.ViewMode.Settings && mode != Models.ViewMode.ActionLog && mode != Models.ViewMode.RecycleBin) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Settings/ActionLog 모드일 때만 표시 (탭↔콘텐츠 연결 strip)
        /// </summary>
        public Visibility IsSettingsOrActionLogMode(Models.ViewMode mode)
            => (mode == Models.ViewMode.Settings || mode == Models.ViewMode.ActionLog) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Unified Bar 하단 border: 분할뷰에서는 LeftPathHeader border와 중복되므로 제거
        /// </summary>
        public Thickness UnifiedBarBorderThickness(bool isSplitViewEnabled)
            => isSplitViewEnabled ? new Thickness(0) : new Thickness(0, 0, 0, 1);

        /// <summary>
        /// Single mode toolbar/address bar: visible when NOT split AND NOT Home mode
        /// </summary>
        public Visibility IsSingleNonHomeVisible(bool isSplitViewEnabled, Models.ViewMode mode)
            => (!isSplitViewEnabled && mode != Models.ViewMode.Home) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Single mode nav/address bar: visible when NOT split AND NOT Settings/ActionLog mode (Home included)
        /// </summary>
        public Visibility IsSingleNonSettingsVisible(bool isSplitViewEnabled, Models.ViewMode mode)
            => (!isSplitViewEnabled && mode != Models.ViewMode.Settings && mode != Models.ViewMode.ActionLog) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Left pane header (split mode): visible when split enabled (including Home mode for accent bar)
        /// </summary>
        public Visibility IsLeftPaneHeaderVisible(bool isSplitViewEnabled, Models.ViewMode mode)
            => (isSplitViewEnabled && mode != Models.ViewMode.Settings && mode != Models.ViewMode.ActionLog)
                ? Visibility.Visible : Visibility.Collapsed;

        public double LeftPaneAccentOpacity(ActivePane activePane)
            => activePane == ActivePane.Left ? 1.0 : 0.0;

        public double RightPaneAccentOpacity(ActivePane activePane)
            => activePane == ActivePane.Right ? 1.0 : 0.0;

        public double PaneAccentOpacity(ActivePane activePane, bool isQuad, ActivePane pane)
            => isQuad && activePane == pane ? 1.0 : 0.0;

        #endregion

        #region Focus Tracking

        // --- Focus tracking ---

        /// <summary>
        /// 좌측 패널 GotFocus 이벤트. ActivePane을 Left로 설정한다.
        /// </summary>
        private void OnLeftPaneGotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActivePane != ActivePane.Left)
            {
                ViewModel.ActivePane = ActivePane.Left;
            }
        }

        /// <summary>
        /// 우측 패널 GotFocus 이벤트. ActivePane을 Right로 설정한다.
        /// </summary>
        private void OnRightPaneGotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActivePane != ActivePane.Right)
            {
                ViewModel.ActivePane = ActivePane.Right;
            }
        }

        private void OnTopRightPaneGotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActivePane != ActivePane.TopRight)
                ViewModel.ActivePane = ActivePane.TopRight;
        }

        private void OnBottomRightPaneGotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActivePane != ActivePane.BottomRight)
                ViewModel.ActivePane = ActivePane.BottomRight;
        }

        private void OnTopRightPaneHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => ViewModel.ActivePane = ActivePane.TopRight;

        private void OnBottomRightPaneHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => ViewModel.ActivePane = ActivePane.BottomRight;

        /// <summary>
        /// 빈 공간 클릭 시에도 ActivePane을 전환하고 포커스를 이동.
        /// GotFocus는 포커스 가능 요소가 hit될 때만 발생하므로, 빈 공간에서는
        /// PointerPressed로 보완해야 함.
        /// 패인 헤더 버튼 클릭 시에는 FocusActivePane()을 생략하여
        /// Button의 pressed 상태를 보존 (Click 이벤트 정상 발생 보장).
        /// </summary>
        private void OnLeftPanePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsDragInProgress) return;
            if (ViewModel.ActivePane != ActivePane.Left)
            {
                ViewModel.ActivePane = ActivePane.Left;
                // 패인 헤더 내 버튼 클릭 시 FocusActivePane() 호출하면
                // Low priority 디스패처가 Button 포커스를 빼앗아 Click 이벤트가 씹힘
                // 우클릭(컨텍스트 메뉴) 시에도 FocusActivePane 생략 — 우클릭은
                // ListView가 자체적으로 해당 항목을 선택하므로 추가 포커스 이동 불필요
                var props = e.GetCurrentPoint(sender as UIElement).Properties;
                if (!props.IsRightButtonPressed && !IsDescendant(LeftPathHeader, e.OriginalSource as DependencyObject))
                    FocusActivePane();
            }
        }

        private void OnRightPanePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsDragInProgress) return;
            if (ViewModel.ActivePane != ActivePane.Right)
            {
                ViewModel.ActivePane = ActivePane.Right;
                var props = e.GetCurrentPoint(sender as UIElement).Properties;
                if (!props.IsRightButtonPressed && !IsDescendant(RightPathHeader, e.OriginalSource as DependencyObject))
                    FocusActivePane();
            }
        }

        private void OnTopRightPanePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsDragInProgress) return;
            if (ViewModel.ActivePane != ActivePane.TopRight)
            {
                ViewModel.ActivePane = ActivePane.TopRight;
                var props = e.GetCurrentPoint(sender as UIElement).Properties;
                if (!props.IsRightButtonPressed && !IsDescendant(TopRightPathHeader, e.OriginalSource as DependencyObject))
                    FocusActivePane();
            }
        }

        private void OnBottomRightPanePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsDragInProgress) return;
            if (ViewModel.ActivePane != ActivePane.BottomRight)
            {
                ViewModel.ActivePane = ActivePane.BottomRight;
                var props = e.GetCurrentPoint(sender as UIElement).Properties;
                if (!props.IsRightButtonPressed && !IsDescendant(BottomRightPathHeader, e.OriginalSource as DependencyObject))
                    FocusActivePane();
            }
        }

        private void OnLeftPaneHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.ActivePane = ActivePane.Left;
        }

        private void OnRightPaneHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.ActivePane = ActivePane.Right;
        }

        #endregion

        #region Pane-Specific Flyout / View Mode Menus

        // --- Pane-specific flyout opening handlers (set ActivePane before menu item click) ---

        private void OnLeftPaneSortMenuOpening(object sender, object e)
        {
            ViewModel.ActivePane = ActivePane.Left;
        }

        private void OnRightPaneSortMenuOpening(object sender, object e)
        {
            ViewModel.ActivePane = ActivePane.Right;
        }

        private void OnMainViewModeMenuOpening(object sender, object e)
        {
            LocalizeViewMenuItems(MainVm_Miller, MainVm_Details, MainVm_Icons,
                MainVm_ExtraLarge, MainVm_Large, MainVm_Medium, MainVm_Small);
        }

        private void OnLeftPaneViewModeMenuOpening(object sender, object e)
        {
            ViewModel.ActivePane = ActivePane.Left;
            LocalizeViewMenuItems(LeftVm_Miller, LeftVm_Details, LeftVm_Icons,
                LeftVm_ExtraLarge, LeftVm_Large, LeftVm_Medium, LeftVm_Small);
        }

        private void OnRightPaneViewModeMenuOpening(object sender, object e)
        {
            ViewModel.ActivePane = ActivePane.Right;
            LocalizeViewMenuItems(RightVm_Miller, RightVm_Details, RightVm_Icons,
                RightVm_ExtraLarge, RightVm_Large, RightVm_Medium, RightVm_Small);
        }

        private void LocalizeViewMenuItems(
            MenuFlyoutItem miller, MenuFlyoutItem details, MenuFlyoutSubItem icons,
            MenuFlyoutItem extraLarge, MenuFlyoutItem large, MenuFlyoutItem medium, MenuFlyoutItem small)
        {
            miller.Text = _loc.Get("MillerColumns");
            details.Text = _loc.Get("Details");
            icons.Text = _loc.Get("Icons");
            extraLarge.Text = _loc.Get("ExtraLargeIcons");
            large.Text = _loc.Get("LargeIcons");
            medium.Text = _loc.Get("MediumIcons");
            small.Text = _loc.Get("SmallIcons");
        }

        /// <summary>
        /// Applies all localized strings to UI elements that have hardcoded XAML text.
        /// Called once at startup and again whenever <see cref="Services.LocalizationService.LanguageChanged"/> fires.
        /// </summary>
        private void LocalizeViewModeTooltips()
        {
            // --- Toolbar tooltips (single-pane mode) ---
            ToolTipService.SetToolTip(NewTabButton, _loc.Get("Tooltip_NewTab"));
            ToolTipService.SetToolTip(BackButton, _loc.Get("Tooltip_Back"));
            ToolTipService.SetToolTip(ForwardButton, _loc.Get("Tooltip_Forward"));
            ToolTipService.SetToolTip(UpButton, _loc.Get("Tooltip_Up"));
            ToolTipService.SetToolTip(CopyPathButton, _loc.Get("Tooltip_CopyPath"));
            ToolTipService.SetToolTip(NewFolderButton, _loc.Get("Tooltip_NewFolder"));
            ToolTipService.SetToolTip(NewItemDropdown, _loc.Get("Tooltip_NewFile"));
            var newTip = _loc.Get("Tooltip_New");
            ToolTipService.SetToolTip(LeftNewButton, newTip);
            ToolTipService.SetToolTip(RightNewButton, newTip);
            ToolTipService.SetToolTip(TopRightNewButton, newTip);
            ToolTipService.SetToolTip(BottomRightNewButton, newTip);
            ToolTipService.SetToolTip(ToolbarCutButton, _loc.Get("Tooltip_Cut"));
            ToolTipService.SetToolTip(ToolbarCopyButton, _loc.Get("Tooltip_Copy"));
            ToolTipService.SetToolTip(ToolbarPasteButton, _loc.Get("Tooltip_Paste"));
            ToolTipService.SetToolTip(ToolbarRenameButton, _loc.Get("Tooltip_Rename"));
            ToolTipService.SetToolTip(ToolbarDeleteButton, _loc.Get("Tooltip_Delete"));
            ToolTipService.SetToolTip(SortButton, _loc.Get("Tooltip_Sort"));
            ToolTipService.SetToolTip(SplitViewButton, _loc.Get("Tooltip_SplitView"));
            UpdateSplitOrientationTooltip();
            if (QuadSplitButton != null)
                ToolTipService.SetToolTip(QuadSplitButton, _loc.Get("Tooltip_SplitOrientationQuad"));
            ToolTipService.SetToolTip(PreviewToggleButton, _loc.Get("Tooltip_Preview"));

            // RecycleBin mode toolbar tooltips (Issue #28)
            ToolTipService.SetToolTip(ToolbarRestoreButton, _loc.Get("RecycleBin_Restore") + " (Ctrl+Z)");
            ToolTipService.SetToolTip(ToolbarDeletePermButton, _loc.Get("RecycleBin_DeletePermanently") + " (Delete)");
            ToolTipService.SetToolTip(ToolbarEmptyBinButton, _loc.Get("RecycleBin_Empty"));
            ToolTipService.SetToolTip(ToolbarRefreshButton, _loc.Get("Refresh") + " (F5)");

            // View mode button tooltip (all three: main, left, right)
            var vmTip = _loc.Get("ViewModeSwitch");
            ToolTipService.SetToolTip(ViewModeButton, vmTip);
            ToolTipService.SetToolTip(LeftViewModeButton, vmTip);
            ToolTipService.SetToolTip(RightViewModeButton, vmTip);

            // Sidebar bottom bar tooltips
            ToolTipService.SetToolTip(HelpButton, _loc.Get("Tooltip_Help"));
            ToolTipService.SetToolTip(LogButton, _loc.Get("Tooltip_Log"));
            ToolTipService.SetToolTip(WorkspaceButton, _loc.Get("Cmd_Cat_Workspace"));
            ToolTipService.SetToolTip(SettingsButton, _loc.Get("Tooltip_Settings"));

            // --- Search placeholder ---
            SearchBox.PlaceholderText = _loc.Get("SearchPlaceholder");

            // --- Sidebar section labels ---
            SidebarHomeText.Text = _loc.Get("Home");
            SidebarFavoritesText.Text = _loc.Get("Favorites");
            SidebarLocalDrivesText.Text = _loc.Get("LocalDrives");
            SidebarCloudText.Text = _loc.Get("Cloud");
            SidebarNetworkText.Text = _loc.Get("Network");
            RecycleBinLabel.Text = _loc.Get("RecycleBin");

            // --- Main sort menu items ---
            SortByNameItem.Text = _loc.Get("Name");
            SortByDateItem.Text = _loc.Get("Date");
            SortBySizeItem.Text = _loc.Get("Size");
            SortByTypeItem.Text = _loc.Get("Type");
            SortAscendingItem.Text = _loc.Get("Ascending");
            SortDescendingItem.Text = _loc.Get("Descending");
            GroupBySubMenu.Text = _loc.Get("GroupBy");
            GroupByNoneItem.Text = _loc.Get("None");
            GroupByNameItem.Text = _loc.Get("Name");
            GroupByTypeItem.Text = _loc.Get("Type");
            GroupByDateItem.Text = _loc.Get("Date");
            GroupBySizeItem.Text = _loc.Get("Size");

            // --- Main view mode menu items ---
            MainVm_Miller.Text = _loc.Get("MillerColumns");
            MainVm_Details.Text = _loc.Get("Details");
            MainVm_List.Text = _loc.Get("ViewMode_List");
            MainVm_Icons.Text = _loc.Get("Icons");
            MainVm_ExtraLarge.Text = _loc.Get("ExtraLargeIcons");
            MainVm_Large.Text = _loc.Get("LargeIcons");
            MainVm_Medium.Text = _loc.Get("MediumIcons");
            MainVm_Small.Text = _loc.Get("SmallIcons");

            // --- Left pane tooltips ---
            ToolTipService.SetToolTip(LeftBackButton, _loc.Get("Tooltip_Back"));
            ToolTipService.SetToolTip(LeftForwardButton, _loc.Get("Tooltip_Forward"));
            ToolTipService.SetToolTip(LeftUpButton, _loc.Get("Tooltip_Up"));
            ToolTipService.SetToolTip(LeftCopyPathButton, _loc.Get("Tooltip_CopyPath"));
            ToolTipService.SetToolTip(LeftSortButton, _loc.Get("Tooltip_Sort"));
            ToolTipService.SetToolTip(LeftPreviewButton, _loc.Get("Tooltip_Preview"));

            // Left pane sort menu items
            LeftSortByNameItem.Text = _loc.Get("Name");
            LeftSortByDateItem.Text = _loc.Get("Date");
            LeftSortBySizeItem.Text = _loc.Get("Size");
            LeftSortByTypeItem.Text = _loc.Get("Type");
            LeftSortAscendingItem.Text = _loc.Get("Ascending");
            LeftSortDescendingItem.Text = _loc.Get("Descending");

            // Left pane view mode menu items
            LeftVm_Miller.Text = _loc.Get("MillerColumns");
            LeftVm_Details.Text = _loc.Get("Details");
            LeftVm_List.Text = _loc.Get("ViewMode_List");
            LeftVm_Icons.Text = _loc.Get("Icons");
            LeftVm_ExtraLarge.Text = _loc.Get("ExtraLargeIcons");
            LeftVm_Large.Text = _loc.Get("LargeIcons");
            LeftVm_Medium.Text = _loc.Get("MediumIcons");
            LeftVm_Small.Text = _loc.Get("SmallIcons");

            // --- Right pane tooltips ---
            ToolTipService.SetToolTip(RightBackButton, _loc.Get("Tooltip_Back"));
            ToolTipService.SetToolTip(RightForwardButton, _loc.Get("Tooltip_Forward"));
            ToolTipService.SetToolTip(RightUpButton, _loc.Get("Tooltip_Up"));
            ToolTipService.SetToolTip(RightCopyPathButton, _loc.Get("Tooltip_CopyPath"));
            ToolTipService.SetToolTip(RightSortButton, _loc.Get("Tooltip_Sort"));
            ToolTipService.SetToolTip(RightPreviewButton, _loc.Get("Tooltip_Preview"));

            // Right pane sort menu items
            RightSortByNameItem.Text = _loc.Get("Name");
            RightSortByDateItem.Text = _loc.Get("Date");
            RightSortBySizeItem.Text = _loc.Get("Size");
            RightSortByTypeItem.Text = _loc.Get("Type");
            RightSortAscendingItem.Text = _loc.Get("Ascending");
            RightSortDescendingItem.Text = _loc.Get("Descending");

            // Right pane view mode menu items
            RightVm_Miller.Text = _loc.Get("MillerColumns");
            RightVm_Details.Text = _loc.Get("Details");
            RightVm_List.Text = _loc.Get("ViewMode_List");
            RightVm_Icons.Text = _loc.Get("Icons");
            RightVm_ExtraLarge.Text = _loc.Get("ExtraLargeIcons");
            RightVm_Large.Text = _loc.Get("LargeIcons");
            RightVm_Medium.Text = _loc.Get("MediumIcons");
            RightVm_Small.Text = _loc.Get("SmallIcons");

            // --- Tab headers (Home / Settings / ActionLog) ---
            foreach (var tab in ViewModel.Tabs)
            {
                if (tab.ViewMode == Models.ViewMode.Home)
                    tab.Header = _loc.Get("Home");
                else if (tab.ViewMode == Models.ViewMode.Settings)
                    tab.Header = _loc.Get("Settings");
                else if (tab.ViewMode == Models.ViewMode.ActionLog)
                    tab.Header = _loc.Get("Log_Title");
            }

            // --- Sidebar favorites: localize known folder names ---
            ViewModel.LocalizeFavoriteNames();
        }

        #endregion

        #region Pane Preview Toggle

        private void OnPanePreviewToggle(object sender, RoutedEventArgs e)
        {
            // Tag에서 대상 패인을 결정 — ActivePane은 변경하지 않음 (포커스 사이드이펙트 방지)
            var targetPane = ActivePane.Left;
            if (sender is FrameworkElement fe && fe.Tag is string tag)
                targetPane = tag == "Right" ? ActivePane.Right : ActivePane.Left;

            TogglePreviewForPane(targetPane);
            UpdatePreviewButtonState();
        }

        #endregion

        // Breadcrumb scroll/overflow and breadcrumb click/chevron logic
        // are now handled internally by AddressBarControl.
        // Events are dispatched via OnAddressBarBreadcrumbClicked / OnAddressBarChevronClicked
        // in MainWindow.NavigationManager.cs.

        // ──── Legacy handlers removed ────
        // OnBreadcrumbScrollerSizeChanged, OnBreadcrumbContentSizeChanged,
        // OnBreadcrumbScrollerViewChanged, UpdateBreadcrumbOverflow,
        // OnPaneBreadcrumbClick, OnBreadcrumbChevronClick
        // are all now internal to AddressBarControl.

        #region Split View Toggle

        // --- Split View Toggle ---

        /// <summary>
        /// 분할 뷰 토글 버튼 클릭 이벤트.
        /// </summary>
        private void OnSplitViewToggleClick(object sender, RoutedEventArgs e)
        {
            ToggleSplitView();
            UpdateSplitViewButtonState();
            UpdateSplitOrientationButtonState();
            UpdateQuadSplitButtonState();
        }

        private void OnSplitOrientationToggleClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsSplitViewEnabled)
                return;

            var next = ViewModel.SplitLayoutMode switch
            {
                SplitLayoutMode.DualSideBySide => SplitLayoutMode.DualStacked,
                SplitLayoutMode.DualStacked => SplitLayoutMode.Quad,
                SplitLayoutMode.Quad => SplitLayoutMode.DualSideBySide,
                _ => SplitLayoutMode.DualSideBySide,
            };

            ViewModel.SetSplitLayoutMode(next);
            ApplySplitLayout();
            if (next == SplitLayoutMode.Quad)
                _ = InitializeQuadPanesAsync();
            UpdateSplitOrientationButtonState();
            UpdateQuadSplitButtonState();
        }

        /// <summary>
        /// One-click 2×2 quad layout. Click again while already in Quad to leave split view.
        /// </summary>
        private void OnQuadSplitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsRecycleBinTab) return;

            if (ViewModel.IsQuadSplit)
            {
                // Already 2×2 → exit split entirely
                ViewModel.SetSplitLayoutMode(SplitLayoutMode.Single);
                ApplySplitLayout();
                UnsubscribeRightExplorerForAddressBar();
                UnsubscribeQuadPaneAddressBars();
                ViewModel.ActivePane = ActivePane.Left;
                UpdateSplitViewButtonState();
                UpdateSplitOrientationButtonState();
                UpdateQuadSplitButtonState();
                UpdateViewModeVisibility();
                Helpers.DebugLogger.Log("[MainWindow] Quad split disabled via toolbar button");
                return;
            }

            ViewModel.SetSplitLayoutMode(SplitLayoutMode.Quad);
            ApplySplitLayout();
            _ = InitializeQuadPanesAsync();

            // Match ToggleSplitView: close side previews for space
            ViewModel.IsLeftPreviewEnabled = false;
            LeftPreviewSplitterCol.Width = new GridLength(0);
            LeftPreviewCol.Width = new GridLength(0);
            LeftPreviewPanel.StopMedia();
            ViewModel.IsRightPreviewEnabled = false;
            RightPreviewSplitterCol.Width = new GridLength(0);
            RightPreviewCol.Width = new GridLength(0);
            RightPreviewPanel.StopMedia();
            UpdatePreviewButtonState();

            if (ViewModel.Explorer?.PathSegments != null)
            {
                LeftAddressBar.PathSegments = ViewModel.Explorer.PathSegments;
                LeftAddressBar.CurrentPath = ViewModel.Explorer.CurrentPath;
            }

            ViewModel.NotifySplitViewChanged();
            UpdateSplitViewButtonState();
            UpdateSplitOrientationButtonState();
            UpdateQuadSplitButtonState();
            UpdateViewModeVisibility();
            Helpers.DebugLogger.Log("[MainWindow] Quad split enabled via toolbar button");
        }

        /// <summary>
        /// Apply side-by-side or stacked dual-pane layout.
        /// </summary>
        internal void ApplySplitLayout()
        {
            SetQuadPanesVisible(false);

            if (!ViewModel.IsSplitViewEnabled)
            {
                SplitterCol.Width = new GridLength(0);
                RightPaneCol.Width = new GridLength(0);
                SplitterRow.Height = new GridLength(0);
                BottomPaneRow.Height = new GridLength(0);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(SplitPaneDivider, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(SplitPaneDivider, 0);
                SplitPaneDivider.Width = 1;
                SplitPaneDivider.Height = double.NaN;
                SplitPaneDivider.HorizontalAlignment = HorizontalAlignment.Left;
                SplitPaneDivider.VerticalAlignment = VerticalAlignment.Stretch;
                RightPaneContainer.Margin = new Thickness(1, 0, 0, 0);
                UnsubscribeQuadPaneAddressBars();
                ViewModel.SyncExplorerAutoNavigationForLayout();
                UpdateViewModeVisibility();
                return;
            }

            if (ViewModel.SplitLayoutMode == SplitLayoutMode.Quad)
            {
                // Plan layout: equal * | 1px | * columns and rows
                SplitLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                SplitterCol.Width = new GridLength(1, GridUnitType.Pixel);
                RightPaneCol.Width = new GridLength(1, GridUnitType.Star);
                SplitLayoutGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                SplitterRow.Height = new GridLength(1, GridUnitType.Pixel);
                BottomPaneRow.Height = new GridLength(1, GridUnitType.Star);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(LeftPaneContainer, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(LeftPaneContainer, 1);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(TopRightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(TopRightPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(TopRightPaneContainer, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(TopRightPaneContainer, 1);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(RightPaneContainer, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(RightPaneContainer, 1);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(BottomRightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(BottomRightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(BottomRightPaneContainer, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(BottomRightPaneContainer, 1);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(SplitPaneDivider, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(SplitPaneDivider, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(SplitPaneDivider, 3);
                SplitPaneDivider.Width = double.NaN;
                SplitPaneDivider.Height = double.NaN;
                SplitPaneDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
                SplitPaneDivider.VerticalAlignment = VerticalAlignment.Stretch;

                LeftPaneContainer.Margin = new Thickness(0);
                RightPaneContainer.Margin = new Thickness(0, 1, 0, 0);
                TopRightPaneContainer.Margin = new Thickness(0);
                BottomRightPaneContainer.Margin = new Thickness(0, 1, 0, 0);

                SetQuadPanesVisible(true);
                // Keep shared mode: sync Right to Current before applying visibility
                if (ViewModel.RightViewMode != ViewModel.CurrentViewMode)
                    ViewModel.RightViewMode = ViewModel.CurrentViewMode;
                if (ViewModel.LeftViewMode != ViewModel.CurrentViewMode)
                    ViewModel.LeftViewMode = ViewModel.CurrentViewMode;
                ApplyQuadSharedViewVisibility();
                SyncRightAddressBar();
                SubscribeRightExplorerForAddressBar();
                SubscribeQuadPaneAddressBars();
                InitializeQuadMillerTemplates();
                RelayoutQuadMillerScroll();
                SplitLayoutGrid.InvalidateMeasure();
                return;
            }

            UnsubscribeQuadPaneAddressBars();
            ViewModel.SyncExplorerAutoNavigationForLayout();
            UpdateViewModeVisibility();

            if (ViewModel.SplitOrientation == SplitOrientation.Stacked)
            {
                SplitterCol.Width = new GridLength(0);
                RightPaneCol.Width = new GridLength(0);
                SplitterRow.Height = new GridLength(0);
                BottomPaneRow.Height = new GridLength(1, GridUnitType.Star);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(SplitPaneDivider, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(SplitPaneDivider, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(SplitPaneDivider, 1);
                SplitPaneDivider.Width = double.NaN;
                SplitPaneDivider.Height = 1;
                SplitPaneDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
                SplitPaneDivider.VerticalAlignment = VerticalAlignment.Top;
                LeftPaneContainer.Margin = new Thickness(0);
                RightPaneContainer.Margin = new Thickness(0, 1, 0, 0);
            }
            else
            {
                SplitterCol.Width = new GridLength(0);
                RightPaneCol.Width = new GridLength(1, GridUnitType.Star);
                SplitterRow.Height = new GridLength(0);
                BottomPaneRow.Height = new GridLength(0);

                Microsoft.UI.Xaml.Controls.Grid.SetColumn(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(LeftPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPaneContainer, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPaneContainer, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(SplitPaneDivider, 2);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(SplitPaneDivider, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(SplitPaneDivider, 1);
                SplitPaneDivider.Width = 1;
                SplitPaneDivider.Height = double.NaN;
                SplitPaneDivider.HorizontalAlignment = HorizontalAlignment.Left;
                SplitPaneDivider.VerticalAlignment = VerticalAlignment.Stretch;
                LeftPaneContainer.Margin = new Thickness(0);
                RightPaneContainer.Margin = new Thickness(1, 0, 0, 0);
            }
        }

        /// <summary>
        /// RightExplorer PropertyChanged 구독 — RightAddressBar 동기화용
        /// </summary>
        private PropertyChangedEventHandler? _rightExplorerAddressBarHandler;
        private NotifyCollectionChangedEventHandler? _rightExplorerPathSegmentsHandler;

        /// <summary>
        /// Re-apply split layout and right pane after session restore on startup.
        /// </summary>
        internal void RestoreSplitViewOnStartup()
        {
            if (!ViewModel.IsSplitViewEnabled)
                return;

            ApplySplitLayout();

            if (ViewModel.Explorer?.PathSegments != null)
            {
                LeftAddressBar.PathSegments = ViewModel.Explorer.PathSegments;
                LeftAddressBar.CurrentPath = ViewModel.Explorer.CurrentPath;
            }

            if (ViewModel.IsQuadSplit)
            {
                _ = InitializeQuadPanesAsync();
                ViewModel.NotifySplitViewChanged();
                UpdateSplitViewButtonState();
                UpdateSplitOrientationButtonState();
                UpdateViewModeVisibility();
                Helpers.DebugLogger.Log("[MainWindow] Quad split view restored on startup");
                return;
            }

            if (ViewModel.RightExplorer.Columns.Count == 0
                || ViewModel.RightExplorer.CurrentPath == "PC"
                || string.IsNullOrEmpty(ViewModel.RightExplorer.CurrentPath))
            {
                _ = NavigateRightPaneToRealPathAsync();
            }

            if (ViewModel.RightViewMode == ViewMode.MillerColumns)
            {
                MillerScrollViewerRight.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (_isClosed || !ViewModel.IsSplitViewEnabled) return;
                        ScrollToLastColumn(ViewModel.RightExplorer, MillerScrollViewerRight, disableAnimation: true);
                        ApplyPersistedMillerColumnWidth(MillerColumnsControlRight);
                    });
            }

            SyncRightAddressBar();
            SubscribeRightExplorerForAddressBar();
            ViewModel.NotifySplitViewChanged();
            UpdateSplitViewButtonState();
            UpdateSplitOrientationButtonState();
            UpdateViewModeVisibility();

            Helpers.DebugLogger.Log("[MainWindow] Split view restored on startup");
        }

        private void ToggleSplitView()
        {
            if (ViewModel.IsRecycleBinTab) return;
            ViewModel.IsSplitViewEnabled = !ViewModel.IsSplitViewEnabled;

            if (ViewModel.IsSplitViewEnabled)
            {
                ApplySplitLayout();

                // Sync left pane breadcrumb — 비활성 상태에서 탭 전환 시 갱신 안 된 경우 보정
                if (ViewModel.Explorer?.PathSegments != null)
                {
                    LeftAddressBar.PathSegments = ViewModel.Explorer.PathSegments;
                    LeftAddressBar.CurrentPath = ViewModel.Explorer.CurrentPath;
                }

                if (ViewModel.IsQuadSplit)
                {
                    _ = InitializeQuadPanesAsync();
                }
                else
                {
                    // Initialize right pane based on Tab2 startup settings
                    if (ViewModel.RightExplorer.Columns.Count == 0 ||
                        ViewModel.RightExplorer.CurrentPath == "PC")
                    {
                        var tab2Behavior = _settings.Tab2StartupBehavior;
                        if (tab2Behavior == 0)
                        {
                            // Home: 우측 패인에 홈 화면 표시
                            ViewModel.RightViewMode = Models.ViewMode.Home;
                            Helpers.DebugLogger.Log("[ToggleSplitView] Right pane → Home view");
                        }
                        else if (tab2Behavior == 2 && !string.IsNullOrEmpty(_settings.Tab2StartupPath)
                            && System.IO.Directory.Exists(_settings.Tab2StartupPath))
                        {
                            // CustomPath: 사용자 지정 경로로 이동
                            _ = ViewModel.RightExplorer.NavigateToPath(_settings.Tab2StartupPath);
                            Helpers.DebugLogger.Log($"[ToggleSplitView] Right pane → custom path: {_settings.Tab2StartupPath}");
                        }
                        else
                        {
                            // RestoreSession (behavior=1) 또는 fallback: 저장된 경로 복원
                            NavigateRightPaneToRealPath();
                            Helpers.DebugLogger.Log("[ToggleSplitView] Right pane → restore session");
                        }
                    }

                    // RightExplorer 네비게이션 시 RightAddressBar 자동 동기화
                    SyncRightAddressBar();
                    SubscribeRightExplorerForAddressBar();
                }

                // Close ALL previews when entering split view (saves screen space)
                // 1) 사이드 패널 미리보기 비활성화
                ViewModel.IsLeftPreviewEnabled = false;
                LeftPreviewSplitterCol.Width = new GridLength(0);
                LeftPreviewCol.Width = new GridLength(0);
                LeftPreviewPanel.StopMedia();

                ViewModel.IsRightPreviewEnabled = false;
                RightPreviewSplitterCol.Width = new GridLength(0);
                RightPreviewCol.Width = new GridLength(0);
                RightPreviewPanel.StopMedia();

                // 2) 버튼 상태 동기화
                UpdatePreviewButtonState();

                // Set active pane to right and focus it after UI has updated
                ViewModel.ActivePane = ActivePane.Right;
                FocusActivePane();

                Helpers.DebugLogger.Log("[MainWindow] Split View enabled");
            }
            else
            {
                ApplySplitLayout();

                // Right preview panel 정리 — 분할뷰 해제 시 우측 미리보기가 남는 버그 방지
                ViewModel.IsRightPreviewEnabled = false;
                RightPreviewSplitterCol.Width = new GridLength(0);
                RightPreviewCol.Width = new GridLength(0);
                RightPreviewPanel.StopMedia();

                // Sync main address bar — Split 모드에서 갱신 안 된 경우 보정
                if (ViewModel.Explorer?.PathSegments != null)
                {
                    MainAddressBar.PathSegments = ViewModel.Explorer.PathSegments;
                    MainAddressBar.CurrentPath = ViewModel.Explorer.CurrentPath;
                }

                // RightExplorer 구독 해제
                UnsubscribeRightExplorerForAddressBar();
                UnsubscribeQuadPaneAddressBars();

                // 미리보기 상태 복원: 분할뷰 진입 시 비활성화했으므로 기본 설정값으로 복원
                try
                {
                    var settingsSvc = App.Current.Services.GetRequiredService<SettingsService>();
                    var previewDefault = settingsSvc.DefaultPreviewEnabled;

                    // 모든 뷰 모드 공통: 사이드 미리보기 패널 복원
                    ViewModel.IsLeftPreviewEnabled = previewDefault;
                    if (previewDefault)
                    {
                        LeftPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                        LeftPreviewCol.Width = new GridLength(GetSavedPreviewWidth("LeftPreviewWidth"), GridUnitType.Pixel);
                        SubscribePreviewToLastColumn(isLeft: true);
                    }
                    UpdatePreviewButtonState();
                    Helpers.DebugLogger.Log($"[MainWindow] Preview restored to default={previewDefault} after split view disabled");
                }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[MainWindow] Preview restore error: {ex.Message}");
                }

                // Reset active pane to left and focus it
                ViewModel.ActivePane = ActivePane.Left;
                FocusActivePane();

                Helpers.DebugLogger.Log("[MainWindow] Split View disabled");
            }
        }

        private void SyncRightAddressBar()
        {
            if (ViewModel.RightExplorer == null)
                return;

            var explorer = ViewModel.RightExplorer;
            RightAddressBar.CurrentPath = explorer.CurrentPath ?? string.Empty;

            // Snapshot segments so the breadcrumb always reflects RightExplorer (not a stale shared ref).
            var segments = new ObservableCollection<Models.PathSegment>(
                explorer.PathSegments.Select(s => new Models.PathSegment(s.Name, s.FullPath, s.IsLast)));
            RightAddressBar.PathSegments = segments;
            RightAddressBar.RefreshItemsSource();
        }

        private void SubscribeRightExplorerForAddressBar()
        {
            UnsubscribeRightExplorerForAddressBar();
            if (ViewModel.RightExplorer == null) return;

            _rightExplorerAddressBarHandler = (s, e) =>
            {
                if (e.PropertyName != nameof(ExplorerViewModel.CurrentPath))
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncRightAddressBar();
                    if (ViewModel.IsSplitViewEnabled)
                        ViewModel.PersistSplitViewState();
                });
            };
            ViewModel.RightExplorer.PropertyChanged += _rightExplorerAddressBarHandler;

            _rightExplorerPathSegmentsHandler = (_, _) =>
                DispatcherQueue.TryEnqueue(SyncRightAddressBar);
            ViewModel.RightExplorer.PathSegments.CollectionChanged += _rightExplorerPathSegmentsHandler;
        }

        private void UnsubscribeRightExplorerForAddressBar()
        {
            if (ViewModel.RightExplorer != null)
            {
                if (_rightExplorerAddressBarHandler != null)
                {
                    ViewModel.RightExplorer.PropertyChanged -= _rightExplorerAddressBarHandler;
                    _rightExplorerAddressBarHandler = null;
                }

                if (_rightExplorerPathSegmentsHandler != null)
                {
                    ViewModel.RightExplorer.PathSegments.CollectionChanged -= _rightExplorerPathSegmentsHandler;
                    _rightExplorerPathSegmentsHandler = null;
                }
            }
        }

        /// <summary>
        /// Navigate the right pane to a real filesystem path (saved path, first drive, or user profile).
        /// </summary>
        private async Task NavigateRightPaneToRealPathAsync()
        {
            var path = ViewModel.GetRightPaneInitialPath();
            await ViewModel.RightExplorer.NavigateToPath(path);
            SyncRightAddressBar();
            Helpers.DebugLogger.Log($"[MainWindow] Right pane navigated to: {path}");
        }

        private void NavigateRightPaneToRealPath()
        {
            _ = NavigateRightPaneToRealPathAsync();
        }

        #region Quad Split Pane Management

        private PropertyChangedEventHandler? _topRightExplorerAddressBarHandler;
        private NotifyCollectionChangedEventHandler? _topRightExplorerPathSegmentsHandler;
        private PropertyChangedEventHandler? _bottomRightExplorerAddressBarHandler;
        private NotifyCollectionChangedEventHandler? _bottomRightExplorerPathSegmentsHandler;
        private bool _quadMillerTemplatesInitialized;
        private bool _quadColumnHandlersSubscribed;

        private ExplorerViewModel GetExplorerForPaneTag(string? tag) => tag switch
        {
            "Left" => ViewModel.LeftExplorer,
            "Right" => ViewModel.RightExplorer,
            "TopRight" => ViewModel.TopRightExplorer,
            "BottomRight" => ViewModel.BottomRightExplorer,
            _ => ViewModel.ActiveExplorer,
        };

        private ActivePane? GetActivePaneForElement(DependencyObject element)
        {
            if (element == null) return null;
            if (IsDescendant(TopRightPaneContainer, element)) return ActivePane.TopRight;
            if (IsDescendant(BottomRightPaneContainer, element)) return ActivePane.BottomRight;
            if (IsDescendant(RightPaneContainer, element)) return ActivePane.Right;
            if (IsDescendant(LeftPaneContainer, element)) return ActivePane.Left;
            return null;
        }

        private ExplorerViewModel GetExplorerForMillerElement(DependencyObject element)
        {
            if (GetActivePaneForElement(element) is ActivePane pane)
            {
                ViewModel.ActivePane = pane;
                return ViewModel.GetExplorerForPane(pane);
            }

            return ViewModel.LeftExplorer;
        }

        private void SetQuadPanesVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            TopRightPaneContainer.Visibility = v;
            BottomRightPaneContainer.Visibility = v;
            SplitPaneDividerHorizontal.Visibility = v;
        }

        /// <summary>
        /// Quad: all 4 panes show the same explorer view mode (Miller/Details/List/Icon).
        /// </summary>
        private void ApplyQuadSharedViewVisibility()
        {
            var mode = ViewModel.CurrentViewMode;
            // Special modes are left-only; keep explorer panes on last usable mode.
            // Do NOT assign CurrentViewMode here — mutating it during tab switch / layout
            // would overwrite the active tab's saved ViewMode and bleed into other tabs.
            if (mode is Models.ViewMode.Home or Models.ViewMode.Settings
                or Models.ViewMode.ActionLog or Models.ViewMode.RecycleBin)
            {
                mode = Models.ViewMode.MillerColumns;
            }

            if (ViewModel.LeftViewMode != mode)
                ViewModel.LeftViewMode = mode;
            if (ViewModel.RightViewMode != mode)
                ViewModel.RightViewMode = mode;
            ViewModel.SyncExplorerAutoNavigationForLayout();

            bool miller = mode == Models.ViewMode.MillerColumns;
            bool details = mode == Models.ViewMode.Details;
            bool list = mode == Models.ViewMode.List;
            bool icon = Helpers.ViewModeExtensions.IsIconMode(mode);

            MillerTabsHost.Visibility = miller ? Visibility.Visible : Visibility.Collapsed;
            DetailsTabsHost.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
            ListTabsHost.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            IconTabsHost.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;
            HomeView.Visibility = Visibility.Collapsed;

            LeftPreviewSplitterCol.Width = new GridLength(0);
            LeftPreviewCol.Width = new GridLength(0);
            LeftPreviewPanel.Visibility = Visibility.Collapsed;

            MillerScrollViewerRight.Visibility = miller ? Visibility.Visible : Visibility.Collapsed;
            HomeViewRight.Visibility = Visibility.Collapsed;
            DetailsViewRight.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
            ListViewRight.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            IconViewRight.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;

            MillerScrollViewerTopRight.Visibility = miller ? Visibility.Visible : Visibility.Collapsed;
            DetailsViewTopRight.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
            ListViewTopRight.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            IconViewTopRight.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;

            MillerScrollViewerBottomRight.Visibility = miller ? Visibility.Visible : Visibility.Collapsed;
            DetailsViewBottomRight.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
            ListViewBottomRight.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            IconViewBottomRight.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;

            RightPreviewSplitterCol.Width = new GridLength(0);
            RightPreviewCol.Width = new GridLength(0);
            RightPreviewPanel.Visibility = Visibility.Collapsed;
            RightGitStatusBar.Visibility = Visibility.Collapsed;
            LeftGitStatusBar.Visibility = Visibility.Collapsed;
        }

        private void InitializeQuadMillerTemplates()
        {
            if (_quadMillerTemplatesInitialized)
                return;

            var template = MillerColumnsControlRight.ItemTemplate;
            var itemsPanel = MillerColumnsControlRight.ItemsPanel;
            if (template != null)
            {
                MillerColumnsControlTopRight.ItemTemplate = template;
                MillerColumnsControlBottomRight.ItemTemplate = template;
            }
            if (itemsPanel != null)
            {
                MillerColumnsControlTopRight.ItemsPanel = itemsPanel;
                MillerColumnsControlBottomRight.ItemsPanel = itemsPanel;
            }

            MillerColumnsControlTopRight.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(OnMillerKeyDown),
                true);
            MillerColumnsControlBottomRight.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(OnMillerKeyDown),
                true);
            MillerColumnsControlTopRight.AddHandler(
                UIElement.CharacterReceivedEvent,
                new Windows.Foundation.TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnMillerCharacterReceived),
                true);
            MillerColumnsControlBottomRight.AddHandler(
                UIElement.CharacterReceivedEvent,
                new Windows.Foundation.TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnMillerCharacterReceived),
                true);

            MillerScrollViewerTopRight.SizeChanged += OnQuadMillerScrollViewerSizeChanged;
            MillerScrollViewerBottomRight.SizeChanged += OnQuadMillerScrollViewerSizeChanged;

            _quadMillerTemplatesInitialized = true;
        }

        private void SubscribeQuadExplorerColumnHandlers()
        {
            if (_quadColumnHandlersSubscribed)
                return;

            ViewModel.TopRightExplorer.Columns.CollectionChanged += OnTopRightColumnsChanged;
            ViewModel.BottomRightExplorer.Columns.CollectionChanged += OnBottomRightColumnsChanged;
            _quadColumnHandlersSubscribed = true;
        }

        private async Task InitializeQuadPanesAsync()
        {
            SubscribeQuadExplorerColumnHandlers();
            InitializeQuadMillerTemplates();

            if (ViewModel.CurrentViewMode == Models.ViewMode.Home)
                ViewModel.SwitchViewMode(ViewModel.ResolveViewModeFromHome());

            await NavigateQuadPaneIfNeededAsync(ViewModel.LeftExplorer, ActivePane.Left);
            await NavigateQuadPaneIfNeededAsync(ViewModel.RightExplorer, ActivePane.Right);
            await NavigateQuadPaneIfNeededAsync(ViewModel.TopRightExplorer, ActivePane.TopRight);
            await NavigateQuadPaneIfNeededAsync(ViewModel.BottomRightExplorer, ActivePane.BottomRight);

            SyncAllSecondaryAddressBars();
            SubscribeRightExplorerForAddressBar();
            SubscribeQuadPaneAddressBars();
            RelayoutQuadMillerScroll();
        }

        private void RelayoutQuadMillerScroll()
        {
            try { MillerColumnSpacerLeft.Width = 0; } catch { }
            try { MillerColumnSpacerRight.Width = 0; } catch { }

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || !ViewModel.IsQuadSplit) return;
                RelayoutQuadMillerScrollCore();
            });
        }

        private void RelayoutQuadMillerScrollCore()
        {
            ExplorerViewModel[] explorers =
            {
                ViewModel.LeftExplorer,
                ViewModel.RightExplorer,
                ViewModel.TopRightExplorer,
                ViewModel.BottomRightExplorer,
            };

            foreach (var explorer in explorers)
            {
                var sv = GetMillerScrollViewerForExplorer(explorer);
                ResetMillerHorizontalScroll(sv);
            }

            ApplyPersistedMillerColumnWidth(GetLeftMillerColumnsControl());
            ApplyPersistedMillerColumnWidth(MillerColumnsControlRight);
            ApplyPersistedMillerColumnWidth(MillerColumnsControlTopRight);
            ApplyPersistedMillerColumnWidth(MillerColumnsControlBottomRight);

            foreach (var explorer in explorers)
            {
                if (explorer.Columns.Count == 0) continue;
                var sv = GetMillerScrollViewerForExplorer(explorer);
                if (sv == null) continue;
                ScrollToLastColumnSync(explorer, sv, disableAnimation: true);
            }

            // Second pass after quad pane layout has measured viewports.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || !ViewModel.IsQuadSplit) return;
                foreach (var explorer in explorers)
                {
                    if (explorer.Columns.Count == 0) continue;
                    var sv = GetMillerScrollViewerForExplorer(explorer);
                    if (sv == null) continue;
                    ScrollToLastColumnSync(explorer, sv, disableAnimation: true);
                }
            });
        }

        private static void ResetMillerHorizontalScroll(ScrollViewer? sv)
        {
            if (sv == null) return;
            try { sv.ChangeView(0, null, null, disableAnimation: true); } catch { }
        }

        private void ResetMillerSpacerForExplorer(ExplorerViewModel explorer)
        {
            try
            {
                if (ReferenceEquals(explorer, ViewModel.LeftExplorer))
                    MillerColumnSpacerLeft.Width = 0;
                else if (ReferenceEquals(explorer, ViewModel.RightExplorer))
                    MillerColumnSpacerRight.Width = 0;
            }
            catch { }
        }

        private void OnQuadMillerScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!ViewModel.IsQuadSplit || sender is not ScrollViewer sv) return;
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 1) return;

            ExplorerViewModel? explorer = ReferenceEquals(sv, MillerScrollViewerTopRight)
                ? ViewModel.TopRightExplorer
                : ReferenceEquals(sv, MillerScrollViewerBottomRight)
                    ? ViewModel.BottomRightExplorer
                    : null;
            if (explorer == null) return;

            ScrollToLastColumnSync(explorer, sv, disableAnimation: true);
        }

        private async Task NavigateQuadPaneIfNeededAsync(ExplorerViewModel explorer, ActivePane pane)
        {
            if (explorer.Columns.Count > 0
                && explorer.CurrentPath != "PC"
                && !string.IsNullOrEmpty(explorer.CurrentPath))
                return;

            var path = ViewModel.GetPaneInitialPath(pane);
            await explorer.NavigateToPath(path);
        }

        private void SyncAllSecondaryAddressBars()
        {
            SyncRightAddressBar();
            SyncQuadAddressBar(ViewModel.TopRightExplorer, TopRightAddressBar);
            SyncQuadAddressBar(ViewModel.BottomRightExplorer, BottomRightAddressBar);
        }

        private static void SyncQuadAddressBar(ExplorerViewModel? explorer, Controls.AddressBarControl bar)
        {
            if (explorer == null) return;

            bar.CurrentPath = explorer.CurrentPath ?? string.Empty;
            var segments = new ObservableCollection<Models.PathSegment>(
                explorer.PathSegments.Select(s => new Models.PathSegment(s.Name, s.FullPath, s.IsLast)));
            bar.PathSegments = segments;
            bar.RefreshItemsSource();
        }

        private void SubscribeQuadPaneAddressBars()
        {
            SubscribeExplorerForAddressBar(
                ViewModel.TopRightExplorer,
                SyncTopRightAddressBar,
                ref _topRightExplorerAddressBarHandler,
                ref _topRightExplorerPathSegmentsHandler);
            SubscribeExplorerForAddressBar(
                ViewModel.BottomRightExplorer,
                SyncBottomRightAddressBar,
                ref _bottomRightExplorerAddressBarHandler,
                ref _bottomRightExplorerPathSegmentsHandler);
        }

        private void UnsubscribeQuadPaneAddressBars()
        {
            UnsubscribeExplorerForAddressBar(
                ViewModel.TopRightExplorer,
                ref _topRightExplorerAddressBarHandler,
                ref _topRightExplorerPathSegmentsHandler);
            UnsubscribeExplorerForAddressBar(
                ViewModel.BottomRightExplorer,
                ref _bottomRightExplorerAddressBarHandler,
                ref _bottomRightExplorerPathSegmentsHandler);
        }

        private void SyncTopRightAddressBar() =>
            SyncQuadAddressBar(ViewModel.TopRightExplorer, TopRightAddressBar);

        private void SyncBottomRightAddressBar() =>
            SyncQuadAddressBar(ViewModel.BottomRightExplorer, BottomRightAddressBar);

        private void SubscribeExplorerForAddressBar(
            ExplorerViewModel? explorer,
            Action syncAction,
            ref PropertyChangedEventHandler? propertyHandler,
            ref NotifyCollectionChangedEventHandler? segmentsHandler)
        {
            UnsubscribeExplorerForAddressBar(explorer, ref propertyHandler, ref segmentsHandler);
            if (explorer == null) return;

            propertyHandler = (_, e) =>
            {
                if (e.PropertyName != nameof(ExplorerViewModel.CurrentPath))
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    syncAction();
                    if (ViewModel.IsSplitViewEnabled)
                        ViewModel.PersistSplitViewState();
                });
            };
            explorer.PropertyChanged += propertyHandler;

            segmentsHandler = (_, _) => DispatcherQueue.TryEnqueue(() => syncAction());
            explorer.PathSegments.CollectionChanged += segmentsHandler;
        }

        private void UnsubscribeExplorerForAddressBar(
            ExplorerViewModel? explorer,
            ref PropertyChangedEventHandler? propertyHandler,
            ref NotifyCollectionChangedEventHandler? segmentsHandler)
        {
            if (explorer == null) return;

            if (propertyHandler != null)
            {
                explorer.PropertyChanged -= propertyHandler;
                propertyHandler = null;
            }

            if (segmentsHandler != null)
            {
                explorer.PathSegments.CollectionChanged -= segmentsHandler;
                segmentsHandler = null;
            }
        }

        private void OnTopRightColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => OnQuadSecondaryColumnsChanged(ViewModel.TopRightExplorer, MillerScrollViewerTopRight, MillerColumnsControlTopRight, e);

        private void OnBottomRightColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => OnQuadSecondaryColumnsChanged(ViewModel.BottomRightExplorer, MillerScrollViewerBottomRight, MillerColumnsControlBottomRight, e);

        private void OnQuadSecondaryColumnsChanged(
            ExplorerViewModel explorer,
            ScrollViewer scrollViewer,
            ItemsControl columnsControl,
            NotifyCollectionChangedEventArgs e)
        {
            // TopRight/BottomRight 컬럼 변경 시 Watcher 경로 갱신 (Left-only 갱신 버그 수정).
            UpdateFileSystemWatcherPaths();

            if (!ViewModel.IsQuadSplit) return;

            bool isReplacing = explorer.IsReplacingLastColumn;

            if (e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Replace)
            {
                if (!isReplacing)
                    ScrollToLastColumn(explorer, scrollViewer);

                if (_millerSelectionMode != ListViewSelectionMode.Extended)
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => ApplyCheckboxToItemsControl(columnsControl, _millerSelectionMode));
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Add &&
                explorer.Columns.Count > 1 &&
                !isReplacing)
            {
                PrepareAndAnimateNewColumn(columnsControl);
            }

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ApplyPersistedMillerColumnWidth(columnsControl);
                SyncAllSecondaryAddressBars();
            });
        }

        #endregion

        #endregion

        #region Pane Navigation / Copy Path

        /// <summary>
        /// Per-pane navigate up button click.
        /// </summary>
        private void OnPaneNavigateUpClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
                GetExplorerForPaneTag(btn.Tag as string).NavigateUp();
        }

        /// <summary>
        /// Per-pane copy path button click.
        /// </summary>
        private void OnPaneCopyPathClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                var explorer = GetExplorerForPaneTag(btn.Tag as string);
                var path = explorer.CurrentPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var dataPackage = new DataPackage();
                    dataPackage.SetText(path);
                    Clipboard.SetContent(dataPackage);
                    ViewModel.ShowToast(_loc.Get("Toast_PathCopied"), 2000);
                }
            }
        }

        /// <summary>
        /// Per-pane New menu (folder + Shell New types), like OneCommander — works without
        /// needing an empty-area right-click target.
        /// </summary>
        private void OnPaneNewItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            var tag = (fe as Button)?.Tag as string ?? fe.Tag as string;
            ViewModel.ActivePane = tag switch
            {
                "Left" => ActivePane.Left,
                "Right" => ActivePane.Right,
                "TopRight" => ActivePane.TopRight,
                "BottomRight" => ActivePane.BottomRight,
                _ => ViewModel.ActivePane
            };

            var explorer = GetExplorerForPaneTag(tag);
            var folderPath = GetColumnPathForExplorer(explorer);
            if (string.IsNullOrEmpty(folderPath) || folderPath == "PC")
            {
                ViewModel.ShowToast(_loc.Get("Toast_CannotCreateHere"), 2000);
                return;
            }

            var menu = _contextMenuService.BuildNewItemMenu(folderPath, this);
            menu.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
            });
        }

        /// <summary>
        /// Target folder for New in a pane: active Miller column, else last column / CurrentPath.
        /// </summary>
        private string? GetColumnPathForExplorer(ExplorerViewModel explorer)
        {
            if (explorer?.Columns == null || explorer.Columns.Count == 0)
                return explorer?.CurrentPath;

            var active = explorer.Columns.FirstOrDefault(c => c.IsActive);
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return active.Path;

            return explorer.Columns[^1].Path;
        }

        #endregion

        #region Focus Active Pane

        /// <summary>
        /// Focus the active pane's content (used after pane switch or split toggle).
        /// Handles all view modes and retries if columns haven't loaded yet.
        /// </summary>
        private void FocusActivePane(int retryCount = 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || ViewModel == null) return;

                var viewMode = GetActivePaneViewMode();

                switch (viewMode)
                {
                    case Models.ViewMode.MillerColumns:
                        var columns = ViewModel.ActiveExplorer.Columns;
                        if (columns.Count > 0)
                        {
                            // autoSelect: false — 패인 전환 시 자동 선택 억제.
                            // 패인 클릭으로 포커스만 이동하고, 첫 항목을 자동 선택하지 않음.
                            // 이를 통해 좌/우클릭 번갈아 시 컬럼이 연쇄 생성되는 버그 방지.
                            FocusColumnAsync(columns.Count - 1, autoSelect: false);
                        }
                        else if (retryCount < 3)
                        {
                            // Columns may still be loading after NavigateRightPaneToRealPath
                            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                                () => FocusActivePane(retryCount + 1));
                        }
                        break;

                    case Models.ViewMode.Details:
                        GetActiveDetailsView()?.FocusListView();
                        break;

                    case Models.ViewMode.List:
                        GetActiveListView()?.FocusGridView();
                        break;

                    case Models.ViewMode.IconSmall:
                    case Models.ViewMode.IconMedium:
                    case Models.ViewMode.IconLarge:
                    case Models.ViewMode.IconExtraLarge:
                        GetActiveIconView()?.FocusGridView();
                        break;
                }
            });
        }

        #endregion

        // =================================================================
        //  Preview Panel
        // =================================================================

        #region Preview Panel

        /// <summary>
        /// x:Bind visibility helper for preview panel.
        /// </summary>
        public Visibility PreviewVisible(bool isPreviewEnabled)
            => isPreviewEnabled ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Initialize preview panels with ViewModels from DI.
        /// </summary>
        private void InitializePreviewPanels()
        {
            var previewService = App.Current.Services.GetRequiredService<PreviewService>();

            var leftVm = new PreviewPanelViewModel(previewService);
            LeftPreviewPanel.Initialize(leftVm);

            var rightVm = new PreviewPanelViewModel(previewService);
            RightPreviewPanel.Initialize(rightVm);

            // Defensive unsubscribe before subscribe to prevent handler accumulation
            ViewModel.LeftExplorer.Columns.CollectionChanged -= OnLeftColumnsChangedForPreview;
            ViewModel.RightExplorer.Columns.CollectionChanged -= OnRightColumnsChangedForPreview;
            ViewModel.PropertyChanged -= OnViewModelPropertyChangedForPreview;

            // Subscribe to LeftExplorer column changes for preview updates
            ViewModel.LeftExplorer.Columns.CollectionChanged += OnLeftColumnsChangedForPreview;
            ViewModel.RightExplorer.Columns.CollectionChanged += OnRightColumnsChangedForPreview;

            // Subscribe to ViewModel property changes for preview state
            ViewModel.PropertyChanged += OnViewModelPropertyChangedForPreview;

            // Initialize Git status bars
            InitializeGitStatusBars();
        }

        /// <summary>
        /// When columns change, subscribe to the last column's SelectedChild for preview.
        /// </summary>
        private void OnLeftColumnsChangedForPreview(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isClosed) return;
            if (!ViewModel.IsLeftPreviewEnabled) return;
            SubscribePreviewToLastColumn(isLeft: true);
        }

        private void OnRightColumnsChangedForPreview(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isClosed) return;
            bool needSubscribe = ViewModel.IsRightPreviewEnabled;
            if (!needSubscribe) return;
            SubscribePreviewToLastColumn(isLeft: false);
        }

        /// <summary>
        /// Subscribe to the last column's SelectedChild property changes to auto-update preview.
        /// </summary>
        private void SubscribePreviewToLastColumn(bool isLeft)
        {
            var explorer = isLeft ? ViewModel.LeftExplorer : ViewModel.RightExplorer;
            var columns = explorer.Columns;

            UnsubscribePreviewSelection(isLeft);

            if (columns.Count == 0) return;

            var lastColumn = columns[columns.Count - 1];
            lastColumn.PropertyChanged += isLeft ? OnLeftColumnSelectionForPreview : OnRightColumnSelectionForPreview;

            if (isLeft) _leftPreviewSubscribedColumn = lastColumn;
            else _rightPreviewSubscribedColumn = lastColumn;

            // Immediately update preview with current selection.
            var selectedChild = lastColumn.SelectedChild;
            var previewPanel = isLeft ? LeftPreviewPanel : RightPreviewPanel;
            var targetViewMode = isLeft ? ViewModel.CurrentViewMode : ViewModel.RightViewMode;

            if (targetViewMode == Models.ViewMode.MillerColumns)
            {
                // Miller: 파일 선택 시만 패널 표시, 그 외(폴더/미선택) → 패널 자체 숨김
                bool showPanel = selectedChild is FileViewModel;
                SetMillerPreviewPanelVisible(isLeft, showPanel);
                previewPanel.UpdatePreview(showPanel ? FilterPreviewItem(selectedChild) : null);
            }
            else
            {
                // Details/List/Icon: 기존 동작
                var previewItem = selectedChild != null
                    ? FilterPreviewItem(selectedChild)
                    : FilterPreviewItem(lastColumn);
                previewPanel.UpdatePreview(previewItem);
            }
        }

        /// <summary>
        /// 미리보기 대상 항목 필터: PreviewShowFolderInfo 설정에 따라 폴더 항목을 null로 변환.
        /// </summary>
        private FileSystemViewModel? FilterPreviewItem(FileSystemViewModel? item)
        {
            if (item is FolderViewModel)
            {
                try
                {
                    var settings = App.Current.Services.GetRequiredService<SettingsService>();
                    if (!settings.PreviewShowFolderInfo) return null;
                }
                catch { return null; }
            }
            return item;
        }

        /// <summary>
        /// Miller 모드 전용: 파일 선택 여부에 따라 사이드 미리보기 패널 Width를 표시/숨김.
        /// IsLeftPreviewEnabled 상태는 변경하지 않음 (토글 버튼 유지).
        /// </summary>
        private void SetMillerPreviewPanelVisible(bool isLeft, bool visible)
        {
            if (isLeft)
            {
                if (visible)
                {
                    if (LeftPreviewCol.Width.Value < 1)
                    {
                        LeftPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                        LeftPreviewCol.Width = new GridLength(GetSavedPreviewWidth("LeftPreviewWidth"), GridUnitType.Pixel);
                    }
                }
                else
                {
                    LeftPreviewSplitterCol.Width = new GridLength(0);
                    LeftPreviewCol.Width = new GridLength(0);
                    LeftPreviewPanel.StopMedia();
                }
            }
            else
            {
                if (visible)
                {
                    if (RightPreviewCol.Width.Value < 1)
                    {
                        RightPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                        RightPreviewCol.Width = new GridLength(GetSavedPreviewWidth("RightPreviewWidth"), GridUnitType.Pixel);
                    }
                }
                else
                {
                    RightPreviewSplitterCol.Width = new GridLength(0);
                    RightPreviewCol.Width = new GridLength(0);
                    RightPreviewPanel.StopMedia();
                }
            }
        }

        private void UnsubscribePreviewSelection(bool isLeft)
        {
            if (isLeft && _leftPreviewSubscribedColumn != null)
            {
                _leftPreviewSubscribedColumn.PropertyChanged -= OnLeftColumnSelectionForPreview;
                _leftPreviewSubscribedColumn = null;
            }
            else if (!isLeft && _rightPreviewSubscribedColumn != null)
            {
                _rightPreviewSubscribedColumn.PropertyChanged -= OnRightColumnSelectionForPreview;
                _rightPreviewSubscribedColumn = null;
            }
        }

        private void OnLeftColumnSelectionForPreview(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FolderViewModel.SelectedChild)) return;
            if (_isClosed) return;

            if (sender is FolderViewModel folder)
            {
                if (ViewModel.IsLeftPreviewEnabled)
                {
                    var child = folder.SelectedChild;
                    bool isMiller = ViewModel.CurrentViewMode == Models.ViewMode.MillerColumns;

                    if (isMiller)
                    {
                        // Miller: 파일 선택 시만 패널 표시, 폴더/미선택 → 패널 숨김
                        bool showPanel = child is FileViewModel;
                        SetMillerPreviewPanelVisible(isLeft: true, showPanel);
                        LeftPreviewPanel.UpdatePreview(showPanel ? FilterPreviewItem(child) : null);
                    }
                    else
                    {
                        var item = child != null ? FilterPreviewItem(child) : FilterPreviewItem(folder);
                        LeftPreviewPanel.UpdatePreview(item);
                    }
                }

                // Quick Look 윈도우가 열려 있으면 내용 업데이트
                if (ViewModel.ActivePane == ActivePane.Left)
                    UpdateQuickLookContent(folder.SelectedChild);
            }
        }

        private void OnRightColumnSelectionForPreview(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FolderViewModel.SelectedChild)) return;
            if (_isClosed) return;

            if (sender is FolderViewModel folder)
            {
                if (ViewModel.IsRightPreviewEnabled)
                {
                    var child = folder.SelectedChild;
                    bool isMiller = ViewModel.RightViewMode == Models.ViewMode.MillerColumns;

                    if (isMiller)
                    {
                        bool showPanel = child is FileViewModel;
                        SetMillerPreviewPanelVisible(isLeft: false, showPanel);
                        RightPreviewPanel.UpdatePreview(showPanel ? FilterPreviewItem(child) : null);
                    }
                    else
                    {
                        var item = child != null ? FilterPreviewItem(child) : FilterPreviewItem(folder);
                        RightPreviewPanel.UpdatePreview(item);
                    }
                }

                // Quick Look 윈도우가 열려 있으면 내용 업데이트
                if (ViewModel.ActivePane == ActivePane.Right)
                    UpdateQuickLookContent(folder.SelectedChild);
            }
        }

        /// <summary>
        /// Update preview when selection changes in Details/Icon mode (via Miller column selection handler).
        /// </summary>
        private void UpdatePreviewForSelection(FileSystemViewModel? selectedItem)
        {
            if (_isClosed) return;

            var filtered = FilterPreviewItem(selectedItem);
            if (ViewModel.ActivePane == ActivePane.Left && ViewModel.IsLeftPreviewEnabled)
                LeftPreviewPanel.UpdatePreview(filtered);
            else if (ViewModel.ActivePane == ActivePane.Right && ViewModel.IsRightPreviewEnabled)
                RightPreviewPanel.UpdatePreview(filtered);

            // Quick Look 윈도우가 열려 있으면 내용 업데이트
            UpdateQuickLookContent(selectedItem);
        }

        /// <summary>
        /// React to preview enable/disable changes to wire/unwire subscriptions.
        /// </summary>
        private void OnViewModelPropertyChangedForPreview(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsLeftPreviewEnabled))
            {
                if (ViewModel.IsLeftPreviewEnabled)
                    SubscribePreviewToLastColumn(isLeft: true);
                else
                {
                    UnsubscribePreviewSelection(isLeft: true);
                    LeftPreviewPanel.UpdatePreview(null);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.IsRightPreviewEnabled))
            {
                if (ViewModel.IsRightPreviewEnabled)
                    SubscribePreviewToLastColumn(isLeft: false);
                else
                {
                    UnsubscribePreviewSelection(isLeft: false);
                    RightPreviewPanel.UpdatePreview(null);
                }
            }
        }

        private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
        {
            TogglePreviewPanel();
            UpdatePreviewButtonState();
        }

        private void TogglePreviewPanel()
        {
            TogglePreviewForPane(ViewModel.ActivePane);
        }

        /// <summary>
        /// 지정된 패인의 미리보기를 토글.
        /// ActivePane을 건드리지 않고 대상 프로퍼티를 직접 토글하여 경합 제거.
        /// </summary>
        private void TogglePreviewForPane(ActivePane targetPane)
        {
            if (ViewModel.IsRecycleBinTab) return;
            // 모든 뷰 모드 공통: 사이드 미리보기 패널 토글
            if (targetPane == ActivePane.Left)
            {
                ViewModel.IsLeftPreviewEnabled = !ViewModel.IsLeftPreviewEnabled;
                if (ViewModel.IsLeftPreviewEnabled)
                {
                    LeftPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    LeftPreviewCol.Width = new GridLength(GetSavedPreviewWidth("LeftPreviewWidth"), GridUnitType.Pixel);
                }
                else
                {
                    LeftPreviewSplitterCol.Width = new GridLength(0);
                    LeftPreviewCol.Width = new GridLength(0);
                    LeftPreviewPanel.StopMedia();
                }
            }
            else
            {
                ViewModel.IsRightPreviewEnabled = !ViewModel.IsRightPreviewEnabled;
                if (ViewModel.IsRightPreviewEnabled)
                {
                    RightPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    RightPreviewCol.Width = new GridLength(GetSavedPreviewWidth("RightPreviewWidth"), GridUnitType.Pixel);
                }
                else
                {
                    RightPreviewSplitterCol.Width = new GridLength(0);
                    RightPreviewCol.Width = new GridLength(0);
                    RightPreviewPanel.StopMedia();
                }
            }
            ViewModel.SavePreviewState();

            Helpers.DebugLogger.Log($"[MainWindow] Preview toggled (pane={targetPane}): Left={ViewModel.IsLeftPreviewEnabled}, Right={ViewModel.IsRightPreviewEnabled}");

            // After preview toggle, the Miller columns viewport width changes.
            // Scroll to keep the last column visible.
            var explorer = ViewModel.ActiveExplorer;
            if (explorer != null && explorer.Columns.Count > 0)
            {
                var scrollViewer = GetActiveMillerScrollViewer();
                ScrollToLastColumn(explorer, scrollViewer);
            }
        }

        /// <summary>
        /// 미리보기 토글 버튼의 활성 상태를 시각적으로 업데이트.
        /// Miller Columns 모드: 인라인 미리보기 설정 기반
        /// Details/List/Icon 모드: 사이드 패널 활성화 상태 기반
        /// </summary>
        internal void UpdatePreviewButtonState()
        {
            try
            {
                var accentBrush = GetThemeBrush("SpanAccentBrush");
                var defaultBrush = GetThemeBrush("SpanTextSecondaryBrush");

                // 상단 미리보기 버튼 (비분할 모드용)
                if (!ViewModel.IsSplitViewEnabled)
                {
                    bool isActive = ViewModel.IsLeftPreviewEnabled;
                    PreviewToggleIcon.Foreground = isActive ? accentBrush : defaultBrush;
                }

                // Split view pane-specific buttons
                if (ViewModel.IsSplitViewEnabled)
                {
                    bool leftActive = ViewModel.IsLeftPreviewEnabled;
                    bool rightActive = ViewModel.IsRightPreviewEnabled;

                    LeftPreviewIcon.Foreground = leftActive ? accentBrush : defaultBrush;
                    RightPreviewIcon.Foreground = rightActive ? accentBrush : defaultBrush;
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] UpdatePreviewButtonState error: {ex.Message}");
            }
        }

        /// <summary>
        /// 뷰모드 버튼 아이콘을 현재 활성 뷰모드에 맞게 업데이트.
        /// </summary>
        internal void UpdateViewModeIcon()
        {
            try
            {
                var mode = ViewModel.CurrentViewMode;
                string glyph = GetViewModeGlyph(mode);

                ViewModeIcon.Glyph = glyph;

                // Split view pane-specific buttons
                if (ViewModel.IsSplitViewEnabled)
                {
                    LeftViewModeIcon.Glyph = GetViewModeGlyph(ViewModel.LeftViewMode);
                    RightViewModeIcon.Glyph = GetViewModeGlyph(ViewModel.RightViewMode);
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] UpdateViewModeIcon error: {ex.Message}");
            }
        }

        private static string GetViewModeGlyph(Models.ViewMode mode) => mode switch
        {
            Models.ViewMode.MillerColumns => "\uF0E2",
            Models.ViewMode.Details => "\uE8EF",
            Models.ViewMode.List => "\uE80A",
            _ when mode >= Models.ViewMode.IconSmall && mode <= Models.ViewMode.IconExtraLarge => "\uE91B",
            _ => "\uF0E2"
        };

        internal void UpdateSplitViewButtonState()
        {
            try
            {
                var accentBrush = GetThemeBrush("SpanAccentBrush");
                var defaultBrush = GetThemeBrush("SpanTextSecondaryBrush");

                SplitViewIcon.Foreground = ViewModel.IsSplitViewEnabled ? accentBrush : defaultBrush;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] UpdateSplitViewButtonState error: {ex.Message}");
            }
        }

        internal void UpdateSplitOrientationButtonState()
        {
            try
            {
                var accentBrush = GetThemeBrush("SpanAccentBrush");
                var defaultBrush = GetThemeBrush("SpanTextSecondaryBrush");

                SplitOrientationIcon.Foreground = ViewModel.IsSplitViewEnabled ? accentBrush : defaultBrush;

                var stackedGlyph = (string)Application.Current.Resources["Icon_SplitOrientationStacked"];
                var sideBySideGlyph = (string)Application.Current.Resources["Icon_SplitOrientationSideBySide"];
                var quadGlyph = (string)Application.Current.Resources["Icon_SplitOrientationQuad"];
                SplitOrientationIcon.Glyph = ViewModel.SplitLayoutMode switch
                {
                    SplitLayoutMode.DualSideBySide => stackedGlyph,
                    SplitLayoutMode.DualStacked => quadGlyph,
                    SplitLayoutMode.Quad => sideBySideGlyph,
                    _ => stackedGlyph,
                };

                UpdateSplitOrientationTooltip();
                UpdateQuadSplitButtonState();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] UpdateSplitOrientationButtonState error: {ex.Message}");
            }
        }

        private void UpdateSplitOrientationTooltip()
        {
            if (SplitOrientationButton == null)
                return;

            var key = ViewModel.SplitLayoutMode switch
            {
                SplitLayoutMode.DualSideBySide => "Tooltip_SplitOrientationStacked",
                SplitLayoutMode.DualStacked => "Tooltip_SplitOrientationQuad",
                SplitLayoutMode.Quad => "Tooltip_SplitOrientationSideBySide",
                _ => "Tooltip_SplitOrientationStacked",
            };
            ToolTipService.SetToolTip(SplitOrientationButton, _loc.Get(key));
        }

        internal void UpdateQuadSplitButtonState()
        {
            try
            {
                if (QuadSplitIcon == null) return;
                var accentBrush = GetThemeBrush("SpanAccentBrush");
                var defaultBrush = GetThemeBrush("SpanTextSecondaryBrush");
                QuadSplitIcon.Foreground = ViewModel.IsQuadSplit ? accentBrush : defaultBrush;
                if (QuadSplitButton != null)
                    ToolTipService.SetToolTip(QuadSplitButton, _loc.Get("Tooltip_SplitOrientationQuad"));
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] UpdateQuadSplitButtonState error: {ex.Message}");
            }
        }

        /// <summary>
        /// 우측 패인의 뷰모드 변경 시 프리뷰 패널 너비를 동기화.
        /// Miller → Details/List/Icon 전환 시 프리뷰 활성화 상태에 맞게 너비 조정.
        /// </summary>
        internal void SyncRightPreviewPanelWidth()
        {
            try
            {
                if (!ViewModel.IsSplitViewEnabled) return;

                if (ViewModel.IsRightPreviewEnabled)
                {
                    RightPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    RightPreviewCol.Width = new GridLength(GetSavedPreviewWidth("RightPreviewWidth"), GridUnitType.Pixel);
                }
                else
                {
                    RightPreviewSplitterCol.Width = new GridLength(0);
                    RightPreviewCol.Width = new GridLength(0);
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] SyncRightPreviewPanelWidth error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore preview panel widths from saved settings on Loaded.
        /// </summary>
        private void RestorePreviewState()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

                if (ViewModel.IsLeftPreviewEnabled)
                {
                    LeftPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    double leftW = 320;
                    if (settings.Values.TryGetValue("LeftPreviewWidth", out var lw))
                        leftW = Math.Max(320, (double)lw);
                    LeftPreviewCol.Width = new GridLength(leftW, GridUnitType.Pixel);
                    SubscribePreviewToLastColumn(isLeft: true);
                }

                if (ViewModel.IsRightPreviewEnabled)
                {
                    RightPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    double rightW = 320;
                    if (settings.Values.TryGetValue("RightPreviewWidth", out var rw))
                        rightW = Math.Max(320, (double)rw);
                    RightPreviewCol.Width = new GridLength(rightW, GridUnitType.Pixel);
                    SubscribePreviewToLastColumn(isLeft: false);
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainWindow] RestorePreviewState error: {ex.Message}");
            }
        }

        /// <summary>
        /// LocalSettings에 저장된 미리보기 패널 너비를 읽는다. 미저장 시 기본 320px.
        /// </summary>
        private static double GetSavedPreviewWidth(string key)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(key, out var val))
                    return Math.Max(320, (double)val);
            }
            catch { }
            return 320;
        }

        #endregion

        // =================================================================
        //  Git Status Bar (bottom of explorer)
        // =================================================================

        #region Git Status Bar

        private PropertyChangedEventHandler? _leftExplorerGitHandler;
        private PropertyChangedEventHandler? _rightExplorerGitHandler;

        /// <summary>
        /// Git 상태바 초기화: ViewModel 생성, 이벤트 구독, SizeChanged 연결.
        /// </summary>
        private void InitializeGitStatusBars()
        {
            _leftGitStatusBarVm = new GitStatusBarViewModel();
            _rightGitStatusBarVm = new GitStatusBarViewModel();

            // PropertyChanged로 UI 바인딩
            _leftGitStatusBarVm.PropertyChanged += OnLeftGitStatusBarChanged;
            _rightGitStatusBarVm.PropertyChanged += OnRightGitStatusBarChanged;

            // Explorer CurrentPath 변경 구독
            SubscribeGitStatusToExplorer(isLeft: true);
            SubscribeGitStatusToExplorer(isLeft: false);

            // SizeChanged로 반응형 텍스트 갱신
            LeftGitStatusBar.SizeChanged += (s, e) =>
                _leftGitStatusBarVm?.UpdateStatusText(e.NewSize.Width);
            RightGitStatusBar.SizeChanged += (s, e) =>
                _rightGitStatusBarVm?.UpdateStatusText(e.NewSize.Width);

            // 초기 경로로 갱신
            _ = _leftGitStatusBarVm.UpdateForPathAsync(ViewModel.LeftExplorer?.CurrentPath);
            _ = _rightGitStatusBarVm.UpdateForPathAsync(ViewModel.RightExplorer?.CurrentPath);
        }

        /// <summary>
        /// Explorer.CurrentPath 변경을 감시하여 Git 상태바 갱신.
        /// </summary>
        private void SubscribeGitStatusToExplorer(bool isLeft)
        {
            UnsubscribeGitStatusFromExplorer(isLeft);

            var explorer = isLeft ? ViewModel.LeftExplorer : ViewModel.RightExplorer;
            if (explorer == null) return;

            PropertyChangedEventHandler handler = (s, e) =>
            {
                if (e.PropertyName == nameof(ExplorerViewModel.CurrentPath))
                {
                    var vm = isLeft ? _leftGitStatusBarVm : _rightGitStatusBarVm;
                    var path = (s as ExplorerViewModel)?.CurrentPath;
                    if (vm != null)
                        _ = vm.UpdateForPathAsync(path);
                }
            };

            explorer.PropertyChanged += handler;
            if (isLeft) _leftExplorerGitHandler = handler;
            else _rightExplorerGitHandler = handler;
        }

        private void UnsubscribeGitStatusFromExplorer(bool isLeft)
        {
            var explorer = isLeft ? ViewModel.LeftExplorer : ViewModel.RightExplorer;
            if (isLeft && _leftExplorerGitHandler != null)
            {
                if (explorer != null) explorer.PropertyChanged -= _leftExplorerGitHandler;
                _leftExplorerGitHandler = null;
            }
            else if (!isLeft && _rightExplorerGitHandler != null)
            {
                if (explorer != null) explorer.PropertyChanged -= _rightExplorerGitHandler;
                _rightExplorerGitHandler = null;
            }
        }

        /// <summary>
        /// 탭 전환 시 Git 상태바 Explorer 구독을 재연결.
        /// </summary>
        internal void ResubscribeGitStatusBar(bool isLeft)
        {
            SubscribeGitStatusToExplorer(isLeft);
            var explorer = isLeft ? ViewModel.LeftExplorer : ViewModel.RightExplorer;
            var vm = isLeft ? _leftGitStatusBarVm : _rightGitStatusBarVm;
            if (vm == null) return;

            // 파일 탐색과 무관한 모드(Settings/ActionLog/Home)에서는 git 정보 조회 자체를 skip
            // (SetViewModeVisibility의 Clear()를 ResubscribeGitStatusBar가 덮어쓰는 레이스 방지)
            var mode = ViewModel.CurrentViewMode;
            if (mode == ViewMode.Settings || mode == ViewMode.ActionLog || mode == ViewMode.Home)
            {
                vm.Clear();
                return;
            }

            if (explorer != null)
                _ = vm.UpdateForPathAsync(explorer.CurrentPath);
        }

        /// <summary>
        /// Left Git 상태바 ViewModel → UI 동기화.
        /// </summary>
        private void OnLeftGitStatusBarChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() => SyncGitStatusBarUI(isLeft: true));
        }

        private void OnRightGitStatusBarChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() => SyncGitStatusBarUI(isLeft: false));
        }

        /// <summary>
        /// GitStatusBarViewModel 데이터를 XAML 요소에 반영.
        /// </summary>
        private void SyncGitStatusBarUI(bool isLeft)
        {
            var vm = isLeft ? _leftGitStatusBarVm : _rightGitStatusBarVm;
            if (vm == null) return;

            var bar = isLeft ? LeftGitStatusBar : RightGitStatusBar;
            var branchTb = isLeft ? LeftGitBranch : RightGitBranch;
            var statusTb = isLeft ? LeftGitStatus : RightGitStatus;
            var flyoutBranch = isLeft ? LeftFlyoutBranch : RightFlyoutBranch;
            var flyoutStatus = isLeft ? LeftFlyoutStatus : RightFlyoutStatus;
            var flyoutCommitsLabel = isLeft ? LeftFlyoutCommitsLabel : RightFlyoutCommitsLabel;
            var flyoutCommits = isLeft ? LeftFlyoutCommits : RightFlyoutCommits;
            var flyoutFilesLabel = isLeft ? LeftFlyoutFilesLabel : RightFlyoutFilesLabel;
            var flyoutFiles = isLeft ? LeftFlyoutFiles : RightFlyoutFiles;

            bar.Visibility = vm.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            branchTb.Text = vm.Branch;
            statusTb.Text = vm.StatusText;

            // Flyout content
            flyoutBranch.Text = vm.Branch;
            flyoutStatus.Text = vm.FullStatusText;
            flyoutCommitsLabel.Text = _loc?.Get("GitStatus_RecentCommits") ?? "Recent Commits";
            flyoutCommits.Text = vm.RecentCommits;
            flyoutFilesLabel.Text = _loc?.Get("GitStatus_ChangedFiles") ?? "Changed Files";
            flyoutFiles.Text = vm.ChangedFiles;
        }

        /// <summary>
        /// Git 상태바 리소스 해제.
        /// </summary>
        private void CleanupGitStatusBars()
        {
            UnsubscribeGitStatusFromExplorer(isLeft: true);
            UnsubscribeGitStatusFromExplorer(isLeft: false);

            if (_leftGitStatusBarVm != null)
            {
                _leftGitStatusBarVm.PropertyChanged -= OnLeftGitStatusBarChanged;
                _leftGitStatusBarVm.Dispose();
                _leftGitStatusBarVm = null;
            }
            if (_rightGitStatusBarVm != null)
            {
                _rightGitStatusBarVm.PropertyChanged -= OnRightGitStatusBarChanged;
                _rightGitStatusBarVm.Dispose();
                _rightGitStatusBarVm = null;
            }
        }

        #endregion
    }
}
