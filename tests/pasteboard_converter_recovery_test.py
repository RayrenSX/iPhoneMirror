"""A failed rich conversion can be skipped without signaling other processes."""
import asyncio
import plistlib
import struct
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import pasteboard_recovery as recovery
import usb_touch_bridge as bridge


def process(pid, path, started=1):
    return {'pid': pid, 'realAppName': path, 'isApplication': False,
            'startDate': SimpleNamespace(timestamp=started)}


class ConverterRecoveryTests(unittest.IsolatedAsyncioTestCase):
    def monitor(self):
        helper = process(100, recovery.HELPER_PATH)
        converter = process(200, recovery.CONVERTER_PATH, 2)
        monitor = recovery.PasteboardConverterRecovery(object(), Mock(), lambda: True)
        monitor.helper = helper
        monitor.started = True
        monitor.failed = {200}
        monitor.info = SimpleNamespace(invoke=AsyncMock(return_value=[helper, converter]))
        monitor.control = SimpleNamespace(invoke=AsyncMock())
        return monitor

    @staticmethod
    def sample(coalition=700):
        return [{'pid': 100, 'coalitionID': 700}, {'pid': 200, 'coalitionID': coalition}]

    async def test_signals_only_verified_failed_converter_and_once(self):
        monitor = self.monitor()
        # Each device query returns new NSDate objects; compare their value.
        monitor.info.invoke.return_value[0] = process(100, recovery.HELPER_PATH)
        await monitor._recover_sample(self.sample())
        await monitor._recover_sample(self.sample())
        monitor.control.invoke.assert_awaited_once_with('sendSignal:toPid:', 9, 200)
        self.assertEqual({200}, monitor.cancelled)

    async def test_missing_failure_existing_agent_or_other_coalition_never_signaled(self):
        for mode in ('no_failure', 'foreign', 'no_coalition', 'ambiguous_owner', 'inactive', 'not_started', 'limit'):
            with self.subTest(mode=mode):
                monitor = self.monitor()
                entries = self.sample()
                if mode == 'no_failure': monitor.failed.clear()
                if mode == 'foreign': entries[1]['coalitionID'] = 999
                if mode == 'no_coalition': entries[0].pop('coalitionID')
                if mode == 'ambiguous_owner': entries.append(entries[0])
                if mode == 'inactive': monitor.active = lambda: False
                if mode == 'not_started': monitor.started = False
                if mode == 'limit': monitor.cancelled = set(range(recovery.MAX_CANCELLED_CONVERTERS))
                await monitor._recover_sample(entries)
                monitor.control.invoke.assert_not_awaited()

    async def test_reused_agent_requires_live_failure_and_matching_start_time(self):
        for failure, started, should_signal in ((True, 2, True), (False, 2, False), (True, 3, False)):
            monitor = self.monitor()
            monitor.preexisting = {200: started}
            if not failure: monitor.failed.clear()
            await monitor._recover_sample(self.sample())
            self.assertEqual(1 if should_signal else 0, monitor.control.invoke.await_count)

    async def test_kernel_identity_and_exact_paths_can_verify_without_global_enumeration(self):
        monitor = self.monitor()
        rows = self.sample()
        for row in rows: row.update(uniqueID=row['pid'],startAbsTime=row['pid']*100)
        monitor.info.invoke.side_effect = [recovery.HELPER_PATH,recovery.CONVERTER_PATH]
        await monitor._recover_sample(rows)
        monitor.control.invoke.assert_awaited_once_with('sendSignal:toPid:',9,200)
        self.assertEqual(['execnameForPid:','execnameForPid:'],
                         [call.args[0] for call in monitor.info.invoke.await_args_list])

    async def test_kernel_identity_change_or_wrong_executable_never_signals(self):
        for mode in ('owner_reused','converter_reused','wrong_path','cancelled_during_lookup'):
            monitor = self.monitor()
            rows = self.sample()
            for row in rows: row.update(uniqueID=row['pid'],startAbsTime=row['pid']*100)
            if mode == 'owner_reused': monitor.helper_identity=(100,100,1)
            if mode == 'converter_reused': monitor.identities[200]=(200,200,1)
            if mode == 'wrong_path': monitor.info.invoke.side_effect=[recovery.HELPER_PATH,'/Applications/Other']
            if mode == 'cancelled_during_lookup':
                async def lookup(selector,pid):
                    monitor.active=lambda:False
                    return recovery.HELPER_PATH if pid==100 else recovery.CONVERTER_PATH
                monitor.info.invoke.side_effect=lookup
            await monitor._recover_sample(rows)
            monitor.control.invoke.assert_not_awaited()

    async def test_rechecks_process_path_application_flag_pid_and_helper_incarnation(self):
        for mode in ('path', 'application', 'missing', 'helper_restarted', 'helper_missing', 'cancelled_during_query'):
            with self.subTest(mode=mode):
                monitor = self.monitor()
                processes = monitor.info.invoke.return_value
                if mode == 'path': processes[1]['realAppName'] = '/Applications/Example.app/nsattributedstringagent'
                if mode == 'application': processes[1]['isApplication'] = True
                if mode == 'missing': processes.pop()
                if mode == 'helper_restarted': processes[0] = process(100, recovery.HELPER_PATH, 2)
                if mode == 'helper_missing': processes.pop(0)
                if mode == 'cancelled_during_query':
                    async def changed(*args):
                        monitor.active = lambda: False
                        return processes
                    monitor.info.invoke.side_effect = changed
                await monitor._recover_sample(self.sample())
                monitor.control.invoke.assert_not_awaited()

    def test_accepts_only_explicit_failed_webcontent_launch_from_converter(self):
        message = ("Failed to create extensionProcess for extension 'com.apple.WebKit.WebContent' "
                   'error: Operation not permitted; Launchd job spawn failed')
        valid = SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message)
        self.assertTrue(recovery._webcontent_launch_denied(valid))
        for entry in (SimpleNamespace(filename='/Applications/Other', message=message),
                      SimpleNamespace(filename=recovery.CONVERTER_PATH, message='WebKit slow'),
                      SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message.replace('Operation not permitted', 'Waiting')),
                      SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message.replace('com.apple.WebKit.WebContent', 'Other'))):
            self.assertFalse(recovery._webcontent_launch_denied(entry))

    async def test_setup_failure_closes_partial_resources(self):
        hub = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()), __aexit__=AsyncMock())
        monitor = recovery.PasteboardConverterRecovery(object(), Mock(return_value=hub), lambda: True)
        logs = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock())
        async def records():
            monitor.logs_ready.set()
            await asyncio.Event().wait()
            yield
        monitor._live_syslog = records
        with patch.object(recovery,'OsTraceService',return_value=logs):
            self.assertFalse(await monitor.prepare())
        hub.__aexit__.assert_awaited_once()
        logs.__aexit__.assert_awaited_once()
        self.assertIsNone(monitor.hub)

    async def test_monitor_uses_only_live_logs_and_does_not_arm_before_read(self):
        monitor = self.monitor()
        monitor.started = False
        message = ("Failed to create extensionProcess for extension 'com.apple.WebKit.WebContent' "
                   'Operation not permitted; Launchd job spawn failed')
        async def entries():
            yield SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message, pid=201)
            monitor.started = True
            monitor.preexisting[202] = 2
            yield SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message, pid=202)
            yield SimpleNamespace(filename=recovery.CONVERTER_PATH, message=message, pid=203)
        monitor._live_syslog = entries
        await monitor._read_failures()
        self.assertNotIn(201, monitor.failed)
        self.assertIn(202, monitor.failed)
        self.assertIn(203, monitor.failed)

    async def test_log_monitor_acknowledges_readiness_without_waiting_for_any_log(self):
        reply = plistlib.dumps({'Status': 'RequestSuccessful'})
        pending = bytearray(struct.pack('<I',4)+struct.pack('<I',len(reply))+reply)
        async def receive(size):
            if not pending: await asyncio.Event().wait()
            result = bytes(pending[:size]); del pending[:size]
            return result
        connection = SimpleNamespace(send_plist=AsyncMock(), recvall=receive)
        monitor = self.monitor()
        monitor.logs = SimpleNamespace(service=connection)
        task = asyncio.create_task(monitor._read_failures())
        try:
            await asyncio.wait_for(monitor.logs_ready.wait(),0.1)
            self.assertFalse(task.done())
            flags = connection.send_plist.call_args.args[0]['StreamFlags']
            self.assertFalse(flags & recovery.OsActivityStreamFlag.HISTORICAL)
            self.assertEqual(-1,connection.send_plist.call_args.args[0]['Pid'])
        finally:
            task.cancel()
            await asyncio.gather(task,return_exceptions=True)

    async def test_cancelling_full_read_closes_recovery_before_writer_proceeds(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = object()
        entered = asyncio.Event()
        monitor = SimpleNamespace(prepare=AsyncMock(), close=AsyncMock(), started=False)
        async def blocked(**kwargs):
            entered.set()
            await asyncio.Event().wait()
        pb = SimpleNamespace(get=blocked)
        with patch.object(bridge, 'PasteboardConverterRecovery', return_value=monitor) as constructor:
            task = asyncio.create_task(session._resolve_device_pasteboard(pb))
            session._pasteboard_full_read_task = task
            await entered.wait()
            self.assertTrue(constructor.call_args.args[2]())
            session._pasteboard_writers_waiting = 1
            self.assertFalse(constructor.call_args.args[2]())
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        monitor.close.assert_awaited_once()
        self.assertTrue(session._pasteboard_full_read_interrupted)
        self.assertEqual(1, session._pasteboard_full_read_serial)

    async def test_read_starts_after_live_log_ack_while_process_monitor_is_preparing(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()),120)
        session.rsd=object()
        entered=asyncio.Event()
        finish_setup=asyncio.Event()
        events=[]
        async def prepare(*,on_log_ready):
            events.append('live_logs_acknowledged')
            on_log_ready()
            await entered.wait()
            await finish_setup.wait()
            events.append('process_monitor_ready')
            return True
        async def get(**kwargs):
            events.append('read_started')
            entered.set()
            return {'items':[]}
        monitor=SimpleNamespace(prepare=prepare,close=AsyncMock(),started=False,cancelled=set())
        with patch.object(bridge,'PasteboardConverterRecovery',return_value=monitor):
            task=asyncio.create_task(session._resolve_device_pasteboard(SimpleNamespace(get=get)))
            session._pasteboard_full_read_task=task
            await asyncio.wait_for(entered.wait(),0.1)
            self.assertFalse(task.done())
            finish_setup.set()
            self.assertEqual({'items':[]},await task)
        self.assertEqual(['live_logs_acknowledged','read_started','process_monitor_ready'],events)
        monitor.close.assert_awaited_once()

    async def test_cleanup_retires_abandoned_read_before_closing_device_connection(self):
        for interrupted in (False, True):
            session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
            session.rsd = object()
            session._pasteboard_full_read_interrupted = interrupted
            order = []
            async def restart(): order.append('restart');return True
            async def close(service, name): order.append(name)
            session._restart_failed_pasteboard = AsyncMock(side_effect=restart)
            session._close_remote_service = AsyncMock(side_effect=close)
            await session._cleanup()
            self.assertEqual(1 if interrupted else 0, session._restart_failed_pasteboard.await_count)
            if interrupted: self.assertLess(order.index('restart'), order.index('rsd'))

    async def test_reconnected_reader_retires_abandoned_read_before_request(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = object()
        session._pasteboard_full_read_interrupted = True
        events = []
        async def restart():
            self.assertFalse(session._pasteboard_lock.locked())
            events.append('restart')
            session._pasteboard_full_read_interrupted = False
            return True
        async def get_text():
            events.append('read')
            return 'recovered'
        session._restart_failed_pasteboard = restart
        session._get_device_pasteboard = AsyncMock(return_value=SimpleNamespace(get_text=get_text))
        self.assertEqual('recovered',await session._read_device_pasteboard())
        self.assertEqual(['restart','read'],events)

    async def test_failed_retirement_never_queues_new_read_or_write_behind_abandoned_job(self):
        for operation in ('read', 'write'):
            session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
            session.rsd = object()
            session._pasteboard_full_read_interrupted = True
            session._restart_failed_pasteboard = AsyncMock(return_value=False)
            session._get_device_pasteboard = AsyncMock()
            with self.assertRaisesRegex(ConnectionError, 'recovery is pending'):
                if operation == 'read':
                    await session._read_device_pasteboard()
                else:
                    await session._write_device_pasteboard('host text')
            session._get_device_pasteboard.assert_not_awaited()
            self.assertFalse(session._pasteboard_lock.locked())
            self.assertTrue(session._pasteboard_writes_idle.is_set())


if __name__ == '__main__':
    unittest.main()
