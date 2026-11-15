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
    def list_apps(device_id: str, include_hidden: bool = False, include_internal: bool = False, include_clips: bool = False) -> dict:
        """List installed apps via an already ready CoreDevice bridge."""
        return selected(device_id).list_apps(include_hidden, include_internal, include_clips)

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

    @server.tool(annotations={"readOnlyHint": True})
    def device_info(device_id: str) -> dict:
        """Read hardware, battery and disk details; unavailable domains are explicit."""
        return selected(device_id).device_info()

    @server.tool(annotations={"readOnlyHint": True})
    def hub_capabilities(device_id: str) -> dict:
        """Inspect supported actions, advertised services, settings and file limits."""
        return selected(device_id).capabilities()

    @server.tool()
    def prepare_device(device_id: str, enable_control: bool = True) -> dict:
        """Explicitly prepare capture/control, including DDI when required."""
        return selected(device_id).prepare(enable_control)

    @server.tool()
    def open_device_window(device_id: str) -> dict:
        """Open this device's existing projection window."""
        return selected(device_id).open_window()

    @server.tool(annotations={"readOnlyHint": True})
    def app_icon(device_id: str, bundle_id: str) -> list:
        """Read an installed app's PNG icon."""
        import base64
        icon = selected(device_id).app_icon(bundle_id)
        return [Image(data=base64.b64decode(icon["data"], validate=True), format="png").to_image_content()]

    @server.tool(annotations={"readOnlyHint": True})
    def list_processes(device_id: str) -> dict:
        """List running process tokens and identifiers."""
        return selected(device_id).processes()

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def terminate_process(device_id: str, pid: int, signal: int = 15) -> dict:
        """Send SIGTERM (15) or SIGKILL (9) once; requires control."""
        return selected(device_id).terminate(pid, signal)

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def install_app(device_id: str, path: str) -> dict:
        """Upload an explicit local IPA (up to 512 MiB) and install it; requires control."""
        return selected(device_id).install_app(path)

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def uninstall_app(device_id: str, bundle_id: str) -> dict:
        """Remove an installed app and its data; requires control."""
        return selected(device_id).uninstall_app(bundle_id)

    @server.tool(annotations={"readOnlyHint": True})
    def list_profiles(device_id: str, kind: str = "all") -> dict:
        """List provisioning/configuration profiles without exposing private payloads."""
        return selected(device_id).profiles(kind)

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def install_profile(device_id: str, path: str) -> dict:
        """Upload a local mobileprovision/mobileconfig; configuration installation may prompt on the phone."""
        return selected(device_id).install_profile(path)

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def remove_profile(device_id: str, kind: str, identifier: str) -> dict:
        """Remove a specified provisioning/configuration profile; requires control."""
        return selected(device_id).remove_profile(kind, identifier)

    @server.tool(annotations={"readOnlyHint": True})
    def list_device_files(device_id: str, path: str = "", root: str = "media", offset: int = 0, limit: int = 200) -> dict:
        """Page through the media or crashes file service. Paths are relative; links cannot be traversed."""
        return selected(device_id).list_dir(path, root, offset, limit)

    @server.tool(annotations={"readOnlyHint": True})
    def read_device_file(device_id: str, path: str, root: str = "media", offset: int = 0, length: int = 196608) -> dict:
        """Read one bounded base64 chunk without writing a host file."""
        return selected(device_id).read_file(path, root, offset, length)

    @server.tool()
    def download_device_file(device_id: str, path: str, destination: str, root: str = "media") -> dict:
        """Export a device file to an explicit local destination; never replaces existing files."""
        return selected(device_id).download(path, destination, root)

    @server.tool()
    def export_crash_reports(device_id: str, destination: str) -> dict:
        """Export crash, spin and log reports into a new local directory (5000 files / 1 GiB maximum)."""
        return selected(device_id).export_crashes(destination)

    @server.tool(annotations={"readOnlyHint": True})
    def start_syslog(device_id: str) -> dict:
        """Start a bounded log subscription; poll within 60 seconds and stop when finished."""
        return selected(device_id).start_logs()

    @server.tool(annotations={"readOnlyHint": True})
    def poll_syslog(device_id: str, stream_id: str, cursor: int = 0, limit: int = 200) -> dict:
        """Read logs since a cursor; dropped messages and disconnects are reported."""
        return selected(device_id).poll_logs(stream_id, cursor, limit)

    @server.tool(annotations={"readOnlyHint": True})
    def stop_syslog(device_id: str, stream_id: str) -> dict:
        """Close this log subscription."""
        return selected(device_id).stop_logs(stream_id)

    @server.tool(annotations={"readOnlyHint": True})
    def get_phone_settings(device_id: str) -> dict:
        """Read appearance/accessibility settings; unsupported actions are explicit."""
        return selected(device_id).settings()

    @server.tool()
    def set_phone_setting(device_id: str, key: str, value: bool | float | str) -> dict:
        """Apply an allowlisted phone setting once; inspect capabilities for keys and text sizes."""
        return selected(device_id).set_setting(key, value)

    @server.tool()
    def set_simulated_location(device_id: str, latitude: float, longitude: float) -> dict:
        """Set simulated device location; requires control. Clear it after use."""
        return selected(device_id).set_location(latitude, longitude)

    @server.tool()
    def clear_simulated_location(device_id: str) -> dict:
        """Restore the phone's actual location; requires control."""
        return selected(device_id).clear_location()

    @server.tool(annotations={"destructiveHint": True, "idempotentHint": False})
    def device_power(device_id: str, action: str) -> dict:
        """Request restart, shutdown or sleep once; requires control."""
        return selected(device_id).device_action(action)

    @server.tool(annotations={"idempotentHint": False})
    def press_phone_button(device_id: str, button: str) -> dict:
        """Press home, app_switcher, lock, volume_up or volume_down once; requires control."""
        return selected(device_id).press_button(button)

    @server.tool()
    def start_recording(device_id: str, format: str = "h265") -> dict:
        """Record the active CoreDevice stream as keyframe-gated h265 or packet replay imrtp."""
        return selected(device_id).start_recording(format)

    @server.tool()
    def stop_recording(device_id: str) -> dict:
        """Stop recording and return its local server path and size."""
        return selected(device_id).stop_recording()

    @server.tool(annotations={"readOnlyHint": True})
    def tunnel_info(device_id: str) -> dict:
        """Inspect the selected bridge's existing tunnel without discovering another device."""
        return selected(device_id).tunnel_info()

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
