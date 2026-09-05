"""MuxBridge：在已激活隐藏配置的设备上，用我们自己的 usbmux 通道替代 Apple Mobile Device Service。

启动后设置 ``USBMUXD_SOCKET_ADDRESS``，此后本进程内 pymobiledevice3 的全部 usbmux 访问都走这里，
触摸隧道与 QuickTime 视频得以共存于同一 USB 配置。
"""

from __future__ import annotations

import logging
import os
import tempfile
from pathlib import Path
from typing import Optional

from iostouch.qt.usbmux_usb import MuxError, UsbMuxTransport
from iostouch.qt.usbmuxd_server import UsbmuxdThread

logger = logging.getLogger(__name__)
ENV = "USBMUXD_SOCKET_ADDRESS"
ADDR_FILE_NAME = "iostouch_usbmux.addr"


def addr_file() -> Path:
    return Path(tempfile.gettempdir()) / ADDR_FILE_NAME


def read_saved_address() -> Optional[str]:
    """读取上次 MuxBridge 写下的地址（供 ``--usbmux auto``）。"""
    try:
        return addr_file().read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def free_port_windows(port: int) -> None:
    """Windows：若端口被本项目遗留的 python 进程占用，结束它。"""
    import subprocess
    import sys

    if sys.platform != "win32" or port <= 0:
        return
    try:
        out = subprocess.run(["netstat", "-ano", "-p", "TCP"], capture_output=True, text=True, timeout=10).stdout
    except Exception as exc:  # noqa: BLE001
        logger.debug("netstat failed: %s", exc)
        return
    pids = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 5 and parts[1].endswith(f":{port}") and parts[3].upper() == "LISTENING":
            pids.add(parts[4])
    for pid in pids:
        try:
            info = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"], capture_output=True, text=True, timeout=10).stdout
        except Exception:  # noqa: BLE001
            info = ""
        if "python" in info.lower():
            logger.warning("端口 %d 被遗留的 python 进程 %s 占用，结束它", port, pid)
            subprocess.run(["taskkill", "/F", "/PID", pid], capture_output=True, timeout=10)
        else:
            logger.warning("端口 %d 被 PID %s 占用（非 python），不动它：%s", port, pid, info.strip()[:80])


class MuxBridge:
    def __init__(self, dev, serial: str, *, port: int = 0) -> None:
        self.dev = dev
        self.serial = serial
        self.port = port
        self.transport: Optional[UsbMuxTransport] = None
        self.server: Optional[UsbmuxdThread] = None
        self.address: Optional[str] = None
        self._prev_env: Optional[str] = None

    def start(self) -> str:
        free_port_windows(self.port)
        self.transport = UsbMuxTransport(self.dev, self.serial)
        try:
            self.transport.start()
        except MuxError:
            self.transport.close()
            self.transport = None
            raise
        self.server = UsbmuxdThread(self.transport.mux, self.serial, port=self.port)
        self.address = self.server.start()
        self._prev_env = os.environ.get(ENV)
        os.environ[ENV] = self.address
        try:
            addr_file().write_text(self.address, encoding="utf-8")
        except OSError as exc:
            logger.debug("write addr file: %s", exc)
        logger.info("MuxBridge up: %s=%s (device mux v%d)", ENV, self.address, self.transport.mux.version)
        return self.address

    def stop(self) -> None:
        try:
            if addr_file().exists() and addr_file().read_text(encoding="utf-8").strip() == self.address:
                addr_file().unlink()
        except OSError:
            pass
        if self._prev_env is None:
            os.environ.pop(ENV, None)
        else:
            os.environ[ENV] = self._prev_env
        if self.server is not None:
            try:
                self.server.stop()
            except Exception as exc:  # noqa: BLE001
                logger.debug("usbmuxd server stop: %s", exc)
            self.server = None
        if self.transport is not None:
            try:
                self.transport.close()
            except Exception as exc:  # noqa: BLE001
                logger.debug("usbmux transport close: %s", exc)
            self.transport = None
