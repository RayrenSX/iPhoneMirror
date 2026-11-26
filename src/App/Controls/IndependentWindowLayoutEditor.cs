using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Controls;

internal sealed class IndependentWindowLayoutEditor
{
    private readonly MainViewModel _viewModel;
    private readonly Func<string, IndependentWindowLayout?> _currentLayout;
    private readonly ComboBox IndependentLayoutModeSelector;
    private readonly TextBlock IndependentLayoutModeHelp;
    private readonly TextBlock IndependentLayoutDevice;
    private readonly TextBlock IndependentLayoutDeviceHint;
    private readonly TextBlock IndependentLayoutFallbackHint;
    private readonly Button IndependentLayoutSaveCurrentButton;
    private readonly StackPanel IndependentLayoutCustomFields;
    private readonly TextBox IndependentLayoutX;
    private readonly TextBox IndependentLayoutY;
    private readonly TextBox IndependentLayoutWidth;
    private readonly TextBox IndependentLayoutHeight;
    private readonly TextBlock IndependentLayoutStatus;

    internal IndependentWindowLayoutEditor(Window window, MainViewModel viewModel,
        Func<string, IndependentWindowLayout?> currentLayout)
    {
        _viewModel = viewModel;
        _currentLayout = currentLayout;
        IndependentLayoutModeSelector = (ComboBox)window.FindName("IndependentLayoutModeSelector");
        IndependentLayoutModeHelp = (TextBlock)window.FindName("IndependentLayoutModeHelp");
        IndependentLayoutDevice = (TextBlock)window.FindName("IndependentLayoutDevice");
        IndependentLayoutDeviceHint = (TextBlock)window.FindName("IndependentLayoutDeviceHint");
        IndependentLayoutFallbackHint = (TextBlock)window.FindName("IndependentLayoutFallbackHint");
        IndependentLayoutSaveCurrentButton = (Button)window.FindName("IndependentLayoutSaveCurrentButton");
        IndependentLayoutCustomFields = (StackPanel)window.FindName("IndependentLayoutCustomFields");
        IndependentLayoutX = (TextBox)window.FindName("IndependentLayoutX");
        IndependentLayoutY = (TextBox)window.FindName("IndependentLayoutY");
        IndependentLayoutWidth = (TextBox)window.FindName("IndependentLayoutWidth");
        IndependentLayoutHeight = (TextBox)window.FindName("IndependentLayoutHeight");
        IndependentLayoutStatus = (TextBlock)window.FindName("IndependentLayoutStatus");
        foreach (var field in new[] { IndependentLayoutX, IndependentLayoutY, IndependentLayoutWidth, IndependentLayoutHeight })
        {
            field.LostKeyboardFocus += (_, _) => SaveCustomEdit();
            field.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                SaveCustomEdit();
                e.Handled = true;
            };
        }
    }

    private bool _refreshing;
    private string? _draftDeviceId;
    private (IndependentWindowLayoutMode Mode, IndependentWindowLayout? Layout, IndependentWindowLayoutHistory? History) _savedState;
    private string? IndependentLayoutDeviceId =>
        _viewModel.SelectedDevice is { IsMediaCast: false } device ? device.Udid : null;

    internal void Refresh()
    {
        _refreshing = true;
        try
        {
            var udid = IndependentLayoutDeviceId;
            _draftDeviceId = udid;
            IndependentLayoutDevice.Text = udid is not null ? _viewModel.SelectedDevice!.DisplayName : "";
            IndependentLayoutDeviceHint.Visibility = udid is null ? Visibility.Visible : Visibility.Collapsed;
            var history = udid is null ? null : MultiDevicePreviewManager.GetLayoutHistory(udid);
            IndependentLayoutModeSelector.SelectedValue = MultiDevicePreviewManager.GetLayoutMode();
            ShowIndependentWindowLayout(history?.CustomLayout is { IsValid: true } custom ? custom : null);
            RefreshIndependentLayoutModeDetails();
            IndependentLayoutStatus.Text = "";
            _savedState = ReadSavedState();
        }
        finally { _refreshing = false; }
    }

    internal void RefreshIfChanged()
    {
        if (_savedState != ReadSavedState()) Refresh();
    }

    private (IndependentWindowLayoutMode, IndependentWindowLayout?, IndependentWindowLayoutHistory?) ReadSavedState()
    {
        var mode = MultiDevicePreviewManager.GetLayoutMode();
        return IndependentLayoutDeviceId is { } udid
            ? (mode, MultiDevicePreviewManager.GetModeLayout(udid, mode), MultiDevicePreviewManager.GetLayoutHistory(udid))
            : (mode, null, null);
    }

    private void ShowIndependentWindowLayout(IndependentWindowLayout? layout)
    {
        IndependentLayoutX.Text = Format(layout?.X);
        IndependentLayoutY.Text = Format(layout?.Y);
        IndependentLayoutWidth.Text = Format(layout?.Width);
        IndependentLayoutHeight.Text = Format(layout?.Height);

        static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    internal void ChangeMode()
    {
        if (_refreshing || IndependentLayoutStatus is null || Application.Current is not App app ||
            IndependentLayoutModeSelector.SelectedItem is not ComboBoxItem { Tag: IndependentWindowLayoutMode mode }) return;

        var previousMode = MultiDevicePreviewManager.GetLayoutMode();
        if (mode != previousMode)
        {
            var previousSetting = app.UpdateSettings.IndependentWindowStartupMode;
            var previousSchema = app.UpdateSettings.IndependentWindowLayoutSchema;
            app.UpdateSettings.IndependentWindowStartupMode = mode;
            app.UpdateSettings.IndependentWindowLayoutSchema = 1;
            if (!app.SaveUpdateSettings())
            {
                app.UpdateSettings.IndependentWindowStartupMode = previousSetting;
                app.UpdateSettings.IndependentWindowLayoutSchema = previousSchema;
                _refreshing = true;
                try { IndependentLayoutModeSelector.SelectedValue = previousMode; }
                finally { _refreshing = false; }
                ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get("SetupSaveFailed"), false);
                return;
            }
        }
        RefreshIndependentLayoutModeDetails();
        if (mode != previousMode)
            ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get("IndependentWindowLayoutSaved"), true);
        _savedState = ReadSavedState();
    }

    private void RefreshIndependentLayoutModeDetails()
    {
        var udid = IndependentLayoutDeviceId;
        var mode = MultiDevicePreviewManager.GetLayoutMode();
        var layout = udid is null ? null : MultiDevicePreviewManager.GetModeLayout(udid, mode);
        var custom = mode == IndependentWindowLayoutMode.Custom;
        IndependentLayoutCustomFields.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        IndependentLayoutCustomFields.IsEnabled = udid is not null && custom;
        IndependentLayoutSaveCurrentButton.Visibility = mode == IndependentWindowLayoutMode.Preset
            ? Visibility.Visible : Visibility.Collapsed;
        IndependentLayoutSaveCurrentButton.IsEnabled = udid is not null;
        var help = mode switch
        {
            IndependentWindowLayoutMode.Automatic => "IndependentWindowLayoutAutomaticHelp",
            IndependentWindowLayoutMode.Last => "IndependentWindowLayoutUseLastHelp",
            _ => null,
        };
        if (help is not null) IndependentLayoutModeHelp.SetResourceReference(TextBlock.TextProperty, help);
        IndependentLayoutModeHelp.Visibility = help is null ? Visibility.Collapsed : Visibility.Visible;
        var hint = udid is not null && layout is null ? mode switch
        {
            IndependentWindowLayoutMode.Preset => "IndependentWindowLayoutPresetEmpty",
            IndependentWindowLayoutMode.Custom => "IndependentWindowLayoutCustomEmpty",
            _ => null,
        } : null;
        if (hint is not null) IndependentLayoutFallbackHint.SetResourceReference(TextBlock.TextProperty, hint);
        IndependentLayoutFallbackHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveCustomEdit()
    {
        if (_refreshing || !DeviceViewModel.UdidEquals(_draftDeviceId, IndependentLayoutDeviceId) ||
            MultiDevicePreviewManager.GetLayoutMode() != IndependentWindowLayoutMode.Custom) return;
        Save();
    }

    internal bool Save()
    {
        if (IndependentLayoutDeviceId is not { } udid || Application.Current is not App app) return false;
        var mode = MultiDevicePreviewManager.GetLayoutMode();
        var layout = mode == IndependentWindowLayoutMode.Custom ? ReadCustomWindowLayout() : null;
        if (mode == IndependentWindowLayoutMode.Custom && layout is null)
        {
            ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get("IndependentWindowLayoutInvalid"), false);
            return false;
        }
        return SaveIndependentWindowLayout(app, udid, mode, layout is { IsEmpty: false } ? layout : null);
    }

    internal void SaveCurrent()
    {
        if (MultiDevicePreviewManager.GetLayoutMode() != IndependentWindowLayoutMode.Preset ||
            IndependentLayoutDeviceId is not { } udid || Application.Current is not App app) return;
        if (_currentLayout(udid) is not { } layout)
        {
            ShowSettingsFeedback(IndependentLayoutStatus,
                LocalizationService.Get("IndependentWindowLayoutCurrentUnavailable"), false);
            return;
        }
        SaveIndependentWindowLayout(app, udid, IndependentWindowLayoutMode.Preset, layout);
    }

    private bool SaveIndependentWindowLayout(App app, string udid, IndependentWindowLayoutMode mode,
        IndependentWindowLayout? layout)
    {
        var settings = app.UpdateSettings;
        var previousLayouts = settings.IndependentWindowLayouts;
        var previousHistory = settings.IndependentWindowLayoutHistoryByDevice;
        if (mode == IndependentWindowLayoutMode.Preset && layout is not null)
        {
            var layouts = UpdateSettings.CopyDeviceSettings(previousLayouts);
            layouts[udid] = layout;
            settings.IndependentWindowLayouts = layouts;
        }
        var updatedHistory = UpdateSettings.CopyDeviceSettings(previousHistory);
        var history = updatedHistory.GetValueOrDefault(udid) ?? new();
        updatedHistory[udid] = history with
        {
            CustomLayout = mode == IndependentWindowLayoutMode.Custom ? layout : history.CustomLayout,
            AutoFix = null,
        };
        settings.IndependentWindowLayoutHistoryByDevice = updatedHistory;

        var saved = app.SaveUpdateSettings();
        if (!saved)
        {
            settings.IndependentWindowLayouts = previousLayouts;
            settings.IndependentWindowLayoutHistoryByDevice = previousHistory;
        }
        else
        {
            RefreshIndependentLayoutModeDetails();
            _savedState = ReadSavedState();
        }
        ShowSettingsFeedback(IndependentLayoutStatus, LocalizationService.Get(saved
            ? "IndependentWindowLayoutSaved" : "SetupSaveFailed"), saved);
        return saved;
    }

    private IndependentWindowLayout? ReadCustomWindowLayout()
    {
        if (!TryReadLayoutNumber(IndependentLayoutX, out var x) ||
            !TryReadLayoutNumber(IndependentLayoutY, out var y) ||
            !TryReadLayoutNumber(IndependentLayoutWidth, out var width) ||
            !TryReadLayoutNumber(IndependentLayoutHeight, out var height))
            return null;
        var layout = new IndependentWindowLayout(x, y, width, height);
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

    private static void ShowSettingsFeedback(TextBlock status, string text, bool success)
    {
        status.SetResourceReference(TextBlock.ForegroundProperty, success ? "MutedTextBrush" : "ErrorBrush");
        status.Text = text;
    }
}
