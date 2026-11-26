"""Fetch the WWDR intermediate named by a development certificate's AIA extension."""

from __future__ import annotations

import urllib.request
from urllib.parse import urlparse

from .x509_reader import CertificateError, read

_cache: dict[str, bytes] = {}


class WWDRError(Exception):
    pass


def fetch(certificate_der: bytes) -> bytes:
    try:
        url = read(certificate_der).ca_issuer_url
    except CertificateError as exc:
        raise WWDRError("The signing certificate could not be read.") from exc
    if not url:
        raise WWDRError("The signing certificate has no CA Issuers URL.")
    cached = _cache.get(url)
    if cached is not None:
        return cached
    parsed = urlparse(url)
    host = parsed.hostname or ""
    if parsed.scheme not in ("http", "https") or (host != "apple.com" and not host.endswith(".apple.com")):
        raise WWDRError(f"Refusing a CA Issuers URL on {host or url}.")
    try:
        with urllib.request.urlopen(url, timeout=20) as response:
            data = response.read()
    except OSError as exc:
        raise WWDRError(f"Fetching the WWDR certificate failed: {exc}") from exc
    if not data.startswith(b"\x30"):
        raise WWDRError("The CA Issuers URL did not return a certificate.")
    try:
        read(data)
    except CertificateError as exc:
        raise WWDRError("The CA Issuers URL did not return a certificate.") from exc
    _cache[url] = data
    return data
