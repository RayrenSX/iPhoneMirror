"""Apple device identifiers and per-device capture broker addresses."""
import os

HEX_DIGITS = '0123456789abcdef'


def udid_key(value):
    return value.replace('-', '').casefold() if isinstance(value, str) else ''


def udid_matches(left, right):
    identity = udid_key(left)
    return bool(identity) and identity == udid_key(right)


def correct_serial(serial):
    """Format a modern 24-character USB serial as an 8-16 Apple UDID."""
    if len(serial) == 24 and all(c in HEX_DIGITS for c in serial.lower()):
        return serial[:8] + '-' + serial[8:]
    return serial


def capture_mux_environment_key(udid):
    identity = udid_key(udid)
    if not identity or any(c not in HEX_DIGITS for c in identity):
        raise ValueError('invalid Apple device identity')
    return 'IPHONE_MIRROR_CAPTURE_MUX_' + identity.upper()


def capture_mux_address(value):
    if not isinstance(value, str) or not value.startswith('127.0.0.1:'):
        return None
    port = value[len('127.0.0.1:'):]
    if not port.isascii() or not port.isdigit() or len(port) > 5 or not 1 <= int(port) <= 65535:
        return None
    return value


def published_capture_mux(udid):
    try:
        key = capture_mux_environment_key(udid or '')
    except ValueError:
        return None
    return capture_mux_address(os.environ.get(key))
