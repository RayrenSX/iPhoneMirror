using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class FailureHelpWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    public string ErrorMessage { get; }
    public string? LogPath { get; }
    public Visibility LogVisibility => string.IsNullOrWhiteSpace(LogPath) ? Visibility.Collapsed : Visibility.Visible;

    internal FailureHelpWindow(string errorMessage, string? logPath)
    {
        ErrorMessage = errorMessage;
        LogPath = logPath;
        DataContext = this;
        InitializeComponent();
    }

    private void OnCopyGroupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DriverConstants.QqGroupNumber);
            PromptWindow.Inform(this, DriverLocalization.Get("GroupCopiedTitle"),
                DriverLocalization.Get("GroupCopiedBody"));
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "copy_group_failed", error);
            PromptWindow.Inform(this, DriverLocalization.Get("CannotCopyGroup"),
                DriverLocalization.Get("HelpBody"), isError: true);
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LogPath)) return;
        try
        {
            var path = Path.GetFullPath(LogPath);
            var directory = Path.GetDirectoryName(path);
            while (directory is not null && !Directory.Exists(directory))
                directory = Path.GetDirectoryName(directory);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (File.Exists(path)) start.Arguments = $"/select,\"{path}\"";
            else if (directory is not null) start.ArgumentList.Add(directory);
            else return;
            Process.Start(start);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "open_failure_log_failed", error);
            PromptWindow.Inform(this, DriverLocalization.Get("CannotOpenLogs"),
                DriverLocalization.Get("OpenLogFailedBody"), isError: true);
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
                DriverLocalization.Format("HelpBrowserFailed", DriverConstants.AisiOfficialUrl), isError: true);
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
            PromptWindow.Inform(this, DriverLocalization.Get("DriverCleanupUnavailableTitle"),
                DriverLocalization.Get("DriverCleanupScriptStartFailed"), isError: true);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
