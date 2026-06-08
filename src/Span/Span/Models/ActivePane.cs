namespace Span.Models
{
    /// <summary>
    /// Split View에서 활성 패널 구분.
    /// Left/Right = dual-pane; TopRight/BottomRight = quad-only slots.
    /// </summary>
    public enum ActivePane
    {
        /// <summary>Primary tab pane (top-left in quad).</summary>
        Left = 0,

        /// <summary>Secondary pane: top-right (side-by-side) or bottom-left (stacked/quad).</summary>
        Right = 1,

        /// <summary>Quad: top-right pane.</summary>
        TopRight = 2,

        /// <summary>Quad: bottom-right pane.</summary>
        BottomRight = 3,
    }

    /// <summary>Overall multi-pane layout mode.</summary>
    public enum SplitLayoutMode
    {
        Single = 0,
        DualSideBySide = 1,
        DualStacked = 2,
        Quad = 3,
    }

    /// <summary>Layout of the dual-pane split view (legacy; mapped from SplitLayoutMode).</summary>
    public enum SplitOrientation
    {
        SideBySide = 0,
        Stacked = 1,
    }
}
