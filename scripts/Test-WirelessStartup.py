"""Opt-in real-device startup/HID release/stop test; never changes DDI or network.

Run with the bridge venv. Select --device explicitly; logs omit its identifier.
The shared exclusive lock coordinates other device tests in this workspace.
"""
import argparse
import asyncio
import hashlib
import importlib
import json
import logging
import os
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


async def run(args):
    sys.path.insert(0, str(ROOT / 'tools'))
    sys.path.insert(0, str(Path(args.source_root).resolve()))
    b = importlib.import_module('usb_touch_bridge')
    report = {'mode': args.mode, 'source_sha256': hashlib.sha256(Path(b.__file__).read_bytes()).hexdigest(),
              'passes': [], 'ddi_changes_allowed': False, 'network_changes_allowed': False}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    class Ipc:
        def __init__(self):
            self.messages = asyncio.Queue()
            self.ready = asyncio.Event()
            self.ack = asyncio.Event()
            self.started = time.monotonic()
            self.events = []
        async def emit(self, event):
            # Status messages and clipboard payloads can contain private data.
            row = {'seconds': round(time.monotonic() - self.started, 3),
                   'event': event.get('event'), 'code': event.get('code')}
            if row['event'] in ('status', 'ready', 'error', 'warning'):
                self.events.append(row)
            if row['event'] == 'ready': self.ready.set()
            if row['code'] == 'input_verified': self.ack.set()
        async def read_messages(self):
            while True:
                frame = await self.messages.get()
                if frame is None: return
                yield frame
    async def forbid_ddi(*_a, **_k):
        raise b.BridgePrerequisiteError('live_test_ddi_change_forbidden',
                                       'This test preserves the currently mounted developer image.')
    for number in range(1, args.passes + 1):
        ipc = Ipc()
        session = b.TouchSession(ipc, 120, args.device, 'wireless')
        for method in ('_prepare_ddi_for_remote_pairing', '_mount_ddi_candidate',
                       '_mount_personalized_ddi', '_refresh_personalized_ddi',
                       '_prepare_cryptex_developer_image'):
            setattr(session, method, forbid_ddi)
        operation = session.connect if args.mode == 'auto' else session._connect_via_remote_pairing
        task = asyncio.create_task(operation())
        waiter = asyncio.create_task(ipc.ready.wait())
        row = {'pass': number, 'success': False}
        try:
            done, _ = await asyncio.wait((task, waiter), timeout=90, return_when=asyncio.FIRST_COMPLETED)
            if task in done: await task
            if not ipc.ready.is_set(): raise TimeoutError('Real device did not become ready')
            row['ready_seconds'] = round(time.monotonic() - ipc.started, 3)
            row['auth_mode'] = session.auth_mode
            row['acknowledgements'] = 0
            for seq in range(1, 4):
                ipc.ack.clear()
                await ipc.messages.put({'schema': b.MESSAGE_SCHEMA, 'kind': b.KEYBOARD_MESSAGE_KIND,
                    'generation': session._generation, 'seq': seq, 'usages': [], 'releaseAll': True})
                await asyncio.wait_for(ipc.ack.wait(), 10)
                row['acknowledgements'] += 1
                await asyncio.sleep(.25)
            await asyncio.sleep(args.hold)
            if task.done() or not session._session_ready.is_set():
                if task.done(): await task
                raise RuntimeError('Control session did not remain ready')
            stopping = time.monotonic()
            await ipc.messages.put(None)
            await asyncio.wait_for(task, 15)
            row['stop_seconds'] = round(time.monotonic() - stopping, 3)
            row['success'] = True
        except BaseException as error:
            row['error_type'] = type(error).__name__
            row['error_code'] = getattr(error, 'code', None)
            if isinstance(error, asyncio.CancelledError): raise
        finally:
            task.cancel()
            waiter.cancel()
            await asyncio.gather(task, waiter, return_exceptions=True)
            row['events'] = ipc.events
            report['passes'].append(row)
            output.write_text(json.dumps(report, indent=2), encoding='utf-8')
            print(json.dumps({k: v for k, v in row.items() if k != 'events'}), flush=True)
        if not row['success']: return 1
        await asyncio.sleep(1)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--device', required=True)
    parser.add_argument('--mode', choices=('auto', 'remote'), default='auto')
    parser.add_argument('--passes', type=int, default=3)
    parser.add_argument('--hold', type=float, default=2)
    parser.add_argument('--source-root', default=str(ROOT / 'tools'))
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if not 1 <= args.passes <= 10 or not 0 <= args.hold <= 60:
        parser.error('passes must be 1..10 and hold 0..60 seconds')
    lock = ROOT / 'work/device-hardware-test.lock'
    lock.parent.mkdir(exist_ok=True)
    try:
        handle = lock.open('x', encoding='utf-8')
    except FileExistsError:
        parser.error('Device test is occupied: work/device-hardware-test.lock')
    try:
        handle.write(f'thread=01a18719-08b4-7c90-a1be-aca87c6ce745 pid={os.getpid()} purpose=wireless-startup\n')
        handle.flush()
        logging.disable(logging.CRITICAL)
        return asyncio.run(run(args))
    finally:
        handle.close()
        lock.unlink()


if __name__ == '__main__':
    raise SystemExit(main())
