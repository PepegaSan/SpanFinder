using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Span.Models;
using Span.Services;
using Span.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Span.Views
{
    /// <summary>
    /// Home 뷰 모드 UserControl.
    /// 로컬 드라이브, 클라우드 스토리지, 네트워크 위치, 즐겨찾기를
    /// GridView로 표시하는 시작 화면이다.
    /// 드라이브/즐겨찾기 클릭 시 탐색, 우클릭 컨텍스트 메뉴를 지원한다.
    /// </summary>
    public sealed partial class HomeModeView : UserControl
    {
        public ContextMenuService? ContextMenuService { get; set; }
        public IContextMenuHost? ContextMenuHost { get; set; }

        private MainViewModel? _mainViewModel;
        private SettingsService? _settings;
        private LocalizationService? _loc;

        public ObservableCollection<DriveItem>? LocalDrives => _mainViewModel?.Drives;
        public ObservableCollection<DriveItem>? CloudDrivesList => _mainViewModel?.CloudDrives;
        public ObservableCollection<DriveItem>? NetworkDrivesList => _mainViewModel?.NetworkAndRemoteDrives;
        public ObservableCollection<FavoriteItem>? Favorites => _mainViewModel?.Favorites;

        public Visibility HasCloudDrives =>
            _mainViewModel?.CloudDrives?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasNetworkDrives =>
            _mainViewModel?.NetworkAndRemoteDrives?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public MainViewModel? MainViewModel
        {
            get => _mainViewModel;
            set
            {
                if (_mainViewModel != null)
                {
                    _mainViewModel.CloudDrives.CollectionChanged -= OnCloudDrivesChanged;
                    _mainViewModel.NetworkAndRemoteDrives.CollectionChanged -= OnNetworkDrivesChanged;
                }

                _mainViewModel = value;

                if (_mainViewModel != null)
                {
                    _mainViewModel.CloudDrives.CollectionChanged += OnCloudDrivesChanged;
                    _mainViewModel.NetworkAndRemoteDrives.CollectionChanged += OnNetworkDrivesChanged;
                    Bindings.Update();
                }
            }
        }

        public HomeModeView()
        {
            this.InitializeComponent();

            this.Loaded += (s, e) =>
            {
                try
                {
                    _settings = App.Current.Services.GetService(typeof(SettingsService)) as SettingsService;
                    _loc = App.Current.Services.GetService(typeof(LocalizationService)) as LocalizationService;
                    LocalizeUI();
                    if (_loc != null) _loc.LanguageChanged += LocalizeUI;
                    if (_settings != null) _settings.SettingChanged += OnSettingChanged;
                }
                catch (Exception ex) { Helpers.DebugLogger.Log($"[HomeModeView] Loaded init error: {ex.Message}"); }

                _ = LoadRecentFilesAsync();
            };

            // Startseite bleibt beim Tab-/Ansichtswechsel geladen und wird nur ausgeblendet — beim Einblenden neu lesen
            RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            {
                if (Visibility == Visibility.Visible) _ = LoadRecentFilesAsync();
            });

            this.Unloaded += (s, e) =>
            {
                if (_mainViewModel != null)
                {
                    _mainViewModel.CloudDrives.CollectionChanged -= OnCloudDrivesChanged;
                    _mainViewModel.NetworkAndRemoteDrives.CollectionChanged -= OnNetworkDrivesChanged;
                }
                if (_loc != null) _loc.LanguageChanged -= LocalizeUI;
                if (_settings != null) _settings.SettingChanged -= OnSettingChanged;
                _settings = null;
            };
        }

        // ── Zuletzt verwendet ──

        private bool _recentLoading;

        private async System.Threading.Tasks.Task LoadRecentFilesAsync()
        {
            if (_recentLoading) return;
            if (_settings != null && !_settings.HomeShowRecentFiles)
            {
                RecentSection.Visibility = Visibility.Collapsed;
                return;
            }

            _recentLoading = true;
            try
            {
                var items = await RecentFilesService.LoadAsync();
                RecentListView.ItemsSource = items;
                RecentSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[HomeModeView] recent files failed: {ex.Message}");
            }
            finally
            {
                _recentLoading = false;
            }
        }

        private void OnSettingChanged(string key, object? value)
        {
            if (key == "HomeShowRecentFiles")
                Helpers.DispatcherHelper.SafeEnqueue(DispatcherQueue, () => _ = LoadRecentFilesAsync());
        }

        private void OnRecentItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not RecentFileItem item) return;
            try
            {
                (App.Current.Services.GetService(typeof(ShellService)) as ShellService)?.OpenFile(item.Path);
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[HomeModeView] open recent failed: {ex.Message}"); }
        }

        private void OnRecentRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not RecentFileItem item) return;
            e.Handled = true;
            string L(string key) => _loc?.Get(key) ?? key;

            var flyout = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = L("Open"), Icon = new FontIcon { Glyph = "\uE8E5" } };
            open.Click += (_, _) => (App.Current.Services.GetService(typeof(ShellService)) as ShellService)?.OpenFile(item.Path);
            var location = new MenuFlyoutItem { Text = L("Home_OpenFileLocation"), Icon = new FontIcon { Glyph = "\uED25" } };
            location.Click += (_, _) => (ContextMenuHost as MainWindow)?.RevealFile(item.Path);
            var copyPath = new MenuFlyoutItem { Text = L("CopyPath"), Icon = new FontIcon { Glyph = "\uE8C8" } };
            copyPath.Click += (_, _) => ContextMenuHost?.PerformCopyPath(item.Path);
            flyout.Items.Add(open);
            flyout.Items.Add(location);
            flyout.Items.Add(copyPath);

            if (ContextMenuService != null && ContextMenuHost != null)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                var more = new MenuFlyoutItem { Text = L("Home_MoreOptions"), Icon = new FontIcon { Glyph = "\uE712" } };
                more.Click += (_, _) => ContextMenuService.TryShowNativeContextMenu(item.Path, ContextMenuHost);
                flyout.Items.Add(more);
            }

            flyout.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(fe) });
        }

        private void OnCloudDrivesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Bindings.Update();
        }

        private void OnNetworkDrivesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Bindings.Update();
        }

        private void LocalizeUI()
        {
            if (_loc == null) return;
            DevicesHeader.Text = _loc.Get("DevicesAndDrives");
            CloudHeader.Text = _loc.Get("CloudStorage");
            NetworkHeader.Text = _loc.Get("NetworkLocations");
            FavoritesHeader.Text = _loc.Get("Favorites");
            RecentHeader.Text = _loc.Get("Home_Recent");
        }

        /// <summary>
        /// Drive single-click via GridView.ItemClick (IsItemClickEnabled=True)
        /// </summary>
        private void OnDriveItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DriveItem drive && _mainViewModel != null)
            {
                _mainViewModel.OpenDrive(drive);
                Helpers.DebugLogger.Log($"[HomeModeView] Drive clicked: {drive.Name}");
            }
        }

        /// <summary>
        /// Favorite single-click via GridView.ItemClick (IsItemClickEnabled=True)
        /// </summary>
        private void OnFavoriteItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FavoriteItem favorite && _mainViewModel != null)
            {
                _mainViewModel.NavigateToFavorite(favorite);
                Helpers.DebugLogger.Log($"[HomeModeView] Favorite clicked: {favorite.Name}");
            }
        }

        private void OnDriveRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (_settings != null && !_settings.ShowContextMenu) return;
            if (ContextMenuService == null || ContextMenuHost == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is DriveItem drive)
            {
                var flyout = ContextMenuService.BuildDriveMenu(drive, ContextMenuHost);
                flyout.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = e.GetPosition(fe)
                });
                e.Handled = true;
            }
        }

        /// <summary>
        /// Issue #39 a: 홈 즐겨찾기 컨테이너가 가시화될 때 desktop.ini 커스텀 아이콘 lazy 로드.
        /// </summary>
        private void OnFavoriteContainerContentChanging(
            Microsoft.UI.Xaml.Controls.ListViewBase sender,
            Microsoft.UI.Xaml.Controls.ContainerContentChangingEventArgs args)
        {
            if (args.Item is FavoriteItem favorite)
                favorite.RequestCustomIconLoad();
        }

        private void OnFavoriteRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (_settings != null && !_settings.ShowContextMenu) return;
            if (ContextMenuService == null || ContextMenuHost == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is FavoriteItem favorite)
            {
                var flyout = ContextMenuService.BuildFavoriteMenu(favorite, ContextMenuHost);
                flyout.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = e.GetPosition(fe)
                });
                e.Handled = true;
            }
        }

        /// <summary>
        /// 홈 화면의 TextBlock/FontIcon 크기를 스케일 레벨에 맞춰 조정한다.
        /// 절대값 기반: baseline + level = 최종 FontSize.
        /// 범위: TextBlock/FontIcon 8~20 (28px 드라이브 아이콘, 24px 즐겨찾기 아이콘은 제외)
        /// </summary>
        public void ApplyIconFontScale(int level)
        {
            MainWindow.ApplyAbsoluteScaleToTree(this, level, 8, 20);
        }

        public void Cleanup()
        {
            try
            {
                if (_mainViewModel != null)
                {
                    _mainViewModel.CloudDrives.CollectionChanged -= OnCloudDrivesChanged;
                    _mainViewModel.NetworkAndRemoteDrives.CollectionChanged -= OnNetworkDrivesChanged;
                }

                if (DrivesGridView != null)
                    DrivesGridView.ItemsSource = null;
                if (CloudDrivesGridView != null)
                    CloudDrivesGridView.ItemsSource = null;
                if (NetworkDrivesGridView != null)
                    NetworkDrivesGridView.ItemsSource = null;
                if (FavoritesGridView != null)
                    FavoritesGridView.ItemsSource = null;
                RootPanel.DataContext = null;
                _mainViewModel = null;
            }
            catch { /* ignore during teardown */ }
        }
    }
}
