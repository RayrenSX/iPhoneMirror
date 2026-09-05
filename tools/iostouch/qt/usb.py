"""pyusb 设备层：发现 iPhone、激活隐藏 QuickTime 配置、claim 0x2A 接口、收发。

Windows 上必须使用 **libusb-win32 过滤驱动 + libusb0 后端**（与 Apple 驱动共存）；
libusb-1.0 后端只有在设备被 WinUSB 接管时才可用，但那会破坏 usbmuxd，故不推荐。
"""

from __future__ import annotations

import logging
import sys
import threading
import time
from dataclasses import dataclass
from typing import Callable, Optional

import usb.core
import usb.util

logger = logging.getLogger(__name__)

APPLE_VID = 0x05AC
CLASS_VENDOR = 0xFF
SUBCLASS_USBMUX = 0xFE
SUBCLASS_QUICKTIME = 0x2A


def _is_timeout(exc: BaseException) -> bool:
    """libusb0 后端的超时是普通 USBError + 'timeout' 文字；libusb1 是 USBTimeoutError 或 errno 110/10060。"""
    if isinstance(exc, usb.core.USBTimeoutError):
        return True
    errno = getattr(exc, "errno", None)
    if errno in (110, 10060, 116):
        return True
    return "timeout" in str(exc).lower()


def get_backend(prefer: str = "auto"):
    """选择 pyusb 后端：Windows 优先 libusb0（libusb-win32），否则 libusb1。"""
    import usb.backend.libusb0 as b0
    import usb.backend.libusb1 as b1

    order = {"auto": (b0, b1) if sys.platform == "win32" else (b1, b0), "libusb0": (b0,), "libusb1": (b1,)}[prefer]
    for mod in order:
        try:
            backend = mod.get_backend()
        except Exception as exc:  # noqa: BLE001
            logger.debug("backend %s failed: %s", mod.__name__, exc)
            backend = None
        if backend is not None:
            logger.info("USB backend: %s", mod.__name__.rsplit(".", 1)[-1])
            return backend
    raise RuntimeError(
        "没有可用的 libusb 后端。Windows 请安装 libusb-win32 过滤驱动（libusb0.dll 需在 PATH 或程序目录），"
        "或 pip install libusb 提供 libusb-1.0.dll"
    )


class StaleConfigError(RuntimeError):
    """设备停留在隐藏配置且关闭请求无效；调用方可尝试直接探测或硬复位。"""


@dataclass
class IosUsbDevice:
    dev: "usb.core.Device"
    serial: str
    mux_config: int
    qt_config: int
    original_config: int = -1  # 激活前实际生效的配置，停止时恢复

    @property
    def activated(self) -> bool:
        return self.qt_config != -1

    def describe(self) -> dict:
        cfgs = []
        for cfg in self.dev:
            ifaces = []
            for intf in cfg:
                eps = [f"0x{ep.bEndpointAddress:02x}({'IN' if ep.bEndpointAddress & 0x80 else 'OUT'},{ep.wMaxPacketSize})" for ep in intf]
                ifaces.append({"number": intf.bInterfaceNumber, "class": intf.bInterfaceClass,
                               "subclass": intf.bInterfaceSubClass, "endpoints": eps})
            cfgs.append({"value": cfg.bConfigurationValue, "interfaces": ifaces})
        return {"serial": self.serial, "vid": f"0x{self.dev.idVendor:04x}", "pid": f"0x{self.dev.idProduct:04x}",
                "mux_config": self.mux_config, "qt_config": self.qt_config, "configs": cfgs}


def _find_interface_for_subclass(cfg, subclass: int):
    for intf in cfg:
        if intf.bInterfaceClass == CLASS_VENDOR and intf.bInterfaceSubClass == subclass:
            return intf
    return None


def _find_configs(dev) -> tuple[int, int]:
    mux, qt = -1, -1
    for cfg in dev:
        has_qt = _find_interface_for_subclass(cfg, SUBCLASS_QUICKTIME) is not None
        has_mux = _find_interface_for_subclass(cfg, SUBCLASS_USBMUX) is not None
        if has_mux and not has_qt:
            mux = cfg.bConfigurationValue
        if has_qt:
            qt = cfg.bConfigurationValue
    return mux, qt


def _serial_of(dev) -> str:
    try:
        s = usb.util.get_string(dev, dev.iSerialNumber) or ""
    except Exception:  # noqa: BLE001
        s = ""
    return correct_serial(s.rstrip("\x00").strip())


def correct_serial(s: str) -> str:
    """USB 序列号 24 位时缺连字符（应为 8-16 形式的 UDID）。"""
    if len(s) == 24 and "-" not in s:
        return s[:8] + "-" + s[8:]
    return s


def find_devices(backend=None, udid: Optional[str] = None) -> list[IosUsbDevice]:
    backend = backend or get_backend()
    found = []
    for dev in usb.core.find(find_all=True, idVendor=APPLE_VID, backend=backend):
        try:
            mux, qt = _find_configs(dev)
        except Exception as exc:  # noqa: BLE001
            logger.debug("skip device: %s", exc)
            continue
        if mux == -1 and qt == -1:
            continue
        serial = _serial_of(dev)
        if udid and serial != udid and serial.replace("-", "") != udid.replace("-", ""):
            continue
        found.append(IosUsbDevice(dev, serial, mux, qt))
    return found


def send_qt_config_request(dev, enable: bool) -> None:
    """bmRequestType=0x40 bRequest=0x52 wValue=0 wIndex=2(开)/0(关)"""
    try:
        rc = dev.ctrl_transfer(0x40, 0x52, 0x00, 0x02 if enable else 0x00, b"", timeout=1000)
        logger.info("QT config control request (%s) sent, rc=%s", "enable" if enable else "disable", rc)
    except usb.core.USBError as exc:
        logger.warning("QT config control request (%s) errored (often harmless): %s", "enable" if enable else "disable", exc)


def _wait_for_device(backend, serial: str, want_activated: bool, retries: int, log=None,
                     resend_every: int = 0) -> Optional[IosUsbDevice]:
    """轮询等待设备以期望状态出现。``resend_every``>0 时每隔 N 次轮询重发一次激活/关闭请求。"""
    for i in range(retries):
        time.sleep(0.5)
        if log and i and i % 20 == 0:
            log(f"[usb] 仍在等待设备{'出现' if want_activated else '关闭'}隐藏配置 ... {i // 2}s（实测有时需要 1 分钟以上）")
        if resend_every and i and i % resend_every == 0:
            try:
                for d in find_devices(backend, serial):
                    if d.activated != want_activated:
                        logger.info("resending QT config %s request", "enable" if want_activated else "disable")
                        send_qt_config_request(d.dev, want_activated)
                        usb.util.dispose_resources(d.dev)
            except Exception as exc:  # noqa: BLE001
                logger.debug("resend failed: %s", exc)
        try:
            candidates = find_devices(backend, serial)
        except Exception as exc:  # noqa: BLE001  重枚举期间设备可能短暂消失
            logger.debug("enumerate failed while waiting: %s", exc)
            candidates = []
        for d in candidates:
            if d.activated == want_activated:
                return d
        logger.debug("waiting for QT config %s (%d/%d)", "on" if want_activated else "off", i + 1, retries)
    return None


def activate(device: IosUsbDevice, backend=None, retries: int = 40, log=None) -> IosUsbDevice:
    """激活隐藏配置并保证是**新会话**。

    设备只在刚激活时发起一次 QuickTime 会话（发 PING）；若配置 5 已经存在（上次遗留），
    连上去设备会一直沉默。所以发现已激活时先发关闭请求让其重枚举，再重新激活。
    """
    backend = backend or get_backend()
    if device.activated:
        logger.info("device %s already has QT config %d (stale session); resetting first", device.serial, device.qt_config)
        try:
            device = reset_device(device, backend, retries=30, log=log)   # 15s
        except RuntimeError as exc:
            raise StaleConfigError(str(exc)) from exc
    try:
        device.original_config = device.dev.get_active_configuration().bConfigurationValue
    except Exception:  # noqa: BLE001
        device.original_config = device.mux_config
    send_qt_config_request(device.dev, True)
    try:
        usb.util.dispose_resources(device.dev)
    except Exception:  # noqa: BLE001
        pass
    d = _wait_for_device(backend, device.serial, want_activated=True, retries=retries, log=log, resend_every=20)
    if d is None:
        raise RuntimeError(
            f"could not activate QuickTime config for {device.serial}（{retries * 0.5:.0f}s 内未出现隐藏配置）。"
            "iOS 锁屏时会拒绝屏幕采集：请解锁手机并保持亮屏后重试（可临时把“自动锁定”设为永不）"
        )
    d.original_config = device.original_config
    logger.info("QT config %d activated for %s (was config %d)", d.qt_config, d.serial, d.original_config)
    return d


def reset_device(device: IosUsbDevice, backend=None, retries: int = 20, log=None, hard: bool = False) -> IosUsbDevice:
    """让设备退出隐藏配置并重枚举回普通配置。

    默认只发关闭控制请求（安全）。``hard=True`` 时再做 USB 端口复位（等价于重新插拔）——
    实测复位后设备需要更久才能再次激活，调用方应给更长的等待。
    """
    backend = backend or get_backend()
    send_qt_config_request(device.dev, False)
    if hard:
        try:
            device.dev.reset()
            logger.warning("usb port reset issued for %s", device.serial)
        except Exception as exc:  # noqa: BLE001
            logger.warning("usb reset failed: %s", exc)
    try:
        usb.util.dispose_resources(device.dev)
    except Exception:  # noqa: BLE001
        pass
    fresh = _wait_for_device(backend, device.serial, want_activated=False, retries=retries, log=log)
    if fresh is None:
        raise RuntimeError(f"设备 {device.serial} 关闭隐藏配置后未以普通配置重新出现")
    time.sleep(1.0)  # 让系统驱动装载完成
    return fresh


def wait_for_replug(backend=None, udid: Optional[str] = None, timeout: float = 120.0, log=print) -> IosUsbDevice:
    """诊断用：提示用户拔掉再插上数据线，插上后立即返回新枚举的设备（未激活状态）。"""
    backend = backend or get_backend()
    present = bool(find_devices(backend, udid))
    log("[replug] 请拔掉 iPhone 数据线 ..." if present else "[replug] 未检测到设备，请插上数据线 ...")
    deadline = time.time() + timeout
    if present:
        while time.time() < deadline and find_devices(backend, udid):
            time.sleep(0.3)
        log("[replug] 已拔出，请重新插上 ...")
    while time.time() < deadline:
        devices = find_devices(backend, udid)
        if devices:
            d = devices[0]
            log(f"[replug] 检测到 {d.serial}（{'已' if d.activated else '未'}激活），立即激活")
            time.sleep(0.5)
            return d
        time.sleep(0.2)
    raise RuntimeError("等待重新插拔超时")


def deactivate(device: IosUsbDevice) -> None:
    """发送关闭隐藏配置的请求。仅非 Windows 平台再切回原配置（Windows 过滤驱动下切换配置有蓝屏风险）。"""
    send_qt_config_request(device.dev, False)
    if sys.platform == "win32":
        return
    target = device.original_config if device.original_config > 0 else (device.mux_config if device.mux_config != -1 else 1)
    try:
        device.dev.set_configuration(target)
    except Exception as exc:  # noqa: BLE001
        logger.debug("reset config failed: %s", exc)


class QuickTimeTransport:
    """claim 0x2A 接口后的 bulk 收发；``start_reading`` 在线程里循环读，把原始块交给回调。"""

    def __init__(self, device: IosUsbDevice, *, read_size: int = 64 * 1024, timeout_ms: int = 1000,
                 deactivate_on_close: bool = True) -> None:
        # deactivate_on_close：关闭时发送“关闭 QT 配置”控制请求，让设备自行重枚举回普通配置。
        # 只发控制请求、不调用 set_configuration（后者在 Windows 过滤驱动下曾导致蓝屏）。
        # 必须关闭：否则下次连接时设备不会再发起新会话。
        if not device.activated:
            raise RuntimeError("device not activated for screen mirroring")
        self.device = device
        self.dev = device.dev
        self.read_size = read_size
        self.timeout_ms = timeout_ms
        self.deactivate_on_close = deactivate_on_close
        self._intf = None
        self._ep_in = None
        self._ep_out = None
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self.bytes_in = 0

    def open(self) -> None:
        dev = self.dev
        # 必须通过 libusb 显式 SET_CONFIGURATION：libusb0 过滤驱动只认它自己设置过的配置，
        # 否则 claim 时报 "invalid configuration 0"（即使设备本身已处于该配置）
        logger.info("set configuration %d", self.device.qt_config)
        try:
            dev.set_configuration(self.device.qt_config)
        except usb.core.USBError as exc:
            logger.warning("set_configuration(%d) failed: %s（继续尝试 claim）", self.device.qt_config, exc)
        try:
            cfg = dev.get_active_configuration()
        except usb.core.USBError:
            cfg = None
        if cfg is None or cfg.bConfigurationValue != self.device.qt_config:
            cfg = next(c for c in dev if c.bConfigurationValue == self.device.qt_config)
        intf = _find_interface_for_subclass(cfg, SUBCLASS_QUICKTIME)
        if intf is None:
            raise RuntimeError("QuickTime interface (subclass 0x2A) not found in active config")
        if sys.platform != "win32":
            try:
                if dev.is_kernel_driver_active(intf.bInterfaceNumber):
                    dev.detach_kernel_driver(intf.bInterfaceNumber)
            except Exception as exc:  # noqa: BLE001
                logger.debug("detach kernel driver: %s", exc)
        try:
            usb.util.claim_interface(dev, intf.bInterfaceNumber)
        except usb.core.USBError as exc:
            if "invalid configuration" in str(exc):
                logger.warning("claim 失败（%s），强制 set_configuration 后重试", exc)
                dev.set_configuration(self.device.qt_config)
                usb.util.claim_interface(dev, intf.bInterfaceNumber)
            else:
                raise
        self._intf = intf
        self._ep_in = usb.util.find_descriptor(
            intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_IN
            and usb.util.endpoint_type(e.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
        self._ep_out = usb.util.find_descriptor(
            intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_OUT
            and usb.util.endpoint_type(e.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
        if self._ep_in is None or self._ep_out is None:
            raise RuntimeError("bulk endpoints not found on QuickTime interface")
        # CLEAR_FEATURE(ENDPOINT_HALT) 两个端点，与 qvh 一致
        for ep in (self._ep_in, self._ep_out):
            try:
                dev.ctrl_transfer(0x02, 0x01, 0, ep.bEndpointAddress, b"", timeout=1000)
            except usb.core.USBError as exc:
                logger.debug("clear feature 0x%02x: %s", ep.bEndpointAddress, exc)
        logger.info("QuickTime interface %d claimed: IN=0x%02x OUT=0x%02x",
                    intf.bInterfaceNumber, self._ep_in.bEndpointAddress, self._ep_out.bEndpointAddress)

    def write(self, data: bytes) -> None:
        for attempt in range(2):
            try:
                self._ep_out.write(data, timeout=self.timeout_ms)
                return
            except usb.core.USBError as exc:
                if _is_timeout(exc) and attempt == 0:
                    logger.warning("usb write timeout, retrying once")
                    continue
                logger.error("usb write failed: %s", exc)
                return

    def read_once(self) -> bytes:
        """读一块数据；超时返回空字节（设备暂无数据是正常现象，不是错误）。"""
        try:
            return bytes(self._ep_in.read(self.read_size, timeout=self.timeout_ms))
        except usb.core.USBTimeoutError:
            return b""
        except usb.core.USBError as exc:
            if _is_timeout(exc):
                return b""
            raise

    def start_reading(self, on_chunk: Callable[[bytes], None], on_error: Callable[[Exception], None]) -> None:
        def run() -> None:
            while not self._stop.is_set():
                try:
                    chunk = self.read_once()
                except Exception as exc:  # noqa: BLE001
                    if not self._stop.is_set():
                        on_error(exc)
                    return
                if chunk:
                    self.bytes_in += len(chunk)
                    on_chunk(chunk)

        self._thread = threading.Thread(target=run, name="qt-usb-reader", daemon=True)
        self._thread.start()

    def close(self) -> None:
        """保守的拆卸顺序：先让读线程彻底退出（不能有在途 IN 传输），再释放接口，最后释放句柄。"""
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=self.timeout_ms / 1000 + 2)
            if self._thread.is_alive():
                logger.warning("usb reader thread did not exit; skipping interface release to stay safe")
                return
        time.sleep(0.2)
        if self._intf is not None:
            try:
                usb.util.release_interface(self.dev, self._intf.bInterfaceNumber)
                logger.info("QuickTime interface released")
            except Exception as exc:  # noqa: BLE001
                logger.debug("release interface: %s", exc)
            self._intf = None
        if self.deactivate_on_close:
            deactivate(self.device)
        try:
            usb.util.dispose_resources(self.dev)
        except Exception:  # noqa: BLE001
            pass
