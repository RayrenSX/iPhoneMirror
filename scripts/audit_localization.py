"""Audit first-party UI resources and generate the complete multilingual matrix.

Run: python scripts/audit_localization.py [--write-report]
Reports: work/diagnostics/localization/LOCALIZATION-AUDIT.md (ignored output).
No third-party dependencies. Does not run the driver cleanup script or touch devices.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from audit_taiwan_content import check_changelog, check_web_references, unique_json, web_catalog

ROOT = Path(__file__).resolve().parents[1]
LANGUAGES = ('zh-CN', 'zh-HK', 'zh-TW', 'en-US', 'ja-JP')
XKEY = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
TOKEN = re.compile(r'(?<!\{)\{(?:\d+|[A-Za-z_]\w*)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})|%(?:\d+\$)?[sd]|%[1-9]|%[A-Z_][A-Z0-9_]*%|\[(?:name(?:/ver)?|gb|mb)\]')
EXCLUDED = {'bin', 'obj', 'native', 'Assets', '_internal', '.git', '__pycache__'}


def source_files(directory: Path):
    return sorted(p for p in directory.rglob('*') if p.is_file()
                  and p.suffix.lower() in {'.cs', '.xaml', '.cpp', '.h', '.py', '.ps1', '.iss', '.rc', '.js', '.html'}
                  and not any(part in EXCLUDED for part in p.relative_to(directory).parts))


def read_xaml(path: Path, errors: list[str], strings_only=True):
    result = {}
    for node in ET.parse(path).getroot().iter():
        key = node.get(XKEY)
        if key is None or (strings_only and not node.tag.endswith('String')):
            continue
        if key in result:
            errors.append(f'{path.relative_to(ROOT)}: duplicate key {key}')
        result[key] = ''.join(node.itertext())
        if node.tag.endswith('String'):
            value = result[key]
            if (value != value.strip() or re.search(r'\n|\t|  +', value)) and node.get('{http://www.w3.org/XML/1998/namespace}space') != 'preserve':
                errors.append(f'{path.relative_to(ROOT)}/{key}: significant whitespace requires xml:space="preserve"')
            # Only treat a 1, 2 sequence as a list; leave versions, units and
            # ordinary numeric values alone. Every item must start a new line.
            markers = list(re.finditer(r'(?<![\w.])([1-9]\d*)[.、．)）](?=\s|[\u3400-\u9fff])', value))
            if len(markers) >= 2 and [m[1] for m in markers[:2]] == ['1', '2']:
                for marker in markers:
                    prefix = value[value.rfind('\n', 0, marker.start()) + 1:marker.start()]
                    if prefix.strip():
                        errors.append(f'{path.relative_to(ROOT)}/{key}: item {marker[1]} must start on a new line')
    return result


def read_ini(path: Path):
    section = ''
    result = {}
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        line = line.strip()
        if line.startswith('['):
            section = line.strip('[]')
        elif line and not line.startswith(';') and '=' in line:
            key, value = line.split('=', 1)
            if section in ('Messages', 'CustomMessages', 'LangOptions') and (section, key) in result:
                raise ValueError(f'{path}: duplicate {section}/{key}')
            result[(section, key)] = value
    return result


def installer_resources():
    compiler = ROOT/'work/tools/inno-setup'
    script_path = ROOT/'installer/iPhoneMirror.iss'
    script = script_path.read_text(encoding='utf-8-sig')
    codes = {'chinesesimp': 'zh-CN', 'chinesetrad': 'zh-HK',
             'chinesetaiwan': 'zh-TW', 'english': 'en-US', 'japanese': 'ja-JP'}
    language_ids = {'zh-CN': '$0804', 'zh-HK': '$0C04', 'zh-TW': '$0404', 'en-US': '$0409',
                    'ja-JP': '$0411'}
    # Read the actual compiler inputs. Cached files must not hide a deleted
    # [Languages] entry or a changed MessagesFile path.
    section = re.search(r'^\[Languages\]\s*\n(.*?)(?=^\[|\Z)', script, re.M | re.S)
    if section is None:
        raise ValueError('Installer: missing [Languages] section')
    declarations = {}
    for line in section[1].splitlines():
        if not line.strip() or line.lstrip().startswith(';'):
            continue
        name = re.search(r'\bName:\s*"([^"]+)"', line)
        messages = re.search(r'\bMessagesFile:\s*"([^"]+)"', line)
        if name is None or messages is None:
            raise ValueError(f'Installer: cannot audit language declaration: {line}')
        code = name[1]
        if code not in codes or code in declarations:
            raise ValueError(f'Installer: unknown or duplicate language {code}')
        relative = messages[1].replace('\\', '/')
        declarations[code] = (compiler/relative.removeprefix('compiler:')
                              if relative.startswith('compiler:') else script_path.parent/relative)
    missing = set(codes) - set(declarations)
    if missing:
        raise ValueError(f'Installer: missing language declarations: {", ".join(sorted(missing))}')
    absent = [str(path) for path in declarations.values() if not path.is_file()]
    if absent:
        return {}, f'Installer language files unavailable: {", ".join(absent)}'
    overrides = read_ini(script_path)
    result = {}
    for code, path in declarations.items():
        entries = read_ini(path)
        language = codes[code]
        language_id = overrides.get(('LangOptions', f'{code}.LanguageID'),
                                    entries.get(('LangOptions', 'LanguageID'), ''))
        if language_id.upper() != language_ids[language]:
            raise ValueError(f'Installer: wrong LanguageID for {code}: {language_id}')
        result[language] = {f'{section}/{key}': value for (section, key), value in entries.items()
                            if section in ('Messages', 'CustomMessages')}
    # Unqualified messages apply to every language; qualified ones override them.
    for (section, name), value in overrides.items():
        if section in ('Messages', 'CustomMessages') and '.' not in name:
            for catalog in result.values():
                catalog[f'{section}/{name}'] = value
    for (section, name), value in overrides.items():
        if section not in ('Messages', 'CustomMessages') or '.' not in name:
            continue
        code, key = name.split('.', 1)
        if code not in codes:
            raise ValueError(f'Installer: unknown message language {code}')
        result[codes[code]][f'{section}/{key}'] = value
    # Language-specific shortcut labels live outside the message dictionary.
    for name, target, code in re.findall(
            r'Name: "\{group\}\\([^"{}]+)"; Filename: "([^"]+)";[^\n]*Languages: (\w+)', script):
        if code in codes:
            key = 'Icons/Changelog' if target in (r'{app}\CHANGELOG.md', r'{app}\CHANGELOG.zh-TW.md') else 'Icons/Uninstall'
            result[codes[code]][key] = name
    return result, None


def hardcoded_resources(inventory):
    """Inventory presentation literals retained after human review, not business IDs."""
    shared = {lang: {} for lang in LANGUAGES}
    attributes = {'Text', 'Content', 'Header', 'ToolTip', 'Title',
                  'AutomationProperties.Name', 'PlaceholderText', 'Watermark'}
    for path in inventory:
        if path.suffix != '.xaml' or path.name.startswith('Strings.'):
            continue
        root = ET.parse(path).getroot()
        for index, node in enumerate(root.iter(), 1):
            for attribute, value in node.attrib.items():
                if attribute not in attributes or not value.strip() or value.startswith('{'):
                    continue
                key = f'{path.relative_to(ROOT).as_posix()} / node {index} / {attribute}'
                for lang in LANGUAGES:
                    shared[lang][key] = value
    startup = (ROOT/'src/App/Services/StartupDiagnostics.cs').read_text(encoding='utf-8')
    body = startup.split('internal static string UserMessage(Exception error, string language)', 1)[1]
    body = body.split('internal static string Label', 1)[0]
    # Conditional Chinese expressions do not end in a semicolon.
    values = re.findall(r'(?:return|\?|:)\s*"([^"\n]+)"', body)
    fallback = {lang: {} for lang in LANGUAGES}
    # Each message is returned in this order: Taiwan, Hong Kong, Japanese,
    # then the Simplified Chinese / English conditional.
    order = ('zh-TW', 'zh-HK', 'ja-JP', 'zh-CN', 'en-US')
    if len(values) != 2 * len(order):
        raise ValueError('Review StartupDiagnostics fallback inventory after code changes')
    for index, key in enumerate(('NativeComponentLoadFailure', 'StartupFailure')):
        for lang, value in zip(order, values[index*len(order):(index+1)*len(order)]):
            fallback[lang][f'StartupDiagnostics.UserMessage/{key}'] = value
    label_body = startup.split('internal static string Label', 1)[1].split('private static bool Find', 1)[0]
    # Taiwan and Japanese have their own switch blocks before the shared tuple.
    taiwan_body, rest = label_body.split('== "ja-JP")', 1)
    japanese_body = rest.split('var labels', 1)[0]
    taiwan_labels = dict(re.findall(r'"(Startup\w+)" => "([^"]+)"', taiwan_body))
    japanese_labels = dict(re.findall(r'"(Startup\w+)" => "([^"]+)"', japanese_body))
    for key, simplified, traditional, english in re.findall(r'"(Startup\w+)" => \("([^"]+)", "([^"]+)", "([^"]+)"\)', label_body):
        for lang, value in zip(LANGUAGES, (simplified, traditional, taiwan_labels.pop(key), english,
                                           japanese_labels.pop(key))):
            fallback[lang]['StartupDiagnostics.Label/' + key] = value
    if taiwan_labels or japanese_labels:
        raise ValueError('StartupDiagnostics: unmatched Taiwan or Japanese fallback labels')
    return shared, fallback


def check_catalog(name, catalog, errors):
    languages = tuple(dict.fromkeys((*LANGUAGES, *catalog)))
    for language in languages:
        if language not in catalog:
            errors.append(f'{name}: missing language {language}')
        elif not catalog[language]:
            errors.append(f'{name}: empty language dictionary {language}')
    catalog = {language: catalog.get(language, {}) for language in languages}
    keys = set().union(*(set(values) for values in catalog.values()))
    for key in keys:
        for lang in languages:
            if key not in catalog[lang]:
                errors.append(f'{name}/{key}: missing {lang}')
            elif not catalog[lang][key].strip() and not (name == 'Installer' and key in {
                    'Messages/BeveledLabel', 'Messages/HelpTextNote', 'Messages/AboutSetupNote', 'Messages/TranslatorNote'}):
                errors.append(f'{name}/{key}: empty {lang}')
        present = [catalog[lang][key] for lang in languages if key in catalog[lang]]
        if len(present) == len(languages) and len({tuple(sorted(TOKEN.findall(v))) for v in present}) != 1:
            errors.append(f'{name}/{key}: placeholder mismatch')
        # Filter descriptions may differ; patterns and separators must remain intact.
        if key.endswith('Filter') and len(present) == len(languages):
            if len({tuple(v.split('|')[1::2]) for v in present}) != 1:
                errors.append(f'{name}/{key}: file filter mismatch')
        if any('\ufffd' in v for v in present):
            errors.append(f'{name}/{key}: replacement character')
        if 'zh-TW' in catalog and key in catalog['zh-TW']:
            taiwan = catalog['zh-TW'][key]
            simplified = set(taiwan) & set('传储关写删务动启图备复夹导应开弹录径户据数断显权标检盘码确线络缩网蓝览触认讯设试误读调软载边连选错键页频驱')
            if simplified:
                errors.append(f'{name}/{key}: simplified-only characters {"".join(sorted(simplified))}')
            for term in ('軟件', '硬件', '網絡', '數據線', '私隱', '解像度',
                         '流動裝置', '快捷鍵', '投屏', '反控', '日誌', '視頻',
                         '鼠標', '設置', '信息', '文件夾', '內置', '適配器'):
                if term in taiwan:
                    errors.append(f'{name}/{key}: non-Taiwan term {term}')
            # Keep significant whitespace, escape sequences, markup and links
            # stable against both source Chinese catalogs, including repeats.
            if name in ('App', 'DriverInstaller', 'Cleanup', 'Installer'):
                syntax = re.compile(r'\r?\n|\\[nrt]|%n|</?[^>]+>|`+|https?://[^\s<>「」]+')
                for reference in ('zh-CN', 'zh-HK'):
                    # Upstream Simplified Chinese has an extra paragraph in
                    # ExitSetupMessage and a nonempty translator credit. Taiwan
                    # preserves the Traditional source's intentional structure.
                    if name == 'Installer' and reference == 'zh-CN' and key in (
                            'Messages/TranslatorNote', 'Messages/ExitSetupMessage'):
                        continue
                    if key in catalog[reference] and Counter(syntax.findall(taiwan)) != Counter(syntax.findall(catalog[reference][key])):
                        errors.append(f'{name}/{key}: whitespace/markup differs from {reference}')
        if name == 'Installer' and len(present) == len(languages) and key != 'Messages/RetryCancelCancel':
            # Chinese adds Alt+C to this optional action; the English catalog
            # intentionally uses a plain Cancel label. This is not a mismatch.
            if len({tuple(sorted(c.upper() for c in re.findall(r'(?<!&)&([A-Za-z])',v))) for v in present}) != 1:
                errors.append(f'{name}/{key}: accelerator mismatch')


def audit(require_installer=False):
    errors, warnings, catalogs, references = [], [], {}, {}
    inventory = (source_files(ROOT/'src') + source_files(ROOT/'scripts') +
                 source_files(ROOT/'tools') + source_files(ROOT/'installer') +
                 sorted(ROOT.glob('*.cmd')) + sorted(ROOT.glob('*.bat')))
    for project in ('App', 'DriverInstaller'):
        catalog = {path.stem.removeprefix('Strings.'): read_xaml(path, errors)
                   for path in sorted((ROOT/f'src/{project}/Localization').glob('Strings.*.xaml'))}
        catalogs[project] = catalog
        check_catalog(project, catalog, errors)
        if 'en-US' not in catalog:
            references[project] = set()
            continue
        project_sources = source_files(ROOT/'src'/project) + source_files(ROOT/'src/SharedUI')
        used = set()
        all_resource_keys = set(catalog['en-US'])
        for path in project_sources:
            if path.suffix == '.xaml':
                all_resource_keys.update(read_xaml(path, [], strings_only=False))
        for path in project_sources:
            if path.name.startswith('Strings.'):
                continue
            content = path.read_text(encoding='utf-8-sig')
            literal_keys = set(re.findall(r'"(\w+)"', content))
            literal_keys.update(re.findall(r'\b(?:DynamicResource|StaticResource)\s+(\w+)\}', content))
            used.update(literal_keys.intersection(catalog['en-US']))
            for key in re.findall(r'(?:LocalizationService|DriverLocalization)\.(?:Get|GetOrDefault|Format)\(\s*"(\w+)"(?!\s*\+)', content):
                if key not in catalog['en-US']:
                    errors.append(f'{path.relative_to(ROOT)}: undefined string key {key}')
            # Reading a template as a caption leaks {0} into the UI even when
            # every dictionary and the template itself are otherwise valid.
            for key in re.findall(r'\b(?:(?:LocalizationService|DriverLocalization)\.Get|L)\(\s*"(\w+)"\s*\)', content):
                if re.search(r'(?<!\{)\{\d+', catalog['en-US'].get(key, '')):
                    errors.append(f'{path.relative_to(ROOT)}: unformatted string template {key}')
            for alias, prefix in re.findall(
                    r'string\s+([LF])\(string key[^)]*\)\s*=>\s*(?:LocalizationService|DriverLocalization)\.(?:Get|Format)\(\s*(?:"(\w*)"\s*\+\s*)?key\b', content):
                for suffix in re.findall(rf'\b{alias}\(\s*"(\w+)"(?!\s*\+)', content):
                    key = prefix + suffix
                    if key not in catalog['en-US']:
                        errors.append(f'{path.relative_to(ROOT)}: undefined alias string key {key}')
                    elif alias == 'L' and re.search(r'(?<!\{)\{\d+', catalog['en-US'][key]):
                        errors.append(f'{path.relative_to(ROOT)}: unformatted string template {key}')
            if path.name == 'BluetoothHidMouseService.cs':
                for call in re.findall(r'\bSetStatus\((.*?)\);', content, re.S):
                    if re.search(r'\$?"[A-Za-z][^"\n]* [A-Za-z][^"\n]*"', call):
                        errors.append(f'{path.relative_to(ROOT)}: hardcoded Bluetooth display status')
            for key in re.findall(r'\{DynamicResource\s+(\w+)\}', content):
                if key not in all_resource_keys:
                    errors.append(f'{path.relative_to(ROOT)}: undefined dynamic resource {key}')
        if project == 'App':
            control = (ROOT/'src/App/Services/ControlStatusService.cs').read_text(encoding='utf-8')
            for enum, prefix in [('ControlStage','ControlStage'),('ControlStageProgress','ControlProgress')]:
                body = re.search(r'enum '+enum+r'\s*\{([^}]+)\}', control)[1]
                for name in body.split(','):
                    if not name.strip(): continue
                    key = prefix + name.strip()
                    used.add(key)
                    if key not in catalog['en-US']: errors.append(f'{project}: undefined enum resource {key}')
        references[project] = used
    cleanup_path = ROOT/'scripts/remove_selected_iphone_drivers.ps1'
    cleanup = cleanup_path.read_text(encoding='utf-8-sig')
    catalogs['Cleanup'] = json.loads(re.search(r"\$script:CleanupMessages = @'\n(.*?)\n'@ \| ConvertFrom-Json", cleanup, re.S)[1], object_pairs_hook=unique_json)
    check_catalog('Cleanup', catalogs['Cleanup'], errors)
    for key in re.findall(r"Get-CleanupText '([^']+)'", cleanup):
        if key not in catalogs['Cleanup']['en-US']: errors.append(f'Cleanup: undefined key {key}')
    digest = hashlib.sha256(cleanup.encode('utf-8')).hexdigest().upper()
    host = (ROOT/'src/DriverInstaller/Services/DriverCleanupHost.cs').read_text(encoding='utf-8')
    if re.search(r'ScriptHash\s*=\s*"([A-F0-9]+)"', host)[1] != digest:
        errors.append('Cleanup: canonical script integrity hash mismatch')
    installer, warning = installer_resources()
    if warning:
        (errors if require_installer else warnings).append(warning)
    else:
        catalogs['Installer'] = installer
        check_catalog('Installer', installer, errors)
    catalogs['Hardcoded'], catalogs['StartupFallback'] = hardcoded_resources(inventory)
    check_catalog('StartupFallback', catalogs['StartupFallback'], errors)
    for name, relative in (('SrsLab', 'tools/srs-lab/localize.js'), ('SrsTest', 'tools/srs-test/web/localize.js')):
        catalogs[name] = web_catalog(ROOT/relative)
        check_catalog(name, catalogs[name], errors)
        check_web_references((ROOT/relative).parent, catalogs[name], errors)
    check_changelog(ROOT, errors)
    launcher = (ROOT/'Remove-Selected-iPhone-Drivers.cmd').read_text(encoding='utf-8')
    launcher_bytes = (ROOT/'Remove-Selected-iPhone-Drivers.cmd').read_bytes()
    if launcher_bytes.startswith(b'\xef\xbb\xbf') or b'\n' in launcher_bytes.replace(b'\r\n', b''):
        errors.append('Launcher: CMD requires BOM-free UTF-8 with CRLF line endings')
    messages = re.findall(r'^\s*echo (.+)$', launcher, re.M)
    # Each message is echoed as Simplified, Traditional, English, Japanese.
    if len(messages) != 8 or 'chcp 65001 >nul' not in launcher:
        errors.append('Launcher: review UTF-8 multilingual fallback messages')
    else:
        title = re.search(r'^title (.+)$', launcher, re.M)[1]
        catalogs['Launcher'] = {lang: {'Title': title,
            'ExecutableMissing': messages[index], 'OperationIncomplete': messages[index+4]}
            for index, lang in ((0, "zh-CN"), (1, "zh-HK"), (1, "zh-TW"), (2, "en-US"), (3, "ja-JP"))}
        check_catalog('Launcher', catalogs['Launcher'], errors)
    app_languages = set(catalogs['App'])
    for component, catalog in catalogs.items():
        for language in sorted(app_languages - set(catalog)):
            errors.append(f'{component}: missing App language {language}')
    return catalogs, references, inventory, errors, warnings


def cell(value):
    return str(value).replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('|','&#124;').replace('\r','').replace('\n','<br>')


def write_report(catalogs, references, inventory, errors, warnings):
    # Generated matrices are build evidence, not maintained user documentation.
    from datetime import datetime, timezone
    report_dir = ROOT/'work/diagnostics/localization'
    report_dir.mkdir(parents=True, exist_ok=True)
    checked_at = datetime.now(timezone.utc).isoformat(timespec='seconds')
    languages = '、'.join(LANGUAGES)
    lines = ['# 多语言资源检查', '', f'生成时间（UTC）：{checked_at}', '',
             f'扫描 {len(inventory)} 个第一方源码/脚本文件。语言：{languages}。', '',
             '本表反映当前源码中的资源值与静态检查；不代表翻译语义、真实界面排版或设备行为已验收。', '',
             '## 多语言对照', '']
    for project, catalog in catalogs.items():
        lines += [f'### {project}', '',
                  '| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 日本語 |',
                  '| --- | --- | --- | --- | --- | --- |']
        for key in sorted(set().union(*(set(values) for values in catalog.values()))):
            values = [key, *(catalog.get(lang, {}).get(key, '[缺失]') for lang in LANGUAGES)]
            lines.append('| ' + ' | '.join(cell(value) for value in values) + ' |')
        lines.append('')
    lines += ['## 静态未发现引用的资源', '',
              '动态拼接和外部调用可能无法被静态扫描识别，不能仅据此删除资源。', '']
    for project, used in references.items():
        keys = sorted(set(catalogs[project].get('en-US', {}))-used)
        lines.append(f'- {project}：{len(keys)} 项。' + ', '.join('`'+key+'`' for key in keys))
    lines += ['', '## 检查结果', '', f'- 错误：{len(errors)}；提醒：{len(warnings)}。']
    lines += ['- '+message for message in errors + warnings]
    lines += ['', '## 扫描文件', '']
    lines += ['- `'+path.relative_to(ROOT).as_posix()+'`' for path in inventory]
    (report_dir/'LOCALIZATION-AUDIT.md').write_text('\n'.join(lines)+'\n', encoding='utf-8')


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--write-report',action='store_true')
    parser.add_argument('--require-installer', action='store_true',
                        help='Fail if Inno Setup language resources cannot be audited (CI).')
    parser.add_argument('--summary', action='store_true', help='Omit the unreferenced-key inventory.')
    args=parser.parse_args()
    catalogs, references, inventory, errors, warnings = audit(require_installer=args.require_installer)
    if args.write_report: write_report(catalogs,references,inventory,errors,warnings)
    result = {'source_files': len(inventory),
              'keys': {p: {l: len(v) for l, v in c.items()} for p, c in catalogs.items()},
              'errors': errors, 'warnings': warnings}
    if not args.summary:
        result['unreferenced'] = {p: sorted(set(catalogs[p].get('en-US', {}))-v) for p, v in references.items()}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(1 if errors else 0)
