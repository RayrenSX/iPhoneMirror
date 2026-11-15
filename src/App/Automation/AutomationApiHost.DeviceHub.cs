using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Automation;

internal sealed partial class AutomationApiHost
{
    private void MapDeviceHub(WebApplication app, string principal, Action<string[]> permission)
    {
        app.MapGet("/api/v1/hub/actions", () =>
        {
            permission(["device.read"]);
            return Results.Json(new { actions = DeviceHubContract.Actions.Where(p => p.Value.Public)
                .ToDictionary(p => p.Key, p => new { p.Value.ReadOnly, p.Value.Timeout }) });
        });
        app.MapPost("/api/v1/devices/{id}/hub/{action}", async (string id, string action, HttpContext c) =>
        {
            var policy = DeviceHubContract.Require(action, publicOnly: true);
            permission([policy.ReadOnly ? "device.read" : "device.control"]);
            if (action.StartsWith("record_", StringComparison.Ordinal)) permission(["screen.capture"]);
            var args = await ReadAsync<JsonElement>(c);
            return Results.Json(await service.DeviceHubRequestAsync(id, principal, Session(c), action, args, c.RequestAborted));
        });
        foreach (var kind in new[] { "apps", "profiles" })
            app.MapPost($"/api/v1/devices/{{id}}/{kind}/install", async (string id, HttpContext c) =>
            {
                permission(["device.control"]);
                var action = kind == "apps" ? "install_app" : "install_profile";
                var policy = DeviceHubContract.Require(action);
                // Admission, generation and ownership cover the entire upload and install.
                return Results.Json(await service.DeviceHubOperationAsync(id, principal, Session(c), policy,
                    async (target, current, ct) =>
                    {
                        if (!string.Equals(c.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                            throw new AutomationException("INVALID_REQUEST", "Upload application/octet-stream with X-File-Name.", 400);
                        var fileName = c.Request.Headers["X-File-Name"];
                        var suffix = fileName.Count == 1 ? Path.GetExtension(fileName[0] ?? "").ToLowerInvariant() : "";
                        if (kind == "apps" ? suffix != ".ipa" : suffix is not (".mobileprovision" or ".mobileconfig"))
                            throw new AutomationException("INVALID_REQUEST", "Use .ipa, .mobileprovision or .mobileconfig.", 422);
                        long maximum = kind == "apps" ? 512L * 1024 * 1024 : 8L * 1024 * 1024;
                        if (c.Request.ContentLength is not > 0 || c.Request.ContentLength > maximum)
                            throw new AutomationException("INVALID_REQUEST", "Supply a bounded, positive Content-Length.", 413);
                        var feature = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
                        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximum;
                        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror", "HubUploads");
                        Directory.CreateDirectory(root);
                        // The request never chooses a server path or replaces an existing file.
                        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + suffix);
                        try
                        {
                            using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            upload.CancelAfter(TimeSpan.FromSeconds(90));
                            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            {
                                var buffer = new byte[65536];
                                long count = 0;
                                int size;
                                while ((size = await c.Request.Body.ReadAsync(buffer, upload.Token)) > 0)
                                {
                                    count += size;
                                    if (count > maximum || count > c.Request.ContentLength)
                                        throw new AutomationException("INVALID_REQUEST", "Upload exceeds the declared size.", 413);
                                    await output.WriteAsync(buffer.AsMemory(0, size), upload.Token);
                                }
                                if (count != c.Request.ContentLength)
                                    throw new AutomationException("INVALID_REQUEST", "Incomplete upload.", 400);
                            }
                            ct.ThrowIfCancellationRequested();
                            if (!current()) throw new OperationCanceledException();
                            return await target.DeviceHub!(action, new { path }, current, ct);
                        }
                        finally { if (File.Exists(path)) File.Delete(path); }
                    }, c.RequestAborted));
            });
    }
}
