"""Skip failed rich-format converters owned by the developer pasteboard helper.

The original DDI resolves all advertised representations before replying, even
when the host needs only text. On affected iOS versions its attributed-string
agent cannot launch WebKit and waits a minute per conversion. A live failure
event plus exact process/coalition checks allow that failed work to end early.
"""
from __future__ import annotations

import asyncio
import logging
import math
import plistlib
import struct
import time
from datetime import datetime
from typing import Callable

from pymobiledevice3.services.dvt.instruments.sysmontap import Sysmontap
from pymobiledevice3.services.dvt.instruments.device_info import DeviceInfo
from pymobiledevice3.services.os_trace import (
    OsTraceService, OsActivityStreamFlag, parse_syslog_entry,
    OS_TRACE_RELAY_MESSAGE_FILTER_ALL,
)


log = logging.getLogger('iphoneMirror.usb_touch')
HELPER_PATH = '/System/Developer/usr/libexec/dtpasteboardd'
CONVERTER_PATH = ('/System/Library/PrivateFrameworks/UIFoundation.framework/'
                  'XPCServices/nsattributedstringagent.xpc/nsattributedstringagent')
SETUP_TIMEOUT_SECONDS = 4.0
CLEANUP_TIMEOUT_SECONDS = 1.0
MAX_CANCELLED_CONVERTERS = 4


def _process(process, path):
    return (isinstance(process, dict) and process.get('realAppName') == path and
            process.get('isApplication') is False and
            type(process.get('pid')) is int and process['pid'] > 1)


def _webcontent_launch_denied(entry):
    # No clipboard content or unrelated device logs are retained or logged.
    message = getattr(entry, 'message', '')
    return (getattr(entry, 'filename', None) == CONVERTER_PATH and
            isinstance(message, str) and
            'Failed to create extensionProcess' in message and
            'com.apple.WebKit.WebContent' in message and
            'Operation not permitted' in message and
            'Launchd job spawn failed' in message)


def _start_time(process):
    value = process.get('startDate')
    if isinstance(value, datetime):
        return value.timestamp()
    timestamp = getattr(value, 'timestamp', None)
    if type(timestamp) in (int, float) and math.isfinite(timestamp):
        return timestamp
    return None


def _sample_identity(process):
    fields = tuple(process.get(key) for key in ('pid', 'uniqueID', 'startAbsTime'))
    return fields if all(type(value) is int and value > 0 for value in fields) else None


class PasteboardResourceMonitor:
    """One small sampler belongs to one RSD lifetime, alongside HID.

    Repeatedly starting/stopping full-device sysmon streams can stall the
    shared userspace tunnel and lose button/keyboard input. Keep a single
    stream, request only resource/identity fields, and continuously drain it.
    """
    SAMPLE_INTERVAL_MS = 500
    MAX_SAMPLE_AGE_SECONDS = 2.0
    MAX_CACHED_PROCESSES = 8192

    def __init__(self):
        self._lock = asyncio.Lock()
        self._rsd = self._hub = self._monitor = self._info = self._reader = None
        self._attempted_rsd = None
        self._helper = self._helper_identity = None
        self._rows = {}
        self._sample_at = 0.0
        self._sample_ready = asyncio.Event()

    async def _find_helper(self):
        helpers = [p for p in await self._info.invoke('runningProcesses') if _process(p, HELPER_PATH)]
        self._helper = helpers[0] if len(helpers) == 1 else None
        self._helper_identity = None

    async def _start(self, rsd, hub_factory):
        self._rsd = rsd
        self._hub = hub_factory(rsd)
        await self._hub.__aenter__()
        self._info = await self._hub.dtx.open_channel('com.apple.instruments.server.services.deviceinfo')
        await self._find_helper()
        async with DeviceInfo(self._hub) as info:
            process = list(await info.sysmon_process_attributes())
            system = list(await info.sysmon_system_attributes())
        if not {'pid', 'nfiles'}.issubset(process) or not system:
            raise RuntimeError('Required pasteboard resource attributes are unavailable')
        fields = [key for key in process if key in ('pid', 'nfiles', 'uniqueID', 'startAbsTime')]
        # Keep the advertised system schema: an empty sysAttrs array causes
        # older Instruments servers to close the channel instead of sampling.
        self._monitor = Sysmontap(self._hub, fields, system, interval_ms=self.SAMPLE_INTERVAL_MS)
        self._monitor._config['ur'] = self.SAMPLE_INTERVAL_MS
        await self._monitor.__aenter__()
        self._reader = asyncio.create_task(self._collect(self._monitor), name='pasteboard-resource-sampler')

    async def _collect(self, monitor):
        async for rows in monitor.iter_processes():
            # Sysmon emits deltas, including empty heartbeat frames when the
            # requested fields did not change. Keep unchanged process rows;
            # replacing the table would force a global process enumeration on
            # every later check and again load the shared input tunnel.
            for row in rows:
                pid = row.get('pid')
                if type(pid) is int and pid > 1:
                    self._rows[pid] = row
            while len(self._rows) > self.MAX_CACHED_PROCESSES:
                self._rows.pop(next(iter(self._rows)))
            self._sample_at = time.monotonic()
            self._sample_ready.set()
        raise ConnectionError('Pasteboard resource sampler stopped')

    async def read(self, rsd, hub_factory, *, timeout_seconds=None):
        async with self._lock:
            try:
                async with asyncio.timeout(SETUP_TIMEOUT_SECONDS if timeout_seconds is None else timeout_seconds):
                    if self._rsd is not rsd or (self._reader is not None and self._reader.done()):
                        attempted = self._attempted_rsd is rsd
                        await self._close_locked()
                        if attempted:
                            # Cold Instruments setup belongs before input is
                            # ready. An optional failed sampler must not start
                            # another bulk setup behind an active HID channel.
                            return None
                        self._attempted_rsd = rsd
                        await self._start(rsd, hub_factory)
                    if (not self._sample_ready.is_set() or
                            time.monotonic() - self._sample_at > self.MAX_SAMPLE_AGE_SECONDS):
                        self._sample_ready.clear()
                        await self._sample_ready.wait()
                    # Delta frames omit exited processes too. Check the live
                    # executable with a small targeted RPC before reusing a
                    # cached row; PID reuse must never authorize retirement of
                    # an unrelated process.
                    if (self._helper is None or await self._info.invoke(
                            'execnameForPid:', self._helper['pid']) != HELPER_PATH):
                        await self._find_helper()
                    matches = [self._rows[self._helper['pid']]] if (
                        self._helper is not None and self._helper['pid'] in self._rows) else []
                    identity = _sample_identity(matches[0]) if len(matches) == 1 else None
                    if (len(matches) != 1 or (self._helper_identity is not None and
                                              identity != self._helper_identity)):
                        # A retired/replaced helper keeps the same sampler.
                        # Verify the new executable before trusting its row.
                        await self._find_helper()
                        matches = [self._rows[self._helper['pid']]] if (
                            self._helper is not None and self._helper['pid'] in self._rows) else []
                        identity = _sample_identity(matches[0]) if len(matches) == 1 else None
                    if len(matches) != 1 or type(matches[0].get('nfiles')) is not int:
                        return None
                    self._helper_identity = identity
                    return self._helper['pid'], matches[0]['nfiles']
            except asyncio.CancelledError:
                await self._close_locked()
                raise
            except Exception as error:
                log.info('pasteboard_resource_check_unavailable error=%s', type(error).__name__)
                await self._close_locked()
                return None

    async def _close_locked(self):
        reader, self._reader = self._reader, None
        monitor, self._monitor = self._monitor, None
        hub, self._hub = self._hub, None
        self._rsd = self._info = self._helper = self._helper_identity = None
        self._rows = {}
        self._sample_at = 0.0
        self._sample_ready.clear()
        if reader is not None:
            reader.cancel()
            await asyncio.gather(reader, return_exceptions=True)
        for service in (monitor, hub):
            if service is not None:
                try:
                    await asyncio.wait_for(service.__aexit__(None, None, None), CLEANUP_TIMEOUT_SECONDS)
                except Exception:
                    pass

    async def close(self):
        async with self._lock:
            await self._close_locked()
            self._attempted_rsd = None


async def pasteboard_resource_usage(rsd, hub_factory, monitor=None):
    """Read the helper's file-table usage; control sessions retain their monitor."""
    owned = monitor is None
    monitor = monitor if monitor is not None else PasteboardResourceMonitor()
    try:
        return await monitor.read(rsd, hub_factory)
    finally:
        if owned:
            await monitor.close()


class PasteboardConverterRecovery:
    def __init__(self, rsd, hub_factory, active: Callable[[], bool]):
        self.rsd = rsd
        self.hub_factory = hub_factory
        self.active = active
        self.hub = None
        self.sysmon = None
        self.logs = None
        self.tasks = []
        self.helper = None
        self.info = None
        self.control = None
        self.preexisting = {}
        self.failed = set()
        self.cancelled = set()
        self.helper_identity = None
        self.identities = {}
        self.started = False
        self.logs_ready = asyncio.Event()

    async def prepare(self, *, on_log_ready=None):
        """Best-effort setup; a refused diagnostic service never loses text."""
        try:
            async with asyncio.timeout(SETUP_TIMEOUT_SECONDS):
                self.logs = OsTraceService(self.rsd)
                await self.logs.__aenter__()
                self.tasks = [asyncio.create_task(self._read_failures())]
                await self.logs_ready.wait()
                if on_log_ready is not None:
                    on_log_ready()
                self.hub = self.hub_factory(self.rsd)
                await self.hub.__aenter__()
                self.info = await self.hub.dtx.open_channel('com.apple.instruments.server.services.deviceinfo')
                processes = await self.info.invoke('runningProcesses')
                helpers = [p for p in processes if _process(p, HELPER_PATH)]
                if len(helpers) != 1 or _start_time(helpers[0]) is None:
                    await self.close()
                    return False
                self.helper = helpers[0]
                self.preexisting = {p['pid']: _start_time(p) for p in processes if _process(p, CONVERTER_PATH)}
                self.control = await self.hub.dtx.open_channel('com.apple.instruments.server.services.processcontrol')
                self.sysmon = await Sysmontap.create(self.hub, interval=100)
                await self.sysmon.__aenter__()
                self.tasks.append(asyncio.create_task(self._monitor_converters()))
            if any(task.done() for task in self.tasks):
                await self.close()
                return False
            log.info('pasteboard_converter_recovery_ready helper=%s existing_converters=%s',
                     self.helper['pid'], len(self.preexisting))
            return True
        except asyncio.CancelledError:
            await self.close()
            raise
        except Exception as error:
            log.info('pasteboard_converter_recovery_unavailable error=%s', type(error).__name__)
            await self.close()
            return False

    async def _read_failures(self):
        async for entry in self._live_syslog():
            if not self.active():
                return
            if (self.started and _webcontent_launch_denied(entry) and
                    type(entry.pid) is int and entry.pid > 1 and
                    len(self.failed) < 32):
                self.failed.add(entry.pid)
                log.info('pasteboard_converter_launch_failed pid=%s', entry.pid)

    async def _live_syslog(self):
        # HISTORICAL is deliberately absent: only failures observed during our
        # live read can qualify. The raw message is never stored or forwarded.
        flags = (OsActivityStreamFlag.PAYLOAD | OsActivityStreamFlag.DEBUG |
                 OsActivityStreamFlag.INFO | OsActivityStreamFlag.NO_SENSITIVE)
        connection = self.logs.service
        # The SDK generator only yields after the first log. A quiet phone is
        # already ready once StartActivity is acknowledged; waiting for a log
        # could otherwise disable recovery and restore the minute-long delay.
        await connection.send_plist({'Request': 'StartActivity',
            'MessageFilter': OS_TRACE_RELAY_MESSAGE_FILTER_ALL, 'Pid': -1,
            'StreamFlags': flags})
        length_bytes, = struct.unpack('<I', await connection.recvall(4))
        if not 1 <= length_bytes <= 8:
            raise ValueError('invalid log start header')
        length = int.from_bytes(await connection.recvall(length_bytes), 'little')
        if not 1 <= length <= 1024 * 1024:
            raise ValueError('invalid log start response size')
        response = plistlib.loads(await connection.recvall(length))
        if response.get('Status') != 'RequestSuccessful':
            raise RuntimeError('device rejected log monitoring')
        self.logs_ready.set()
        while True:
            if await connection.recvall(1) != b'\x02':
                raise ValueError('invalid log record header')
            length, = struct.unpack('<I', await connection.recvall(4))
            if not 1 <= length <= 16 * 1024 * 1024:
                raise ValueError('invalid log record size')
            yield parse_syslog_entry(await connection.recvall(length))

    async def _monitor_converters(self):
        async for entries in self.sysmon.iter_processes():
            if not self.active():
                return
            if self.started:
                await self._recover_sample(entries)

    async def _recover_sample(self, entries):
        if (not self.active() or not self.started or
                len(self.cancelled) >= MAX_CANCELLED_CONVERTERS):
            return
        owners = [p for p in entries if p.get('pid') == self.helper['pid']]
        if len(owners) != 1:
            return
        coalition = owners[0].get('coalitionID')
        if type(coalition) is not int or coalition <= 0:
            return
        owner_identity = _sample_identity(owners[0])
        if owner_identity is not None:
            if self.helper_identity is None:
                self.helper_identity = owner_identity
            elif owner_identity != self.helper_identity:
                return
        candidates = {p.get('pid') for p in entries if
                      p.get('coalitionID') == coalition and
                      type(p.get('pid')) is int and p['pid'] in self.failed and
                      p['pid'] not in self.cancelled}
        for pid in candidates:
            rows = [p for p in entries if p.get('pid') == pid]
            identity = _sample_identity(rows[0]) if len(rows) == 1 else None
            fast_verified = False
            if owner_identity is not None and identity is not None:
                # Kernel uniqueID/startAbsTime identify the actual sampled
                # process incarnation. Avoid repeatedly enumerating every
                # process (which can block the DTX receiver for over a second).
                previous_identity = self.identities.setdefault(pid, identity)
                if previous_identity != identity:
                    continue
                try:
                    paths = await asyncio.gather(
                        self.info.invoke('execnameForPid:', self.helper['pid']),
                        self.info.invoke('execnameForPid:', pid))
                    if paths == [HELPER_PATH, CONVERTER_PATH]:
                        fast_verified = True
                    elif any(isinstance(path, str) for path in paths):
                        continue
                except Exception:
                    pass  # Older services retain the full process-list check.
            # Recheck live executable identity and helper incarnation after the
            # sample. PIDs/names alone must never authorize a signal.
            if not fast_verified:
                processes = await self.info.invoke('runningProcesses')
                helpers = [p for p in processes if _process(p, HELPER_PATH)]
                converters = [p for p in processes if _process(p, CONVERTER_PATH) and p['pid'] == pid]
                if (len(helpers) != 1 or helpers[0]['pid'] != self.helper['pid'] or
                        _start_time(helpers[0]) != _start_time(self.helper) or len(converters) != 1):
                    return
                # A reused agent may be the one that reports this read's failure.
                # Accept it only for the same process incarnation.
                started = _start_time(converters[0])
                if (started is None or started < _start_time(self.helper) or
                        (pid in self.preexisting and started != self.preexisting[pid])):
                    continue
            if not self.active() or not self.started or len(self.cancelled) >= MAX_CANCELLED_CONVERTERS:
                return
            await self.control.invoke('sendSignal:toPid:', 9, pid)
            self.cancelled.add(pid)
            log.info('pasteboard_failed_converter_cancelled pid=%s helper=%s', pid, self.helper['pid'])

    async def close(self):
        self.started = False
        for task in self.tasks:
            task.cancel()
        if self.tasks:
            results = await asyncio.gather(*self.tasks, return_exceptions=True)
            for result in results:
                if isinstance(result, Exception):
                    log.info('pasteboard_converter_monitor_stopped error=%s', type(result).__name__)
        self.tasks = []
        for name in ('logs', 'sysmon', 'hub'):
            service = getattr(self, name)
            setattr(self, name, None)
            if service is not None:
                try:
                    await asyncio.wait_for(service.__aexit__(None, None, None), CLEANUP_TIMEOUT_SECONDS)
                except Exception as error:
                    log.debug('pasteboard_converter_cleanup service=%s error=%s', name, type(error).__name__)
