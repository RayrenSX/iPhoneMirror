using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IPhoneMirror.App.Windows;

internal readonly record struct ImageAdjustmentValues(
    double Brightness, double Contrast, double Saturation, double Gamma);

public partial class ImageSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ImageAdjustmentValues _originalValues;
    private readonly Func<ImageAdjustmentValues, (bool Success, string Message)> _preview;
    private readonly Func<ImageAdjustmentValues, (bool Success, string Message)> _save;
    private readonly Func<ImageAdjustmentValues, (bool Success, string Message)> _revert;
    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(33),
    };
    private bool _ready;
    private bool _saved;
    private bool _skipRevertOnClose;
    private bool _dirty;

    internal ImageAdjustmentValues Values => new(
        BrightnessSlider.Value, ContrastSlider.Value,
        SaturationSlider.Value, GammaSlider.Value);

    internal ImageSettingsWindow(ImageAdjustmentValues originalValues,
        Func<ImageAdjustmentValues, (bool Success, string Message)> preview,
        Func<ImageAdjustmentValues, (bool Success, string Message)> save,
        Func<ImageAdjustmentValues, (bool Success, string Message)> revert)
    {
        _originalValues = originalValues;
        _preview = preview;
        _save = save;
        _revert = revert;
        InitializeComponent();
        BrightnessSlider.Value = Math.Clamp(originalValues.Brightness, -100, 100);
        ContrastSlider.Value = Math.Clamp(originalValues.Contrast, 0, 200);
        SaturationSlider.Value = Math.Clamp(originalValues.Saturation, 0, 200);
        GammaSlider.Value = Math.Clamp(originalValues.Gamma, 50, 200);
        _previewTimer.Tick += OnPreviewTimerTick;
        _ready = true;
    }

    internal static void ShowDeveloperPreview(Window owner)
    {
        var window = new ImageSettingsWindow(
            new ImageAdjustmentValues(0, 100, 100, 100),
            _ => (true, string.Empty), _ => (true, string.Empty),
            _ => (true, string.Empty))
        {
            Owner = owner,
        };
        window.Show();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 100;
        SaturationSlider.Value = 100;
        GammaSlider.Value = 100;
    }

    private void OnAdjustmentValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _dirty = true;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _ = Apply(_preview, Values);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        if (!Apply(_save, Values)) return;
        _saved = true;
        _dirty = false;
        Close();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _previewTimer.Stop();
        if (_saved || _skipRevertOnClose) return;
        if (Apply(_revert, _originalValues)) return;

        // Keep the window available so the user can retry or save instead of
        // silently leaving an unsaved native preview active.
        e.Cancel = true;
    }

    private bool Apply(
        Func<ImageAdjustmentValues, (bool Success, string Message)> action,
        ImageAdjustmentValues values)
    {
        var result = action(values);
        FeedbackText.Text = result.Success ? string.Empty : result.Message;
        return result.Success;
    }

    internal void CloseForShutdown()
    {
        _skipRevertOnClose = true;
        Close();
    }

    internal void CloseForSessionInvalidation()
    {
        _skipRevertOnClose = true;
        Close();
    }

    internal void SetEditingEnabled(bool enabled)
    {
        AdjustmentPanel.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        if (!enabled)
        {
            _previewTimer.Stop();
            return;
        }
        if (_dirty) _ = Apply(_preview, Values);
    }
}
