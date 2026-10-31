"""Extract text already carried by rich pasteboard formats, without iOS conversion."""
import codecs
from html.parser import HTMLParser
import plistlib
import re

MAX_RICH_TEXT_BYTES = 4 * 1024 * 1024


def _decode_html(raw, encoding=None):
    if isinstance(raw, str):
        return raw if len(raw) <= MAX_RICH_TEXT_BYTES else None
    if not isinstance(raw, (bytes, bytearray)) or len(raw) > MAX_RICH_TEXT_BYTES:
        return None
    if raw.startswith((b'\xff\xfe', b'\xfe\xff')):
        encoding = 'utf-16'
    elif raw.startswith(b'\xef\xbb\xbf'):
        encoding = 'utf-8-sig'
    elif not encoding:
        match = re.search(br'<meta\b[^>]*\bcharset\s*=\s*[\x22\x27]?([a-zA-Z0-9_-]+)', raw[:4096], re.I)
        encoding = match[1].decode('ascii') if match else 'utf-8'
    try:
        name = codecs.lookup(encoding).name
        if name not in {'utf-8', 'utf-8-sig', 'utf-16', 'utf-16-le', 'utf-16-be',
                        'cp1252', 'iso8859-1', 'gb18030', 'gbk', 'big5', 'shift_jis', 'euc_jp'}:
            return None
        return raw.decode(name)
    except (LookupError, UnicodeError, TypeError):
        return None


class _ClipboardHtml(HTMLParser):
    BLOCKS = {'p', 'div', 'section', 'article', 'header', 'footer', 'blockquote',
              'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'li', 'tr', 'pre'}
    VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'wbr'}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []
        self.stack = []
        self.pre = 0
        self.hidden = 0

    def boundary(self):
        if self.parts and not self.parts[-1].endswith('\n'):
            self.parts[-1] = self.parts[-1].rstrip(' \t')
            self.parts.append('\n')

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        style = re.sub(r'\s+', '', attrs.get('style') or '').lower()
        hidden = tag in {'head', 'script', 'style', 'template'} or 'hidden' in attrs or any(
            rule in style for rule in ('display:none', 'visibility:hidden'))
        if tag not in self.VOID:
            if len(self.stack) >= 256:
                raise ValueError('HTML nesting exceeds clipboard limit')
            self.stack.append((tag, hidden))
            self.hidden += hidden
        if self.hidden or hidden:
            return
        if tag == 'br':
            # Consecutive explicit breaks are copied blank lines. A block
            # boundary only ensures a break and must not collapse these.
            if self.parts:
                self.parts[-1] = self.parts[-1].rstrip(' \t')
            self.parts.append('\n')
        elif tag in self.BLOCKS or tag == 'hr':
            self.boundary()
        if tag == 'pre':
            self.pre += 1
        if tag in {'td', 'th'} and self.parts and not self.parts[-1].endswith(('\n', '\t')):
            self.parts.append('\t')

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in self.VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        was_hidden = self.hidden
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                self.hidden -= sum(hidden for _, hidden in self.stack[i:])
                del self.stack[i:]
                break
        if was_hidden:
            return
        if tag == 'pre':
            self.pre = max(0, self.pre - 1)
        if tag in self.BLOCKS:
            self.boundary()

    def handle_data(self, data):
        if self.hidden:
            return
        if not self.pre:
            data = re.sub(r'[ \t\r\n\f]+', ' ', data)
            if not self.parts or self.parts[-1].endswith((' ', '\n', '\t')):
                data = data.lstrip(' ')
        if data:
            self.parts.append(data)


def html_text(raw, encoding=None):
    markup = _decode_html(raw, encoding)
    if markup is None:
        return None
    parser = _ClipboardHtml()
    try:
        parser.feed(markup)
        parser.close()
    except (ValueError, RecursionError):
        return None
    return ''.join(parser.parts).strip('\n').rstrip(' \t')


def inline_rich_text(data):
    for uti in ('public.html', 'com.apple.webarchive', 'Apple Web Archive pasteboard type'):
        entry = data.get(uti)
        raw = entry.get('data') if isinstance(entry, dict) else None
        if uti == 'public.html':
            text = html_text(raw)
        else:
            if not isinstance(raw, (bytes, bytearray)) or len(raw) > MAX_RICH_TEXT_BYTES:
                continue
            try:
                resource = plistlib.loads(raw).get('WebMainResource', {})
                if resource.get('WebResourceMIMEType') not in ('text/html', 'application/xhtml+xml'):
                    continue
                text = html_text(resource.get('WebResourceData'), resource.get('WebResourceTextEncodingName'))
            except (ValueError, TypeError, AttributeError, plistlib.InvalidFileException):
                continue
        if text is not None:
            return text
    return None
