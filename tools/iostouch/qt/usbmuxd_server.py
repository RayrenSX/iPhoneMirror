"""本地 usbmuxd 兼容服务（主机侧 plist 协议）。

pymobiledevice3 通过环境变量 ``USBMUXD_SOCKET_ADDRESS=127.0.0.1:<port>`` 连到这里，
所有 lockdown / CoreDevice 隧道流量经 :class:`~iostouch.qt.usbmux_usb.MuxDevice` 走我们自己 claim 的 USB 接口，
从而与 QuickTime 视频共存于同一 USB 配置。

线上格式（小端）：``length u32 | version u32(1=PLIST) | message u32(8=PLIST) | tag u32 | XML plist``。
支持的 MessageType：ReadBUID、ListDevices、Listen、ReadPairRecord、SavePairRecord、DeletePairRecord、Connect。
Connect 返回 Result 0 后，同一 socket 变成与设备端口的裸字节通道。
"""

from __future__ import annotations

import asyncio
import logging
import os
import plistlib
import struct
import threading
import uuid
from pathlib import Path
from typing import Optional

from iostouch.qt.usbmux_usb import ConnectionRefused, MuxConnection, MuxDevice, MuxError

logger = logging.getLogger(__name__)

VERSION_PLIST = 1
MSG_RESULT = 1
MSG_PLIST = 8
RESULT_OK = 0
RESULT_BADCOMMAND = 1
RESULT_BADDEV = 2
RESULT_CONNREFUSED = 3

_HDR = struct.Struct("<IIII")


def _pair_record_dir() -> Path:
    """Apple Mobile Device Service 的配对记录目录（Windows：%ALLUSERSPROFILE%\\Apple\\Lockdown）。"""
    if os.name == "nt":
        return Path(os.environ.get("ALLUSERSPROFILE", r"C:\ProgramData"), "Apple", "Lockdown")
    return Path("/var/lib/lockdown")


def _read_system_buid() -> str:
    p = _pair_record_dir() / "SystemConfiguration.plist"
    try:
        return plistlib.loads(p.read_bytes()).get("SystemBUID") or str(uuid.uuid4()).upper()
    except Exception:  # noqa: BLE001
        return "30142955-444094379208051516"  # pymobiledevice3 的默认 SYSTEM_BUID


class UsbmuxdServer:
    """asyncio TCP 服务；``mux`` 为已完成握手的 :class:`MuxDevice`。"""

    def __init__(self, mux: MuxDevice, serial: str, *, host: str = "127.0.0.1", port: int = 0,
                 device_id: int = 1, product_id: int = 0x12A8, pair_records: Optional[dict[str, bytes]] = None) -> None:
        self.mux = mux
        self.serial = serial
        self.host = host
        self.port = port
        self.device_id = device_id
        self.product_id = product_id
        self.pair_records: dict[str, bytes] = pair_records if pair_records is not None else {}
        self.buid = _read_system_buid()
        self._server: Optional[asyncio.AbstractServer] = None
        self._listeners: set[asyncio.StreamWriter] = set()
        self.connections = 0

    # ---------------------------------------------------------------- lifecycle
    async def start(self) -> int:
        try:
            self._server = await asyncio.start_server(self._handle_client, self.host, self.port)
        except OSError as exc:
            if self.port == 0:
                raise
            logger.warning("端口 %d 被占用（%s），改用系统分配的空闲端口", self.port, exc)
            self._server = await asyncio.start_server(self._handle_client, self.host, 0)
        self.port = self._server.sockets[0].getsockname()[1]
        logger.info("usbmuxd server listening on %s:%d for device %s", self.host, self.port, self.serial)
        return self.port

    async def stop(self) -> None:
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None

    @property
    def address(self) -> str:
        return f"{self.host}:{self.port}"

    # ---------------------------------------------------------------- framing
    @staticmethod
    async def _read_msg(reader: asyncio.StreamReader) -> tuple[int, dict]:
        hdr = await reader.readexactly(16)
        length, version, message, tag = _HDR.unpack(hdr)
        if length < 16 or length > 1 << 20:
            raise MuxError(f"bad usbmuxd frame length {length}")
        body = await reader.readexactly(length - 16)
        if message != MSG_PLIST:
            raise MuxError(f"unsupported usbmuxd message type {message} (binary protocol not implemented)")
        return tag, plistlib.loads(body)

    @staticmethod
    def _frame(tag: int, payload: dict) -> bytes:
        body = plistlib.dumps(payload)
        return _HDR.pack(16 + len(body), VERSION_PLIST, MSG_PLIST, tag) + body

    def _device_entry(self) -> dict:
        return {
            "DeviceID": self.device_id,
            "MessageType": "Attached",
            "Properties": {
                "ConnectionSpeed": 480000000,
                "ConnectionType": "USB",
                "DeviceID": self.device_id,
                "LocationID": 0,
                "ProductID": self.product_id,
                "SerialNumber": self.serial,
                "USBSerialNumber": self.serial.replace("-", ""),
            },
        }

    # ---------------------------------------------------------------- client handling
    async def _handle_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer = writer.get_extra_info("peername")
        logger.info("usbmuxd: client connected from %s", peer)
        try:
            while True:
                try:
                    tag, req = await self._read_msg(reader)
                except (asyncio.IncompleteReadError, ConnectionError):
                    return
                mt = req.get("MessageType")
                logger.debug("usbmuxd: %s from %s tag=%d", mt, peer, tag)
                if mt == "ReadBUID":
                    writer.write(self._frame(tag, {"BUID": self.buid}))
                elif mt == "ListDevices":
                    writer.write(self._frame(tag, {"DeviceList": [self._device_entry()]}))
                elif mt == "Listen":
                    writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_OK}))
                    writer.write(self._frame(tag, self._device_entry()))
                    self._listeners.add(writer)
                elif mt == "ReadPairRecord":
                    data = self._load_pair_record(str(req.get("PairRecordID", "")))
                    if data is None:
                        writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_BADDEV}))
                    else:
                        writer.write(self._frame(tag, {"PairRecordData": data}))
                elif mt == "SavePairRecord":
                    self.pair_records[str(req.get("PairRecordID", ""))] = bytes(req.get("PairRecordData", b""))
                    writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_OK}))
                elif mt == "DeletePairRecord":
                    self.pair_records.pop(str(req.get("PairRecordID", "")), None)
                    writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_OK}))
                elif mt == "Connect":
                    await self._handle_connect(tag, req, reader, writer)
                    return
                else:
                    logger.warning("usbmuxd: unsupported message %s from %s", mt, peer)
                    writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_BADCOMMAND}))
                await writer.drain()
        except Exception:  # noqa: BLE001
            logger.exception("usbmuxd client %s failed", peer)
        finally:
            self._listeners.discard(writer)
            with _suppress():
                writer.close()

    def _load_pair_record(self, identifier: str) -> Optional[bytes]:
        for key in (identifier, identifier.replace("-", ""), self.serial):
            if key in self.pair_records:
                return self.pair_records[key]
        for name in (identifier, identifier.replace("-", ""), self.serial, self.serial.replace("-", "")):
            p = _pair_record_dir() / f"{name}.plist"
            if p.is_file():
                try:
                    return p.read_bytes()
                except OSError:
                    continue
        return None

    async def _handle_connect(self, tag: int, req: dict, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        device_id = int(req.get("DeviceID", -1))
        port_raw = int(req.get("PortNumber", 0))
        port = ((port_raw & 0xFF) << 8) | ((port_raw >> 8) & 0xFF)  # 客户端按网络字节序发（htons）
        if device_id != self.device_id:
            writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_BADDEV}))
            await writer.drain()
            return
        loop = asyncio.get_running_loop()
        try:
            conn: MuxConnection = await loop.run_in_executor(None, self.mux.connect, port)
        except ConnectionRefused:
            writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_CONNREFUSED}))
            await writer.drain()
            return
        except MuxError as exc:
            logger.error("usbmux connect to port %d failed: %s", port, exc)
            writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_BADDEV}))
            await writer.drain()
            return
        self.connections += 1
        writer.write(self._frame(tag, {"MessageType": "Result", "Number": RESULT_OK}))
        await writer.drain()
        logger.info("usbmuxd: bridged client to device port %d (sport %d)", port, conn.sport)
        await self._bridge(reader, writer, conn)

    async def _bridge(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter, conn: MuxConnection) -> None:
        loop = asyncio.get_running_loop()
        stop = asyncio.Event()

        async def sock_to_usb() -> None:
            try:
                while not stop.is_set():
                    data = await reader.read(65536)
                    if not data:
                        break
                    await loop.run_in_executor(None, conn.send, data)
            except Exception as exc:  # noqa: BLE001
                logger.info("bridge sport=%d→%d sock→usb ended: %s", conn.sport, conn.dport, exc)
            else:
                logger.info("bridge sport=%d→%d: client closed socket", conn.sport, conn.dport)
            finally:
                stop.set()

        def _recv_blocking() -> bytes:
            return conn.recv(65536, timeout=0.5)

        async def usb_to_sock() -> None:
            try:
                while not stop.is_set():
                    data = await loop.run_in_executor(None, _recv_blocking)
                    if not data:
                        if conn.closed:
                            break
                        continue
                    writer.write(data)
                    await writer.drain()
            except Exception as exc:  # noqa: BLE001
                logger.info("bridge sport=%d→%d usb→sock ended: %s", conn.sport, conn.dport, exc)
            else:
                logger.info("bridge sport=%d→%d: mux connection closed (%s), rx %d B tx %d B",
                            conn.sport, conn.dport, conn.close_reason or "no reason", conn.bytes_rx, conn.bytes_tx)
            finally:
                stop.set()

        t1 = asyncio.create_task(sock_to_usb())
        t2 = asyncio.create_task(usb_to_sock())
        await stop.wait()
        conn.close()
        for t in (t1, t2):
            t.cancel()
        with _suppress():
            await asyncio.gather(t1, t2, return_exceptions=True)


class _suppress:
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return True

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return True


class UsbmuxdThread:
    """在后台线程里跑一个独立事件循环承载 :class:`UsbmuxdServer`，便于同步代码 / Tk 使用。"""

    def __init__(self, mux: MuxDevice, serial: str, port: int = 0, **kw) -> None:
        self.server = UsbmuxdServer(mux, serial, port=port, **kw)
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._thread: Optional[threading.Thread] = None
        self._started = threading.Event()
        self.error: Optional[BaseException] = None

    def start(self) -> str:
        def run() -> None:
            loop = asyncio.new_event_loop()
            self._loop = loop
            asyncio.set_event_loop(loop)
            try:
                loop.run_until_complete(self.server.start())
                self._started.set()
                loop.run_forever()
            except BaseException as exc:  # noqa: BLE001
                self.error = exc
                self._started.set()
            finally:
                loop.close()

        self._thread = threading.Thread(target=run, name="usbmuxd-server", daemon=True)
        self._thread.start()
        self._started.wait(10)
        if self.error:
            raise self.error
        return self.server.address

    def stop(self) -> None:
        loop = self._loop
        if loop is None:
            return

        async def _shutdown() -> None:
            await self.server.stop()
            loop.stop()

        asyncio.run_coroutine_threadsafe(_shutdown(), loop)
        if self._thread:
            self._thread.join(timeout=5)
