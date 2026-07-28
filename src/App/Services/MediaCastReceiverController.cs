using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed class MediaCastReceiverController(
    NativeCore core, WirelessReceiverService? receiver = null)
{
    private readonly WirelessReceiverService _receiver = receiver ?? new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _startError;
    private DateTime _automaticStartNotBeforeUtc;

    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal bool IsAvailable => _receiver.IsAvailable;

    internal async Task<bool> EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsAvailable) return Running = Ready = false;
            var status = core.GetMediaCastReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            if (Running) return true;
            if (DateTime.UtcNow < _automaticStartNotBeforeUtc) return false;
            if (_receiver.ExecutablePath is not { } hostPath) return false;
            var runtime = await Task.Run(_receiver.ProbeRuntime);
            if (!runtime.Success)
            {
                Running = Ready = false;
                _startError = WirelessReceiverService.DescribeProbeFailure(runtime);
                _automaticStartNotBeforeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                return false;
            }
            var result = await Task.Run(() => core.StartMediaCastReceiver(
                WirelessReceiverConfiguration.DefaultReceiverName, hostPath));
            Running = result.Success;
            Ready = false;
            _startError = result.Success ? null : result.Message;
            return result.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!IsAvailable) return LocalizationService.Get("MediaCastReceiverMissing");
        if (!Running && !string.IsNullOrWhiteSpace(_startError))
            return LocalizationService.Format("StartFailedFormat", _startError);
        return LocalizationService.Get(Ready ? "MediaCastReady" : "MediaCastStarting");
    }
}
