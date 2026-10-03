"""Audit first-party UI resources and generate the complete multilingual matrix.

Run: python scripts/audit_localization.py [--write-report]
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
LANGUAGES = ('zh-CN', 'zh-HK', 'zh-TW', 'en-US')
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
    defaults = ('Languages/ChineseSimplified.isl', 'Languages/ChineseTraditional.isl', str(ROOT/'installer/Languages/ChineseTraditionalTaiwan.isl'), 'Default.isl')
    if not all((compiler/p).exists() for p in defaults):
        return {}, '未发现本地 Inno Setup 语言包；安装器继承文本未验证'
    result = {lang: {f'{section}/{key}': value for (section, key), value in read_ini(compiler/p).items()
                     if section in ('Messages', 'CustomMessages')}
              for lang, p in zip(LANGUAGES, defaults)}
    overrides = read_ini(ROOT/'installer/iPhoneMirror.iss')
    codes = dict(zip(('chinesesimp', 'chinesetrad', 'chinesetaiwan', 'english'), LANGUAGES))
    for (section, name), value in overrides.items():
        if section not in ('Messages', 'CustomMessages') or '.' not in name:
            continue
        code, key = name.split('.', 1)
        if code in codes:
            result[codes[code]][f'{section}/{key}'] = value
    # Language-specific shortcut labels live outside the message dictionary.
    script = (ROOT/'installer/iPhoneMirror.iss').read_text(encoding='utf-8-sig')
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
    if len(values) != 8:
        raise ValueError('Review StartupDiagnostics fallback inventory after code changes')
    for index, key in enumerate(('NativeComponentLoadFailure', 'StartupFailure')):
        for lang, value in zip(('zh-TW', 'zh-HK', 'zh-CN', 'en-US'), values[index*4:index*4+4]):
            fallback[lang][f'StartupDiagnostics.UserMessage/{key}'] = value
    label_body = startup.split('internal static string Label', 1)[1].split('private static bool Find', 1)[0]
    taiwan_labels = dict(re.findall(r'"(Startup\w+)" => "([^"]+)"', label_body))
    for key, simplified, traditional, english in re.findall(r'"(Startup\w+)" => \("([^"]+)", "([^"]+)", "([^"]+)"\)', label_body):
        for lang, value in zip(LANGUAGES, (simplified, traditional, taiwan_labels.pop(key), english)):
            fallback[lang]['StartupDiagnostics.Label/' + key] = value
    if taiwan_labels:
        raise ValueError('StartupDiagnostics: unmatched Taiwan fallback labels')
    return shared, fallback


def check_catalog(name, catalog, errors):
    keys = set().union(*(set(values) for values in catalog.values()))
    for key in keys:
        for lang in LANGUAGES:
            if key not in catalog[lang]:
                errors.append(f'{name}/{key}: missing {lang}')
            elif not catalog[lang][key].strip() and not (name == 'Installer' and key in {
                    'Messages/BeveledLabel', 'Messages/HelpTextNote', 'Messages/AboutSetupNote', 'Messages/TranslatorNote'}):
                errors.append(f'{name}/{key}: empty {lang}')
        present = [catalog[lang][key] for lang in LANGUAGES if key in catalog[lang]]
        if len(present) == len(LANGUAGES) and len({tuple(sorted(TOKEN.findall(v))) for v in present}) != 1:
            errors.append(f'{name}/{key}: placeholder mismatch')
        # Filter descriptions may differ; patterns and separators must remain intact.
        if key.endswith('Filter') and len(present) == len(LANGUAGES):
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
        if name == 'Installer' and len(present) == len(LANGUAGES) and key != 'Messages/RetryCancelCancel':
            # Chinese adds Alt+C to this optional action; the English catalog
            # intentionally uses a plain Cancel label. This is not a mismatch.
            if len({tuple(sorted(c.upper() for c in re.findall(r'(?<!&)&([A-Za-z])',v))) for v in present}) != 1:
                errors.append(f'{name}/{key}: accelerator mismatch')


def audit():
    errors, warnings, catalogs, references = [], [], {}, {}
    inventory = (source_files(ROOT/'src') + source_files(ROOT/'scripts') +
                 source_files(ROOT/'tools') + source_files(ROOT/'installer') +
                 sorted(ROOT.glob('*.cmd')) + sorted(ROOT.glob('*.bat')))
    for project in ('App', 'DriverInstaller'):
        catalog = {lang: read_xaml(ROOT/f'src/{project}/Localization/Strings.{lang}.xaml', errors)
                   for lang in LANGUAGES}
        catalogs[project] = catalog
        check_catalog(project, catalog, errors)
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
            if re.search(r'string [LF]\(string key', content):
                for key in re.findall(r'\b[LF]\(\s*"(\w+)"(?!\s*\+)', content):
                    if key not in catalog['en-US']:
                        errors.append(f'{path.relative_to(ROOT)}: undefined alias string key {key}')
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
    if warning: warnings.append(warning)
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
    if len(messages) != 6 or 'chcp 65001 >nul' not in launcher:
        errors.append('Launcher: review UTF-8 multilingual fallback messages')
    else:
        title = re.search(r'^title (.+)$', launcher, re.M)[1]
        catalogs['Launcher'] = {lang: {'Title': title,
            'ExecutableMissing': messages[index], 'OperationIncomplete': messages[index+3]}
            for index, lang in ((0, "zh-CN"), (1, "zh-HK"), (1, "zh-TW"), (2, "en-US"))}
        check_catalog('Launcher', catalogs['Launcher'], errors)
    return catalogs, references, inventory, errors, warnings


def cell(value):
    return str(value).replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('|','&#124;').replace('\r','').replace('\n','<br>')


def write_report(catalogs, references, inventory, errors, warnings):
    changes_path = ROOT/'docs/localization-audit-changes.json'
    changes = json.loads(changes_path.read_text(encoding='utf-8')) if changes_path.exists() else {}
    labels = {'缺失翻译':'❌ 缺失', '错误翻译':'❌ 含义不一致', '语义不一致':'❌ 含义不一致',
              '占位符问题':'❌ 占位符不一致', '术语不一致':'❌ 术语不一致', '可读性问题':'⚠️ 需要优化'}
    counts = Counter(category for issue in changes.values() for category in issue['categories'])
    lines = ['# 多语言文字一致性审计表', '', '审计日期：2026-10-03。新增独立台湾繁体中文资源。', '',
             '## 审查范围', '',
             '支持语言：简体中文（zh-CN）、香港繁体中文（zh-HK）、台湾繁体中文（zh-TW）、英文（en-US）。', '',
             f'扫描 {len(inventory)} 个第一方源码/脚本文件，核对主程序、驱动管理器的八份语言字典、WPF XAML、C# 提示/错误、独立驱动清理脚本，以及安装器有效语言资源。', '',
             '主程序覆盖主窗口、设备绑定、蓝牙/有线/无线控制、所有设置与状态窗口、采集恢复、截图、录制、推流、虚拟摄像头、更新、关于、诊断和开发者预览。', '',
             '语言无关内容（产品名、协议、键名、单位、尺寸、路径、设备自报名称）保持原样。原生库、FFmpeg、Windows 返回的原始诊断及结构化日志事件/字段是技术数据，保留原文用于排错。开发/构建脚本、测试断言、第三方代码和许可证不作为应用 UI 字典翻译。台湾完整更新日志另行校验版本、条目、代码标记及链接；两个串流测试网页也纳入资源表。', '',
             '硬编码表逐项列出保留的 XAML 品牌、协议、数字、符号和单位；node 是 XML 文档中的节点序号。启动故障的两条消息及五个备用标签均有独立四语回退：它们必须在语言字典加载失败时仍然可用。安装器的更新记录、卸载快捷方式也纳入对应表。', '',
             '## 问题统计', '', '| 分类 | 受影响条目数（可重叠） |','| --- | ---: |']
    for category in ('缺失翻译','错误翻译','语义不一致','占位符问题','术语不一致','可读性问题'):
        lines.append(f'| {category} | {counts[category]} |')
    lines += ['', f'确认并修复的问题条目：{len(changes)}。问题状态表示**修改前**；表内四语为**修改后**。未发现问题的资源标为“✅ 完全一致”。', '',
              '缺失翻译按硬编码消息/资源 Key 计数，不按缺少的语言单元格重复计数。静态未发现引用不等于已废弃，全部保留并列出，避免误删外部或动态调用。', '',
              '## 多语言对照', '']
    for project,catalog in catalogs.items():
        lines += [f'### {project}', '', '| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 / 使用情况 |', '| --- | --- | --- | --- | --- | --- | --- | --- |']
        for key in sorted(set().union(*(set(v) for v in catalog.values()))):
            issue = changes.get(project+'/'+key)
            status = '✅ 完全一致'
            note = '保留'
            if issue:
                status = '、'.join(dict.fromkeys(labels[c] for c in issue['categories']))+' → ✅ 已修复'
                note = '；'.join(issue['reasons'])
            if project in references and key not in references[project]:
                note += '；静态未发现引用，保留待后续调用核对'
            if project == 'Hardcoded':
                note = '品牌、协议、数字、符号或单位；四语通用，保留'
            elif project == 'StartupFallback':
                note = '语言字典无法加载时的独立四语回退，保留'
            elif project == 'Launcher':
                note += '；独立 CMD 启动器无语言字典，异常时同时显示三语回退'
            elif project == 'Installer' and key in {'Messages/BeveledLabel', 'Messages/HelpTextNote', 'Messages/AboutSetupNote', 'Messages/TranslatorNote'}:
                note += '；可选语言包备注/标签，允许为空或按语言不同'
            lines.append('| '+' | '.join(cell(v) for v in [key, *(catalog[lang].get(key,'[缺失]') for lang in LANGUAGES), '—', status, note])+' |')
        lines.append('')
    code_issues = {key: issue for key, issue in changes.items() if key.startswith('Code/')}
    if code_issues:
        lines += ['### 代码调用对应关系', '', '| Key / 位置 | 简体中文 | 繁體中文（香港） | 繁體中文（台灣） | English | 其他语言 | 状态（修改前 → 修改后） | 修改内容 |', '| --- | --- | --- | --- | --- | --- | --- | --- |']
        for key, issue in code_issues.items():
            status = '、'.join(dict.fromkeys(labels[c] for c in issue['categories']))+' → ✅ 已修复'
            values = issue.get('values', {})
            lines.append('| '+' | '.join(cell(v) for v in [key, *(values.get(lang, '见对应资源 Key') for lang in LANGUAGES), '—', status, '；'.join(issue['reasons'])])+' |')
        lines.append('')
    lines += ['## 代码位置与修改前记录', '', '| Key / 位置 | 修改前文字 | 修改说明 | 当前调用位置 |', '| --- | --- | --- | --- |']
    for key, issue in sorted(changes.items()):
        before='；'.join(f'{lang}: {value}' for lang,value in issue.get('before',{}).items() if value is not None)
        if not before: before='原为代码硬编码；对应源码变更见工作区 diff'
        locations = '；'.join(issue.get('locations', [])) or '见 Key 所指文件或资源字典'
        lines.append('| '+' | '.join(cell(v) for v in (key,before,'；'.join(issue['reasons']), locations))+' |')
    lines += ['', '## 静态未发现引用的资源', '',
              '以下是保守的字面引用扫描结果，可能包含动态拼接、外部窗口或预留入口；没有据此删除资源。', '']
    for project, used in references.items():
        keys = sorted(set(catalogs[project]['en-US'])-used)
        lines += [f'- {project}：{len(keys)} 项。'+', '.join('`'+key+'`' for key in keys)]
    lines += ['', '## 完整性检查', '', f'- XML、Key、空值、占位符、文件过滤器、动态状态资源及脚本哈希：{len(errors)} 项错误。',
              '- 安装器使用本地固定版本 Inno Setup 语言包叠加项目覆盖项核对；未修改上游语言包。',
              '- 换行保留语义分段，不要求不同语言标点和句法逐字相同。',
              '- 同时核对位置/命名占位符、printf 占位符以及安装器参数。']
    for message in errors + warnings: lines.append('- '+message)
    lines += ['', '台湾新增语言的构建、运行时格式化、语言切换及 UI 布局验证见 [LOCALIZATION-TAIWAN.md](LOCALIZATION-TAIWAN.md)；早期三语审查见 [LOCALIZATION-VALIDATION.md](LOCALIZATION-VALIDATION.md)。', '',
              '## 扫描文件清单', '']
    lines += ['- `'+p.relative_to(ROOT).as_posix()+'`' for p in inventory]
    (ROOT/'docs/LOCALIZATION-AUDIT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--write-report',action='store_true')
    args=parser.parse_args()
    catalogs, references, inventory, errors, warnings = audit()
    if args.write_report: write_report(catalogs,references,inventory,errors,warnings)
    print(json.dumps({'source_files':len(inventory),'keys':{p:{l:len(v) for l,v in c.items()} for p,c in catalogs.items()},
                      'unreferenced':{p:sorted(set(catalogs[p]['en-US'])-v) for p,v in references.items()},
                      'errors':errors,'warnings':warnings},ensure_ascii=False,indent=2))
    raise SystemExit(1 if errors else 0)
