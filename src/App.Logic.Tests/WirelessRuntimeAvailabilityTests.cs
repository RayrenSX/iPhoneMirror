using System.IO;
using IPhoneMirror.App.Services;

internal static class WirelessRuntimeAvailabilityTests
{
    internal static void Run()
    {
        const string variable = "IPHONE_MIRROR_AIRPLAY_HOST";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = WirelessReceiverConfiguration.GetRuntime(WirelessReceiverBackend.Original);
            foreach (var name in runtime.RequiredRuntimeFiles.Append(runtime.ExecutableName))
                File.WriteAllText(Path.Combine(root, name), "fixture");
            Environment.SetEnvironmentVariable(variable, Path.Combine(root, runtime.ExecutableName));
            var checks = 0;
            var valid = true;
            var service = new WirelessReceiverService(_ => { ++checks; return valid; });
            for (var i = 0; i < 30; ++i)
                Assert(service.IsAvailable, "An unchanged runtime must stay available.");
            Assert(checks == 1, "Repeated status queries must not reread all runtime bytes.");

            var changed = Path.Combine(root, "airplay2dll.dll");
            valid = false;
            File.AppendAllText(changed, "changed");
            Assert(!service.IsAvailable && checks == 2,
                "A changed payload must invalidate a successful status snapshot.");
            Assert(!service.IsAvailable && checks == 2,
                "An unchanged rejected payload may reuse only its short status result.");
            valid = true;
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow + TimeSpan.FromSeconds(2));
            Assert(service.IsAvailable && checks == 3,
                "Repairing file metadata must invalidate a rejected snapshot immediately.");
            File.Delete(changed);
            Assert(!service.IsAvailable && checks == 3,
                "A missing required runtime file must be unavailable immediately.");
            File.WriteAllText(changed, "restored");
            Assert(service.IsAvailable && checks == 4,
                "Restoring a file must trigger a new status check.");

            var another = Path.Combine(root, "replacement");
            Directory.CreateDirectory(another);
            foreach (var name in runtime.RequiredRuntimeFiles.Append(runtime.ExecutableName))
                File.WriteAllText(Path.Combine(another, name), "fixture");
            Environment.SetEnvironmentVariable(variable, Path.Combine(another, runtime.ExecutableName));
            Assert(service.IsAvailable && checks == 5,
                "A receiver path change must not reuse the previous directory result.");

            // Deliberately positive status injection cannot attest to launch:
            // actual preflight must hash and reject these untrusted fixtures.
            var beforeProbe = checks;
            Assert(service.ProbeRuntime().Status == WirelessRuntimeProbeStatus.CodeIntegrityBlocked &&
                checks == beforeProbe,
                "Launch preflight must bypass status caching and use full integrity verification.");
            Console.WriteLine("PASS runtime availability: repeated status uses metadata snapshots; changed, missing and repaired files invalidate; launch always verifies integrity.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
