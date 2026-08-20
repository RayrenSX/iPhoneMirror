using System.Windows;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class AdvancedSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _previewOnly;

    public uint RequestedWidth { get; private set; }
    public uint RequestedHeight { get; private set; }
    public bool DisableAdvancedModeRequested { get; private set; }

    public AdvancedSettingsWindow(uint width, uint height, bool previewOnly = false)
    {
        _previewOnly = previewOnly;
        InitializeComponent();
        WidthBox.Text = width == 0 ? "" : width.ToString();
        HeightBox.Text = height == 0 ? "" : height.ToString();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!uint.TryParse(WidthBox.Text.Trim(), out var width) ||
            !uint.TryParse(HeightBox.Text.Trim(), out var height) ||
            width < 16 || height < 16 || width > 8192 || height > 8192)
        {
            ErrorText.Text = LocalizationService.Get("AdvancedResolutionInvalid");
            return;
        }
        RequestedWidth = width;
        RequestedHeight = height;
        Complete(true);
    }

    private void OnDisableClick(object sender, RoutedEventArgs e)
    {
        DisableAdvancedModeRequested = true;
        Complete(false);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
