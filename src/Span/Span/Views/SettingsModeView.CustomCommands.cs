using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Span.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Span.Views;

/// <summary>
/// Einstellungen → Werkzeuge → Eigene Befehle: Name, Symbol, Programm, Argumente (Platzhalter), einzeln pro Datei.
/// Jeder Befehl wird ein Button in der Toolbar (MainWindow.CustomCommands.cs). Gespeichert bei jeder Änderung.
/// </summary>
public sealed partial class SettingsModeView
{
    /// <summary>Wählbare Symbole (Segoe Fluent Icons); leer = Name als Text im Button.</summary>
    private static readonly string[] CommandIcons =
    {
        "\uE768", "\uE714", "\uE8B9", "\uE8D6", "\uE756", "\uE70F", "\uE8C8", "\uE896",
        "\uE898", "\uE8A5", "\uE945", "\uE734", "\uE7C3", "\uE9F5", "",
    };

    private List<CustomCommand> _customCommands = new();

    private void LocalizeCustomCommands()
    {
        if (_loc == null) return;
        CustomCommandsLabel.Text = _loc.Get("Settings_CustomCommands");
        CustomCommandsDesc.Text = _loc.Get("Settings_CustomCommandsDesc");
        CustomCommandAddButton.Content = _loc.Get("Cmd_Add");
        RebuildCustomCommandList();
    }

    private void SaveCustomCommands() => _settings.CustomCommandsJson = CustomCommands.ToJson(_customCommands);

    private void RebuildCustomCommandList()
    {
        _customCommands = CustomCommands.Load(_settings.CustomCommandsJson);
        CustomCommandListPanel.Children.Clear();
        for (int i = 0; i < _customCommands.Count; i++)
            CustomCommandListPanel.Children.Add(BuildCustomCommandRow(_customCommands[i], i));
    }

    private Border BuildCustomCommandRow(CustomCommand command, int index)
    {
        string L(string key) => _loc?.Get(key) ?? key;

        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Zeile 0: Symbol, Name, Reihenfolge, Löschen
        var iconBox = new ComboBox { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center };
        foreach (var glyph in CommandIcons)
        {
            iconBox.Items.Add(glyph.Length == 0
                ? new TextBlock { Text = "Aa", FontSize = 12 }
                : new FontIcon { Glyph = glyph, FontSize = 14 });
        }
        int iconIndex = Array.IndexOf(CommandIcons, command.Icon ?? string.Empty);
        iconBox.SelectedIndex = iconIndex >= 0 ? iconIndex : 0;
        ToolTipService.SetToolTip(iconBox, L("Cmd_Icon"));
        iconBox.SelectionChanged += (_, _) =>
        {
            if (iconBox.SelectedIndex < 0) return;
            command.Icon = CommandIcons[iconBox.SelectedIndex];
            SaveCustomCommands();
        };

        var nameBox = new TextBox { Text = command.Name, PlaceholderText = L("Cmd_Name") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, L("Cmd_Name"));
        nameBox.LostFocus += (_, _) =>
        {
            if (command.Name == nameBox.Text.Trim()) return;
            command.Name = nameBox.Text.Trim();
            SaveCustomCommands();
        };
        Grid.SetColumn(nameBox, 1);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(SmallIconButton("\uE70E", "ColorTag_MoveUp", index > 0, () => MoveCustomCommand(index, -1)));
        actions.Children.Add(SmallIconButton("\uE70D", "ColorTag_MoveDown", index < _customCommands.Count - 1, () => MoveCustomCommand(index, 1)));
        actions.Children.Add(SmallIconButton("\uE74D", "Cmd_Delete", true, () =>
        {
            _customCommands.Remove(command);
            SaveCustomCommands();
            RebuildCustomCommandList();
        }));
        Grid.SetColumn(actions, 2);

        // Zeile 1: Programm + Durchsuchen
        var programLabel = RowLabel(L("Cmd_Program"), 1);
        var programBox = new TextBox { Text = command.Program, PlaceholderText = @"C:\ffmpeg\ffmpeg.exe" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(programBox, L("Cmd_Program"));
        programBox.LostFocus += (_, _) =>
        {
            if (command.Program == programBox.Text.Trim()) return;
            command.Program = programBox.Text.Trim();
            SaveCustomCommands();
        };
        Grid.SetRow(programBox, 1);
        Grid.SetColumn(programBox, 1);
        var browse = new Button { Content = L("Cmd_Browse"), VerticalAlignment = VerticalAlignment.Center };
        browse.Click += async (_, _) =>
        {
            var path = await PickProgramAsync();
            if (path == null) return;
            programBox.Text = path;
            command.Program = path;
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                command.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                nameBox.Text = command.Name;
            }
            SaveCustomCommands();
        };
        Grid.SetRow(browse, 1);
        Grid.SetColumn(browse, 2);

        // Zeile 2: Argumente
        var argsLabel = RowLabel(L("Cmd_Arguments"), 2);
        var argsBox = new TextBox { Text = command.Arguments, PlaceholderText = "{files}" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(argsBox, L("Cmd_Arguments"));
        argsBox.LostFocus += (_, _) =>
        {
            if (command.Arguments == argsBox.Text) return;
            command.Arguments = argsBox.Text;
            SaveCustomCommands();
        };
        Grid.SetRow(argsBox, 2);
        Grid.SetColumn(argsBox, 1);
        Grid.SetColumnSpan(argsBox, 2);

        // Zeile 3: einzeln pro Datei
        var perFile = new CheckBox { Content = L("Cmd_PerFile"), IsChecked = command.PerFile };
        perFile.Click += (_, _) =>
        {
            command.PerFile = perFile.IsChecked == true;
            SaveCustomCommands();
        };
        Grid.SetRow(perFile, 3);
        Grid.SetColumn(perFile, 1);
        Grid.SetColumnSpan(perFile, 2);

        grid.Children.Add(iconBox);
        grid.Children.Add(nameBox);
        grid.Children.Add(actions);
        grid.Children.Add(programLabel);
        grid.Children.Add(programBox);
        grid.Children.Add(browse);
        grid.Children.Add(argsLabel);
        grid.Children.Add(argsBox);
        grid.Children.Add(perFile);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = GetThemeBrush("SpanBorderSubtleBrush"),
        };
    }

    private static TextBlock RowLabel(string text, int row)
    {
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        Grid.SetRow(label, row);
        return label;
    }

    private void MoveCustomCommand(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= _customCommands.Count) return;
        (_customCommands[index], _customCommands[target]) = (_customCommands[target], _customCommands[index]);
        SaveCustomCommands();
        RebuildCustomCommandList();
    }

    private void OnCustomCommandAddClick(object sender, RoutedEventArgs e)
    {
        _customCommands.Add(new CustomCommand { Name = _loc?.Get("Cmd_NewName") ?? "New command" });
        SaveCustomCommands();
        RebuildCustomCommandList();

        // Namen gleich bearbeiten lassen
        if (CustomCommandListPanel.Children.LastOrDefault() is Border { Child: Grid grid }
            && grid.Children.OfType<TextBox>().FirstOrDefault() is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private async System.Threading.Tasks.Task<string?> PickProgramAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".ps1");
            picker.FileTypeFilter.Add("*");

            var windows = ((App)App.Current).GetRegisteredWindows();
            if (windows.Count == 0) return null;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(windows[0]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Helpers.DebugLogger.Log($"[Settings] Pick program failed: {ex.Message}");
            return null;
        }
    }
}
