using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunIndependentCornerProfileTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        using var core = new NativeCore();
        var session = core.CreateExternalSession("independent-corner-fixture-" + Guid.NewGuid().ToString("N"), false, 0);
        InteractionAssert(session.Success && session.Handle is not null,
            "Cannot create a synthetic independent corner session: " + session.Message);
        using var handle = session.Handle!;
        var calls = new List<object>();
        var wheelEvents = new List<PreviewPointerEventArgs>();
        (double Radius, double Exponent) applied = default;
        var rejectCornerChange = false;
        var constructor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
        var parameters = constructor.GetParameters();
        var values = parameters.Select(parameter => parameter.DefaultValue).ToArray();
        object[] required = [1206u, 2622u, "Independent corner profile regression",
            (Func<nint, bool>)(hwnd => NativeCore.AttachDevicePreview(handle.RawHandle, hwnd)),
            (Action<nint>)(hwnd => NativeCore.DetachDevicePreview(handle.RawHandle, hwnd)),
            (Func<nint, bool>)(hwnd => NativeCore.AttachDevicePreview(handle.RawHandle, hwnd)),
            handle.RawHandle, 0d, 2d];
        Array.Copy(required, values, required.Length);
        values[Array.FindIndex(parameters, parameter => parameter.Name == "isUsbControlEnabled")] =
            (Func<bool>)(() => true);
        values[Array.FindIndex(parameters, parameter => parameter.Name == "pointerInput")] =
            (Action<PreviewPointerEventArgs>)(input =>
            {
                if (input.Kind == PreviewPointerKind.Wheel) wheelEvents.Add(input);
            });
        var setterIndex = Array.FindIndex(parameters, parameter => parameter.Name == "setCornerProfile");
        if (setterIndex >= 0) values[setterIndex] = (Func<ulong, nint, double, double, bool>)((sessionHandle, hwnd, radius, exponent) =>
            {
                if (rejectCornerChange) return false;
                var success = NativeCore.SetDeviceWindowCornerProfile(sessionHandle, hwnd, radius, exponent);
                InteractionAssert(success, "The synthetic renderer rejected a window corner profile.");
                applied = (radius, exponent);
                calls.Add(new { radius, exponent, success });
                return success;
            });
        using var preview = (NativePreviewWindow)constructor.Invoke(values);
        try
        {
            InteractionAssert(NativeCore.AttachDevicePreview(handle.RawHandle, preview.Handle),
                "Cannot attach the synthetic independent corner renderer.");
            SetKeyboardField(preview, "_attached", true);

            void Expect(string? product, uint width, uint height, string reason, bool enabled = true)
            {
                var expected = DeviceCornerProfileResolver.Resolve(product, width, height);
                var radius = expected.IsRounded ? expected.RadiusRatio : 0;
                InteractionAssert((double)KeyboardField(preview, "_cornerRadius") == radius &&
                    (double)KeyboardField(preview, "_cornerExponent") == expected.CurveExponent,
                    "An independent preview kept a stale base profile: " + reason);
                InteractionAssert(setterIndex < 0 || applied == (enabled ? radius : 0, expected.CurveExponent),
                    "An independent preview sent the wrong profile to its renderer: " + reason);
                InteractionAssert((bool)KeyboardField(preview, "_cornersEnabled") == enabled,
                    "Refreshing a profile changed the user's corner menu choice: " + reason);
            }

            InteractionAssert(preview.UpdateDeviceAppearance("iPhone19,4", DeviceAppearance.Unknown),
                "Cannot set the Duo fixture model.");
            preview.SetSourceDimensions(2007, 2853);
            Expect("iPhone19,4", 2007, 2853, "placeholder to Duo inner panel");
            preview.SetSourceDimensions(1398, 2034);
            Expect("iPhone19,4", 1398, 2034, "Duo inner to asymmetric outer panel fallback");
            preview.SetSourceDimensions(2853, 2007);
            Expect("iPhone19,4", 2853, 2007, "Duo outer to landscape inner panel");

            InteractionAssert(preview.UpdateDeviceAppearance(null, DeviceAppearance.Unknown),
                "Cannot clear synthetic model metadata.");
            preview.SetSourceDimensions(1206, 2622);
            Expect(null, 1206, 2622, "unknown model tall geometry fallback");
            preview.SetSourceDimensions(1640, 2360);
            Expect(null, 1640, 2360, "unknown model geometry changes before metadata arrives");
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone18,3", DeviceAppearance.Unknown),
                "Cannot provide the late iPhone 17 model metadata.");
            Expect("iPhone18,3", 1640, 2360, "late iPhone 17 model metadata");

            var cornerItem = (System.Windows.Controls.MenuItem)KeyboardField(preview, "_cornerItem");
            var previousHeader = cornerItem.Header;
            rejectCornerChange = true;
            KeyboardCall(preview, "ToggleCorners");
            rejectCornerChange = false;
            Expect("iPhone18,3", 1640, 2360, "failed change keeps the rendered corner profile");
            InteractionAssert(Equals(previousHeader, cornerItem.Header), "A failed corner change updated the menu.");

            KeyboardCall(preview, "ToggleCorners");
            Expect("iPhone18,3", 1640, 2360, "explicit remove corners", enabled: false);
            preview.SetSourceDimensions(1206, 2622);
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone18,1", DeviceAppearance.Unknown),
                "Cannot update the fixture model while corners are disabled.");
            Expect("iPhone18,1", 1206, 2622, "new profile while corners are disabled", enabled: false);
            KeyboardCall(preview, "ToggleCorners");
            Expect("iPhone18,1", 1206, 2622, "restoring corners uses the refreshed base profile");

            InteractionAssert(preview.SetPhoneFrame(true), "Cannot enable the synthetic phone frame.");
            InteractionAssert(!cornerItem.IsEnabled, "The renderer is showing a phone frame, so the corner command must be unavailable.");
            KeyboardCall(preview, "ToggleCorners");
            Expect("iPhone18,1", 1206, 2622, "the frame prevents changes to the corner preference");
            preview.SetSourceDimensions(2007, 2853);
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone19,4", DeviceAppearance.Unknown),
                "Cannot select the Duo inner frame.");
            Expect("iPhone19,4", 2007, 2853, "profile updates with the phone frame enabled");
            InteractionAssert(preview.PhoneFrameEnabled && preview.ActiveDeviceFrame?.Id == "iphone-duo-inner",
                "Updating the corner profile changed the phone frame preference or panel selection.");
            preview.SetSourceDimensions(1398, 2034);
            Expect("iPhone19,4", 1398, 2034, "framed Duo switches to outer fallback");
            InteractionAssert(preview.PhoneFrameEnabled && preview.ActiveDeviceFrame?.Id == "iphone-duo",
                "Switching Duo panels disabled the user's phone frame preference.");
            InteractionAssert(preview.SetPhoneFrame(false), "Cannot disable the synthetic phone frame.");
            InteractionAssert(cornerItem.IsEnabled, "Hiding the phone frame must make corner controls available again.");
            preview.SetSourceDimensions(2007, 2853);
            Expect("iPhone19,4", 2007, 2853, "unframed Duo returns to inner panel");

            var beforeUnchanged = calls.Count;
            preview.SetSourceDimensions(2007, 2853);
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone19,4", DeviceAppearance.Unknown) &&
                calls.Count == beforeUnchanged, "Unchanged geometry or metadata resent the same corner profile.");

            TestIndependentRotationGeometry(core, handle, preview, wheelEvents);
            using var replacement = core.CreateExternalSession("independent-recovery-fixture", false, 0).Handle ??
                throw new InvalidOperationException("Cannot create the replacement preview session.");
            InteractionAssert(!preview.RebindSession(ulong.MaxValue) && preview.SessionHandle == handle.RawHandle,
                "A failed recovery must not report the rejected handle as the bound session.");
            InteractionAssert(preview.RebindSession(replacement.RawHandle) &&
                preview.SessionHandle == replacement.RawHandle && preview.PhoneFrameEnabled,
                "A successful retry must publish the replacement session and restore the phone frame.");
            core.PushExternalFrame(replacement, 1, 120, 260, new byte[120 * 260 * 3 / 2]);
            var retryTime = Stopwatch.StartNew();
            while (NativeCore.GetDeviceWindowPhoneFrameOrientation(replacement.RawHandle, preview.Handle) < 0 &&
                retryTime.Elapsed < TimeSpan.FromSeconds(3))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
            InteractionAssert(NativeCore.GetDeviceWindowPhoneFrameOrientation(replacement.RawHandle, preview.Handle) == 0,
                "The replacement renderer did not present the frame using the retained phone artwork.");
            Console.WriteLine("PASS independent preview recovery: rejected handle and successful retry.");
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                scope = "Real independent HWND and synthetic native renderer; no phone or capture receiver is used.",
                calls,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS independent corner refresh: placeholder/panel changes, portrait/landscape, late metadata, remove/restore corners, and phone frame preference.");
            return 0;
        }
        finally
        {
            preview.Dispose();
            app.Shutdown();
        }
    }

    private static void TestIndependentRotationGeometry(NativeCore core, NativeSessionHandle session,
        NativePreviewWindow preview, List<PreviewPointerEventArgs> wheelEvents)
    {
        InteractionAssert(preview.UpdateDeviceAppearance("iPhone18,3", DeviceAppearance.Unknown) &&
            preview.SetPhoneFrame(true), "Cannot enable the iPhone 17 test frame.");
        KeyboardCall(preview, "ShowInitially");
        KeyboardCall(preview, "ToggleTopMost");
        SetKeyboardField(preview, "_isFixed", true);
        SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => preview.Handle));

        void Push(uint width, uint height) => core.PushExternalFrame(session, 1, width, height,
            Enumerable.Repeat((byte)200, (int)(width * height * 3 / 2)).ToArray());

        int PresentedOrientation() => NativeCore.GetDeviceWindowPhoneFrameOrientation(session.RawHandle, preview.Handle);

        (int Width, int Height) Bounds()
        {
            InteractionAssert(UnfixGetClientRect(preview.Handle, out var client), "Cannot read the independent client size.");
            return (client.Right - client.Left, client.Bottom - client.Top);
        }

        void WaitFor(Func<bool> condition, bool dispatch, string error)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                if (dispatch) DrainDispatcher();
                Thread.Sleep(5);
            }
            InteractionAssert(condition(), error);
        }

        Push(120, 260);
        WaitFor(() => PresentedOrientation() == 0, true, "Portrait did not reach the independent renderer.");
        var portrait = Bounds();
        InteractionAssert(portrait.Width < portrait.Height && preview.ControlGeometry == (120u, 260u, 0),
            "The first source frame did not fit the independent window.");
        TestFramedWheel(preview, portrait, wheelEvents);

        Push(260, 120);
        WaitFor(() => PreviewGeometryProperty(preview.Handle, "iPhoneMirrorPendingPreviewGeometry") ==
            (nint)((260 << 16) | 120), false, "The rotation did not notify the window of its decoded dimensions.");
        Thread.Sleep(100); // Give the render worker time while deliberately withholding the UI message.
        InteractionAssert(PresentedOrientation() == 0 && Bounds() == portrait,
            "A landscape frame reached the portrait-sized window before its resize.");

        WaitFor(() => PresentedOrientation() is 1 or 3 or 5, true,
            "Landscape did not present after the window resized.");
        var landscape = Bounds();
        InteractionAssert(landscape.Width > landscape.Height && preview.ControlGeometry == (260u, 120u, 0),
            "The new landscape frame did not fit the resized independent window.");
        TestFramedWheel(preview, landscape, wheelEvents);
        preview.SetSourceDimensions(120, 260);
        DrainDispatcher();
        InteractionAssert(Bounds() == landscape && preview.ControlGeometry == (260u, 120u, 0),
            "A stale status poll changed the independent window back to portrait.");

        Push(120, 260);
        WaitFor(() => PresentedOrientation() == 0, true, "Portrait did not present after switching back.");
        var restored = Bounds();
        InteractionAssert(restored.Width < restored.Height && preview.ControlGeometry == (120u, 260u, 0),
            "The independent window did not return to portrait.");
        Console.WriteLine("PASS independent rotation: window geometry is applied before presentation and stale status cannot resize it.");
        Console.WriteLine("PASS framed wheel in portrait and landscape: both directions reach the screen, bezel rejected.");
    }

    private static void TestFramedWheel(NativePreviewWindow preview, (int Width, int Height) client,
        List<PreviewPointerEventArgs> wheelEvents)
    {
        var bounds = PhoneFrameGeometry.ScreenBounds(preview.ActiveDeviceFrame!, preview.DeviceFrameRotation,
            client.Width, client.Height);
        void SendWheel(int x, int y, int delta)
        {
            var point = new AbsoluteLivePoint { X = x, Y = y };
            InteractionAssert(AbsoluteLiveClientToScreen(preview.Handle, ref point), "Cannot find wheel screen coordinates.");
            var position = (nint)((point.X & 0xFFFF) | (point.Y << 16));
            PreviewStyleSendMessage(preview.Handle, 0x020A, (nint)(delta << 16), position);
        }

        wheelEvents.Clear();
        var centerX = (int)Math.Round(bounds.Left + bounds.Width / 2);
        var centerY = (int)Math.Round(bounds.Top + bounds.Height / 2);
        SendWheel(centerX, centerY, 120);
        SendWheel(centerX, centerY, -120);
        SendWheel((int)(bounds.Left / 2), centerY, 120);
        var (sourceWidth, sourceHeight, _) = preview.ControlGeometry;
        InteractionAssert(wheelEvents.Select(e => e.Wheel).SequenceEqual([120, -120]) &&
            wheelEvents.All(e => Math.Abs(e.X - sourceWidth / 2.0) <= 2 && Math.Abs(e.Y - sourceHeight / 2.0) <= 2),
            "The independent preview must forward both wheel directions at the mapped screen point and reject the bezel.");
    }

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint PreviewGeometryProperty(nint window, string name);
}
