#!/usr/bin/env python3
"""
USB 直连 iPhone 触控独立 Demo。

独立 USB 鼠标映射 Demo。
直接使用 pymobiledevice3 公开 API + userspace TCP 隧道。

用法:
  python usb_mouse_demo.py                          # 自动发现设备
  python usb_mouse_demo.py --udid 00008101-...      # 指定 UDID
  python usb_mouse_demo.py --tap 0.5 0.5            # 在 (50%, 50%) 点击
  python usb_mouse_demo.py --swipe 0.2 0.5 0.8 0.5  # 从 (20%, 50%) 滑到 (80%, 50%)
  python usb_mouse_demo.py --interactive            # 交互模式
"""

from __future__ import annotations

import argparse
import asyncio
import struct
import time
import sys
from typing import Optional

import pymobiledevice3.remote.tunnel_service as _ts
_ts.USE_USERSPACE_TUNNEL = True

from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.remote.common import TunnelProtocol
from pymobiledevice3.remote.module_imports import start_tunnel
from pymobiledevice3.remote.remote_service_discovery import RemoteServiceDiscoveryService
from pymobiledevice3.remote.tunnel_service import CoreDeviceTunnelProxy
from pymobiledevice3.remote.core_device.display_service import DisplayService
from pymobiledevice3.remote.core_device.hid_service import (
    UniversalHIDServiceService,
    IndigoHIDService,
    TOUCHSCREEN_STATE_CONTACT,
    TOUCHSCREEN_STATE_RELEASE,
    DIGITIZER_SURFACE_MAIN_TOUCHSCREEN,
    HID_BUTTON_STATE_DOWN,
    HID_BUTTON_STATE_UP,
)


def to_pixel(norm: float) -> int:
    return int(round(max(0.0, min(1.0, norm)) * 65535))


class UsbDirectTouch:
    """USB 直连触控会话 — 直接 API，不经过 stdin/stdout IPC。"""

    def __init__(self, udid: Optional[str] = None) -> None:
        self.udid = udid
        self.rsd: Optional[RemoteServiceDiscoveryService] = None
        self.hid: Optional[UniversalHIDServiceService] = None
        self.indigo: Optional[IndigoHIDService] = None
        self.display: Optional[DisplayService] = None
        self.dial_plane = None
        self.gate_open = False
        self._lock = asyncio.Lock()

    async def connect(self) -> None:
        print('[1/6] 正在连接 USB 设备...')
        lockdown = await create_using_usbmux(serial=self.udid)
        self.udid = lockdown.udid
        print(f'    UDID: {self.udid}')
        print(f'    产品: {lockdown.product_type} / iOS {lockdown.product_version}')

        service = await CoreDeviceTunnelProxy.create(lockdown)
        print('[2/6] CoreDevice 隧道已建立')

        async with start_tunnel(service, protocol=TunnelProtocol.TCP) as tunnel_result:
            print(f'[3/6] 数据通道已建立: {tunnel_result.address}:{tunnel_result.port}')
            from pymobiledevice3.remote.userspace_tunnel import UserspaceDialPlane
            tun = tunnel_result.client.tun
            tun.set_peer(tunnel_result.address)
            self.dial_plane = UserspaceDialPlane(tun, tunnel_result.address)
            await self.dial_plane.__aenter__()
            try:
                self.rsd = RemoteServiceDiscoveryService(
                    (tunnel_result.address, tunnel_result.port),
                    open_connection=self.dial_plane.dial,
                )
                await self.rsd.__aenter__()
                print(f'[4/6] 设备服务目录已连接: {self.rsd.product_type} / iOS {self.rsd.product_version}')

                await self._init_hid()
                await self._try_open_gate()
                await self._run()
            finally:
                await self._cleanup()
        await lockdown.close()

    async def _init_hid(self) -> None:
        print('[5/6] 初始化触控与按键通道...')
        self.hid = UniversalHIDServiceService(self.rsd)
        await self.hid.__aenter__()
        surfaces = await asyncio.wait_for(self.hid.list_connected_services(), 8)
        svcs = surfaces.get('connectedServices', [])
        print(f'    已发现 {len(svcs)} 个 HID surface:')
        for s in svcs:
            print(f'      _ServiceID={s.get("_ServiceID")} '
                  f'{s.get("Product", "?")} / {s.get("PrimaryUsage", "?")} '
                  f'Built-In={s.get("Built-In", "?")}')

        self.indigo = IndigoHIDService(self.rsd)
        await self.indigo.__aenter__()
        print('    Indigo HID 服务已连接（按钮可用，无需 media stream gate）')

    async def _try_open_gate(self) -> None:
        print('[6/6] 检查媒体流认证状态...')
        self.display = DisplayService(self.rsd)
        await self.display.__aenter__()
        try:
            from pymobiledevice3.remote.core_device.screen_stream import open_media_receiver
            transport, receiver_ip = open_media_receiver(self.display, (1 * 1024 * 1024,))
            sender_ip = self.rsd.service.address[0]
            await asyncio.wait_for(
                self.display.start_video_stream(
                    receiver_ip=receiver_ip, receiver_port=transport.port,
                    sender_ip=sender_ip, display_id=1,
                ),
                timeout=10.0,
            )
            self.gate_open = True
            self._transport = transport
            self._drain_task = asyncio.create_task(self._drain(transport))
            print('    ✅ Gate 已打开 — 触控可用！')
        except Exception as e:
            if '9021' in str(e):
                print('    认证状态：设备返回 9021，媒体流不可用；触控能力需以设备实测为准。')
            else:
                print(f'    ⚠️  Gate 打开失败: {type(e).__name__}: {str(e)[:200]}')

    async def _drain(self, transport) -> None:
        try:
            while True:
                await transport.recv()
        except (asyncio.CancelledError, OSError):
            pass

    async def touch_down(self, x: float, y: float, slot: int = 0) -> None:
        async with self._lock:
            report = self._build_report(slot, TOUCHSCREEN_STATE_CONTACT, to_pixel(x), to_pixel(y))
            await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)

    async def touch_move(self, x: float, y: float, slot: int = 0) -> None:
        async with self._lock:
            report = self._build_report(slot, TOUCHSCREEN_STATE_CONTACT, to_pixel(x), to_pixel(y))
            await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)

    async def touch_up(self, x: float, y: float, slot: int = 0) -> None:
        async with self._lock:
            report = self._build_report(slot, TOUCHSCREEN_STATE_RELEASE, to_pixel(x), to_pixel(y))
            await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)

    async def tap(self, x: float, y: float) -> None:
        await self.touch_down(x, y)
        await asyncio.sleep(0.08)
        await self.touch_up(x, y)

    async def swipe(self, x1: float, y1: float, x2: float, y2: float, steps: int = 20) -> None:
        await self.touch_down(x1, y1)
        for i in range(1, steps + 1):
            x = x1 + (x2 - x1) * i / steps
            y = y1 + (y2 - y1) * i / steps
            await self.touch_move(x, y)
            await asyncio.sleep(0.03)
        await self.touch_up(x2, y2)

    async def press_home(self) -> None:
        await self.indigo.send_button(usage_page=0x09, usage_code=0x01, state=HID_BUTTON_STATE_DOWN)
        await asyncio.sleep(0.1)
        await self.indigo.send_button(usage_page=0x09, usage_code=0x01, state=HID_BUTTON_STATE_UP)

    async def press_volume_up(self) -> None:
        await self.indigo.send_button(usage_page=0x0C, usage_code=0xE9, state=HID_BUTTON_STATE_DOWN)
        await asyncio.sleep(0.05)
        await self.indigo.send_button(usage_page=0x0C, usage_code=0xE9, state=HID_BUTTON_STATE_UP)

    @staticmethod
    def _build_report(slot: int, state: int, x: int, y: int) -> bytes:
        ts = time.monotonic_ns() & ((1 << 48) - 1)
        if state == TOUCHSCREEN_STATE_CONTACT:
            state_byte = 0xC2 | (slot & 0x07)
        else:
            state_byte = 0x02 | (slot & 0x07)
        return (
            bytes([0x09, 0x01, 0x05, state_byte])
            + struct.pack('<HH', x & 0xFFFF, y & 0xFFFF)
            + b'\x00' * 32
            + b'\x02\x00\x00\x00'
            + ts.to_bytes(6, 'little')
            + b'\x00' * 8
        )

    async def _run(self) -> None:
        raise NotImplementedError

    async def _cleanup(self) -> None:
        print('\n[清理] 正在释放活动触点并关闭 USB 会话...')
        if self.hid is not None:
            try:
                for slot in range(5):
                    report = self._build_report(slot, TOUCHSCREEN_STATE_RELEASE, 0, 0)
                    await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)
            except Exception:
                pass
            try:
                await self.hid.__aexit__(None, None, None)
            except Exception:
                pass
        if hasattr(self, '_drain_task') and self._drain_task:
            self._drain_task.cancel()
            try:
                await self._drain_task
            except Exception:
                pass
        if hasattr(self, '_transport') and self._transport:
            try:
                self._transport.close()
            except Exception:
                pass
        if self.display is not None:
            try:
                await self.display.__aexit__(None, None, None)
            except Exception:
                pass
        if self.indigo is not None:
            try:
                await self.indigo.__aexit__(None, None, None)
            except Exception:
                pass
        if self.rsd is not None:
            try:
                await self.rsd.__aexit__(None, None, None)
            except Exception:
                pass
        if self.dial_plane is not None:
            try:
                await self.dial_plane.__aexit__(None, None, None)
            except Exception:
                pass
        print('[完成] USB 触控会话已安全关闭')


class TapDemo(UsbDirectTouch):
    def __init__(self, udid, x, y):
        super().__init__(udid)
        self._x, self._y = x, y

    async def _run(self):
        print(f'\n[动作] 点击 ({self._x:.1%}, {self._y:.1%})')
        await self.tap(self._x, self._y)
        print('  点击样本已发送')
        if not self.gate_open:
            print('  提示：媒体流认证未打开，最终送达状态取决于设备系统')

        await asyncio.sleep(1)


class SwipeDemo(UsbDirectTouch):
    def __init__(self, udid, x1, y1, x2, y2):
        super().__init__(udid)
        self._coords = (x1, y1, x2, y2)

    async def _run(self):
        x1, y1, x2, y2 = self._coords
        print(f'\n[动作] 滑动 ({x1:.1%}, {y1:.1%}) → ({x2:.1%}, {y2:.1%})')
        await self.swipe(x1, y1, x2, y2)
        print('  滑动样本已发送')
        if not self.gate_open:
            print('  提示：媒体流认证未打开，最终送达状态取决于设备系统')

        await asyncio.sleep(1)


class InteractiveDemo(UsbDirectTouch):
    async def _run(self):
        print('\n=== 交互模式 ===')
        print('命令:')
        print('  tap X Y          — 在 (X, Y) 点击，坐标 0.0-1.0')
        print('  swipe X1 Y1 X2 Y2 — 滑动')
        print('  home             — 按 Home 按钮（Indigo，可用）')
        print('  volup            — 按音量+（Indigo，可用）')
        print('  gate             — 显示 gate 状态')
        print('  quit             — 退出')
        print()

        loop = asyncio.get_event_loop()
        while True:
            try:
                line = await loop.run_in_executor(None, input, '> ')
            except EOFError:
                break
            parts = line.strip().split()
            if not parts:
                continue
            cmd = parts[0]
            try:
                if cmd == 'quit':
                    break
                elif cmd == 'tap' and len(parts) >= 3:
                    await self.tap(float(parts[1]), float(parts[2]))
                    print('  tap 已发送')
                elif cmd == 'swipe' and len(parts) >= 5:
                    await self.swipe(float(parts[1]), float(parts[2]),
                                     float(parts[3]), float(parts[4]))
                    print('  swipe 已发送')
                elif cmd == 'home':
                    await self.press_home()
                    print('  Home 按钮已发送')
                elif cmd == 'volup':
                    await self.press_volume_up()
                    print('  音量+ 已发送')
                elif cmd == 'gate':
                    print(f'  gateOpen = {self.gate_open}')
                else:
                    print('  未知命令')
            except Exception as e:
                print(f'  错误: {type(e).__name__}: {e}')


async def main_async(args) -> None:
    if args.tap:
        demo = TapDemo(args.udid, args.tap[0], args.tap[1])
    elif args.swipe:
        demo = SwipeDemo(args.udid, args.swipe[0], args.swipe[1],
                         args.swipe[2], args.swipe[3])
    elif args.interactive:
        demo = InteractiveDemo(args.udid)
    else:
        demo = InteractiveDemo(args.udid)
    await demo.connect()


def main() -> None:
    parser = argparse.ArgumentParser(description='USB 直连 iPhone 触控独立 Demo')
    parser.add_argument('--udid', default=None, help='设备 UDID')
    parser.add_argument('--tap', nargs=2, type=float, metavar=('X', 'Y'),
                        help='在 (X, Y) 点击，坐标 0.0-1.0')
    parser.add_argument('--swipe', nargs=4, type=float, metavar=('X1', 'Y1', 'X2', 'Y2'),
                        help='从 (X1, Y1) 滑到 (X2, Y2)')
    parser.add_argument('--interactive', action='store_true', help='交互模式')
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print('\n已中断')


if __name__ == '__main__':
    main()
