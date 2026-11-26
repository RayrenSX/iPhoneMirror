using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IPhoneMirror.App.Controls;

// Arrange the capped preview directly in its slot. Binding its Width to the
// already-arranged column feeds every resize back into a second measurement.
internal sealed class PreviewViewport : Decorator
{
    internal static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(nameof(CenterContent), typeof(bool), typeof(PreviewViewport),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    internal bool CenterContent
    {
        get => (bool)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    internal static readonly DependencyProperty FittedAspectProperty =
        DependencyProperty.Register(nameof(FittedAspect), typeof(double), typeof(PreviewViewport),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    internal double FittedAspect
    {
        get => (double)GetValue(FittedAspectProperty);
        set => SetValue(FittedAspectProperty, value);
    }

    internal static readonly DependencyProperty FitHeightProperty =
        DependencyProperty.Register(nameof(FitHeight), typeof(bool), typeof(PreviewViewport),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    internal bool FitHeight
    {
        get => (bool)GetValue(FitHeightProperty);
        set => SetValue(FitHeightProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is FrameworkElement content)
        {
            var width = Math.Min(arrangeSize.Width, content.MaxWidth);
            var height = arrangeSize.Height;
            if (FittedAspect > 0 && content is Border frame)
            {
                var inset = frame.BorderThickness;
                var padding = frame.Padding;
                var horizontal = inset.Left + inset.Right + padding.Left + padding.Right;
                var vertical = inset.Top + inset.Bottom + padding.Top + padding.Bottom;
                var dpi = VisualTreeHelper.GetDpi(this);
                var fittedWidth = Math.Max(0, height - vertical) * FittedAspect + horizontal;
                width = Math.Min(width, Math.Round(fittedWidth * dpi.DpiScaleX) / dpi.DpiScaleX);
                if (FitHeight)
                {
                    var fittedHeight = Math.Max(0, width - horizontal) / FittedAspect + vertical;
                    height = Math.Min(height, Math.Round(fittedHeight * dpi.DpiScaleY) / dpi.DpiScaleY);
                }
            }
            var x = CenterContent || FittedAspect > 0 ? (arrangeSize.Width - width) / 2 : 0;
            var y = (arrangeSize.Height - height) / 2;
            content.Arrange(new Rect(x, y, width, height));
        }
        return arrangeSize;
    }
}
