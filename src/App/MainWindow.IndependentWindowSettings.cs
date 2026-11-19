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
        IndependentLayoutUseLast.IsChecked = device is { IsMediaCast: false } && Application.Current is App app &&
            app.UpdateSettings.IndependentWindowLayoutHistoryByDevice.GetValueOrDefault(device.Udid)?.UseLastLayout == true;
        IndependentLayoutX.Text = Format(layout?.X);
        IndependentLayoutY.Text = Format(layout?.Y);
        IndependentLayoutWidth.Text = Format(layout?.Width);
        IndependentLayoutHeight.Text = Format(layout?.Height);
        IndependentLayoutFixed.IsChecked = layout?.Fixed == true;
        IndependentLayoutStatus.Text = "";

        static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void OnIndependentLayoutModeClick(object sender, RoutedEventArgs e) =>
        IndependentLayoutStatus.Text = "";

    private void OnSaveIndependentWindowLayout(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDevice is not { IsMediaCast: false } device ||
            Application.Current is not App app) return;
        var useLast = IndependentLayoutUseLast.IsChecked == true;
        var layout = useLast ? null : ReadCustomWindowLayout();
        if (!useLast && layout is null)
        {
            ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get("IndependentWindowLayoutInvalid"), false);
            return;
        }

        var settings = app.UpdateSettings;
        var previousLayouts = settings.IndependentWindowLayouts;
        var previousHistory = settings.IndependentWindowLayoutHistoryByDevice;
        if (layout is not null)
        {
            var layouts = new Dictionary<string, IndependentWindowLayout>(previousLayouts, StringComparer.OrdinalIgnoreCase);
            if (layout.IsEmpty) layouts.Remove(device.Udid);
            else layouts[device.Udid] = layout;
            settings.IndependentWindowLayouts = layouts;
        }
        var history = previousHistory.GetValueOrDefault(device.Udid) ?? new();
        if (history.UseLastLayout != useLast)
            settings.IndependentWindowLayoutHistoryByDevice = new(previousHistory, StringComparer.OrdinalIgnoreCase)
            {
                [device.Udid] = history with { UseLastLayout = useLast },
            };

        var saved = app.SaveUpdateSettings();
        if (!saved)
        {
            settings.IndependentWindowLayouts = previousLayouts;
            settings.IndependentWindowLayoutHistoryByDevice = previousHistory;
        }
        ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get(saved
            ? "IndependentWindowLayoutSaved" : "SetupSaveFailed"), saved);
    }

    private IndependentWindowLayout? ReadCustomWindowLayout()
    {
        if (!TryReadLayoutNumber(IndependentLayoutX, out var x) ||
            !TryReadLayoutNumber(IndependentLayoutY, out var y) ||
            !TryReadLayoutNumber(IndependentLayoutWidth, out var width) ||
            !TryReadLayoutNumber(IndependentLayoutHeight, out var height))
            return null;
        var layout = new IndependentWindowLayout(x, y, width, height, IndependentLayoutFixed.IsChecked == true);
        return layout.IsValid ? layout : null;
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
