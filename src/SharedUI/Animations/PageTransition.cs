using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IPhoneMirror.UI.Animations;

public static class PageTransition
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
            typeof(PageTransition), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty EntranceTransformProperty =
        DependencyProperty.RegisterAttached("EntranceTransform",
            typeof(TranslateTransform), typeof(PageTransition));

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
        // A root can unload/reload when child surfaces open. Release stale
        // clocks before replaying the transition from its resting state.
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        var transform = GetEntranceTransform(element);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
        if (!SystemParameters.ClientAreaAnimation) return;

        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var translate = new DoubleAnimation(8, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(translate, 60);
        transform.BeginAnimation(TranslateTransform.YProperty, translate,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform GetEntranceTransform(FrameworkElement element)
    {
        if (element.GetValue(EntranceTransformProperty) is TranslateTransform transform)
            return transform;

        transform = new TranslateTransform();
        var group = new TransformGroup();
        if (element.RenderTransform is { } existing &&
            !ReferenceEquals(existing, Transform.Identity))
            group.Children.Add(existing);
        group.Children.Add(transform);
        element.RenderTransform = group;
        element.SetValue(EntranceTransformProperty, transform);
        return transform;
    }
}
