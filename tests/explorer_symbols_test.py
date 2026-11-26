import asyncio
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from explorer_symbols import SymbolFileCatalog, SymbolFiles


class Transfer:
    def __init__(self, source):
        self.source=source
        self.offset=0
        self.closed=False
        self.service=self
        self.channels=[]
        self.frames=0
        self.block=False
    async def connect(self): pass
    async def close(self): self.closed=True
    async def get_dsc_file_list(self):
        return [SimpleNamespace(file_path='/usr/lib/dyld',file_size=len(self.source))]
    async def _open_channel(self, stream, flags): self.channels.append(stream)
    async def _receive_next_data_frame(self):
        if self.block: await asyncio.Future()
        self.frames+=1
        data=self.source[self.offset:self.offset+16384]
        self.offset+=len(data)
        return SimpleNamespace(stream_id=2,data=data,flags=set() if data else {'END_STREAM'})


class SymbolTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.source=bytes(range(256))*4096
        self.services=[]
        def factory(rsd):
            service=Transfer(self.source)
            self.services.append(service)
            return service
        self.patch=patch('explorer_symbols.RemoteFetchSymbolsService',side_effect=factory)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.catalog=SymbolFileCatalog(object())
        self.addAsyncCleanup(self.catalog.close)

    async def test_catalog_directories_read_only_and_only_advertised_files(self):
        self.assertEqual(await self.catalog.listdir('/'),['usr'])
        self.assertEqual(await self.catalog.listdir('/usr/lib'),['dyld'])
        self.assertTrue((await self.catalog.metadata('/usr/lib/dyld'))['readOnly'])
        self.assertEqual(self.services[0].channels,[])
        self.assertEqual(self.services[0].frames,0)
        with self.assertRaises(FileNotFoundError): await self.catalog.metadata('/etc/passwd')
        files=SymbolFiles(self.catalog)
        with self.assertRaises(PermissionError): await files.fopen('/usr/lib/dyld','w')

    async def test_sequential_reads_pull_only_requested_frames_and_reuse_transfer(self):
        self.assertEqual(await self.catalog.read('/usr/lib/dyld',0,64),self.source[:64])
        service=self.services[-1]
        self.assertEqual(service.frames,1)
        await asyncio.sleep(0.01)
        self.assertEqual(service.frames,1)
        self.assertEqual(await self.catalog.read('/usr/lib/dyld',64,192*1024),self.source[64:64+192*1024])
        self.assertIs(self.services[-1],service)
        self.assertLessEqual(len(self.catalog.buffer),16384)
        self.assertEqual(service.channels,[2])

    async def test_seeks_close_old_stream_and_reopen_after_idle(self):
        await self.catalog.read('/usr/lib/dyld',0,64)
        first=self.services[-1]
        self.assertEqual(await self.catalog.read('/usr/lib/dyld',128*1024,64),self.source[128*1024:128*1024+64])
        self.assertIs(first,self.services[-1])
        self.assertEqual(await self.catalog.read('/usr/lib/dyld',32,64),self.source[32:96])
        self.assertTrue(first.closed)
        self.assertIsNot(first,self.services[-1])
        await self.catalog.close()
        self.assertEqual(await self.catalog.read('/usr/lib/dyld',96,64),self.source[96:160])

    async def test_cancel_and_truncated_transfer_close_resources(self):
        await self.catalog.read('/usr/lib/dyld',0,16384)
        service=self.services[-1]
        service.block=True
        task=asyncio.create_task(self.catalog.read('/usr/lib/dyld',16384,64))
        await asyncio.sleep(0)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertTrue(service.closed)
        self.assertIsNone(self.catalog.service)
        self.assertFalse(self.catalog.reading)
        await self.catalog.read('/usr/lib/dyld',0,16384)
        self.services[-1].source=b''
        with self.assertRaises(OSError): await self.catalog.read('/usr/lib/dyld',16384,64)
        self.assertIsNone(self.catalog.service)

    async def test_changed_catalog_rejected_and_bad_paths_never_mapped(self):
        await self.catalog.listdir('/')
        await self.catalog.close()
        self.source=b'changed'
        with self.assertRaises(OSError): await self.catalog.read('/usr/lib/dyld',0,1)
        self.assertTrue(self.services[-1].closed)
        with self.assertRaises(FileNotFoundError): await self.catalog.metadata('/usr/../lib/dyld')


if __name__=='__main__': unittest.main()
