"""A control session owns one live resource sampler, including recovery/teardown."""
import asyncio
from pathlib import Path
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import pasteboard_recovery as recovery
import usb_touch_bridge as bridge


class ResourceMonitorTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.processes = [dict(pid=123, isApplication=False, realAppName=recovery.HELPER_PATH)]
        self.sample = [dict(pid=123, nfiles=40, uniqueID=1001, startAbsTime=100)]
        self.hubs = []
        self.samplers = []
        self.events = []
        def invoke(method, *args):
            if method == 'runningProcesses': return self.processes
            if method == 'execnameForPid:':
                return next((p['realAppName'] for p in self.processes if p['pid'] == args[0]), None)
            raise AssertionError(method)
        self.info = SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
        self.attributes = MagicMock()
        self.attributes.__aenter__.return_value = self.attributes
        self.attributes.sysmon_process_attributes = AsyncMock(
            return_value=['pid', 'cpuUsage', 'nfiles', 'uniqueID', 'startAbsTime', 'name'])
        self.attributes.sysmon_system_attributes = AsyncMock(return_value=['physMemSize'])
        self.owner = recovery.PasteboardResourceMonitor()
        self.addAsyncCleanup(self.owner.close)
        self.enterContext(patch.object(recovery, 'DeviceInfo', return_value=self.attributes))
        self.sampler_factory = self.enterContext(patch.object(recovery, 'Sysmontap', side_effect=self.sampler))

    def hub(self, _rsd):
        async def open_(): self.events.append('hub_open')
        async def close_(*_): self.events.append('hub_close')
        hub = SimpleNamespace(__aenter__=AsyncMock(side_effect=open_), __aexit__=AsyncMock(side_effect=close_),
                              dtx=SimpleNamespace(open_channel=AsyncMock(return_value=self.info)))
        self.hubs.append(hub)
        return hub

    def sampler(self, *_args, **_kwargs):
        queue = asyncio.Queue()
        queue.put_nowait(self.sample)
        finished = asyncio.Event()
        async def rows():
            try:
                while True:
                    value = await queue.get()
                    if isinstance(value, Exception): raise value
                    yield value
            finally:
                finished.set()
        async def close_(*_): self.events.append('sampler_close')
        sampler = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(side_effect=close_),
                                  iter_processes=rows, _config={}, queue=queue, finished=finished)
        self.samplers.append(sampler)
        return sampler

    async def read(self, rsd):
        return await recovery.pasteboard_resource_usage(rsd, self.hub, self.owner)

    async def publish(self, rows):
        await self.samplers[-1].queue.put(rows)
        await asyncio.sleep(0)

    async def test_repeated_and_concurrent_checks_reuse_one_small_sampler(self):
        rsd = object()
        self.assertEqual([(123, 40)] * 16, await asyncio.gather(*(self.read(rsd) for _ in range(16))))
        await self.publish([dict(pid=123, nfiles=201, uniqueID=1001, startAbsTime=100)])
        self.assertEqual((123, 201), await self.read(rsd))
        self.assertEqual(1, len(self.hubs))
        self.assertEqual(1, len(self.samplers))
        self.assertEqual(1, sum(call.args[0] == 'runningProcesses' for call in self.info.invoke.await_args_list))
        self.assertEqual(['pid', 'nfiles', 'uniqueID', 'startAbsTime'], self.sampler_factory.call_args.args[1])
        self.assertEqual(500, self.samplers[0]._config['ur'])
        self.samplers[0].__aexit__.assert_not_awaited()

    async def test_replacement_rsd_closes_old_sampler_and_hub_before_opening_new(self):
        await self.read(object())
        self.assertEqual((123, 40), await self.read(object()))
        self.assertEqual(['hub_open', 'sampler_close', 'hub_close', 'hub_open'], self.events)
        self.assertTrue(self.samplers[0].finished.is_set())

    async def test_empty_and_partial_delta_frames_preserve_unchanged_helper(self):
        rsd = object()
        await self.read(rsd)
        await self.publish([])
        self.assertEqual((123, 40), await self.read(rsd))
        await self.publish([dict(pid=999, nfiles=240, uniqueID=2001, startAbsTime=200)])
        self.assertEqual((123, 40), await self.read(rsd))
        self.assertEqual(1, sum(call.args[0] == 'runningProcesses' for call in self.info.invoke.await_args_list))

    async def test_close_is_idempotent_and_same_rsd_can_start_again(self):
        rsd = object()
        await self.read(rsd)
        await self.owner.close()
        await self.owner.close()
        self.assertTrue(self.samplers[0].finished.is_set())
        self.samplers[0].__aexit__.assert_awaited_once()
        self.hubs[0].__aexit__.assert_awaited_once()
        self.assertEqual((123, 40), await self.read(rsd))
        self.assertEqual(2, len(self.samplers))

    async def test_reused_pid_requires_a_new_executable_identity_check(self):
        rsd = object()
        await self.read(rsd)
        self.processes = [dict(pid=123, isApplication=False, realAppName='/Applications/Other')]
        await self.publish([dict(pid=123, nfiles=240, uniqueID=1002, startAbsTime=200)])
        self.assertIsNone(await self.read(rsd))
        self.processes = [dict(pid=124, isApplication=False, realAppName=recovery.HELPER_PATH)]
        await self.publish([dict(pid=124, nfiles=30, uniqueID=1003, startAbsTime=300)])
        self.assertEqual((124, 30), await self.read(rsd))
        self.assertEqual(1, len(self.samplers))

    async def test_dead_sampler_waits_for_new_rsd_before_another_cold_setup(self):
        rsd = object()
        await self.read(rsd)
        await self.publish(ConnectionError('diagnostic stream closed'))
        await self.samplers[0].finished.wait()
        self.sample = [dict(pid=123, nfiles=70, uniqueID=1001, startAbsTime=100)]
        self.assertIsNone(await self.read(rsd))
        self.assertIsNone(await self.read(rsd))
        self.assertEqual(1, len(self.samplers))
        self.assertEqual((123, 70), await self.read(object()))
        self.assertEqual(2, len(self.samplers))
        self.hubs[0].__aexit__.assert_awaited_once()

    async def test_stale_samples_cannot_hide_a_failed_reader(self):
        rsd = object()
        await self.read(rsd)
        with patch.object(self.owner, 'MAX_SAMPLE_AGE_SECONDS', 0), \
             patch.object(recovery, 'SETUP_TIMEOUT_SECONDS', .02):
            self.assertIsNone(await self.read(rsd))
        self.assertTrue(self.samplers[0].finished.is_set())
        self.hubs[0].__aexit__.assert_awaited_once()

    async def test_cancelling_a_wait_for_fresh_data_closes_owned_channels(self):
        rsd = object()
        await self.read(rsd)
        with patch.object(self.owner, 'MAX_SAMPLE_AGE_SECONDS', 0):
            reading = asyncio.create_task(self.read(rsd))
            await asyncio.sleep(0)
            reading.cancel()
            with self.assertRaises(asyncio.CancelledError): await reading
        self.assertTrue(self.samplers[0].finished.is_set())
        self.samplers[0].__aexit__.assert_awaited_once()
        self.hubs[0].__aexit__.assert_awaited_once()

    async def test_touch_session_closes_sampler_before_rsd(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = object()
        order = []
        async def close_resource(): order.append('resource')
        async def close_service(_service, stage): order.append(stage)
        session._pasteboard_resource_monitor = SimpleNamespace(close=close_resource)
        session._close_remote_service = close_service
        await session._cleanup()
        self.assertLess(order.index('resource'), order.index('rsd'))

    async def test_startup_prepares_resources_during_recovery_before_input_ready(self):
        for usage, expected in [((123, 40), 0), ((123, 240), -bridge.PASTEBOARD_RESOURCE_CHECK_CONNECTIONS), (None, 0)]:
            session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
            session.rsd = SimpleNamespace(start_lockdown_service=AsyncMock())
            session._recovering = True
            session._pasteboard_resource_monitor.read = AsyncMock(return_value=usage)
            await session._prepare_pasteboard_resources()
            session._pasteboard_resource_monitor.read.assert_awaited_once_with(
                session.rsd, bridge.PasteboardRecoveryHub, timeout_seconds=8.0)
            self.assertIs(session.rsd, session._pasteboard_resource_rsd)
            self.assertEqual(expected, session._pasteboard_resource_last_check)
            self.assertTrue(session._recovering)
            self.assertFalse(session._session_ready.is_set())


if __name__ == '__main__': unittest.main()
