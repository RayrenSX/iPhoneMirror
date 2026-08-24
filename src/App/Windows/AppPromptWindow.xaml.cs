using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class AppPromptWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _previewOnly;

    public string PromptTitle { get; }
    public string PromptBody { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public Visibility CancelVisibility { get; }

    private AppPromptWindow(string title, string body, bool showCancel,
        bool previewOnly = false)
    {
        _previewOnly = previewOnly;
        PromptTitle = title;
        PromptBody = body;
        ConfirmText = LocalizationService.Get(showCancel ? "Continue" : "Close");
        CancelText = LocalizationService.Get("Cancel");
        CancelVisibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DataContext = this;
        InitializeComponent();
    }

    internal static bool Confirm(string title, string body) =>
        new AppPromptWindow(title, body, true) { Owner = Application.Current.MainWindow }
            .ShowDialog() == true;

    internal static void Inform(string title, string body) =>
        new AppPromptWindow(title, body, false) { Owner = Application.Current.MainWindow }
            .ShowDialog();

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
