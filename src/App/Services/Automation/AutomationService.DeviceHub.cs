using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services.Automation;

internal sealed partial class AutomationService
{
    internal async Task<JsonElement> DeviceHubAsync(string id, string principal, string? token,
        string action, string? bundleId, CancellationToken cancellation)
        => await DeviceHubRequestAsync(id, principal, token, action,
            action == "launch" ? JsonSerializer.SerializeToElement(new { bundleId }) : JsonSerializer.SerializeToElement(new { }), cancellation);

    internal Task<JsonElement> DeviceHubRequestAsync(string id, string principal, string? token,
        string action, JsonElement arguments, CancellationToken cancellation)
    {
        var policy = DeviceHubContract.Require(action, publicOnly: true);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new AutomationException("INVALID_REQUEST", "Pass a JSON argument object.", 422);
        if (action == "restart_with_metal_hud" && arguments.EnumerateObject().Count() != 1)
            throw new AutomationException("INVALID_REQUEST", "A Metal HUD restart accepts only bundleId.", 422);
        if (action is "launch" or "restart_with_metal_hud" or "uninstall_app" or "get_app_icon")
        {
            var bundle = arguments.TryGetProperty("bundleId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (bundle is null || bundle.Length > 512 ||
                !Regex.IsMatch(bundle, @"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+\z", RegexOptions.CultureInvariant))
                throw new AutomationException("INVALID_REQUEST", "Pass an installed application's bundleId.", 422);
        }
        return DeviceHubOperationAsync(id, principal, token, policy,
            (target, current, ct) => target.DeviceHub!(action, arguments, current, ct), cancellation);
    }

    internal async Task<JsonElement> DeviceHubOperationAsync(string id, string principal, string? token,
        DeviceHubPolicy policy, Func<AutomationTarget, Func<bool>, CancellationToken, Task<JsonElement>> execute,
        CancellationToken cancellation)
    {
        var target = await backend.ResolveAsync(id, cancellation);
        Online(target);
        if (target.DeviceHub is null)
            throw new AutomationException("CAPABILITY_NOT_SUPPORTED", "Prepare CoreDevice control using the dev bridge first.", 422);
        if (policy.ReadOnly)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
            return await execute(target, target.IsCurrent, read.Token);
        }
        var lease = ownership.Require(target.PhysicalId, principal, token);
        using var owner = ownership.Use(lease);
        if (!await lease.Operation.WaitAsync(0, cancellation))
            throw new AutomationException("CONTROL_LOCKED", "Another device operation is in progress.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Cancellation.Token, _stop.Token);
        bool Current() => ownership.IsCurrent(lease) && target.IsCurrent();
        try
        {
            if (!Current()) throw new OperationCanceledException();
            if (lease.Touch is not null) throw new AutomationException("CONTROL_LOCKED", "Release the active touch before a device operation.");
            return await execute(target, Current, linked.Token);
        }
        finally { lease.Operation.Release(); }
    }
}
