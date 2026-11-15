using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services.Automation;

internal sealed partial class AutomationService
{
    internal async Task<JsonElement> DeviceHubAsync(string id, string principal, string? token,
        string action, string? bundleId, CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        if (target.DeviceHub is null)
            throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Prepare CoreDevice control using the dev bridge first.", 422);
        if (action is "apps" or "media_status")
            return await target.DeviceHub(action, null, () => true, cancellation);
        if (action != "launch" || bundleId is null || bundleId.Length > 512 ||
            !Regex.IsMatch(bundleId, @"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+\z", RegexOptions.CultureInvariant))
            throw new AutomationException("INVALID_REQUEST", "Pass an installed application's bundleId.", 422);
        var lease = ownership.Require(target.PhysicalId, principal, token);
        using var owner = ownership.Use(lease);
        if (!await lease.Operation.WaitAsync(0, cancellation))
            throw new AutomationException("CONTROL_LOCKED", "Another device operation is in progress.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Cancellation.Token, _stop.Token);
        bool Current() => ownership.IsCurrent(lease) && target.IsCurrent();
        try
        {
            if (!Current()) throw new OperationCanceledException();
            if (lease.Touch is not null) throw new AutomationException("CONTROL_LOCKED", "Release the active touch before launching an app.");
            return await target.DeviceHub("launch", new { bundleId }, Current, linked.Token);
        }
        finally { lease.Operation.Release(); }
    }
}
