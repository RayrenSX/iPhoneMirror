using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class FailureHelpWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    public string ErrorMessage { get; }

    internal FailureHelpWindow(string errorMessage)
    {
        ErrorMessage = errorMessage;
        DataContext = this;
        InitializeComponent();
    }

    private void OnCopyGroupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DriverConstants.QqGroupNumber);
            Process.Start(new ProcessStartInfo("https://im.qq.com/") { UseShellExecute = true });
            PromptWindow.Inform(this, DriverLocalization.Get("GroupCopiedTitle"),
                DriverLocalization.Get("GroupCopiedBody"));
        }
        catch (Exception error)
        {
            PromptWindow.Inform(this, DriverLocalization.Get("CannotOpenQq"), error.Message);
        }
    }

    private void OnOpenAisiClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DriverConstants.AisiOfficialUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "help_browser_start_failed", error);
            PromptWindow.Inform(this, DriverLocalization.Get("HelpBrowserFailedTitle"),
                DriverLocalization.Format("HelpBrowserFailed", DriverConstants.AisiOfficialUrl, error.Message));
        }
    }

    private void OnRunDriverCleanupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DriverCleanupHost.LaunchElevated();
            DriverLogger.Write("Trusted driver cleanup host launched from failure help.");
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "driver_cleanup_script_start_failed", error);
            PromptWindow.Inform(this, DriverLocalization.Get("DriverCleanupTitle"),
                DriverLocalization.Format("DriverCleanupScriptStartFailed", error.Message));
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
