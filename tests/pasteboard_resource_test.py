"""Retire the original helper before its accepted-descriptor limit aborts it."""
import asyncio
from pathlib import Path
import sys
import time
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, MagicMock, Mock, patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
import pasteboard_recovery as recovery
import usb_touch_bridge as bridge

class PasteboardResourceTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        session=bridge.TouchSession(SimpleNamespace(emit=AsyncMock()),120)
        session.rsd=SimpleNamespace(start_lockdown_service=AsyncMock())
        session._pasteboard_resource_rsd=session.rsd
        session._pasteboard_accepts=32
        session._pasteboard_resource_last_check=16
        session._pasteboard_resource_started=time.monotonic()-20
        return session

    async def test_pressure_is_retired_before_another_connection_opens(self):
        session=self.session()
        order=[]
        async def inspect(*args):order.append('inspect');return 123,200
        async def restart(**kwargs):order.append('retire');return True
        async def connect():
            order.append('connect')
            return SimpleNamespace(get_text=AsyncMock(return_value='complete'))
        session._restart_failed_pasteboard=AsyncMock(side_effect=restart)
        session._get_device_pasteboard=AsyncMock(side_effect=connect)
        with patch.object(bridge,'pasteboard_resource_usage',side_effect=inspect):
            self.assertEqual('complete',await session._read_device_pasteboard())
        self.assertEqual(['inspect','retire','connect'],order)
        session._restart_failed_pasteboard.assert_awaited_once_with(for_write=False,resource_pid=123)

    async def test_failed_retirement_never_opens_another_descriptor(self):
        session=self.session()
        session._restart_failed_pasteboard=AsyncMock(return_value=False)
        session._get_device_pasteboard=AsyncMock()
        with patch.object(bridge,'pasteboard_resource_usage',AsyncMock(return_value=(123,200))):
            with self.assertRaisesRegex(ConnectionError,'resource recovery'):
                await session._read_device_pasteboard()
        session._get_device_pasteboard.assert_not_awaited()
        self.assertFalse(session._pasteboard_lock.locked())

    async def test_publication_rebases_after_maintenance_before_native_read(self):
        session=self.session()
        events=[]
        async def emit(event):events.append(event)
        session.ipc.emit=emit
        async def inspect(*args):
            events.append({'event':'windows_copy_during_maintenance'})
            return 123,200
        session._restart_failed_pasteboard=AsyncMock(return_value=True)
        async def read():
            self.assertEqual({'event':'clipboard_read_started','readId':1},events[-1])
            return 'new phone copy'
        session._get_device_pasteboard=AsyncMock(return_value=SimpleNamespace(get_text=read))
        with patch.object(bridge,'pasteboard_resource_usage',side_effect=inspect):
            await session._publish_device_pasteboard()
        self.assertEqual(['clipboard_read_started','windows_copy_during_maintenance',
                          'clipboard_read_started','clipboard_text','clipboard_read_finished'],
                         [event['event'] for event in events])
        self.assertIsNone(session._pasteboard_publishing_read_id)

    async def test_low_usage_is_sampled_periodically_without_restarting(self):
        session=self.session()
        session._restart_failed_pasteboard=AsyncMock()
        with patch.object(bridge,'pasteboard_resource_usage',AsyncMock(return_value=(123,50))) as inspect:
            await session._maintain_pasteboard_resources()
            await session._maintain_pasteboard_resources()
        inspect.assert_awaited_once()
        session._restart_failed_pasteboard.assert_not_awaited()

    async def test_queued_writer_owns_resource_maintenance(self):
        session=self.session()
        session._pasteboard_writers_waiting=1
        session._restart_failed_pasteboard=AsyncMock(return_value=True)
        with patch.object(bridge,'pasteboard_resource_usage',AsyncMock(return_value=(123,200))) as inspect:
            await session._maintain_pasteboard_resources()
            inspect.assert_not_awaited()
            await session._maintain_pasteboard_resources(for_write=True)
        session._restart_failed_pasteboard.assert_awaited_once_with(for_write=True,resource_pid=123)

    async def test_helper_replacement_during_check_is_never_signalled(self):
        session=self.session()
        process={'pid':124,'name':'dtpasteboardd','isApplication':False,'realAppName':recovery.HELPER_PATH}
        info=SimpleNamespace(invoke=AsyncMock(return_value=[process]))
        control=SimpleNamespace(invoke=AsyncMock())
        hub=SimpleNamespace(__aenter__=AsyncMock(),__aexit__=AsyncMock(),
            dtx=SimpleNamespace(open_channel=AsyncMock(side_effect=[info,control])))
        with patch.object(bridge,'PasteboardRecoveryHub',return_value=hub):
            self.assertFalse(await session._restart_failed_pasteboard(resource_pid=123))
        control.invoke.assert_not_awaited()

    async def test_inspection_uses_exact_helper_pid_and_closes_channels(self):
        process={'pid':123,'isApplication':False,'realAppName':recovery.HELPER_PATH}
        info=SimpleNamespace(invoke=AsyncMock(return_value=[process]))
        hub=SimpleNamespace(__aenter__=AsyncMock(),__aexit__=AsyncMock(),
            dtx=SimpleNamespace(open_channel=AsyncMock(return_value=info)))
        async def rows():
            yield [{'pid':999,'nfiles':256},{'pid':123,'nfiles':100}]
            await asyncio.Event().wait()
        monitor=SimpleNamespace(__aenter__=AsyncMock(),__aexit__=AsyncMock(),iter_processes=rows,_config={})
        attributes=MagicMock(__aenter__=AsyncMock(),__aexit__=AsyncMock(),
            sysmon_process_attributes=AsyncMock(return_value=['pid','nfiles','cpuUsage']),
            sysmon_system_attributes=AsyncMock(return_value=['physMemSize']))
        attributes.__aenter__.return_value=attributes
        with patch.object(recovery,'DeviceInfo',return_value=attributes), \
             patch.object(recovery,'Sysmontap',return_value=monitor) as factory:
            self.assertEqual((123,100),await recovery.pasteboard_resource_usage(object(),Mock(return_value=hub)))
        self.assertEqual(['pid','nfiles'],factory.call_args.args[1])
        self.assertEqual(['physMemSize'],factory.call_args.args[2])
        monitor.__aexit__.assert_awaited_once()
        hub.__aexit__.assert_awaited_once()

if __name__=='__main__':unittest.main()
