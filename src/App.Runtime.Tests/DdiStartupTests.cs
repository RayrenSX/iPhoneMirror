using System.Reflection;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDdiStartupTests(string? packagedBridge = null)
    {
        var bundleCheck = typeof(DirectUsbInputBridge).GetMethod("HasCompletePersonalizedDdiBundle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var bundleDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddi-alias-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(bundleDirectory);
        var files = new[] { "Image.dmg", "BuildManifest.plist", "Image.dmg.trustcache" };
        try
        {
            foreach (var name in files) System.IO.File.WriteAllText(System.IO.Path.Combine(bundleDirectory, name), "fixture");
            if (!(bool)bundleCheck.Invoke(null, [bundleDirectory])!)
                throw new InvalidOperationException("The official trustcache filename was skipped.");
            System.IO.File.WriteAllText(System.IO.Path.Combine(bundleDirectory, "Image.dmg.trustcache"), "");
            if ((bool)bundleCheck.Invoke(null, [bundleDirectory])!)
                throw new InvalidOperationException("An empty trustcache was accepted.");
        }
        finally
        {
            foreach (var name in files) System.IO.File.Delete(System.IO.Path.Combine(bundleDirectory, name));
            System.IO.Directory.Delete(bundleDirectory);
        }
        // Without an Application, localization returns resource keys. This
        // exercises actual exception routing without touching user preferences.
        var failureMessage = typeof(MainViewModel).GetMethod("GetUsbControlFailureMessage",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var innerField = typeof(UsbTouchBridgeHost).GetField("_bridge", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handleLine = typeof(DirectUsbInputBridge).GetMethod("HandleLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cases = new Dictionary<string, string>
        {
            ["developer_image_tss_timeout"] = "UsbControlFailureImageTssTimeout",
            ["developer_image_service_timeout"] = "UsbControlFailureImageServiceTimeout",
            ["developer_image_upload_timeout"] = "UsbControlFailureImageUploadTimeout",
            ["developer_image_prepare_timeout"] = "UsbControlFailureImagePrepareTimeout",
            ["developer_image_mount_timeout"] = "UsbControlFailureImagePrepareTimeout",
            ["developer_image_tss_failed"] = "UsbControlFailureImageTss",
            ["developer_image_tss_rejected"] = "UsbControlFailureImageTssRejected",
            ["developer_image_download_timeout"] = "UsbControlFailureImageDownload",
            ["developer_image_cache_failed"] = "UsbControlFailureImageCache",
            ["bridge_start_timeout"] = "UsbControlFailureStartTimeout",
            ["bridge_runtime_invalid"] = "UsbControlFailureRuntimeInvalid",
            ["developer_mode_check_failed"] = "UsbControlFailureDeveloperModeCheck",
            ["developer_mode_check_timeout"] = "UsbControlFailureDeveloperModeCheck",
            ["developer_mode_required"] = "UsbControlFailureDeveloperMode",
            ["apple_device_locked"] = "UsbControlFailureDeviceLocked",
            ["apple_connection_lost"] = "UsbControlFailureConnectionLost",
            ["developer_image_service_unavailable"] = "UsbControlFailureImageServiceUnavailable",
            ["developer_image_upload_failed"] = "UsbControlFailureImageUploadFailed",
            ["developer_image_verification_failed"] = "UsbControlFailureImageVerification",
            ["developer_image_tss_unavailable"] = "UsbControlFailureImageTssUnavailable",
            ["device_identity_mismatch"] = "UsbControlFailureIdentityMismatch",
            ["bridge_transport_mismatch"] = "UsbControlFailureBridgeProtocol",
            ["bridge_invalid_output"] = "UsbControlFailureBridgeProtocol",
            ["bridge_exited"] = "UsbControlFailureBridgeExited",
            ["bridge_output_closed"] = "UsbControlFailureBridgeExited",
            ["remote_control_service_unavailable"] = "UsbControlFailureTouchService",
            ["wired_control_service_unavailable"] = "UsbControlFailureTouchService",
            ["apple_device_connection_timeout"] = "UsbControlFailureDeviceConnectionTimeout",
            ["wireless_device_not_discoverable"] = "UsbControlFailureWirelessNotDiscoverable",
            ["wireless_discovery_unavailable"] = "UsbControlFailureWirelessDiscoveryUnavailable",
            ["wireless_address_resolution_failed"] = "UsbControlFailureWirelessAddressResolution",
            ["wireless_remote_pairing_failed"] = "UsbControlFailureWirelessPairingFailed",
            ["wireless_remote_pairing_required"] = "UsbControlFailureWirelessPairingRequired",
            ["control_service_start_timeout"] = "UsbControlFailureControlServiceTimeout",
            ["direct_hid_recovery_exhausted"] = "UsbControlFailureRecoveryExhausted",
            ["userspace_tunnel_unavailable"] = "UsbControlFailureBridgeProtocol",
            ["bad_frame"] = "UsbControlFailureBridgeProtocol",
            ["bridge_input_overflow"] = "UsbControlFailureBridgeProtocol",
        };
        foreach (var (code, expected) in cases)
        {
            var host = new UsbTouchBridgeHost();
            var inner = (DirectUsbInputBridge)innerField.GetValue(host)!;
            handleLine.Invoke(inner, [JsonSerializer.Serialize(new { @event = "error", code, message = "specific runtime failure" })]);
            handleLine.Invoke(inner, ["{\"event\":\"status\",\"code\":\"terminated\"}"]);
            handleLine.Invoke(inner, ["{\"event\":\"warning\",\"code\":\"cleanup\",\"message\":\"cleanup completed\"}"]);
            if (host.LastDiagnostic is not { } diagnostic || !diagnostic.Contains(code) ||
                !diagnostic.Contains("specific runtime failure"))
                throw new InvalidOperationException($"DDI failure {code} was overwritten by cleanup diagnostics.");
            // The host's fallback raises TimeoutException. A specific bridge
            // error must still win, especially developer_image_download_timeout.
            foreach (var error in new Exception[] { new InvalidOperationException("specific runtime failure"),
                                                    new TimeoutException("specific runtime failure") })
            {
                var actual = (string)failureMessage.Invoke(null, [error, host,
                    code.StartsWith("wireless_", StringComparison.Ordinal)])!;
                if (actual != expected) throw new InvalidOperationException($"DDI error {code}: expected {expected}, got {actual}");
            }
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Console.WriteLine($"DDI startup error routing and retained diagnostics passed ({cases.Count * 2} cases).");
        if (packagedBridge is not null)
        {
            if (!RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(packagedBridge, out var failure))
                throw new InvalidOperationException(failure);
            Console.WriteLine("Published DDI bridge passed application runtime integrity verification.");
        }
        return 0;
    }
}
