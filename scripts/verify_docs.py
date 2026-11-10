"""Check repository Markdown links, anchors, fences and docs index coverage offline.

Uses Git's tracked/nonignored files, so a local build directory cannot hide a
broken link in a fresh checkout. External URLs and historical inline-code paths
are not fetched or validated. This is a repository check, not a Markdown renderer.
"""
from __future__ import annotations

import html
from pathlib import Path
import re
import subprocess
import sys
import unicodedata
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[1]
INLINE_LINK = re.compile(
    r'!?\[[^\]\n]*\]\(\s*(<[^>\n]+>|(?:[^\s()]+|\([^()\n]*\))+)'
    r'(?:\s+["\'][^\n]*?["\'])?\s*\)'
)
HTML_LINK = re.compile(r'\b(?:href|src)=["\']([^"\']+)["\']', re.I)
REFERENCE = re.compile(r'^\s{0,3}\[[^\]\n]+\]:\s*(<[^>\n]+>|\S+)', re.M)


def repository_files() -> set[Path]:
    result = subprocess.run(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'],
        cwd=ROOT, check=True, stdout=subprocess.PIPE,
    )
    return {ROOT / item.decode('utf-8') for item in result.stdout.split(b'\0') if item}


def prose(text: str) -> tuple[str, int | None]:
    lines = []
    opened = None
    for number, line in enumerate(text.splitlines(), 1):
        marker = re.match(r'^\s{0,3}(`{3,}|~{3,})(.*)$', line)
        if opened:
            if marker and marker[1][0] == opened[0] and len(marker[1]) >= opened[1] and not marker[2].strip():
                opened = None
            lines.append('')
        elif marker:
            opened = (marker[1][0], len(marker[1]), number)
            lines.append('')
        else:
            lines.append(line)
    return '\n'.join(lines), opened[2] if opened else None


def heading_ids(text: str) -> set[str]:
    ids = set(re.findall(r'\b(?:id|name)=["\']([^"\']+)["\']', text))
    used = set()
    lines = text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r'^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$', line)
        title = match[1] if match else None
        if not title and index + 1 < len(lines) and line.strip() and re.fullmatch(r'\s{0,3}(?:=+|-+)\s*', lines[index + 1]):
            title = line.strip()
        if not title:
            continue
        title = re.sub(r'!?\[([^\]]*)\]\([^)]*\)', r'\1', title)
        title = html.unescape(re.sub(r'<[^>]*>', '', title)).lower()
        title = title.replace('`', '').replace('*', '')
        slug = ''.join(c for c in title if c in ' _-' or unicodedata.category(c)[0] in 'LNM')
        slug = slug.replace(' ', '-')
        candidate, suffix = slug, 0
        while candidate in used:
            suffix += 1
            candidate = f'{slug}-{suffix}'
        used.add(candidate)
        ids.add(candidate)
    return ids


def links(text: str):
    # Links written as code are examples, not navigable references.
    text = re.sub(r'(`+).*?\1', lambda m: ' ' * len(m[0]), text)
    for pattern in (INLINE_LINK, HTML_LINK, REFERENCE):
        for match in pattern.finditer(text):
            yield text.count('\n', 0, match.start()) + 1, html.unescape(match[1].strip('<>'))


def main() -> int:
    files = repository_files()
    documents = sorted(p for p in files if p.suffix.lower() == '.md' and p.is_file())
    contents, anchors, graph, errors = {}, {}, {}, []
    for path in documents:
        try:
            text, unclosed = prose(path.read_text(encoding='utf-8-sig'))
        except UnicodeError as error:
            errors.append(f'{path.relative_to(ROOT).as_posix()}: invalid UTF-8: {error}')
            continue
        contents[path] = text
        anchors[path] = heading_ids(text)
        graph[path] = set()
        if unclosed:
            errors.append(f'{path.relative_to(ROOT).as_posix()}:{unclosed}: unclosed code fence')
    count = 0
    directories = {parent for path in files for parent in path.parents if parent.is_relative_to(ROOT)}
    for path, text in contents.items():
        for line, url in links(text):
            parts = urlsplit(url)
            if re.match(r'^[A-Za-z]:[/\\]', url):
                errors.append(f'{path.relative_to(ROOT).as_posix()}:{line}: use a relative repository link: {url}')
                continue
            if url.startswith('//') or parts.scheme:
                continue
            count += 1
            target = (path.parent / unquote(parts.path)).resolve() if parts.path else path
            label = f'{path.relative_to(ROOT).as_posix()}:{line}'
            if target not in files and target not in directories:
                errors.append(f'{label}: target not in repository: {url}')
                continue
            if not target.exists():
                errors.append(f'{label}: missing target: {url}')
                continue
            if target in contents:
                graph[path].add(target)
                if parts.fragment and unquote(parts.fragment) not in anchors[target]:
                    errors.append(f'{label}: missing heading anchor: {url}')
    reached, pending = set(), [ROOT / 'docs/README.md']
    while pending:
        path = pending.pop()
        if path in reached:
            continue
        reached.add(path)
        pending.extend(graph.get(path, set()) - reached)
    for path in documents:
        if path.is_relative_to(ROOT / 'docs') and path not in reached:
            errors.append(f'{path.relative_to(ROOT).as_posix()}: unreachable from docs/README.md')
    for error in errors:
        print(error)
    print(f'{len(documents)} Markdown files; {count} local links/images; {len(errors)} errors.')
    return 1 if errors else 0


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
    raise SystemExit(main())
