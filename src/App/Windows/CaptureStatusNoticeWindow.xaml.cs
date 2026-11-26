using System.ComponentModel;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;

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
    private readonly CaptureFailureNotice? _failure;
    public bool HasFailure => _failure.HasValue;
    public bool HasDiagnostics => HasFailure || HasTechnicalDetails;
    private readonly string? _recoveryActionKey;
    public bool HasRecoveryAction => _recoveryActionKey is not null;
    public string RecoveryActionText => _recoveryActionKey is null ? string.Empty : LocalizationService.Get(_recoveryActionKey);
    private bool _recoveryRequested;
    public string FailureStageText => _failure is { } failure
        ? CaptureErrorGuidance.FailureStageText(failure.Stage) : string.Empty;
    public string FailureCodeText => _failure?.ErrorCode is { } code && code != 0
        ? CaptureErrorGuidance.ErrorCodeText(code)
        : LocalizationService.Get("CaptureFailureCodeUnavailable");
    public bool IsStopped { get; }
    public bool IsUsbConfiguration { get; }
    public bool IsReverseControl { get; }
    public bool IsWarning => IsStopped || IsUsbConfiguration;

    private CaptureStatusNoticeWindow(string title, string body, NoticeKind kind,
        bool reverseControl = false, string? technicalDetails = null,
        CaptureFailureNotice? failure = null, string? recoveryActionKey = null)
    {
        _titleText = title;
        _bodyText = body;
        _failure = failure;
        _recoveryActionKey = recoveryActionKey;
        TechnicalDetails = technicalDetails;
        IsStopped = kind == NoticeKind.Stopped;
        IsUsbConfiguration = kind == NoticeKind.UsbConfiguration;
        IsReverseControl = reverseControl;
        DataContext = this;
        InitializeComponent();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
            PropertyChanged?.Invoke(this, new(null)));
    }

    internal static void ShowError(string title, string body) =>
        ShowError(title, body, usbConfiguration: false);

    internal static void ShowError(string title, string body,
        bool usbConfiguration, bool reverseControl = false, string? technicalDetails = null) =>
        ShowErrorCore(title, body, usbConfiguration, reverseControl, technicalDetails, null, null, null);

    internal static void ShowError(string title, CaptureFailureNotice failure, bool usbConfiguration = false,
        Action? recover = null, string recoveryActionKey = "CaptureRetryAction") =>
        ShowErrorCore(title, failure.Message, usbConfiguration, false, null, failure,
            recover is null ? null : recoveryActionKey, recover);

    private static void ShowErrorCore(string title, string body,
        bool usbConfiguration, bool reverseControl, string? technicalDetails, CaptureFailureNotice? failure,
        string? recoveryActionKey, Action? recover)
    {
        if (_activeError is { IsVisible: true })
        {
            if (_activeError.TitleText == title && _activeError.BodyText == body &&
                _activeError._failure == failure && _activeError.TechnicalDetails == technicalDetails &&
                _activeError._recoveryActionKey == recoveryActionKey)
            {
                _activeError.Activate();
                return;
            }
            _activeError.Close();
        }
        // WPF assigns the first constructed Window as MainWindow. Capture the
        // existing owner first, and ignore a hidden or already closed owner.
        var owner = Application.Current?.MainWindow;
        var notice = new CaptureStatusNoticeWindow(title, body,
            usbConfiguration ? NoticeKind.UsbConfiguration : NoticeKind.Error,
            reverseControl: reverseControl, technicalDetails: technicalDetails, failure: failure,
            recoveryActionKey: recoveryActionKey);
        if (owner is { IsVisible: true }) notice.Owner = owner;
        _activeError = notice;
        notice.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeError, notice)) _activeError = null;
        };
        notice.ShowDialog();
        if (notice._recoveryRequested && recover is not null)
            _ = notice.Dispatcher.InvokeAsync(recover);
    }

    internal static void ShowStoppedThen(string title, string body,
        Func<Task> afterShown, CaptureFailureNotice? failure = null)
    {
        ArgumentNullException.ThrowIfNull(afterShown);
        var owner = Application.Current?.MainWindow;
        var notice = new CaptureStatusNoticeWindow(title, body, NoticeKind.Stopped, failure: failure);
        if (owner is { IsVisible: true }) notice.Owner = owner;
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

    internal static void ShowDeveloperErrorPreview(Window owner, string? deviceName = null)
    {
        var failure = CaptureErrorGuidance.Notice(new NativeCaptureStatus
        {
            FailureKind = CaptureFailureKind.UsbConnection,
            FailureStage = CaptureFailureStage.InterfaceOpen,
            ErrorCode = -2103,
        });
        ShowDeveloperPreview(owner,
            LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                deviceName ?? LocalizationService.Get("DeveloperPreviewDeviceName")),
            failure.Message, NoticeKind.Error, failure: failure, recoveryActionKey: "CaptureRetryAction");
    }

    internal static void ShowDeveloperReverseControlErrorPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Get("ReverseControlErrorTitle"),
            LocalizationService.Get("DeveloperPreviewReverseControlUnavailable"), NoticeKind.Error,
            reverseControl: true, technicalDetails: "USB / bridge startup timeout");

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
        string body, NoticeKind kind, bool reverseControl = false, CaptureFailureNotice? failure = null,
        string? technicalDetails = null, string? recoveryActionKey = null)
    {
        new CaptureStatusNoticeWindow(title, body, kind,
            reverseControl: reverseControl, failure: failure, technicalDetails: technicalDetails,
            recoveryActionKey: recoveryActionKey)
        {
            Owner = owner,
        }.Show();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRecoveryClick(object sender, RoutedEventArgs e)
    {
        _recoveryRequested = true;
        Close();
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var details = LocalizationService.Join(Environment.NewLine, [TitleText, BodyText,
            HasFailure ? LocalizationService.Format("CaptureFailureStageCodeFormat", FailureStageText, FailureCodeText) : "",
            TechnicalDetails ?? ""]);
        try
        {
            Clipboard.SetText(details);
            CopyFeedback.Text = LocalizationService.Get("CaptureDiagnosticsCopied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopyFeedback.Text = LocalizationService.Get("CaptureDiagnosticsCopyFailed");
        }
    }
}
