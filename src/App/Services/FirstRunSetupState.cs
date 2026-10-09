using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneMirror.App.Services;

internal enum SetupUsage { WirelessOnly, WiredOnly, MirrorAndControl }
internal enum SetupDisposition { New, InProgress, Deferred, Completed }
internal enum SetupCategory { Basics, Devices, Experience }
internal enum SetupStep { Welcome, Preferences, Usage, ControlIntroduction, Connection, Devices,
    Environment, ReDetection, Profile, WiredControl, Wireless, WirelessControl, Bluetooth,
    DeviceComplete, Display, Validation, Completed,
    // Append values to preserve existing numeric checkpoints.
    ApplicationMode, Appearance, WirelessIntroduction, BluetoothIntroduction,
    WirelessBackend, WirelessDisplay, WiredDisplay, WiredFrameRate, WiredDecoder, MirrorConnection, ControlReadiness }
internal enum SetupOutcome { Pending, Verified, Skipped }
internal sealed class SetupDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<SetupStep, SetupOutcome> Outcomes { get; set; } = [];
}

// Checkpoints describe saved identities and preferences, not functional control tests.
internal sealed class FirstRunSetupState
{
    public int Version { get; set; } = 1;
    public SetupDisposition Disposition { get; set; }
    public SetupUsage Usage { get; set; } = SetupUsage.WirelessOnly;
    public SetupStep Step { get; set; }
    public int DeviceIndex { get; set; }
    public bool PreferencesApplied { get; set; }
    // Null means a legacy checkpoint has not made an explicit feature choice.
    public bool? WirelessEnabled { get; set; }
    public bool? BluetoothEnabled { get; set; }
    public bool? UsbControlEnabled { get; set; }
    public bool PreserveKnownDevices { get; set; }
    public bool CustomizeDisplay { get; set; }
    public bool WirelessRestarted { get; set; }
    public WirelessReceiverBackend? WirelessBackendDraft { get; set; }
    public string? WirelessProfileDraft { get; set; }
    public int DisplayDeviceIndex { get; set; }
    public List<SetupDevice> Devices { get; set; } = [];
    public Dictionary<SetupStep, SetupOutcome> GlobalOutcomes { get; set; } = [];
    [JsonIgnore] public SetupDevice? CurrentDevice => DeviceIndex >= 0 && DeviceIndex < Devices.Count ? Devices[DeviceIndex] : null;
    [JsonIgnore] public bool ShouldOpen => Disposition is SetupDisposition.New or SetupDisposition.InProgress;
    [JsonIgnore] public SetupCategory Category => Step switch
    {
        SetupStep.Welcome or SetupStep.Preferences or SetupStep.ApplicationMode or SetupStep.Appearance or SetupStep.Usage
            => SetupCategory.Basics,
        SetupStep.Display or SetupStep.WirelessDisplay or SetupStep.WiredDisplay or SetupStep.WiredFrameRate or
            SetupStep.WiredDecoder or SetupStep.Validation or SetupStep.Completed => SetupCategory.Experience,
        _ => SetupCategory.Devices
    };
    [JsonIgnore] public bool IsChoiceStep => Step is SetupStep.Welcome or SetupStep.Preferences or
        SetupStep.ApplicationMode or SetupStep.Appearance or SetupStep.Usage or SetupStep.ControlIntroduction or
        SetupStep.WirelessIntroduction or SetupStep.BluetoothIntroduction or SetupStep.MirrorConnection or SetupStep.Display or
        SetupStep.WirelessBackend or SetupStep.WirelessDisplay or SetupStep.WiredDisplay or SetupStep.WiredFrameRate or SetupStep.WiredDecoder;
    [JsonIgnore] public bool IsWiredDisplayStep => Step is SetupStep.WiredDisplay or SetupStep.WiredFrameRate or SetupStep.WiredDecoder;
    [JsonIgnore] public bool UsesWirelessMirroring => Usage == SetupUsage.WirelessOnly &&
        GlobalOutcomes.GetValueOrDefault(SetupStep.Wireless) == SetupOutcome.Verified ||
        Usage == SetupUsage.MirrorAndControl && Devices.Any(d =>
            d.Outcomes.GetValueOrDefault(SetupStep.Wireless) == SetupOutcome.Verified);
    [JsonIgnore] public bool Optional => Usage == SetupUsage.MirrorAndControl && Step is
        SetupStep.WirelessBackend or SetupStep.Wireless or SetupStep.Bluetooth;
    [JsonIgnore] public bool CanSkip => Step != SetupStep.Completed;
    [JsonIgnore] public bool HasSkippedSteps => GlobalOutcomes.Values.Contains(SetupOutcome.Skipped) ||
        Devices.Any(d => d.Outcomes.Values.Contains(SetupOutcome.Skipped));
    [JsonIgnore] public bool HasSavedDevices => Usage == SetupUsage.WirelessOnly
        ? GlobalOutcomes.GetValueOrDefault(SetupStep.Wireless) == SetupOutcome.Verified && Devices.Count > 0
        : Devices.Any(d => d.Outcomes.GetValueOrDefault(SetupStep.Profile) == SetupOutcome.Verified);
    [JsonIgnore] public IReadOnlyList<SetupStep> DeviceSteps => Usage == SetupUsage.MirrorAndControl
        ? [SetupStep.Environment, SetupStep.ReDetection, SetupStep.Profile,
           SetupStep.WirelessBackend, SetupStep.Wireless, SetupStep.Bluetooth, SetupStep.DeviceComplete]
        : [SetupStep.Environment, SetupStep.ReDetection, SetupStep.Profile, SetupStep.DeviceComplete];
    internal void SelectUsage(SetupUsage usage)
    {
        if (Usage == usage) return;
        Usage = usage; Devices.Clear(); DeviceIndex = DisplayDeviceIndex = 0;
        foreach (var step in GlobalOutcomes.Keys.Where(s => s is not (SetupStep.Welcome or SetupStep.Preferences or SetupStep.ApplicationMode or SetupStep.Appearance)).ToArray())
            GlobalOutcomes.Remove(step);
        WirelessRestarted = false;
    }
    internal void ReconcileConnectedDevices(IReadOnlyList<SetupUsbDevice> connected)
    {
        static string Identity(string id) => IPhoneFilterDriverService.NormalizeSerial(id);
        if (!PreserveKnownDevices) Devices.RemoveAll(d => !connected.Any(c => Identity(c.Serial) == Identity(d.Id)));
        foreach (var device in connected)
        {
            var existing = Devices.FirstOrDefault(d => Identity(d.Id) == Identity(device.Serial));
            if (existing is null) Devices.Add(new() { Id = device.Serial, Name = device.Name });
            else existing.Name = device.Name;
        }
        DeviceIndex = 0;
    }
    internal void Record(SetupOutcome outcome)
    {
        if (outcome == SetupOutcome.Skipped && !CanSkip) throw new InvalidOperationException("Setup is already complete.");
        Outcomes[Step] = outcome;
    }
    internal void Skip()
    {
        Record(SetupOutcome.Skipped);
        if (Step is SetupStep.Connection or SetupStep.Devices)
        {
            Devices.Clear(); DeviceIndex = DisplayDeviceIndex = 0; Step = SetupStep.Display; return;
        }
        if (Step is SetupStep.ReDetection or SetupStep.Profile)
        {
            // Identity-dependent pages cannot bind a device without its saved wired identity.
            foreach (var dependent in DeviceSteps.SkipWhile(s => s != Step).Skip(1).TakeWhile(s => s != SetupStep.DeviceComplete))
                Outcomes[dependent] = SetupOutcome.Skipped;
            Step = SetupStep.DeviceComplete; return;
        }
        if (Step == SetupStep.Display) CustomizeDisplay = false;
        Next();
    }
    internal Dictionary<SetupStep, SetupOutcome> Outcomes =>
        IsWiredDisplayStep && Devices.ElementAtOrDefault(DisplayDeviceIndex) is { } displayDevice ? displayDevice.Outcomes :
        Usage != SetupUsage.WirelessOnly && DeviceSteps.Contains(Step) && CurrentDevice is { } device
            ? device.Outcomes : GlobalOutcomes;
    internal bool DeviceComplete(SetupDevice device) =>
        new[] { SetupStep.Environment, SetupStep.ReDetection, SetupStep.Profile }.All(s =>
            device.Outcomes.GetValueOrDefault(s) is SetupOutcome.Verified or SetupOutcome.Skipped) &&
        (Usage != SetupUsage.MirrorAndControl ||
            (WirelessEnabled == false || device.Outcomes.GetValueOrDefault(SetupStep.Wireless) is SetupOutcome.Verified or SetupOutcome.Skipped) &&
            (BluetoothEnabled == false || device.Outcomes.GetValueOrDefault(SetupStep.Bluetooth) is SetupOutcome.Verified or SetupOutcome.Skipped));
    internal void Next()
    {
        if (Step is SetupStep.Environment or SetupStep.ReDetection or SetupStep.Profile or
            SetupStep.Wireless or SetupStep.Bluetooth or SetupStep.Validation)
        {
            var outcome = Outcomes.GetValueOrDefault(Step);
            if (outcome is not (SetupOutcome.Verified or SetupOutcome.Skipped))
                throw new InvalidOperationException("Setup step has not been verified.");
        }
        if (Step == SetupStep.DeviceComplete && Outcomes.GetValueOrDefault(Step) != SetupOutcome.Skipped && (CurrentDevice is null || !DeviceComplete(CurrentDevice)))
            throw new InvalidOperationException("Device setup is incomplete.");
        if (Step is SetupStep.Connection or SetupStep.Devices && Devices.Count == 0) throw new InvalidOperationException("No connected devices.");
        Step = Step switch
        {
            SetupStep.Welcome => SetupStep.Preferences,
            SetupStep.Preferences => SetupStep.ApplicationMode,
            SetupStep.ApplicationMode => SetupStep.Appearance,
            SetupStep.Appearance => SetupStep.Usage,
            SetupStep.Usage => Usage == SetupUsage.MirrorAndControl ? SetupStep.ControlIntroduction : SetupStep.MirrorConnection,
            SetupStep.MirrorConnection => Usage == SetupUsage.WirelessOnly ? SetupStep.WirelessBackend : SetupStep.Connection,
            SetupStep.ControlIntroduction or SetupStep.WirelessIntroduction or SetupStep.BluetoothIntroduction => SetupStep.Connection,
            SetupStep.Connection => SetupStep.Environment,
            SetupStep.Devices => SetupStep.Environment,
            SetupStep.Wireless when Usage == SetupUsage.WirelessOnly => SetupStep.Display,
            SetupStep.DeviceComplete => ++DeviceIndex < Devices.Count ? SetupStep.Environment : SetupStep.Display,
            SetupStep.Display => BeginDisplay(),
            SetupStep.WirelessBackend => Outcomes.GetValueOrDefault(SetupStep.WirelessBackend) == SetupOutcome.Skipped ? SkipWireless() : SetupStep.Wireless,
            SetupStep.WirelessDisplay => Usage == SetupUsage.WirelessOnly ? SetupStep.Validation : BeginWiredDisplay(),
            SetupStep.WiredDisplay => SetupStep.WiredFrameRate,
            SetupStep.WiredFrameRate => SetupStep.WiredDecoder,
            SetupStep.WiredDecoder => NextWiredDisplay(),
            SetupStep.Validation => SetupStep.Completed,
            SetupStep.Completed => SetupStep.Completed,
            _ => DeviceSteps[DeviceSteps.ToList().IndexOf(Step) + 1]
        };
        if (Usage == SetupUsage.MirrorAndControl)
        {
            if (Step == SetupStep.WirelessBackend && WirelessEnabled == false) Step = SetupStep.Bluetooth;
            if (Step == SetupStep.Bluetooth && BluetoothEnabled == false) Step = SetupStep.DeviceComplete;
        }
    }
    internal void Previous()
    {
        Step = Step switch
        {
            SetupStep.Preferences => SetupStep.Welcome,
            SetupStep.ApplicationMode => SetupStep.Preferences,
            SetupStep.Appearance => SetupStep.ApplicationMode,
            SetupStep.Usage => SetupStep.Appearance,
            SetupStep.ControlIntroduction => SetupStep.Usage,
            SetupStep.WirelessIntroduction => SetupStep.ControlIntroduction,
            SetupStep.BluetoothIntroduction => SetupStep.WirelessIntroduction,
            SetupStep.MirrorConnection => SetupStep.Usage,
            SetupStep.Connection => Usage == SetupUsage.MirrorAndControl ? SetupStep.ControlIntroduction : SetupStep.MirrorConnection,
            SetupStep.Devices => SetupStep.Connection,
            SetupStep.Environment => DeviceIndex > 0 ? PreviousDevice() : SetupStep.Connection,
            SetupStep.Wireless => SetupStep.WirelessBackend,
            SetupStep.Bluetooth => WirelessEnabled == false ? SetupStep.Profile : CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.WirelessBackend) == SetupOutcome.Skipped ? SetupStep.WirelessBackend : SetupStep.Wireless,
            SetupStep.DeviceComplete => CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.ReDetection) == SetupOutcome.Skipped ? SetupStep.ReDetection :
                CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.Profile) == SetupOutcome.Skipped ? SetupStep.Profile :
                Usage == SetupUsage.MirrorAndControl && BluetoothEnabled == false ? WirelessEnabled == false ? SetupStep.Profile : SetupStep.Wireless : DeviceSteps[^2],
            SetupStep.Display => Usage == SetupUsage.WirelessOnly ? GlobalOutcomes.GetValueOrDefault(SetupStep.WirelessBackend) == SetupOutcome.Skipped ? SetupStep.WirelessBackend : SetupStep.Wireless : Devices.Count == 0 ? SetupStep.Connection : PreviousDevice(),
            SetupStep.WirelessBackend => Usage == SetupUsage.WirelessOnly ? SetupStep.MirrorConnection : SetupStep.Profile,
            SetupStep.WirelessDisplay => SetupStep.Display,
            SetupStep.WiredDisplay => PreviousDisplayDevice(),
            SetupStep.WiredFrameRate => SetupStep.WiredDisplay,
            SetupStep.WiredDecoder => SetupStep.WiredFrameRate,
            SetupStep.Validation => !CustomizeDisplay ? SetupStep.Display : Usage == SetupUsage.WirelessOnly ? UsesWirelessMirroring ? SetupStep.WirelessDisplay : SetupStep.Display : LastDisplayDevice(),
            SetupStep.Completed => SetupStep.Validation,
            SetupStep.Welcome => SetupStep.Welcome,
            _ => DeviceSteps[DeviceSteps.ToList().IndexOf(Step) - 1]
        };
    }
    private SetupStep PreviousDevice() { DeviceIndex = Math.Max(0, DeviceIndex - 1); return SetupStep.DeviceComplete; }
    private SetupStep BeginDisplay()
    {
        DisplayDeviceIndex = 0;
        return !CustomizeDisplay ? SetupStep.Validation : UsesWirelessMirroring ? SetupStep.WirelessDisplay : Usage == SetupUsage.WirelessOnly ? SetupStep.Validation : BeginWiredDisplay();
    }
    private SetupStep SkipWireless()
    {
        Outcomes[SetupStep.Wireless] = SetupOutcome.Skipped;
        return Usage == SetupUsage.WirelessOnly ? SetupStep.Display : SetupStep.Bluetooth;
    }
    private bool HasWiredProfile(int index) => Devices[index].Outcomes.GetValueOrDefault(SetupStep.Profile) == SetupOutcome.Verified;
    private SetupStep BeginWiredDisplay() { DisplayDeviceIndex = -1; return NextWiredDisplay(); }
    private SetupStep NextWiredDisplay()
    {
        while (++DisplayDeviceIndex < Devices.Count)
            if (HasWiredProfile(DisplayDeviceIndex)) return SetupStep.WiredDisplay;
        return SetupStep.Validation;
    }
    private SetupStep PreviousDisplayDevice()
    {
        while (--DisplayDeviceIndex >= 0)
            if (HasWiredProfile(DisplayDeviceIndex)) return SetupStep.WiredDecoder;
        DisplayDeviceIndex = 0;
        return UsesWirelessMirroring ? SetupStep.WirelessDisplay : SetupStep.Display;
    }
    private SetupStep LastDisplayDevice() { DisplayDeviceIndex = Devices.Count; return PreviousDisplayDevice(); }
}

internal sealed class FirstRunSetupStore(string? path = null)
{
    internal string PathName { get; } = path ?? Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "iPhoneMirror", "first-run-setup.json");
    internal FirstRunSetupState Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            var state = JsonSerializer.Deserialize<FirstRunSetupState>(File.ReadAllText(PathName));
            if (state is null || state.Version != 1 || !Enum.IsDefined(state.Step) || !Enum.IsDefined(state.Usage)
                || !Enum.IsDefined(state.Disposition) || state.Devices is null || state.GlobalOutcomes is null)
                throw new InvalidDataException("Invalid setup checkpoint.");
            state.Devices = state.Devices.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Id))
                .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var device in state.Devices) device.Outcomes ??= [];
            foreach (var outcomes in state.Devices.Select(d => d.Outcomes).Append(state.GlobalOutcomes))
                foreach (var pair in outcomes.Where(p => !Enum.IsDefined(p.Key) || !Enum.IsDefined(p.Value)).ToArray())
                    outcomes.Remove(pair.Key);
            if (state.Step is SetupStep.WirelessIntroduction or SetupStep.BluetoothIntroduction) state.Step = SetupStep.ControlIntroduction;
            if (state.Step == SetupStep.WiredControl) state.Step = SetupStep.Profile;
            if (state.Step == SetupStep.WirelessControl) state.Step = SetupStep.Bluetooth;
            if (state.Step == SetupStep.Devices) state.Step = SetupStep.Connection;
            if (state.Usage == SetupUsage.WirelessOnly && state.Step is not (SetupStep.Welcome or SetupStep.Preferences or SetupStep.ApplicationMode or
                SetupStep.Appearance or SetupStep.Usage or SetupStep.MirrorConnection or SetupStep.Wireless or SetupStep.Display or SetupStep.WirelessBackend or
                SetupStep.WirelessDisplay or SetupStep.Validation or SetupStep.Completed)) state.Step = SetupStep.Usage;
            if (state.Usage == SetupUsage.WiredOnly && state.Step is SetupStep.ControlIntroduction or SetupStep.WirelessIntroduction or
                SetupStep.BluetoothIntroduction or SetupStep.WiredControl or SetupStep.Wireless or SetupStep.WirelessControl or SetupStep.Bluetooth or
                SetupStep.WirelessBackend or SetupStep.WirelessDisplay) state.Step = SetupStep.Usage;
            state.DeviceIndex = Math.Clamp(state.DeviceIndex, 0, state.Devices.Count);
            state.DisplayDeviceIndex = Math.Clamp(state.DisplayDeviceIndex, 0, Math.Max(0, state.Devices.Count - 1));
            if (state.IsWiredDisplayStep && state.Devices.Count == 0) state.Step = SetupStep.Connection;
            if (state.Usage != SetupUsage.WirelessOnly && state.DeviceSteps.Contains(state.Step) && state.CurrentDevice is null)
                state.Step = SetupStep.Connection;
            if (state.Usage == SetupUsage.MirrorAndControl && state.Step is (SetupStep.WirelessBackend or SetupStep.Wireless or SetupStep.Bluetooth) &&
                state.CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.Profile) != SetupOutcome.Verified)
                state.Step = state.CurrentDevice?.Outcomes.GetValueOrDefault(SetupStep.ReDetection) == SetupOutcome.Verified ? SetupStep.Profile : SetupStep.ReDetection;
            if (state.IsWiredDisplayStep && state.Devices[state.DisplayDeviceIndex].Outcomes.GetValueOrDefault(SetupStep.Profile) != SetupOutcome.Verified)
                state.Step = SetupStep.Display;
            // A resumed completion screen must revalidate before it can finish.
            if (state.Step == SetupStep.Completed && state.Disposition != SetupDisposition.Completed &&
                state.GlobalOutcomes.GetValueOrDefault(SetupStep.Validation) != SetupOutcome.Skipped) state.Step = SetupStep.Validation;
            return state;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { DiagnosticLogger.Exception("setup", "checkpoint_read_failed", error); return new(); }
    }
    internal void Save(FirstRunSetupState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, PathName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
