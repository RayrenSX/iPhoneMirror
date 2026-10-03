using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IPhoneMirror.UI.Animations;

// One replaceable clock for a coordinated layout transition. It ticks before
// layout, unlike CompositionTarget.Rendering (which can cause a second layout).
internal sealed class RevealTransition : Animatable
{
    private static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register("Progress", typeof(double), typeof(RevealTransition),
            new PropertyMetadata(0d, (target, args) =>
            {
                var transition = (RevealTransition)target;
                if (transition.IsRunning) transition._frame?.Invoke((double)args.NewValue);
            }));

    private readonly Action<double>? _frame;
    private readonly Action? _completed;
    private AnimationClock? _clock;
    private DispatcherOperation? _pendingStart;
    internal bool IsRunning => _clock is not null || _pendingStart is not null;

    private RevealTransition() { }
    internal RevealTransition(Action<double> frame, Action completed)
    {
        _frame = frame;
        _completed = completed;
    }

    internal void Start(FrameworkElement owner)
    {
        Stop();
        // A formerly collapsed card has to realize templates and measure once.
        // Do that at width zero before starting time, so first-use layout does
        // not consume the reveal. Loaded runs after layout, before user input.
        _pendingStart = owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _pendingStart = null;
            if (!owner.IsLoaded || !owner.IsVisible)
            {
                _completed?.Invoke();
                return;
            }
            StartClock(owner);
        });
    }

    private void StartClock(FrameworkElement owner)
    {
        var animation = new DoubleAnimation(0, 1,
            owner.TryFindResource("NormalAnimationDuration") is Duration duration
                ? duration : new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = owner.TryFindResource("ModernEase") as IEasingFunction
                ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Freeze();
        _clock = (AnimationClock)((Timeline)animation).CreateClock(true);
        _clock.Completed += OnCompleted;
        ApplyAnimationClock(ProgressProperty, _clock, HandoffBehavior.SnapshotAndReplace);
    }

    internal void Stop()
    {
        _pendingStart?.Abort();
        _pendingStart = null;
        var clock = _clock;
        _clock = null;
        if (clock is null) return;
        clock.Completed -= OnCompleted;
        ApplyAnimationClock(ProgressProperty, null);
        clock.Controller?.Remove();
    }

    private void OnCompleted(object? sender, EventArgs args)
    {
        Stop();
        _completed?.Invoke();
    }

    protected override Freezable CreateInstanceCore() => new RevealTransition();
}
