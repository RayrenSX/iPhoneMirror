#!/usr/bin/env python3
"""Download iPhone/iPad identifiers and Apple display specs; generate offline tables.

Only metadata and specification HTML are downloaded, never firmware or images.
Use --offline with a populated cache to reproduce the generated catalog.
Use --check to verify committed JSON/header consistency without network access.
"""
import argparse
import ast
import base64
from concurrent.futures import ThreadPoolExecutor
from datetime import date
import hashlib
import html
import json
from pathlib import Path
import re
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'config/apple-display-catalog.json'
HEADER = ROOT / 'src/Shared/Devices/AppleDisplayCatalog.generated.h'
DOCUMENTATION = ROOT / 'docs/APPLE_DISPLAY_CATALOG.md'
DEVICEKIT = 'https://api.github.com/repos/devicekit/DeviceKit/contents/Source/Device.swift.gyb'
IPSW = 'https://api.ipsw.me/v4/devices'
LEGACY = [
    ('iPhoneOriginal', 'iPhone', ['iPhone1,1'], 'https://support.apple.com/kb/SP2'),
    ('iPhone3G', 'iPhone 3G', ['iPhone1,2'], 'https://support.apple.com/kb/SP495'),
    ('iPhone3GS', 'iPhone 3GS', ['iPhone2,1'], 'https://support.apple.com/kb/SP565'),
    ('iPadOriginal', 'iPad', ['iPad1,1'], 'https://support.apple.com/kb/SP580'),
]
# Recent products not yet present in DeviceKit. Their identifiers are checked
# against IPSW below and their actual panel data still comes from Apple.
SUPPLEMENTAL = [
    ('iPadMiniA20Pro', 'iPad mini (A20 Pro)', ['iPad19,1', 'iPad19,2'], 'https://support.apple.com/en-us/149482'),
]
# DeviceKit currently links both M5 sizes to the 11-inch article. The 13-inch
# title and dimensions are checked from Apple's separate official article.
SPEC_URL_OVERRIDES = {'iPadPro13M5': 'https://support.apple.com/en-us/125407',
                      'iPhoneDuo': 'https://support.apple.com/en-us/148588'}
DIMENSIONS = re.compile(r'(\d[\d,]{2,})\s*[-\u2010-\u2015]?\s*(?:by|×|x)\s*[-\u2010-\u2015]?\s*(\d[\d,]{2,})\s*[-\u2010-\u2015]?\s*(?:pixel)?', re.I)


def text_content(data):
    text = data.decode('utf-8-sig')
    text = re.sub(r'<(?:script|style)\b[^>]*>.*?</(?:script|style)>', ' ', text, flags=re.S | re.I)
    return re.sub(r'\s+', ' ', html.unescape(re.sub(r'<[^>]+>', ' ', text))).strip()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def download(url, target, offline):
    if target.exists():
        return target.read_bytes()
    if offline:
        raise RuntimeError(f'missing cached source: {target}')
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={
                'User-Agent': 'iPhoneMirror-display-catalog', 'Accept-Language': 'en-US,en;q=0.9'})
            with urllib.request.urlopen(request, timeout=20) as response:
                data = response.read(4 * 1024 * 1024 + 1)
            if len(data) > 4 * 1024 * 1024:
                raise ValueError('specification source exceeds size limit')
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            return data
        except Exception:
            if attempt == 2:
                raise
            time.sleep(.2)


def products_from_devicekit(template):
    block = template.decode('utf-8-sig').split('%{', 1)[1].split('}%', 1)[0]
    products = []
    # Parse constant Device(...) arguments; downloaded Python is never run.
    for node in ast.parse(block).body:
        if not isinstance(node, ast.Assign) or not any(
                isinstance(target, ast.Name) and target.id in ('iPhones', 'iPads') for target in node.targets):
            continue
        for call in node.value.elts:
            if not isinstance(call, ast.Call) or not isinstance(call.func, ast.Name) or call.func.id != 'Device':
                raise ValueError('unexpected DeviceKit model entry')
            args = [ast.literal_eval(argument) for argument in call.args]
            source = re.search(r'https://(?:support\.apple\.com|www\.apple\.com)/[^)\s]+', args[1])
            if source is None:
                raise ValueError(f'no Apple specification link for {args[0]}')
            products.append((args[0], args[6], args[3], SPEC_URL_OVERRIDES.get(args[0], source.group())))
    covered = {identifier for product in products for identifier in product[2]}
    return products + LEGACY + [product for product in SUPPLEMENTAL if not set(product[2]).intersection(covered)]


def parse_spec(product, data):
    key, name, identifiers, url = product
    raw = data.decode('utf-8-sig')
    titles = re.findall(r'<title\b[^>]*>(.*?)</title>', raw, re.S | re.I)
    if not titles:
        raise ValueError(f'Apple article has no title: {url}')
    title = html.unescape(titles[0])
    family = 'iPad' if identifiers[0].startswith('iPad') else 'iPhone'
    if family.casefold() not in title.casefold():
        raise ValueError(f'wrong device family in Apple article: {name}: {title}')
    if 'M5' in key and ('13' if '13' in key else '11') not in title:
        raise ValueError(f'wrong M5 display size in Apple article: {name}: {title}')
    text = text_content(data)
    # The single-table USB request uses Duo's closed/outer panel. Do not take
    # the first resolution on its two-display spec page (the inner panel).
    scope_start, scope_end = 0, len(text)
    if key == 'iPhoneDuo':
        scope_start = text.find('Outer display')
        scope_end = text.find('Both displays', scope_start)
        if scope_start < 0 or scope_end <= scope_start:
            raise ValueError('iPhone Duo outer display specification is missing')
    candidates = []
    for match in DIMENSIONS.finditer(text):
        if not scope_start <= match.start() < scope_end:
            continue
        first, second = (int(value.replace(',', '')) for value in match.groups())
        if min(first, second) < 320 or max(first, second) > 8192:
            continue
        context = text[max(0, match.start() - 100):match.end() + 130]
        # Display specs precede camera/video formats on these official tech
        # pages. Require resolution/ppi language, not any pair of dimensions.
        following = text[match.end():match.end() + 100].casefold()
        if 'resolution' not in following and 'ppi' not in following:
            continue
        candidates.append((min(first, second), max(first, second), context))
    if not candidates:
        raise ValueError(f'no display resolution found in Apple article: {name}: {url}')
    width, height, quote = candidates[0]
    return {'name': name, 'family': family, 'identifiers': identifiers,
            'width': width, 'height': height, 'longEdge': height,
            'appleSpecUrl': url, 'appleSpecTitle': title,
            'appleSpecSha256': digest(data), 'displayQuote': quote}


def validate(catalog):
    identities = set()
    for product in catalog['products']:
        width, height = product['width'], product['height']
        if not 320 <= width <= height <= 8192 or product['longEdge'] != height:
            raise ValueError(f'invalid physical display geometry: {product["name"]}')
        for identifier in product['identifiers']:
            if not re.fullmatch(r'(iPhone|iPad)\d+,\d+', identifier) or identifier in identities:
                raise ValueError(f'invalid or duplicate identifier: {identifier}')
            identities.add(identifier)
    if len(identities) != catalog['coverage']['identifierCount']:
        raise ValueError('catalog identifier count does not match coverage record')
    return identities


def generated_header(catalog):
    rows = [(identifier, product['width'], product['height'])
            for product in catalog['products'] for identifier in product['identifiers']]
    rows.sort(key=lambda row: (row[0].split(',')[0], int(row[0].split(',')[1])))
    lines = ['// Generated by scripts/update_apple_display_catalog.py. Do not edit.',
             '// Physical panel pixels from Apple specifications; never a promise of stream resolution.',
             '#pragma once', '#include <algorithm>', '#include <cstdint>', '#include <string_view>',
             'namespace iPhoneMirror::devices {',
             'struct AppleDisplay { std::wstring_view product_type; std::uint32_t width, height; };',
             'inline constexpr AppleDisplay AppleDisplays[] = {']
    lines += [f'    {{L"{identifier}", {width}, {height}}},' for identifier, width, height in rows]
    lines += ['};', '[[nodiscard]] constexpr const AppleDisplay* find_apple_display(std::wstring_view model) noexcept {',
              '    for (const auto& display : AppleDisplays) if (display.product_type == model) return &display;',
              '    return nullptr;', '}',
              '[[nodiscard]] constexpr bool is_apple_display_long_edge(std::uint32_t edge) noexcept {',
              '    for (const auto& display : AppleDisplays) if (display.height == edge) return true;',
              '    return false;', '}', '} // namespace iPhoneMirror::devices', '']
    return '\n'.join(lines)


def generated_documentation(catalog):
    coverage = catalog['coverage']
    lines = ['# iPhone / iPad 面板分辨率目录', '',
        f'采集日期：{catalog["retrievedOn"]}。覆盖当日型号索引中的全部 **{coverage["identifierCount"]} 个 ProductType**，'
        f'对应 **{coverage["productCount"]} 份 Apple 官方产品规格**。只包含 iPhone、iPad。', '',
        '这里的尺寸是正常竖屏的物理面板像素，不是逻辑点数，也不是 USB/AirPlay 编码器必然输出的尺寸。'
        '收录旧设备的规格不代表新增了这些设备的投屏协议支持。只有实际接收并解码的帧才能证明输出达到了原生像素。', '',
        '型号标识由 [DeviceKit](https://github.com/devicekit/DeviceKit) 提供，并与 '
        '[IPSW.me 型号索引](https://api.ipsw.me/v4/devices) 逐项对照；显示像素逐项来自下表的 Apple 官方规格页。'
        'iPad Pro M5 两种尺寸使用不同的官方规格页，修正了上游 13 英寸指向 11 英寸页面的问题。', '',
        '新增型号若尚未进入 DeviceKit，则由更新脚本明确补录并与 IPSW 索引核对。'
        'iPhone Duo 的单项规格采用合上时的外屏，型号识别不表示已支持折叠内外屏自动切换。', '',
        '统一数据位于 [apple-display-catalog.json](../config/apple-display-catalog.json)，'
        '其中保留每份规格页的标题、显示参数原文片段和 SHA256。完整下载的原始规格页保存在更新脚本的本地缓存，'
        '不下载固件、图片或整个网站。应用运行时使用随包内置数据，不联网查表。', '',
        '## 横竖屏与实际输出', '',
        '- 无线：设置中的原画档按面板分辨率分组，接收能力始终是“长边×长边”，横竖屏共用同一档。'
        '保留已有档位及默认 1080p；其他型号可选对应的新原画尺寸。首次发现发送端时尚未得到其型号，'
        '不会声称在发现之前已自动识别并替它修改协商。',
        '- 有线 AirPlay 推荐模式：iPhone/iPad 均按完整 ProductType 匹配面板，原生 presentation 使用至少 4096×4096 的默认对称能力，'
        '自定义宽高取长边构造对称请求。能力范围不等于源帧分辨率，正常旋转无需重启 USB。',
        '- 无线展示宽高以新解码帧为准，并随图像一同替换；有线原生 presentation 使用只读系统方向。'
        '屏幕旋转不会把型号表尺寸强行覆盖到实际视频帧上。当前模式与旧 A/B/C 的区别见[协议说明](PROTOCOL.md#当前有线模式与尺寸协商)。',
        '- 日志分别记录型号、面板尺寸和解码尺寸，未知型号保持兼容回退，不把回退值称为真实原生尺寸。', '',
        '## 产品规格', '', '| 产品 | 型号标识 | 物理像素（宽×高） | 长边 | 官方来源 |',
        '| --- | --- | --- | --- | --- |']
    for product in catalog['products']:
        lines.append(f'| {product["name"]} | {", ".join(product["identifiers"])} | '
            f'{product["width"]}×{product["height"]} | {product["longEdge"]} | [Apple]({product["appleSpecUrl"]}) |')
    lines += ['', '## 更新与校验', '', '```powershell',
              'python scripts/update_apple_display_catalog.py',
              'python scripts/update_apple_display_catalog.py --check', '```', '',
              '更新脚本会拒绝型号重复、索引覆盖缺失、无官方显示像素或原生头文件与目录不一致的结果。'
              '需要重新下载上游最新数据时，指定新的 `--cache-dir`；使用已有缓存可加 `--offline` 复现。', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache-dir', type=Path, default=ROOT / 'work/apple-display-catalog-cache')
    parser.add_argument('--offline', action='store_true')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.check:
        catalog = json.loads(CATALOG.read_text(encoding='utf-8'))
        validate(catalog)
        if HEADER.read_text(encoding='utf-8') != generated_header(catalog):
            raise ValueError('native header is out of sync with Apple display catalog')
        if DOCUMENTATION.read_text(encoding='utf-8') != generated_documentation(catalog):
            raise ValueError('display documentation is out of sync with the catalog')
        print(f'Apple catalog verified: {len(catalog["products"])} products, {catalog["coverage"]["identifierCount"]} identifiers')
        return
    cache = args.cache_dir
    api_data = download(DEVICEKIT, cache / 'devicekit-api.json', args.offline)
    api = json.loads(api_data)
    template = base64.b64decode(api['content'], validate=False)
    index_data = download(IPSW, cache / 'ipsw-devices.json', args.offline)
    index = {item['identifier'] for item in json.loads(index_data) if item['identifier'].startswith(('iPhone', 'iPad'))}
    products = products_from_devicekit(template)
    def load(product):
        url = product[3]
        path = cache / 'apple-specs' / (digest(url.encode())[:16] + '.html')
        return parse_spec(product, download(url, path, args.offline))
    with ThreadPoolExecutor(max_workers=8) as pool:
        displays = list(pool.map(load, products))
    present = {identifier for display in displays for identifier in display['identifiers']}
    if present != index:
        raise ValueError(f'identifier coverage mismatch: missing={sorted(index-present)} extra={sorted(present-index)}')
    catalog = {'schema': 1, 'retrievedOn': date.today().isoformat(),
               'scope': ['iPhone', 'iPad'], 'units': 'physical panel pixels in portrait orientation',
               'sources': [{'url': DEVICEKIT, 'gitBlobSha': api['sha'], 'sha256': digest(template)},
                           {'url': IPSW, 'sha256': digest(index_data)}],
               'coverage': {'identifierCount': len(index), 'productCount': len(displays), 'missingIdentifiers': []},
               'products': sorted(displays, key=lambda display: display['identifiers'][0])}
    validate(catalog)
    CATALOG.parent.mkdir(parents=True, exist_ok=True)
    HEADER.parent.mkdir(parents=True, exist_ok=True)
    CATALOG.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    HEADER.write_text(generated_header(catalog), encoding='utf-8')
    DOCUMENTATION.write_text(generated_documentation(catalog), encoding='utf-8')
    print(f'Generated {len(displays)} product specifications / {len(index)} iPhone and iPad identifiers')


if __name__ == '__main__':
    main()
