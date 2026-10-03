"""Source inventory for the two WPF applications; no device or UI automation."""
from pathlib import Path
import argparse
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'


def audit():
    result = {'surfaces': [], 'theme_key_difference': [], 'localization': {}, 'literal_colors': [], 'contrast': {}}
    for project in ['App', 'DriverInstaller', 'SharedUI']:
        for path in sorted((ROOT / 'src' / project).rglob('*.xaml')):
            if any(part in ['obj', 'bin'] for part in path.parts):
                continue
            root = ET.parse(path).getroot()
            text = path.read_text(encoding='utf-8-sig')
            if X + 'Class' in root.attrib:
                result['surfaces'].append({
                    'root_type': root.tag.rsplit('}', 1)[-1],
                    'file': str(path.relative_to(ROOT)), 'window': root.attrib,
                    'controls': {kind: sum(e.tag.rsplit('}', 1)[-1] == kind for e in root.iter())
                                 for kind in ['Button', 'ScrollViewer', 'Popup', 'ContextMenu', 'TextBox', 'ComboBox', 'ListBox', 'TabItem', 'ProgressBar', 'InfoBar']},
                    'resources': [e.attrib[X + 'Key'] for e in root.iter() if X + 'Key' in e.attrib],
                    'fixed_text_buttons': [dict(e.attrib) for e in root.iter()
                        if e.tag.endswith('}Button') and 'Content' in e.attrib and 'Width' in e.attrib],
                    'literal_cjk': re.findall(r'(?:Text|Content|Header)="([^"{]*[\u4e00-\u9fff][^"]*)"', text),
                })
            if 'Themes' not in path.parts:
                for number, line in enumerate(text.splitlines(), 1):
                    if re.search(r'#[0-9A-Fa-f]{6,8}\b', line):
                        result['literal_colors'].append(f'{path.relative_to(ROOT)}:{number}: {line.strip()}')
    theme_keys = []
    for theme in ['Dark', 'Light']:
        theme_keys.append({e.attrib[X + 'Key'] for e in ET.parse(ROOT / f'src/SharedUI/Themes/{theme}Theme.xaml').getroot() if X + 'Key' in e.attrib})
    result['theme_key_difference'] = sorted(theme_keys[0] ^ theme_keys[1])
    for theme in ['Dark', 'Light']:
        values = {e.attrib.get(X + 'Key'): e.attrib.get('Color', e.text or '')
                  for e in ET.parse(ROOT / f'src/SharedUI/Themes/{theme}Theme.xaml').getroot()}
        def rgb(key):
            value = values[key].strip()
            if value.startswith('{StaticResource '):
                return rgb(value[len('{StaticResource '):-1])
            return tuple(int(value[-6:][offset:offset+2], 16) / 255 for offset in (0, 2, 4))
        def luminance(channels):
            linear = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in channels]
            return sum(v * w for v, w in zip(linear, (0.2126, 0.7152, 0.0722)))
        pairs = [(key, 'BackgroundBrush') for key in ['TextBrush', 'MutedTextBrush', 'SuccessBrush', 'WarningBrush', 'ErrorBrush', 'InfoBrush']]
        pairs += [('PrimaryActionTextBrush', 'PrimaryActionBrush'), ('DangerButtonTextBrush', 'DangerBrush'), ('DisabledTextBrush', 'PrimaryActionDisabledBrush')]
        result['contrast'][theme] = {}
        for foreground, background in pairs:
            a, b = sorted([luminance(rgb(foreground)), luminance(rgb(background))])
            result['contrast'][theme][f'{foreground}/{background}'] = round((b + .05) / (a + .05), 2)
    for project in ['App', 'DriverInstaller']:
        dictionaries = {}
        for culture in ['zh-CN', 'en-US', 'zh-HK', 'zh-TW']:
            dictionaries[culture] = {e.attrib[X + 'Key'] for e in ET.parse(ROOT / f'src/{project}/Localization/Strings.{culture}.xaml').getroot() if X + 'Key' in e.attrib}
        union = set.union(*dictionaries.values())
        result['localization'][project] = {culture: sorted(union - keys) for culture, keys in dictionaries.items()}
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = audit()
    content = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(content, encoding='utf-8')
    else:
        print(content)
    print(f"Inventoried {len(result['surfaces'])} XAML surfaces; theme differences: {len(result['theme_key_difference'])}")
