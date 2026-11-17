"""Wire-level regressions for the dtuhidd second-request crash and attach."""
import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge
from hyperframe.frame import DataFrame, Frame
from pymobiledevice3.remote.xpc_message import (
    XpcInt64Type, XpcUInt64Type, XpcWrapper, create_xpc_wrapper, decode_xpc_object,
)


class Writer:
    def __init__(self):
        self.messages = []

    def write(self, raw):
        frame, _ = Frame.parse_frame_header(memoryview(raw[:9]))
        frame.parse_body(memoryview(raw[9:]))
        wrapper = XpcWrapper.parse(frame.data)
        self.messages.append((wrapper.message.message_id,
                              decode_xpc_object(wrapper.message.payload.obj)))

    async def drain(self):
        await asyncio.sleep(0)


def service(hid_type=bridge.UniversalHIDServiceService, responses=()):
    hid = hid_type(None)
    wire = bridge.RemoteXPCConnection(('127.0.0.1', 0))
    wire._writer = Writer()
    wire.next_message_id[1] = 1  # the empty handshake has already been sent
    responses = [{key: (XpcUInt64Type(value) if value >= 0 else XpcInt64Type(value))
                  if type(value) is int else value for key, value in response.items()}
                 for response in responses]
    # Exercise the pinned SDK's receive path, which overwrites its ID counter
    # from the peer. Our request sequence must remain independent of it.
    wire._receive_next_data_frame = AsyncMock(side_effect=[
        DataFrame(stream_id=1, data=create_xpc_wrapper(response, message_id=200 + index))
        for index, response in enumerate(responses)
    ])
    wire.close = AsyncMock()
    hid._service = wire
    return hid, wire


class HidKeyboardProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_all_requests_keep_odd_ids_across_replies_and_concurrent_sends(self):
        hid, wire = service(responses=[{'connectedServices': []}, {'connectedServices': []}])
        await hid.list_connected_services()
        await asyncio.gather(hid.send_keyboard(512, []), hid.send_report(257, b'\x09'))
        await hid.list_connected_services()
        self.assertEqual([1, 3, 5, 7], [message_id for message_id, _ in wire.writer.messages])

    async def test_pending_response_does_not_block_key_release_or_reuse_its_id(self):
        hid, wire = service()
        reading, release_reply = asyncio.Event(), asyncio.Event()

        async def receive_frame():
            reading.set()
            await release_reply.wait()
            return DataFrame(stream_id=1, data=create_xpc_wrapper({'connectedServices': []}, message_id=300))

        wire._receive_next_data_frame.side_effect = receive_frame
        inventory = asyncio.create_task(hid.list_connected_services())
        try:
            await asyncio.wait_for(reading.wait(), .5)
            await asyncio.wait_for(hid.send_keyboard(512, []), .5)
            release_reply.set()
            await asyncio.wait_for(inventory, .5)
            await hid.send_report(257, b'\x09')
        finally:
            inventory.cancel()
            await asyncio.gather(inventory, return_exceptions=True)
        self.assertEqual([1, 3, 5], [message_id for message_id, _ in wire.writer.messages])

    async def test_keyboard_descriptor_matches_the_39_byte_bitmap_report(self):
        hid, wire = service(responses=[{'serviceID': bridge.KEYBOARD_SURFACE_DEFAULT_SERVICE_ID}])
        with patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            service_id = await hid.create_keyboard_service()
        payload = wire.writer.messages[0][1]['payload']['createService']['_0']
        self.assertEqual((1, 6), (payload['PrimaryUsagePage'], payload['PrimaryUsage']))
        self.assertIsInstance(payload['PrimaryUsage'], XpcUInt64Type)
        storage = payload['_CoreDevice_codablePropertyStorage']
        self.assertIsInstance(storage['PrimaryUsage']['int'], XpcInt64Type)
        self.assertEqual({'bool': True}, storage['UniversalControlVirtualService'])
        descriptor = storage['ReportDescriptor']['data']
        report_size = report_count = report_id = 0
        bit_counts = {}
        index = 0
        while index < len(descriptor):
            prefix = descriptor[index]
            length = (0, 1, 2, 4)[prefix & 3]
            value = int.from_bytes(descriptor[index + 1:index + 1 + length], 'little')
            tag = prefix & 0xFC
            if tag == 0x74: report_size = value
            elif tag == 0x94: report_count = value
            elif tag == 0x84: report_id = value
            elif tag == 0x80:
                bit_counts[report_id] = bit_counts.get(report_id, 0) + report_size * report_count
            index += length + 1
        await hid.send_keyboard(service_id, [4, 0xE1])
        report = wire.writer.messages[-1][1]['payload']['send']['_0']
        self.assertEqual(39, len(report))
        self.assertEqual(8 + bit_counts[report[0]], len(report) * 8)
        self.assertTrue(report[1] & (1 << 4))
        self.assertTrue(report[29] & (1 << 1))

    async def test_connections_register_distinct_ids_so_old_close_cannot_remove_successor(self):
        old, old_wire = service(responses=[{'serviceID': 0x100001234}])
        new, new_wire = service(responses=[{'serviceID': 0x100005678}])
        with patch.object(bridge.os, 'urandom', side_effect=[b'\x34\x12\x00\x00', b'\x78\x56\x00\x00']), \
             patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            old_id = await old.create_keyboard_service()
            new_id = await new.create_keyboard_service()
        requested = [wire.writer.messages[0][1]['payload']['createService']['_0']['_ServiceID']
                     for wire in (old_wire, new_wire)]
        self.assertEqual([old_id, new_id], requested)
        self.assertNotEqual(old_id, new_id)
        self.assertTrue(all(value & (1 << 32) for value in requested))
        await old.close()
        await old.close()  # the media context may still retain the old wrapper
        await new.send_keyboard(new_id, [])
        self.assertEqual([old_id], [message['payload']['removeService']['_0']
                                   for _, message in old_wire.writer.messages
                                   if 'removeService' in message['payload']])
        self.assertFalse(any('removeService' in message['payload'] for _, message in new_wire.writer.messages))
        await new.close()

    async def test_missing_or_invalid_creation_ack_cannot_claim_a_keyboard(self):
        for value in (None, True, 0, -1):
            with self.subTest(value=value):
                response = {'error': 'rejected'} if value is None else {'serviceID': value}
                hid, _ = service(responses=[response])
                with self.assertRaisesRegex(ValueError, 'did not confirm'):
                    await hid.create_keyboard_service()
                self.assertIsNone(getattr(hid, '_created_keyboard_service_id', None))

    async def test_legacy_endpoint_uses_its_feature_for_registration_input_and_removal(self):
        hid, wire = service(bridge.LegacyUniversalHIDServiceService,
                            responses=[{'connectedServices': []}, {'serviceID': 0x100002001}])
        await hid.list_connected_services()
        with patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            service_id = await hid.create_keyboard_service()
        await hid.send_keyboard(service_id, [])
        await hid.close()
        for _, message in wire.writer.messages:
            self.assertEqual(bridge.LEGACY_UNIVERSAL_HID_FEATURE, message['featureIdentifier'])
        self.assertIn('removeService', wire.writer.messages[-1][1]['payload'])

    async def test_close_releases_and_removes_only_its_own_keyboard_once(self):
        hid, wire = service(responses=[{'serviceID': 0x100002001}])
        with patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            await hid.create_keyboard_service()
        await hid.close()
        await hid.close()
        payloads = [message['payload'] for _, message in wire.writer.messages]
        removals = [payload['removeService']['_0'] for payload in payloads if 'removeService' in payload]
        self.assertEqual([0x100002001], removals)
        release = payloads[-2]['send']['_0']
        self.assertEqual(b'\x01' + bytes(30), release[:31])
        self.assertEqual(2, wire.close.await_count)

    async def test_cancel_during_attach_settle_still_removes_the_created_keyboard(self):
        hid, wire = service(responses=[{'serviceID': 0x100002001}])
        settling = asyncio.Event()
        real_sleep = asyncio.sleep

        async def sleep(delay):
            if delay == .3:
                settling.set()
                await asyncio.Event().wait()
            else:
                await real_sleep(delay)

        with patch.object(bridge.asyncio, 'sleep', sleep):
            task = asyncio.create_task(hid.create_keyboard_service())
            await asyncio.wait_for(settling.wait(), .5)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        await hid.close()
        self.assertEqual(0x100002001, wire.writer.messages[-1][1]['payload']['removeService']['_0'])
        wire.close.assert_awaited_once()

    async def test_failed_key_release_still_attempts_keyboard_removal(self):
        hid, wire = service(responses=[{'serviceID': 0x100002001}])
        with patch.object(bridge.asyncio, 'sleep', AsyncMock()):
            await hid.create_keyboard_service()
        hid.send_keyboard = AsyncMock(side_effect=RuntimeError('release rejected'))
        await hid.close()
        self.assertEqual(0x100002001, wire.writer.messages[-1][1]['payload']['removeService']['_0'])
        wire.close.assert_awaited_once()


if __name__ == '__main__':
    unittest.main()
