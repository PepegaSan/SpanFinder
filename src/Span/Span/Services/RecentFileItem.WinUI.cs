namespace Span.Services
{
    /// <summary>WinUI-abhängige Anzeige-Eigenschaften (Testprojekt verlinkt nur RecentFilesService.cs).</summary>
    public sealed partial class RecentFileItem
    {
        public string IconGlyph => IconService.Current?.GetIcon(System.IO.Path.GetExtension(Path)) ?? "";
        public Microsoft.UI.Xaml.Media.Brush? IconBrush => IconService.Current?.GetBrush(System.IO.Path.GetExtension(Path));
    }
}
