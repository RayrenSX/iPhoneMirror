using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private PreviewWindowsHotkeyPolicy? _previewWindowsHotkeys;

    private void RefreshIndependentWindowLayoutEditor()
    {
        var device = _viewModel.SelectedDevice;
        IndependentWindowLayoutEditor.IsEnabled = device is { IsMediaCast: false };
        IndependentLayoutDevice.Text = IndependentWindowLayoutEditor.IsEnabled ? device?.DisplayName ?? "" : "";
        var layout = device is null ? null : MultiDevicePreviewManager.GetSavedLayout(device.Udid);
        IndependentLayoutX.Text = Format(layout?.X);
        IndependentLayoutY.Text = Format(layout?.Y);
        IndependentLayoutWidth.Text = Format(layout?.Width);
        IndependentLayoutHeight.Text = Format(layout?.Height);
        IndependentLayoutFixed.IsChecked = layout?.Fixed == true;
        IndependentLayoutStatus.Text = "";

        static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void OnSaveIndependentWindowLayout(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDevice is not { IsMediaCast: false } device ||
            Application.Current is not App app) return;
        if (!TryReadLayoutNumber(IndependentLayoutX, out var x) ||
            !TryReadLayoutNumber(IndependentLayoutY, out var y) ||
            !TryReadLayoutNumber(IndependentLayoutWidth, out var width) ||
            !TryReadLayoutNumber(IndependentLayoutHeight, out var height) ||
            new IndependentWindowLayout(x, y, width, height, IndependentLayoutFixed.IsChecked == true) is not { IsValid: true } layout)
        {
            ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get("IndependentWindowLayoutInvalid"), false);
            return;
        }

        var previous = app.UpdateSettings.IndependentWindowLayouts;
        var updated = new Dictionary<string, IndependentWindowLayout>(previous, StringComparer.OrdinalIgnoreCase);
        if (layout.IsEmpty) updated.Remove(device.Udid);
        else updated[device.Udid] = layout;
        app.UpdateSettings.IndependentWindowLayouts = updated;
        var saved = app.SaveUpdateSettings();
        if (!saved) app.UpdateSettings.IndependentWindowLayouts = previous;
        ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get(saved
            ? "IndependentWindowLayoutSaved" : "SetupSaveFailed"), saved);
    }

    private static bool TryReadLayoutNumber(TextBox field, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(field.Text)) return true;
        if (!int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private void InitializePreviewWindowsHotkeys()
    {
        if (Application.Current is not App app) return;
        var settings = app.UpdateSettings;
        _previewWindowsHotkeys = PreviewWindowsHotkeyPolicy.Parse(
            settings.PreviewWindowsHotkeyAllowList, settings.PreviewWindowsHotkeyBlockList, out _);
    }

    private PreviewWindowsHotkeyPolicy? PreviewWindowsHotkeysFor(nint window) =>
        !_mappingClosing && window != 0 && _previewWindowsHotkeys is { HasRules: true } &&
        (_secondaryMirrors.ContainsWindow(window) || _mediaCastPreviewWindow?.Handle == window)
            ? _previewWindowsHotkeys : null;

    private static void ShowSettingsFeedback(TextBlock status, string text, bool success)
    {
        status.SetResourceReference(TextBlock.ForegroundProperty, success ? "MutedTextBrush" : "ErrorBrush");
        status.Text = text;
    }
}
