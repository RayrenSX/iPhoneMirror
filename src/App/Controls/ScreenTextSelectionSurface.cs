using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Controls;

internal sealed class ScreenTextSelectionSurface(BitmapSource image) : FrameworkElement
{
    private static readonly Brush WordMarker = CreateBrush(Color.FromArgb(42, 149, 190, 235));
    private static readonly Brush SelectionBrush = CreateBrush(Color.FromArgb(116, 61, 156, 255));
    private ScreenTextPage? _page;
    private Point? _mouseDown;
    private int _anchor;
    private int _active;
    private bool _dragging;

    internal event Action? SelectionChanged;
    internal string Selection => _page?.Text[Math.Min(_anchor, _active)..Math.Max(_anchor, _active)] ?? "";

    internal void SetPage(ScreenTextPage page)
    {
        _page = page;
        SetSelection(0, 0);
    }

    internal void SelectAll() => SetSelection(0, _page?.Text.Length ?? 0);

    private Rect ImageBounds => new(RenderSize);

    internal Point ToSurface(Point imagePoint)
    {
        var rect = ImageBounds;
        return new Point(rect.Left + imagePoint.X * rect.Width / image.PixelWidth,
            rect.Top + imagePoint.Y * rect.Height / image.PixelHeight);
    }

    private Point ToImage(Point surfacePoint)
    {
        var rect = ImageBounds;
        return new Point((surfacePoint.X - rect.Left) * image.PixelWidth / rect.Width,
            (surfacePoint.Y - rect.Top) * image.PixelHeight / rect.Height);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var imageBounds = ImageBounds;
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0) return;
        drawing.DrawRectangle(Brushes.Black, null, imageBounds);
        drawing.DrawImage(image, imageBounds);
        if (_page is null) return;
        drawing.PushClip(new RectangleGeometry(imageBounds));
        foreach (var word in _page.Words)
            drawing.DrawRoundedRectangle(WordMarker, null,
                new Rect(ToSurface(word.Bounds.TopLeft), ToSurface(word.Bounds.BottomRight)), 2, 2);
        foreach (var rect in _page.Highlights(Math.Min(_anchor, _active), Math.Max(_anchor, _active)))
            drawing.DrawRoundedRectangle(SelectionBrush, null,
                new Rect(ToSurface(rect.TopLeft), ToSurface(rect.BottomRight)), 2, 2);
        drawing.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_page is null) return;
        var point = e.GetPosition(this);
        if (!BeginSelection(point, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) return;
        Focus();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseDown is null || e.LeftButton != MouseButtonState.Pressed) return;
        ExtendSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mouseDown is null) return;
        EndSelection(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _mouseDown = null;
        base.OnLostMouseCapture(e);
    }

    internal bool BeginSelection(Point point, bool extend = false)
    {
        if (_page is null || !ImageBounds.Contains(point)) return false;
        var imagePoint = ToImage(point);
        var word = _page.HitWord(imagePoint, nearest: true);
        if (word is null) return false;
        _mouseDown = point;
        _dragging = extend;
        var caret = word.Caret(imagePoint.X);
        SetSelection(extend ? _anchor : caret, caret);
        return true;
    }

    internal void ExtendSelection(Point point)
    {
        if (_mouseDown is not { } origin || _page is null) return;
        if (!_dragging && Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        var imagePoint = ToImage(point);
        if (_page.HitWord(imagePoint, nearest: true) is { } word)
            SetSelection(_anchor, word.Caret(imagePoint.X));
    }

    internal void EndSelection(Point point)
    {
        ExtendSelection(point);
        _mouseDown = null;
        if (!_dragging && _page?.HitWord(ToImage(point), nearest: false) is { } word)
            SetSelection(word.Start, word.End);
    }

    private void SetSelection(int anchor, int active)
    {
        (_anchor, _active) = (anchor, active);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
