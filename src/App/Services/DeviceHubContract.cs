using System.Text.Json;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services;

internal sealed record DeviceHubPolicy(bool ReadOnly, bool Public, int Timeout);

internal static class DeviceHubContract
{
    internal static readonly IReadOnlyDictionary<string, DeviceHubPolicy> Actions = Load();
    private static IReadOnlyDictionary<string, DeviceHubPolicy> Load()
    {
        using var stream = typeof(DeviceHubContract).Assembly.GetManifestResourceStream("IPhoneMirror.App.DeviceHub.contract.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("actions").EnumerateObject().ToDictionary(p => p.Name, p =>
            new DeviceHubPolicy(p.Value.GetProperty("readOnly").GetBoolean(), p.Value.GetProperty("public").GetBoolean(),
                p.Value.GetProperty("timeout").GetInt32()), StringComparer.Ordinal);
    }
    internal static DeviceHubPolicy Require(string action, bool publicOnly = false)
    {
        if (!Actions.TryGetValue(action, out var policy) || publicOnly && !policy.Public)
            throw new AutomationException("INVALID_REQUEST", "Unknown or private Device Hub operation.", 422);
        return policy;
    }
}
