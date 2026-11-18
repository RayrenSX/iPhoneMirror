using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Services.ExplorerMapping;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    // Stop revokes this identity immediately; cleanup joins the startup before
    // disposing resources that an outstanding ConnectAsync may still return.
    private sealed class ExplorerMappingSession(DeviceViewModel device, UsbTouchBridgeHost? bridge,
        CancellationTokenSource lifetime)
    {
        internal DeviceViewModel Device { get; } = device;
        internal UsbTouchBridgeHost? Bridge { get; } = bridge;
        internal long Generation { get; } = bridge?.InputGeneration ?? 0;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal CancellationToken Token { get; } = lifetime.Token;
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? BundleId;
        internal ExplorerWebDavServer? Server;
        internal WindowsExplorerDrive? Drive;
        internal Task? Cleanup;
    }

    private ExplorerMappingSession? _explorerMapping;
    private DispatcherTimer? _explorerWatch;
    private bool _explorerMappingBusy;
    private string? _explorerStatusKey;
    private string? _explorerFailure;
    private bool _explorerMappingError;
    private Task _explorerCleanup = Task.CompletedTask;
    private long _explorerLimitBytes = 50_000_000;

    public bool ExplorerMappingBusy => _explorerMappingBusy;
    public string? ExplorerMappingDeviceUdid => _explorerMapping?.Device.Udid;
    public string? ExplorerMappingBundleId => _explorerMapping?.BundleId;
    public string ExplorerMappingDriveName => _explorerMapping?.Drive?.DriveName ?? "";
    public long ExplorerMappingLimitBytes => _explorerLimitBytes;
    public bool ExplorerMappingHasError => _explorerMappingError;
    public string ExplorerMappingStatus => _explorerStatusKey switch
    {
        "DeviceHubExplorerMappedFormat" => LocalizationService.Format(_explorerStatusKey, ExplorerMappingDriveName),
        "DeviceHubExplorerFailedFormat" => LocalizationService.Format(_explorerStatusKey, _explorerFailure ?? ""),
        null => "",
        _ => LocalizationService.Get(_explorerStatusKey),
    };

    private bool ExplorerRouteCurrent(ExplorerMappingSession session) => !_disposed &&
        !session.Token.IsCancellationRequested && ReferenceEquals(_explorerMapping, session) &&
        ReferenceEquals(SelectedDevice, session.Device) && session.Bridge is { IsReady: true, SupportsDeviceHub: true } bridge &&
        ReferenceEquals(GetReadyDeviceHubBridge(session.Device), bridge) && bridge.InputGeneration == session.Generation;

    private void NotifyExplorerMapping()
    {
        foreach (var property in new[] { nameof(ExplorerMappingBusy), nameof(ExplorerMappingDeviceUdid),
            nameof(ExplorerMappingBundleId), nameof(ExplorerMappingDriveName), nameof(ExplorerMappingStatus), nameof(ExplorerMappingHasError),
            nameof(ExplorerMappingLimitBytes) }) OnPropertyChanged(property);
    }

    private void CheckExplorerMappingConnection()
    {
        if (_explorerMapping is { } session && !ExplorerRouteCurrent(session))
            _ = StopExplorerMappingAsync(disconnected: true);
    }

    internal async Task<bool> SetExplorerMappingAsync(DeviceViewModel device, bool enabled)
        => await SetExplorerDocumentMappingAsync(device, enabled, null);

    internal async Task<bool> SetExplorerDocumentMappingAsync(DeviceViewModel device, bool enabled, string? bundleId)
    {
        if (!enabled)
        {
            if (_explorerMapping is null || DeviceViewModel.UdidEquals(_explorerMapping.Device.Udid, device.Udid))
                await StopExplorerMappingAsync();
            return true;
        }
        if (_disposed || _explorerMappingBusy || !ReferenceEquals(SelectedDevice, device) || device.IsMediaCast)
            return false;
        if (_explorerMapping?.Drive is not null && ReferenceEquals(_explorerMapping.Device, device) && _explorerMapping.BundleId == bundleId) return true;

        // Publish the attempt before the first await, including old cleanup.
        _explorerMappingBusy = true;
        var previous = StopExplorerMappingAsync(keepBusy: true);
        var session = new ExplorerMappingSession(device, GetReadyDeviceHubBridge(device),
            CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token));
        session.BundleId = bundleId;
        _explorerMapping = session;
        _explorerMappingBusy = true;
        _explorerMappingError = false;
        _explorerFailure = null;
        _explorerStatusKey = "DeviceHubExplorerStarting";
        NotifyExplorerMapping();
        var mapped = false;
        try
        {
            await previous;
            session.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_explorerMapping, session) || !ReferenceEquals(SelectedDevice, device))
                throw new OperationCanceledException();
            var availability = WindowsExplorerDrive.GetAvailability();
            _explorerLimitBytes = availability.DownloadLimitBytes;
            if (!availability.CanTryConnect)
            {
                _explorerStatusKey = "DeviceHubExplorerUnavailable";
                _explorerMappingError = true;
                return false;
            }
            if (session.Bridge is not { SupportsDeviceHub: true }) throw new IOException("DEVICE_HUB_UNAVAILABLE");
            if (!ExplorerRouteCurrent(session)) throw new OperationCanceledException();
            var store = new DeviceHubExplorerFileStore(async (action, arguments, cancellation) =>
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Token);
                var nested = await Application.Current.Dispatcher.InvokeAsync(() =>
                    ExplorerRequestAsync(session, action, arguments, request.Token),
                    DispatcherPriority.Normal, request.Token);
                return await nested.ConfigureAwait(false);
            });
            _ = await store.StatAsync("", session.Token);
            if (!ExplorerRouteCurrent(session)) throw new OperationCanceledException();
            session.Server = ExplorerWebDavServer.Start(store,
                Math.Clamp(availability.DownloadLimitBytes, 1, uint.MaxValue), requestObserved: (method, status) =>
                {
                    if (status >= 400 || method is "PUT" or "MKCOL" or "MOVE" or "DELETE" or "PROPPATCH")
                        AddDiagnosticLog(AppLog.Event("explorer_mapping_http", ("method", method), ("status", status)));
                });
            _explorerWatch ??= new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(300) };
            _explorerWatch.Tick -= OnExplorerMappingWatch;
            _explorerWatch.Tick += OnExplorerMappingWatch;
            _explorerWatch.Start();
            session.Drive = await WindowsExplorerDrive.ConnectAsync(session.Server.BaseUri, session.Token);
            if (!ExplorerRouteCurrent(session)) throw new OperationCanceledException();
            mapped = true;
            _explorerStatusKey = "DeviceHubExplorerMappedFormat";
            AddDiagnosticLog(AppLog.Event("explorer_mapping_started", ("device", AppLog.Device(device.Udid)),
                ("drive", session.Drive.DriveName)));
            return true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_explorerMapping, session)) _explorerStatusKey = "DeviceHubExplorerDisconnected";
            return false;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_explorerMapping, session))
            {
                _explorerMappingError = true;
                _explorerStatusKey = "DeviceHubExplorerFailedFormat";
                // Never expose the capability URL or a phone filename.
                _explorerFailure = error is Win32Exception native ? native.NativeErrorCode.ToString() :
                    error is AutomationException hub ? hub.Code : error.GetType().Name;
                AddDiagnosticLog(AppLog.Event("explorer_mapping_failed", ("error", _explorerFailure)));
            }
            return false;
        }
        finally
        {
            var ownsState = ReferenceEquals(_explorerMapping, session);
            if (ownsState)
            {
                if (!mapped)
                {
                    _explorerMapping = null;
                    _explorerWatch?.Stop();
                    session.Lifetime.Cancel();
                }
                _explorerMappingBusy = false;
            }
            session.Finished.TrySetResult();
            var cleanup = !mapped ? QueueExplorerCleanup(session) : Task.CompletedTask;
            if (ownsState) NotifyExplorerMapping();
            await cleanup;
        }
    }

    private void OnExplorerMappingWatch(object? sender, EventArgs e) => CheckExplorerMappingConnection();

    private async Task<JsonElement> ExplorerRequestAsync(ExplorerMappingSession session,
        string action, object? arguments, CancellationToken cancellation)
    {
        if (!ExplorerRouteCurrent(session)) throw new OperationCanceledException();
        bool Current() => ExplorerRouteCurrent(session);
        var bridge = session.Bridge!;
        if (session.BundleId is not null)
        {
            var scoped = JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            scoped["root"] = JsonSerializer.SerializeToElement("documents");
            scoped["bundleId"] = JsonSerializer.SerializeToElement(session.BundleId);
            arguments = scoped;
        }
        try
        {
            if (DeviceHubContract.Require(action).ReadOnly)
                return await bridge.DeviceHubAsync(action, arguments, Current, cancellation);
            var physical = AutomationPhysicalId(session.Device.Udid);
            var guard = AutomationHumanGuard(session.Device.Udid);
            using var owner = AutomationOwnership.EnterInput(physical);
            return await bridge.DeviceHubAsync(action, arguments, () => Current() && guard(), cancellation);
        }
        catch (AutomationException error)
        {
            AddDiagnosticLog(AppLog.Event("explorer_mapping_device_failed", ("action", action), ("code", error.Code)));
            throw;
        }
    }

    private Task StopExplorerMappingAsync(bool disconnected = false, bool keepStatus = false, bool keepBusy = false)
    {
        _explorerWatch?.Stop();
        var session = _explorerMapping;
        _explorerMapping = null;
        session?.Lifetime.Cancel();
        if (!keepStatus) _explorerStatusKey = disconnected ? "DeviceHubExplorerDisconnected" : null;
        if (!keepBusy) _explorerMappingBusy = false;
        var cleanup = session is not null ? QueueExplorerCleanup(session) : _explorerCleanup;
        NotifyExplorerMapping();
        return cleanup;
    }

    private Task QueueExplorerCleanup(ExplorerMappingSession session)
    {
        if (session.Cleanup is not null) return session.Cleanup;
        var previous = _explorerCleanup;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Cleanup = completion.Task;
        _explorerCleanup = completion.Task;
        _ = CleanupAsync();
        return completion.Task;

        async Task CleanupAsync()
        {
            await session.Finished.Task;
            // No resource failure may skip the next resource or old cleanup.
            try { if (session.Server is not null) await session.Server.DisposeAsync(); }
            catch (Exception error) { LogCleanupFailure(error); }
            try { if (session.Drive is not null) await session.Drive.DisposeAsync(); }
            catch (Exception error) { LogCleanupFailure(error); }
            try { await previous; }
            catch (Exception error) { LogCleanupFailure(error); }
            finally
            {
                session.Lifetime.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private void LogCleanupFailure(Exception error) =>
        AddDiagnosticLog(AppLog.Event("explorer_mapping_cleanup_failed", ("error", error.GetType().Name)));
}
