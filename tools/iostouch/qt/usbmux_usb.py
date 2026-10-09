"""usbmuxd 设备侧协议（TCP-over-USB 复用）的 Python 实现。

蓝本：libimobiledevice/usbmuxd ``src/device.c``。线上格式（全部大端）：

* mux 头：``protocol u32 | length u32``；设备版本 ≥ 2 时再加 ``magic 0xfeedface u32 | tx_seq u16 | rx_seq u16``
* protocol：0 VERSION，1 CONTROL，2 SETUP，6 TCP
* VERSION 负载：``major u32 | minor u32 | padding u32``
* TCP 负载：标准 20 字节 TCP 头（sport/dport/seq/ack/off/flags/win/csum/urp）+ 数据；窗口字段为实际值 >> 8

流程：主机发 VERSION(2,0)（v1 头）→ 设备回 VERSION → 主机发 SETUP(b"\\x07")（v2 头，tx_seq=0/rx_seq=0xFFFF）
→ 之后每个连接：SYN → SYN|ACK → ACK；数据包带 ACK 标志；每收到一个数据包回一个 ACK；RST 关闭。

USB 传输规则：一个 mux 包一次 bulk 写；长度是 wMaxPacketSize 整数倍时补一个零长包（ZLP）。
"""

from __future__ import annotations

import logging
import struct
import threading
from dataclasses import dataclass
from typing import Callable, Optional

logger = logging.getLogger(__name__)

PROTO_VERSION = 0
PROTO_CONTROL = 1
PROTO_SETUP = 2
PROTO_TCP = 6
MUX_MAGIC = 0xFEEDFACE

TH_FIN = 0x01
TH_SYN = 0x02
TH_RST = 0x04
TH_PUSH = 0x08
TH_ACK = 0x10

USB_MTU = 3 * 16384          # 单个 mux 包最大长度（含头）
DEV_MRU = 65536              # 设备→主机单包上限
TCP_HDR_LEN = 20
TX_WINDOW = 131072
_TCPHDR = struct.Struct("!HHIIBBHHH")


class MuxError(Exception):
    pass


class ConnectionRefused(MuxError):
    pass


# --------------------------------------------------------------------------- packet build/parse
def build_mux_packet(version: int, proto: int, payload: bytes, tx_seq: int = 0, rx_seq: int = 0) -> bytes:
    if version >= 2:
        total = 16 + len(payload)
        return struct.pack("!IIIHH", proto, total, MUX_MAGIC, tx_seq & 0xFFFF, rx_seq & 0xFFFF) + payload
    total = 8 + len(payload)
    return struct.pack("!II", proto, total) + payload


def build_version_payload(major: int = 2, minor: int = 0) -> bytes:
    return struct.pack("!III", major, minor, 0)


def build_tcp(sport: int, dport: int, seq: int, ack: int, flags: int, win: int, data: bytes = b"") -> bytes:
    return _TCPHDR.pack(sport, dport, seq & 0xFFFFFFFF, ack & 0xFFFFFFFF, (TCP_HDR_LEN // 4) << 4, flags,
                        (win >> 8) & 0xFFFF, 0, 0) + data


@dataclass
class TcpSegment:
    sport: int
    dport: int
    seq: int
    ack: int
    flags: int
    win: int
    payload: bytes


def parse_tcp(data: bytes) -> TcpSegment:
    if len(data) < TCP_HDR_LEN:
        raise MuxError("short tcp header")
    sport, dport, seq, ack, off, flags, win, _csum, _urp = _TCPHDR.unpack_from(data, 0)
    hdr_len = (off >> 4) * 4
    return TcpSegment(sport, dport, seq, ack, flags, win << 8, data[hdr_len:])


class PacketAssembler:
    """按 mux 头里的 length 把任意切分的 USB 读块重组成完整 mux 包。"""

    def __init__(self) -> None:
        self._buf = bytearray()

    def feed(self, chunk: bytes) -> list[bytes]:
        self._buf += chunk
        out: list[bytes] = []
        while len(self._buf) >= 8:
            length = struct.unpack_from("!I", self._buf, 4)[0]
            if length < 8 or length > DEV_MRU:
                raise MuxError(f"bad mux length {length}")
            if len(self._buf) < length:
                break
            out.append(bytes(self._buf[:length]))
            del self._buf[:length]
        return out


# --------------------------------------------------------------------------- connection
class MuxConnection:
    """一条 TCP-over-USB 连接。收数据进 ``recv``，发数据用 ``send``；线程安全。"""

    def __init__(self, mux: "MuxDevice", sport: int, dport: int) -> None:
        self.mux = mux
        self.sport = sport
        self.dport = dport
        self.state = "connecting"
        self.tx_seq = 0
        self.tx_ack = 0
        self.rx_ack = 0
        self.rx_win = 0
        self.tx_win = TX_WINDOW
        self.max_payload = USB_MTU - 16 - TCP_HDR_LEN
        self._inbox = bytearray()
        self._cv = threading.Condition()
        self.closed = False
        self.refused = False
        self.bytes_rx = 0   # 设备→主机 载荷字节
        self.bytes_tx = 0   # 主机→设备 载荷字节
        self.close_reason = ""

    # ---- called by MuxDevice reader thread
    def _on_segment(self, seg: TcpSegment) -> None:
        forget = False
        with self._cv:
            self.rx_ack = seg.ack
            self.rx_win = seg.win
            if self.state == "connecting":
                if seg.flags == (TH_SYN | TH_ACK):
                    self.tx_seq += 1
                    self.tx_ack = seg.seq + 1
                    self.state = "connected"
                    self.mux._send_tcp(self, TH_ACK)
                else:
                    self.refused = bool(seg.flags & TH_RST)
                    self.state = "dead"
                    self.closed = True
                self._cv.notify_all()
                return
            if self.state == "connected":
                if seg.payload and not seg.flags & TH_RST:
                    self._inbox += seg.payload
                    self.bytes_rx += len(seg.payload)
                    self.tx_ack = seg.seq + len(seg.payload)
                    if not seg.flags & TH_FIN:
                        self.mux._send_tcp(self, TH_ACK)
                if seg.flags & (TH_RST | TH_FIN):
                    if seg.flags & TH_FIN:
                        self.tx_ack = seg.seq + len(seg.payload) + 1
                        try:
                            self.mux._send_tcp(self, TH_ACK)
                        except Exception as exc:  # noqa: BLE001
                            logger.debug("acknowledge FIN failed: %s", exc)
                    self.close_reason = "device sent " + ("RST" if seg.flags & TH_RST else "FIN")
                    logger.info("mux conn sport=%d dport=%d closed: %s (rx %d B, tx %d B, inbox %d B)",
                                self.sport, self.dport, self.close_reason, self.bytes_rx, self.bytes_tx, len(self._inbox))
                    self.state = "dead"
                    self.closed = True
                    forget = True
                    self._cv.notify_all()
                else:
                    self._cv.notify_all()
        if forget:
            self.mux._forget(self)

    # ---- public API
    def wait_connected(self, timeout: float = 10.0) -> None:
        with self._cv:
            self._cv.wait_for(lambda: self.state != "connecting", timeout)
            if self.state != "connected":
                if self.close_reason:
                    raise MuxError(self.close_reason)
                raise ConnectionRefused(f"connect to device port {self.dport} {'refused' if self.refused else 'failed'}")

    def _sendable(self) -> int:
        if self.closed:
            return -1
        inflight = (self.tx_seq - self.rx_ack) & 0xFFFFFFFF
        return max(0, min(self.rx_win - inflight, self.max_payload))

    def send(self, data: bytes, timeout: float = 30.0) -> None:
        view = memoryview(data)
        off = 0
        while off < len(view):
            with self._cv:
                if not self._cv.wait_for(lambda: self._sendable() != 0, timeout):
                    self.close_reason = f"send window stalled (device win {self.rx_win}, inflight {(self.tx_seq - self.rx_ack) & 0xFFFFFFFF})"
                    logger.warning("mux conn sport=%d dport=%d: %s", self.sport, self.dport, self.close_reason)
                    raise MuxError("send window stalled")
                n = self._sendable()
                if n < 0:
                    raise MuxError(f"connection closed ({self.close_reason or 'by host'})")
                chunk = bytes(view[off:off + n])
                self.mux._send_tcp(self, TH_ACK, chunk)
                self.tx_seq += len(chunk)
                self.bytes_tx += len(chunk)
            off += len(chunk)

    def recv(self, max_bytes: int = 65536, timeout: Optional[float] = None) -> bytes:
        """返回至少 1 字节；连接关闭且无数据时返回 b""。"""
        with self._cv:
            self._cv.wait_for(lambda: self._inbox or self.closed, timeout)
            if not self._inbox:
                return b""
            out = bytes(self._inbox[:max_bytes])
            del self._inbox[:max_bytes]
            return out

    def close(self) -> None:
        with self._cv:
            if not self.closed:
                self.closed = True
                if self.state == "connected":
                    try:
                        self.mux._send_tcp(self, TH_RST)
                    except Exception as exc:  # noqa: BLE001
                        logger.debug("send RST failed: %s", exc)
                self.state = "dead"
                self._cv.notify_all()
        self.mux._forget(self)

    def abort(self, reason: str) -> None:
        with self._cv:
            self.close_reason = reason
            self.closed = True
            self.state = "dead"
            self._cv.notify_all()
        self.mux._forget(self)


# --------------------------------------------------------------------------- device
class MuxDevice:
    """一台设备的 mux 会话。``write`` 由传输层注入（真机是 libusb bulk，测试用假实现）。"""

    def __init__(self, write: Callable[[bytes], None], wmax_packet: int = 512, serial: str = "") -> None:
        self._write_raw = write
        self.wmax = wmax_packet
        self.serial = serial
        self.version = 0
        self.tx_seq = 0
        self.rx_seq = 0
        self.ready = threading.Event()
        self._lock = threading.RLock()  # 收包处理里会再发 ACK/RST，必须可重入
        self._conns: dict[int, MuxConnection] = {}
        self._next_sport = 1
        self._failure_reason: Optional[str] = None
        self._resume_probe_pending = False
        self._assembler = PacketAssembler()
        self.on_control: Optional[Callable[[bytes], None]] = None

    # ---- outbound
    def _send_packet(self, proto: int, payload: bytes) -> None:
        with self._lock:
            if self._failure_reason is not None:
                raise MuxError(self._failure_reason)
            if self.version >= 2:
                if proto == PROTO_SETUP:
                    self.tx_seq, self.rx_seq = 0, 0xFFFF
                pkt = build_mux_packet(2, proto, payload, self.tx_seq, self.rx_seq)
                self.tx_seq = (self.tx_seq + 1) & 0xFFFF
            else:
                pkt = build_mux_packet(1, proto, payload)
            if len(pkt) > USB_MTU:
                raise MuxError("packet exceeds USB_MTU")
            self._write_raw(pkt)
            if len(pkt) % self.wmax == 0:
                self._write_raw(b"")  # ZLP

    def _send_tcp(self, conn: MuxConnection, flags: int, data: bytes = b"") -> None:
        self._send_packet(PROTO_TCP, build_tcp(conn.sport, conn.dport, conn.tx_seq, conn.tx_ack, flags, conn.tx_win, data))

    def start(self) -> None:
        """发送版本包，开始握手。之后通过 ``feed`` 喂入设备数据。"""
        self._send_packet(PROTO_VERSION, build_version_payload(2, 0))

    # ---- inbound（由传输层读线程调用）
    def feed(self, chunk: bytes) -> None:
        for pkt in self._assembler.feed(chunk):
            self._handle_packet(pkt)

    def _handle_packet(self, pkt: bytes) -> None:
        proto, _length = struct.unpack_from("!II", pkt, 0)
        if self.version >= 2:
            if len(pkt) < 16:
                return
            _magic, _tx, rx = struct.unpack_from("!IHH", pkt, 8)
            self.rx_seq = rx
            body = pkt[16:]
        else:
            body = pkt[8:]
        if proto == PROTO_VERSION:
            major, minor, _ = struct.unpack_from("!III", body, 0)
            if major not in (1, 2):
                raise MuxError(f"unsupported device mux version {major}")
            self.version = major
            logger.info("usbmux device version %d.%d", major, minor)
            if major >= 2:
                self._send_packet(PROTO_SETUP, b"\x07")
            self.ready.set()
        elif proto == PROTO_CONTROL:
            logger.debug("mux control: %s", body[:64])
            if self.on_control:
                self.on_control(body)
        elif proto == PROTO_TCP:
            seg = parse_tcp(body)
            conn = self._conns.get(seg.dport)  # 设备的 dport 就是我们的 sport
            if conn is None or conn.sport != seg.dport or conn.dport != seg.sport:
                if not seg.flags & TH_RST:
                    self._send_packet(PROTO_TCP, build_tcp(seg.dport, seg.sport, 0, seg.seq, TH_RST, 0))
                return
            conn._on_segment(seg)
        else:
            logger.debug("unknown mux proto %d", proto)

    # ---- connections
    def connect(self, dport: int, timeout: float = 10.0) -> MuxConnection:
        if not self.ready.wait(timeout):
            raise MuxError("usbmux version handshake not completed")
        with self._lock:
            if self._failure_reason is not None:
                raise MuxError(self._failure_reason)
            while self._next_sport in self._conns or self._next_sport == 0:
                self._next_sport = (self._next_sport + 1) & 0xFFFF
            sport = self._next_sport
            self._next_sport = (self._next_sport + 1) & 0xFFFF
            conn = MuxConnection(self, sport, dport)
            self._conns[sport] = conn
            # The first SYN after reopening a reader can go unanswered on an
            # otherwise live capture mux. Bound just this probe; Lockdown's
            # existing retry uses a fresh source port and the normal budget.
            connect_timeout = min(timeout, 0.75) if self._resume_probe_pending else timeout
            self._resume_probe_pending = False
        try:
            self._send_tcp(conn, TH_SYN)
            conn.wait_connected(connect_timeout)
        except Exception:
            conn.close()
            raise
        return conn

    def _forget(self, conn: MuxConnection) -> None:
        with self._lock:
            if self._conns.get(conn.sport) is conn:
                self._conns.pop(conn.sport)

    def close_all(self) -> None:
        with self._lock:
            connections = list(self._conns.values())
        for c in connections:
            c.close()

    def abort_all(self, reason: str) -> None:
        with self._lock:
            self._failure_reason = reason
            connections = list(self._conns.values())
            self.ready.set()
        for c in connections:
            c.abort(reason)


# --------------------------------------------------------------------------- libusb transport
class UsbMuxTransport:
    """在已激活的隐藏配置上 claim usbmux 接口（子类 0xFE），跑 :class:`MuxDevice`。"""

    SUBCLASS_USBMUX = 0xFE

    @property
    def failure_reason(self) -> Optional[str]:
        # A listener socket can remain open after the USB reader is gone.
        # Its address and VERSION-ready event are not evidence of liveness.
        if self.mux._failure_reason is not None:
            return self.mux._failure_reason
        if self._stop.is_set() or (self._thread is not None and not self._thread.is_alive()):
            return 'usbmux USB reader stopped'
        return None

    def __init__(self, dev, serial: str, *, timeout_ms: int = 200) -> None:
        import usb.util

        self.dev = dev
        self.serial = serial
        self.timeout_ms = timeout_ms
        cfg = dev.get_active_configuration()
        self._configuration = int(cfg.bConfigurationValue)
        intf = None
        for i in cfg:
            if i.bInterfaceClass == 0xFF and i.bInterfaceSubClass == self.SUBCLASS_USBMUX:
                intf = i
                break
        if intf is None:
            raise MuxError("usbmux interface (subclass 0xFE) not found in active configuration")
        usb.util.claim_interface(dev, intf.bInterfaceNumber)
        try:
            self._initialize_claimed_interface(intf)
        except BaseException:
            # A failed constructor never reaches the caller's mux variable.
            # Release our claim here so bounded retry/Apple fallback cannot
            # inherit a half-initialized owner. Preserve the startup failure
            # if unplugging also makes release fail.
            try:
                usb.util.release_interface(dev, intf.bInterfaceNumber)
            except Exception as exc:  # noqa: BLE001
                logger.debug("release partially initialized usbmux interface: %s", exc)
            raise

    def _initialize_claimed_interface(self, intf) -> None:
        import usb.util

        dev = self.dev
        self._intf = intf
        self._ep_in = usb.util.find_descriptor(
            intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_IN)
        self._ep_out = usb.util.find_descriptor(
            intf, custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_OUT)
        if self._ep_in is None or self._ep_out is None:
            raise MuxError("usbmux bulk endpoints not found")
        for ep in (self._ep_in, self._ep_out):
            try:
                # Reset the host pipe's DATA toggle as well as the device's.
                # A raw CLEAR_FEATURE resets only the phone; on reopen Windows
                # can retain the old toggle and silently drop the next packet.
                dev.clear_halt(ep.bEndpointAddress)
            except Exception as exc:  # noqa: BLE001
                logger.debug("clear halt 0x%02x: %s", ep.bEndpointAddress, exc)
        self.mux = MuxDevice(self._write, wmax_packet=self._ep_out.wMaxPacketSize or 512, serial=self.serial)
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self.bytes_in = 0
        self.bytes_out = 0
        self.resumed = False
        logger.info("usbmux interface %d claimed: IN=0x%02x OUT=0x%02x", intf.bInterfaceNumber,
                    self._ep_in.bEndpointAddress, self._ep_out.bEndpointAddress)

    def _write(self, data: bytes) -> None:
        self._ep_out.write(data, timeout=2000)
        self.bytes_out += len(data)

    def resume_from(self, previous: "UsbMuxTransport") -> None:
        """Reopen a failed reader without renegotiating the live device mux.

        The caller verifies the same phone's active capture configuration.
        VERSION is a one-shot handshake on that configuration. Keep its mux
        sequence numbers and next source port; discard only dead TCP clients.
        """
        if previous._thread is not None and previous._thread.is_alive():
            raise MuxError('cannot resume while the old USB reader is alive')
        if previous.mux.version not in (1, 2):
            raise MuxError('cannot resume an unnegotiated usbmux session')
        mux = previous.mux
        mux.abort_all('usbmux reader replaced')
        with mux._lock:
            mux._write_raw = self._write
            mux.wmax = self._ep_out.wMaxPacketSize or 512
            mux._assembler = PacketAssembler()
            mux._failure_reason = None
            mux._resume_probe_pending = True
        self.mux = mux
        self.resumed = True

    def export_checkpoint(self) -> dict:
        """Checkpoint only after the reader and every TCP connection stopped."""
        if self._thread is not None and self._thread.is_alive():
            raise MuxError('cannot checkpoint a live USB reader')
        with self.mux._lock:
            if self.mux.version not in (1, 2) or self.mux._conns:
                raise MuxError('cannot checkpoint an unnegotiated or busy mux')
            return dict(schema=1, serial=self.serial, bus=self.dev.bus,
                        address=self.dev.address, product=self.dev.idProduct,
                        configuration=self._configuration,
                        version=self.mux.version, tx=self.mux.tx_seq,
                        rx=self.mux.rx_seq, port=self.mux._next_sport)

    def import_checkpoint(self, state: dict) -> bool:
        # The parent additionally scopes this in-memory value to one native
        # capture lifetime. Never adopt another device/configuration, and never
        # restore TCP clients, pressed keys, tunnels or authentication state.
        expected = dict(schema=1, serial=self.serial, bus=self.dev.bus,
                        address=self.dev.address, product=self.dev.idProduct,
                        configuration=self._configuration)
        if not isinstance(state, dict) or any(state.get(k) != v for k, v in expected.items()):
            return False
        if state.get('version') not in (1, 2) or any(
                type(state.get(k)) is not int or not 0 <= state[k] <= 65535
                for k in ('tx', 'rx', 'port')):
            return False
        with self.mux._lock:
            self.mux.version = state['version']
            self.mux.tx_seq, self.mux.rx_seq = state['tx'], state['rx']
            self.mux._next_sport = state['port']
            self.mux._resume_probe_pending = True
            self.mux.ready.set()
        self.resumed = True
        return True

    def start(self) -> None:
        from iostouch.qt.usb import _is_timeout

        def run() -> None:
            while not self._stop.is_set():
                try:
                    chunk = bytes(self._ep_in.read(DEV_MRU, timeout=self.timeout_ms))
                except Exception as exc:  # noqa: BLE001
                    if _is_timeout(exc):
                        continue
                    if not self._stop.is_set():
                        logger.error("usbmux read failed, reader thread exiting; all mux connections are now dead: %s", exc)
                        self._stop.set()
                        self.mux.abort_all(f"USB read failed: {exc}")
                    return
                if chunk:
                    self.bytes_in += len(chunk)
                    try:
                        self.mux.feed(chunk)
                    except Exception:  # noqa: BLE001
                        logger.exception("usbmux packet handling failed")

        self._thread = threading.Thread(target=run, name="usbmux-usb-reader", daemon=True)
        self._thread.start()
        if self.resumed:
            logger.info('usbmux reader resumed without VERSION: version=%d tx_seq=%d next_sport=%d',
                        self.mux.version, self.mux.tx_seq, self.mux._next_sport)
        else:
            self.mux.start()
        if not self.mux.ready.wait(5.0):
            raise MuxError("device did not answer usbmux VERSION packet")
        if self.mux._failure_reason is not None:
            raise MuxError(self.mux._failure_reason)

    def close(self) -> None:
        import usb.util

        self.mux.close_all()
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=self.timeout_ms / 1000 + 1)
        try:
            usb.util.release_interface(self.dev, self._intf.bInterfaceNumber)
        except Exception as exc:  # noqa: BLE001
            logger.debug("release usbmux interface: %s", exc)
