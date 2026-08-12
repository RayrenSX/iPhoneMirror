using System.Diagnostics;
using System.IO;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class StartupErrorWindow : Wpf.Ui.Controls.FluentWindow
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

        var language = LocalizationService.StartupCultureName;
        var hongKong = language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-Hant-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        HeadingText.Text = hongKong ? "iPhoneMirror 無法啟動" :
            chinese ? "iPhoneMirror 无法启动" : "iPhoneMirror could not start";
        SummaryText.Text = StartupDiagnostics.UserMessage(error,
            hongKong ? "zh-HK" : chinese ? "zh-CN" : "en-US");
        LogLabelText.Text = hongKong ? "診斷記錄" :
            chinese ? "诊断日志" : "Diagnostic log";
        LogPathTextBox.Text = logPath;
        DetailsExpander.Header = hongKong ? "錯誤詳細資料" :
            chinese ? "错误详情" : "Error details";
        DetailsTextBox.Text = error.ToString();
        OpenLogButton.Content = hongKong ? "開啟記錄位置" :
            chinese ? "打开日志位置" : "Open log location";
        CloseButton.Content = hongKong ? "關閉" :
            chinese ? "关闭" : "Close";
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
