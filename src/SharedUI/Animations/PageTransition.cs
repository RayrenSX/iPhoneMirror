using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IPhoneMirror.UI.Animations;

public static class PageTransition
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
            typeof(PageTransition), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject target,
        DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element || args.NewValue is not true) return;
        element.Loaded += Play;
    }

    private static void Play(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        var transform = element.RenderTransform as TranslateTransform ??
                        new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
