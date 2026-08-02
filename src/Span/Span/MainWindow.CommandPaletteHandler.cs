// ═══════════════════════════════════════════════════════════════════════════════
//  Command Palette (Ctrl+K)
// ═══════════════════════════════════════════════════════════════════════════════
//
//  Default binding: Ctrl+K (KeyBindingService). Overlay starts Collapsed until opened.
//  Future idea: reframe toward Quick Open (folder jump + "> " command mode).
//
//  Related: ShortcutCommands, CommandPaletteItem, HangulSearchHelper,
//  MainWindow.xaml overlay, KeyboardHandler ExecuteCommand, LocalizationData.
//
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Span.Helpers;
using Span.Models;
using Span.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Span
{
    /// <summary>
    /// Command Palette (Ctrl+K) 관련 이벤트 핸들러.
    /// 한글 검색, 컨텍스트 기반 비활성화, Settings 통합, 카테고리 그룹화, 최근 사용 추적 지원.
    /// </summary>
    public partial class MainWindow
    {
        private bool _isCommandPaletteOpen;
        private List<CommandPaletteItem>? _commandCatalog;
        private const int MaxRecentCommands = 8;

        internal void ToggleCommandPalette()
        {
            if (_isCommandPaletteOpen) CloseCommandPalette();
            else OpenCommandPalette();
        }

        private void OpenCommandPalette()
        {
            _isCommandPaletteOpen = true;
            CommandPaletteOverlay.Visibility = Visibility.Visible;
            CommandPaletteInput.Text = string.Empty;
            CommandPaletteInput.PlaceholderText = _loc.Get("CommandPalette_Placeholder");
            CommandPaletteInput.Focus(FocusState.Programmatic);

            _commandCatalog = BuildCommandCatalog();
            UpdateCommandPaletteResults(string.Empty);
        }

        private void CloseCommandPalette()
        {
            _isCommandPaletteOpen = false;
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnCommandPaletteOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CloseCommandPalette();
        }

        private void OnCommandPaletteInputTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCommandPaletteResults(CommandPaletteInput.Text);
        }

        private void OnCommandPaletteInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseCommandPalette();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ExecuteSelectedPaletteItem();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                if (CommandPaletteList.Items.Count > 0)
                {
                    var idx = CommandPaletteList.SelectedIndex;
                    CommandPaletteList.SelectedIndex = Math.Min(idx + 1, CommandPaletteList.Items.Count - 1);
                    CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
                }
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Up)
            {
                if (CommandPaletteList.Items.Count > 0)
                {
                    var idx = CommandPaletteList.SelectedIndex;
                    CommandPaletteList.SelectedIndex = Math.Max(idx - 1, 0);
                    CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
                }
                e.Handled = true;
            }
        }

        private void OnCommandPaletteItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is CommandPaletteItem item)
                ExecutePaletteItem(item);
        }

        private void ExecuteSelectedPaletteItem()
        {
            if (CommandPaletteList.SelectedItem is CommandPaletteItem item)
                ExecutePaletteItem(item);
        }

        private void ExecutePaletteItem(CommandPaletteItem item)
        {
            // 비활성 항목은 무시 (회색 처리된 명령)
            if (!item.IsEnabled)
            {
                ViewModel.ShowToast(_loc.Get("CommandPalette_NotAvailable"));
                return;
            }

            CloseCommandPalette();

            switch (item.Type)
            {
                case CommandPaletteItemType.Command:
                case CommandPaletteItemType.SettingToggle:
                case CommandPaletteItemType.SettingSelect:
                case CommandPaletteItemType.SettingsSection:
                    if (!string.IsNullOrEmpty(item.CommandId))
                    {
                        TrackRecentCommand(item.CommandId);
                        ExecuteCommand(item.CommandId);
                    }
                    break;

                case CommandPaletteItemType.Tab:
                    if (item.TabIndex >= 0 && item.TabIndex < ViewModel.Tabs.Count)
                        SwitchToTabByIndex(item.TabIndex);
                    break;

                case CommandPaletteItemType.Navigation:
                    break;
            }
        }

        // ── 결과 갱신 ───────────────────────────────────────────

        private void UpdateCommandPaletteResults(string query)
        {
            if (_commandCatalog == null) return;

            // 컨텍스트 변경에 따라 IsEnabled 매번 재평가
            foreach (var item in _commandCatalog)
                item.IsEnabled = IsCommandAvailable(item);

            List<CommandPaletteItem> results;

            if (string.IsNullOrWhiteSpace(query))
            {
                // 빈 입력: 최근 사용 → 그 외 알파벳 순
                var recentIds = LoadRecentCommandIds();
                var recentItems = recentIds
                    .Select(id => _commandCatalog.FirstOrDefault(c => c.CommandId == id))
                    .Where(c => c != null)
                    .Cast<CommandPaletteItem>()
                    .ToList();

                var rest = _commandCatalog
                    .Where(c => !recentIds.Contains(c.CommandId))
                    .OrderByDescending(c => c.IsEnabled) // 활성 우선
                    .ThenBy(c => c.Category)
                    .ThenBy(c => c.Title)
                    .Take(60)
                    .ToList();

                results = recentItems.Concat(rest).ToList();
            }
            else
            {
                // 검색: 한글/초성/영문 매칭
                results = _commandCatalog
                    .Where(c => MatchesQuery(c, query))
                    .OrderByDescending(c => c.IsEnabled)
                    .ThenByDescending(c => ScoreItem(c, query))
                    .Take(80)
                    .ToList();
            }

            CommandPaletteList.ItemsSource = results;
            if (results.Count > 0)
                CommandPaletteList.SelectedIndex = 0;

            CommandPaletteNoResults.Visibility = results.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool MatchesQuery(CommandPaletteItem item, string query)
        {
            if (HangulSearchHelper.Match(item.Title, query)) return true;
            if (HangulSearchHelper.Match(item.Category, query)) return true;
            if (HangulSearchHelper.Match(item.GroupName, query)) return true;
            foreach (var alias in item.Aliases)
            {
                if (HangulSearchHelper.Match(alias, query)) return true;
            }
            return false;
        }

        private static int ScoreItem(CommandPaletteItem item, string query)
        {
            int best = HangulSearchHelper.Score(item.Title, query);
            int catScore = HangulSearchHelper.Score(item.Category, query);
            if (catScore > best) best = catScore;
            foreach (var alias in item.Aliases)
            {
                int s = HangulSearchHelper.Score(alias, query);
                if (s > best) best = s;
            }
            return best;
        }

        // ── 컨텍스트 기반 활성화 ────────────────────────────────

        private bool IsCommandAvailable(CommandPaletteItem item)
        {
            var commandId = item.CommandId;
            var viewMode = ViewModel.CurrentViewMode;
            int selectedCount = 0;
            try { selectedCount = GetCurrentSelectedItems()?.Count ?? 0; } catch { }

            bool isFileMode = viewMode == ViewMode.MillerColumns
                || viewMode == ViewMode.Details
                || viewMode == ViewMode.List
                || viewMode == ViewMode.IconSmall
                || viewMode == ViewMode.IconMedium
                || viewMode == ViewMode.IconLarge
                || viewMode == ViewMode.IconExtraLarge;

            // 탭 항목은 항상 사용 가능
            if (item.Type == CommandPaletteItemType.Tab) return true;

            // 파일 작업 명령: file 모드가 아니면 비활성
            switch (commandId)
            {
                case ShortcutCommands.Copy:
                case ShortcutCommands.Cut:
                case ShortcutCommands.Delete:
                case ShortcutCommands.PermanentDelete:
                case ShortcutCommands.Rename:
                case ShortcutCommands.Duplicate:
                case ShortcutCommands.ShowProperties:
                case ShortcutCommands.OpenInNewTab:
                case ShortcutCommands.PasteAsShortcut:
                    if (!isFileMode) return false;
                    if (selectedCount == 0) return false;
                    return true;

                case ShortcutCommands.NewFolder:
                case ShortcutCommands.Paste:
                    return isFileMode;

                case ShortcutCommands.NavigateBack:
                case ShortcutCommands.NavigateForward:
                case ShortcutCommands.NavigateUp:
                    if (viewMode == ViewMode.RecycleBin) return false;
                    return true;

                case ShortcutCommands.SelectAll:
                case ShortcutCommands.SelectNone:
                case ShortcutCommands.InvertSelection:
                case ShortcutCommands.Refresh:
                    return isFileMode;

                case ShortcutCommands.QuickLook:
                    if (!isFileMode || selectedCount == 0) return false;
                    return App.Current.Services.GetRequiredService<ISettingsService>().EnableQuickLook;

                case ShortcutCommands.SwitchPane:
                    return ViewModel.IsSplitViewEnabled;

                case ShortcutCommands.OpenSettings:
                    // 이미 Settings 탭이 열려 있으면 비활성
                    return !ViewModel.Tabs.Any(t => t.ViewMode == ViewMode.Settings);

                case ShortcutCommands.ShelfAdd:
                case ShortcutCommands.ShelfMoveHere:
                case ShortcutCommands.ShelfCopyHere:
                case ShortcutCommands.ShelfToggle:
                    return App.Current.Services.GetRequiredService<ISettingsService>().ShelfEnabled;

                case ShortcutCommands.ShelfClear:
                    return ViewModel.ShelfItems.Count > 0;

                case ShortcutCommands.OpenWorkspacePalette:
                case ShortcutCommands.SaveWorkspace:
                    return true;
            }

            return true;
        }

        // ── 카탈로그 빌드 ───────────────────────────────────────

        private List<CommandPaletteItem> BuildCommandCatalog()
        {
            var catalog = new List<CommandPaletteItem>();
            var keyBindingSvc = _keyBindingService ??= App.Current.Services.GetRequiredService<KeyBindingService>();
            var bindings = keyBindingSvc.CloneCurrentBindings();
            var settings = App.Current.Services.GetRequiredService<ISettingsService>();

            // 1. 등록된 모든 명령 (Settings 명령은 별도로 처리하므로 제외)
            foreach (var cmdId in ShortcutCommands.GetAllCommands())
            {
                if (cmdId.StartsWith("span.settings.")) continue;

                var displayName = ShortcutCommands.GetDisplayName(cmdId);
                var category = ShortcutCommands.GetCategory(cmdId);
                var shortcut = bindings.TryGetValue(cmdId, out var keys) && keys.Count > 0
                    ? keys[0] : string.Empty;

                catalog.Add(new CommandPaletteItem
                {
                    Title = displayName,
                    Category = LocalizeCategory(category),
                    GroupName = LocalizeCategory(category),
                    CommandId = cmdId,
                    Shortcut = shortcut,
                    Type = CommandPaletteItemType.Command,
                    IconGlyph = GetCommandIconGlyph(category),
                });
            }

            // 2. Settings: Toggle 명령
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleHidden, "Settings_ShowHiddenFiles", settings.ShowHiddenFiles, new[] { "hidden", "숨김" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleExtensions, "Settings_ShowFileExtensions", settings.ShowFileExtensions, new[] { "extension", "확장자" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleCheckboxes, "Settings_ShowCheckboxes", settings.ShowCheckboxes, new[] { "checkbox", "체크박스" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleThumbnails, "Settings_ShowThumbnails", settings.ShowThumbnails, new[] { "thumb", "썸네일" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleQuickLook, "Settings_EnableQuickLook", settings.EnableQuickLook, new[] { "quicklook", "preview", "미리보기" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleWasd, "Settings_EnableWasdNavigation", settings.EnableWasdNavigation, new[] { "wasd", "키보드" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleConfirmDelete, "Settings_ConfirmDelete", settings.ConfirmDelete, new[] { "delete", "삭제확인" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsTogglePreviewFolderInfo, "Settings_PreviewFolderInfo", settings.PreviewShowFolderInfo, new[] { "folder info", "폴더정보" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleDefaultPreview, "Settings_DefaultPreview", settings.DefaultPreviewEnabled, new[] { "default preview", "기본미리보기" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleFavoritesTree, "Settings_ShowFavoritesTree", settings.ShowFavoritesTree, new[] { "favorites tree", "즐겨찾기 트리" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleShelf, "Settings_ShelfEnabled", settings.ShelfEnabled, new[] { "shelf", "선반" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleShelfSave, "Settings_ShelfSave", settings.ShelfSaveEnabled, new[] { "shelf save", "선반 저장" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleContextMenu, "Settings_ShowContextMenu", settings.ShowContextMenu, new[] { "context menu", "컨텍스트" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleTray, "Settings_MinimizeToTray", settings.MinimizeToTray, new[] { "tray", "트레이" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleWindowPosition, "Settings_RememberWindowPosition", settings.RememberWindowPosition, new[] { "window position", "창위치" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleGitIntegration, "Settings_ShowGitIntegration", settings.ShowGitIntegration, new[] { "git", "깃" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleHexPreview, "Settings_ShowHexPreview", settings.ShowHexPreview, new[] { "hex", "헥스" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleFileHash, "Settings_ShowFileHash", settings.ShowFileHash, new[] { "hash", "해시" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleShellExtensions, "Settings_ShowShellExtensions", settings.ShowShellExtensions, new[] { "shell ext", "셸확장" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleWindowsShellExtras, "Settings_ShowWindowsShellExtras", settings.ShowWindowsShellExtras, new[] { "windows shell", "윈도우셸" });
            AddSettingToggle(catalog, ShortcutCommands.SettingsToggleCopilotMenu, "Settings_ShowCopilotMenu", settings.ShowCopilotMenu, new[] { "copilot", "코파일럿" });

            // 3. Sidebar 토글
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarHome, "Settings_SidebarShowHome", settings.SidebarShowHome, new[] { "sidebar home", "사이드바 홈" }, "Sidebar");
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarFavorites, "Settings_SidebarShowFavorites", settings.SidebarShowFavorites, new[] { "sidebar favorites", "사이드바 즐겨찾기" }, "Sidebar");
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarDrives, "Settings_SidebarShowDrives", settings.SidebarShowLocalDrives, new[] { "sidebar drives", "사이드바 드라이브" }, "Sidebar");
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarCloud, "Settings_SidebarShowCloud", settings.SidebarShowCloud, new[] { "sidebar cloud", "사이드바 클라우드" }, "Sidebar");
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarNetwork, "Settings_SidebarShowNetwork", settings.SidebarShowNetwork, new[] { "sidebar network", "사이드바 네트워크" }, "Sidebar");
            AddSettingToggle(catalog, ShortcutCommands.SettingsSidebarRecycleBin, "Settings_SidebarShowRecycleBin", settings.SidebarShowRecycleBin, new[] { "sidebar recycle", "사이드바 휴지통" }, "Sidebar");

            // 4. Theme select
            AddSettingSelect(catalog, ShortcutCommands.SettingsThemeSystem, "Cmd_ThemeSystem", "Theme", settings.Theme == "system", new[] { "theme system", "테마 시스템" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsThemeLight, "Cmd_ThemeLight", "Theme", settings.Theme == "light", new[] { "theme light", "라이트", "밝은" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsThemeDark, "Cmd_ThemeDark", "Theme", settings.Theme == "dark", new[] { "theme dark", "다크", "어두운" });

            // 5. Density select
            AddSettingSelect(catalog, ShortcutCommands.SettingsDensityCompact, "Cmd_DensityCompact", "Density", settings.Density == "compact", new[] { "density compact", "조밀" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsDensityComfortable, "Cmd_DensityComfortable", "Density", settings.Density == "comfortable", new[] { "density comfortable", "기본" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsDensitySpacious, "Cmd_DensitySpacious", "Density", settings.Density == "spacious", new[] { "density spacious", "넓은" });

            // 6. Language select
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageSystem, "Cmd_LangSystem", "Language", settings.Language == "system", new[] { "language system", "언어 시스템" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageEn, "Cmd_LangEn", "Language", settings.Language == "en", new[] { "english", "영어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageKo, "Cmd_LangKo", "Language", settings.Language == "ko", new[] { "korean", "한국어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageJa, "Cmd_LangJa", "Language", settings.Language == "ja", new[] { "japanese", "일본어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageZhHans, "Cmd_LangZhHans", "Language", settings.Language == "zh-Hans", new[] { "chinese simplified", "중국어 간체" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageZhHant, "Cmd_LangZhHant", "Language", settings.Language == "zh-Hant", new[] { "chinese traditional", "중국어 번체" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageDe, "Cmd_LangDe", "Language", settings.Language == "de", new[] { "german", "독일어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageEs, "Cmd_LangEs", "Language", settings.Language == "es", new[] { "spanish", "스페인어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguageFr, "Cmd_LangFr", "Language", settings.Language == "fr", new[] { "french", "프랑스어" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsLanguagePtBr, "Cmd_LangPtBr", "Language", settings.Language == "pt-BR", new[] { "portuguese", "포르투갈어" });

            // 7. Icon Pack
            AddSettingSelect(catalog, ShortcutCommands.SettingsIconPackRemix, "Cmd_IconRemix", "IconPack", settings.IconPack == "remix", new[] { "icon remix" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsIconPackPhosphor, "Cmd_IconPhosphor", "IconPack", settings.IconPack == "phosphor", new[] { "icon phosphor" });
            AddSettingSelect(catalog, ShortcutCommands.SettingsIconPackTabler, "Cmd_IconTabler", "IconPack", settings.IconPack == "tabler", new[] { "icon tabler" });

            // 8. Settings Sections
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenGeneral, "Cmd_OpenGeneral");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenAppearance, "Cmd_OpenAppearance");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenBrowsing, "Cmd_OpenBrowsing");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenSidebar, "Cmd_OpenSidebar");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenTools, "Cmd_OpenTools");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenShortcuts, "Cmd_OpenShortcuts");
            AddSettingsSection(catalog, ShortcutCommands.SettingsOpenAdvanced, "Cmd_OpenAdvanced");

            // 9. Open tabs
            for (int i = 0; i < ViewModel.Tabs.Count; i++)
            {
                var tab = ViewModel.Tabs[i];
                catalog.Add(new CommandPaletteItem
                {
                    Title = tab.Header ?? "Tab",
                    Category = _loc.Get("CommandPalette_Tabs"),
                    GroupName = _loc.Get("CommandPalette_Tabs"),
                    TabIndex = i,
                    Type = CommandPaletteItemType.Tab,
                    IconGlyph = "\uE737",
                    Aliases = { "tab", "탭" },
                });
            }

            return catalog;
        }

        private void AddSettingToggle(List<CommandPaletteItem> catalog, string commandId, string locKey, bool currentValue, string[] aliases, string? group = null)
        {
            var label = _loc.Get(locKey);
            var stateText = currentValue ? _loc.Get("Cmd_StateOn") : _loc.Get("Cmd_StateOff");
            var groupName = group ?? _loc.Get("Cmd_Group_Settings");
            var item = new CommandPaletteItem
            {
                Title = $"{label} ({stateText})",
                Category = groupName,
                GroupName = groupName,
                CommandId = commandId,
                Type = CommandPaletteItemType.SettingToggle,
                IconGlyph = currentValue ? "\uE73E" : "\uE711", // 체크 / X
                CurrentStateText = stateText,
            };
            foreach (var a in aliases) item.Aliases.Add(a);
            catalog.Add(item);
        }

        private void AddSettingSelect(List<CommandPaletteItem> catalog, string commandId, string titleLocKey, string groupKey, bool isCurrent, string[] aliases)
        {
            var groupName = _loc.Get($"Cmd_Group_{groupKey}");
            var titleBase = _loc.Get(titleLocKey);
            var item = new CommandPaletteItem
            {
                Title = isCurrent ? $"{titleBase}  ●" : titleBase,
                Category = groupName,
                GroupName = groupName,
                CommandId = commandId,
                Type = CommandPaletteItemType.SettingSelect,
                IconGlyph = isCurrent ? "\uE915" : "\uE9CE", // selected / circle
            };
            foreach (var a in aliases) item.Aliases.Add(a);
            catalog.Add(item);
        }

        private void AddSettingsSection(List<CommandPaletteItem> catalog, string commandId, string titleLocKey)
        {
            var groupName = _loc.Get("Cmd_Group_GoToSettings");
            catalog.Add(new CommandPaletteItem
            {
                Title = _loc.Get(titleLocKey),
                Category = groupName,
                GroupName = groupName,
                CommandId = commandId,
                Type = CommandPaletteItemType.SettingsSection,
                IconGlyph = "\uE713", // gear
                Aliases = { "settings", "설정" },
            });
        }

        private string LocalizeCategory(string category) => category switch
        {
            "Navigation" => _loc.Get("Cmd_Cat_Navigation"),
            "Edit" => _loc.Get("Cmd_Cat_Edit"),
            "Selection" => _loc.Get("Cmd_Cat_Selection"),
            "View" => _loc.Get("Cmd_Cat_View"),
            "Tab" => _loc.Get("Cmd_Cat_Tab"),
            "Window" => _loc.Get("Cmd_Cat_Window"),
            "Workspace" => _loc.Get("Cmd_Cat_Workspace"),
            "Shelf" => _loc.Get("Cmd_Cat_Shelf"),
            "QuickLook" => _loc.Get("Cmd_Cat_QuickLook"),
            "CommandPalette" => _loc.Get("Cmd_Cat_CommandPalette"),
            _ => category,
        };

        private static string GetCommandIconGlyph(string category) => category switch
        {
            "Navigation" => "\uE72A",
            "Edit" => "\uE70F",
            "Selection" => "\uE762",
            "View" => "\uE8A9",
            "Tab" => "\uE737",
            "Window" => "\uE8A7",
            "Shelf" => "\uE8F1",
            "CommandPalette" => "\uE773",
            "Workspace" => "\uE737",
            "QuickLook" => "\uE8FF",
            _ => "\uE756",
        };

        // ── 최근 사용 추적 ──────────────────────────────────────

        private void TrackRecentCommand(string commandId)
        {
            try
            {
                var settings = App.Current.Services.GetRequiredService<ISettingsService>();
                var current = LoadRecentCommandIds();
                current.Remove(commandId);
                current.Insert(0, commandId);
                if (current.Count > MaxRecentCommands)
                    current.RemoveRange(MaxRecentCommands, current.Count - MaxRecentCommands);
                settings.RecentCommandIds = string.Join("|", current);
            }
            catch { }
        }

        private List<string> LoadRecentCommandIds()
        {
            try
            {
                var settings = App.Current.Services.GetRequiredService<ISettingsService>();
                var raw = settings.RecentCommandIds;
                if (string.IsNullOrEmpty(raw)) return new List<string>();
                return raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch { return new List<string>(); }
        }
    }
}
