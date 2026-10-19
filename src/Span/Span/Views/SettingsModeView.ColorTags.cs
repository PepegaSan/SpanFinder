using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Span.Helpers;
using Span.Services;
using System;
using System.Linq;

namespace Span.Views;

/// <summary>
/// Einstellungen → Durchsuchen → Farb-Tags: Name und Farbe jedes Tags ändern, Reihenfolge, löschen, neue Tags.
/// Die Reihenfolge gilt für das Kontextmenü und die Gruppierung nach Farb-Tag.
/// </summary>
public sealed partial class SettingsModeView
{
    // Farben für neue Tags (der Reihe nach, danach von vorn)
    private static readonly string[] NewTagColors =
        { "#00B7C3", "#E3008C", "#8E562E", "#498205", "#0063B1", "#C239B3", "#CA5010", "#038387" };

    private void LocalizeColorTags()
    {
        if (_loc == null) return;
        ColorTagsLabel.Text = _loc.Get("Settings_ColorTags");
        ColorTagsDesc.Text = _loc.Get("Settings_ColorTagsDesc");
        ColorTagAddButton.Content = _loc.Get("ColorTag_Add");
        ColorTagResetButton.Content = _loc.Get("ColorTag_Reset");
        RebuildColorTagList();
    }

    /// <summary>Einstellungen bei den Farb-Tags öffnen (aus dem Kontextmenü "Tags bearbeiten…").</summary>
    public void ShowColorTags()
    {
        SelectNavItem(NavBrowsing);
        ShowSection("Browsing");
        RebuildColorTagList();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ColorTagsCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }));
    }

    private void RebuildColorTagList()
    {
        ColorTagListPanel.Children.Clear();
        var catalog = ColorTagCatalog.Current;
        for (int i = 0; i < catalog.Tags.Count; i++)
            ColorTagListPanel.Children.Add(BuildColorTagRow(catalog.Tags[i], i, catalog.Tags.Count));
    }

    private Grid BuildColorTagRow(ColorTagDefinition tag, int index, int count)
    {
        var catalog = ColorTagCatalog.Current;
        var id = tag.Id;
        var row = new Grid { ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(240), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        // Farbe: Knopf mit Farbfläche, Farbauswahl im Flyout
        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ColorTagBrushes.Create(tag.Color),
        };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        ColorTagCatalog.TryParseColor(tag.Color, out var r, out var g, out var b);
        picker.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        var flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Bottom };
        var colorButton = new Button
        {
            Content = swatch,
            Padding = new Thickness(6),
            Flyout = flyout,
        };
        ToolTipService.SetToolTip(colorButton, _loc?.Get("ColorTag_Color"));

        // Name: übernommen bei Enter oder beim Verlassen des Felds
        var nameBox = new TextBox
        {
            Text = catalog.DisplayName(tag),
            PlaceholderText = ColorTagCatalog.IsBuiltIn(id) ? LocalizationService.L("ColorTag_" + id) : string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameBox, 1);

        string CurrentColor() => $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        void Commit()
        {
            var current = catalog.Find(id);
            if (current == null) return;
            var color = CurrentColor();
            if (nameBox.Text.Trim() == catalog.DisplayName(current) && string.Equals(color, current.Color, StringComparison.OrdinalIgnoreCase))
                return;
            catalog.Update(id, nameBox.Text, color);
            if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = catalog.DisplayName(current);
        }

        flyout.Closed += (_, _) =>
        {
            swatch.Background = ColorTagBrushes.Create(CurrentColor());
            Commit();
        };
        nameBox.LostFocus += (_, _) => Commit();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; }
        };

        var up = SmallIconButton("", "ColorTag_MoveUp", index > 0, () => { catalog.Move(id, index - 1); RebuildColorTagList(); });
        Grid.SetColumn(up, 2);
        var down = SmallIconButton("", "ColorTag_MoveDown", index < count - 1, () => { catalog.Move(id, index + 1); RebuildColorTagList(); });
        Grid.SetColumn(down, 3);
        var delete = SmallIconButton("", "ColorTag_Delete", true, () => _ = DeleteColorTagAsync(id));
        Grid.SetColumn(delete, 4);

        row.Children.Add(colorButton);
        row.Children.Add(nameBox);
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(delete);
        return row;
    }

    private Button SmallIconButton(string glyph, string tooltipKey, bool enabled, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 6, 8, 6),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, _loc?.Get(tooltipKey));
        button.Click += (_, _) => action();
        return button;
    }

    private void OnColorTagAddClick(object sender, RoutedEventArgs e)
    {
        var catalog = ColorTagCatalog.Current;
        if (catalog.Tags.Count >= ColorTagCatalog.MaxTags) return;

        var used = catalog.Tags.Select(t => t.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = NewTagColors.FirstOrDefault(c => !used.Contains(c)) ?? NewTagColors[catalog.Tags.Count % NewTagColors.Length];
        catalog.Add(LocalizationService.L("ColorTag_NewName"), color);
        RebuildColorTagList();

        // Namen gleich bearbeiten lassen
        if (ColorTagListPanel.Children.LastOrDefault() is Grid row && row.Children.OfType<TextBox>().FirstOrDefault() is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void OnColorTagResetClick(object sender, RoutedEventArgs e)
    {
        ColorTagCatalog.Current.ResetBuiltIns();
        RebuildColorTagList();
    }

    private async System.Threading.Tasks.Task DeleteColorTagAsync(string id)
    {
        try
        {
            var catalog = ColorTagCatalog.Current;
            var tag = catalog.Find(id);
            if (tag == null) return;

            var annotations = App.Current.Services.GetService<ItemAnnotationService>();
            int usage = annotations?.GetTaggedPaths().Count(p => string.Equals(p.Value, id, StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (usage > 0)
            {
                var dialog = new ContentDialog
                {
                    Title = _loc?.Get("ColorTag_Delete"),
                    Content = string.Format(LocalizationService.L("ColorTag_DeleteConfirm"), catalog.DisplayName(tag), usage),
                    PrimaryButtonText = _loc?.Get("ColorTag_Delete"),
                    CloseButtonText = _loc?.Get("Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            catalog.Remove(id);
            if (annotations != null && usage > 0)
                await annotations.RemoveColorTagEverywhereAsync(id);
            RebuildColorTagList();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[Settings] Delete color tag failed: {ex.Message}");
        }
    }
}
