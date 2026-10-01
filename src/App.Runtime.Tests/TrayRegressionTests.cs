using System.Net;
using System.Net.Http;
using System.Windows;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestTrayAudioSettings(App app, object vm, DeviceViewModel selected)
    {
        var sessions = KeyboardField(vm, "_sessions");
        var other = TrayTestDevice("tray-audio-other", "Other device");
        var extra = TrayTestDevice("tray-audio-extra", "Complete mode extra window");
        var originalAudio = KeyboardField(vm, "_playAudio");
        var originalVolume = KeyboardField(vm, "_playbackVolume");
        try
        {
            InteractionAssert(KeyboardCall(sessions, "Get", selected.Udid) is null &&
                (bool)KeyboardField(vm, "PlayAudio"), "Expected a fresh selected device with audio enabled.");
            var state = KeyboardCall(vm, "GetOrCreateIndependentDeviceState", selected)!;
            InteractionAssert((bool)KeyboardField(state, "PlayAudio") &&
                Equals(KeyboardField(state, "Volume"), KeyboardField(vm, "PlaybackVolume")) &&
                Equals(KeyboardField(state, "FrameRate"), KeyboardField(vm, "SelectedFrameRate")),
                "First tray projection did not preserve the selected device controls.");

            vm.GetType().GetProperty("PlayAudio")!.SetValue(vm, false);
            vm.GetType().GetProperty("PlaybackVolume")!.SetValue(vm, 37d);
            InteractionAssert(ReferenceEquals(state, KeyboardCall(vm, "GetOrCreateIndependentDeviceState", selected)) &&
                !(bool)KeyboardField(state, "PlayAudio") && (double)KeyboardField(state, "Volume") == 37d,
                "Reopening projection reset the user's mute or volume.");
            KeyboardCall(sessions, "Remove", selected.Udid);
            state = KeyboardCall(vm, "GetOrCreateIndependentDeviceState", selected)!;
            InteractionAssert(!(bool)KeyboardField(state, "PlayAudio") && (double)KeyboardField(state, "Volume") == 37d,
                "Fresh tray projection ignored pre-session mute and volume.");

            var otherState = KeyboardCall(vm, "GetOrCreateIndependentDeviceState", other)!;
            InteractionAssert((bool)KeyboardField(otherState, "PlayAudio") &&
                (double)KeyboardField(otherState, "Volume") == 100d,
                "Another tray device inherited the selected device's mute or volume.");
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
            var extraState = KeyboardCall(vm, "GetOrCreateIndependentDeviceState", extra)!;
            InteractionAssert(!(bool)KeyboardField(extraState, "PlayAudio"),
                "Complete mode extra windows must retain their muted default.");
        }
        finally
        {
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
            foreach (var device in new[] { selected, other, extra }) KeyboardCall(sessions, "Remove", device.Udid);
            SetKeyboardField(vm, "_playAudio", originalAudio);
            SetKeyboardField(vm, "_playbackVolume", originalVolume);
        }
    }

    private static void TestTrayMediaStop(MainWindow main, object vm)
    {
        // Use the real independent window, but no media source or receiver.
        // The stop event is published by the controller request path even when
        // the receiver is unavailable; direct local cleanup never publishes it.
        var requests = 0;
        Action stopped = () => requests++;
        var stopEvent = vm.GetType().GetEvent("MediaCastStopRequested", KeyboardTestMembers)!;
        stopEvent.GetAddMethod(true)!.Invoke(vm, [stopped]);
        try
        {
            foreach (var closeWindow in new[] { true, false })
            {
                var before = requests;
                KeyboardCall(vm, "BeginMediaCast", 1d);
                SetKeyboardField(main, "_mediaCastActive", true);
                KeyboardCall(main, "ShowMediaCastPreviewWindow");
                var mediaWindow = (IDisposable)KeyboardField(main, "_mediaCastPreviewWindow");
                InteractionAssert(!main.IsVisible, "Media projection showed the main workspace.");
                if (closeWindow) mediaWindow.Dispose();
                else KeyboardCall(vm, "RequestMediaCastStop", false);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                mediaWindow.Dispose(); // Repeated teardown must not send another stop.
                InteractionAssert(requests == before + 1, "Media stop must notify the controller exactly once.");
                InteractionAssert(!(bool)KeyboardField(main, "_mediaCastActive") &&
                    !(bool)KeyboardField(vm, "IsMediaCasting") &&
                    KeyboardField(main, "_mediaCastPreviewWindow") is null && !main.IsVisible &&
                    KeyboardField(main, "_trayIcon") is not null,
                    "Stopping media projection did not clean up playback and preserve tray residency.");
            }
        }
        finally { stopEvent.GetRemoveMethod(true)!.Invoke(vm, [stopped]); }
    }

    private static void TestTrayStartupUpdates(App app, MainWindow main, object vm)
    {
        var settings = KeyboardField(app, "UpdateSettings");
        var savedSettings = KeyboardCall(settings, "Clone")!;
        var originalClient = KeyboardField(app, "_releaseClient");
        var launchProperty = typeof(App).GetProperty("LaunchOptions", KeyboardTestMembers)!;
        var originalLaunch = launchProperty.GetValue(app)!;
        var originalStarted = KeyboardField(app, "_startupUpdateCheckStarted");
        using var handler = new TrayUpdateHandler();
        using var http = new HttpClient(handler);
        using var client = (IDisposable)Activator.CreateInstance(originalClient.GetType(),
            KeyboardTestMembers, null, [http, null], null)!;
        void SetSetting(string name, bool value) => settings.GetType().GetProperty(name)!.SetValue(settings, value);
        void Reset(bool enabled = true, bool compact = false)
        {
            SetKeyboardField(app, "_startupUpdateCheckStarted", false);
            SetSetting("CheckOnStartup", enabled);
            launchProperty.SetValue(app, Activator.CreateInstance(originalLaunch.GetType(),
                KeyboardTestMembers, null, [compact, null], null));
            handler.ReleaseRequests = 0;
            handler.ReturnUpdate = false;
            handler.Fail = false;
        }
        void Start()
        {
            // Exercise the same entry used by OnStartup, including hidden tray startup.
            KeyboardCall(app, "ShowInitialWindow");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
        }
        try
        {
            SetKeyboardField(app, "_releaseClient", client);
            SetSetting("AutoDownload", false);
            SetSetting("AllowMirrorFallback", false);
            SetSetting("NotifyStableReleases", true);
            Reset(enabled: false);
            Start();
            InteractionAssert(handler.ReleaseRequests == 0, "Disabled startup update checks still used HTTP.");
            Reset(compact: true);
            Start();
            InteractionAssert(handler.ReleaseRequests == 0, "Compact launch unexpectedly checked for updates.");

            Reset();
            Start();
            InteractionAssert(handler.ReleaseRequests == 1 && !main.IsVisible,
                "Tray startup skipped updates or showed the main workspace.");
            Start();
            InteractionAssert(handler.ReleaseRequests == 1, "Startup update check ran more than once.");

            Reset();
            handler.ReturnUpdate = true;
            Start();
            InteractionAssert(handler.ReleaseRequests == 1 &&
                KeyboardField(app, "_updateWindow") is Window { IsVisible: true } && !main.IsVisible,
                "An available update was not presented from hidden tray startup.");
            ((Window)KeyboardField(app, "_updateWindow")).Close();

            Reset();
            handler.Fail = true;
            Start();
            InteractionAssert(handler.ReleaseRequests == 1 && KeyboardField(app, "_updateWindow") is null && !main.IsVisible,
                "A failed update check disrupted tray startup.");
            KeyboardCall(main, "ShowTrayPanel");
            var panel = (Window)KeyboardField(main, "_trayPanel");
            InteractionAssert(panel.IsVisible, "Tray panel stopped working after an update failure.");
            panel.Hide();

            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, mode);
                Reset();
                Start();
                InteractionAssert(handler.ReleaseRequests == 1 && main.IsVisible,
                    $"Startup update checks regressed in {mode} mode.");
            }
        }
        finally
        {
            if (KeyboardField(app, "_updateWindow") is Window update) update.Close();
            vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, ApplicationDisplayMode.Tray);
            KeyboardCall(app, "RestoreUpdateSettings", savedSettings);
            launchProperty.SetValue(app, originalLaunch);
            SetKeyboardField(app, "_startupUpdateCheckStarted", originalStarted);
            SetKeyboardField(app, "_releaseClient", originalClient);
        }
    }

    private sealed class TrayUpdateHandler : HttpMessageHandler
    {
        internal int ReleaseRequests;
        internal bool ReturnUpdate;
        internal bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var releaseList = request.RequestUri!.Host == "api.github.com";
            if (releaseList) ReleaseRequests++;
            var content = releaseList
                ? ReturnUpdate
                    ? """[{"tag_name":"v999.0.0","name":"Test update","body":"Test","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z","assets":[]}]"""
                    : "[]"
                : "# Test update\nOffline tray regression fixture.";
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content),
            });
        }
    }
}
