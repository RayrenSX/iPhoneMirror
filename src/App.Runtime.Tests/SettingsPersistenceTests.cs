using System.IO;
using IPhoneMirror.App.Interop;
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
            TestDeviceControlPreferences(app, vm, device, store, blockedStore);
            TestSettingsComboSelections(window, vm);
        }
        finally
        {
            app.IsUiPreviewMode = true;
            SetKeyboardField(app, "_settingsStore", store);
        }
        Console.WriteLine("PASS settings persistence: theme/mode rollback, USB-mode disk roundtrip/legacy default, offline video failure and retry.");
    }

    private static int RunDeviceControlPreferencesTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Drain WPF's queued startup while discovery and external services are disabled.
        AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        try
        {
            var vm = new MainViewModel();
            var first = DeviceViewModel.FromNative(new NativeDeviceInfo
            { Udid = "control-settings-first", Name = "First phone", State = ConnectionState.Ready }, DeviceAppearance.Unknown);
            vm.Devices.Add(first);
            var blockedPath = Path.Combine(Path.GetFullPath(output), "blocked-parent");
            File.WriteAllText(blockedPath, "blocked");
            app.IsUiPreviewMode = false;
            TestDeviceControlPreferences(app, vm, first, store,
                new UpdateSettingsStore(Path.Combine(blockedPath, "settings.json")));
            TestDeviceControlPreferencesDuringSave(app, vm, store, output);
            app.RestoreUpdateSettings(store.Load());
            var restored = new MainViewModel();
            foreach (var (udid, saved) in app.UpdateSettings.DeviceControlPreferences)
                InteractionAssert(restored.ControlPreferencesFor(udid.ToUpperInvariant()) == saved,
                    "Restarting the view model did not restore case-insensitive per-device input settings.");
            Console.WriteLine("PASS per-device input settings reload at startup.");
            TestAutomationDeviceControlPreferences(output);
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            app.Shutdown();
        }
    }

    private static void TestDeviceControlPreferencesDuringSave(App app, MainViewModel vm,
        UpdateSettingsStore store, string output)
    {
        var device = vm.SelectedDevice!;
        var applied = vm.ControlPreferencesFor(device.Udid);
        var other = app.UpdateSettings.DeviceControlPreferences.First(pair => !DeviceViewModel.UdidEquals(pair.Key, device.Udid));
        var lockedPath = Path.Combine(Path.GetFullPath(output), "locked-settings.json");
        File.WriteAllText(lockedPath, "{}");
        var lockedStore = new UpdateSettingsStore(lockedPath);
        // Keep serialization in progress long enough for the dedicated reader
        // to observe the same configuration that automation guards read.
        const string paddingKey = "control-save-test-padding";
        app.UpdateSettings.DeviceFrameColors[paddingKey] = new string('x', 8_000_000);
        vm.BluetoothMouseReverseHorizontal = !applied.ReverseHorizontal;
        vm.BluetoothMouseSensitivity = applied.MouseSensitivity == 456 ? 457 : 456;
        using var ready = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var readCount = 0;
        var observedUnsaved = false;
        var reader = Task.Factory.StartNew(() =>
        {
            ready.Set();
            while (!stop.IsCancellationRequested)
            {
                if (vm.ControlPreferencesFor(device.Udid) != applied || vm.ControlPreferencesFor(other.Key) != other.Value)
                    observedUnsaved = true;
                Interlocked.Increment(ref readCount);
                Thread.Yield();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            InteractionAssert(ready.Wait(TimeSpan.FromSeconds(5)), "Background settings reader did not start.");
            SetKeyboardField(app, "_settingsStore", lockedStore);
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                KeyboardCall(vm, "ApplyBluetoothMouseSettings");
        }
        finally
        {
            stop.Cancel();
            reader.GetAwaiter().GetResult();
            SetKeyboardField(app, "_settingsStore", store);
            app.UpdateSettings.DeviceFrameColors.Remove(paddingKey);
        }
        InteractionAssert(readCount > 0 && !observedUnsaved && vm.ControlPreferencesFor(device.Udid) == applied &&
            vm.MouseSettingsStatus.Length > 0 && vm.HasPendingBluetoothMouseSettings,
            "A background input reader observed uncommitted settings during a failed save.");

        KeyboardCall(vm, "ApplyBluetoothMouseSettings");
        var committed = vm.ControlPreferencesFor(device.Udid);
        var fromWorker = Task.Run(() => vm.ControlPreferencesFor(device.Udid)).GetAwaiter().GetResult();
        InteractionAssert(committed.ReverseHorizontal != applied.ReverseHorizontal &&
            committed.MouseSensitivity == vm.BluetoothMouseSensitivity && fromWorker == committed &&
            vm.ControlPreferencesFor(other.Key) == other.Value &&
            store.Load().DeviceControlPreferences[device.Udid] == committed && vm.MouseSettingsStatus.Length == 0 &&
            !vm.HasPendingBluetoothMouseSettings,
            "A successful retry did not publish the saved device settings to background readers.");
        Console.WriteLine("PASS per-device save publication: background input keeps committed settings throughout failed writes and sees a successful retry.");
    }

    private static void TestDeviceControlPreferences(App app, MainViewModel vm, DeviceViewModel first,
        UpdateSettingsStore store, UpdateSettingsStore blockedStore)
    {
        var second = DeviceViewModel.FromNative(new IPhoneMirror.App.Interop.NativeDeviceInfo
        { Udid = "control-settings-second", Name = "Second phone", State = ConnectionState.Ready }, DeviceAppearance.Unknown);
        void SelectControlDevice(DeviceViewModel device)
        {
            KeyboardCall(vm, "SetSelectedDevice", device, false);
        }
        vm.Devices.Add(second);
        SelectControlDevice(first);
        var legacy = DeviceControlPreferences.FromLegacy(app.UpdateSettings).Normalize();
        var initial = vm.ControlPreferencesFor(first.Udid);
        InteractionAssert(vm.ControlPreferencesFor(second.Udid) == legacy,
            "An unconfigured device must inherit the legacy settings.");
        vm.BluetoothMouseReverseHorizontal = !initial.ReverseHorizontal;
        vm.BluetoothMouseSensitivity = 123;
        KeyboardCall(vm, "ApplyBluetoothMouseSettings");
        var firstSaved = vm.ControlPreferencesFor(first.Udid);
        InteractionAssert(firstSaved.ReverseHorizontal != initial.ReverseHorizontal && firstSaved.MouseSensitivity == 123,
            "Device-specific direction and sensitivity were not applied.");
        vm.BluetoothWheelSensitivity = 234; // Unsaved drafts must stay with their device.
        SelectControlDevice(second);
        InteractionAssert(vm.ControlPreferencesFor(second.Udid) == legacy &&
            vm.BluetoothMouseReverseHorizontal == legacy.ReverseHorizontal && !vm.HasPendingBluetoothMouseSettings,
            "Selecting the second device inherited the first device's changes.");
        vm.BluetoothMouseReverseVertical = !legacy.ReverseVertical;
        vm.BluetoothMouseSensitivity = 345;
        SetKeyboardField(app, "_settingsStore", blockedStore);
        KeyboardCall(vm, "ApplyBluetoothMouseSettings");
        InteractionAssert(vm.ControlPreferencesFor(second.Udid) == legacy && vm.HasPendingBluetoothMouseSettings &&
            vm.MouseSettingsStatus.Length > 0 && vm.ControlPreferencesFor(first.Udid) == firstSaved,
            "Failed device settings save leaked into active settings or another device.");
        SetKeyboardField(app, "_settingsStore", store);
        KeyboardCall(vm, "ApplyBluetoothMouseSettings");
        var secondSaved = vm.ControlPreferencesFor(second.Udid);
        SelectControlDevice(first);
        InteractionAssert(vm.BluetoothMouseSensitivity == 123 && vm.BluetoothWheelSensitivity == 234 &&
            vm.HasPendingBluetoothSensitivitySettings && vm.ControlPreferencesFor(first.Udid) == firstSaved &&
            vm.ControlPreferencesFor(second.Udid) == secondSaved,
            "Switching devices lost a draft or changed another window's applied settings.");
        var loaded = store.Load();
        InteractionAssert(loaded.DeviceControlPreferences[first.Udid] == firstSaved &&
            loaded.DeviceControlPreferences[second.Udid] == secondSaved &&
            DeviceControlPreferences.FromLegacy(loaded).Normalize() == legacy,
            "Per-device preferences did not survive disk reload or modified the legacy defaults.");
        var clone = loaded.Clone();
        clone.DeviceControlPreferences[first.Udid] = secondSaved;
        InteractionAssert(loaded.DeviceControlPreferences[first.Udid] == firstSaved,
            "Settings rollback clone shares its per-device dictionary.");
        vm.Devices.Remove(second);
        Console.WriteLine("PASS per-device control settings: independent direction/sensitivity, drafts, failed save rollback and disk reload.");
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
