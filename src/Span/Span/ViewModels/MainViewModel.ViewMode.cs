using Microsoft.Extensions.DependencyInjection;
using Span.Models;
using Span.Services;
using System;

namespace Span.ViewModels
{
    /// <summary>
    /// MainViewModel partial — 뷰 모드 전환 및 영속화.
    /// Miller Columns/Details/Icon/Home/Settings 모드 스위칭, 듀얼 패인 별 ViewMode 관리,
    /// 미리보기 패널 토글, Split View 상태 저장/복원.
    /// </summary>
    public partial class MainViewModel
    {
        #region View Mode Switching

        /// <summary>
        /// 뷰 모드 전환 — 활성 패널에 적용
        /// </summary>
        public void SwitchViewMode(ViewMode mode)
        {
            // Settings mode: 별도 탭으로 열기
            if (mode == ViewMode.Settings)
            {
                OpenOrSwitchToSettingsTab();
                return;
            }

            // RecycleBin mode: Home과 동일하게 현재 탭에서 ViewMode 전환
            if (mode == ViewMode.RecycleBin)
            {
                if (CurrentViewMode == ViewMode.RecycleBin) return;
                // RecycleBin 전환 전 현재 ViewMode 저장 (복귀용)
                if (CurrentViewMode != ViewMode.Settings && CurrentViewMode != ViewMode.ActionLog
                    && CurrentViewMode != ViewMode.Home && CurrentViewMode != ViewMode.RecycleBin)
                    _viewModeBeforeHome = CurrentViewMode;
                ActivePane = ActivePane.Left;
                CurrentViewMode = ViewMode.RecycleBin;
                LeftViewMode = ViewMode.RecycleBin;
                if (ActiveTab != null)
                {
                    ActiveTab.ViewMode = ViewMode.RecycleBin;
                }
                UpdateActiveTabHeader();
                UpdateStatusBar();
                _ = RefreshRecycleBinInfoAsync();
                return;
            }

            // Home mode — targets whichever pane is active
            if (mode == ViewMode.Home)
            {
                if (IsSplitViewEnabled && ActivePane == ActivePane.Right)
                {
                    // Right pane → Home
                    Helpers.DebugLogger.Log($"[SwitchViewMode→Home] RightViewMode={RightViewMode} → Home");
                    if (RightViewMode == ViewMode.Home) return;
                    if (_rightPreferredViewMode == null)
                        _rightPreferredViewMode = RightViewMode;
                    RightViewMode = ViewMode.Home;
                    Helpers.DebugLogger.Log($"[MainViewModel] ViewMode changed: Home (right pane)");
                    UpdateStatusBar();
                    return;
                }

                // Left pane → Home
                Helpers.DebugLogger.Log($"[SwitchViewMode→Home] CurrentViewMode={CurrentViewMode}, _viewModeBeforeHome={_viewModeBeforeHome}, _lastClosedViewMode={_lastClosedViewMode}");
                if (CurrentViewMode == ViewMode.Home) return;
                // Home 전환 전 현재 ViewMode 저장 — 드라이브/즐겨찾기 클릭 시 이전 뷰모드 복원에 사용.
                // Settings/ActionLog는 탐색기 뷰모드가 아니므로 저장하지 않음 (복원해도 의미 없음).
                // 저장된 값은 ResolveViewModeFromHome() 또는 CloseTab()에서 소비됨.
                if (CurrentViewMode != ViewMode.Settings && CurrentViewMode != ViewMode.ActionLog && CurrentViewMode != ViewMode.RecycleBin)
                    _viewModeBeforeHome = CurrentViewMode;
                Helpers.DebugLogger.Log($"[SwitchViewMode→Home] SAVED _viewModeBeforeHome={_viewModeBeforeHome}");
                ActivePane = ActivePane.Left;
                CurrentViewMode = ViewMode.Home;
                LeftViewMode = ViewMode.Home;
                SaveViewModePreference();
                Helpers.DebugLogger.Log($"[MainViewModel] ViewMode changed: Home (left pane)");
                UpdateStatusBar();
                return;
            }

            // Split/Quad: all visible panes share one explorer view mode (Miller/Details/List/Icon).
            // Independent left/right modes were confusing; dual=2 and quad=4 stay in sync.
            if (IsSplitViewEnabled)
            {
                bool already =
                    CurrentViewMode == mode && LeftViewMode == mode && RightViewMode == mode;
                if (already && !Helpers.ViewModeExtensions.IsIconMode(mode))
                    return;
                if (already && Helpers.ViewModeExtensions.IsIconMode(mode) && CurrentIconSize == mode)
                    return;

                if (Helpers.ViewModeExtensions.IsIconMode(mode))
                    CurrentIconSize = mode;

                CurrentViewMode = mode;
                LeftViewMode = mode;
                RightViewMode = mode;
                SyncExplorerAutoNavigationForLayout();
                Helpers.DebugLogger.Log($"[MainViewModel] Shared split ViewMode: {Helpers.ViewModeExtensions.GetDisplayName(mode)} (quad={IsQuadSplit})");
            }
            else
            {
                if (CurrentViewMode == mode) return;

                if (Helpers.ViewModeExtensions.IsIconMode(mode))
                {
                    CurrentIconSize = mode;
                    CurrentViewMode = mode;
                    LeftViewMode = mode;
                }
                else
                {
                    CurrentViewMode = mode;
                    LeftViewMode = mode;
                }

                LeftExplorer.EnableAutoNavigation = ShouldAutoNavigate(mode);
                Helpers.DebugLogger.Log($"[MainViewModel] Left pane AutoNav: {LeftExplorer.EnableAutoNavigation} (mode: {mode})");
            }

            // 활성 탭의 ViewMode를 먼저 동기화 (UpdateActiveTabHeader가 참조하므로)
            if (ActiveTab != null)
            {
                ActiveTab.ViewMode = CurrentViewMode;
                ActiveTab.IconSize = CurrentIconSize;
                ActiveTab.SplitRightViewMode = RightViewMode;
            }
            SaveViewModePreference();
            UpdateActiveTabHeader();
            Helpers.DebugLogger.Log($"[MainViewModel] ViewMode changed: {Helpers.ViewModeExtensions.GetDisplayName(mode)}");
            UpdateStatusBar();
        }

        /// <summary>
        /// Determines if auto-navigation should be enabled based on view mode and MillerClickBehavior setting.
        /// </summary>
        private bool ShouldAutoNavigate(ViewMode mode)
        {
            if (mode != ViewMode.MillerColumns) return false;
            try
            {
                var settings = App.Current.Services.GetRequiredService<Services.SettingsService>();
                return settings.MillerClickBehavior != "double";
            }
            catch { return true; }
        }

        #endregion

        #region View Mode Persistence

        /// <summary>
        /// ViewMode 설정 저장 (LocalSettings)
        /// </summary>
        private void SaveViewModePreference()
        {
            try
            {
                // Don't persist Home, Settings, ActionLog or RecycleBin as startup mode
                if (CurrentViewMode == ViewMode.Home || CurrentViewMode == ViewMode.Settings || CurrentViewMode == ViewMode.ActionLog || CurrentViewMode == ViewMode.RecycleBin) return;

                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                settings.Values["ViewMode"] = (int)CurrentViewMode;
                settings.Values["IconSize"] = (int)CurrentIconSize;
                settings.Values["LeftViewMode"] = (int)LeftViewMode;
                settings.Values["RightViewMode"] = (int)RightViewMode;
                Helpers.DebugLogger.Log($"[MainViewModel] ViewMode saved: L={LeftViewMode}, R={RightViewMode}, IconSize={CurrentIconSize}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveViewModePreference error: {ex.Message}");
            }
        }

        /// <summary>
        /// ViewMode 설정 로드 (앱 시작 시)
        /// </summary>
        public void LoadViewModePreference()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

                if (settings.Values.TryGetValue("ViewMode", out var mode) && mode is int modeInt
                    && System.Enum.IsDefined(typeof(ViewMode), modeInt))
                {
                    CurrentViewMode = (ViewMode)modeInt;
                    LeftViewMode = CurrentViewMode;
                }

                if (settings.Values.TryGetValue("IconSize", out var size) && size is int sizeInt
                    && System.Enum.IsDefined(typeof(ViewMode), sizeInt))
                {
                    CurrentIconSize = (ViewMode)sizeInt;
                }

                if (settings.Values.TryGetValue("LeftViewMode", out var leftMode) && leftMode is int leftInt
                    && System.Enum.IsDefined(typeof(ViewMode), leftInt))
                {
                    LeftViewMode = (ViewMode)leftInt;
                    CurrentViewMode = LeftViewMode;
                }

                if (settings.Values.TryGetValue("RightViewMode", out var rightMode) && rightMode is int rightInt
                    && System.Enum.IsDefined(typeof(ViewMode), rightInt))
                {
                    RightViewMode = (ViewMode)rightInt;
                }

                var settingsSvc = App.Current.Services.GetRequiredService<SettingsService>();
                var layoutInt = settingsSvc.Get("SplitLayoutMode", -1);
                if (layoutInt >= 0 && System.Enum.IsDefined(typeof(SplitLayoutMode), layoutInt))
                {
                    _splitLayoutMode = (SplitLayoutMode)layoutInt;
                    _isSplitViewEnabled = _splitLayoutMode != SplitLayoutMode.Single;
                    _splitOrientation = _splitLayoutMode == SplitLayoutMode.DualStacked
                        ? SplitOrientation.Stacked
                        : SplitOrientation.SideBySide;
                }
                else
                {
                    var orientInt = settingsSvc.Get("SplitOrientation", (int)SplitOrientation.SideBySide);
                    if (System.Enum.IsDefined(typeof(SplitOrientation), orientInt))
                        SplitOrientation = (SplitOrientation)orientInt;
                }

                // Split layout restored after tabs load (see RestoreSplitViewFromSettings).

                // Preview: 설정에서 기본값 로드 (DefaultPreviewEnabled)
                var previewDefault = settingsSvc.DefaultPreviewEnabled;
                IsLeftPreviewEnabled = previewDefault;
                IsRightPreviewEnabled = previewDefault;

                // Set auto-navigation based on loaded view mode
                LeftExplorer.EnableAutoNavigation = ShouldAutoNavigate(LeftViewMode);
                RightExplorer.EnableAutoNavigation = ShouldAutoNavigate(RightViewMode);
                Helpers.DebugLogger.Log($"[MainViewModel] AutoNav: L={LeftExplorer.EnableAutoNavigation}, R={RightExplorer.EnableAutoNavigation}");

                Helpers.DebugLogger.Log($"[MainViewModel] ViewMode loaded: L={Helpers.ViewModeExtensions.GetDisplayName(LeftViewMode)}, R={Helpers.ViewModeExtensions.GetDisplayName(RightViewMode)}, Split={IsSplitViewEnabled}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadViewModePreference error: {ex.Message}");
                CurrentViewMode = ViewMode.MillerColumns;
                LeftViewMode = ViewMode.MillerColumns;
                RightViewMode = ViewMode.MillerColumns;
                LeftExplorer.EnableAutoNavigation = ShouldAutoNavigate(ViewMode.MillerColumns);
                RightExplorer.EnableAutoNavigation = ShouldAutoNavigate(ViewMode.MillerColumns);
            }
        }

        #endregion

        #region Preview / Split View State

        /// <summary>
        /// Toggle preview panel for the active pane.
        /// </summary>
        public void TogglePreview()
        {
            if (ActivePane == ActivePane.Left)
                IsLeftPreviewEnabled = !IsLeftPreviewEnabled;
            else
                IsRightPreviewEnabled = !IsRightPreviewEnabled;

            SavePreviewState();
        }

        /// <summary>
        /// Save preview panel state to LocalSettings.
        /// </summary>
        public void SavePreviewState()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                settings.Values["IsLeftPreviewEnabled"] = IsLeftPreviewEnabled;
                settings.Values["IsRightPreviewEnabled"] = IsRightPreviewEnabled;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainViewModel] Error saving preview state: {ex.Message}");
            }
        }

        /// <summary>
        /// Save preview panel widths (called from MainWindow on close).
        /// </summary>
        public void SavePreviewWidths(double leftWidth, double rightWidth)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                settings.Values["LeftPreviewWidth"] = leftWidth;
                settings.Values["RightPreviewWidth"] = rightWidth;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainViewModel] Error saving preview widths: {ex.Message}");
            }
        }

        /// <summary>
        /// Persist split view state (e.g. after right pane navigation).
        /// </summary>
        internal void PersistSplitViewState() => SaveSplitViewState();

        /// <summary>
        /// Save split view state to LocalSettings
        /// </summary>
        private void SaveSplitViewState()
        {
            try
            {
                var settingsSvc = App.Current.Services.GetRequiredService<SettingsService>();
                settingsSvc.Set("SplitLayoutMode", (int)SplitLayoutMode);
                settingsSvc.Set("IsSplitViewEnabled", IsSplitViewEnabled);
                settingsSvc.Set("SplitOrientation", (int)SplitOrientation);

                PersistPanePath(settingsSvc, RightExplorer, ActivePane.Right);
                PersistPanePath(settingsSvc, TopRightExplorer, ActivePane.TopRight);
                PersistPanePath(settingsSvc, BottomRightExplorer, ActivePane.BottomRight);

                Helpers.DebugLogger.Log($"[MainViewModel] Split state saved: mode={SplitLayoutMode}");
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainViewModel] Error saving split state: {ex.Message}");
            }
        }

        private static void PersistPanePath(SettingsService settingsSvc, ExplorerViewModel? explorer, ActivePane pane)
        {
            var key = GetPanePathSettingsKey(pane);
            if (string.IsNullOrEmpty(key) || explorer == null)
                return;

            if (!string.IsNullOrEmpty(explorer.CurrentPath) && explorer.CurrentPath != "PC")
                settingsSvc.Set(key, explorer.CurrentPath);
        }

        partial void OnSplitOrientationChanged(SplitOrientation value)
        {
            if (SplitLayoutMode == SplitLayoutMode.DualSideBySide || SplitLayoutMode == SplitLayoutMode.DualStacked)
                SaveSplitViewState();
        }

        partial void OnIsSplitViewEnabledChanged(bool value)
        {
            if (!value)
            {
                if (SplitLayoutMode != SplitLayoutMode.Single)
                    _splitLayoutMode = SplitLayoutMode.Single;
            }
            else if (SplitLayoutMode == SplitLayoutMode.Single)
            {
                _splitLayoutMode = SplitLayoutMode.DualSideBySide;
                OnPropertyChanged(nameof(SplitLayoutMode));
                OnPropertyChanged(nameof(IsQuadSplit));
            }

            // Entering split: force shared view mode on all panes
            if (value && CurrentViewMode is not ViewMode.Home and not ViewMode.Settings
                and not ViewMode.ActionLog and not ViewMode.RecycleBin)
            {
                LeftViewMode = CurrentViewMode;
                RightViewMode = CurrentViewMode;
                SyncExplorerAutoNavigationForLayout();
            }

            SaveSplitViewState();
            SaveActiveTabState();
        }

        /// <summary>
        /// Restore dual-pane state after tabs are loaded (SwitchToTab resets per-tab split flags).
        /// </summary>
        public void RestoreSplitViewFromSettings()
        {
            try
            {
                var settingsSvc = App.Current.Services.GetRequiredService<SettingsService>();
                MigrateSplitStateFromLocalSettingsIfNeeded(settingsSvc);

                var layoutInt = settingsSvc.Get("SplitLayoutMode", -1);
                if (layoutInt >= 0 && System.Enum.IsDefined(typeof(SplitLayoutMode), layoutInt))
                {
                    var mode = (SplitLayoutMode)layoutInt;
                    if (mode == SplitLayoutMode.Single)
                        return;

                    _splitLayoutMode = mode;
                    _isSplitViewEnabled = true;
                    _splitOrientation = mode == SplitLayoutMode.DualStacked
                        ? SplitOrientation.Stacked
                        : SplitOrientation.SideBySide;
                    OnPropertyChanged(nameof(SplitLayoutMode));
                    OnPropertyChanged(nameof(IsSplitViewEnabled));
                    OnPropertyChanged(nameof(SplitOrientation));
                    OnPropertyChanged(nameof(IsQuadSplit));
                }
                else if (!settingsSvc.Get("IsSplitViewEnabled", false))
                {
                    return;
                }
                else
                {
                    var orientInt = settingsSvc.Get("SplitOrientation", (int)SplitOrientation.SideBySide);
                    if (System.Enum.IsDefined(typeof(SplitOrientation), orientInt))
                        SplitOrientation = (SplitOrientation)orientInt;

                    IsSplitViewEnabled = true;
                }

                if (ActiveTab != null)
                {
                    ActiveTab.IsSplitEnabled = true;
                    ActiveTab.SplitRightViewMode = RightViewMode;
                }

                Helpers.DebugLogger.Log($"[MainViewModel] Split view restored: {SplitLayoutMode}");
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainViewModel] RestoreSplitViewFromSettings error: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy split keys were written to ApplicationData.LocalSettings; unpackaged builds use settings.json.
        /// </summary>
        private static void MigrateSplitStateFromLocalSettingsIfNeeded(SettingsService settingsSvc)
        {
            if (settingsSvc.Get("IsSplitViewEnabled", false))
                return;

            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (!local.Values.TryGetValue("IsSplitViewEnabled", out var splitObj))
                    return;

                var enabled = splitObj switch
                {
                    bool b => b,
                    int i => i != 0,
                    _ => false
                };
                settingsSvc.Set("IsSplitViewEnabled", enabled);

                if (local.Values.TryGetValue("SplitOrientation", out var orient) && orient is int orientInt)
                    settingsSvc.Set("SplitOrientation", orientInt);

                if (local.Values.TryGetValue("SplitLayoutMode", out var layout) && layout is int layoutInt)
                    settingsSvc.Set("SplitLayoutMode", layoutInt);

                if (local.Values.TryGetValue("RightPanePath", out var path) && path is string pathStr)
                    settingsSvc.Set("RightPanePath", pathStr);

                if (local.Values.TryGetValue("TopRightPanePath", out var trPath) && trPath is string trStr)
                    settingsSvc.Set("TopRightPanePath", trStr);

                if (local.Values.TryGetValue("BottomRightPanePath", out var brPath) && brPath is string brStr)
                    settingsSvc.Set("BottomRightPanePath", brStr);

                Helpers.DebugLogger.Log("[MainViewModel] Migrated split state from LocalSettings to settings.json");
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[MainViewModel] Split state migration skipped: {ex.Message}");
            }
        }

        #region Pane Routing

        /// <summary>
        /// Keep Miller auto-navigation in sync with the active layout.
        /// Dual/Quad: all panes share CurrentViewMode (and thus the same AutoNav setting).
        /// </summary>
        public void SyncExplorerAutoNavigationForLayout()
        {
            if (IsQuadSplit)
            {
                bool autoNav = ShouldAutoNavigate(CurrentViewMode);
                LeftExplorer.EnableAutoNavigation = autoNav;
                RightExplorer.EnableAutoNavigation = autoNav;
                TopRightExplorer.EnableAutoNavigation = autoNav;
                BottomRightExplorer.EnableAutoNavigation = autoNav;
                Helpers.DebugLogger.Log($"[MainViewModel] Quad AutoNav: {autoNav} mode={CurrentViewMode}");
                return;
            }

            if (IsSplitViewEnabled)
            {
                // Dual: keep Left/Right in sync
                var mode = CurrentViewMode;
                if (RightViewMode != mode) RightViewMode = mode;
                if (LeftViewMode != mode) LeftViewMode = mode;
                bool autoNav = ShouldAutoNavigate(mode);
                LeftExplorer.EnableAutoNavigation = autoNav;
                RightExplorer.EnableAutoNavigation = autoNav;
            }
            else
            {
                LeftExplorer.EnableAutoNavigation = ShouldAutoNavigate(CurrentViewMode);
            }
        }

        public bool IsQuadSplit => SplitLayoutMode == SplitLayoutMode.Quad;

        /// <summary>
        /// 현재 레이아웃에서 표시 중인 패인 목록 (Single=Left만, Dual=Left+Right, Quad=4개).
        /// </summary>
        public IEnumerable<ActivePane> GetSplitLayoutPanes()
        {
            if (!IsSplitViewEnabled)
                return new[] { ActivePane.Left };
            if (IsQuadSplit)
                return new[] { ActivePane.Left, ActivePane.Right, ActivePane.TopRight, ActivePane.BottomRight };
            return new[] { ActivePane.Left, ActivePane.Right };
        }

        public ExplorerViewModel GetExplorerForPane(ActivePane pane) => pane switch
        {
            ActivePane.Left => LeftExplorer,
            ActivePane.Right => RightExplorer,
            ActivePane.TopRight => TopRightExplorer,
            ActivePane.BottomRight => BottomRightExplorer,
            _ => LeftExplorer,
        };

        public static string GetPanePathSettingsKey(ActivePane pane) => pane switch
        {
            ActivePane.Right => "RightPanePath",
            ActivePane.TopRight => "TopRightPanePath",
            ActivePane.BottomRight => "BottomRightPanePath",
            _ => "",
        };

        public void SetSplitLayoutMode(SplitLayoutMode mode)
        {
            SplitLayoutMode = mode;
            IsSplitViewEnabled = mode != SplitLayoutMode.Single;
            SplitOrientation = mode == SplitLayoutMode.DualStacked
                ? SplitOrientation.Stacked
                : SplitOrientation.SideBySide;
        }

        partial void OnSplitLayoutModeChanged(SplitLayoutMode value)
        {
            var split = value != SplitLayoutMode.Single;
            if (_isSplitViewEnabled != split)
                _isSplitViewEnabled = split;

            var orient = value == SplitLayoutMode.DualStacked
                ? SplitOrientation.Stacked
                : SplitOrientation.SideBySide;
            if (_splitOrientation != orient)
                _splitOrientation = orient;

            OnPropertyChanged(nameof(IsSplitViewEnabled));
            OnPropertyChanged(nameof(SplitOrientation));
            OnPropertyChanged(nameof(IsQuadSplit));
            SaveSplitViewState();
        }

        #endregion

        #endregion
    }
}
