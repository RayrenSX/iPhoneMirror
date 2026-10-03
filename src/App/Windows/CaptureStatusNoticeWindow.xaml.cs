using System.ComponentModel;
using System.Windows;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class CaptureStatusNoticeWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private static CaptureStatusNoticeWindow? _activeError;
    private enum NoticeKind { Error, UsbConfiguration, Stopped }

    private readonly string _titleText;
    public string TitleText => LocalizationService.RefreshText(_titleText);
    private readonly string _bodyText;
    public string BodyText => LocalizationService.RefreshText(_bodyText);
    public string? TechnicalDetails { get; }
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);
    private readonly string _badgeText;
    public string BadgeText => LocalizationService.RefreshText(_badgeText);
    private readonly string _hintText;
    public string HintText => LocalizationService.RefreshText(_hintText);
    public bool IsStopped { get; }
    public bool IsUsbConfiguration { get; }
    public bool IsReverseControl { get; }
    public bool IsWarning => IsStopped || IsUsbConfiguration;

    private CaptureStatusNoticeWindow(string title, string body, NoticeKind kind,
        bool reverseControl = false, bool previewOnly = false, string? technicalDetails = null)
    {
        _titleText = title;
        _bodyText = body;
        TechnicalDetails = technicalDetails;
        IsStopped = kind == NoticeKind.Stopped;
        IsUsbConfiguration = kind == NoticeKind.UsbConfiguration;
        IsReverseControl = reverseControl;
        _badgeText = LocalizationService.Get(kind switch
        {
            NoticeKind.Stopped => "CaptureNoticeStoppedBadge",
            NoticeKind.UsbConfiguration => "CaptureNoticeUsbBadge",
            _ => reverseControl ? "ReverseControlNoticeErrorBadge" : "CaptureNoticeErrorBadge",
        });
        _hintText = LocalizationService.Get(kind switch
        {
            NoticeKind.Stopped => "CaptureNoticeStoppedHint",
            NoticeKind.UsbConfiguration => "CaptureNoticeUsbHint",
            _ => "CaptureNoticeErrorHint",
        });
        DataContext = this;
        InitializeComponent();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
            PropertyChanged?.Invoke(this, new(null)));
    }

    internal static void ShowError(string title, string body) =>
        ShowError(title, body, usbConfiguration: false);

    internal static void ShowError(string title, string body,
        bool usbConfiguration, bool reverseControl = false, string? technicalDetails = null) =>
        ShowErrorCore(title, body, usbConfiguration, reverseControl, technicalDetails);

    private static void ShowErrorCore(string title, string body,
        bool usbConfiguration, bool reverseControl, string? technicalDetails)
    {
        if (_activeError is { IsVisible: true })
        {
            if (_activeError.TitleText == title && _activeError.BodyText == body)
            {
                _activeError.Activate();
                return;
            }
            _activeError.Close();
        }
        var notice = new CaptureStatusNoticeWindow(title, body,
            usbConfiguration ? NoticeKind.UsbConfiguration : NoticeKind.Error,
            reverseControl: reverseControl, technicalDetails: technicalDetails)
        {
            Owner = Application.Current.MainWindow,
        };
        _activeError = notice;
        notice.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeError, notice)) _activeError = null;
        };
        notice.ShowDialog();
    }

    internal static void ShowStoppedThen(string title, string body,
        Func<Task> afterShown)
    {
        ArgumentNullException.ThrowIfNull(afterShown);
        var notice = new CaptureStatusNoticeWindow(title, body, NoticeKind.Stopped)
        {
            Owner = Application.Current.MainWindow,
        };
        var started = false;
        async Task ReleaseAfterShownAsync()
        {
            if (started) return;
            started = true;
            try
            {
                await notice.Dispatcher.InvokeAsync(
                    static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                await afterShown();
            }
            catch (Exception error)
            {
                Services.DiagnosticLogger.Exception("capture",
                    "capture_notice_after_shown_failed", error);
            }
        }
        notice.ContentRendered += async (_, _) => await ReleaseAfterShownAsync();
        try { notice.ShowDialog(); }
        finally
        {
            // Closing before ContentRendered (or failing to show) must not
            // retain the failed capture. The once guard also covers re-entry.
            if (!started) _ = ReleaseAfterShownAsync();
        }
    }

    internal static void ShowDeveloperErrorPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                LocalizationService.Get("DeveloperPreviewDeviceName")),
            LocalizationService.Get("CaptureActionVideoRetry"), NoticeKind.Error);

    internal static void ShowDeveloperReverseControlErrorPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Get("ReverseControlErrorTitle"),
            LocalizationService.Format("ReverseControlErrorBodyFormat",
                LocalizationService.Get("ReverseControlTransportWired"),
                LocalizationService.Get("DeveloperPreviewReverseControlUnavailable")), NoticeKind.Error,
            reverseControl: true);

    internal static void ShowDeveloperStoppedPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Format("DeviceSessionClosedWarningTitleFormat",
                LocalizationService.Get("DeveloperPreviewDeviceName")),
            LocalizationService.Get("DeviceSessionClosedWarningBody"), NoticeKind.Stopped);

    internal static void ShowDeveloperUsbPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Get("CaptureUsbConfigurationTitle"),
            LocalizationService.Get("CaptureActionReconnectDevice"),
            NoticeKind.UsbConfiguration);

    private static void ShowDeveloperPreview(Window owner, string title,
        string body, NoticeKind kind, bool reverseControl = false)
    {
        new CaptureStatusNoticeWindow(title, body, kind,
            reverseControl: reverseControl, previewOnly: true)
        {
            Owner = owner,
        }.Show();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
