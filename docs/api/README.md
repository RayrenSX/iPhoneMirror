# Local Automation API

Click the main window’s bottom-right version label **five times** to open **Developer tools**. In its **Automation API** section, enable the local API, choose a port (default 17890), click **Apply**, and copy the key. Closing Developer tools keeps the API running; closing iPhoneMirror stops it. The API is disabled by default and listens only on `127.0.0.1`. The controls are no longer in the ordinary mirroring settings panel.

A port/key error leaves mirroring usable and appears in the API status. If saving settings fails, the listener stops and the UI reports the save failure.

The existing device connection, binding, mirroring and reverse control must be established in iPhoneMirror. Acquiring API control does not install drivers, download DDI, change the selected device, or start another transport. Use the **Reclaim device control** button to cancel API input and return ownership to manual control.

- Documentation: `http://127.0.0.1:17890/api/docs` (works offline).
- OpenAPI 3.1: `http://127.0.0.1:17890/api/v1/openapi.json`.
- Examples: [Python](python-example.py), [curl / PowerShell](curl-example.md).

## Authentication and permissions

Send `X-API-Key` on every device request. Keys are protected with Windows DPAPI for the current user; regenerating a key stops the host, revokes control and restarts with the new credential. Never put a key in a URL. Device data and errors are not cached by HTTP clients. CORS is not enabled and foreign Origin/Host values are rejected.

The initial credential has these scopes: `device.read`, `device.control`, `screen.capture`, `keyboard.input`, `clipboard.read`, `clipboard.write`. The `AutomationApi.Permissions` setting permits a restricted credential. Input requires `device.control`; key/text additionally require `keyboard.input`; text also requires `clipboard.write` because the existing text route uses the phone pasteboard. Pure clipboard writes require both `clipboard.write` and `device.control`. Screenshots and clipboard reads require their respective scopes and do not require a control lease.

Ordinary settings are saved in the existing `%LOCALAPPDATA%/iPhoneMirror/settings.json`. The key is in `automation-key.bin` as DPAPI ciphertext, not plaintext in settings. Logs use `[AutomationAPI]` and omit credentials, text and clipboard values.

## Device identity and capabilities

`GET /api/v1/devices` returns `{ "devices": [...] }`. Use the opaque `id` exactly as returned. It encodes the existing source identity; it is not the display name. USB and AirPlay sources of the same bound phone can have separate captures but share exclusive control through the existing binding profile. No new device manager or binding is created.

`GET /devices/{id}` returns details; `GET /devices/{id}/status` exposes `online`, `connection`, `capturing`, `control`, `capabilities`, and `geometry`. Paths in this document that start with `/devices` are relative to `/api/v1`.

USB/wireless direct control can provide absolute touch, keys and clipboard/text when the appropriate existing service is ready. BLE provides the existing keyboard route; it does **not** promise absolute coordinate taps or arbitrary Unicode text/clipboard. A capture without reverse control can still provide screenshots. Missing metadata stays unknown. Aggregate video-app playback is not represented as a controllable phone.

Clipboard RPC requires a bridge advertising `iphoneMirror.clipboard.rpc.v1`. Older bridges remain compatible with their existing commands but report this API capability as unavailable. Ship the app and rebuilt bridge together.

## Coordinates and gesture timing

`geometry.coordinateSpace` is **capturePixels**. Coordinates refer to the current native capture frame returned by the screenshot endpoint, with top-left origin, x rightwards and y downwards. Bounds are `0 <= x < width`, `0 <= y < height`. These are neither WPF preview coordinates nor iOS logical points nor a guessed device panel resolution. Preview window rotation, borders and DPI do not change this API space.

The application converts to normalized touch coordinates using its existing orientation/calibration mapper. The frame is not cropped to guess a device screen. Use only supported full-screen mirroring and the established control calibration. The current capture stack does not provide an independent authoritative iOS rotation angle; unsupported orientation/calibration configurations require correction in the existing UI, not guessed transforms.

Read geometry before input. Optionally include `geometryVersion` in the request. Resolution, session or calibration changes revoke the old geometry; refresh status and reacquire. A `202` response means dispatch through the existing transport, not confirmation that the iPhone UI performed the action. Never automatically retry input after timeout/disconnection.

Durations are **seconds**, `0.05..10` for long press, swipe and path. Tap uses the existing 40ms gesture. Paths have 2..256 points, evenly timed over the total duration and interpolated by the shared gesture executor. Nonfinite/out-of-bounds coordinates are rejected, not clamped.

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
| GET | `/devices` | `{devices:[...]}` |
| GET | `/devices/{id}` | Device details |
| GET | `/devices/{id}/status` | Status, capabilities, geometry, lease summary |
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

Screenshots require an already active capture and a decoded frame. Concurrent callers share an in-flight PNG task per source; a bounded encoder pool avoids UI blocking. No WPF window or desktop screenshot is taken. Device clipboard reads do not synthesize Command+C or overwrite the Windows clipboard. Empty text is a valid clipboard value.

## Limits and errors

Defaults: 60 authenticated requests/s (burst 120), 5 screenshot requests/s, two simultaneous encoders, 64 KiB UTF-8 text, 128 KiB JSON, and 64 HTTP connections. Request rates are per credential; applications sharing a key share its budget. Limits are configurable under `AutomationApi`; the input queue is fail-fast. Release has a separate limited allowance. Path expansion remains bounded by points/duration and the existing transport pacing.

Errors use `{ "error": { "code": "...", "message": "..." }, "requestId": "..." }`:

| HTTP | Codes |
|---|---|
| 400 | INVALID_REQUEST |
| 401 / 403 | UNAUTHORIZED / FORBIDDEN |
| 404 | DEVICE_NOT_FOUND |
| 409 | CONTROL_LOCKED, CONTROL_NOT_AVAILABLE, GEOMETRY_CHANGED |
| 422 | CAPABILITY_NOT_SUPPORTED, INVALID_COORDINATE, INVALID_DURATION |
| 429 | RATE_LIMITED; Retry-After header |
| 500 | INTERNAL_ERROR / INPUT_FAILED |
| 503 | DEVICE_NOT_CONNECTED, CONTROL_NOT_AVAILABLE, CLIPBOARD_UNAVAILABLE, SCREENSHOT_UNAVAILABLE |

Future event/frame WebSockets will reuse device identity, ownership, authentication and frame services. No WebSocket stream or script execution endpoint is exposed in v1.

## Validation and publishing

Run `App.Runtime.Tests --automation-api`, Python `automation_clipboard_test.py`, the existing keyboard/router/mapping/clipboard/touch suites and localization verification. Real USB/wireless/BLE and multi-device coordinate effects still need device testing; simulation is not proof of an iPhone action.

Kestrel is supplied by `Microsoft.AspNetCore.App`. Both self-contained publish variants must ship its runtime, with no IIS/Hosting Bundle installation. The installer needs no API firewall rule. Local docs/OpenAPI are in the app assembly. Rebuild the repository bridge recipe so the packaged executable advertises the new clipboard capability; keep its runtime manifest/payload together. Packaging checks include ASP.NET assemblies for the multi-file variant.
