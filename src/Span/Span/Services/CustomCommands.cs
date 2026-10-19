using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Span.Services
{
    /// <summary>Ein eigener Befehl: Button in der Toolbar, der ein Programm mit der Auswahl startet.</summary>
    public sealed class CustomCommand
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = string.Empty;
        /// <summary>Programm oder Skript (Umgebungsvariablen wie %USERPROFILE% erlaubt).</summary>
        public string Program { get; set; } = string.Empty;
        /// <summary>Argumente mit Platzhaltern, siehe <see cref="CustomCommands.BuildArguments"/>.</summary>
        public string Arguments { get; set; } = "{files}";
        /// <summary>Für jede ausgewählte Datei einzeln starten (sonst einmal mit allen).</summary>
        public bool PerFile { get; set; }
        /// <summary>Segoe-Fluent-Symbol des Buttons; leer = Name als Text.</summary>
        public string Icon { get; set; } = "\uE768";
    }

    /// <summary>
    /// Eigene Befehle (Einstellungen → Werkzeuge): Speichern als JSON und Aufbau der Kommandozeile.
    /// Platzhalter: {file} erste Auswahl, {files} alle Auswahlen, {folder} aktueller Ordner (jeweils in
    /// Anführungszeichen), {name} Dateiname ohne Endung, {ext} Endung ohne Punkt (beide ohne Anführungszeichen).
    /// </summary>
    public static class CustomCommands
    {
        /// <summary>Ab so vielen Einzelstarts fragt Span nach (PerFile mit großer Auswahl).</summary>
        public const int ConfirmPerFileAbove = 20;

        private static readonly string[] SelectionPlaceholders = { "{file}", "{files}", "{name}", "{ext}" };

        public static List<CustomCommand> Load(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<CustomCommand>();
            try
            {
                var list = JsonSerializer.Deserialize<List<CustomCommand>>(json) ?? new List<CustomCommand>();
                return list.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id)).ToList();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[CustomCommands] load failed: {ex.Message}");
                return new List<CustomCommand>();
            }
        }

        public static string ToJson(IEnumerable<CustomCommand> commands) => JsonSerializer.Serialize(commands.ToList());

        /// <summary>Braucht der Befehl eine Auswahl (Platzhalter für Dateien in den Argumenten)?</summary>
        public static bool NeedsSelection(CustomCommand command)
            => SelectionPlaceholders.Any(p => (command.Arguments ?? string.Empty).Contains(p, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Pfad für die Kommandozeile in Anführungszeichen. Ein abschließender Backslash wird verdoppelt,
        /// sonst läse CommandLineToArgvW "C:\" als maskiertes Anführungszeichen.
        /// </summary>
        public static string Quote(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\"\"";
            var p = path.EndsWith('\\') ? path + "\\" : path;
            return "\"" + p + "\"";
        }

        public static string BuildArguments(string? template, IReadOnlyList<string> files, string? folder)
        {
            var first = files.Count > 0 ? files[0] : null;
            var sb = new StringBuilder(template ?? string.Empty);
            sb.Replace("{files}", string.Join(" ", files.Select(Quote)));
            sb.Replace("{file}", first != null ? Quote(first) : string.Empty);
            sb.Replace("{folder}", !string.IsNullOrEmpty(folder) ? Quote(folder) : string.Empty);
            sb.Replace("{name}", first != null ? System.IO.Path.GetFileNameWithoutExtension(first) : string.Empty);
            sb.Replace("{ext}", first != null ? System.IO.Path.GetExtension(first).TrimStart('.') : string.Empty);
            return sb.ToString().Trim();
        }

        /// <summary>Die einzelnen Aufrufe: bei PerFile einer je Datei, sonst einer mit allen.</summary>
        public static List<string> PlanInvocations(CustomCommand command, IReadOnlyList<string> files, string? folder)
        {
            if (command.PerFile && files.Count > 1)
                return files.Select(f => BuildArguments(command.Arguments, new[] { f }, folder)).ToList();
            return new List<string> { BuildArguments(command.Arguments, files, folder) };
        }
    }
}
