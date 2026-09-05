using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Windows;

public sealed partial class BluetoothControlNoticeWindow :
    Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private enum NoticeState { Waiting, Connected, Failed, ReportMapChanged, Prerequisite }

    private static BluetoothControlNoticeWindow? _active;
    private const double WaitingWidth = 500;
    private readonly DispatcherTimer _closeTimer;
    private readonly bool _previewOnly;
    private NoticeState _state = NoticeState.Waiting;
    private string? _failureDetail;
    private string? _suggestedDeviceName;
    private Action? _reportMapAcknowledged;
    private string? _prerequisiteTitle;
    private string? _prerequisiteBody;
    private int _remainingSeconds = 5;

    public string TitleText => _state == NoticeState.Prerequisite
        ? _prerequisiteTitle ?? string.Empty
        : LocalizationService.Get(_state switch
        {
            NoticeState.Waiting => "BluetoothControlWaitingTitle",
            NoticeState.Failed => "BluetoothControlFailedTitle",
            NoticeState.ReportMapChanged => "BluetoothControlReportMapChangedTitle",
            _ => "BluetoothControlPromptTitle",
        });
    public string BodyText => _state == NoticeState.Prerequisite
        ? _prerequisiteBody ?? string.Empty
        : _state == NoticeState.Connected
        ? LocalizationService.Format("BluetoothControlPromptBodyFormat",
            GetConfiguredShortcut().DisplayText)
        : LocalizationService.Get(_state switch
        {
            NoticeState.Waiting => "BluetoothControlWaitingBody",
            NoticeState.ReportMapChanged => "BluetoothControlReportMapChangedBody",
            _ => "BluetoothControlFailedBody",
        });
    public string DetailText
    {
        get
        {
            if (_state == NoticeState.Prerequisite) return string.Empty;
            if (_state == NoticeState.Failed && !string.IsNullOrWhiteSpace(_failureDetail))
                return _failureDetail;
            var detail = LocalizationService.Get(_state switch
            {
                NoticeState.Waiting => "BluetoothControlWaitingDetail",
                NoticeState.ReportMapChanged => "BluetoothControlReportMapChangedDetail",
                _ => "BluetoothControlPromptDetail",
            });
            return (_state is NoticeState.Waiting or NoticeState.ReportMapChanged) &&
                !string.IsNullOrWhiteSpace(_suggestedDeviceName)
                ? $"{LocalizationService.Format("BluetoothControlWaitingTargetFormat", _suggestedDeviceName)}\n{detail}"
                : detail;
        }
    }
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(DetailText)
        ? Visibility.Collapsed : Visibility.Visible;
    public string ShortcutText => LocalizationService.Format(
        "BluetoothControlPromptShortcutFormat", GetConfiguredShortcut().DisplayText);
    public string PairStepOneText => LocalizationService.Get("BluetoothControlPairStepOneFormat");
    public string PairStepTwoText => LocalizationService.Format(
        "BluetoothControlPairStepTwo", _suggestedDeviceName ?? Environment.MachineName);
    public string PairStepThreeText => LocalizationService.Get("BluetoothControlPairStepThree");
    public string PairStepFourText => LocalizationService.Get("BluetoothControlPairStepFour");
    public string PairStepFiveText => LocalizationService.Get("BluetoothControlPairStepFive");
    public string StatusText => _state switch
    {
        NoticeState.Waiting => LocalizationService.Get("BluetoothControlWaitingStatus"),
        NoticeState.Prerequisite => LocalizationService.Get("ReverseControlPrerequisiteStatus"),
        NoticeState.Failed => LocalizationService.Get("BluetoothControlFailedStatus"),
        NoticeState.ReportMapChanged => LocalizationService.Get("BluetoothControlReportMapChangedStatus"),
        _ => LocalizationService.Format(
            "BluetoothControlPromptAutoCloseFormat", _remainingSeconds),
    };
    public Visibility ShortcutVisibility => _state == NoticeState.Connected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WaitingStepsVisibility => _state is NoticeState.Waiting or NoticeState.ReportMapChanged
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PrerequisiteActionsVisibility => _state == NoticeState.Prerequisite
        ? Visibility.Visible : Visibility.Collapsed;
    public string CloseButtonText => _state == NoticeState.Prerequisite
        ? LocalizationService.Get("Cancel") : LocalizationService.Get("Close");
    public Visibility ReportMapAcknowledgementVisibility =>
        _state == NoticeState.ReportMapChanged ? Visibility.Visible : Visibility.Collapsed;

    internal static event EventHandler? ActiveNoticeClosed;

    private BluetoothControlNoticeWindow(Window owner, bool previewOnly = false)
    {
        _previewOnly = previewOnly;
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

    internal static void ShowReportMapChanged(Window owner, string? suggestedDeviceName,
        Action acknowledged)
    {
        _active?.Close();
        var window = new BluetoothControlNoticeWindow(owner)
        {
            _suggestedDeviceName = suggestedDeviceName,
            _reportMapAcknowledged = acknowledged,
        };
        _active = window;
        window.Show();
        window.SetState(NoticeState.ReportMapChanged);
        window.Activate();
    }

    internal static bool ConfirmPrerequisite(Window owner, bool wireless)
    {
        var window = CreatePrerequisite(owner, wireless, previewOnly: false);
        window.SetState(NoticeState.Prerequisite);
        return window.ShowDialog() == true;
    }

    internal static void ShowPrerequisitePreview(Window owner, bool wireless)
    {
        var window = CreatePrerequisite(owner, wireless, previewOnly: true);
        window.SetState(NoticeState.Prerequisite);
        window.Show();
        window.Activate();
    }

    private static BluetoothControlNoticeWindow CreatePrerequisite(Window owner, bool wireless,
        bool previewOnly)
    {
        return new BluetoothControlNoticeWindow(owner, previewOnly)
        {
            _prerequisiteTitle = LocalizationService.Get(wireless
                ? "ReverseControlPrerequisiteWirelessTitle"
                : "ReverseControlPrerequisiteWiredTitle"),
            _prerequisiteBody = LocalizationService.Get(wireless
                ? "ReverseControlPrerequisiteWirelessBody"
                : "ReverseControlPrerequisiteWiredBody"),
        };
    }

    internal static bool TryCloseActive()
    {
        if (_active is null) return false;
        _active.Close();
        return true;
    }

    internal static void NotifyShortcutChanged()
    {
        var window = _active;
        if (window is null) return;
        if (window.Dispatcher.CheckAccess())
            window.RefreshShortcutText();
        else
            window.Dispatcher.BeginInvoke(window.RefreshShortcutText);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPrerequisiteConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_state != NoticeState.Prerequisite) return;
        if (_previewOnly) Close();
        else DialogResult = true;
    }

    private void OnReportMapAcknowledgedClick(object sender, RoutedEventArgs e)
    {
        if (_state != NoticeState.ReportMapChanged) return;
        var acknowledged = _reportMapAcknowledged;
        _reportMapAcknowledged = null;
        acknowledged?.Invoke();
        Close();
    }

    private static KeyboardShortcut GetConfiguredShortcut() =>
        Application.Current is App app
            ? KeyboardShortcut.FromSettings(app.UpdateSettings,
                BluetoothShortcutAction.BluetoothControl)
            : KeyboardShortcut.Unbound;

    private void RefreshShortcutText()
    {
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(ShortcutText));
        ReflowToContent();
    }

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
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(PairStepOneText));
        OnPropertyChanged(nameof(PairStepTwoText));
        OnPropertyChanged(nameof(PairStepThreeText));
        OnPropertyChanged(nameof(PairStepFourText));
        OnPropertyChanged(nameof(PairStepFiveText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShortcutVisibility));
        OnPropertyChanged(nameof(WaitingStepsVisibility));
        OnPropertyChanged(nameof(ReportMapAcknowledgementVisibility));
        OnPropertyChanged(nameof(PrerequisiteActionsVisibility));
        OnPropertyChanged(nameof(CloseButtonText));
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
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(PairStepOneText));
        OnPropertyChanged(nameof(PairStepTwoText));
        OnPropertyChanged(nameof(PairStepThreeText));
        OnPropertyChanged(nameof(PairStepFourText));
        OnPropertyChanged(nameof(PairStepFiveText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrerequisiteActionsVisibility));
        OnPropertyChanged(nameof(CloseButtonText));
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
