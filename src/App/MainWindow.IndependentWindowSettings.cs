using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private PreviewWindowsHotkeyPolicy? _previewWindowsHotkeys;
    private IndependentWindowLayoutEditor? _independentLayoutEditor;

    private void RefreshIndependentWindowLayoutEditor() =>
        (_independentLayoutEditor ??= new(this, _viewModel, _secondaryMirrors.GetCurrentLayout)).Refresh();

    private void OnIndependentLayoutModeChanged(object sender, SelectionChangedEventArgs e) =>
        _independentLayoutEditor?.ChangeMode();

    private void OnSaveIndependentWindowLayout(object sender, RoutedEventArgs e) =>
        _independentLayoutEditor?.Save();

    private void OnSaveCurrentIndependentWindowLayout(object sender, RoutedEventArgs e) =>
        _independentLayoutEditor?.SaveCurrent();

    internal IndependentWindowLayout? GetCurrentIndependentWindowLayout(string udid) =>
        _secondaryMirrors.GetCurrentLayout(udid);

    private void InitializePreviewWindowsHotkeys()
    {
        if (Application.Current is not App app) return;
        var settings = app.UpdateSettings;
        _previewWindowsHotkeys = PreviewWindowsHotkeyPolicy.Parse(
            settings.PreviewWindowsHotkeyAllowList, settings.PreviewWindowsHotkeyBlockList, out _);
    }

    internal PreviewWindowsHotkeyPolicy? PreviewWindowsHotkeysFor(nint window) =>
        !_mappingClosing && window != 0 && _previewWindowsHotkeys is { HasRules: true } &&
        (_secondaryMirrors.ContainsWindow(window) || _mediaCastPreviewWindow?.Handle == window)
            ? _previewWindowsHotkeys : null;
}
