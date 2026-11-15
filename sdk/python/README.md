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
