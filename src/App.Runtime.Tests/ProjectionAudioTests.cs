using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunProjectionAudioTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        typeof(LocalizationService).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["zh-CN", false, true]);
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var vm = (MainViewModel)KeyboardField(main, "_viewModel");
            var wired = DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = "projection-audio-usb-fixture", Name = "iPhone", ProductType = "iPhone13,1",
                OsVersion = "17.7", State = ConnectionState.Ready,
            }, DeviceAppearance.Unknown);
            var wireless = DeviceViewModel.FromNative(new NativeDeviceInfo
            {
                Udid = "airplay://projection-audio-fixture", Name = "iPhone AirPlay", State = ConnectionState.Ready,
            }, DeviceAppearance.Unknown);
            void Select(DeviceViewModel? device)
            {
                SetKeyboardField(vm, "_selectedDevice", device);
                KeyboardCall(vm, "NotifyProjectionAudio");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            }

            ((RadioButton)main.FindName("ProjectionSettingsTab")).IsChecked = true;
            KeyboardCall(main, "SetSettingsPanelVisible", true);
            Select(wired);
            var mainControls = FindVisualDescendants<ProjectionAudioSettings>(
                (ScrollViewer)main.FindName("ProjectionSettingsScrollViewer")).Single();
            var floating = new ProjectionSettingsWindow(vm,
                () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, () => { }) { Owner = main, ShowInTaskbar = false };
            floating.Show();
            try
            {
                var floatingControls = FindVisualDescendants<ProjectionAudioSettings>(floating).Single();
                T Control<T>(ProjectionAudioSettings panel, string name) where T : FrameworkElement =>
                    (T)panel.FindName(name);
                var route = Control<ComboBox>(mainControls, "RoutePicker");
                var floatingRoute = Control<ComboBox>(floatingControls, "RoutePicker");
                var buffer = Control<ComboBox>(mainControls, "BufferPicker");
                var sync = Control<CheckBox>(floatingControls, "LipSyncCheckBox");
                var volume = Control<Slider>(floatingControls, "VolumeSlider");
                var mainWindowsAudio = Control<StackPanel>(mainControls, "WindowsAudioControls");
                var floatingWindowsAudio = Control<StackPanel>(floatingControls, "WindowsAudioControls");
                InteractionAssert(route.IsEnabled && route.SelectedIndex == 0 && buffer.Items.Count == 4,
                    "USB defaults must expose Windows audio with four buffer options.");

                app.IsUiPreviewMode = false;
                try
                {
                    buffer.SelectedIndex = 1;
                    sync.IsChecked = true;
                }
                finally { app.IsUiPreviewMode = true; }
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(store.Load().DeviceVideoPreferences[wired.Udid] is
                    { AudioBuffer: ProjectionAudioBuffer.Low, SyncAudioPreview: true } &&
                    Control<CheckBox>(mainControls, "LipSyncCheckBox").IsChecked == true,
                    "Timing controls must persist and synchronize between settings entrances.");

                route.SelectedIndex = 1;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(floatingRoute.SelectedIndex == 1 && floatingRoute.IsEnabled &&
                    mainWindowsAudio.Visibility == Visibility.Collapsed &&
                    floatingWindowsAudio.Visibility == Visibility.Collapsed &&
                    !volume.IsVisible && !sync.IsVisible,
                    "Phone-only routing must collapse Windows audio controls in both entrances.");
                floatingControls.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                SaveWindowRender(floating, Path.Combine(output, "projection-audio-phone-only.png"));

                route.SelectedIndex = 0;
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                InteractionAssert(floatingRoute.SelectedIndex == 0 &&
                    mainWindowsAudio.Visibility == Visibility.Visible &&
                    floatingWindowsAudio.Visibility == Visibility.Visible &&
                    volume.IsEnabled && buffer.SelectedIndex == 1 && sync.IsChecked == true,
                    "Returning to Windows must show the controls with their previous preferences.");

                floatingControls.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                SaveWindowRender(floating, Path.Combine(output, "projection-audio.png"));

                Select(wireless);
                InteractionAssert(!route.IsEnabled && volume.IsEnabled && buffer.IsEnabled &&
                    route.SelectedIndex == 0 && buffer.SelectedIndex == 0,
                    "AirPlay must keep Windows playback controls and use independent preferences.");
                floatingControls.BringIntoView();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                SaveWindowRender(floating, Path.Combine(output, "projection-audio-airplay.png"));
                Select(null);
                InteractionAssert(!route.IsEnabled && !buffer.IsEnabled,
                    "With no source, device-specific controls must be disabled.");
            }
            finally { floating.Close(); }
            Console.WriteLine("PASS projection audio settings, source gating, state synchronization and persistence");
            return 0;
        }
        finally
        {
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }
}
