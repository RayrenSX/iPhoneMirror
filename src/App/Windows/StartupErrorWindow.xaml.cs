using System.Diagnostics;
using System.IO;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class StartupErrorWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly string _logPath;
    private readonly Exception _error;

    internal StartupErrorWindow(Exception error, string logPath)
    {
        _logPath = logPath;
        _error = error;
        InitializeComponent();
        try { ThemeService.Attach(this); }
        catch (Exception themeError)
        {
            DiagnosticLogger.Exception("startup", "error_window_theme_failed",
                themeError);
        }

        LogPathTextBox.Text = logPath;
        DetailsTextBox.Text = error.ToString();
        RefreshLanguage();
        LocalizationService.RefreshWhenLanguageChanges(this, RefreshLanguage);
    }

    private void RefreshLanguage()
    {
        var language = LocalizationService.StartupCultureName;
        // This window also handles dictionary-load failures. Its captions must
        // remain readable when none of the localization resources are available.
        HeadingText.Text = StartupDiagnostics.Label("StartupErrorHeading", language);
        SummaryText.Text = StartupDiagnostics.UserMessage(_error, language);
        LogLabelText.Text = StartupDiagnostics.Label("StartupErrorLogLabel", language);
        DetailsExpander.Header = StartupDiagnostics.Label("StartupErrorDetails", language);
        OpenLogButton.Content = StartupDiagnostics.Label("StartupErrorOpenLog", language);
        CloseButton.Content = StartupDiagnostics.Label("StartupErrorClose", language);
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
            AppPromptWindow.Inform(Localization.LocalizationService.Get("StartupErrorOpenLog"),
                Localization.LocalizationService.Format("UiLogLocationFailed", _logPath), this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
