namespace Span.Models
{
    /// <summary>
    /// Split View에서 활성 패널 구분
    /// </summary>
    public enum ActivePane
    {
        Left,
        Right
    }

    /// <summary>Layout of the dual-pane split view.</summary>
    public enum SplitOrientation
    {
        /// <summary>Left and right panes (default).</summary>
        SideBySide = 0,

        /// <summary>Top and bottom panes.</summary>
        Stacked = 1,
    }
}
