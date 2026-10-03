"""Taiwan changelog and first-party browser catalogs (standard library only)."""
from collections import Counter
import html
import json
import re


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f'Duplicate JSON key: {key}')
        result[key] = value
    return result


def web_catalog(path):
    source = path.read_text(encoding='utf-8-sig')
    literal = re.search(r'const messages = (\{.*?\n\});', source, re.S)
    if literal is None:
        raise ValueError(f'{path}: cannot locate browser localization catalog')
    messages = json.loads(literal[1], object_pairs_hook=unique_json)
    catalog = {language: {} for language in ('zh-CN', 'zh-HK', 'zh-TW', 'en-US')}
    for key, values in messages.items():
        if not isinstance(values, list) or len(values) != 3:
            raise ValueError(f'{path}/{key}: expected three independent Chinese translations')
        for language, value in zip(catalog, [*values, key]):
            catalog[language][key] = value
    return catalog


def check_web_references(directory, catalog, errors):
    for path in sorted(directory.iterdir()):
        if path.suffix not in ('.js', '.html') or path.name == 'localize.js':
            continue
        source = path.read_text(encoding='utf-8-sig')
        references = [value for _, value in re.findall(r'\bt\(\s*([\'"])(.*?)\1', source)]
        references += re.findall(r'<[^>]*\sdata-i18n(?=\s|>)[^>]*>([^<]+)</', source)
        references += re.findall(r'<[^>]*\bdata-i18n-label\b[^>]*\baria-label="([^"]+)"', source)
        for value in references:
            key = html.unescape(value.strip())
            if key not in catalog['en-US']:
                errors.append(f'{path}/{key}: missing browser translation')


def check_changelog(root, errors):
    def sections(text):
        parts = re.split(r'^## (.+)$', text, flags=re.M)
        return unique_json(zip(parts[1::2], parts[2::2]))

    original = (root/'CHANGELOG.md').read_text(encoding='utf-8-sig')
    taiwan = (root/'CHANGELOG.zh-TW.md').read_text(encoding='utf-8-sig')
    source, target = sections(original), sections(taiwan)
    if list(source) != list(target):
        errors.append('Changelog: Taiwan version headings/order differ from the original')
    for heading in source.keys() & target.keys():
        for pattern in (r'^- ', r'^### ', r'`[^`]+`', r'https?://[^\s)]+', r'</?[^>]+>'):
            if Counter(re.findall(pattern, source[heading], re.M)) != Counter(re.findall(pattern, target[heading], re.M)):
                errors.append(f'Changelog/{heading}: missing content or altered markup ({pattern})')
        if not target[heading].strip():
            errors.append(f'Changelog/{heading}: empty Taiwan section')
    if re.findall(r'^\[[^\]\n]+\]:[^\n]+', original, re.M) != re.findall(r'^\[[^\]\n]+\]:[^\n]+', taiwan, re.M):
        errors.append('Changelog: Taiwan reference links differ from the original')
    if '\ufffd' in taiwan:
        errors.append('Changelog: replacement character')
    return {'versions': len(target), 'bullets': len(re.findall(r'^- ', taiwan, re.M))}
