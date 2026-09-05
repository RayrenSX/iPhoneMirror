using System.Windows;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class CaptureStatusNoticeWindow : Wpf.Ui.Controls.FluentWindow
{
    private enum NoticeKind { Error, UsbConfiguration, Stopped }

    public string TitleText { get; }
    public string BodyText { get; }
    public string BadgeText { get; }
    public string HintText { get; }
    public bool IsStopped { get; }
    public bool IsUsbConfiguration { get; }
    public bool IsReverseControl { get; }
    public bool IsWarning => IsStopped || IsUsbConfiguration;

    private CaptureStatusNoticeWindow(string title, string body, NoticeKind kind,
        bool reverseControl = false, bool previewOnly = false)
    {
        TitleText = title;
        BodyText = body;
        IsStopped = kind == NoticeKind.Stopped;
        IsUsbConfiguration = kind == NoticeKind.UsbConfiguration;
        IsReverseControl = reverseControl;
        BadgeText = LocalizationService.Get(kind switch
        {
            NoticeKind.Stopped => "CaptureNoticeStoppedBadge",
            NoticeKind.UsbConfiguration => "CaptureNoticeUsbBadge",
            _ => reverseControl ? "ReverseControlNoticeErrorBadge" : "CaptureNoticeErrorBadge",
        });
        HintText = LocalizationService.Get(kind switch
        {
            NoticeKind.Stopped => "CaptureNoticeStoppedHint",
            NoticeKind.UsbConfiguration => "CaptureNoticeUsbHint",
            _ => "CaptureNoticeErrorHint",
        });
        DataContext = this;
        InitializeComponent();
    }

    internal static void ShowError(string title, string body) =>
        ShowError(title, body, usbConfiguration: false);

    internal static void ShowError(string title, string body,
        bool usbConfiguration, bool reverseControl = false) =>
        new CaptureStatusNoticeWindow(title, body,
            usbConfiguration ? NoticeKind.UsbConfiguration : NoticeKind.Error,
            reverseControl: reverseControl)
        {
            Owner = Application.Current.MainWindow,
        }.ShowDialog();

    internal static void ShowStoppedThen(string title, string body,
        Func<Task> afterShown)
    {
        ArgumentNullException.ThrowIfNull(afterShown);
        var notice = new CaptureStatusNoticeWindow(title, body, NoticeKind.Stopped)
        {
            Owner = Application.Current.MainWindow,
        };
        var started = false;
        notice.ContentRendered += async (_, _) =>
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
        };
        notice.ShowDialog();
    }

    internal static void ShowDeveloperErrorPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Format("DeviceCaptureErrorTitleFormat",
                LocalizationService.Get("DeveloperPreviewDeviceName")),
            LocalizationService.Get("CaptureActionVideoRetry"), NoticeKind.Error);

    internal static void ShowDeveloperReverseControlErrorPreview(Window owner) =>
        ShowDeveloperPreview(owner,
            LocalizationService.Get("ReverseControlErrorTitle"),
            LocalizationService.Format("ReverseControlErrorBodyFormat", "USB",
                "开发者服务未就绪（开发者工具预览）"), NoticeKind.Error,
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
