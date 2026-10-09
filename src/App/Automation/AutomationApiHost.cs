using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Automation;

internal sealed class AutomationApiHost(AutomationService service) : IAsyncDisposable
{
    private WebApplication? _app;
    private int _accepting;
    private int _disposed;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal string State { get; private set; } = "Stopped";
    internal int Port { get; private set; }
    internal event Action? StateChanged;
    private readonly TokenBucket _anonymous = new(20, 40);
    private readonly TokenBucket _cleanupRequests = new(10, 20);
    internal async Task StartAsync(AutomationSettings settings, string key, CancellationToken cancellation = default)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        cancellation = startup.Token;
        await _lifecycle.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_app is not null) return;
            settings.Validate();
            SetState("Starting");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            // Only the application's validated settings may configure this
            // listener; environment/appsettings endpoints must not add LAN binds.
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxRequestBodySize = 131072;
                server.Limits.MaxConcurrentConnections = 64;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                server.Listen(IPAddress.Loopback, settings.Port);
            });
            var app = builder.Build();
            var expectedKey = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var principal = Convert.ToHexString(expectedKey);
            var requests = new TokenBucket(settings.RequestsPerSecond, settings.RequestBurst);
            var screenshots = new TokenBucket(settings.ScreenshotsPerSecond, settings.ScreenshotsPerSecond);
            var scopes = new HashSet<string>(settings.Permissions ?? [], StringComparer.Ordinal);
            app.Use(async (context, next) =>
            {
                var watch = Stopwatch.StartNew();
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    var host = context.Request.Host;
                    if (host.Host is not ("127.0.0.1" or "localhost") || host.Port != settings.Port)
                        throw new AutomationException("INVALID_REQUEST", "Invalid Host header.", 400);
                    if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                        (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) ||
                        uri.Scheme != "http" || uri.Host != host.Host || uri.Port != host.Port))
                        throw new AutomationException("FORBIDDEN", "Cross-origin requests are not allowed.", 403);
                    var path = context.Request.Path.Value ?? "";
                    var publicDocs = context.Request.Method == "GET" && path is "/api/docs" or "/api/v1/openapi.json";
                    if (!publicDocs)
                    {
                        if (Volatile.Read(ref _accepting) == 0)
                            throw new AutomationException("CONTROL_NOT_AVAILABLE", "The API server is stopping.", 503);
                        var supplied = context.Request.Headers["X-API-Key"];
                        if (supplied.Count != 1 || supplied[0]?.Length != 64 ||
                            !CryptographicOperations.FixedTimeEquals(expectedKey, SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]!))))
                        {
                            if (!_anonymous.Take()) throw new AutomationException("RATE_LIMITED", "Too many authentication failures.", 429);
                            throw new AutomationException("UNAUTHORIZED", "A valid X-API-Key header is required.", 401);
                        }
                        var release = path.EndsWith("/control/release", StringComparison.Ordinal) ||
                            path.EndsWith("/input/touch-up", StringComparison.Ordinal);
                        // Cleanup has a separate small budget; ordinary traffic
                        // cannot prevent a client from releasing a held contact.
                        if (!(release ? _cleanupRequests.Take() : requests.Take()))
                            throw new AutomationException("RATE_LIMITED", "Request budget exceeded.", 429);
                        if (path.EndsWith("/screenshot", StringComparison.Ordinal) && !screenshots.Take())
                            throw new AutomationException("RATE_LIMITED", "Screenshot budget exceeded.", 429);
                    }
                    await next(context);
                    if (context.Response.StatusCode is 404 or 405 && !context.Response.HasStarted)
                        await ErrorAsync(context, new("INVALID_REQUEST", "Unknown endpoint or HTTP method.", context.Response.StatusCode));
                }
                catch (AutomationException error) { await ErrorAsync(context, error); }
                catch (Exception error) when (error is JsonException or BadHttpRequestException)
                { await ErrorAsync(context, new("INVALID_REQUEST", "Invalid JSON or request body.", 400)); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (OperationCanceledException)
                { await ErrorAsync(context, new("CONTROL_NOT_AVAILABLE", "Input was cancelled or its session changed.", 409)); }
                catch (TimeoutException)
                { await ErrorAsync(context, new("CONTROL_NOT_AVAILABLE", "The device operation timed out; do not automatically retry input.", 503)); }
                catch (Exception error)
                {
                    DiagnosticLogger.Warning("AutomationAPI", "request_failed", ("type", error.GetType().Name));
                    await ErrorAsync(context, new("INTERNAL_ERROR", "The operation failed.", 500));
                }
                finally
                {
                    DiagnosticLogger.Info("AutomationAPI", "request", ("requestId", context.TraceIdentifier),
                        ("status", context.Response.StatusCode), ("elapsedMs", watch.ElapsedMilliseconds));
                }
            });
            void Permission(params string[] required)
            {
                if (required.Any(scope => !scopes.Contains(scope)))
                    throw new AutomationException("FORBIDDEN", "The credential does not have the required permission.", 403);
            }
            app.MapGet("/api/docs", () => Results.Content(AutomationOpenApi.Docs, "text/html; charset=utf-8"));
            app.MapGet("/api/v1/openapi.json", () => Results.Json(AutomationOpenApi.Document()));
            app.MapGet("/api/v1/devices", async (HttpContext c) =>
            { Permission("device.read"); return Results.Json(new { devices = await service.DevicesAsync(c.RequestAborted) }); });
            app.MapGet("/api/v1/devices/{id}", async (string id, HttpContext c) =>
            { Permission("device.read"); return Results.Json((await service.TargetAsync(id, c.RequestAborted)).Device); });
            app.MapGet("/api/v1/devices/{id}/status", async (string id, HttpContext c) =>
            {
                Permission("device.read");
                var target = await service.TargetAsync(id, c.RequestAborted);
                var lease = service.Ownership.Snapshot().FirstOrDefault(l => l.PhysicalId == target.PhysicalId);
                return Results.Json(new { target.Device.Online, target.Device.Connection, target.Device.Capturing,
                    control = new { available = target.Device.ControlAvailable, transport = target.Device.ControlTransport,
                        acquired = lease is not null, expiresAt = lease?.ExpiresAt },
                    target.Device.Capabilities, target.Device.Geometry });
            });
            app.MapPost("/api/v1/devices/{id}/control/acquire", async (string id, HttpContext c) =>
            {
                Permission("device.control");
                var body = await ReadAsync<AcquireRequest>(c);
                return Results.Json(await service.AcquireAsync(id, principal, Session(c), body.DurationSeconds, c.RequestAborted));
            });
            app.MapPost("/api/v1/devices/{id}/control/release", async (string id, HttpContext c) =>
            { Permission("device.control"); await service.ReleaseAsync(id, principal, Session(c), c.RequestAborted); return Results.NoContent(); });
            foreach (var action in AutomationOpenApi.InputActions)
                app.MapPost("/api/v1/devices/{id}/input/" + action, async (string id, HttpContext c) =>
                {
                    Permission("device.control");
                    if (action is "key" or "text") Permission("keyboard.input");
                    if (action == "text") Permission("clipboard.write");
                    var body = await ReadAsync<AutomationInput>(c);
                    return Results.Json(await service.InputAsync(id, principal, Session(c), action, body, c.RequestAborted), statusCode: 202);
                });
            app.MapGet("/api/v1/devices/{id}/clipboard", async (string id, HttpContext c) =>
            { Permission("clipboard.read"); return Results.Json(new { text = await service.ClipboardAsync(id, principal, null, false, null, c.RequestAborted) }); });
            app.MapPut("/api/v1/devices/{id}/clipboard", async (string id, HttpContext c) =>
            {
                Permission("device.control", "clipboard.write");
                var body = await ReadAsync<TextRequest>(c);
                await service.ClipboardAsync(id, principal, Session(c), true, body.Text, c.RequestAborted);
                return Results.NoContent();
            });
            app.MapGet("/api/v1/devices/{id}/screenshot", async (string id, HttpContext c) =>
            { Permission("screen.capture"); return Results.Bytes(await service.ScreenshotAsync(id, c.RequestAborted), "image/png"); });
            try { await app.StartAsync(cancellation); }
            catch { await app.DisposeAsync(); throw; }
            _app = app;
            Volatile.Write(ref _accepting, 1);
            Port = settings.Port;
            service.Start();
            SetState("Running");
            DiagnosticLogger.Info("AutomationAPI", "server_started", ("address", "127.0.0.1"), ("port", Port));
        }
        catch { SetState("Failed"); throw; }
        finally { _lifecycle.Release(); }
    }
    internal async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            Volatile.Write(ref _accepting, 0);
            service.Suspend();
            var app = _app;
            _app = null;
            if (app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await service.RevokeAllAsync(); }
                finally
                {
                    try { await app.StopAsync(timeout.Token); }
                    finally { await app.DisposeAsync(); }
                }
            }
            SetState("Stopped");
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Volatile.Write(ref _accepting, 0);
        _shutdown.Cancel();
        try { await StopAsync(); }
        finally { await service.DisposeAsync(); }
    }
    private void SetState(string state) { State = state; StateChanged?.Invoke(); }
    private static string? Session(HttpContext c) => c.Request.Headers["X-Control-Session"].Count == 1 ? c.Request.Headers["X-Control-Session"].ToString() : null;
    private static async Task<T> ReadAsync<T>(HttpContext c)
    {
        if (!c.Request.HasJsonContentType()) throw new AutomationException("INVALID_REQUEST", "Content-Type must be application/json.", 400);
        return await JsonSerializer.DeserializeAsync<T>(c.Request.Body, Json, c.RequestAborted)
            ?? throw new AutomationException("INVALID_REQUEST", "A JSON object is required.", 400);
    }
    private static async Task ErrorAsync(HttpContext context, AutomationException error)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        context.Response.StatusCode = error.Status;
        if (error.Status == 429) context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { error = new { code = error.Code, message = error.Message }, requestId = context.TraceIdentifier }, context.RequestAborted);
    }
    private sealed record AcquireRequest(int DurationSeconds = 30);
    private sealed record TextRequest(string? Text);
    private sealed class TokenBucket(int rate, int burst)
    {
        private readonly object _gate = new();
        private double _tokens = burst;
        private long _at = Stopwatch.GetTimestamp();
        internal bool Take()
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(burst, _tokens + Stopwatch.GetElapsedTime(_at, now).TotalSeconds * rate);
                _at = now;
                if (_tokens < 1) return false;
                _tokens--;
                return true;
            }
        }
    }
}
