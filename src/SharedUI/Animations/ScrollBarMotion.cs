using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace IPhoneMirror.UI.Animations;

/// <summary>Animates the visible grip while preserving the full pointer target.</summary>
public static class ScrollBarMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollBarMotion), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled", typeof(bool), typeof(ScrollBarMotion), new PropertyMetadata(true, OnPreferenceChanged));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetAnimationsEnabled(DependencyObject target) => (bool)target.GetValue(AnimationsEnabledProperty);
    public static void SetAnimationsEnabled(DependencyObject target, bool value) => target.SetValue(AnimationsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollBar bar) return;
        if ((bool)args.NewValue)
        {
            bar.Loaded += OnLoaded;
            bar.MouseEnter += OnHoverChanged;
            bar.MouseLeave += OnHoverChanged;
            bar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            bar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            if (bar.IsLoaded) Update(bar, animate: false);
        }
        else
        {
            bar.Loaded -= OnLoaded;
            bar.MouseEnter -= OnHoverChanged;
            bar.MouseLeave -= OnHoverChanged;
            bar.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            bar.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            Update(bar, animate: false);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Update((ScrollBar)sender, animate: false);
    private static void OnHoverChanged(object sender, System.Windows.Input.MouseEventArgs args) =>
        Update((ScrollBar)sender, GetAnimationsEnabled((ScrollBar)sender));
    private static void OnDragStarted(object sender, DragStartedEventArgs args) =>
        Update((ScrollBar)sender, GetAnimationsEnabled((ScrollBar)sender));
    private static void OnDragCompleted(object sender, DragCompletedEventArgs args) =>
        Update((ScrollBar)sender, GetAnimationsEnabled((ScrollBar)sender));
    private static void OnPreferenceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ScrollBar { IsLoaded: true } bar && GetIsEnabled(bar)) Update(bar, animate: false);
    }

    private static void Update(ScrollBar bar, bool animate)
        => ApplyState(bar, GetIsEnabled(bar) && (bar.IsMouseOver ||
            bar.Template?.FindName("ScrollThumb", bar) is Thumb { IsDragging: true }), animate);

    internal static void ApplyState(ScrollBar bar, bool hover, bool animate)
    {
        if (bar.Template is null) return;
        if (bar.Template.FindName("TrackSurface", bar) is not FrameworkElement track ||
            bar.Template.FindName("ScrollThumb", bar) is not Thumb thumb) return;
        thumb.ApplyTemplate();
        if (thumb.Template.FindName("Grip", thumb) is not FrameworkElement grip) return;
        Transition(track, UIElement.OpacityProperty, hover ? 0.62 : 0, animate, hover ? 120 : 200);
        Transition(thumb, UIElement.OpacityProperty, hover ? 0.96 : 0.56, animate, hover ? 100 : 180);
        // Track owns the thumb's complete drag target in both dimensions.
        Transition(grip, bar.Orientation == Orientation.Vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty,
            hover ? 4 : 2, animate, hover ? 120 : 180);
    }

    private static void Transition(FrameworkElement target, DependencyProperty property, double value, bool animate, int milliseconds)
    {
        var previous = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetCurrentValue(property, value);
        if (animate && previous != value)
            target.BeginAnimation(property, new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
    }
}
