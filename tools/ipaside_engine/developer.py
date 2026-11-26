"""Apple Developer Services client (developerservices2.apple.com).

Uses the scoped Xcode token minted by :mod:`ipaside_engine.gsa` to drive the
same private developer-portal API Xcode uses: list the team, issue a development
certificate from a CSR, register the device, create/reuse an App ID, and
download a provisioning profile. These are the steps that let a free Apple ID
sign an app for a specific device.

All requests are plist-over-HTTPS POSTs carrying a Xcode identity (clientId +
protocolVersion) plus anisette and the GS token, verified against the pinned
Apple CA bundle.
"""

from __future__ import annotations

import plistlib
import unicodedata
import uuid
from typing import Any

import requests

from . import anisette, gsa, tls
from .errors import EngineError

_BASE = "https://developerservices2.apple.com/services/QH65B2/"
_CLIENT_ID = "XABBG36SBA"
_PROTOCOL_VERSION = "QH65B2"
_USER_AGENT = "Xcode"
_XCODE_VERSION = "11.2 (11B41)"
_APP_INFO = "com.apple.gs.xcode.auth"
_TIMEOUT = 30


class DeveloperServicesError(EngineError):
    """Raised when developerservices2 returns a non-zero result code."""


def _headers(session: dict[str, Any], anisette_headers: dict[str, str]) -> dict[str, str]:
    headers = {
        "Content-Type": "text/x-xml-plist",
        "User-Agent": _USER_AGENT,
        "Accept": "text/x-xml-plist",
        "Accept-Language": "en-us",
        "X-Apple-App-Info": _APP_INFO,
        "X-Xcode-Version": _XCODE_VERSION,
        "X-Apple-I-Identity-Id": session["adsid"],
        "X-Apple-GS-Token": session["auth_token"],
        "X-Apple-I-Locale": anisette_headers.get("X-Apple-Locale", "en_US"),
    }
    headers.update(anisette_headers)
    return headers


def _request(
    endpoint: str, params: dict[str, Any] | None = None, team_id: str | None = None
) -> dict[str, Any]:
    """POST a plist request to a developerservices2 endpoint and parse the result."""
    session = gsa.load_session()
    body: dict[str, Any] = {
        "clientId": _CLIENT_ID,
        "protocolVersion": _PROTOCOL_VERSION,
        "requestId": str(uuid.uuid4()).upper(),
    }
    if team_id:
        body["teamId"] = team_id
    if params:
        body.update(params)

    resp = requests.post(
        f"{_BASE}{endpoint}?clientId={_CLIENT_ID}",
        headers=_headers(session, anisette.get_headers()),
        data=plistlib.dumps(body),
        timeout=_TIMEOUT,
        verify=tls.ca_bundle(),
    )
    resp.raise_for_status()
    result = plistlib.loads(resp.content)

    result_code = result.get("resultCode")
    if result_code not in (0, None, "0"):
        message = result.get("userString") or result.get("resultString") or "unknown error"
        raise DeveloperServicesError(f"{result_code}: {message}")
    return result


def list_teams() -> list[dict[str, Any]]:
    """Return the development teams available to the signed-in account."""
    result = _request("listTeams.action")
    return result.get("teams", [])


def _sanitize_name(name: str) -> str:
    """Apple App ID and device names accept ASCII letters, digits, and spaces.

    ``appIdName`` rejects anything else. ``str.isalnum`` treats ``é`` as a
    letter, so ``Pokémon GO`` was sent unchanged and Apple answered result
    code 35. Decompose first so ``é`` becomes ``e`` plus a mark, then drop
    the mark. The name on the phone is unchanged; only the portal label is.
    """
    folded = unicodedata.normalize("NFKD", name)
    ascii_name = folded.encode("ascii", "ignore").decode("ascii")
    cleaned = "".join(ch for ch in ascii_name if ch.isascii() and (ch.isalnum() or ch == " "))
    cleaned = " ".join(cleaned.split())
    return (cleaned[:50] or "iPASide")


# --------------------------------------------------------------------------- #
# Devices
# --------------------------------------------------------------------------- #
def list_devices(team_id: str) -> list[dict[str, Any]]:
    return _request("ios/listDevices.action", team_id=team_id).get("devices", [])


def register_device(team_id: str, udid: str, name: str = "iPASide device") -> dict[str, Any]:
    result = _request(
        "ios/addDevice.action",
        {"deviceNumber": udid, "name": _sanitize_name(name)},
        team_id=team_id,
    )
    return result.get("device", {})


# --------------------------------------------------------------------------- #
# Certificates
# --------------------------------------------------------------------------- #
def list_certificates(team_id: str) -> list[dict[str, Any]]:
    result = _request("ios/listAllDevelopmentCerts.action", team_id=team_id)
    return result.get("certificates", [])


def submit_csr(team_id: str, csr_pem: str, machine_name: str = "iPASide") -> dict[str, Any]:
    result = _request(
        "ios/submitDevelopmentCSR.action",
        {"csrContent": csr_pem, "machineId": str(uuid.uuid4()), "machineName": machine_name},
        team_id=team_id,
    )
    return result.get("certRequest", {})


def revoke_certificate(team_id: str, serial_number: str) -> None:
    _request(
        "ios/revokeDevelopmentCert.action",
        {"serialNumber": serial_number},
        team_id=team_id,
    )


# --------------------------------------------------------------------------- #
# App IDs
# --------------------------------------------------------------------------- #
def list_app_ids(team_id: str) -> list[dict[str, Any]]:
    return _request("ios/listAppIds.action", team_id=team_id).get("appIds", [])


def add_app_id(team_id: str, bundle_id: str, name: str) -> dict[str, Any]:
    result = _request(
        "ios/addAppId.action",
        {"identifier": bundle_id, "name": _sanitize_name(name)},
        team_id=team_id,
    )
    return result.get("appId", {})


def delete_app_id(team_id: str, app_id_id: str) -> None:
    """Delete a registered App ID, freeing one of a free account's 10 weekly slots."""
    _request("ios/deleteAppId.action", {"appIdId": app_id_id}, team_id=team_id)


def enable_app_id_feature(
    team_id: str, app_id_id: str, feature: str, enabled: bool = True
) -> dict[str, Any]:
    """Turn one App ID capability on or off.

    Features are identified by opaque Apple codes rather than names - see
    :data:`FEATURE_APP_GROUPS`. An entitlement only becomes signable once its
    capability is enabled here *and*, for app groups, the group is assigned.
    """
    result = _request(
        "ios/updateAppId.action",
        {"appIdId": app_id_id, feature: enabled},
        team_id=team_id,
    )
    return result.get("appId", {})


# --------------------------------------------------------------------------- #
# Application groups
#
# Free accounts can list, create and assign these, which is what makes it
# possible to sign apps that talk to each other through a shared container -
# LiveContainer being the one that matters here. Verified against a free account.
# --------------------------------------------------------------------------- #

#: App ID capability code for app groups, needed before a group can be assigned.
FEATURE_APP_GROUPS = "APG3427HIY"


def list_application_groups(team_id: str) -> list[dict[str, Any]]:
    """Return the team's app groups, each with an ``applicationGroup`` id."""
    result = _request("ios/listApplicationGroups.action", team_id=team_id)
    return result.get("applicationGroupList", [])


def add_application_group(team_id: str, identifier: str, name: str) -> dict[str, Any]:
    """Register an app group (``group.…`` identifier) and return it."""
    result = _request(
        "ios/addApplicationGroup.action",
        {"identifier": identifier, "name": _sanitize_name(name)},
        team_id=team_id,
    )
    return result.get("applicationGroup", {})


def assign_application_groups(team_id: str, app_id_id: str, group_ids: list[str]) -> None:
    """Attach app groups to an App ID, so its profile grants them.

    ``group_ids`` are the internal ``applicationGroup`` ids from
    :func:`list_application_groups`, not the ``group.…`` identifiers.
    """
    _request(
        "ios/assignApplicationGroupToAppId.action",
        {"appIdId": app_id_id, "applicationGroups": group_ids},
        team_id=team_id,
    )


# --------------------------------------------------------------------------- #
# Provisioning profiles
# --------------------------------------------------------------------------- #
def download_profile(team_id: str, app_id_id: str) -> dict[str, Any]:
    result = _request(
        "ios/downloadTeamProvisioningProfile.action",
        {"appIdId": app_id_id},
        team_id=team_id,
    )
    return result.get("provisioningProfile", {})
