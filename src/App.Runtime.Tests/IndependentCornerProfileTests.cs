using System.IO;
using System.Text.Json;
using System.Windows;
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
        (double Radius, double Exponent) applied = default;
        var constructor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
        var parameters = constructor.GetParameters();
        var values = parameters.Select(parameter => parameter.DefaultValue).ToArray();
        object[] required = [1206u, 2622u, "Independent corner profile regression",
            (Func<nint, bool>)(hwnd => NativeCore.AttachDevicePreview(handle.RawHandle, hwnd)),
            (Action<nint>)(hwnd => NativeCore.DetachDevicePreview(handle.RawHandle, hwnd)),
            (Func<nint, bool>)(hwnd => NativeCore.AttachDevicePreview(handle.RawHandle, hwnd)),
            handle.RawHandle, 0d, 2d];
        Array.Copy(required, values, required.Length);
        var setterIndex = Array.FindIndex(parameters, parameter => parameter.Name == "setCornerProfile");
        if (setterIndex >= 0) values[setterIndex] = (Func<ulong, nint, double, double, bool>)((sessionHandle, hwnd, radius, exponent) =>
            {
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

            KeyboardCall(preview, "ToggleCorners");
            Expect("iPhone18,3", 1640, 2360, "explicit remove corners", enabled: false);
            preview.SetSourceDimensions(1206, 2622);
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone18,1", DeviceAppearance.Unknown),
                "Cannot update the fixture model while corners are disabled.");
            Expect("iPhone18,1", 1206, 2622, "new profile while corners are disabled", enabled: false);
            KeyboardCall(preview, "ToggleCorners");
            Expect("iPhone18,1", 1206, 2622, "restoring corners uses the refreshed base profile");

            InteractionAssert(preview.SetPhoneFrame(true), "Cannot enable the synthetic phone frame.");
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
            preview.SetSourceDimensions(2007, 2853);
            Expect("iPhone19,4", 2007, 2853, "unframed Duo returns to inner panel");

            var beforeUnchanged = calls.Count;
            preview.SetSourceDimensions(2007, 2853);
            InteractionAssert(preview.UpdateDeviceAppearance("iPhone19,4", DeviceAppearance.Unknown) &&
                calls.Count == beforeUnchanged, "Unchanged geometry or metadata resent the same corner profile.");
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
}
