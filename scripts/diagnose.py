"""Read-only iPhoneMirror environment and device diagnostics."""

from __future__ import annotations

import ctypes
import argparse
import hashlib
import json
import os
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / "outputs" / "iPhoneMirror"
os.add_dll_directory(str(BIN))
core = ctypes.CDLL(str(BIN / "iPhoneMirror.Core.dll"))


class DeviceInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("api_version", ctypes.c_uint32),
        ("device_id", ctypes.c_uint32),
        ("mux_port", ctypes.c_uint32),
        ("state", ctypes.c_int32),
        ("usb_connected", ctypes.c_int32),
        ("pair_record_present", ctypes.c_int32),
        ("lockdown_accessible", ctypes.c_int32),
        ("udid", ctypes.c_wchar * 128),
        ("name", ctypes.c_wchar * 128),
        ("product_type", ctypes.c_wchar * 64),
        ("os_version", ctypes.c_wchar * 32),
        ("connection_type", ctypes.c_wchar * 32),
        ("status", ctypes.c_wchar * 192),
    ]


class EnvironmentInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("api_version", ctypes.c_uint32),
        ("service_installed", ctypes.c_int32),
        ("service_running", ctypes.c_int32),
        ("standard_mux", ctypes.c_int32),
        ("capture_mux", ctypes.c_int32),
        ("physical_apple_usb_devices", ctypes.c_uint32),
        ("diagnostic", ctypes.c_wchar * 512),
        ("libusb_runtime", ctypes.c_int32),
        ("usbdk_backend", ctypes.c_int32),
        ("libusb_apple_devices", ctypes.c_uint32),
        ("libusb_version", ctypes.c_wchar * 32),
    ]


class CaptureStatus(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("api_version", ctypes.c_uint32),
        ("state", ctypes.c_int32),
        ("width", ctypes.c_uint32),
        ("height", ctypes.c_uint32),
        ("fps", ctypes.c_double),
        ("latency_ms", ctypes.c_double),
        ("video_frames", ctypes.c_uint64),
        ("audio_packets", ctypes.c_uint64),
        ("audio_sample_rate", ctypes.c_uint32),
        ("audio_channels", ctypes.c_uint32),
        ("message", ctypes.c_wchar * 192),
    ]


class VideoFrameInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("api_version", ctypes.c_uint32),
        ("width", ctypes.c_uint32),
        ("height", ctypes.c_uint32),
        ("stride", ctypes.c_uint32),
        ("pixel_format", ctypes.c_uint32),
        ("timestamp_100ns", ctypes.c_int64),
    ]


core.im_initialize.restype = ctypes.c_int32
core.im_shutdown.restype = None
core.im_api_version.restype = ctypes.c_uint32
core.im_get_environment.argtypes = [ctypes.POINTER(EnvironmentInfo)]
core.im_get_environment.restype = ctypes.c_int32
core.im_refresh_devices.argtypes = [ctypes.POINTER(DeviceInfo), ctypes.POINTER(ctypes.c_uint32)]
core.im_refresh_devices.restype = ctypes.c_int32
core.im_last_error.restype = ctypes.c_wchar_p
core.im_start_capture.argtypes = [ctypes.c_wchar_p]
core.im_start_capture.restype = ctypes.c_int32
core.im_start_capture_ex.argtypes = [ctypes.c_wchar_p, ctypes.c_int32]
core.im_start_capture_ex.restype = ctypes.c_int32
core.im_stop_capture.restype = ctypes.c_int32
core.im_set_video_preferences.argtypes = [
    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32]
core.im_set_video_preferences.restype = ctypes.c_int32
core.im_set_audio_enabled.argtypes = [ctypes.c_int32]
core.im_set_audio_enabled.restype = ctypes.c_int32
core.im_set_audio_volume.argtypes = [ctypes.c_float]
core.im_set_audio_volume.restype = ctypes.c_int32
core.im_get_capture_status.argtypes = [ctypes.POINTER(CaptureStatus)]
core.im_get_capture_status.restype = ctypes.c_int32
core.im_copy_latest_video_frame.argtypes = [
    ctypes.POINTER(VideoFrameInfo), ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
core.im_copy_latest_video_frame.restype = ctypes.c_int32


def check(result: int) -> None:
    if result != 0:
        raise RuntimeError(core.im_last_error() or f"native error {result}")


def capture(
    udid: str,
    seconds: float,
    save_frame: Path | None = None,
    width: int = 0,
    height: int = 0,
    fps: int = 60,
    play_audio: bool = True,
    volume: float = 1.0,
) -> None:
    check(core.im_set_video_preferences(width, height, fps))
    check(core.im_set_audio_volume(ctypes.c_float(volume)))
    check(core.im_set_audio_enabled(1 if play_audio else 0))
    check(core.im_start_capture_ex(udid, 1 if play_audio else 0))
    started = time.monotonic()
    last: CaptureStatus | None = None
    try:
        while time.monotonic() - started < seconds:
            status = CaptureStatus(struct_size=ctypes.sizeof(CaptureStatus))
            check(core.im_get_capture_status(ctypes.byref(status)))
            last = status
            print(json.dumps({
                "elapsed": round(time.monotonic() - started, 2),
                "state": status.state,
                "message": status.message,
                "resolution": [status.width, status.height],
                "fps": round(status.fps, 2),
                "decode_ms": round(status.latency_ms, 2),
                "video_frames": status.video_frames,
                "audio_packets": status.audio_packets,
                "audio": [status.audio_sample_rate, status.audio_channels],
            }, ensure_ascii=False))
            if status.state == 7:
                raise RuntimeError(status.message)
            time.sleep(0.5)

        info = VideoFrameInfo(struct_size=ctypes.sizeof(VideoFrameInfo))
        size = ctypes.c_uint32()
        result = core.im_copy_latest_video_frame(ctypes.byref(info), None, ctypes.byref(size))
        if result == -3 and size.value:
            pixels = ctypes.create_string_buffer(size.value)
            info.struct_size = ctypes.sizeof(VideoFrameInfo)
            check(core.im_copy_latest_video_frame(
                ctypes.byref(info), ctypes.cast(pixels, ctypes.c_void_p), ctypes.byref(size)))
            print(json.dumps({
                "frame": {
                    "width": info.width,
                    "height": info.height,
                    "stride": info.stride,
                    "bytes": size.value,
                    "timestamp_100ns": info.timestamp_100ns,
                    "sha256": hashlib.sha256(pixels.raw[:size.value]).hexdigest(),
                }
            }, ensure_ascii=False))
            if save_frame:
                from PIL import Image
                save_frame.parent.mkdir(parents=True, exist_ok=True)
                image = Image.frombytes("RGBA", (info.width, info.height), pixels.raw[:size.value],
                    "raw", "BGRA", info.stride, 1)
                image.save(save_frame)
                print(json.dumps({"saved_frame": str(save_frame)}, ensure_ascii=False))
        elif last:
            detail = core.im_last_error() or last.message or "no decoded video frame was received"
            print(json.dumps({"frame_error": detail, "state": last.state}, ensure_ascii=False))
            raise RuntimeError(detail)
        else:
            raise RuntimeError("capture returned no status")
    finally:
        core.im_stop_capture()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--capture", metavar="UDID")
    parser.add_argument("--seconds", type=float, default=10.0)
    parser.add_argument("--save-frame", type=Path)
    parser.add_argument("--render-width", "--width", dest="width", type=int, default=0,
                        help="local D3D render-limit width; headless capture only stores it")
    parser.add_argument("--render-height", "--height", dest="height", type=int, default=0,
                        help="local D3D render-limit height; 0 with width 0 means native")
    parser.add_argument("--fps", type=int, choices=(120, 60, 30, 24), default=60,
                        help="local presentation cap; the USB source may remain 60 fps")
    parser.add_argument("--mute", action="store_true",
                        help="capture audio but do not play it through Windows")
    parser.add_argument("--volume", type=float, default=1.0,
                        help="Windows playback gain from 0.0 to 1.0")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    check(core.im_initialize())
    try:
        environment = EnvironmentInfo(struct_size=ctypes.sizeof(EnvironmentInfo))
        check(core.im_get_environment(ctypes.byref(environment)))
        count = ctypes.c_uint32()
        check(core.im_refresh_devices(None, ctypes.byref(count)))
        devices = (DeviceInfo * count.value)()
        for item in devices:
            item.struct_size = ctypes.sizeof(DeviceInfo)
        if count.value:
            capacity = ctypes.c_uint32(count.value)
            check(core.im_refresh_devices(devices, ctypes.byref(capacity)))
        payload = {
            "api_version": core.im_api_version(),
            "environment": {
                "service_installed": bool(environment.service_installed),
                "service_running": bool(environment.service_running),
                "standard_mux": bool(environment.standard_mux),
                "capture_mux": bool(environment.capture_mux),
                "physical_apple_usb_devices": environment.physical_apple_usb_devices,
                "libusb_runtime": bool(environment.libusb_runtime),
                "libusb_version": environment.libusb_version,
                "usbdk_backend": bool(environment.usbdk_backend),
                "libusb_apple_devices": environment.libusb_apple_devices,
                "diagnostic": environment.diagnostic,
            },
            "devices": [
                {
                    "device_id": item.device_id,
                    "mux_port": item.mux_port,
                    "state": item.state,
                    "udid": item.udid,
                    "name": item.name,
                    "product_type": item.product_type,
                    "os_version": item.os_version,
                    "connection_type": item.connection_type,
                    "paired": bool(item.pair_record_present),
                    "lockdown_accessible": bool(item.lockdown_accessible),
                    "status": item.status,
                }
                for item in devices
            ],
        }
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        if args.capture:
            if (args.width == 0) != (args.height == 0):
                parser.error("--width and --height must both be zero or both be non-zero")
            if not 0.0 <= args.volume <= 1.0:
                parser.error("--volume must be between 0.0 and 1.0")
            capture(args.capture, args.seconds, args.save_frame,
                    args.width, args.height, args.fps, not args.mute, args.volume)
    finally:
        core.im_shutdown()


if __name__ == "__main__":
    main()
