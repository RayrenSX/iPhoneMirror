using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

internal static class PairedDeviceDiscoveryTests
{
    internal static void Run()
    {
        const string udid = "00008150-0006650A0144C01C";
        var network = DeviceViewModel.FromPairedNetwork(new(udid, "My phone", "iPhone17,1", "18.0"), null);
        Require(network.IsPairedNetwork && !network.IsWireless && !network.Ready &&
            !network.HasPendingWiredReadiness && network.CardTransport == "Wi-Fi",
            "LAN presence must not masquerade as a USB-ready or active AirPlay source");
        Require(!DeviceActionService.CanExecute(network), "LAN discovery must not enable USB power actions");
        var usb = DeviceViewModel.FromNative(new NativeDeviceInfo {
            Udid = udid, Name = "My phone", ProductType = "iPhone17,1", OsVersion = "18.0",
            ConnectionType = "USB", State = ConnectionState.Ready
        }, DeviceAppearance.Unknown);
        var changes = new HashSet<string>();
        network.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        network.UpdateFrom(usb);
        Require(!network.IsPairedNetwork && network.Ready && network.CardTransport == "USB" &&
            changes.Contains(nameof(DeviceViewModel.IsPairedNetwork)) &&
            changes.Contains(nameof(DeviceViewModel.CardStatusDisplay)),
            "plugging in must update the same card and its dependent bindings");
        network.UpdateFrom(DeviceViewModel.FromPairedNetwork(new(udid, "My phone", "iPhone17,1", "18.0"), null));
        Require(network.IsPairedNetwork && !network.Ready, "unplugging must revoke cached USB readiness");
        Require(PairedDeviceDiscovery.Key(udid) == PairedDeviceDiscovery.Key(udid.Replace("-", "").ToLowerInvariant()),
            "LAN and USB UDID formats must deduplicate");
        VerifyProbeHandoff(udid);
        Console.WriteLine("Paired LAN device model checks passed.");
    }

    private static void VerifyProbeHandoff(string udid)
    {
        // Cancelled refreshes exercise the real cache policy without spawning
        // a network worker. Advance a monotonic clock instead of sleeping.
        var noProbe = new CancellationToken(canceled: true);
        foreach (var transition in new[] { "CoreDevice stop", "USB unplug" })
        {
            var clock = new DiscoveryClock();
            var discovery = new PairedDeviceDiscovery(clock);
            var phone = new PairedNetworkDevice(udid, "My phone", "iPhone18,3", "27.2.1");
            discovery.ObserveVerifiedDevices([phone]);
            Require(discovery.SnapshotAndRefresh([], noProbe).Single() == phone, "Verified phone is missing before " + transition);
            Require(discovery.SnapshotAndRefresh([udid.Replace("-", "").ToLowerInvariant()], noProbe).Count == 0,
                "An excluded device must not create an extra Wi-Fi card during " + transition);
            clock.Advance(TimeSpan.FromMinutes(20));
            Require(discovery.SnapshotAndRefresh([udid], noProbe).Count == 0, "A live owner must keep owning the visible card");
            Require(discovery.SnapshotAndRefresh([], noProbe).Single() == phone,
                transition + " must return the existing phone immediately instead of waiting for a LAN rescan");
            clock.Advance(TimeSpan.FromSeconds(29));
            Require(discovery.SnapshotAndRefresh([], noProbe).Single() == phone, transition + " lost its bounded verification grace");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(discovery.SnapshotAndRefresh([], noProbe).Count == 0,
                "Unreachable devices must expire; repeated inventory reads must not extend grace forever");
        }
        var offlineClock = new DiscoveryClock();
        var offline = new PairedDeviceDiscovery(offlineClock);
        offline.ObserveVerifiedDevices([new(udid, "Phone", null, null)]);
        offlineClock.Advance(TimeSpan.FromSeconds(31));
        Require(offline.SnapshotAndRefresh([], noProbe).Count == 0, "Ordinary offline expiration must still work");
    }

    private sealed class DiscoveryClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
