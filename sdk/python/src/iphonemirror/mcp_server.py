"""stdio MCP: every device tool takes an opaque id; no default-device guessing."""
from __future__ import annotations

import atexit
import threading
from .client import Client, MirrorError


def create_server(client=None):
    from mcp.server.fastmcp import FastMCP
    from mcp.server.fastmcp.utilities.types import Image
    from mcp.types import TextContent

    client = client or Client()
    server = FastMCP("iPhoneMirror")
    devices = {}
    lock = threading.RLock()

    def selected(device_id):
        with lock:
            if device_id not in devices:
                devices[device_id] = client.device(device_id)
            return devices[device_id]

    @server.tool()
    def list_devices() -> list[dict]:
        """List devices and capabilities. Choose an explicit id for later calls."""
        return client.devices()

    @server.tool()
    def acquire_control(device_id: str, seconds: int = 30) -> dict:
        """Reserve an already enabled device. Renewed on input; release when finished."""
        d = selected(device_id).acquire(seconds)
        return {"deviceId": d.id, "status": "acquired", "geometry": d.status()["geometry"]}

    @server.tool()
    def release_control(device_id: str) -> dict:
        """Release this client's lease and held contacts."""
        selected(device_id).release()
        return {"status": "released"}

    @server.tool()
    def screenshot(device_id: str) -> list:
        """Current native PNG plus geometry. Coordinates exclude preview borders and DPI."""
        shot = selected(device_id).screenshot()
        return [Image(data=shot.png, format="png").to_image_content(),
                TextContent(type="text", text=__import__("json").dumps(shot.geometry))]

    @server.tool()
    def tap(device_id: str, x: float, y: float) -> dict:
        """Tap using 0..1 screenshot fractions. Requires acquire_control."""
        return selected(device_id).tap_fraction(x, y)

    @server.tool()
    def swipe(device_id: str, x1: float, y1: float, x2: float, y2: float, duration: float = .3) -> dict:
        """Swipe between 0..1 screenshot fractions; duration is seconds."""
        return selected(device_id).swipe_fraction(x1, y1, x2, y2, duration)

    @server.tool()
    def press_key(device_id: str, key: str) -> dict:
        """Send a documented API key such as ENTER or ESCAPE."""
        return selected(device_id).key(key)

    @server.tool()
    def type_text(device_id: str, text: str) -> dict:
        """Paste text into the phone. Changes its clipboard; requires text capability."""
        return selected(device_id).text(text)

    @server.tool()
    def list_apps(device_id: str) -> dict:
        """List installed apps via an already ready CoreDevice bridge."""
        return selected(device_id).apps()

    @server.tool()
    def launch_app(device_id: str, bundle_id: str) -> dict:
        """Launch a specific app without killing its existing process; requires control."""
        return selected(device_id).launch_app(bundle_id)

    @server.tool()
    def start_coredevice_mirror(device_id: str, audio: bool = True) -> dict:
        """Explicitly start experimental CoreDevice mirroring; may prepare DDI. Never replaces an active capture."""
        return selected(device_id).start_coredevice(audio)

    @server.tool()
    def stop_coredevice_mirror(device_id: str) -> dict:
        """Stop this device's CoreDevice capture. Release its control lease first."""
        return selected(device_id).stop_coredevice()

    @server.tool()
    def media_status(device_id: str) -> dict:
        """Inspect decoded frame count, RTP gaps, drops and keyframe recovery."""
        return selected(device_id).media_status()

    def close():
        for device in list(devices.values()):
            try:
                device.release()
            except MirrorError:
                pass
    atexit.register(close)
    return server


def main():
    create_server().run(transport="stdio")


if __name__ == "__main__":
    main()
