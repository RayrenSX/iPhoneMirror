using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestSettingsScrollbarAlignment(MainWindow window, ScrollViewer viewer)
    {
        viewer.ApplyTemplate(); window.UpdateLayout();
        var presenter = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
        var content = (FrameworkElement)viewer.Content;
        var frame = (Border)window.FindName("ControlPanelContent");
        var bounds = content.TransformToAncestor(frame).TransformBounds(new Rect(content.RenderSize));
        var left = bounds.Left;
        var right = frame.ActualWidth - bounds.Right;
        InteractionAssert(Math.Abs(left - right) <= 1.5,
            $"{viewer.Name}: content must have symmetrical frame insets, left={left}, right={right}.");
        InteractionAssert(Math.Abs(content.ActualWidth - presenter.ActualWidth) <= 1,
            $"{viewer.Name}: content does not fill the viewport.");
        var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        if (!bar.IsVisible || viewer.ScrollableHeight <= 0) return;
        var barBounds = bar.TransformToAncestor(frame).TransformBounds(new Rect(bar.RenderSize));
        InteractionAssert(barBounds.Left >= bounds.Right + 3 && barBounds.Right <= frame.ActualWidth,
            $"{viewer.Name}: scrollbar must be in a separate gutter to the right of aligned content: content={bounds}, bar={barBounds}, frame={frame.ActualWidth}.");
        viewer.ScrollToHome(); window.UpdateLayout();
        bar.ApplyTemplate();
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        var thumb = track.Thumb;
        thumb.ApplyTemplate(); window.UpdateLayout();
        InteractionAssert(thumb.ActualWidth >= 9,
            $"{viewer.Name}: the thin visible grip must retain a wide invisible drag target.");
        foreach (var x in new[] { 1d, thumb.ActualWidth / 2, thumb.ActualWidth - 1 })
        {
            var point = thumb.TranslatePoint(new Point(x, thumb.ActualHeight / 2), window);
            var hit = window.InputHitTest(point) as DependencyObject;
            var ownsHit = false;
            for (var current = hit; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, thumb)) { ownsHit = true; break; }
            InteractionAssert(ownsHit, $"{viewer.Name}: scrollbar thumb cannot be clicked at x={x}.");
        }
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 24, false) { RoutedEvent = Thumb.DragCompletedEvent });
        window.UpdateLayout();
        InteractionAssert(viewer.VerticalOffset > 0, $"{viewer.Name}: dragging the thumb did not scroll its viewer.");
        viewer.ScrollToHome(); window.UpdateLayout();
        ScrollBar.PageDownCommand.Execute(null, bar);
        window.UpdateLayout();
        InteractionAssert(viewer.VerticalOffset > 0, $"{viewer.Name}: clicking the scrollbar track did not page down.");
        viewer.ScrollToHome(); window.UpdateLayout();
    }
}
