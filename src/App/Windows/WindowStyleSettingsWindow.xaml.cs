using System.Windows;
using System.Windows.Input;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Windows;

public partial class WindowStyleSettingsWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    private readonly Func<double, bool> _apply;
    private readonly Func<bool, bool> _applyOpaqueOnHover;
    private bool _ready;
    private double _appliedOpacity;

    internal WindowStyleSettingsWindow(double opacity, Func<double, bool> apply,
        bool opaqueOnHover, Func<bool, bool> applyOpaqueOnHover)
    {
        _apply = apply;
        _applyOpaqueOnHover = applyOpaqueOnHover;
        InitializeComponent();
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
