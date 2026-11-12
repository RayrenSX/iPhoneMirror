using System.IO;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestSettingsPersistence(MainWindow window, App app, MainViewModel vm,
        DeviceViewModel device, UpdateSettingsStore store, string blockedPath)
    {
        var blockedStore = new UpdateSettingsStore(Path.Combine(blockedPath, "settings.json"));
        var themeBox = (ComboBox)window.FindName("ThemeComboBox");
        var originalTheme = app.UpdateSettings.Theme;
        ThemeService.Apply(originalTheme);
        var alternateTheme = originalTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        var mode = vm.SelectedApplicationDisplayMode;
        SetKeyboardField(app, "_settingsStore", blockedStore);
        app.IsUiPreviewMode = false;
        try
        {
            themeBox.SelectedValue = alternateTheme.ToString();
            InteractionAssert(app.UpdateSettings.Theme == originalTheme &&
                ThemeService.Preference == originalTheme &&
                (string)themeBox.SelectedValue == originalTheme.ToString() &&
                vm.ApplicationSettingsStatus.Length > 0,
                "Failed theme save changed the saved theme, active appearance or selection without an error.");
            vm.SelectedApplicationDisplayMode = mode == ApplicationDisplayMode.Complete
                ? ApplicationDisplayMode.Lightweight : ApplicationDisplayMode.Complete;
            InteractionAssert(vm.SelectedApplicationDisplayMode == mode && vm.ApplicationSettingsStatus.Length > 0,
                "Failed application-mode save did not roll back and report the failure.");
            SetKeyboardField(app, "_settingsStore", store);
            themeBox.SelectedValue = alternateTheme.ToString();
            InteractionAssert(app.UpdateSettings.Theme == alternateTheme &&
                ThemeService.Preference == alternateTheme && store.Load().Theme == alternateTheme &&
                vm.ApplicationSettingsStatus.Length == 0,
                "Retrying theme save did not persist/apply the theme or clear the error.");

            var state = (DeviceCaptureState)KeyboardCall(vm, "GetOrCreateDeviceState", device)!;
            var initialMode = state.UsbProjectionMode;
            var desiredMode = vm.UsbProjectionModes.First(option => option.Mode != initialMode);
            SetKeyboardField(app, "_settingsStore", blockedStore);
            vm.SelectedUsbProjectionMode = desiredMode;
            InteractionAssert(state.UsbProjectionMode == initialMode &&
                vm.SelectedUsbProjectionMode?.Mode == initialMode &&
                vm.SettingsStatus == LocalizationService.Get("SetupSaveFailed"),
                "Failed USB-mode save changed the selected mode or claimed success.");
            SetKeyboardField(app, "_settingsStore", store);
            vm.SelectedUsbProjectionMode = desiredMode;
            var restored = new DeviceCaptureState { Udid = device.Udid };
            store.Load().DeviceVideoPreferences[device.Udid].Apply(restored);
            InteractionAssert(restored.UsbProjectionMode == desiredMode.Mode,
                "USB projection mode did not survive a disk save/reload.");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<DeviceVideoPreferences>(
                "{\"Width\":0,\"Height\":0,\"FrameRate\":60,\"Decoder\":0}")!;
            InteractionAssert(legacy.IsValid && legacy.UsbProjectionMode == UsbProjectionMode.Demo,
                "Old video settings without a USB mode lost the existing default.");

            SetKeyboardField(app, "_settingsStore", blockedStore);
            vm.SelectedFrameRate = state.FrameRate == 30 ? 60 : 30;
            WaitReviewTask((Task)KeyboardCall(vm, "ApplyVideoSettingsAsync")!);
            InteractionAssert(state.HasPendingVideoSettings &&
                vm.SettingsStatus == LocalizationService.Get("SetupSaveFailed"),
                "Offline video settings claimed to be saved after a failed write.");
            SetKeyboardField(app, "_settingsStore", store);
            WaitReviewTask((Task)KeyboardCall(vm, "ApplyVideoSettingsAsync")!);
            var saved = store.Load().DeviceVideoPreferences[device.Udid];
            InteractionAssert(!state.HasPendingVideoSettings && saved.FrameRate == state.FrameRate &&
                saved.UsbProjectionMode == desiredMode.Mode,
                "Offline apply retry failed to persist the pending video settings.");

            var previousReceiverName = app.UpdateSettings.WirelessReceiverName;
            SetKeyboardField(app, "_settingsStore", blockedStore);
            var receiverSaved = (bool)KeyboardCall(vm, "PersistWirelessReceiverSettings",
                vm.SelectedWirelessReceiverBackend.Backend, vm.SelectedWirelessDisplayProfile,
                "Settings persistence test")!;
            KeyboardCall(vm, "RefreshWirelessStatus");
            InteractionAssert(!receiverSaved && app.UpdateSettings.WirelessReceiverName == previousReceiverName &&
                vm.WirelessStatus.Contains(LocalizationService.Get("WirelessBackendSettingsSaveFailed")),
                "Wireless save failure changed saved preferences or disappeared on status refresh.");
            SetKeyboardField(app, "_settingsStore", store);
            receiverSaved = (bool)KeyboardCall(vm, "PersistWirelessReceiverSettings",
                vm.SelectedWirelessReceiverBackend.Backend, vm.SelectedWirelessDisplayProfile,
                "Settings persistence test")!;
            KeyboardCall(vm, "RefreshWirelessStatus");
            InteractionAssert(receiverSaved && store.Load().WirelessReceiverName == "Settings persistence test" &&
                !vm.WirelessStatus.Contains(LocalizationService.Get("WirelessBackendSettingsSaveFailed")),
                "Wireless retry did not save preferences or clear the persistence error.");
            TestSettingsComboSelections(window, vm);
        }
        finally
        {
            app.IsUiPreviewMode = true;
            SetKeyboardField(app, "_settingsStore", store);
        }
        Console.WriteLine("PASS settings persistence: theme/mode rollback, USB-mode disk roundtrip/legacy default, offline video failure and retry.");
    }

    private static void TestSettingsComboSelections(MainWindow window, MainViewModel vm)
    {
        var paths = new HashSet<string>
        {
            "SelectedMirroringAutoControlMode", "SelectedUsbProjectionMode", "SelectedWirelessReceiverBackend",
            "SelectedWirelessDisplayProfile", "SelectedResolutionPreset", "SelectedFrameRate",
            "SelectedDecoderPreference", "SelectedBluetoothPortraitMouseDirection", "SelectedBluetoothLandscapeMouseDirection",
        };
        var checkedPaths = new HashSet<string>();
        foreach (var combo in FindVisualDescendants<System.Windows.Controls.Primitives.Selector>(window))
        {
            var expression = System.Windows.Data.BindingOperations.GetBindingExpression(combo,
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
            var path = expression?.ParentBinding.Path.Path;
            if (path is null || !paths.Contains(path) || !ReferenceEquals(combo.DataContext, vm)) continue;
            var property = typeof(MainViewModel).GetProperty(path)!;
            var original = combo.SelectedItem;
            var choices = combo.Items.Cast<object>().Where(item => item is not WirelessDisplayProfile profile ||
                !WirelessReceiverConfiguration.RequiresOriginalQualityWarning(profile)).ToArray();
            foreach (var item in choices.Concat(choices.Reverse()))
            {
                // WPF subscribes to Label. Mutable event subscriptions must never change
                // an option's hash code and invalidate Selector's selected-item lookup.
                if (item is System.ComponentModel.INotifyPropertyChanged observable)
                {
                    var before = item.GetHashCode();
                    System.ComponentModel.PropertyChangedEventHandler observer = (_, _) => { };
                    observable.PropertyChanged += observer;
                    var subscribed = item.GetHashCode();
                    observable.PropertyChanged -= observer;
                    InteractionAssert(before == subscribed && before == item.GetHashCode(),
                        $"{path}: option identity changes when localization bindings subscribe.");
                }
                combo.SelectedItem = item;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(15));
                InteractionAssert(Equals(combo.SelectedItem, item) && Equals(property.GetValue(vm), item),
                    $"{path}: selecting {item} did not update both the actual dropdown and the view model.");
            }
            combo.SelectedItem = original;
            checkedPaths.Add(path);
        }
        InteractionAssert(paths.SetEquals(checkedPaths),
            $"Missing settings dropdowns in the binding audit: {string.Join(", ", paths.Except(checkedPaths))}.");
        Console.WriteLine($"PASS real settings dropdowns: {checkedPaths.Count} binding paths, forward/back selections and stable option identity.");
    }
}
