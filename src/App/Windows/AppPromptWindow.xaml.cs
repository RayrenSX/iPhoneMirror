using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.UI.Controls;
using Wpf.Ui.Controls;

namespace IPhoneMirror.App.Windows;

public partial class AppPromptWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly bool _previewOnly;

    private readonly string _promptTitle;
    public string PromptTitle => LocalizationService.RefreshText(_promptTitle);
    private readonly string _promptBody;
    public string PromptBody => LocalizationService.RefreshText(_promptBody);
    private readonly string _confirmText;
    public string ConfirmText => LocalizationService.RefreshText(_confirmText);
    private readonly string _cancelText;
    public string CancelText => LocalizationService.RefreshText(_cancelText);
    public Visibility CancelVisibility { get; }
    public StatusTone Tone { get; }
    public SymbolRegular PromptSymbol => Tone switch
    {
        StatusTone.Error => SymbolRegular.ErrorCircle20,
        StatusTone.Warning => SymbolRegular.Warning20,
        _ => SymbolRegular.Info20,
    };
    public bool ConfirmIsDefault { get; }
    public bool CancelIsDefault => !ConfirmIsDefault;
    public Style ConfirmStyle => (Style)FindResource(ConfirmIsDefault ? "PrimaryButton" : "DangerButton");

    private AppPromptWindow(string title, string body, bool showCancel,
        bool previewOnly = false, string? confirmText = null, bool destructive = false,
        StatusTone tone = StatusTone.Info)
    {
        _previewOnly = previewOnly;
        _promptTitle = title;
        _promptBody = body;
        _confirmText = confirmText ?? LocalizationService.Get(showCancel ? "Continue" : "Close");
        ConfirmIsDefault = !destructive;
        Tone = destructive ? StatusTone.Warning : tone;
        _cancelText = LocalizationService.Get("Cancel");
        CancelVisibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DataContext = this;
        InitializeComponent();
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
            PropertyChanged?.Invoke(this, new(null)));
    }

    internal static bool Confirm(string title, string body, Window? owner = null) =>
        new AppPromptWindow(title, body, true) { Owner = owner ?? Application.Current.MainWindow }
            .ShowDialog() == true;

    internal static void Inform(string title, string body, Window? owner = null) =>
        new AppPromptWindow(title, body, false) { Owner = owner ?? Application.Current.MainWindow }
            .ShowDialog();

    internal static bool ConfirmDestructive(string title, string body, string confirmText, Window owner) =>
        new AppPromptWindow(title, body, true, confirmText: confirmText, destructive: true)
            { Owner = owner }.ShowDialog() == true;

    internal static void Warn(string title, string body, Window owner) =>
        new AppPromptWindow(title, body, false, tone: StatusTone.Warning)
            { Owner = owner }.ShowDialog();

    internal static void InformThen(string title, string body, Func<Task> afterShown)
    {
        ArgumentNullException.ThrowIfNull(afterShown);
        var prompt = new AppPromptWindow(title, body, false)
        {
            Owner = Application.Current.MainWindow,
        };
        var started = false;
        prompt.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                // Let the composed prompt reach the screen before beginning
                // the USB/session cleanup requested for this warning.
                await prompt.Dispatcher.InvokeAsync(
                    static () => { }, DispatcherPriority.ContextIdle);
                await afterShown();
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("capture", "prompt_after_shown_action_failed",
                    error);
            }
        };
        prompt.ShowDialog();
    }

    internal static void ShowDeveloperPreview(Window owner)
    {
        ShowDeveloperPreview(owner,
            LocalizationService.Get("DeveloperPreviewPromptTitle"),
            LocalizationService.Get("DeveloperPreviewPromptBody"),
            showCancel: true);
    }

    internal static void ShowReverseControlPrerequisitePreview(Window owner, bool wireless)
    {
        BluetoothControlNoticeWindow.ShowPrerequisitePreview(owner, wireless);
    }

    internal static void ShowReverseControlErrorPreview(Window owner)
    {
        CaptureStatusNoticeWindow.ShowDeveloperReverseControlErrorPreview(owner);
    }

    internal static bool ConfirmReverseControlPrerequisite(Window owner, bool wireless) =>
        BluetoothControlNoticeWindow.ConfirmPrerequisite(owner, wireless);

    private static void ShowDeveloperPreview(Window owner, string title,
        string body, bool showCancel)
    {
        var prompt = new AppPromptWindow(title, body, showCancel,
            previewOnly: true) { Owner = owner };
        prompt.Show();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Complete(true);
    private void OnCancelClick(object sender, RoutedEventArgs e) => Complete(false);

    private void Complete(bool result)
    {
        if (_previewOnly)
        {
            Close();
            return;
        }
        DialogResult = result;
    }
}
