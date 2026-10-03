using System.Diagnostics;
using System.IO;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Explicit integration mode: needs the real downloaded component installed
    // and exclusive access to the receiver ports. It does not simulate iOS.
    private static int RunUxPlayLifecycleTests(string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_LOG_FILE",
            Path.GetFullPath(Path.Combine(output, "capture.log")));
        Environment.SetEnvironmentVariable("IPHONE_MIRROR_APP_LOG_DIRECTORY",
            Path.GetFullPath(output));
        var descriptor = UxPlayComponent.Descriptor!;
        ComponentRequire(UxPlayComponent.FindInstalledExecutable(descriptor,
            UxPlayComponent.CacheRoot) is not null, "Verified installed component is required");
        using var core = new NativeCore();
        var controller = new WirelessReceiverController(core)
        { Backend = WirelessReceiverBackend.UxPlay, ReceiverName = "iPhoneMirror-Cache-Test" };
        try
        {
            for (var round = 1; round <= 2; round++)
            {
                var started = controller.EnsureStartedAsync().GetAwaiter().GetResult();
                ComponentRequire(started.Started, "Receiver failed to start: " + started.Error);
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(25))
                {
                    Thread.Sleep(500);
                    var status = core.GetWirelessReceiverStatus();
                    ComponentRequire(status.Running, "Native receiver exited during startup");
                    ComponentRequire(controller.IsAvailable, "Running receiver invalidated its component cache");
                }
                var stable = core.GetWirelessReceiverStatus();
                ComponentRequire(stable.Ready, "Receiver did not become ready");
                var registry = Environment.GetEnvironmentVariable("GST_REGISTRY_1_0");
                ComponentRequire(registry is not null && File.Exists(registry), "GStreamer index was not created");
                ComponentRequire(!Path.GetFullPath(registry!).StartsWith(
                    Path.GetFullPath(UxPlayComponent.CacheRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "Registry is inside the component cache");
                ComponentRequire(UxPlayComponent.FindInstalledExecutable(descriptor,
                    UxPlayComponent.CacheRoot) is not null, "Running cache failed full hash verification");
                controller.StopAsync().GetAwaiter().GetResult();
                ComponentRequire(!core.GetWirelessReceiverStatus().Running, "Receiver did not stop");
                ComponentRequire(UxPlayComponent.FindInstalledExecutable(descriptor,
                    UxPlayComponent.CacheRoot) is not null, "Stopped cache failed full hash verification");
                Console.WriteLine($"PASS round {round}: ready for 25s, external registry, complete {descriptor.Files.Length}-file hashes before/after stop");
            }
        }
        finally { controller.StopAsync().GetAwaiter().GetResult(); }
        return 0;
    }
}
