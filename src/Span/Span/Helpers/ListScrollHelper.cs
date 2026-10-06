using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Span.Helpers;

internal static class ListScrollHelper
{
    /// <summary>
    /// ScrollIntoView only guarantees the item touches the viewport edge, and for rows that
    /// are not realized yet it scrolls to an estimated offset — the target often ended up
    /// half below the visible area. Like Explorer, scroll the list's own ScrollViewer so the
    /// realized row is fully visible with one row of breathing room above/below.
    ///
    /// StartBringIntoView is not used: the Miller host ScrollViewers mark every
    /// BringIntoViewRequested as handled (to stop horizontal jumps), which swallowed it.
    /// </summary>
    internal static void ScrollIntoViewWithMargin(ListViewBase list, object item)
    {
        if (list == null || item == null) return;

        list.ScrollIntoView(item);

        // The container exists only after the next layout pass.
        list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!AdjustWithMargin(list, item))
            {
                // Not realized yet → one more pass
                list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => AdjustWithMargin(list, item));
            }
        });
    }

    private static bool AdjustWithMargin(ListViewBase list, object item)
    {
        try
        {
            if (list.ContainerFromItem(item) is not FrameworkElement container) return false;
            var sv = VisualTreeHelpers.FindChild<ScrollViewer>(list);
            if (sv == null) return true;

            double h = container.ActualHeight;
            if (h <= 0) return false;

            double top = container.TransformToVisual(sv).TransformPoint(new Point(0, 0)).Y;
            double bottom = top + h;

            double viewTop = 0;
            double viewBottom = sv.ViewportHeight;

            // The list's viewport can extend under the window edge / overlays — clip to what
            // is actually on screen.
            if (list.XamlRoot?.Content is FrameworkElement root && root.ActualHeight > 0)
            {
                double svTopInRoot = sv.TransformToVisual(root).TransformPoint(new Point(0, 0)).Y;
                viewBottom = Math.Min(viewBottom, root.ActualHeight - svTopInRoot);
                viewTop = Math.Max(viewTop, -svTopInRoot);
            }

            double margin = h;
            double delta;
            if (bottom + margin > viewBottom)
                delta = bottom + margin - viewBottom;
            else if (top - margin < viewTop)
                delta = top - margin - viewTop;
            else
                return true;

            double target = Math.Clamp(sv.VerticalOffset + delta, 0, sv.ScrollableHeight);
            sv.ChangeView(null, target, null, true);
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ListScrollHelper] {ex.Message}");
            return true;
        }
    }
}
