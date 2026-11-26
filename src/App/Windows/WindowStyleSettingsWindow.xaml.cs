using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class WindowStyleSettingsWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly Func<double, bool> _apply;
    private readonly Func<bool, bool> _applyOpaqueOnHover;
    private readonly Func<bool, bool>? _applyPhoneFrame;
    private readonly Func<bool, bool>? _applyClickThrough;
    private bool _ready;
    private double _appliedOpacity;

    internal WindowStyleSettingsWindow(double opacity, Func<double, bool> apply,
        bool opaqueOnHover, Func<bool, bool> applyOpaqueOnHover,
        bool phoneFrame = false, Func<bool, bool>? applyPhoneFrame = null,
        string? phoneFrameDescription = null, bool phoneFrameAvailable = true,
        Func<bool, bool>? applyClickThrough = null)
    {
        _apply = apply;
        _applyOpaqueOnHover = applyOpaqueOnHover;
        _applyPhoneFrame = applyPhoneFrame;
        _applyClickThrough = applyClickThrough;
        InitializeComponent();
        PhoneFrameCheckBox.IsChecked = phoneFrame;
        PhoneFrameCheckBox.IsEnabled = phoneFrameAvailable;
        if (phoneFrameDescription is not null)
        {
            SetPhoneFrameDescription(phoneFrameDescription);
        }
        PhoneFrameSection.Visibility = applyPhoneFrame is null ? Visibility.Collapsed : Visibility.Visible;
        ClickThroughSection.Visibility = applyClickThrough is null ? Visibility.Collapsed : Visibility.Visible;
        LocalizationService.RefreshWhenLanguageChanges(this, () =>
        {
            FeedbackText.Text = LocalizationService.RefreshText(FeedbackText.Text);
            PhoneFrameHelp.Text = LocalizationService.RefreshText(PhoneFrameHelp.Text);
            if (PhoneFrameCheckBox.ToolTip is string tooltip)
                PhoneFrameCheckBox.ToolTip = LocalizationService.RefreshText(tooltip);
        });
        _appliedOpacity = Math.Clamp(opacity, 0.1, 1.0);
        OpacitySlider.Value = _appliedOpacity * 100;
        OpaqueOnHoverCheckBox.IsChecked = opaqueOnHover;
        _ready = true;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var opacity = OpacitySlider.Value / 100;
        if (_apply(opacity))
        {
            _appliedOpacity = opacity;
            FeedbackText.Visibility = Visibility.Collapsed;
            return;
        }
        _ready = false;
        OpacitySlider.Value = _appliedOpacity * 100;
        _ready = true;
        FeedbackText.Text = LocalizationService.Get("IndependentWindowOpacityFailed");
        FeedbackText.Visibility = Visibility.Visible;
    }

    internal void SetPhoneFrameEnabled(bool enabled)
    {
        var ready = _ready;
        _ready = false;
        PhoneFrameCheckBox.IsChecked = enabled;
        _ready = ready;
    }

    internal void SetClickThroughState(bool enabled, bool canEnable)
    {
        var ready = _ready;
        _ready = false;
        ClickThroughCheckBox.IsChecked = enabled;
        ClickThroughCheckBox.IsEnabled = enabled || canEnable;
        ClickThroughHint.Visibility = enabled || canEnable ? Visibility.Collapsed : Visibility.Visible;
        _ready = ready;
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applyClickThrough is null) return;
        var enabled = ClickThroughCheckBox.IsChecked == true;
        if (_applyClickThrough(enabled))
        {
            FeedbackText.Visibility = Visibility.Collapsed;
            return;
        }
        SetClickThroughState(!enabled, ClickThroughCheckBox.IsEnabled);
        FeedbackText.Text = LocalizationService.Get("IndependentWindowClickThroughUnavailable");
        FeedbackText.Visibility = Visibility.Visible;
    }

    internal void SetPhoneFrameDescription(string description)
    {
        PhoneFrameHelp.Text = description;
        PhoneFrameCheckBox.ToolTip = description;
    }

    private void OnPhoneFrameChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applyPhoneFrame is null) return;
        var enabled = PhoneFrameCheckBox.IsChecked == true;
        if (_applyPhoneFrame(enabled))
        {
            FeedbackText.Visibility = Visibility.Collapsed;
            return;
        }
        _ready = false;
        PhoneFrameCheckBox.IsChecked = !enabled;
        _ready = true;
        FeedbackText.Text = LocalizationService.Get("IndependentWindowPhoneFrameFailed");
        FeedbackText.Visibility = Visibility.Visible;
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => OpacitySlider.Value = 100;
    private void OnOpaqueOnHoverChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = OpaqueOnHoverCheckBox.IsChecked == true;
        if (_applyOpaqueOnHover(enabled))
        {
            FeedbackText.Visibility = Visibility.Collapsed;
            return;
        }
        _ready = false;
        OpaqueOnHoverCheckBox.IsChecked = !enabled;
        _ready = true;
        FeedbackText.Text = LocalizationService.Get("IndependentWindowOpacityFailed");
        FeedbackText.Visibility = Visibility.Visible;
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
