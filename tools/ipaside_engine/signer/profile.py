"""Read the plist inside a ``.mobileprovision``.

The profile is a CMS SignedData envelope with the plist embedded. The signature
is not checked here: Apple already delivered the profile over its own API.
"""

from __future__ import annotations

import plistlib

from .asn1 import ASN1Error, read_children, read_tlv


class ProfileError(Exception):
    pass


def read_plist(profile: bytes) -> dict:
    try:
        info = read_tlv(profile, 0)
        info_children = read_children(profile, info.content_range)
        if len(info_children) < 2:
            raise ProfileError("This provisioning profile is not a CMS blob.")
        explicit = read_children(profile, info_children[1].content_range)
        if not explicit:
            raise ProfileError("This provisioning profile is not a CMS blob.")
        signed = read_children(profile, explicit[0].content_range)
        if len(signed) < 3:
            raise ProfileError("This provisioning profile is not a CMS blob.")
        encap = read_children(profile, signed[2].content_range)
        if len(encap) < 2:
            raise ProfileError("This provisioning profile has no embedded plist.")
        content = read_children(profile, encap[1].content_range)
        if not content:
            raise ProfileError("This provisioning profile has no embedded plist.")
        payload = profile[content[0].content_range.start:content[0].content_range.stop]
    except ASN1Error as exc:
        raise ProfileError("This provisioning profile is not a CMS blob.") from exc
    try:
        parsed = plistlib.loads(payload)
    except Exception as exc:
        raise ProfileError("This provisioning profile's plist could not be read.") from exc
    if not isinstance(parsed, dict):
        raise ProfileError("This provisioning profile's plist could not be read.")
    return parsed
