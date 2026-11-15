# iPhoneMirror Python SDK and stdio MCP

Uses the existing authenticated IPv4 loopback API. Python 3.10+; the base SDK has
no external dependencies. MCP uses the official `mcp` Python package.

```powershell
python -m pip install -e ./sdk/python
# Include the MCP server:
python -m pip install -e './sdk/python[mcp]'
$env:IPHONEMIRROR_API_KEY = '<copy from Developer tools>'
$env:IPHONEMIRROR_API_URL = 'http://127.0.0.1:17890'
```

Enable Automation API in Developer tools and choose a device from its returned
list. Preparation may need unlock/trust, Developer Mode, a matching personalized
DDI and an existing device binding. First-time DDI preparation can take minutes.

```python
from iphonemirror import Client

client = Client(timeout=420)  # first-time device preparation can mount/download DDI
print(client.devices())
phone = client.device('EXPLICIT_OPAQUE_ID_FROM_DEVICES')
phone.start_coredevice(audio=True)  # stop this device's previous capture first
print(phone.media_status())
print(phone.apps())
with phone:                       # acquire; release in finally, including on errors
    image = phone.screenshot()
    with open('phone.png', 'wb') as output:
        output.write(image.png)
    phone.tap_fraction(.5, .5)     # fractions of current screenshot; 1 maps to last pixel
    phone.launch_app('com.apple.Preferences')
phone.stop_coredevice()
```

Input uses the API's exclusive lease and geometry version. Renewals preserve the
same token. An expired/revoked owner is never silently replaced. No input or app
launch is retried after a timeout; its effect may already have occurred. Refresh
the screenshot and reacquire after a geometry change. There is no implicit first
device selection. URLs, proxies and redirects cannot send the API credential off
the local host.

Run `iphonemirror-mcp` or `python -m iphonemirror.mcp_server` as a **stdio** server
in your MCP client, passing those environment variables. Tools include explicit
device selection, acquire/release, PNG screenshot with geometry, tap/swipe,
keyboard/text, apps/launch, CoreDevice start/stop and media status. MCP keeps the
same leases and validation as SDK calls. Release control before stopping or
changing capture. Use `Client(timeout=420)` for manual first-time preparation;
prepare the device in the desktop UI before starting automated MCP sessions.

Neither SDK nor MCP starts a network listener or connects directly to the phone.
Offline replay is available in the desktop Device Hub and the packaged bridge
CLI documented in `docs/DEV_DEVICE_HUB.zh-CN.md`.

The expanded Device Hub adds 41 MCP tools and matching SDK methods:
device_info, capabilities, tunnel_info, list_apps (with include flags), app_icon,
processes, terminate, install_app, uninstall_app, profiles, install_profile,
remove_profile, settings, get_setting, set_setting, set_location, clear_location,
press_button, device_action, list_dir, read_file, download, export_crashes,
start_logs, poll_logs, stop_logs, start_recording and stop_recording.
Use prepare(enable_control=True) and open_window() for explicit setup.

Mutations require an explicitly acquired lease, for example:

    with phone:
        phone.set_setting('appearance', 'dark')
        phone.set_location(31.2, 121.5)
        phone.clear_location()
        phone.press_button('home')
        phone.install_app('MySignedApp.ipa')
        phone.install_profile('Development.mobileprovision')

The SDK streams uploads and renews the same lease token during long management
operations. Installation, removal and power operations are sent once, never retried.
Local SDK/MCP file paths choose client-side files; public HTTP actions never accept
an arbitrary server-side path. The shared action catalog is available through
client.actions(); phone.hub(operation, **arguments) invokes public actions.

phone.list_dir(path='', root='media') pages through files. phone.read_file reads
one bounded base64 chunk. phone.download('DCIM/file.mov', 'new-file.mov') downloads
to a new local file, detecting source changes. phone.export_crashes('new-directory')
exports up to 5000 files / 1 GiB. Existing destinations are not silently replaced.
phone.start_logs() returns streamId; pass it to phone.poll_logs(stream_id, cursor)
and finish with phone.stop_logs(stream_id). Poll within 60 seconds to retain the
subscription; dropped messages are reported.

See the [feature matrix](../../docs/RPLAYHUB_FEATURE_MATRIX.zh-CN.md) for scope and
real-device validation boundaries.
