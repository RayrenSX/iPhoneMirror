using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IPhoneMirror.App.Controls;

// Keep the existing buttons and scroll template. Only their optional labels
// participate in compaction; scrolling remains available if even the icons do not fit.
public sealed class AdaptiveToolbar : ScrollViewer
{
    private static readonly DependencyPropertyDescriptor LabelTextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private readonly List<TextBlock> _observedLabels = [];

    public AdaptiveToolbar()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Content is not StackPanel actions) return base.MeasureOverride(constraint);

        var naturalSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var buttons = actions.Children.OfType<Button>().ToArray();
        var width = 0.0;
        foreach (var button in buttons)
        {
            if (GetLabel(button) is { } label) SetLabelVisibility(button, label, Visibility.Visible);
            button.Measure(naturalSize);
            width += button.DesiredSize.Width;
        }

        // This is the current layout constraint, not the window width or the
        // previous frame's ActualWidth/ViewportWidth. WPF supplies it in DIPs.
        var availableWidth = Math.Max(0, constraint.Width - Padding.Left - Padding.Right -
            BorderThickness.Left - BorderThickness.Right - actions.Margin.Left - actions.Margin.Right);
        // Match WPF's scale-aware double comparison. Summing fractional DIPs
        // can exceed an exact fit by round-off, which must not hide another label.
        const double machineEpsilon = 2.2204460492503131e-16;
        var widthTolerance = (Math.Abs(width) + Math.Abs(availableWidth) + 10) * machineEpsilon;
        for (var index = buttons.Length - 1; index >= 0 && width - availableWidth > widthTolerance; --index)
        {
            var button = buttons[index];
            if (button.Visibility == Visibility.Collapsed || GetLabel(button) is not { } label) continue;
            var expandedWidth = button.DesiredSize.Width;
            SetLabelVisibility(button, label, Visibility.Collapsed);
            button.Measure(naturalSize);
            width -= expandedWidth - button.DesiredSize.Width;
        }

        // All probing and state changes finish before arrange/render. The base
        // ScrollViewer then computes its extent from the final button sizes.
        return base.MeasureOverride(constraint);
    }

    private static TextBlock? GetLabel(Button button) =>
        button.Content is StackPanel content ? content.Children.OfType<TextBlock>().FirstOrDefault() : null;

    private void SetLabelVisibility(Button button, TextBlock label, Visibility visibility)
    {
        if (label.Visibility == visibility) return;
        label.SetCurrentValue(VisibilityProperty, visibility);
        // Invalidate the template path as well, so explicit Measure calls see
        // the new content size immediately, without UpdateLayout or dispatching.
        for (DependencyObject? current = label; current is UIElement element && current != this;
             current = VisualTreeHelper.GetParent(current))
            element.InvalidateMeasure();
        button.InvalidateMeasure();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Content is not StackPanel actions) return;
        foreach (var button in actions.Children.OfType<Button>())
        {
            if (GetLabel(button) is not { } label || _observedLabels.Contains(label)) continue;
            _observedLabels.Add(label);
            LabelTextDescriptor.AddValueChanged(label, OnLabelTextChanged);
        }
        InvalidateMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        foreach (var label in _observedLabels)
            LabelTextDescriptor.RemoveValueChanged(label, OnLabelTextChanged);
        _observedLabels.Clear();
    }

    // A collapsed TextBlock does not propagate a new desired size. Observe text
    // changes explicitly so switching language also remeasures hidden labels.
    private void OnLabelTextChanged(object? sender, EventArgs e) => InvalidateMeasure();
}
