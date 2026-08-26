using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public sealed partial class BluetoothControlNoticeWindow :
    Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private enum NoticeState { Waiting, Connected, Failed }

    private static BluetoothControlNoticeWindow? _active;
    private const double WaitingWidth = 500;
    private readonly DispatcherTimer _closeTimer;
    private NoticeState _state = NoticeState.Waiting;
    private string? _failureDetail;
    private string? _suggestedDeviceName;
    private int _remainingSeconds = 5;

    public string TitleText => LocalizationService.Get(_state switch
    {
        NoticeState.Waiting => "BluetoothControlWaitingTitle",
        NoticeState.Failed => "BluetoothControlFailedTitle",
        _ => "BluetoothControlPromptTitle",
    });
    public string BodyText => LocalizationService.Get(_state switch
    {
        NoticeState.Waiting => "BluetoothControlWaitingBody",
        NoticeState.Failed => "BluetoothControlFailedBody",
        _ => "BluetoothControlPromptBody",
    });
    public string DetailText
    {
        get
        {
            if (_state == NoticeState.Failed && !string.IsNullOrWhiteSpace(_failureDetail))
                return _failureDetail;
            var detail = LocalizationService.Get(_state == NoticeState.Waiting
                ? "BluetoothControlWaitingDetail"
                : "BluetoothControlPromptDetail");
            return _state == NoticeState.Waiting && !string.IsNullOrWhiteSpace(_suggestedDeviceName)
                ? $"{LocalizationService.Format("BluetoothControlWaitingTargetFormat", _suggestedDeviceName)}\n{detail}"
                : detail;
        }
    }
    public string ShortcutText => LocalizationService.Get("BluetoothControlPromptShortcut");
    public string PairStepOneText => LocalizationService.Get("BluetoothControlPairStepOneFormat");
    public string PairStepTwoText => LocalizationService.Format(
        "BluetoothControlPairStepTwo", _suggestedDeviceName ?? Environment.MachineName);
    public string PairStepThreeText => LocalizationService.Get("BluetoothControlPairStepThree");
    public string PairStepFourText => LocalizationService.Get("BluetoothControlPairStepFour");
    public string PairStepFiveText => LocalizationService.Get("BluetoothControlPairStepFive");
    public string StatusText => _state switch
    {
        NoticeState.Waiting => LocalizationService.Get("BluetoothControlWaitingStatus"),
        NoticeState.Failed => LocalizationService.Get("BluetoothControlFailedStatus"),
        _ => LocalizationService.Format(
            "BluetoothControlPromptAutoCloseFormat", _remainingSeconds),
    };
    public Visibility ShortcutVisibility => _state == NoticeState.Connected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WaitingStepsVisibility => _state == NoticeState.Waiting
        ? Visibility.Visible : Visibility.Collapsed;

    internal static event EventHandler? ActiveNoticeClosed;

    private BluetoothControlNoticeWindow(Window owner)
    {
        Owner = owner;
        DataContext = this;
        InitializeComponent();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick += OnCloseTimerTick;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            if (ReferenceEquals(_active, this)) _active = null;
            ActiveNoticeClosed?.Invoke(this, EventArgs.Empty);
        };
    }

    internal static void ShowWaiting(Window owner, string? suggestedDeviceName)
    {
        _active?.Close();
        var window = new BluetoothControlNoticeWindow(owner);
        window._suggestedDeviceName = suggestedDeviceName;
        _active = window;
        window.Show();
        window.ReflowToContent();
        window.Activate();
    }

    internal static void ShowConnected(Window owner)
    {
        var window = _active;
        if (window is null)
        {
            window = new BluetoothControlNoticeWindow(owner);
            _active = window;
            window.Show();
        }
        window.SetState(NoticeState.Connected);
        window.Activate();
    }

    internal static void ShowFailure(Window owner, string? detail)
    {
        var window = _active;
        if (window is null)
        {
            window = new BluetoothControlNoticeWindow(owner);
            _active = window;
            window.Show();
        }
        window._failureDetail = detail;
        window.SetState(NoticeState.Failed);
        window.Activate();
    }

    internal static bool TryCloseActive()
    {
        if (_active is null) return false;
        _active.Close();
        return true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        if (_state != NoticeState.Connected) return;
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            Close();
            return;
        }
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetState(NoticeState state)
    {
        _state = state;
        // Keep the original dialog width. When the pairing checklist hides,
        // remeasure only the height so the connected notice loses the empty
        // lower area instead of becoming a narrower dialog.
        MinWidth = WaitingWidth;
        MaxWidth = WaitingWidth;
        Width = WaitingWidth;
        _remainingSeconds = 5;
        _closeTimer.Stop();
        if (state == NoticeState.Connected) _closeTimer.Start();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(PairStepOneText));
        OnPropertyChanged(nameof(PairStepTwoText));
        OnPropertyChanged(nameof(PairStepThreeText));
        OnPropertyChanged(nameof(PairStepFourText));
        OnPropertyChanged(nameof(PairStepFiveText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShortcutVisibility));
        OnPropertyChanged(nameof(WaitingStepsVisibility));
        PairingStepsPanel.Visibility = WaitingStepsVisibility;
        ShortcutBadge.Visibility = ShortcutVisibility;
        ReflowToContent();
        Dispatcher.BeginInvoke(ReflowToContent, DispatcherPriority.Render);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(PairStepOneText));
        OnPropertyChanged(nameof(PairStepTwoText));
        OnPropertyChanged(nameof(PairStepThreeText));
        OnPropertyChanged(nameof(PairStepFourText));
        OnPropertyChanged(nameof(PairStepFiveText));
        OnPropertyChanged(nameof(StatusText));
        Dispatcher.BeginInvoke(ReflowToContent, DispatcherPriority.Render);
    }

    private void RecenterOverOwner()
    {
        if (Owner is null) return;
        // CenterOwner is applied only when the window is first shown. Width
        // changes later retain the left edge, so recenter after compacting.
        Left = Owner.Left + Math.Max(0, (Owner.ActualWidth - ActualWidth) / 2);
        Top = Owner.Top + Math.Max(0, (Owner.ActualHeight - ActualHeight) / 2);
    }

    private void ReflowToContent()
    {
        if (!IsVisible || Content is not FrameworkElement content) return;

        // SizeToContent can retain the previous height after a collapsed
        // checklist. Measure the live surface against the fixed dialog width
        // and set the outer height explicitly, so no stale lower area remains.
        SizeToContent = System.Windows.SizeToContent.Manual;
        MinWidth = WaitingWidth;
        MaxWidth = WaitingWidth;
        Width = WaitingWidth;
        MinHeight = 0;
        MaxHeight = double.PositiveInfinity;
        content.InvalidateMeasure();
        content.Measure(new Size(WaitingWidth, double.PositiveInfinity));
        var nonClientHeight = Math.Max(0, ActualHeight - content.ActualHeight);
        Height = Math.Max(1, Math.Ceiling(content.DesiredSize.Height + nonClientHeight));
        UpdateLayout();
        RecenterOverOwner();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
