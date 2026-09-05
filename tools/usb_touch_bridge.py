#!/usr/bin/env python3
"""
USB 直连 iPhone 触控桥接器。

完全独立运行，不需要安装其他控制软件。
基于开源 pymobiledevice3 + userspace TCP 隧道（无需管理员）。

stdin  : 4 字节 little-endian 长度 + UTF-8 JSON touch_batch
stdout : JSON-lines 生命周期事件

frame 格式:
  {"schema":"iphoneMirror.touch.v2","kind":"touch_batch","seq":N,
   "timestampNs":...,"points":[{"pointerId":1,"action":"down|move|up",
   "normalizedX":0.5,"normalizedY":0.5}, ...]}

诚实行为:
   - startmediastream 返回 9021 时，验证 Universal HID 后尝试 direct 路径；
     没有 mainTouchscreen surface 时绝不伪造触控成功。
  - 任何会话异常立即发 error 事件并退出，触点强制释放。
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
from concurrent.futures import ThreadPoolExecutor, wait
from dataclasses import dataclass, replace
import hashlib
import json
import logging
import os
from pathlib import Path
import plistlib
import shutil
import struct
import sys
import tempfile
import time
from typing import Callable, Optional
from urllib.parse import quote, urlsplit

import requests
from requests import RequestException

import pymobiledevice3.remote.tunnel_service as _ts
_ts.USE_USERSPACE_TUNNEL = True

from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.common import get_home_folder
from pymobiledevice3.exceptions import (
    AlreadyMountedError,
    BadDevError,
    ConnectionFailedError,
    ConnectionFailedToUsbmuxdError,
    ConnectionTerminatedError,
    DeveloperModeIsNotEnabledError,
    DeviceNotFoundError,
    MissingValueError,
    MuxException,
    NotMountedError,
    NotPairedError,
    RemotePairingCompletedError,
)
from pymobiledevice3.pair_records import iter_remote_paired_identifiers
from pymobiledevice3.services.mobile_image_mounter import (
    LATEST_DDI_BUILD_ID,
    PersonalizedImageMounter,
)
from pymobiledevice3.remote.common import TunnelProtocol
from pymobiledevice3.remote.module_imports import start_tunnel
from pymobiledevice3.remote.remote_service_discovery import RemoteServiceDiscoveryService
from pymobiledevice3.remote.tunnel_service import (
    CoreDeviceTunnelProxy,
    RemotePairingLockdownService,
    get_remote_pairing_tunnel_services,
)
from pymobiledevice3.remote.core_device.display_service import DisplayService
from pymobiledevice3.remote.core_device.hid_service import (
    UniversalHIDServiceService,
    IndigoHIDService,
    touch_session,
    TOUCHSCREEN_STATE_CONTACT,
    TOUCHSCREEN_STATE_RELEASE,
    DIGITIZER_SURFACE_MAIN_TOUCHSCREEN,
    KEYBOARD_SURFACE_DEFAULT_SERVICE_ID,
)
from pymobiledevice3.remote.xpc_message import XpcUInt64Type

try:
    from iostouch.qt.usb import find_devices as _find_usb_devices, get_backend as _get_usb_backend
    from iostouch.qt.usbmux_usb import UsbMuxTransport as _UsbMuxTransport
    from iostouch.qt.usbmuxd_server import UsbmuxdThread as _UsbmuxdThread
except ImportError:  # pragma: no cover - optional in minimal source environments
    _find_usb_devices = _get_usb_backend = _UsbMuxTransport = _UsbmuxdThread = None

log = logging.getLogger('iphoneMirror.usb_touch')

PROTOCOL_VERSION = 2
CAPABILITIES = ['iphoneMirror.usb_touch.v2', 'iphoneMirror.usb_keyboard.v1']
MAX_SLOTS = 5
MAX_FRAME_SIZE = 64 * 1024
MESSAGE_SCHEMA = 'iphoneMirror.touch.v2'
MESSAGE_KIND = 'touch_batch'
VALID_ACTIONS = frozenset(('down', 'move', 'up'))
KEYBOARD_MESSAGE_KIND = 'keyboard_batch'
BUTTON_MESSAGE_KIND = 'button_event'
BUTTON_STATES = frozenset(('down', 'up', 'canceled'))
LEGACY_UNIVERSAL_HID_SERVICE = 'com.apple.coredevice.hid.universalhid'
LOCKDOWN_CONNECT_ATTEMPTS = 4
LOCKDOWN_RETRY_DELAY_SECONDS = 0.35
CAPTURE_MUX_START_ATTEMPTS = 3
CAPTURE_MUX_RETRY_DELAY_SECONDS = 0.75
LOCKDOWN_RETRYABLE_ERRORS = (
    BadDevError,
    ConnectionFailedError,
    ConnectionResetError,
    ConnectionTerminatedError,
    DeviceNotFoundError,
    MuxException,
    OSError,
)
PERSONALIZED_DDI_FILES = ('Image.dmg', 'BuildManifest.plist', 'Image.trustcache')
PERSONALIZED_DDI_MOUNT_TIMEOUT_SECONDS = 180
PERSONALIZED_DDI_REMOUNT_TIMEOUT_SECONDS = 30
PERSONALIZED_DDI_INVENTORY_TIMEOUT_SECONDS = 12
PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS = 150
PERSONALIZED_DDI_DOWNLOAD_CONNECT_TIMEOUT_SECONDS = 8
PERSONALIZED_DDI_DOWNLOAD_READ_TIMEOUT_SECONDS = 30
PERSONALIZED_DDI_SOURCE_PING_WINDOW_SECONDS = 5
PERSONALIZED_DDI_SOURCE_THROUGHPUT_WINDOW_SECONDS = 12
PERSONALIZED_DDI_SOURCE_CONNECT_TIMEOUT_SECONDS = 3
PERSONALIZED_DDI_SOURCE_READ_TIMEOUT_SECONDS = 8
PERSONALIZED_DDI_MIRROR_PROBE_BYTES = 256 * 1024
PERSONALIZED_DDI_DOWNLOAD_CHUNK_BYTES = 128 * 1024
PERSONALIZED_DDI_CACHE_DIRECTORY = 'Xcode_iOS_DDI_Personalized'
PERSONALIZED_DDI_GITHUB_REPOSITORY = 'doronz88/DeveloperDiskImage'
PERSONALIZED_DDI_GITHUB_REF = os.environ.get(
    'IPHONE_MIRROR_DDI_GITHUB_REF', 'main').strip() or 'main'
PERSONALIZED_DDI_GITHUB_COMMIT_API_URL = (
    'https://api.github.com/repos/'
    f'{PERSONALIZED_DDI_GITHUB_REPOSITORY}/commits/{{ref}}'
)
PERSONALIZED_DDI_GITHUB_CONTENTS_API_URL = (
    'https://api.github.com/repos/'
    f'{PERSONALIZED_DDI_GITHUB_REPOSITORY}/contents/'
    'PersonalizedImages/Xcode_iOS_DDI_Personalized?ref={revision}'
)
PERSONALIZED_DDI_GITHUB_RAW_URL = (
    'https://raw.githubusercontent.com/'
    f'{PERSONALIZED_DDI_GITHUB_REPOSITORY}/{{revision}}/'
    'PersonalizedImages/Xcode_iOS_DDI_Personalized/{path}'
)
PERSONALIZED_DDI_GITHUB_API_URL = (
    'https://api.github.com/repos/'
    f'{PERSONALIZED_DDI_GITHUB_REPOSITORY}/git/blobs/{{blob_id}}'
)
PERSONALIZED_DDI_METADATA_FILENAME = '.iphoneMirror-ddi.json'
HID_SERVICE_REGISTRATION_ATTEMPTS = 12
HID_SERVICE_REGISTRATION_RETRY_SECONDS = 1
KEYBOARD_SURFACE_CONNECTED = 512
REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS = 30
REMOTE_PAIRING_DISCOVERY_TIMEOUT_SECONDS = 15
REMOTE_PAIRING_DISCOVERY_GRACE_SECONDS = 2


class BridgePrerequisiteError(RuntimeError):
    """An expected device prerequisite that has a stable host-facing code."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def local_personalized_ddi_bundle(ddi_dir: Path) -> tuple[Path, Path, Path]:
    """Return an explicitly supplied local Personalized DDI bundle.

    The bridge never discovers a DDI from its install directory. When this
    explicit override is absent, pymobiledevice3 prepares its cached
    Personalized DDI through its normal download and Apple personalization
    flow instead. Apple verifies the supplied local image during mount.
    """
    root = Path(ddi_dir).expanduser()
    if not root.is_dir():
        raise BridgePrerequisiteError(
            'developer_image_bundle_invalid',
            'The supplied Personalized DDI directory does not exist.',
        )
    paths = [root / name for name in PERSONALIZED_DDI_FILES]
    # Apple's repository names this file Image.dmg.trustcache while some
    # tooling exports the shorter Image.trustcache spelling. Accept both and
    # pass the actual path through to PersonalizedImageMounter.
    if not paths[2].is_file() and (root / 'Image.dmg.trustcache').is_file():
        paths[2] = root / 'Image.dmg.trustcache'
    invalid = []
    for path in paths:
        try:
            valid = path.is_file() and path.stat().st_size > 0
        except OSError:
            valid = False
        if not valid:
            invalid.append(path.name)
    if invalid:
        raise BridgePrerequisiteError(
            'developer_image_bundle_invalid',
            'The supplied Personalized DDI directory is missing non-empty files: '
            + ', '.join(invalid),
        )
    return tuple(paths)


@dataclass(frozen=True)
class PersonalizedDdiAsset:
    local_name: str
    upstream_name: str
    blob_id: str
    size: int
    sha256: Optional[str] = None
    revision: str = ''


@dataclass(frozen=True)
class PersonalizedDdiDownloadSource:
    name: str
    kind: str
    prefix: str = ''


PERSONALIZED_DDI_PINNED_BUILD_ID = '27A5228h'
PERSONALIZED_DDI_ASSETS = (
    PersonalizedDdiAsset(
        'Image.dmg', 'Image.dmg',
        'a1564ba32725672264dd35733673fbdd0c7943d9',
        15733248,
        '05fd807da5e19f030fa4941f24800c965c6c77982ab572dd5d1ef778fb69f9ca',
    ),
    PersonalizedDdiAsset(
        'BuildManifest.plist', 'BuildManifest.plist',
        'e68b3271feea8496f7fab22cae0c8cd2332a3a0a',
        801505,
        '8edd4a2f4f4ef1fbd7bfe49785d8badc673d1395d1d94d85b132ca8ab5ecaf54',
    ),
    PersonalizedDdiAsset(
        'Image.trustcache', 'Image.dmg.trustcache',
        'dcec241e1a25ee8b8d25550276dbb6d9c5fb8019',
        1895,
        '36af60889ff5a737874a26daeb8e1a0139ebfebec6ec2e4d8f6a3c1bf1dce35c',
    ),
)

# Same MoreTools snapshot used by the built-in updater. They are only used for
# transport: every downloaded DDI file has a pinned length and SHA-256 below.
PERSONALIZED_DDI_MIRROR_PREFIXES = (
    'https://gh-proxy.net/',
    'https://github.cnxiaobai.com/',
    'https://hub.gitmirror.com/',
    'https://www.5555.cab/',
    'https://git.tangbai.cc/',
    'https://gh.ddlc.top/',
    'https://ghproxy.xiaopa.cc/',
    'https://ghproxy.cfd/',
    'https://ghproxy.cc/',
    'https://ghproxy.monkeyray.net/',
    'https://cf.ghproxy.cc/',
    'https://gitproxy.mrhjx.cn/',
    'https://gh.xxooo.cf/',
    'https://github.xxlab.tech/',
    'https://ghproxy.1888866.xyz/',
    'https://github.mlmle.cn/',
    'https://fastgit.cc/',
    'https://gh.1k.ink/',
    'https://ghproxy.net/',
    'https://github.boringhex.top/',
    'https://ghfast.top/',
    'https://y.whereisdoge.work/',
    'https://ghproxy.imciel.com/',
    'https://gh.jdck.fun/',
    'https://xiaomo-station.top/',
    'https://gh.monlor.com/',
    'https://g.blfrp.cn/',
    'https://gh.con.sh/',
    'https://gh.b52m.cn/',
    'https://github.dpik.top/',
    'https://github.geekery.cn/',
    'https://gh.halonice.com/',
    'https://github.limoruirui.com/',
    'https://git.yylx.win/',
    'https://github.tbedu.top/',
    'https://ghproxy.vansour.top/',
    'https://tvv.tw/',
    'https://ghproxy.xzhouqd.com/',
    'https://github-proxy.memory-echoes.cn/',
    'https://gh.catmak.name/',
    'https://hub.ddayh.com/',
    'https://github.ruojian.space/',
    'https://ghproxy.cxkpro.top/',
    'https://ghp.keleyaa.com/',
    'https://ghf.\u65e0\u540d\u6c0f.top/',
    'https://github-proxy.lixxing.top/',
    'https://gh.padao.fun/',
    'https://gp.871201.xyz/',
    'https://gh.wsmdn.dpdns.org/',
    'https://ggg.clwap.dpdns.org/',
    'https://gh-proxy.com/',
    'https://gh.dpik.top/',
    'https://gp.zkitefly.eu.org/',
    'https://gh.bugdey.us.kg/',
    'https://code-hub-hk.freexy.top/',
    'https://github.chenc.dev/',
    'https://ghfile.geekertao.top/',
    'https://kenyu.ggff.net/',
    'https://gh.nxnow.top/',
    'https://github.bullb.net/',
    'https://gitproxy.197545.xyz/',
    'https://gitproxy.127731.xyz/',
    'https://gitproxy1.127731.xyz/',
    'https://jiashu.1win.eu.org/',
    'https://ghproxy.mf-dust.dpdns.org/',
    'https://j.1lin.dpdns.org/',
    'https://gh.jasonzeng.dev/',
    'https://proxy.baguoyuyan.com/',
    'https://github.1ms.xx.kg/',
    'https://gh.198962.xyz/',
    'https://github.880824.xyz/',
    'https://ghps.cc/',
    'https://30006000.xyz/',
    'https://github.tianrld.top/',
    'https://getgit.love8yun.eu.org/',
    'https://github.788787.xyz/',
    'https://ghm.078465.xyz/',
    'https://github-proxy.com/',
    'https://proxy.yaoyaoling.net/',
    'https://ghproxy.sakuramoe.dev/',
    'https://ghproxy.053000.xyz/',
    'https://gh.chjina.com/',
    'https://git.zeas.cc/',
    'https://ghpxy.hwinzniej.top/',
    'https://gh.echofree.xyz/',
    'https://github.zzrbk.xyz/',
    'https://git.669966.xyz/',
    'https://github.ihnic.com/',
    'https://gh.996986.xyz/',
    'https://gh.idayer.com/',
    'https://github.ednovas.xyz/',
    'https://gh.chalin.tk/',
    'https://j.1win.ggff.net/',
    'https://github.lsdfxdk.nyc.mn/',
    'https://gh.aaa.team/',
    'https://github.crdz.eu.org/',
    'https://gh.shiina-rimo.cafe/',
    'https://ghproxy.mirror.skybyte.me/',
    'https://gh.llkk.cc/',
    'https://git.40609891.xyz/',
    'https://github.oterea.top/',
    'https://gh.noki.icu/',
    'https://gh.39.al/',
    'https://ghproxy.cn/',
    'https://down.npee.cn/',
    'https://github.kkproxy.dpdns.org/',
    'https://free.cn.eu.org/',
    'https://git.951959483.xyz/',
    'https://git.820828.xyz/',
    'https://ghproxy.fangkuai.fun/',
    'https://github.cn86.dev/',
    'https://github.zjzzy.cloudns.org/',
    'https://ghb.nilive.top/',
    'https://gitproxy.click/',
    'https://proxy.atoposs.com/',
)


def _personalized_ddi_cache_directory() -> Path:
    """Return the cache path shared with pymobiledevice3's DDI tooling."""
    return get_home_folder() / PERSONALIZED_DDI_CACHE_DIRECTORY


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        while chunk := stream.read(PERSONALIZED_DDI_DOWNLOAD_CHUNK_BYTES):
            digest.update(chunk)
    return digest.hexdigest()


def _github_request_json(url: str) -> object:
    try:
        response = requests.get(
            url,
            headers=_ddi_source_headers(PersonalizedDdiDownloadSource('github-api', 'api')),
            timeout=(PERSONALIZED_DDI_DOWNLOAD_CONNECT_TIMEOUT_SECONDS,
                     PERSONALIZED_DDI_DOWNLOAD_READ_TIMEOUT_SECONDS),
        )
        with response:
            if response.status_code in (403, 429) and \
                    response.headers.get('X-RateLimit-Remaining') == '0':
                raise BridgePrerequisiteError(
                    'developer_image_download_rate_limited',
                    'GitHub API rate limit exhausted while resolving the Personalized DDI.',
                )
            if response.status_code != 200:
                raise BridgePrerequisiteError(
                    'developer_image_download_failed',
                    f'GitHub API returned HTTP {response.status_code}.',
                )
            return response.json()
    except BridgePrerequisiteError:
        raise
    except (RequestException, OSError, ValueError) as error:
        raise BridgePrerequisiteError(
            'developer_image_download_failed',
            f'Unable to resolve Personalized DDI metadata from GitHub: {type(error).__name__}.',
        ) from error


def _resolve_github_personalized_ddi_assets() -> tuple[PersonalizedDdiAsset, ...]:
    """Resolve the current GitHub DDI directory and bind it to this runtime build.

    GitHub's blob SHA is a content identity (SHA-1), not the file SHA-256 used
    by the cache. The downloader verifies both: blob identity while fetching,
    and SHA-256 after the complete payload is received.
    """
    ref_url = PERSONALIZED_DDI_GITHUB_COMMIT_API_URL.format(
        ref=quote(PERSONALIZED_DDI_GITHUB_REF, safe=''))
    commit_payload = _github_request_json(ref_url)
    revision = commit_payload.get('sha') if isinstance(commit_payload, dict) else None
    if not isinstance(revision, str) or len(revision) < 7:
        raise BridgePrerequisiteError(
            'developer_image_download_failed',
            'GitHub did not return a valid DDI commit revision.',
        )
    contents_url = PERSONALIZED_DDI_GITHUB_CONTENTS_API_URL.format(revision=revision)
    contents_payload = _github_request_json(contents_url)
    if not isinstance(contents_payload, list):
        raise BridgePrerequisiteError(
            'developer_image_download_incompatible',
            'GitHub DDI directory response is not a file list.',
        )
    by_name = {
        item.get('name'): item for item in contents_payload
        if isinstance(item, dict) and isinstance(item.get('name'), str)
    }
    upstream_names = {
        'Image.dmg': 'Image.dmg',
        'BuildManifest.plist': 'BuildManifest.plist',
        'Image.trustcache': 'Image.dmg.trustcache',
    }
    assets = []
    for local_name in PERSONALIZED_DDI_FILES:
        upstream_name = upstream_names[local_name]
        item = by_name.get(upstream_name)
        size = item.get('size') if isinstance(item, dict) else None
        blob_id = item.get('sha') if isinstance(item, dict) else None
        if not isinstance(size, int) or size <= 0 or not isinstance(blob_id, str):
            raise BridgePrerequisiteError(
                'developer_image_download_incompatible',
                f'GitHub DDI metadata is missing {upstream_name}.',
            )
        assets.append(PersonalizedDdiAsset(
            local_name, upstream_name, blob_id, size, None, revision))
    return tuple(assets)


def _ddi_metadata_path(root: Path) -> Path:
    return root / PERSONALIZED_DDI_METADATA_FILENAME


def _write_ddi_metadata(root: Path, assets: tuple[PersonalizedDdiAsset, ...],
                        build_id: str) -> None:
    payload = {
        'schema': 1,
        'build_id': build_id,
        'revision': assets[0].revision if assets else '',
        'assets': [
            {'local_name': asset.local_name, 'upstream_name': asset.upstream_name,
             'blob_id': asset.blob_id, 'size': asset.size, 'sha256': asset.sha256}
            for asset in assets
        ],
    }
    temporary = _ddi_metadata_path(root).with_suffix('.tmp')
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding='utf-8')
    os.replace(temporary, _ddi_metadata_path(root))


def _read_ddi_metadata(root: Path) -> Optional[tuple[PersonalizedDdiAsset, ...]]:
    try:
        payload = json.loads(_ddi_metadata_path(root).read_text(encoding='utf-8'))
        if not isinstance(payload, dict):
            return None
        if payload.get('schema') != 1 or payload.get('build_id') != LATEST_DDI_BUILD_ID:
            return None
        assets = []
        for item in payload.get('assets', []):
            if not isinstance(item, dict) or not all(
                    isinstance(item.get(key), expected) for key, expected in (
                        ('local_name', str), ('upstream_name', str), ('blob_id', str),
                        ('size', int), ('sha256', str))):
                return None
            assets.append(PersonalizedDdiAsset(
                item['local_name'], item['upstream_name'], item['blob_id'],
                item['size'], item['sha256'], str(payload.get('revision', ''))))
        if {asset.local_name for asset in assets} != set(PERSONALIZED_DDI_FILES):
            return None
        return tuple(assets)
    except (OSError, TypeError, ValueError, json.JSONDecodeError):
        return None


def _valid_personalized_ddi_bundle(root: Path) -> Optional[tuple[Path, Path, Path]]:
    """Return a cache only when its GitHub-bound metadata and SHA-256 match."""
    assets = _read_ddi_metadata(root)
    if assets is None:
        return None
    try:
        paths = []
        for asset in assets:
            path = root / asset.local_name
            if not path.is_file() or path.stat().st_size != asset.size:
                return None
            if not asset.sha256 or _sha256_file(path) != asset.sha256:
                return None
            paths.append(path)
        build_manifest = plistlib.loads(paths[1].read_bytes())
    except (OSError, ValueError, plistlib.InvalidFileException):
        return None
    if not isinstance(build_manifest, dict) or \
            build_manifest.get('ProductBuildVersion') != LATEST_DDI_BUILD_ID:
        return None
    return tuple(root / name for name in PERSONALIZED_DDI_FILES)


def _ddi_source_headers(source: PersonalizedDdiDownloadSource) -> dict[str, str]:
    headers = {
        'User-Agent': 'iUsbBridge/1.0',
        'Accept-Encoding': 'identity',
    }
    if source.kind == 'api':
        headers['Accept'] = 'application/vnd.github.raw+json'
        # Never send an optional GitHub credential through a public mirror.
        token = os.environ.get('IPHONE_MIRROR_GITHUB_TOKEN', '').strip()
        if token:
            headers['Authorization'] = f'Bearer {token}'
    return headers


def _ddi_asset_url(source: PersonalizedDdiDownloadSource,
                   asset: PersonalizedDdiAsset) -> str:
    if source.kind == 'api':
        return PERSONALIZED_DDI_GITHUB_API_URL.format(blob_id=asset.blob_id)
    upstream = PERSONALIZED_DDI_GITHUB_RAW_URL.format(
        revision=asset.revision or PERSONALIZED_DDI_GITHUB_REF,
        path=quote(asset.upstream_name, safe='/'))
    return upstream if source.kind == 'raw' else source.prefix + upstream


def _normalized_url_host(url: str) -> str:
    host = urlsplit(url).hostname
    if not host:
        raise ValueError('The download URL has no host.')
    return host.encode('idna').decode('ascii').casefold()


def _validate_ddi_source_response(source: PersonalizedDdiDownloadSource,
                                  requested_url: str, response) -> None:
    """Keep a mirror on its declared HTTPS host after redirects."""
    final_url = getattr(response, 'url', '')
    try:
        final_scheme = urlsplit(final_url).scheme.casefold()
        if final_scheme != 'https' or \
                _normalized_url_host(final_url) != _normalized_url_host(requested_url):
            raise ValueError('redirected to a different host')
    except ValueError as error:
        raise BridgePrerequisiteError(
            'developer_image_download_failed',
            f'{source.name} did not retain its trusted HTTPS endpoint: {error}.',
        ) from error


def _ddi_source_root(source: PersonalizedDdiDownloadSource,
                     probe_asset: PersonalizedDdiAsset) -> str:
    parts = urlsplit(_ddi_asset_url(source, probe_asset))
    return f'{parts.scheme}://{parts.netloc}/'


def _personalized_ddi_download_sources() -> tuple[PersonalizedDdiDownloadSource, ...]:
    # DDI downloads must use GitHub directly. Do not route device images
    # through the updater's third-party mirror list or throughput probes.
    return (
        PersonalizedDdiDownloadSource('github-raw', 'raw'),
        PersonalizedDdiDownloadSource('github-api', 'api'),
    )


def _parse_content_length(response) -> Optional[int]:
    raw_value = response.headers.get('Content-Length')
    if raw_value is None:
        return None
    try:
        value = int(raw_value)
    except (TypeError, ValueError) as error:
        raise ValueError('invalid Content-Length') from error
    if value < 0:
        raise ValueError('negative Content-Length')
    return value


def _read_probe_payload(response, expected_bytes: int) -> int:
    received = 0
    for chunk in response.iter_content(chunk_size=64 * 1024):
        if not chunk:
            continue
        remaining = expected_bytes - received
        if remaining <= 0:
            break
        received += min(len(chunk), remaining)
        if received == expected_bytes:
            break
    if received != expected_bytes:
        raise ValueError(
            f'probe ended at {received} bytes, expected {expected_bytes}')
    return received


def _ping_personalized_ddi_source(source: PersonalizedDdiDownloadSource,
                                  probe_asset: PersonalizedDdiAsset) -> Optional[float]:
    """Mirror connectivity stage equivalent to the updater's HEAD sweep."""
    requested_url = _ddi_source_root(source, probe_asset)
    try:
        started = time.perf_counter()
        response = requests.head(
            requested_url,
            headers=_ddi_source_headers(source),
            timeout=(PERSONALIZED_DDI_SOURCE_CONNECT_TIMEOUT_SECONDS,
                     PERSONALIZED_DDI_SOURCE_CONNECT_TIMEOUT_SECONDS),
            allow_redirects=True,
        )
        try:
            _validate_ddi_source_response(source, requested_url, response)
            return time.perf_counter() - started
        finally:
            response.close()
    except (BridgePrerequisiteError, RequestException, OSError, ValueError):
        return None


def _probe_personalized_ddi_source(source: PersonalizedDdiDownloadSource,
                                   asset: PersonalizedDdiAsset) -> Optional[float]:
    """Measure an actual ranged payload before selecting a mirror."""
    requested_url = _ddi_asset_url(source, asset)
    sample_bytes = min(asset.size, PERSONALIZED_DDI_MIRROR_PROBE_BYTES)
    probe_end = sample_bytes - 1
    headers = _ddi_source_headers(source)
    headers['Range'] = f'bytes=0-{probe_end}'
    try:
        started = time.perf_counter()
        with requests.get(
                requested_url,
                headers=headers,
                timeout=(PERSONALIZED_DDI_SOURCE_CONNECT_TIMEOUT_SECONDS,
                         PERSONALIZED_DDI_SOURCE_READ_TIMEOUT_SECONDS),
                stream=True,
                allow_redirects=True) as response:
            _validate_ddi_source_response(source, requested_url, response)
            content_length = _parse_content_length(response)
            if response.status_code == 200:
                if content_length is not None and content_length != asset.size:
                    raise ValueError('full probe size does not match the pinned asset')
            elif response.status_code == 206:
                expected_range = f'bytes 0-{probe_end}/{asset.size}'
                if response.headers.get('Content-Range', '').casefold() != expected_range:
                    raise ValueError('partial probe returned an invalid content range')
                if content_length is not None and content_length != sample_bytes:
                    raise ValueError('partial probe returned an invalid content length')
            else:
                raise ValueError(f'probe returned HTTP {response.status_code}')
            received = _read_probe_payload(response, sample_bytes)
        return received / max(time.perf_counter() - started, 0.001)
    except (BridgePrerequisiteError, RequestException, OSError, ValueError):
        return None


def _measure_ddi_sources(sources: tuple[PersonalizedDdiDownloadSource, ...],
                         operation: Callable[[PersonalizedDdiDownloadSource], Optional[float]],
                         timeout_seconds: int) -> list[tuple[PersonalizedDdiDownloadSource, float]]:
    """Run the updater-style sweep concurrently without blocking the bridge loop."""
    if not sources:
        return []
    executor = ThreadPoolExecutor(
        max_workers=len(sources), thread_name_prefix='iphoneMirror-ddi-probe')
    futures = {executor.submit(operation, source): source for source in sources}
    try:
        completed, _ = wait(futures, timeout=timeout_seconds)
        measurements = []
        for future in completed:
            try:
                measurement = future.result()
            except (RequestException, OSError, ValueError):
                measurement = None
            if measurement is not None:
                measurements.append((futures[future], measurement))
        return measurements
    finally:
        for future in futures:
            future.cancel()
        executor.shutdown(wait=False, cancel_futures=True)


def _deduplicate_ddi_sources(sources: list[PersonalizedDdiDownloadSource]) -> \
        tuple[PersonalizedDdiDownloadSource, ...]:
    selected = []
    seen: set[PersonalizedDdiDownloadSource] = set()
    for source in sources:
        if source not in seen:
            seen.add(source)
            selected.append(source)
    return tuple(selected)


def rank_personalized_ddi_download_sources() -> tuple[PersonalizedDdiDownloadSource, ...]:
    """Return deterministic official GitHub endpoints without mirror probing."""
    sources = _personalized_ddi_download_sources()
    log.info('Personalized DDI downloads use direct GitHub endpoints: %s',
             ', '.join(source.name for source in sources))
    return sources


def _download_personalized_ddi_asset(
        source: PersonalizedDdiDownloadSource, asset: PersonalizedDdiAsset,
        destination: Path) -> None:
    requested_url = _ddi_asset_url(source, asset)
    try:
        with requests.get(
                requested_url,
                headers=_ddi_source_headers(source),
                timeout=(PERSONALIZED_DDI_DOWNLOAD_CONNECT_TIMEOUT_SECONDS,
                         PERSONALIZED_DDI_DOWNLOAD_READ_TIMEOUT_SECONDS),
                stream=True,
                allow_redirects=True) as response:
            _validate_ddi_source_response(source, requested_url, response)
            if response.status_code in (403, 429) and \
                    response.headers.get('X-RateLimit-Remaining') == '0':
                raise BridgePrerequisiteError(
                    'developer_image_download_rate_limited',
                    f'{source.name} rate-limited the Personalized DDI download.',
                )
            if response.status_code != 200:
                raise BridgePrerequisiteError(
                    'developer_image_download_failed',
                    f'{source.name} returned HTTP {response.status_code} for {asset.local_name}.',
                )
            content_length = _parse_content_length(response)
            if content_length is not None and content_length != asset.size:
                raise BridgePrerequisiteError(
                    'developer_image_download_integrity_failed',
                    f'{source.name} returned an unexpected size for {asset.local_name}.',
                )
            digest = hashlib.sha256()
            git_digest = hashlib.sha1(
                b'blob ' + str(asset.size).encode('ascii') + b'\0')
            received = 0
            with destination.open('xb') as output:
                for chunk in response.iter_content(
                        chunk_size=PERSONALIZED_DDI_DOWNLOAD_CHUNK_BYTES):
                    if not chunk:
                        continue
                    received += len(chunk)
                    if received > asset.size:
                        raise BridgePrerequisiteError(
                            'developer_image_download_integrity_failed',
                            f'{source.name} exceeded the pinned size for {asset.local_name}.',
                        )
                    digest.update(chunk)
                    git_digest.update(chunk)
                    output.write(chunk)
                output.flush()
                os.fsync(output.fileno())
            actual_sha256 = digest.hexdigest()
            actual_blob_id = git_digest.hexdigest()
            if received != asset.size or (asset.sha256 and actual_sha256 != asset.sha256) or \
                    (asset.blob_id and actual_blob_id != asset.blob_id):
                raise BridgePrerequisiteError(
                    'developer_image_download_integrity_failed',
                    f'{source.name} failed content verification for {asset.local_name} '
                    f'(sha256={actual_sha256}, blob={actual_blob_id}).',
                )
    except BridgePrerequisiteError:
        raise
    except (RequestException, OSError, ValueError) as error:
        raise BridgePrerequisiteError(
            'developer_image_download_failed',
            f'{source.name} could not download {asset.local_name}: {type(error).__name__}.',
        ) from error


def _raise_ddi_download_failure(asset: PersonalizedDdiAsset,
                                failures: list[BridgePrerequisiteError]) -> None:
    codes = [failure.code for failure in failures]
    if codes and all(code == 'developer_image_download_rate_limited' for code in codes):
        raise BridgePrerequisiteError(
            'developer_image_download_rate_limited',
            'All Personalized DDI download sources are rate-limited. Configure '
            'IPHONE_MIRROR_GITHUB_TOKEN or retry later.',
        )
    code = ('developer_image_download_integrity_failed'
            if 'developer_image_download_integrity_failed' in codes
            else 'developer_image_download_failed')
    details = '; '.join(str(error)[:150] for error in failures[-4:])
    raise BridgePrerequisiteError(
        code,
        f'No verified source could provide {asset.local_name}. {details}',
    )


def fetch_automatic_personalized_ddi_bundle(
        on_download_started: Optional[Callable[[], None]] = None) -> tuple[Path, Path, Path]:
    """Resolve and download a GitHub DDI, then atomically commit its hashes."""
    cache = _personalized_ddi_cache_directory()
    cached = _valid_personalized_ddi_bundle(cache)
    if cached is not None:
        return cached

    assets = _resolve_github_personalized_ddi_assets()
    log.info('Resolved Personalized DDI from GitHub ref=%s revision=%s build=%s',
             PERSONALIZED_DDI_GITHUB_REF, assets[0].revision, LATEST_DDI_BUILD_ID)

    cache.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix='iphoneMirror-ddi-', dir=cache.parent))
    try:
        sources = rank_personalized_ddi_download_sources()
        if not sources:
            raise BridgePrerequisiteError(
                'developer_image_download_failed',
                'No Personalized DDI download sources are available.',
            )
        if on_download_started is not None:
            try:
                on_download_started()
            except Exception as error:
                log.info('Unable to report Personalized DDI download status: %s', error)

        for asset in assets:
            partial = staging / f'{asset.local_name}.download'
            target = staging / asset.local_name
            failures: list[BridgePrerequisiteError] = []
            for source in sources:
                with contextlib.suppress(OSError):
                    partial.unlink()
                try:
                    _download_personalized_ddi_asset(source, asset, partial)
                    os.replace(partial, target)
                    log.info('Downloaded verified Personalized DDI asset %s from %s',
                             asset.local_name, source.name)
                    break
                except BridgePrerequisiteError as error:
                    failures.append(error)
                    log.info('Personalized DDI source failed: asset=%s source=%s code=%s',
                             asset.local_name, source.name, error.code)
            else:
                _raise_ddi_download_failure(asset, failures)

        resolved_assets = tuple(replace(asset, sha256=_sha256_file(staging / asset.local_name))
                                for asset in assets)
        try:
            manifest = plistlib.loads((staging / 'BuildManifest.plist').read_bytes())
        except (OSError, ValueError, plistlib.InvalidFileException) as error:
            raise BridgePrerequisiteError(
                'developer_image_download_incompatible',
                'Downloaded Personalized DDI BuildManifest.plist is invalid.',
            ) from error
        if not isinstance(manifest, dict) or \
                manifest.get('ProductBuildVersion') != LATEST_DDI_BUILD_ID:
            raise BridgePrerequisiteError(
                'developer_image_download_incompatible',
                'Downloaded Personalized DDI BuildManifest does not match '
                f'pymobiledevice3 build {LATEST_DDI_BUILD_ID}.',
            )
        _write_ddi_metadata(staging, resolved_assets, LATEST_DDI_BUILD_ID)
        cache.mkdir(parents=True, exist_ok=True)
        for asset in assets:
            os.replace(staging / asset.local_name, cache / asset.local_name)
        os.replace(staging / PERSONALIZED_DDI_METADATA_FILENAME,
                   cache / PERSONALIZED_DDI_METADATA_FILENAME)
        cached = _valid_personalized_ddi_bundle(cache)
        if cached is None:
            raise BridgePrerequisiteError(
                'developer_image_download_failed',
                'The verified Personalized DDI could not be committed to the local cache.',
            )
        return cached
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def bridge_error_code(error: Exception) -> str:
    """Map expected Apple transport failures to stable IPC error codes.

    The bridge is self-contained, but Apple's signed USB driver/service remains
    the OS-owned transport. A Python exception class is not a useful support
    contract for the WPF host, especially on a clean customer machine.
    """
    if isinstance(error, BridgePrerequisiteError):
        return error.code
    if isinstance(error, ConnectionFailedToUsbmuxdError):
        return 'apple_usbmux_unavailable'
    if isinstance(error, NotPairedError):
        return 'apple_device_not_trusted'
    if isinstance(error, DeviceNotFoundError):
        return 'apple_device_not_found'
    return type(error).__name__.lower()


async def create_lockdown_with_retry(ipc, udid: Optional[str],
                                     connection_type: str):
    """Rebuild the full Lockdown client after transient usbmux failures."""
    for attempt in range(1, LOCKDOWN_CONNECT_ATTEMPTS + 1):
        try:
            # Device discovery already verified the pair record. Never make
            # provisioning or reverse control initiate or replace trust.
            return await create_using_usbmux(
                serial=udid,
                connection_type=connection_type,
                autopair=False,
            )
        except LOCKDOWN_RETRYABLE_ERRORS as error:
            if attempt == LOCKDOWN_CONNECT_ATTEMPTS:
                raise
            await ipc.emit({
                'event': 'warning',
                'code': 'lockdown_retry',
                'message': (
                    f'{type(error).__name__}: {str(error)[:180]}; '
                    f'retrying Lockdown {attempt}/{LOCKDOWN_CONNECT_ATTEMPTS - 1}'
                ),
            })
            await asyncio.sleep(LOCKDOWN_RETRY_DELAY_SECONDS * attempt)
LEGACY_UNIVERSAL_HID_FEATURE = 'com.apple.coredevice.feature.remote.universalhid'


class LegacyUniversalHIDServiceService(UniversalHIDServiceService):
    SERVICE_NAME = LEGACY_UNIVERSAL_HID_SERVICE

    async def list_connected_services(self) -> dict:
        return await self.service.send_receive_request({
            'featureIdentifier': LEGACY_UNIVERSAL_HID_FEATURE,
            'messageType': 'Request',
            'payload': {'connectedServices': {}},
        })

    async def send_report(self, service_id: int, report: bytes) -> None:
        await self.service.send_request({
            'featureIdentifier': LEGACY_UNIVERSAL_HID_FEATURE,
            'messageType': 'Request',
            'payload': {'send': {'_0': report, '_1': XpcUInt64Type(service_id)}},
        })


def decode_touch_batch(message: dict) -> tuple[int, Optional[int], list[dict]]:
    """Validate and normalize one application-layer touch message."""
    if not isinstance(message, dict) or message.get('schema') != MESSAGE_SCHEMA:
        raise ValueError('unsupported touch message schema')
    if message.get('kind') != MESSAGE_KIND:
        raise ValueError('unsupported touch message kind')
    sequence = message.get('seq')
    if not isinstance(sequence, int) or isinstance(sequence, bool) or sequence < 0:
        raise ValueError('sequence must be a non-negative integer')
    timestamp = message.get('timestampNs')
    if timestamp is not None and (not isinstance(timestamp, int) or isinstance(timestamp, bool)):
        raise ValueError('timestampNs must be an integer')
    points = message.get('points')
    if not isinstance(points, list) or not 1 <= len(points) <= MAX_SLOTS:
        raise ValueError('points must contain 1..5 items')
    seen: set[int] = set()
    for point in points:
        if not isinstance(point, dict):
            raise ValueError('each point must be an object')
        pointer_id = point.get('pointerId')
        action = point.get('action')
        x = point.get('normalizedX')
        y = point.get('normalizedY')
        if not isinstance(pointer_id, int) or isinstance(pointer_id, bool) or pointer_id < 0:
            raise ValueError('pointerId must be a non-negative integer')
        if pointer_id in seen:
            raise ValueError('pointerId must be unique within a batch')
        if action not in VALID_ACTIONS:
            raise ValueError('action must be down, move, or up')
        if not isinstance(x, (int, float)) or isinstance(x, bool) or not 0.0 <= float(x) <= 1.0:
            raise ValueError('normalizedX must be between 0 and 1')
        if not isinstance(y, (int, float)) or isinstance(y, bool) or not 0.0 <= float(y) <= 1.0:
            raise ValueError('normalizedY must be between 0 and 1')
        if not (float(x) == float(x) and float(y) == float(y)):
            raise ValueError('coordinates must be finite')
        seen.add(pointer_id)
    return sequence, timestamp, points


def decode_keyboard_batch(message: dict) -> tuple[int, Optional[int], list[int]]:
    if not isinstance(message, dict) or message.get('schema') != MESSAGE_SCHEMA:
        raise ValueError('unsupported keyboard message schema')
    if message.get('kind') != KEYBOARD_MESSAGE_KIND:
        raise ValueError('unsupported keyboard message kind')
    sequence = message.get('seq')
    if not isinstance(sequence, int) or isinstance(sequence, bool) or sequence < 0:
        raise ValueError('sequence must be a non-negative integer')
    timestamp = message.get('timestampNs')
    if timestamp is not None and (not isinstance(timestamp, int) or isinstance(timestamp, bool)):
        raise ValueError('timestampNs must be an integer')
    usages = message.get('usages')
    if not isinstance(usages, list) or len(usages) > 30:
        raise ValueError('usages must contain 0..30 items')
    normalized = []
    seen = set()
    for usage in usages:
        if not isinstance(usage, int) or isinstance(usage, bool) or not 0 <= usage < 240:
            raise ValueError('keyboard usage must be between 0 and 239')
        if usage not in seen:
            normalized.append(usage)
            seen.add(usage)
    return sequence, timestamp, normalized


def decode_button_event(message: dict) -> tuple[int, int, int, str]:
    if not isinstance(message, dict) or message.get('schema') != MESSAGE_SCHEMA:
        raise ValueError('unsupported button message schema')
    if message.get('kind') != BUTTON_MESSAGE_KIND:
        raise ValueError('unsupported button message kind')
    sequence = message.get('seq')
    page = message.get('usagePage')
    code = message.get('usageCode')
    state = message.get('state')
    if not all(isinstance(value, int) and not isinstance(value, bool) and value >= 0
               for value in (sequence, page, code)):
        raise ValueError('button sequence and usages must be non-negative integers')
    if page > 0xFFFF or code > 0xFFFF or state not in BUTTON_STATES:
        raise ValueError('invalid button usage or state')
    return sequence, page, code, state


class FiveSlotStateMachine:
    """逻辑触点 ID → 固定 slot 0..4，最多 5 点同触。

    slot 状态字节: release = 0x02 | slot, contact = 0xC2 | slot
    每点独立 58 字节报告；多点并发时按 slot 顺序逐个发送。
    """

    def __init__(self) -> None:
        self._id_to_slot: dict[int, int] = {}
        self._free_slots = list(range(MAX_SLOTS))

    def assign(self, touch_id: int) -> Optional[int]:
        if touch_id in self._id_to_slot:
            return self._id_to_slot[touch_id]
        if not self._free_slots:
            return None
        slot = self._free_slots.pop(0)
        self._id_to_slot[touch_id] = slot
        return slot

    def release(self, touch_id: int) -> Optional[int]:
        slot = self._id_to_slot.pop(touch_id, None)
        if slot is None:
            return None
        self._free_slots.append(slot)
        self._free_slots.sort()
        return slot

    def slot_for(self, touch_id: int) -> Optional[int]:
        return self._id_to_slot.get(touch_id)

    def clear(self) -> list[int]:
        ids = list(self._id_to_slot.keys())
        for i in ids:
            self.release(i)
        return ids


def build_touchscreen_report(slot: int, state: int, x: int, y: int, timestamp: Optional[int] = None) -> bytes:
    """58 字节 mainTouchscreen 报告，slot 状态字节按五点状态机规则。

    slot 0..4: contact = 0xC2 | slot, release = 0x02 | slot
    """
    if timestamp is None:
        timestamp = time.monotonic_ns() & ((1 << 48) - 1)
    if state == TOUCHSCREEN_STATE_CONTACT:
        state_byte = 0xC2 | (slot & 0x07)
    else:
        state_byte = 0x02 | (slot & 0x07)
    return (
        bytes([0x09, 0x01, 0x05, state_byte])
        + struct.pack('<HH', x & 0xFFFF, y & 0xFFFF)
        + b'\x00' * 32
        + b'\x02\x00\x00\x00'
        + timestamp.to_bytes(6, 'little')
        + b'\x00' * 8
    )


class BridgeChannel:
    """Length-prefixed input and JSON-lines lifecycle output."""

    def __init__(self) -> None:
        self._write_lock = asyncio.Lock()
        self._stdin = sys.stdin.buffer
        self._stdout = sys.stdout.buffer

    async def emit(self, event: dict) -> None:
        line = json.dumps(event, ensure_ascii=False) + '\n'
        async with self._write_lock:
            self._stdout.write(line.encode('utf-8'))
            self._stdout.flush()

    async def read_messages(self):
        loop = asyncio.get_event_loop()
        while True:
            header = await loop.run_in_executor(None, self._stdin.read, 4)
            if not header or len(header) < 4:
                return
            (length,) = struct.unpack('<I', header)
            if length == 0 or length > MAX_FRAME_SIZE:
                await self.emit({'event': 'error', 'code': 'bad_frame',
                                 'message': f'frame length must be between 1 and {MAX_FRAME_SIZE}'})
                return
            payload = await loop.run_in_executor(None, self._stdin.read, length)
            if not payload or len(payload) < length:
                return
            try:
                yield json.loads(payload.decode('utf-8'))
            except (UnicodeDecodeError, json.JSONDecodeError) as e:
                await self.emit({'event': 'error', 'code': 'bad_frame', 'message': str(e)})


class TouchSession:
    """USB → CoreDevice 隧道 → RSD → Universal HID 会话。"""

    def __init__(self, ipc: BridgeChannel, rate_hz: int, udid: Optional[str] = None,
                 transport: str = 'usb', ddi_dir: Optional[Path] = None) -> None:
        self.ipc = ipc
        self.rate_hz = rate_hz
        self.udid = udid
        self.transport_mode = transport
        self.ddi_dir = ddi_dir
        self.rsd: Optional[RemoteServiceDiscoveryService] = None
        self.hid: Optional[UniversalHIDServiceService] = None
        self.indigo: Optional[IndigoHIDService] = None
        self.keyboard_service_id: Optional[int] = None
        self.display: Optional[DisplayService] = None
        self.dial_plane = None
        self.stream_answer = None
        self.drain_task = None
        self.transport = None
        self.auth_mode: Optional[str] = None
        self.gate_open = False
        self._ddi_was_mounted = False
        self._ddi_refresh_attempted = False
        self._remote_pairing_provision_attempted = False
        self._usb_mux_transport = None
        self._usb_mux_server = None
        self._usb_mux_previous_env: Optional[str] = None

    async def _start_capture_mux(self) -> None:
        """Prefer the active QuickTime configuration without making it fatal.

        A wired mirror temporarily exposes a second usbmux interface.  Some
        devices need a short re-enumeration window before that interface will
        answer its VERSION packet.  Releasing and rediscovering it preserves
        the mirror, while a final fallback lets the normal Apple usbmuxd path
        handle devices that still expose it.
        """
        if self.transport_mode != 'usb' or _UsbMuxTransport is None:
            return
        backend = _get_usb_backend('auto')
        devices = _find_usb_devices(backend, self.udid)
        device = next((item for item in devices if item.activated), None)
        if device is None:
            # No active QuickTime configuration means standalone wired control;
            # Apple's normal usbmuxd remains the correct transport in that case.
            return
        for attempt in range(1, CAPTURE_MUX_START_ATTEMPTS + 1):
            mux = None
            server = None
            try:
                mux = _UsbMuxTransport(device.dev, device.serial)
                mux.start()
                server = _UsbmuxdThread(mux.mux, device.serial, port=0)
                address = server.start()
            except Exception as error:  # The fallback below owns this failure.
                with contextlib.suppress(Exception):
                    if server is not None:
                        server.stop()
                with contextlib.suppress(Exception):
                    if mux is not None:
                        mux.close()
                if attempt == CAPTURE_MUX_START_ATTEMPTS:
                    message = f'{type(error).__name__}: {str(error)[:180]}'
                    log.warning('capture usbmux handshake unavailable after %d attempts: %s',
                                attempt, message)
                    await self.ipc.emit({
                        'event': 'warning',
                        'code': 'capture_mux_fallback',
                        'message': (
                            'The active wired-mirroring usbmux interface did not respond; '
                            f'using the Apple usbmuxd path instead ({message}).'
                        ),
                    })
                    return
                await self.ipc.emit({
                    'event': 'warning',
                    'code': 'capture_mux_retry',
                    'message': (
                        f'{type(error).__name__}: {str(error)[:180]}; '
                        f'retrying active wired-mirroring usbmux {attempt}/'
                        f'{CAPTURE_MUX_START_ATTEMPTS - 1}'
                    ),
                })
                await asyncio.sleep(CAPTURE_MUX_RETRY_DELAY_SECONDS * attempt)
                devices = _find_usb_devices(backend, self.udid)
                device = next((item for item in devices if item.activated), None)
                if device is None:
                    log.info('active wired-mirroring usbmux disappeared during retry; using Apple usbmuxd')
                    return
                continue
            self._usb_mux_previous_env = os.environ.get('USBMUXD_SOCKET_ADDRESS')
            self._usb_mux_transport = mux
            self._usb_mux_server = server
            os.environ['USBMUXD_SOCKET_ADDRESS'] = address
            log.info('capture usbmux bridge active at %s (attempt %d)', address, attempt)
            return

    async def _emit_status(self, code: str) -> None:
        await self.ipc.emit({'event': 'status', 'code': code, 'message': {
            'connecting_device': f'正在建立{self.transport_mode_name}设备会话',
            'checking_developer_environment': '正在检查开发者模式和开发者镜像',
            'mounting_developer_image': '正在准备开发者镜像',
            'testing_developer_image_sources': '正在检查 GitHub 开发者镜像下载',
            'downloading_developer_image': '正在下载并校验开发者镜像',
            'remounting_developer_image': '正在刷新不兼容的开发者镜像',
            'discovering_wireless_device': '正在通过 RemotePairing 发现无线设备',
            'initializing_touch': '正在初始化触控通道',
            'terminated': 'USB 触控会话已结束',
        }.get(code, code)})

    @property
    def transport_mode_name(self) -> str:
        return '无线' if self.transport_mode == 'wireless' else 'USB'

    async def connect(self) -> None:
        await self._emit_status('connecting_device')
        connection_type = 'Network' if self.transport_mode == 'wireless' else 'USB'
        await self._start_capture_mux()
        # Never fall back to a cable here.  The caller selected wireless
        # control explicitly, and reporting a ready session over USB would
        # make the UI claim that a network control path is working.
        try:
            lockdown = await self._create_lockdown_with_retry(connection_type)
        except DeviceNotFoundError:
            if self.transport_mode != 'wireless':
                raise
            # Wi-Fi Sync only gives us a legacy Network usbmux record. On
            # current iOS releases the supported wireless CoreDevice route is
            # RemotePairing over mDNS, so try it when that legacy record is
            # absent instead of silently changing to the USB path.
            await self._connect_wireless_via_remote_pairing()
            return
        try:
            await self._connect_with_ddi_recovery(lockdown)
        finally:
            # The tunnel/RSD scopes do not own the usbmux Lockdown client.
            # Always close it, including failures before start_tunnel enters.
            with contextlib.suppress(Exception):
                await lockdown.close()

    async def _provision_remote_pairing(self, lockdown) -> bool:
        """Create the one-time RemotePairing record over the trusted USB link.

        Apple Wi-Fi Sync and RemotePairing are independent credentials. The
        latter is provisioned through a lockdownd service without displaying a
        second Trust prompt. A completed pair deliberately closes its control
        socket, so reconnect once to make sure the record is usable.
        """
        try:
            service = await asyncio.wait_for(
                RemotePairingLockdownService.create(lockdown),
                timeout=REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS,
            )
            try:
                try:
                    await asyncio.wait_for(
                        service.connect(autopair=True),
                        timeout=REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS,
                    )
                except RemotePairingCompletedError:
                    await service.close()
                    service = await asyncio.wait_for(
                        RemotePairingLockdownService.create(lockdown),
                        timeout=REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS,
                    )
                    await asyncio.wait_for(
                        service.connect(autopair=False),
                        timeout=REMOTE_PAIRING_PROVISION_TIMEOUT_SECONDS,
                    )
                return True
            finally:
                with contextlib.suppress(Exception):
                    await service.close()
        except Exception as error:
            await self.ipc.emit({
                'event': 'warning', 'code': 'wireless_remote_pairing_provision_failed',
                'message': f'RemotePairing provisioning did not complete: {type(error).__name__}: {str(error)[:180]}',
            })
            return False

    async def _connect_with_ddi_recovery(self, lockdown) -> None:
        """Reconnect after a DDI service-registration failure exactly once.

        A mounted Personalized DDI is not sufficient evidence that dtuhidd has
        published its CoreDevice services. This is observable after Xcode or a
        previous tool leaves an outdated image mounted: RSD itself connects but
        ``Services`` is empty. Rebuilding the RSD tunnel is necessary because
        its service inventory is a handshake snapshot, not a live list.
        """
        try:
            await self._connect_with_lockdown(lockdown)
            return
        except BridgePrerequisiteError as error:
            if error.code != 'touch_surface_unavailable':
                raise

        if self._ddi_was_mounted and not self._ddi_refresh_attempted:
            self._ddi_refresh_attempted = True
            await self._refresh_personalized_ddi(lockdown)

        last_error: Optional[BridgePrerequisiteError] = None
        for attempt in range(HID_SERVICE_REGISTRATION_ATTEMPTS):
            try:
                await self._connect_with_lockdown(lockdown)
                return
            except BridgePrerequisiteError as error:
                if error.code != 'touch_surface_unavailable':
                    raise
                last_error = error
                if attempt + 1 == HID_SERVICE_REGISTRATION_ATTEMPTS:
                    raise
                await self.ipc.emit({
                    'event': 'status', 'code': 'waiting_for_hid_service',
                    'message': (
                        'Waiting for the Personalized DDI to publish Universal HID; '
                        f'retry {attempt + 1}/{HID_SERVICE_REGISTRATION_ATTEMPTS - 1}'
                    ),
                })
                await asyncio.sleep(HID_SERVICE_REGISTRATION_RETRY_SECONDS)

        if last_error is not None:
            raise last_error

    async def _connect_wireless_via_remote_pairing(self) -> None:
        """Use the iOS RemotePairing route when Network usbmux is unavailable."""
        if not self.udid:
            raise BridgePrerequisiteError(
                'wireless_remote_pairing_required',
                'Wireless CoreDevice control requires a known Apple UDID and a USB provisioning pass.',
            )
        try:
            # The public discovery helper compares the requested identifier
            # byte-for-byte with the pair-record filename.  Keep the exact
            # stored spelling for that call while accepting the casing used by
            # Apple device discovery and the WPF host.
            remote_records = {
                identifier.casefold(): identifier
                for identifier in iter_remote_paired_identifiers()
            }
        except Exception as error:
            raise BridgePrerequisiteError(
                'wireless_remote_pairing_failed',
                f'Unable to inspect local RemotePairing records: {type(error).__name__}: {str(error)[:180]}',
            ) from error
        paired_identifier = remote_records.get(self.udid.casefold())
        if paired_identifier is None:
            raise BridgePrerequisiteError(
                'wireless_remote_pairing_required',
                'This PC has no RemotePairing record for the device. Connect it by USB once, unlock it, '
                'and let the bridge finish wireless provisioning before using wireless control.',
            )

        await self._emit_status('discovering_wireless_device')
        try:
            pairing_services = await asyncio.wait_for(
                get_remote_pairing_tunnel_services(
                    bonjour_timeout=REMOTE_PAIRING_DISCOVERY_TIMEOUT_SECONDS,
                    udid=paired_identifier,
                ),
                timeout=(REMOTE_PAIRING_DISCOVERY_TIMEOUT_SECONDS +
                         REMOTE_PAIRING_DISCOVERY_GRACE_SECONDS),
            )
        except asyncio.TimeoutError as error:
            raise BridgePrerequisiteError(
                'wireless_device_not_discoverable',
                'RemotePairing discovery timed out. Keep the iPhone unlocked on the same LAN and allow mDNS '
                'through the Windows firewall.',
            ) from error
        except Exception as error:
            raise BridgePrerequisiteError(
                'wireless_remote_pairing_failed',
                f'RemotePairing discovery failed: {type(error).__name__}: {str(error)[:180]}',
            ) from error
        if not pairing_services:
            raise BridgePrerequisiteError(
                'wireless_device_not_discoverable',
                'No RemotePairing service was discovered. Keep the iPhone unlocked on the same LAN and allow '
                'mDNS through the Windows firewall.',
            )

        selected, *unused_services = pairing_services
        try:
            async with start_tunnel(selected, protocol=TunnelProtocol.TCP) as tunnel_result:
                await self._connect_with_tunnel_result(tunnel_result)
        except BridgePrerequisiteError:
            raise
        except Exception as error:
            raise BridgePrerequisiteError(
                'wireless_remote_pairing_failed',
                f'RemotePairing tunnel failed: {type(error).__name__}: {str(error)[:180]}',
            ) from error
        finally:
            for service in unused_services:
                with contextlib.suppress(Exception):
                    await service.close()

    async def _create_lockdown_with_retry(self, connection_type: str):
        return await create_lockdown_with_retry(
            self.ipc, self.udid, connection_type)

    async def _preflight_developer_environment(self, lockdown) -> None:
        """Require Developer Mode and prepare a Personalized DDI before RSD.

        An explicit ``--ddi-dir`` wins when provided.  Otherwise the bridge
        lets pymobiledevice3 obtain the current DDI through its normal cache
        and Apple personalization flow.  Neither path searches the install
        directory or consumes a bundled image.
        """
        await self._emit_status('checking_developer_environment')
        try:
            developer_mode_enabled = await lockdown.get_developer_mode_status()
        except Exception as error:
            raise BridgePrerequisiteError(
                'developer_mode_check_failed',
                f'Unable to query Developer Mode status: {type(error).__name__}: {str(error)[:180]}',
            ) from error
        if not developer_mode_enabled:
            raise BridgePrerequisiteError(
                'developer_mode_required',
                'Developer Mode must be enabled on the device before USB control can start.',
            )

        try:
            self._ddi_was_mounted = await self._is_personalized_ddi_mounted(lockdown)
        except DeveloperModeIsNotEnabledError as error:
            raise BridgePrerequisiteError(
                'developer_mode_required',
                'Developer Mode must be enabled on the device before USB control can start.',
            ) from error
        if self._ddi_was_mounted:
            return
        await self._mount_personalized_ddi(lockdown)

    async def _mount_personalized_ddi(self, lockdown) -> None:
        await self._emit_status('mounting_developer_image')
        if self.ddi_dir is not None:
            image, build_manifest, trustcache = local_personalized_ddi_bundle(self.ddi_dir)
            source = 'local'
        else:
            source = 'automatic'
            await self._emit_status('testing_developer_image_sources')
            loop = asyncio.get_running_loop()

            def report_download_started() -> None:
                status = asyncio.run_coroutine_threadsafe(
                    self._emit_status('downloading_developer_image'), loop)
                status.result(timeout=5)

            try:
                image, build_manifest, trustcache = await asyncio.wait_for(
                    asyncio.to_thread(fetch_automatic_personalized_ddi_bundle,
                                      report_download_started),
                    timeout=PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS,
                )
            except BridgePrerequisiteError:
                raise
            except asyncio.TimeoutError as error:
                raise BridgePrerequisiteError(
                    'developer_image_download_timeout',
                    'Testing and downloading the Personalized DDI timed out after '
                    f'{PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS} seconds.',
                ) from error
            except Exception as error:
                raise BridgePrerequisiteError(
                    'developer_image_download_failed',
                    'Unable to prepare the Personalized DDI download: '
                    f'{type(error).__name__}: {str(error)[:180]}',
                ) from error

        try:
            async with PersonalizedImageMounter(lockdown=lockdown) as mounter:
                await asyncio.wait_for(
                    mounter.mount(image, build_manifest, trustcache),
                    timeout=PERSONALIZED_DDI_MOUNT_TIMEOUT_SECONDS,
                )
        except AlreadyMountedError:
            # A concurrent Apple/Xcode client may have completed the mount
            # between our preflight and this request.
            pass
        except DeveloperModeIsNotEnabledError as error:
            raise BridgePrerequisiteError(
                'developer_mode_required',
                'Developer Mode must remain enabled while mounting the Personalized DDI.',
            ) from error
        except asyncio.TimeoutError as error:
            raise BridgePrerequisiteError(
                'developer_image_mount_timeout',
                f'Mounting the {source} Personalized DDI timed out after 180 seconds.',
            ) from error
        except Exception as error:
            if source == 'automatic':
                code = 'developer_image_tss_failed'
                context = 'Apple personalization and mounting of the downloaded'
            else:
                code = 'developer_image_mount_failed'
                context = 'mounting of the local'
            raise BridgePrerequisiteError(
                code,
                f'Unable to complete {context} Personalized DDI: '
                f'{type(error).__name__}: {str(error)[:180]}',
            ) from error

        if not await self._is_personalized_ddi_mounted(lockdown):
            raise BridgePrerequisiteError(
                'developer_image_mount_failed',
                f'The {source} Personalized DDI mount did not become active on the device.',
            )

    async def _refresh_personalized_ddi(self, lockdown) -> None:
        await self._emit_status('remounting_developer_image')
        try:
            async with PersonalizedImageMounter(lockdown=lockdown) as mounter:
                await asyncio.wait_for(
                    mounter.umount(), timeout=PERSONALIZED_DDI_REMOUNT_TIMEOUT_SECONDS)
        except NotMountedError:
            # Another client may have removed the image while the failed tunnel
            # was closing. The normal mount path below can safely continue.
            pass
        except asyncio.TimeoutError as error:
            raise BridgePrerequisiteError(
                'developer_image_remount_failed',
                'Unmounting the stale Personalized DDI timed out after 30 seconds.',
            ) from error
        except Exception as error:
            raise BridgePrerequisiteError(
                'developer_image_remount_failed',
                f'Unable to remove the stale Personalized DDI: {type(error).__name__}: {str(error)[:180]}',
            ) from error
        await self._mount_personalized_ddi(lockdown)

    @staticmethod
    async def _is_personalized_ddi_mounted(lockdown) -> bool:
        """Cross-check LookupImage with CopyDevices when the device supports it.

        iOS can report a Personalized image from LookupImage while its mounted
        image inventory is empty. Preserve the positive result so the recovery
        path can unmount it after a failed HID probe, but record the mismatch
        instead of treating it as a proof that the DDI is usable.
        """
        async with PersonalizedImageMounter(lockdown=lockdown) as mounter:
            mounted = await mounter.is_image_mounted(PersonalizedImageMounter.IMAGE_TYPE)
            copy_devices = getattr(mounter, 'copy_devices', None)
            if not callable(copy_devices):
                return mounted
            try:
                devices = await asyncio.wait_for(
                    copy_devices(), timeout=PERSONALIZED_DDI_INVENTORY_TIMEOUT_SECONDS)
            except Exception as error:
                log.info('Unable to inspect Personalized DDI inventory: %s: %s',
                         type(error).__name__, str(error)[:180])
                return mounted
            personalized = [device for device in devices if isinstance(device, dict) and
                            device.get('DiskImageType') == PersonalizedImageMounter.IMAGE_TYPE]
            if mounted and not personalized:
                log.warning('Personalized DDI LookupImage is mounted but CopyDevices is empty')
            elif personalized and not mounted:
                log.warning('CopyDevices reports a Personalized DDI that LookupImage did not confirm')
            return mounted or bool(personalized)

    @staticmethod
    def _connected_service_ids(surfaces: object) -> set[int]:
        """Extract advertised _ServiceID values from an RSD HID response."""
        ids: set[int] = set()

        def visit(value: object) -> None:
            if isinstance(value, dict):
                service_id = value.get('_ServiceID')
                if isinstance(service_id, int) and not isinstance(service_id, bool):
                    ids.add(service_id)
                elif isinstance(service_id, dict):
                    raw = service_id.get('uint')
                    if isinstance(raw, int) and not isinstance(raw, bool):
                        ids.add(raw)
                for child in value.values():
                    visit(child)
            elif isinstance(value, (list, tuple)):
                for child in value:
                    visit(child)

        visit(surfaces)
        return ids

    async def _verify_touch_surface(self, hid) -> None:
        """Require the real mainTouchscreen surface before reporting ready."""
        surfaces = await asyncio.wait_for(hid.list_connected_services(), 8)
        ids = self._connected_service_ids(surfaces)
        log.info('service=%s surfaces=%s', hid.SERVICE_NAME, sorted(ids))
        # iOS publishes a keyboard surface on some DDIs (service 512). In
        # direct-HID mode registering a second virtual service can be rejected
        # even though the published surface accepts the standard keyboard
        # report. Prefer the device-owned surface when it is available.
        if KEYBOARD_SURFACE_CONNECTED in ids:
            self.keyboard_service_id = KEYBOARD_SURFACE_CONNECTED
        if DIGITIZER_SURFACE_MAIN_TOUCHSCREEN in ids:
            return
        advertised = ', '.join(str(service_id) for service_id in sorted(ids)) or 'none'
        raise BridgePrerequisiteError(
            'touch_surface_unavailable',
            'Universal HID did not advertise the mainTouchscreen surface '
            f'{DIGITIZER_SURFACE_MAIN_TOUCHSCREEN}; advertised surfaces: {advertised}.',
        )

    async def _connect_with_lockdown(self, lockdown) -> None:
        if self.udid is None:
            self.udid = lockdown.udid
        if self.transport_mode == 'usb' and not self._remote_pairing_provision_attempted:
            # Best-effort only: wired control must never fail because optional
            # future wireless provisioning is unavailable on this device.
            self._remote_pairing_provision_attempted = True
            await self._provision_remote_pairing(lockdown)
        await self._preflight_developer_environment(lockdown)
        service = await CoreDeviceTunnelProxy.create(lockdown)
        async with start_tunnel(service, protocol=TunnelProtocol.TCP) as tunnel_result:
            await self._connect_with_tunnel_result(tunnel_result)

    async def _connect_with_tunnel_result(self, tunnel_result) -> None:
        """Open RSD and HID using either a USB or RemotePairing TCP tunnel."""
        from pymobiledevice3.remote.userspace_tunnel import UserspaceDialPlane

        tun = tunnel_result.client.tun
        if not hasattr(tun, 'set_peer'):
            raise BridgePrerequisiteError(
                'userspace_tunnel_unavailable',
                'The selected CoreDevice tunnel did not provide the required userspace network stack.',
            )
        tun.set_peer(tunnel_result.address)
        self.dial_plane = UserspaceDialPlane(tun, tunnel_result.address)
        await self.dial_plane.__aenter__()
        try:
            self.rsd = RemoteServiceDiscoveryService(
                (tunnel_result.address, tunnel_result.port),
                open_connection=self.dial_plane.dial,
            )
            await self.rsd.__aenter__()
            # HID reports are accepted only while the media-stream auth gate
            # is held. Some recent systems instead expose a verified direct
            # Universal HID service, handled below.
            try:
                async with touch_session(self.rsd) as hid:
                    self.hid = hid
                    await self._verify_touch_surface(hid)
                    await self.ipc.emit({'event': 'status', 'code': 'hid_service_selected',
                                         'message': hid.SERVICE_NAME})
                    self.auth_mode = 'mediastream'
                    self.gate_open = True
                    await self._emit_ready()
                    await self._serve()
            except Exception as error:
                if self._can_fallback_to_direct_hid(error):
                    await self._enable_direct_hid_fallback(error)
                    await self._emit_ready()
                    await self._serve()
                    return
                # Some supported iOS builds do not publish DisplayService at
                # first, and touch_session can also report a late HID service
                # registration as InvalidServiceError. Retry only these
                # service-registration cases through explicit HID.
                if not self._is_optional_session_failure(error):
                    raise
                await self.ipc.emit({'event': 'warning', 'code': 'gate_unavailable',
                                     'message': f'touch authentication gate unavailable; continuing HID: {str(error)[:180]}'})
                await self._initialize_touch_with_retry()
                self.auth_mode = 'mediastream' if self.gate_open else None
                await self._emit_ready()
                await self._serve()
        finally:
            await self._cleanup()

    async def _init_touch(self) -> None:
        await self._emit_status('initializing_touch')
        services = (self.rsd.peer_info or {}).get('Services', {})
        modern_name = UniversalHIDServiceService.SERVICE_NAME
        hid_types = [UniversalHIDServiceService, LegacyUniversalHIDServiceService]
        if modern_name not in services and LEGACY_UNIVERSAL_HID_SERVICE in services:
            hid_types.reverse()

        last_unavailable = None
        for hid_type in hid_types:
            hid = hid_type(self.rsd)
            try:
                await hid.__aenter__()
                await self._verify_touch_surface(hid)
            except Exception as error:
                with contextlib.suppress(Exception):
                    await hid.__aexit__(None, None, None)
                if not self._is_hid_service_unavailable(error):
                    raise
                last_unavailable = error
                continue

            self.hid = hid
            await self.ipc.emit({'event': 'status', 'code': 'hid_service_selected',
                                 'message': hid.SERVICE_NAME})
            return

        if last_unavailable is not None:
            raise last_unavailable
        raise RuntimeError('No compatible Universal HID service was available')

    @staticmethod
    def _is_hid_service_unavailable(error: Exception) -> bool:
        detail = str(error).casefold()
        return 'no such service' in detail or 'no_such_service' in detail

    @staticmethod
    def _is_remote_control_unsupported_ios(error: Exception) -> bool:
        return '9021' in str(error)

    @staticmethod
    def _can_fallback_to_direct_hid(error: Exception) -> bool:
        """Whether a media-session failure still permits a direct HID path.

        Recent Personalized DDIs reject startmediastream with 9021 on older
        iOS versions, while still publishing Universal HID.  A missing display
        service behaves the same way.  The direct path below still verifies
        the actual HID service before declaring readiness.
        """
        detail = str(error).casefold()
        return TouchSession._is_remote_control_unsupported_ios(error) or (
            'com.apple.coredevice.displayservice' in detail or
            ('invalidserviceerror' in detail and 'display' in detail)
        )

    @staticmethod
    def _is_optional_session_failure(error: Exception) -> bool:
        """Return true when touch_session failed in an optional gate step.

        The bundled pymobiledevice3 touch_session opens DisplayService before
        Universal HID. On devices without that service, the exception occurs
        before our own fallback code can run. Keep the fallback narrow enough
        to avoid hiding tunnel or protocol failures.
        """
        detail = str(error).casefold()
        return not TouchSession._is_remote_control_unsupported_ios(error) and (
            'startmediastream' in detail
            or 'com.apple.coredevice.displayservice' in detail
            or ('invalidserviceerror' in detail and
                ('display' in detail or 'hid' in detail or 'universal' in detail))
            or 'com.apple.coredevice.hid.universalhidservice' in detail
        )

    async def _enable_direct_hid_fallback(self, error: Exception) -> None:
        await self.ipc.emit({
            'event': 'warning',
            'code': 'direct_hid_fallback',
            'message': (
                'Media-stream authentication is unavailable; verifying direct '
                f'Universal HID instead: {str(error)[:180]}'
            ),
        })
        await self._initialize_touch_with_retry(open_media_gate=False)
        self.auth_mode = 'direct'
        # In direct mode the DDI-published HID service is the authenticated
        # path. Keep the existing gateOpen field true so older hosts accept a
        # verified direct session, and advertise authMode for newer callers.
        self.gate_open = True

    async def _initialize_touch_with_retry(self, open_media_gate: bool = True) -> None:
        gate_attempted = False
        for attempt in range(3):
            try:
                await self._init_touch()
                break
            except Exception as error:
                if not self._is_hid_service_unavailable(error) or attempt == 2:
                    services = self._advertised_hid_services()
                    await self._emit_hid_service_inventory(services)
                    if self._is_hid_service_unavailable(error):
                        advertised = ', '.join(services) or 'none'
                        raise BridgePrerequisiteError(
                            'touch_surface_unavailable',
                            'RSD did not advertise a usable Universal HID service; '
                            f'advertised HID services: {advertised}.',
                        ) from error
                    raise
                if open_media_gate and not gate_attempted:
                    await self._open_gate()
                    gate_attempted = True
                await self.ipc.emit({
                    'event': 'status', 'code': 'waiting_for_hid_service',
                    'message': f'Universal HID service is still registering; retry {attempt + 1}/2',
                })
                await asyncio.sleep(1)
        if open_media_gate and not gate_attempted:
            await self._open_gate()
        # Universal HID is independently authenticated by _init_touch.  The
        # optional media-stream gate can be unavailable on some iOS builds;
        # do not reject an otherwise verified direct HID session.
        if self.hid is not None and not self.gate_open:
            self.auth_mode = 'direct'
            self.gate_open = True
            await self.ipc.emit({
                'event': 'warning',
                'code': 'direct_hid_fallback',
                'message': 'Media-stream authentication unavailable; continuing with verified Universal HID.',
            })

    def _advertised_hid_services(self) -> list[str]:
        services = (self.rsd.peer_info or {}).get('Services', {}) if self.rsd is not None else {}
        if not isinstance(services, dict):
            return []
        return sorted(name for name in services if isinstance(name, str) and 'hid' in name.casefold())

    async def _emit_hid_service_inventory(self, services: Optional[list[str]] = None) -> None:
        if services is None:
            services = self._advertised_hid_services()
        await self.ipc.emit({
            'event': 'warning', 'code': 'hid_service_inventory',
            'message': f'product={getattr(self.rsd, "product_type", "unknown")}; '
                       f'ios={getattr(self.rsd, "product_version", "unknown")}; '
                       f'services={services}',
        })

    async def _open_gate(self) -> None:
        """尝试 startmediastream 持有 backboardd auth gate。

        iOS < 27.0 may return 9021.  The caller can fall back to a direct
        Universal HID session when that service is actually available.
        """
        display = DisplayService(self.rsd)
        try:
            await display.__aenter__()
        except Exception as error:
            # DisplayService is only an optional authentication-gate probe;
            # Universal HID was already initialized and remains usable.
            self.display = None
            await self.ipc.emit({'event': 'warning', 'code': 'gate_unavailable',
                                 'message': f'display service unavailable; continuing HID control: {type(error).__name__}'})
            if self.hid is not None:
                self.auth_mode = 'direct'
                self.gate_open = True
            return
        self.display = display
        try:
            from pymobiledevice3.remote.core_device.screen_stream import open_media_receiver
            self.transport, receiver_ip = open_media_receiver(self.display, (1 * 1024 * 1024,))
            sender_ip = self.rsd.service.address[0]
            self.stream_answer = await asyncio.wait_for(
                self.display.start_video_stream(
                    receiver_ip=receiver_ip,
                    receiver_port=self.transport.port,
                    sender_ip=sender_ip,
                    display_id=1,
                ),
                timeout=10.0,
            )
            self.gate_open = True
            self.drain_task = asyncio.create_task(self._drain())
            await asyncio.sleep(0.3)
        except asyncio.TimeoutError:
            await self.ipc.emit({'event': 'warning', 'code': 'gate_timeout',
                                 'message': 'startmediastream timed out; reports may be dropped'})
        except Exception as error:
            if self._is_remote_control_unsupported_ios(error):
                raise BridgePrerequisiteError(
                    'remote_control_unsupported_ios',
                    'The device rejected media-stream authentication (9021) and '
                    'did not provide a usable direct Universal HID touch surface.',
                ) from error
            msg = str(error)
            await self.ipc.emit({'event': 'warning', 'code': 'gate_failed',
                                 'message': f'startmediastream failed: {type(error).__name__}: {msg[:200]}'})
        if self.hid is not None and not self.gate_open:
            self.auth_mode = 'direct'
            self.gate_open = True

    async def _emit_ready(self) -> None:
        if not self.gate_open and self.hid is None:
            raise BridgePrerequisiteError(
                'remote_control_gate_closed',
                'The media-stream authentication gate is closed; the bridge will not claim ready.',
            )
        await self.ipc.emit({
            'event': 'ready', 'protocol': PROTOCOL_VERSION,
            'capabilities': CAPABILITIES, 'udid': self.udid, 'rateHz': self.rate_hz,
            'gateOpen': self.gate_open, 'authMode': self.auth_mode,
            'transport': self.transport_mode,
        })

    async def _drain(self) -> None:
        try:
            while True:
                await self.transport.recv()
        except (asyncio.CancelledError, OSError):
            pass

    async def _serve(self) -> None:
        sm = FiveSlotStateMachine()
        async for frame in self.ipc.read_messages():
            try:
                if frame.get('kind') == KEYBOARD_MESSAGE_KIND:
                    _, ts, usages = decode_keyboard_batch(frame)
                    await self._apply_keyboard(frame, ts, usages)
                elif frame.get('kind') == BUTTON_MESSAGE_KIND:
                    _, page, code, state = decode_button_event(frame)
                    await self._apply_button(page, code, state)
                else:
                    _, _, points = decode_touch_batch(frame)
                    await self._apply_frame(sm, frame, points)
            except Exception as e:
                await self.ipc.emit({'event': 'error', 'code': 'send_failed',
                                     'message': f'{type(e).__name__}: {str(e)[:200]}'})
                break

    async def _apply_keyboard(self, frame: dict, timestamp: Optional[int], usages: list[int]) -> None:
        if timestamp is not None:
            # Host timestamps are Unix nanoseconds, while the Universal HID
            # keyboard report reserves six bytes for a Mach-absolute value.
            timestamp = int(timestamp) & ((1 << 48) - 1)
        if self.keyboard_service_id is None:
            # Register the virtual keyboard through the same public API used by
            # The service ID is device-specific; do not assume that a requested
            # value was accepted until the device confirms it.
            # accepted: dtuhidd may allocate a different ID per session/device.
            self.keyboard_service_id = await self.hid.create_keyboard_service(
                product='iPhoneMirror virtual keyboard', manufacturer='iPhoneMirror')
            await self.ipc.emit({'event': 'status', 'code': 'keyboard_service_ready',
                                 'message': str(self.keyboard_service_id)})
        # send_keyboard builds the report using the active pymobiledevice3
        # implementation and addresses the registered service consistently.
        await self.hid.send_keyboard(self.keyboard_service_id, usages, timestamp)

    async def _apply_button(self, usage_page: int, usage_code: int, state: str) -> None:
        if self.indigo is None:
            self.indigo = IndigoHIDService(self.rsd)
            await self.indigo.__aenter__()
        state_code = {'down': 1, 'up': 2, 'canceled': 3}[state]
        await self.indigo.send_button(usage_page, usage_code, state_code)

    async def _apply_frame(self, sm: FiveSlotStateMachine, frame: dict, points: list[dict]) -> None:
        ts = frame.get('timestampNs')
        if ts is not None:
            ts = int(ts) & ((1 << 48) - 1)
        for touch_point in points:
            pointer_id = int(touch_point['pointerId'])
            action = touch_point['action']
            x = int(round(float(touch_point['normalizedX']) * 65535))
            y = int(round(float(touch_point['normalizedY']) * 65535))
            if action == 'down':
                slot = sm.assign(pointer_id)
                if slot is None:
                    continue
                report = build_touchscreen_report(slot, TOUCHSCREEN_STATE_CONTACT, x, y, ts)
                await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)
            elif action == 'move':
                slot = sm.slot_for(pointer_id)
                if slot is None:
                    continue
                report = build_touchscreen_report(slot, TOUCHSCREEN_STATE_CONTACT, x, y, ts)
                await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)
            elif action == 'up':
                slot = sm.release(pointer_id)
                if slot is None:
                    continue
                report = build_touchscreen_report(slot, TOUCHSCREEN_STATE_RELEASE, x, y, ts)
                await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)

    async def _cleanup(self) -> None:
        # 强制释放所有触点（异常清理）
        if self.hid is not None:
            try:
                if self.keyboard_service_id is not None:
                    await self.hid.send_keyboard(self.keyboard_service_id, [])
            except Exception:
                pass
        if self.indigo is not None:
            try:
                await self.indigo.__aexit__(None, None, None)
            except Exception:
                pass
            self.indigo = None
            try:
                for slot in range(MAX_SLOTS):
                    report = build_touchscreen_report(slot, TOUCHSCREEN_STATE_RELEASE, 0, 0)
                    await self.hid.send_report(DIGITIZER_SURFACE_MAIN_TOUCHSCREEN, report)
            except Exception:
                pass
            try:
                await self.hid.__aexit__(None, None, None)
            except Exception:
                pass
        if self.drain_task is not None:
            self.drain_task.cancel()
            try:
                await self.drain_task
            except asyncio.CancelledError:
                pass
            except Exception:
                pass
        self.keyboard_service_id = None
        if self.stream_answer is not None and self.display is not None:
            try:
                import uuid as _uuid
                csid = self.stream_answer['connection']['options']['avcMediaStreamOptionClientSessionID']['uuid']
                if not isinstance(csid, _uuid.UUID):
                    csid = _uuid.UUID(csid)
                with __import__('contextlib').suppress(Exception):
                    await self.display.stop_media_stream(csid)
            except Exception:
                pass
        if self.transport is not None:
            try:
                self.transport.close()
            except Exception:
                pass
        if self.display is not None:
            try:
                await self.display.__aexit__(None, None, None)
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
        self.hid = None
        self.rsd = None
        self.display = None
        self.dial_plane = None
        self.stream_answer = None
        self.drain_task = None
        self.transport = None
        if self._usb_mux_server is not None:
            with contextlib.suppress(Exception):
                self._usb_mux_server.stop()
            self._usb_mux_server = None
        if self._usb_mux_transport is not None:
            with contextlib.suppress(Exception):
                self._usb_mux_transport.close()
            self._usb_mux_transport = None
        if self._usb_mux_previous_env is None:
            os.environ.pop('USBMUXD_SOCKET_ADDRESS', None)
        else:
            os.environ['USBMUXD_SOCKET_ADDRESS'] = self._usb_mux_previous_env
        self._usb_mux_previous_env = None
        self.gate_open = False
        self.auth_mode = None


async def main_async(rate_hz: int, udid: Optional[str], transport: str,
                     ddi_dir: Optional[Path] = None) -> None:
    ipc = BridgeChannel()
    session = TouchSession(ipc, rate_hz, udid, transport, ddi_dir)
    try:
        await session.connect()
    except Exception as e:
        await ipc.emit({'event': 'error', 'code': bridge_error_code(e),
                        'message': str(e)[:300]})
        await ipc.emit({'event': 'status', 'code': 'terminated'})


async def enable_wifi_sync_async(udid: str) -> bool:
    ipc = BridgeChannel()
    lockdown = None
    try:
        await ipc.emit({'event': 'status', 'code': 'enabling_wifi_sync'})
        lockdown = await create_lockdown_with_retry(ipc, udid, 'USB')
        was_enabled = await get_wifi_sync_enabled(lockdown)
        if not was_enabled:
            await lockdown.set_enable_wifi_connections(True)
        enabled = await lockdown.get_enable_wifi_connections()
        if not enabled:
            raise RuntimeError('device rejected EnableWifiConnections=true')
        await ipc.emit({'event': 'wifi_sync_enabled', 'udid': lockdown.udid,
                        'changed': not was_enabled})
        return True
    except Exception as error:
        await ipc.emit({'event': 'error', 'code': bridge_error_code(error),
                        'message': str(error)[:300]})
        return False
    finally:
        if lockdown is not None:
            with contextlib.suppress(Exception):
                await lockdown.close()


async def get_wifi_sync_enabled(lockdown) -> bool:
    try:
        return await lockdown.get_enable_wifi_connections()
    except MissingValueError:
        # A device that has never enabled Wi-Fi sync has no value yet.
        # Treat the absent key as false so the caller creates it.
        return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--rate-hz', type=int, default=120)
    parser.add_argument('--udid', default=None)
    parser.add_argument('--ddi-dir', type=Path, default=None,
                        help='仅在未挂载镜像时使用此本地 Personalized DDI 目录')
    transport = parser.add_mutually_exclusive_group()
    transport.add_argument('--usb', action='store_const', const='usb',
                           dest='transport', help='仅连接物理 USB 设备（默认）')
    transport.add_argument('--wireless', action='store_const', const='wireless',
                           dest='transport', help='仅连接已通过 usbmux 配对的无线设备')
    parser.add_argument('--enable-wifi-sync', action='store_true',
                        help='通过 USB 为指定设备启用 Apple Wi-Fi 同步')
    parser.set_defaults(transport='usb')
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(message)s',
                        stream=sys.stderr)
    try:
        if args.enable_wifi_sync:
            if not args.udid:
                parser.error('--enable-wifi-sync requires --udid')
            return 0 if asyncio.run(enable_wifi_sync_async(args.udid)) else 1
        asyncio.run(main_async(args.rate_hz, args.udid, args.transport, args.ddi_dir))
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
