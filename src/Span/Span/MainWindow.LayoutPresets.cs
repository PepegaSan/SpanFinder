using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Span.Models;
using Span.ViewModels;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// Layout-Vorlagen (max. 4): speichern und abrufen den Ansichtszustand — Split-Modus, Vorschau-Panels,
    /// "Ältere Spalten einklappen" und Spaltenbreite.
    ///
    /// Fokus-Anker: Zeigt eine Vorlage weniger Bereiche als das Vierfach-Layout (Einzelspalte, zwei Bereiche),
    /// übernimmt sie nicht stur die Bereiche oben links/unten links, sondern den Teil, in dem gerade der Fokus liegt.
    /// Dazu tauschen die betroffenen Explorer ihre Pfade; beim nächsten Vorlagenwechsel wird das rückgängig gemacht,
    /// sodass jedes Vierfach-Feld wieder seinen Ordner zeigt.
    /// </summary>
    public sealed partial class MainWindow
    {
        private sealed class LayoutPreset
        {
            public int Mode { get; set; }
            public bool LeftPreview { get; set; }
            public bool RightPreview { get; set; }
            public bool CollapseColumns { get; set; }
            public int ColumnWidth { get; set; }
            public int? ViewMode { get; set; }             // Explorer-Ansicht (Miller/Details/Liste/Symbole); null = unverändert
            public double LeftPreviewWidth { get; set; }   // 0 = nicht gespeichert
            public double RightPreviewWidth { get; set; }
        }

        // Aktuell wirksame Pfad-Tausche (in Anwendungsreihenfolge), damit sie rückgängig gemacht werden können.
        private readonly List<(ActivePane a, ActivePane b)> _anchorSwaps = new();
        private bool _presetBusy;
        private ActivePane? _lastPresetOrigin;
        private ActivePane _lastPresetSlot = ActivePane.Left;
        private DateTime _lastPresetUtc = DateTime.MinValue;

        private static bool IsExplorerViewMode(Models.ViewMode m) =>
            m is Models.ViewMode.MillerColumns or Models.ViewMode.Details or Models.ViewMode.List
                or Models.ViewMode.IconSmall or Models.ViewMode.IconMedium
                or Models.ViewMode.IconLarge or Models.ViewMode.IconExtraLarge;

        private static string LayoutPresetKey(int slot) => $"LayoutPreset{slot}";

        private void SaveLayoutPreset(int slot)
        {
            if (ViewModel.IsRecycleBinTab) return;
            var preset = new LayoutPreset
            {
                Mode = (int)ViewModel.SplitLayoutMode,
                LeftPreview = ViewModel.IsLeftPreviewEnabled,
                RightPreview = ViewModel.IsSplitViewEnabled && !ViewModel.IsQuadSplit && ViewModel.IsRightPreviewEnabled,
                CollapseColumns = _settings.CollapseInactiveColumns,
                ColumnWidth = _settings.MillerColumnWidth,
                ViewMode = IsExplorerViewMode(ViewModel.CurrentViewMode) ? (int)ViewModel.CurrentViewMode : null,
                // Breiten nur mitnehmen, solange das jeweilige Panel sichtbar ist (sonst ist die Spalte 0).
                LeftPreviewWidth = ViewModel.IsLeftPreviewEnabled ? LeftPreviewCol.Width.Value : 0,
                RightPreviewWidth = ViewModel.IsRightPreviewEnabled ? RightPreviewCol.Width.Value : 0,
            };
            _settings.Set(LayoutPresetKey(slot), JsonSerializer.Serialize(preset));
            ViewModel.ShowToast(string.Format(_loc.Get("Layout_PresetSaved"), slot));
        }

        private void ApplyLayoutPreset(int slot)
        {
            if (ViewModel.IsRecycleBinTab || _presetBusy) return;
            _ = ApplyLayoutPresetAsync(slot);
        }

        private async Task ApplyLayoutPresetAsync(int slot)
        {
            _presetBusy = true;
            try
            {
                LayoutPreset? preset = null;
                try
                {
                    var json = _settings.Get(LayoutPresetKey(slot), "");
                    if (!string.IsNullOrEmpty(json))
                        preset = JsonSerializer.Deserialize<LayoutPreset>(json);
                }
                catch (JsonException) { }

                if (preset == null)
                {
                    ViewModel.ShowToast(string.Format(_loc.Get("Layout_PresetEmpty"), slot));
                    return;
                }

                var target = Enum.IsDefined(typeof(SplitLayoutMode), preset.Mode)
                    ? (SplitLayoutMode)preset.Mode
                    : SplitLayoutMode.Single;

                // 0) Fokus-Anker: Vierfach-Feld, dessen Inhalt gerade den Fokus hat; vorherige Tausche zurücknehmen.
                var focusOrigin = ResolveFocusOrigin();
                Helpers.DebugLogger.Log($"[LayoutPreset] apply slot={slot} target={target} current={ViewModel.SplitLayoutMode} activePane={ViewModel.ActivePane} focusOrigin={focusOrigin} pendingSwaps={_anchorSwaps.Count}");
                await UndoAnchorSwapsAsync();
                if (_isClosed) return;

                // 0b) Für Layouts mit weniger Bereichen den Teil mit dem Fokus nach vorn holen.
                await ApplyAnchorSwapsAsync(target, focusOrigin);
                if (_isClosed) return;

                // 1) Split-Modus — über dieselben Pfade wie die Toolbar-Buttons, damit Explorer-Zustände erhalten bleiben.
                var current = ViewModel.SplitLayoutMode;
                if (target != current)
                {
                    if (target == SplitLayoutMode.Single)
                    {
                        ToggleSplitView();
                    }
                    else if (target == SplitLayoutMode.Quad)
                    {
                        OnQuadSplitClick(this, null!);
                    }
                    else
                    {
                        if (current == SplitLayoutMode.Single)
                            ToggleSplitView();
                        if (ViewModel.SplitLayoutMode != target)
                        {
                            ViewModel.SetSplitLayoutMode(target);
                            ApplySplitLayout();
                        }
                    }
                    UpdateSplitViewButtonState();
                    UpdateSplitOrientationButtonState();
                    UpdateQuadSplitButtonState();
                    UpdateViewModeVisibility();
                }

                // Ansichtsmodus der Vorlage (Miller/Details/Liste/Symbole) — nur zwischen normalen Explorer-Ansichten.
                if (preset.ViewMode is int vm && Enum.IsDefined(typeof(Models.ViewMode), vm))
                {
                    var wantedMode = (Models.ViewMode)vm;
                    if (IsExplorerViewMode(wantedMode) && IsExplorerViewMode(ViewModel.CurrentViewMode)
                        && ViewModel.CurrentViewMode != wantedMode)
                        ExecuteSwitchViewMode(wantedMode);
                }

                // Fokus dorthin, wo der Inhalt mit dem Fokus jetzt liegt.
                var focusSlot = FindSlotShowing(focusOrigin, target);
                ViewModel.ActivePane = focusSlot;
                _lastPresetOrigin = focusOrigin;
                _lastPresetSlot = focusSlot;
                _lastPresetUtc = DateTime.UtcNow;

                // 2) Vorschau-Panels (Wechsel in/aus Split schließt sie — daher erst danach setzen).
                bool wantRight = preset.RightPreview && ViewModel.IsSplitViewEnabled && !ViewModel.IsQuadSplit;
                bool wantLeft = preset.LeftPreview && !ViewModel.IsQuadSplit;
                if (ViewModel.IsLeftPreviewEnabled != wantLeft)
                    TogglePreviewForPane(ActivePane.Left);
                if (ViewModel.IsRightPreviewEnabled != wantRight && ViewModel.IsSplitViewEnabled && !ViewModel.IsQuadSplit)
                    TogglePreviewForPane(ActivePane.Right);
                // Breite des Vorschau-Panels aus der Vorlage übernehmen (sonst springt es auf die zuletzt
                // beim Beenden gespeicherte Breite zurück).
                if (preset.LeftPreviewWidth >= 100)
                {
                    _lastLeftPreviewWidth = preset.LeftPreviewWidth;
                    if (ViewModel.IsLeftPreviewEnabled)
                        LeftPreviewCol.Width = new GridLength(preset.LeftPreviewWidth, GridUnitType.Pixel);
                }
                if (preset.RightPreviewWidth >= 100)
                {
                    _lastRightPreviewWidth = preset.RightPreviewWidth;
                    if (ViewModel.IsRightPreviewEnabled)
                        RightPreviewCol.Width = new GridLength(preset.RightPreviewWidth, GridUnitType.Pixel);
                }
                UpdatePreviewButtonState();

                // 3) Spaltenbreite und Einklappen
                if (preset.ColumnWidth >= MillerColumnMinWidth && preset.ColumnWidth != _settings.MillerColumnWidth)
                {
                    _settings.MillerColumnWidth = preset.ColumnWidth;
                    ApplyPersistedMillerColumnWidthsAll();
                }
                _settings.CollapseInactiveColumns = preset.CollapseColumns;
                ApplyColumnCollapseAll();

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_isClosed) return;
                    // Layout-Ereignisse (GotFocus der neu sichtbaren Bereiche) können ActivePane umgesetzt haben.
                    ViewModel.ActivePane = focusSlot;
                    FocusActivePane();
                });
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[LayoutPreset] apply failed: {ex.Message}");
            }
            finally
            {
                _presetBusy = false;
            }
        }

        // ── Fokus-Anker ───────────────────────────────────────

        /// <summary>Welches Inhalts-Feld liegt in <paramref name="pane"/>, nachdem alle Tausche angewendet wurden?</summary>
        private Dictionary<ActivePane, ActivePane> ComputeContentMap()
        {
            var map = new Dictionary<ActivePane, ActivePane>
            {
                [ActivePane.Left] = ActivePane.Left,
                [ActivePane.Right] = ActivePane.Right,
                [ActivePane.TopRight] = ActivePane.TopRight,
                [ActivePane.BottomRight] = ActivePane.BottomRight,
            };
            foreach (var (a, b) in _anchorSwaps)
                (map[a], map[b]) = (map[b], map[a]);
            return map;
        }

        /// <summary>Vierfach-Feld (Herkunft) des Inhalts im gerade aktiven Bereich.</summary>
        private ActivePane ResolveFocusOrigin()
        {
            // Direkt nach einem Vorlagenwechsel gilt der dort gesetzte Fokus: neu sichtbare Bereiche melden beim
            // Einblenden selbst GotFocus und würden bei schnellem Weiterschalten den Fokus verfälschen.
            if (_lastPresetOrigin.HasValue && (DateTime.UtcNow - _lastPresetUtc).TotalMilliseconds < 2000)
                return _lastPresetOrigin.Value;

            var map = ComputeContentMap();
            return map.TryGetValue(ViewModel.ActivePane, out var origin) ? origin : ViewModel.ActivePane;
        }

        /// <summary>Bereich, der nach dem Wechsel den Inhalt von <paramref name="origin"/> zeigt.</summary>
        private ActivePane FindSlotShowing(ActivePane origin, SplitLayoutMode layout)
        {
            if (layout == SplitLayoutMode.Quad) return origin;
            foreach (var kvp in ComputeContentMap())
            {
                if (kvp.Value == origin && (kvp.Key == ActivePane.Left || (kvp.Key == ActivePane.Right && layout != SplitLayoutMode.Single)))
                    return kvp.Key;
            }
            return ActivePane.Left;
        }

        private static bool IsUsablePath(string? path) => !string.IsNullOrEmpty(path) && path != "PC";

        /// <summary>Tauscht die Ordner zweier Explorer. false, wenn einer keinen verwertbaren Pfad hat.</summary>
        private async Task<bool> SwapExplorerPathsAsync(ActivePane a, ActivePane b)
        {
            var ea = ViewModel.GetExplorerForPane(a);
            var eb = ViewModel.GetExplorerForPane(b);
            string? pa = ea.CurrentPath, pb = eb.CurrentPath;
            Helpers.DebugLogger.Log($"[LayoutPreset] swap {a} <-> {b}");
            if (!IsUsablePath(pa) || !IsUsablePath(pb))
            {
                Helpers.DebugLogger.Log("[LayoutPreset] swap skipped (path not usable)");
                return false;
            }
            if (!string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase))
            {
                await ea.NavigateToPath(pb!);
                await eb.NavigateToPath(pa!);
            }
            return true;
        }

        private bool _undoOnCloseRunning;

        /// <summary>Schließen wurde zurückgestellt: erst Tausche rückgängig machen, dann erneut schließen.</summary>
        private async Task UndoSwapsThenCloseAsync()
        {
            _undoOnCloseRunning = true;
            try { await UndoAnchorSwapsAsync(); }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[LayoutPreset] undo on close failed: {ex.Message}"); }
            finally
            {
                _anchorSwaps.Clear(); // auch bei Fehler — sonst ließe sich das Fenster nicht mehr schließen
                _undoOnCloseRunning = false;
                DispatcherQueue.TryEnqueue(() => { try { Close(); } catch { } });
            }
        }

        private async Task UndoAnchorSwapsAsync()
        {
            for (int i = _anchorSwaps.Count - 1; i >= 0; i--)
            {
                var (a, b) = _anchorSwaps[i];
                await SwapExplorerPathsAsync(a, b);
            }
            _anchorSwaps.Clear();
        }

        private async Task ApplyAnchorSwapsAsync(SplitLayoutMode target, ActivePane f)
        {
            // Home/Einstellungen/Papierkorb gehören nur zum linken Bereich — nichts verschieben.
            var mode = ViewModel.CurrentViewMode;
            if (mode is Models.ViewMode.Home or Models.ViewMode.Settings or Models.ViewMode.ActionLog or Models.ViewMode.RecycleBin)
                return;

            var wanted = new List<(ActivePane a, ActivePane b)>();
            switch (target)
            {
                case SplitLayoutMode.Single:
                    if (f != ActivePane.Left) wanted.Add((ActivePane.Left, f));
                    break;

                case SplitLayoutMode.DualStacked:
                    // Linke Hälfte (Left oben, Right unten) passt schon; rechte Hälfte nach vorn holen.
                    if (f is ActivePane.TopRight or ActivePane.BottomRight)
                    {
                        wanted.Add((ActivePane.Left, ActivePane.TopRight));
                        wanted.Add((ActivePane.Right, ActivePane.BottomRight));
                    }
                    break;

                case SplitLayoutMode.DualSideBySide:
                    if (f is ActivePane.Left or ActivePane.TopRight)
                    {
                        // Obere Zeile: Left bleibt, rechts kommt TopRight.
                        if (f == ActivePane.TopRight) wanted.Add((ActivePane.Right, ActivePane.TopRight));
                    }
                    else
                    {
                        // Untere Zeile: links Right, rechts BottomRight.
                        wanted.Add((ActivePane.Left, ActivePane.Right));
                        wanted.Add((ActivePane.Right, ActivePane.BottomRight));
                    }
                    break;
            }

            foreach (var (a, b) in wanted)
            {
                if (await SwapExplorerPathsAsync(a, b))
                    _anchorSwaps.Add((a, b));
            }
        }

        /// <summary>Wendet die Einklapp-Logik auf alle Miller-Bereiche an (nach Preset- oder Optionswechsel).</summary>
        private void ApplyColumnCollapseAll()
        {
            foreach (var kvp in _tabMillerPanels)
                ApplyColumnCollapse(kvp.Value.items);
            ItemsControl[] fixedControls = { MillerColumnsControl, MillerColumnsControlRight, MillerColumnsControlTopRight, MillerColumnsControlBottomRight };
            foreach (var c in fixedControls)
                if (c != null) ApplyColumnCollapse(c);
        }
    }
}
