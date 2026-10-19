using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Span.Services;

namespace Span
{
    /// <summary>
    /// Eigene Befehle als Toolbar-Buttons (Einstellungen → Werkzeuge → Eigene Befehle): starten ein Programm oder
    /// Skript mit der Auswahl des aktiven Bereichs, z. B. "mit ffmpeg umwandeln" oder "in Q-cut öffnen".
    /// </summary>
    public sealed partial class MainWindow
    {
        private void RebuildCustomCommandsBar()
        {
            if (_isClosed || CustomCommandsBar == null) return;
            CustomCommandsBar.Children.Clear();

            var commands = CustomCommands.Load(_settings.CustomCommandsJson)
                .Where(c => !string.IsNullOrWhiteSpace(c.Program))
                .ToList();
            if (commands.Count == 0) return;

            CustomCommandsBar.Children.Add(new Rectangle
            {
                Width = 1,
                Height = 20,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SpanBorderSubtleBrush"],
            });

            foreach (var command in commands)
            {
                var name = string.IsNullOrWhiteSpace(command.Name) ? System.IO.Path.GetFileNameWithoutExtension(command.Program) : command.Name;
                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["UnifiedButtonStyle"],
                    Content = string.IsNullOrEmpty(command.Icon)
                        ? new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
                        : new FontIcon { Glyph = command.Icon, FontSize = 15 },
                };
                ToolTipService.SetToolTip(button, name);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                var captured = command;
                button.Click += (_, _) => _ = RunCustomCommandAsync(captured, name);
                CustomCommandsBar.Children.Add(button);
            }
        }

        private async System.Threading.Tasks.Task RunCustomCommandAsync(CustomCommand command, string name)
        {
            try
            {
                // Nur lokale Pfade: Programme können mit Remote-/Archiv-Pfaden von Span nichts anfangen
                var files = GetCurrentSelectedItems()
                    .Select(i => i.Path)
                    .Where(p => !string.IsNullOrEmpty(p) && !FileSystemRouter.IsRemotePath(p) && !Helpers.ArchivePathHelper.IsArchivePath(p))
                    .ToList();
                var folder = ViewModel.ActiveExplorer?.CurrentFolder?.Path;
                if (folder != null && (FileSystemRouter.IsRemotePath(folder) || Helpers.ArchivePathHelper.IsArchivePath(folder)))
                    folder = null;

                if (files.Count == 0 && CustomCommands.NeedsSelection(command))
                {
                    ViewModel.ShowToast(string.Format(_loc.Get("Cmd_NoSelection"), name), isError: true);
                    return;
                }

                var invocations = CustomCommands.PlanInvocations(command, files, folder);
                if (invocations.Count > CustomCommands.ConfirmPerFileAbove)
                {
                    var dialog = new ContentDialog
                    {
                        Title = name,
                        Content = string.Format(_loc.Get("Cmd_ConfirmMany"), invocations.Count),
                        PrimaryButtonText = _loc.Get("Cmd_Run"),
                        CloseButtonText = _loc.Get("Cancel"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }

                var program = Environment.ExpandEnvironmentVariables(command.Program.Trim().Trim('"'));
                foreach (var args in invocations)
                {
                    var psi = new ProcessStartInfo(program, args) { UseShellExecute = true };
                    if (folder != null && Directory.Exists(folder)) psi.WorkingDirectory = folder;
                    Helpers.DebugLogger.Log($"[CustomCommand] {name}: \"{program}\" {args}");
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[CustomCommand] {name} failed: {ex.Message}");
                ViewModel.ShowToast(string.Format(_loc.Get("Cmd_Failed"), name, ex.Message), isError: true);
            }
        }
    }
}
