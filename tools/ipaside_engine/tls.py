"""TLS trust for Apple endpoints.

Some Apple services - notably ``gsa.apple.com`` - present certificates that
chain to Apple's own ``Apple Root CA``, which is not present in public trust
stores (``certifi``) nor, on many machines, the OS store. Rather than disabling
certificate verification (a real MITM risk on the password-bearing auth flow),
we pin Apple's CA chain and verify against a combined bundle: the standard
public roots from ``certifi`` plus Apple's root + intermediate. This keeps every
request authenticated and works on any machine regardless of its trust store.
"""

from __future__ import annotations

from functools import lru_cache
from pathlib import Path

import certifi

from . import paths

_APPLE_CA = paths.resource_dir() / "certs" / "apple_gsa_ca.pem"


@lru_cache(maxsize=1)
def ca_bundle() -> str:
    """Return a path to a combined public + Apple CA bundle (built once)."""
    tls_dir = paths.data_dir() / "tls"
    tls_dir.mkdir(parents=True, exist_ok=True)
    combined = tls_dir / "apple_ca_bundle.pem"

    public = Path(certifi.where()).read_bytes()
    apple = _APPLE_CA.read_bytes()
    combined.write_bytes(public + b"\n" + apple)
    return str(combined)
