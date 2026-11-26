# Local Automation API

This reference describes the API shipped with the local v1.8.5-pre7 build. Check the OpenAPI document on your running app for the endpoints it exposes. [pre7 release notes](../en/RELEASE_NOTES.md)
[English documentation](../en/README.md) · [简体中文 API 说明](../zh-CN/AUTOMATION.md)

Click the version label on the About page **five times** to open **Developer tools**. In its **Automation API** section, enable the local API, choose a port (default 17890), click **Apply**, and copy the key. Closing Developer tools keeps the API running; exiting iPhoneMirror stops it. The API is disabled by default and listens only on `127.0.0.1`. The controls are no longer in the ordinary mirroring settings panel.

A port/key error leaves mirroring usable and appears in the API status. If saving settings fails, the listener stops and the UI reports the save failure.

Connect, trust and bind the device in iPhoneMirror first. Start mirroring and reverse control in the UI,
or use the explicit preparation endpoints below for an already configured device. Acquiring a control
lease only reserves an available input route; it does not install drivers, prepare DDI, change the
selected device, or start another transport. Use **Reclaim device control** to cancel API input and
return ownership to manual control. Exiting the app stops the API; closing to tray keeps it running.

- Documentation: `http://127.0.0.1:17890/api/docs` (works offline).
- OpenAPI 3.1: `http://127.0.0.1:17890/api/v1/openapi.json`.
- Examples: [Python](python-example.py), [curl / PowerShell](curl-example.md).

This repository includes a [Python SDK and stdio MCP server](../../sdk/python/README.md).
Both call this authenticated HTTP API; they do not connect directly to the phone or start another HTTP listener.

## Authentication and permissions

Send `X-API-Key` on every device request. Keys are protected with Windows DPAPI for the current user; regenerating a key stops the host, revokes control and restarts with the new credential. Never put a key in a URL. Device data and errors are not cached by HTTP clients. CORS is not enabled and foreign Origin/Host values are rejected.

The initial credential has these scopes: `device.read`, `device.control`, `screen.capture`, `keyboard.input`, `clipboard.read`, `clipboard.write`. The `AutomationApi.Permissions` setting permits a restricted credential. Input requires `device.control`; key/text additionally require `keyboard.input`; text also requires `clipboard.write` because the existing text route uses the phone pasteboard. Pure clipboard writes require both `clipboard.write` and `device.control`. Screenshots and clipboard reads require their respective scopes and do not require a control lease.

Ordinary settings are saved in the existing `%LOCALAPPDATA%/iPhoneMirror/settings.json`. The key is in `automation-key.bin` as DPAPI ciphertext, not plaintext in settings. Logs use `[AutomationAPI]` and omit credentials, text and clipboard values.

## Device identity and capabilities

`GET /api/v1/devices` returns `{ "devices": [...] }`. Use the opaque `id` exactly as returned. It encodes the existing source identity; it is not the display name. USB and AirPlay sources of the same bound phone can have separate captures but share exclusive control through the existing binding profile. No new device manager or binding is created.

`GET /devices/{id}` returns details; `GET /devices/{id}/status` exposes `online`, `connection`, `capturing`, `control`, `capabilities`, and `geometry`. Paths in this document that start with `/devices` are relative to `/api/v1`.

USB/wireless direct control requires iOS/iPadOS 17.4 or later, Developer Mode, a matching DDI and successful service validation. It can provide absolute touch, keys and clipboard/text when those services are ready. BLE provides the existing keyboard route through this API; the experimental absolute Bluetooth mouse in the preview UI does not extend the API touch capability. BLE does **not** promise absolute coordinate taps or arbitrary Unicode text/clipboard. A capture without reverse control can still provide screenshots. Missing metadata stays unknown. Aggregate video-app playback is not represented as a controllable phone.

Clipboard RPC requires a bridge advertising `iphoneMirror.clipboard.rpc.v1`. Older bridges remain compatible with their existing commands but report this API capability as unavailable. Ship the app and rebuilt bridge together.

## Coordinates and gesture timing

`geometry.coordinateSpace` is **capturePixels**. Coordinates refer to the current native capture frame returned by the screenshot endpoint, with top-left origin, x rightwards and y downwards. Bounds are `0 <= x < width`, `0 <= y < height`. These are neither WPF preview coordinates nor iOS logical points nor a guessed device panel resolution. Preview window rotation, borders and DPI do not change this API space.

The rotation test build can remove verified black padding from a square mirroring canvas after the same session establishes a matching rectangular screen reference. The screenshot pixels and reported geometry change together; encoded canvas dimensions remain diagnostic data. Refresh geometry and reacquire control after such a change. This conservative pixel fallback is not an authoritative iOS aperture or direction source; see the [rotation investigation and test boundaries in Chinese](../diagnostics/original-quality-rotation-geometry-20261118.zh-CN.md).

The application converts to normalized touch coordinates using its existing orientation/calibration mapper. Clients must use the returned capture frame and geometry rather than guess a device panel or crop. Some capture paths use internal system-direction observations, but the public geometry contract does not expose a separate iOS rotation angle. Correct unsupported orientation/calibration configurations in the existing UI, rather than applying guessed client-side transforms.

Read geometry before input. Optionally include `geometryVersion` in the request. Resolution, session or calibration changes revoke the old geometry; refresh status and reacquire. A `202` response means dispatch through the existing transport, not confirmation that the iPhone UI performed the action. Never automatically retry input after timeout/disconnection.

Durations are **seconds**, `0.05..10` for long press, swipe and path. Tap uses the existing 40ms gesture. Paths have 2..256 points, evenly timed over the total duration and interpolated by the shared gesture executor. Nonfinite/out-of-bounds coordinates are rejected, not clamped.

## Device preparation and windows

`GET /app/status` requires `device.read` and returns `managementVersion`, `background`,
`mainWindowVisible`, `apiRunning`, and `independentWindowOpen`.

Before acquiring a lease, `POST /devices/{id}/prepare` with `{ "enableControl": false }`
starts or reuses background capture. Set `enableControl` to `true` to also request the existing
USB/wireless control startup; omitting the property defaults to `true`. This can prepare DDI and
requires the same trust, binding, Developer Mode and device confirmations as the UI. It cannot
automatically satisfy those requirements. The result reports `capturing`, `controlAvailable`,
`mainWindowVisible`, and `actionRequired`; HTTP 200 alone does not establish control readiness.
Allow enough client time for first-time DDI preparation, and refresh status after cancellation.

`POST /devices/{id}/window/open` has no body and opens or activates that device's independent
preview, creating its session if needed. It returns `opened` and `mainWindowVisible`.
Both operations require `device.control` and `screen.capture`, reject an active lease for the
same physical device, and serialize application management with `MANAGEMENT_BUSY` on conflict.
They do not switch the main window's selected device. After preparation, read fresh status/geometry
and then acquire control. Implementation: [HTTP routes](../../src/App/Automation/AutomationApiHost.cs)
and [application management](../../src/App/MainWindow.AutomationManagement.cs).

## Control sessions

1. `POST /devices/{id}/control/acquire` with `{ "durationSeconds": 30 }`.
2. Store `sessionToken` and `expiresAt`. Send the token as `X-Control-Session` on input, clipboard writes and release.
3. Renew by calling acquire with the same header. TTL is 5..300 seconds; HTTP keepalive does not renew it.
4. `POST /devices/{id}/control/release` in a `finally` block.

Manual held input/in-flight writes prevent acquisition. During an API lease, conflicting UI, mapping and shortcut device input is rejected through shared admission. Other devices have independent leases. Concurrent API input to a busy device gets 409 instead of an unbounded queue. Release/cancellation uses a separate path so it can cancel a long gesture.

`touch-down` returns a `touchId`. Release with `touch-up { "touchId": "..." }`. A held touch blocks other gestures and expires after 10 seconds or earlier lease expiry. Cancellation/revocation cleans the captured session; it never releases contacts on a replacement transport. Failed cleanup leaves ownership closed until cleanup/recovery, rather than granting conflicting control.

## Endpoints

| Method | Path | Body / result |
|---|---|---|
| GET | `/app/status` | Application management status |
| GET | `/devices` | `{devices:[...]}` |
| GET | `/devices/{id}` | Device details |
| GET | `/devices/{id}/status` | Status, capabilities, geometry, lease summary |
| POST | `/devices/{id}/prepare` | `{enableControl:false}` → capture/control readiness and action required |
| POST | `/devices/{id}/window/open` | No body → independent window status |
| POST | `/devices/{id}/control/acquire` | `{durationSeconds:30}` → session token |
| POST | `/devices/{id}/control/release` | No body → 204 |
| POST | `/devices/{id}/input/tap` | `{x:500,y:800}` |
| POST | `/devices/{id}/input/long-press` | `{x:500,y:800,duration:1.5}` |
| POST | `/devices/{id}/input/touch-down` | `{x:500,y:800}` → touchId |
| POST | `/devices/{id}/input/touch-up` | `{touchId:"..."}` |
| POST | `/devices/{id}/input/swipe` | `{x1:500,y1:800,x2:500,y2:200,duration:0.5}` |
| POST | `/devices/{id}/input/touch-path` | `{points:[{x:500,y:800},{x:510,y:500}],duration:0.8}` |
| POST | `/devices/{id}/input/key` | `{key:"ENTER"}` |
| POST | `/devices/{id}/input/text` | `{text:"Hello"}`; uses existing paste operation |
| GET | `/devices/{id}/clipboard` | `{text:"..."}` directly from phone |
| PUT | `/devices/{id}/clipboard` | `{text:"..."}`; pure write, no Command+V → 204 |
| GET | `/devices/{id}/screenshot` | `image/png` from existing native capture |

Table bodies use abbreviated notation; transmitted JSON needs quoted keys. Keys include A–Z, 0–9, ENTER, ESCAPE, TAB, SPACE, BACKSPACE, DELETE, INSERT, arrows, HOME, END, PAGEUP, PAGEDOWN, SHIFT, CTRL, ALT and the existing F3–F12 mapping. This API is independent of Windows keyboard layout; Unicode text uses the existing pasteboard path.

On a real iPhone, `input/text` may open an iOS **Allow Paste** prompt (the source may be named `dtpasteboardd`). Handle that prompt on the phone and verify the destination field before continuing. A `202` response can arrive while the prompt is still open. Do not send follow-up keys or restore/change the phone clipboard until the paste finishes; changing the clipboard while permission is pending can cancel the insertion. Clipboard tests should keep the original text in memory and restore it only if the clipboard still contains the test value, so they do not overwrite a later user copy or background synchronization.

Screenshots require an already active capture and a decoded frame. Concurrent callers for the same source share pending work, including target resolution, GPU readback and PNG encoding. Two encoder slots process a queue of at most 32 sources; a source waits at most two seconds for a slot before returning 429 with `Retry-After`. An individual request can be cancelled without cancelling its peers. When all callers leave, queued work is cancelled; work already executing retains its encoder slot until it finishes. Screenshot operations have a ten-second deadline and return 503 if readback/encoding cannot finish in time. Capture restarts invalidate the old result. No completed-frame cache, WPF window or desktop screenshot is used. Device clipboard reads do not synthesize Command+C or overwrite the Windows clipboard. Empty text is a valid clipboard value.

If an existing capture temporarily has no decoded frame, the same coalesced task waits up to 500 ms for a new frame and refreshes its dimensions. Persistent missing frames or a stopped capture return 503. This does not start or restart capture, and never serves a cached PNG.

The API uses fast lossless PNG compression at the original capture resolution. This reduces encoding work and avoids intermediate full-size bitmap copies; PNG files can be larger than screenshots saved through the UI. The HTTP content type and decoded pixels are unchanged.

## Limits and errors

Defaults: 60 authenticated requests/s (burst 120), 30 screenshot requests/s, two simultaneous encoders, 64 KiB UTF-8 text, 128 KiB JSON, and 64 HTTP connections. The screenshot limit can be set from 1 to 30 per second in **Developer tools → Automation API**, next to the port; click **Apply** to save it and restart the listener. Existing saved limits are retained until changed there. This is a request budget, not a guarantee of 30 distinct PNG frames per second; frame availability and encoding time still apply. Request rates are per credential; applications sharing a key share its budget. Request rate, burst and screenshot rate are configurable under `AutomationApi`; the encoder, payload and connection bounds are implementation limits. The input queue is fail-fast. Release has a separate limited allowance. Path expansion remains bounded by points/duration and the existing transport pacing.

Errors use `{ "error": { "code": "...", "message": "..." }, "requestId": "..." }`:

| HTTP | Codes |
|---|---|
| 400 | INVALID_REQUEST |
| 401 / 403 | UNAUTHORIZED / FORBIDDEN |
| 404 | DEVICE_NOT_FOUND |
| 409 | CONTROL_LOCKED, CONTROL_NOT_AVAILABLE, GEOMETRY_CHANGED, MANAGEMENT_BUSY, DEVICE_NOT_READY |
| 422 | CAPABILITY_NOT_SUPPORTED, INVALID_COORDINATE, INVALID_DURATION |
| 429 | RATE_LIMITED; Retry-After header |
| 500 | INTERNAL_ERROR / INPUT_FAILED |
| 501 | UNSUPPORTED_RUNTIME (host without application management) |
| 503 | DEVICE_NOT_CONNECTED, CONTROL_NOT_AVAILABLE, CLIPBOARD_UNAVAILABLE, SCREENSHOT_UNAVAILABLE |

Future event/frame WebSockets will reuse device identity, ownership, authentication and frame services. No WebSocket stream or script execution endpoint is exposed in v1.

The v1.8.5-pre7 API includes `/devices/{id}/apps`, `/devices/{id}/apps/launch`, and the shared device-management actions. App launch and other mutations require the existing exclusive control lease. Experimental CoreDevice mirroring uses the desktop app’s local bridge and is not a public API capture-start endpoint. See [Device Hub](../en/USER_GUIDE.md#device-management) and the [Python SDK / stdio MCP](../../sdk/python/README.md).

## Validation and publishing

The expanded Device Hub publishes GET /api/v1/hub/actions and
POST /api/v1/devices/{id}/hub/{action}. Reads require device.read; mutations require
device.control and the current lease; recording also requires screen.capture.

To restart an installed app with the Metal performance HUD, send
`POST /api/v1/devices/{id}/hub/restart_with_metal_hud` with `{"bundleId":"com.example.game"}`
and the current `X-Control-Session` header. The bridge closes the selected app and
starts it with `MTL_HUD_ENABLED=1`. A successful result confirms the launch request.
Check the device or mirrored picture to confirm the HUD is visible.

Upload IPA/profile bytes to /api/v1/devices/{id}/apps/install or /profiles/install
using application/octet-stream, Content-Length and X-File-Name.
Host paths are never accepted by public actions. Renew the lease during long installs;
the SDK handles streaming and renewal. See [device-management capabilities and limits](../en/USER_GUIDE.md#device-management).

Run `App.Runtime.Tests --automation-api`, Python `automation_clipboard_test.py`, the existing keyboard/router/mapping/clipboard/touch suites and localization verification. Real USB/wireless/BLE and multi-device coordinate effects still need device testing; simulation is not proof of an iPhone action.

Kestrel is supplied by `Microsoft.AspNetCore.App`. Both self-contained publish variants must ship its runtime, with no IIS/Hosting Bundle installation. The installer needs no API firewall rule. Local docs/OpenAPI are in the app assembly. Rebuild the repository bridge recipe so the packaged executable advertises the new clipboard capability; keep its runtime manifest/payload together. Packaging checks include ASP.NET assemblies for the multi-file variant.
