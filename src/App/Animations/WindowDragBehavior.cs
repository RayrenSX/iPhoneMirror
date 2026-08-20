using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace IPhoneMirror.UI.Animations;

public static class WindowDragBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WindowDragBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element) return;
        element.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
        if (args.NewValue is true)
            element.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left ||
            sender is not DependencyObject source) return;

        // Keep buttons and other controls usable when the behavior is attached
        // to a container that also hosts interactive header content.
        var hit = args.OriginalSource as DependencyObject ?? source;
        for (var current = hit; current is not null; current = GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or Selector or ScrollBar or Thumb)
                return;
            if (current is Window) break;
        }

        var window = Window.GetWindow(source);
        if (window is null) return;

        if (args.ClickCount == 2 &&
            window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            args.Handled = true;
            return;
        }

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released between the routed event and DragMove.
        }
    }

    private static DependencyObject? GetParent(DependencyObject element) => element switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(element),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(element),
    };
}
