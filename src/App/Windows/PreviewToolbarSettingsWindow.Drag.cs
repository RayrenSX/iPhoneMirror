using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IPhoneMirror.App.Windows;

public partial class PreviewToolbarSettingsWindow
{
    private string? _dragId;
    private List<string>? _dragOriginalOrder;
    private Point _dragStart, _dragOffset;
    private bool _dragging, _endingDrag, _capturingDrag;
    private FrameworkElement? _dragCard;
    private readonly Dictionary<string, (double Left, double Width, TranslateTransform Shift)> _dragSlots = [];

    private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !CanEdit || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PreviewList, source) is not ListBoxItem { Content: ToolbarRow row }) return;
        BeginToolbarDrag(row.Id, e.GetPosition(PreviewList));
        if (_dragId is null) return;
        // Capture synchronizes the mouse immediately. Ignore that reentrant
        // move until the press has finished arming its capture.
        _capturingDrag = true;
        try { if (!Mouse.Capture(PreviewList, CaptureMode.SubTree)) EndToolbarDrag(cancel: true); }
        finally { _capturingDrag = false; }
        e.Handled = true;
    }

    internal void BeginToolbarDrag(string id, Point position)
    {
        if (!CanEdit || !Draft.Items.Contains(id)) return;
        EndToolbarDrag(cancel: true);
        var row = PreviewList.Items.Cast<ToolbarRow>().Single(item => item.Id == id);
        PreviewList.SelectedItem = row;
        if (PreviewList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
        item.Focus();
        _dragId = id; _dragStart = position; _dragOriginalOrder = Draft.Items.ToList();
        var bounds = item.TransformToAncestor(EditorRoot).TransformBounds(new Rect(item.RenderSize));
        var pointer = PreviewList.TranslatePoint(position, EditorRoot);
        _dragOffset = new Point(pointer.X - bounds.Left, pointer.Y - bounds.Top);
        DragPreview.Width = bounds.Width; DragPreview.Height = bounds.Height;
        _dragCard = item;
        foreach (ToolbarRow card in PreviewList.Items)
        {
            if (PreviewList.ItemContainerGenerator.ContainerFromItem(card) is not FrameworkElement container) continue;
            var shift = new TranslateTransform(); container.RenderTransform = shift;
            _dragSlots[card.Id] = (container.TranslatePoint(new Point(), PreviewList).X, container.ActualWidth + container.Margin.Left + container.Margin.Right, shift);
        }
    }

    private void OnToolbarMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragId is null) return;
        e.Handled = true; // The row owns this gesture, including capture synchronization.
        if (_capturingDrag) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndToolbarDrag(cancel: true); return; }
        UpdateToolbarDrag(e.GetPosition(PreviewList));
    }

    internal void UpdateToolbarDrag(Point position)
    {
        if (_dragId is not { } id || !CanEdit) return;
        if (!_dragging && Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_dragging) DragPreview.Source = _dragCard is { } card ? CaptureFocusVisual(card) : null;
        _dragging = true;
        SelectionTools.Visibility = Visibility.Collapsed;
        DragPreview.Visibility = Visibility.Visible;
        var pointer = PreviewList.TranslatePoint(position, EditorRoot);
        DragShift.X = Math.Clamp(pointer.X - _dragOffset.X, 0, Math.Max(0, EditorRoot.ActualWidth - DragPreview.Width));
        DragShift.Y = Math.Clamp(pointer.Y - _dragOffset.Y, 0, Math.Max(0, EditorRoot.ActualHeight - DragPreview.Height));
        // Keep the row and its containers intact during the gesture. Only move
        // the existing cards with render transforms; commit the collection once.
        var target = 0; var left = _dragSlots.Values.Min(slot => slot.Left);
        foreach (var rowId in Draft.Items)
        {
            var slot = _dragSlots[rowId];
            if (rowId != id && position.X > left + slot.Width / 2) target++;
            left += slot.Width;
        }
        var index = Draft.Items.IndexOf(id);
        if (target != index)
        {
            Draft.Items.RemoveAt(index); Draft.Items.Insert(target, id);
            AnimateDragSlots();
        }
        UpdateDragPlaceholder();
    }

    private void AnimateDragSlots()
    {
        var left = _dragSlots.Values.Min(slot => slot.Left);
        foreach (var id in Draft.Items)
        {
            var slot = _dragSlots[id]; var from = slot.Shift.X; var to = left - slot.Left;
            slot.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            var animate = AnimateFocus && Math.Abs(from - to) > .01;
            slot.Shift.X = animate ? from : to;
            if (animate)
            {
                var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
                slot.Shift.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            left += slot.Width;
        }
    }

    private void UpdateDragPlaceholder()
    {
        foreach (ToolbarRow row in PreviewList.Items)
            if (PreviewList.ItemContainerGenerator.ContainerFromItem(row) is UIElement item)
                item.Opacity = _dragging && row.Id == _dragId ? .25 : 1;
    }

    private void OnToolbarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _dragId is null) return;
        EndToolbarDrag(cancel: false); e.Handled = true;
    }

    private void OnToolbarLostCapture(object sender, MouseEventArgs e)
    {
        if (!_endingDrag && !PreviewList.IsMouseCaptureWithin) EndToolbarDrag(cancel: true);
    }

    internal void EndToolbarDrag(bool cancel)
    {
        if (_dragId is not { } id || _endingDrag) return;
        _endingDrag = true;
        try
        {
            var original = _dragOriginalOrder;
            _dragId = null; _dragOriginalOrder = null; _dragging = false; _dragCard = null;
            DragPreview.Visibility = Visibility.Collapsed; DragPreview.Source = null;
            if (PreviewList.IsMouseCaptureWithin) Mouse.Capture(null);
            foreach (var slot in _dragSlots.Values)
            { slot.Shift.BeginAnimation(TranslateTransform.XProperty, null); slot.Shift.X = 0; }
            _dragSlots.Clear();
            if (original is not null && !Draft.Items.SequenceEqual(original))
            {
                if (cancel) Draft.Items = original;
                else _preview(Draft.Clone());
                Refresh(id);
            }
            UpdateDragPlaceholder(); UpdateSelection();
            if (CanEdit && PreviewList.SelectedItem is { } selected &&
                PreviewList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item) item.Focus();
        }
        finally { _endingDrag = false; }
    }
}
