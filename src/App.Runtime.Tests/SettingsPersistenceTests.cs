using System.IO;
using System.Text.Json;
using System.Windows;
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
    private static void TestDeviceSettingsRecovery(string output)
    {
        var path = Path.Combine(output, "device-settings-recovery.json");
        const string json = """
            {
              "Language":"zh-CN", "Theme":2,
              "DeviceFrameColors":{"PHONE":"black","phone":"white","PHONE":"blue","pHoNe":false,"other":"silver"},
              "BluetoothControlDeviceBindings":{"PHONE":"valid-client","phone":null},
              "IndependentWindowLayoutHistoryByDevice":{"phone":{"LastLayout":{"X":40,"Y":50,"Width":320,"Height":640}},"missing":null},
              "DeviceVideoPreferences":{"phone":{"Width":1080,"Height":1920,"FrameRate":60,"Decoder":0},"broken":{"UsbProjectionMode":"bad"}}
            }
            """;
        File.WriteAllText(path, json);
        var store = new UpdateSettingsStore(path);
        var settings = store.Load(out var readFailed, persistMigrations: false);
        InteractionAssert(!readFailed && File.ReadAllText(path) == json && settings.Language == "zh-CN" &&
            settings.Theme == AppTheme.Light, "Recovering one damaged device record lost unrelated settings or wrote during a read-only load.");
        InteractionAssert(settings.DeviceFrameColors.Count == 2 &&
            settings.DeviceFrameColors["phone"] == "blue" && settings.DeviceFrameColors["OTHER"] == "silver" &&
            settings.BluetoothControlDeviceBindings["phone"] == "valid-client",
            "Recovery did not keep the last valid entry in actual file order.");
        InteractionAssert(settings.IndependentWindowLayoutHistoryByDevice.Count == 1 &&
            settings.IndependentWindowLayoutHistoryByDevice["PHONE"].LastLayout?.Width == 320 &&
            settings.DeviceVideoPreferences.Count == 1 && settings.DeviceVideoPreferences["PHONE"].FrameRate == 60,
            "A malformed record prevented a different device from restoring its settings.");

        store.Load();
        var repaired = File.ReadAllText(path);
        using (var file = JsonDocument.Parse(repaired))
            InteractionAssert(file.RootElement.GetProperty("DeviceFrameColors").EnumerateObject().Count() == 2 &&
                file.RootElement.GetProperty("IndependentWindowLayoutHistoryByDevice").EnumerateObject().Count() == 1,
                "Device settings recovery was not persisted.");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var reloaded = store.Load(out readFailed);
        InteractionAssert(!readFailed && reloaded.DeviceFrameColors["phone"] == "blue" &&
            File.GetLastWriteTimeUtc(path) == lastWrite, "An already repaired file was rewritten.");

        const string invalidDocument = "{\"DeviceVideoPreferences\":";
        File.WriteAllText(path, invalidDocument);
        _ = store.Load(out readFailed);
        InteractionAssert(readFailed && File.ReadAllText(path) == invalidDocument,
            "A truncated settings file must never be silently overwritten.");
        Console.WriteLine("PASS device settings recovery: JSON order, invalid entries, case-insensitive lookup, read-only load and one-time repair.");
    }

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
            TestSetupVideoPreferenceRecovery(app, vm, device, store, blockedStore);
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
            InteractionAssert(legacy.IsValid && legacy.UsbProjectionMode == UsbProjectionMode.AirPlay,
                "Video settings without a USB mode must use recommended native mode.");
            InteractionAssert(vm.UsbProjectionModes.Count == 2 &&
                vm.UsbProjectionModes[0].Mode == UsbProjectionMode.AirPlay &&
                vm.UsbProjectionModes[1].Mode == UsbProjectionMode.Demo,
                "Projection settings must list recommended A before compatible B, without C.");
            var migrationPath = Path.Combine(Path.GetDirectoryName(blockedPath)!, "usb-mode-migration.json");
            File.WriteAllText(migrationPath, """
                {"DeviceVideoPreferences":{
                  "old-a":{"Width":1920,"Height":1080,"FrameRate":30,"Decoder":2,"UsbProjectionMode":0},
                  "old-b":{"Width":0,"Height":0,"FrameRate":60,"Decoder":0,"UsbProjectionMode":1},
                  "old-c":{"Width":1280,"Height":720,"FrameRate":30,"Decoder":2,"UsbProjectionMode":2,"SyncAudioPreview":true,"AudioBuffer":2}
                }}
                """);
            var migratedStore = new UpdateSettingsStore(migrationPath);
            var migrated = migratedStore.Load();
            InteractionAssert(migrated.DeviceVideoPreferences["old-a"].UsbProjectionMode == UsbProjectionMode.Demo &&
                migrated.DeviceVideoPreferences["old-b"].UsbProjectionMode == UsbProjectionMode.AirPlay &&
                migrated.DeviceVideoPreferences["old-c"] is { UsbProjectionMode: UsbProjectionMode.AirPlay,
                    Width: 1280, Height: 720, FrameRate: 30, Decoder: DecoderPreference.SoftwareCompatible,
                    SyncAudioPreview: true, AudioBuffer: ProjectionAudioBuffer.Normal },
                "Upgrade must preserve A/B protocols and migrate C to recommended without losing other settings.");
            using (var migratedJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(migrationPath)))
                InteractionAssert(migratedJson.RootElement.GetProperty("DeviceVideoPreferences")
                    .GetProperty("old-c").GetProperty("UsbProjectionMode").GetUInt32() == 1 &&
                    migratedStore.Load().DeviceVideoPreferences["old-c"].UsbProjectionMode == UsbProjectionMode.AirPlay,
                    "C migration must survive save and repeated reload.");

            SetKeyboardField(app, "_settingsStore", blockedStore);
            var previousRate = state.FrameRate;
            var nextRate = previousRate == 30 ? 60 : 30;
            vm.SelectedFrameRate = nextRate;
            InteractionAssert(state.FrameRate == previousRate && vm.SelectedFrameRate == previousRate &&
                vm.SettingsStatus == LocalizationService.Get("SetupSaveFailed"),
                "Automatic frame-rate save must roll back the dropdown and state after a failed write.");
            SetKeyboardField(app, "_settingsStore", store);
            vm.SelectedFrameRate = nextRate;
            var saved = store.Load().DeviceVideoPreferences[device.Udid];
            InteractionAssert(!state.HasPendingVideoSettings && saved.FrameRate == state.FrameRate &&
                saved.FrameRate == nextRate && saved.UsbProjectionMode == desiredMode.Mode &&
                vm.VideoSettingsFeedbackVisibility == Visibility.Collapsed,
                "Changing frame rate must automatically save and clear obsolete apply feedback.");
            var previousPreset = vm.SelectedResolutionPreset;
            var nextPreset = vm.ResolutionPresets.First(p => p != previousPreset);
            var previousDecoder = vm.SelectedDecoderPreference!;
            var nextDecoder = vm.DecoderPreferences.First(p => p.Preference != previousDecoder.Preference);
            SetKeyboardField(app, "_settingsStore", blockedStore);
            vm.SelectedResolutionPreset = nextPreset;
            vm.SelectedDecoderPreference = nextDecoder;
            InteractionAssert(vm.SelectedResolutionPreset == previousPreset &&
                vm.SelectedDecoderPreference == previousDecoder,
                "Failed automatic resolution/decoder saves must restore both selections.");
            SetKeyboardField(app, "_settingsStore", store);
            vm.SelectedResolutionPreset = nextPreset;
            vm.SelectedDecoderPreference = nextDecoder;
            saved = store.Load().DeviceVideoPreferences[device.Udid];
            InteractionAssert(saved.Width == nextPreset.Width && saved.Height == nextPreset.Height &&
                saved.Decoder == nextDecoder.Preference && !state.HasPendingVideoSettings,
                "Resolution and decoder selections must save automatically without an Apply command.");
            TestAutomaticLiveVideoSettings(vm, device, state);

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

    private static void TestSetupVideoPreferenceRecovery(App app, MainViewModel vm, DeviceViewModel device,
        UpdateSettingsStore store, UpdateSettingsStore blockedStore)
    {
        var preferences = app.UpdateSettings.DeviceVideoPreferences;
        var legacy = new Dictionary<string, DeviceVideoPreferences>(preferences, StringComparer.Ordinal)
        {
            ["Setup-Other"] = new(720, 1280, 30, DecoderPreference.Auto),
            ["setup-other"] = new(1080, 1920, 60, DecoderPreference.Auto),
        };
        app.UpdateSettings.DeviceVideoPreferences = legacy;
        try
        {
            SetKeyboardField(app, "_settingsStore", blockedStore);
            InteractionAssert(!vm.SetupSaveVideoPreferences([device.Udid]) &&
                ReferenceEquals(app.UpdateSettings.DeviceVideoPreferences, legacy) && legacy.Count == preferences.Count + 2,
                "Failed setup save must leave the previous configuration intact.");
            SetKeyboardField(app, "_settingsStore", store);
            InteractionAssert(vm.SetupSaveVideoPreferences([device.Udid]) &&
                app.UpdateSettings.DeviceVideoPreferences["SETUP-OTHER"].Width == 1080 &&
                store.Load().DeviceVideoPreferences.Keys.Count(key => key.Equals("setup-other", StringComparison.OrdinalIgnoreCase)) == 1,
                "Setup retry lost the other device or wrote another duplicate.");
        }
        finally
        {
            app.UpdateSettings.DeviceVideoPreferences = preferences;
            SetKeyboardField(app, "_settingsStore", store);
        }
    }

    private static void TestAutomaticLiveVideoSettings(MainViewModel vm, DeviceViewModel device,
        DeviceCaptureState state)
    {
        var core = (NativeCore)KeyboardField(vm, "_core");
        var created = core.CreateExternalSession(device.Udid, false, 0);
        InteractionAssert(created.Success && created.Handle is not null,
            "The automatic-settings native session fixture could not start.");
        using var handle = created.Handle!;
        state.Handle = handle;
        var gate = (SemaphoreSlim)KeyboardField(vm, "_coreGate");
        var gateHeld = false;
        try
        {
            core.PushExternalFrame(handle, 1, 64, 96, new byte[64 * 96 * 3 / 2]);
            var before = core.GetDeviceSessionStatus(handle);
            var selectedRate = state.FrameRate == 60 ? 30 : 60;
            gate.Wait();
            gateHeld = true;
            vm.SelectedFrameRate = selectedRate;
            InteractionAssert(vm.IsBusy, "An active preference edit must submit without an Apply button.");
            var other = DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = "automatic-video-other-source", Name = "Other source", State = ConnectionState.Ready,
            }, DeviceAppearance.Unknown);
            var otherState = (DeviceCaptureState)KeyboardCall(vm, "GetOrCreateDeviceState", other)!;
            var previousOtherRate = otherState.FrameRate;
            SetKeyboardField(vm, "_selectedDevice", other);
            gate.Release();
            gateHeld = false;
            WaitReviewTask((Task)KeyboardField(vm, "_automaticVideoSettingsTask"));
            InteractionAssert(state.Handle?.RawHandle == handle.RawHandle &&
                state.AppliedFrameRate == selectedRate && otherState.FrameRate == previousOtherRate &&
                core.GetDeviceSessionStatus(handle).VideoFrames == before.VideoFrames,
                "Automatic apply changed a newer selected source or restarted the native source.");
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(vm, "RestoreSelectedVideoControls", state);
            vm.SelectedDecoderPreference = vm.DecoderPreferences.First(p => p.Preference != state.DecoderPreference);
            WaitReviewTask((Task)KeyboardField(vm, "_automaticVideoSettingsTask"));
            InteractionAssert(core.TryGetDeviceVideoOutputStatus(handle, out var pipeline) &&
                pipeline.RequestedDecoderPreference == (uint)state.DecoderPreference,
                "An automatic decoder edit did not reach the existing native session.");
            core.PushExternalFrame(handle, 1, 64, 96, new byte[64 * 96 * 3 / 2]);
            InteractionAssert(core.GetDeviceSessionStatus(handle).VideoFrames > before.VideoFrames,
                "The native source stopped advancing after automatic preference edits.");
        }
        finally
        {
            if (gateHeld) gate.Release();
            SetKeyboardField(vm, "_selectedDevice", device);
            state.Handle = null;
            KeyboardCall(vm, "RestoreSelectedVideoControls", state);
        }
        Console.WriteLine("PASS automatic live video edits: native handle retained, decoder submitted, source switch isolated, frames advancing.");
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
