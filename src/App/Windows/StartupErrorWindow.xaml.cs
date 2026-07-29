using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class StartupErrorWindow : Window
{
    private readonly string _logPath;

    internal StartupErrorWindow(Exception error, string logPath)
    {
        _logPath = logPath;
        InitializeComponent();
        try { ThemeService.Attach(this); }
        catch (Exception themeError)
        {
            DiagnosticLogger.Exception("startup", "error_window_theme_failed",
                themeError);
        }

        var chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh",
            StringComparison.OrdinalIgnoreCase);
        HeadingText.Text = chinese ? "iPhoneMirror 无法启动" : "iPhoneMirror could not start";
        SummaryText.Text = StartupDiagnostics.UserMessage(error, chinese);
        LogLabelText.Text = chinese ? "诊断日志" : "Diagnostic log";
        LogPathTextBox.Text = logPath;
        DetailsExpander.Header = chinese ? "错误详情" : "Error details";
        DetailsTextBox.Text = error.ToString();
        OpenLogButton.Content = chinese ? "打开日志位置" : "Open log location";
        CloseButton.Content = chinese ? "关闭" : "Close";
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var arguments = File.Exists(_logPath)
                ? $"/select,\"{_logPath}\""
                : $"\"{directory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("startup", "open_log_location_failed", error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
