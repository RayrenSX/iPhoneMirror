using System.Windows;
using System.Windows.Controls;

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

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is FrameworkElement content)
        {
            var width = Math.Min(arrangeSize.Width, content.MaxWidth);
            var x = CenterContent ? (arrangeSize.Width - width) / 2 : 0;
            content.Arrange(new Rect(x, 0, width, arrangeSize.Height));
        }
        return arrangeSize;
    }
}
