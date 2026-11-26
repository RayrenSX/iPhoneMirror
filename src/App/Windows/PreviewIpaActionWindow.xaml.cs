using System.IO;
using System.Windows;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

internal enum PreviewIpaAction { Transfer, Install }

public partial class PreviewIpaActionWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    internal PreviewIpaAction? SelectedAction { get; private set; }

    internal static string[] IpaFiles(IEnumerable<string> files) =>
        files.Where(file => string.Equals(Path.GetExtension(file), ".ipa", StringComparison.OrdinalIgnoreCase)).ToArray();

    internal PreviewIpaActionWindow(string deviceName, string[] files)
    {
        InitializeComponent();
        var packages = IpaFiles(files);
        TargetText.Text = LocalizationService.Format("IpaDropTarget", deviceName, packages.Length);
        FilesText.Text = string.Join(Environment.NewLine, packages.Take(3).Select(Path.GetFileName));
        MixedFilesText.Visibility = packages.Length == files.Length ? Visibility.Collapsed : Visibility.Visible;
        MixedFilesText.Text = LocalizationService.Format("IpaDropMixedFiles", files.Length, packages.Length);
    }

    private void TransferClick(object sender, RoutedEventArgs e) { SelectedAction = PreviewIpaAction.Transfer; Close(); }
    private void InstallClick(object sender, RoutedEventArgs e) { SelectedAction = PreviewIpaAction.Install; Close(); }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
}
