using System.Windows;
using System.Windows.Input;

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
        element.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        if (args.NewValue is true)
            element.MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left ||
            sender is not DependencyObject source) return;

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
}
