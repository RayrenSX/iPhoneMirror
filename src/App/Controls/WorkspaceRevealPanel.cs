using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Controls;

/// <summary>
/// A clipped viewport for the existing workspace cards. Only the viewport width
/// changes during a reveal; text, controls and ScrollViewers retain their normal
/// measure/arrange constraints. WPF can reuse their valid layout between frames.
/// </summary>
public sealed class WorkspaceRevealPanel : Grid
{
    public static readonly DependencyProperty ContentWidthProperty =
        DependencyProperty.Register(nameof(ContentWidth), typeof(double),
            typeof(WorkspaceRevealPanel), new FrameworkPropertyMetadata(300d,
                FrameworkPropertyMetadataOptions.AffectsMeasure),
            value => value is double width && double.IsFinite(width) && width > 0);

    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    public WorkspaceRevealPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var content = base.MeasureOverride(new Size(ContentWidth, availableSize.Height));
        return new Size(Math.Min(ContentWidth, availableSize.Width), content.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(new Size(ContentWidth, finalSize.Height));
        return finalSize;
    }
}
