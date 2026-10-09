using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Automation;

internal static class AutomationOpenApi
{
    internal static readonly string[] InputActions = ["tap", "long-press", "touch-down", "touch-up", "swipe", "touch-path", "key", "text"];
    internal static JsonObject Document()
    {
        var schemas = new JsonObject();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        schemas["Device"] = options.GetJsonSchemaAsNode(typeof(AutomationDevice));
        schemas["Input"] = options.GetJsonSchemaAsNode(typeof(AutomationInput));
        schemas["Error"] = JsonNode.Parse("""{"type":"object","required":["error","requestId"],"properties":{"error":{"type":"object","required":["code","message"],"properties":{"code":{"type":"string"},"message":{"type":"string"}}},"requestId":{"type":"string"}}}""");
        schemas["Acquire"] = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"durationSeconds":{"type":"integer","minimum":5,"maximum":300,"default":30}}}""");
        schemas["Lease"] = JsonNode.Parse("""{"type":"object","required":["sessionToken","expiresAt"],"properties":{"sessionToken":{"type":"string"},"expiresAt":{"type":"string","format":"date-time"}}}""");
        schemas["Text"] = JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":["text"],"properties":{"text":{"type":"string","description":"Maximum 64 KiB UTF-8."}}}""");
        schemas["Dispatched"] = JsonNode.Parse("""{"type":"object","required":["status"],"properties":{"status":{"const":"dispatched"},"touchId":{"type":"string"}}}""");
        schemas["Devices"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("devices"),
            ["properties"] = new JsonObject { ["devices"] = new JsonObject { ["type"] = "array", ["items"] = Ref("Device") } } };
        schemas["Status"] = JsonNode.Parse("""{"type":"object","properties":{"online":{"type":"boolean"},"connection":{"type":"string"},"capturing":{"type":"boolean"},"control":{"type":"object","properties":{"available":{"type":"boolean"},"transport":{"type":["string","null"]},"acquired":{"type":"boolean"},"expiresAt":{"type":["string","null"],"format":"date-time"}}}}}""");
        schemas["Status"]!["properties"]!["geometry"] = options.GetJsonSchemaAsNode(typeof(AutomationGeometry));
        schemas["Status"]!["properties"]!["capabilities"] = options.GetJsonSchemaAsNode(typeof(AutomationCapabilities));
        var paths = new JsonObject();
        void Add(string path, string method, string description, string permission, string? request, string? response, int status = 200, bool lease = false)
        {
            var parameters = new JsonArray();
            if (path.Contains("{id}")) parameters.Add(new JsonObject { ["name"] = "id", ["in"] = "path", ["required"] = true,
                ["description"] = "Opaque, URL-safe ID from GET devices. Never substitute a display name or change the selected UI device.",
                ["schema"] = new JsonObject { ["type"] = "string" } });
            if (lease || path.EndsWith("acquire")) parameters.Add(new JsonObject { ["name"] = "X-Control-Session", ["in"] = "header", ["required"] = lease,
                ["description"] = "Token from acquire; supplying it to acquire renews the lease.", ["schema"] = new JsonObject { ["type"] = "string" } });
            var responses = new JsonObject
            {
                [status.ToString()] = new JsonObject { ["description"] = status == 202 ? "Dispatched through the existing transport; device UI effects are not acknowledged." : "Success" }
            };
            if (response is not null) responses[status.ToString()]!["content"] = new JsonObject
            { [response == "png" ? "image/png" : "application/json"] = new JsonObject { ["schema"] = response == "png" ? new JsonObject { ["type"] = "string", ["format"] = "binary" } : Ref(response) } };
            foreach (var code in new[] { 400, 401, 403, 404, 409, 422, 429, 500, 503 })
                responses[code.ToString()] = new JsonObject { ["description"] = Errors, ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref("Error") } } };
            var operation = new JsonObject { ["summary"] = description, ["description"] = "Required permissions: " + permission,
                ["parameters"] = parameters, ["responses"] = responses,
                ["security"] = new JsonArray(new JsonObject { ["ApiKey"] = new JsonArray() }) };
            if (request is not null) operation["requestBody"] = new JsonObject { ["required"] = true,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(request) } } };
            if (paths[path] is null) paths[path] = new JsonObject();
            paths[path]![method] = operation;
        }
        Add("/api/v1/devices", "get", "List observed device sources", "device.read", null, "Devices");
        Add("/api/v1/devices/{id}", "get", "Get a device", "device.read", null, "Device");
        Add("/api/v1/devices/{id}/status", "get", "Inspect capabilities, geometry and control lease", "device.read", null, "Status");
        Add("/api/v1/devices/{id}/control/acquire", "post", "Acquire or renew exclusive control", "device.control", "Acquire", "Lease");
        Add("/api/v1/devices/{id}/control/release", "post", "Cancel input and release control", "device.control", null, null, 204, true);
        foreach (var action in InputActions)
        {
            var input = schemas["Input"]!.DeepClone();
            string[] required = action switch
            {
                "tap" or "touch-down" => ["x", "y"], "long-press" => ["x", "y", "duration"],
                "touch-up" => ["touchId"], "swipe" => ["x1", "y1", "x2", "y2", "duration"],
                "touch-path" => ["points", "duration"], "key" => ["key"], _ => ["text"]
            };
            input["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            var properties = (JsonObject)input["properties"]!;
            foreach (var coordinate in new[] { "x", "y", "x1", "y1", "x2", "y2" })
                properties[coordinate] = new JsonObject { ["type"] = "number", ["minimum"] = 0,
                    ["description"] = "Capture pixels; must be less than the corresponding current geometry dimension." };
            properties["duration"] = new JsonObject { ["type"] = "number", ["minimum"] = .05, ["maximum"] = 10 };
            properties["points"] = JsonNode.Parse("""{"type":"array","minItems":2,"maxItems":256,"items":{"type":"object","required":["x","y"],"additionalProperties":false,"properties":{"x":{"type":"number","minimum":0},"y":{"type":"number","minimum":0}}}}""");
            foreach (var name in new[] { "text", "key", "touchId", "geometryVersion" })
                properties[name] = new JsonObject { ["type"] = "string" };
            properties["text"]!["description"] = "Maximum 64 KiB when encoded as UTF-8.";
            input["additionalProperties"] = false;
            input["description"] = "Coordinates are capture pixels, duration is seconds (0.05..10). " +
                (action == "text" ? "Uses the existing paste operation and changes the phone clipboard." : "touch-path contains 2..256 points, evenly timed over duration.");
            schemas[action] = input;
            Add("/api/v1/devices/{id}/input/" + action, "post", action,
                action == "text" ? "device.control, keyboard.input, clipboard.write" : action == "key" ? "device.control, keyboard.input" : "device.control",
                action, "Dispatched", 202, true);
        }
        Add("/api/v1/devices/{id}/clipboard", "get", "Read the device clipboard without copying into Windows", "clipboard.read", null, "Text");
        Add("/api/v1/devices/{id}/clipboard", "put", "Write the device clipboard without pasting", "device.control, clipboard.write", "Text", null, 204, true);
        Add("/api/v1/devices/{id}/screenshot", "get", "PNG from the existing capture session", "screen.capture", null, "png");
        return new JsonObject
        {
            ["openapi"] = "3.1.0", ["info"] = new JsonObject { ["title"] = "iPhoneMirror Automation API", ["version"] = "1.0.0", ["description"] = Overview },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }), ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas, ["securitySchemes"] = new JsonObject
                { ["ApiKey"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-API-Key" } } }
        };
    }
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private const string Overview = "Local IPv4 loopback only. Every device endpoint requires X-API-Key. Input requires an exclusive X-Control-Session lease. " +
        "Use returned opaque device IDs and inspect capabilities. BLE supports keyboard, not absolute touch or clipboard. " +
        "Coordinates use the current native screenshot frame: origin top-left, 0 <= x < width, 0 <= y < height; not iOS logical points or preview pixels. " +
        "Resolution/orientation/calibration changes invalidate geometry. Acquire leases for 5..300 seconds (default 30), renew explicitly, release in finally. " +
        "A held touch expires after 10 seconds. Input responses mean dispatched, not a device UI acknowledgement. Do not automatically retry input. " +
        "Tap example: {\"x\":500,\"y\":800}. First verify those coordinates fit status.geometry. Read the local docs page for examples.";
    private const string Errors = "INVALID_REQUEST / UNAUTHORIZED / FORBIDDEN / DEVICE_NOT_FOUND / DEVICE_NOT_CONNECTED / CONTROL_NOT_AVAILABLE / CONTROL_LOCKED / CAPABILITY_NOT_SUPPORTED / INVALID_COORDINATE / INVALID_DURATION / GEOMETRY_CHANGED / INPUT_FAILED / CLIPBOARD_UNAVAILABLE / SCREENSHOT_UNAVAILABLE / RATE_LIMITED / INTERNAL_ERROR. 429 includes Retry-After.";
    internal static string Docs => """
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>iPhoneMirror Automation API</title><style>body{font:16px system-ui;max-width:900px;margin:40px auto;padding:0 24px;line-height:1.65;color:#172033}code,pre{background:#eef2f7;padding:3px 6px;border-radius:5px}pre{padding:18px;overflow:auto}li{margin:10px 0}</style>
        <h1>iPhoneMirror Automation API v1</h1><p>Use your preferred HTTP client from Python, C#, Node.js or PowerShell.</p>
        <p><a href="/api/v1/openapi.json">Download the OpenAPI 3.1 contract</a>. This page works offline and contains no credentials.</p>
        <h2>Connect</h2><p>Click the main window’s bottom-right version label five times to open Developer tools, then enable Automation API and click Apply. Copy the address and API Key. Send <code>X-API-Key</code> on all device requests. Never put credentials in a URL. Device control must already be enabled in iPhoneMirror.</p>
        <ol><li><code>GET /api/v1/devices</code>: use an opaque returned ID.</li><li><code>GET /api/v1/devices/{id}/status</code>: inspect capabilities and geometry.</li><li><code>POST /api/v1/devices/{id}/control/acquire</code> with <code>{"durationSeconds":30}</code>.</li><li>Send the returned token in <code>X-Control-Session</code> for input and clipboard writes.</li><li>Always <code>POST /api/v1/devices/{id}/control/release</code> in a finally block. Renew using acquire with the same session header.</li></ol>
        <h2>Input</h2><p>POST under <code>/api/v1/devices/{id}/input/</code>:</p>
        <pre>tap         {"x":500,"y":800}
        long-press  {"x":500,"y":800,"duration":1.5}
        swipe       {"x1":500,"y1":800,"x2":500,"y2":200,"duration":0.5}
        touch-path  {"points":[{"x":500,"y":800},{"x":510,"y":500}],"duration":0.8}
        touch-down  {"x":500,"y":800}       → touchId
        touch-up    {"touchId":"returned-id"}
        key         {"key":"ENTER"}
        text        {"text":"Hello"}</pre>
        <p>Coordinates are pixels of the device's native screenshot, with top-left origin and exclusive width/height bounds. They are not WPF coordinates or iOS logical points. Check dimensions before using the examples. Duration is seconds, 0.05–10. Paths contain 2–256 evenly timed points. Only one API gesture or held contact per lease. Held contacts expire after 10 seconds. Refresh status and reacquire on geometry changes.</p>
        <p>Key names: A–Z, 0–9, ENTER, ESCAPE, TAB, SPACE, BACKSPACE, DELETE, INSERT, LEFT, UP, RIGHT, DOWN, HOME, END, PAGEUP, PAGEDOWN, SHIFT, CTRL, ALT, F3–F12. Text input uses the existing paste operation and changes the phone clipboard.</p>
        <h2>Clipboard and screenshots</h2><p><code>GET /api/v1/devices/{id}/clipboard</code> returns <code>{"text":"..."}</code>. <code>PUT</code> with that body writes without pasting. Reads use the phone, not Windows. These operations require a bridge with clipboard RPC support.</p><p><code>GET /api/v1/devices/{id}/screenshot</code> returns image/png from existing capture. Start mirroring and wait for a frame first. BLE keyboard availability does not imply absolute touch or clipboard support.</p>
        <h2>Permissions, errors and limits</h2><p>Scopes: device.read, device.control, screen.capture, keyboard.input, clipboard.read, clipboard.write. Text needs keyboard.input, clipboard.write and device.control. Error responses contain <code>error.code</code>, <code>error.message</code> and requestId. See OpenAPI for all status codes.</p>
        <p>Default limits: 60 requests/s (burst 120), 5 screenshots/s, two PNG encoders, 64 KiB UTF-8 text and 128 KiB JSON. Busy devices return 409; rate limits return 429 with Retry-After. A 202 response means input was dispatched, not that the phone UI acknowledged its effect. Do not automatically retry input after a timeout. UI can reclaim control in Developer tools.</p>
        </html>
        """;
}
