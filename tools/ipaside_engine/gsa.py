"""Apple GrandSlam (GSA) authentication.

Implements Apple's modified SRP-6a login against ``gsa.apple.com/grandslam``,
using anisette headers from :mod:`ipaside_engine.anisette`. Ported faithfully
from the community GrandSlam implementations (JJTech0130 / nythepegasus), with
five production changes: (1) anisette comes from our in-process provider rather
than a remote server, (2) TLS verification stays on, (3) two-factor auth is
a two-step flow (trigger, then submit a code) so it works across separate CLI
invocations and maps cleanly onto a GUI, (4) every ``gsa.apple.com`` request
uses its own HTTP session and then closes it, and (5) GSA requests present
``com.apple.akd/1.0`` instead of the Anisette package's ``com.apple.dt.Xcode``
client token.

Apple's GsService2 edge returns HTML ``HTTP 503`` for two independent reasons.
Keep-alive: the front-end serves two requests per connection; the third is
HTML 503. Sign-in is three requests on that host (SRP ``init``, SRP
``complete``, then ``o=apptokens`` or the 2FA trigger), so a pooled connection
fails before 2FA can complete. That is GitHub issue #7 / AltStore #1782. A
retry on the spent socket still 503s; a new session does not. Client token:
the same endpoint drops the request at the edge when ``X-MMe-Client-Info``
contains ``com.apple.dt.Xcode`` (AltStore #1790). Separately, since 2026-10-12 it answers a
Mac identity with ``-80045`` at SRP init; :data:`ipaside_engine.anisette.CLIENT_INFO` is what
anisette headers now carry (see there). Anisette still reports
``Xcode/3594.4.19``; bumping the version still 503s. ``com.apple.akd/1.0``
reaches the service. Isolation does not pass the client-token block; swapping
the Xcode version does not pass the keep-alive block. Both are required.

The password is never stored; only short-lived session tokens (adsid, IDMS
token) are cached, under the per-user data dir, never in source control.
"""

from __future__ import annotations

import base64
import contextlib
import contextvars
import hashlib
import hmac
import json
import plistlib
import re
from collections.abc import Iterator
from datetime import datetime
from typing import Any

import requests
import srp._pysrp as srp
from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from requests.adapters import HTTPAdapter

from . import anisette, paths, tls
from .errors import EngineError

# Configure the SRP library for Apple's variant (SHA-256, 2048-bit group,
# username excluded from the x computation).
srp.rfc5054_enable()
srp.no_username_in_x()

_GS_ENDPOINT = "https://gsa.apple.com/grandslam/GsService2"
_TRUSTED_TRIGGER = "https://gsa.apple.com/auth/verify/trusteddevice"
_VALIDATE = "https://gsa.apple.com/grandslam/GsService2/validate"
_SMS_ENDPOINT = "https://gsa.apple.com/auth/verify/phone"
_SMS_SUBMIT = "https://gsa.apple.com/auth/verify/phone/securitycode"
#: Answers with the account's trusted phone numbers (and their ids) as JSON.
_AUTH_INFO = "https://gsa.apple.com/auth"
#: Apple's first trusted number. Only a guess, used when Apple will not list the real ones.
_DEFAULT_PHONE_ID = 1
#: ``serviceErrors`` codes that mean "a text was already sent; enter the last code".
_SMS_CODE_STILL_VALID = frozenset({"-22979", "-22981"})

_GS_USER_AGENT = "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0"
_XCODE_APP_INFO = "com.apple.gs.xcode.auth"
_XCODE_VERSION = "11.2 (11B41)"
_TIMEOUT = 30
# Apple's GsService2 edge refuses any client token matching this (HTML 503).
# Version does not matter; the substring ``com.apple.dt.Xcode`` is enough.
_BLOCKED_GSA_CLIENT_TOKEN = re.compile(r"com\.apple\.dt\.Xcode/[^)\s>]+")
_GSA_CLIENT_TOKEN = "com.apple.akd/1.0"

#: How the SRP ``complete`` reply says a second factor is needed. Until 2026-10 that was
#: ``Status.au`` (``trustedDeviceSecondaryAuth`` or ``secondaryAuth``). Apple now also pushes the
#: code to the person's devices during ``complete`` itself and flags it with ``Status.hsc == 409``
#: and no ``au``. Treating that as "no second factor" went straight on to ``apptokens``, which
#: Apple refuses with ``-22411`` / ``hsc 401``.
_HSC_CODE_ALREADY_SENT = 409
SECONDARY_CODE_ALREADY_SENT = "codeAlreadySent"
#: A text-message challenge (``au == "secondaryAuth"``) that also carries ``hsc 409``. Apple may
#: have pushed a code to the person's devices as well, so if the text itself is refused that code
#: is still worth asking for.
SECONDARY_SMS_CODE_ALREADY_SENT = "secondaryAuthCodeAlreadySent"
_AU_SMS = "secondaryAuth"
#: ``ec`` of ``init`` for an account that wants a newer macOS than the identity claims: "A Mac with
#: macOS version 10.13 or later is required to sign in with this Apple Account."
_EC_NEWER_MAC_REQUIRED = -23782
#: ``au == "repair"`` means the account has no second factor set up; it is not a challenge.
_AU_NO_SECOND_FACTOR = "repair"

_PLIST_PROLOG = (
    b'<?xml version="1.0" encoding="UTF-8"?>\n'
    b'<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" '
    b'"http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
)


class GsaError(EngineError):
    """Raised when Apple returns an authentication error."""


# --------------------------------------------------------------------------- #
# Request plumbing
# --------------------------------------------------------------------------- #
def _cpd(headers: dict[str, str]) -> dict[str, Any]:
    """Client-provided data: flags + anisette (client-info goes in headers)."""
    cpd: dict[str, Any] = {
        "bootstrap": True,
        "icscrec": True,
        "pbe": False,
        "prkgen": True,
        "svct": "iCloud",
        "loc": headers.get("X-Apple-Locale", "en_US"),
    }
    for key, value in headers.items():
        if key != "X-MMe-Client-Info":
            cpd[key] = value
    return cpd


def _gsa_client_info(value: str) -> str:
    """Rewrite anisette client-info so Apple's GSA edge will accept it.

    Since 2026-09, ``POST /grandslam/GsService2`` is dropped at the edge
    (HTML 503) when ``X-MMe-Client-Info`` contains ``com.apple.dt.Xcode``.
    Version bumps still 503; ``com.apple.akd/1.0`` reaches the service
    (AltStore #1790, live-checked 2026-09-18 against gsa.apple.com). The
    Anisette package still reports ``Xcode/3594.4.19``. ``akd`` is the
    daemon that performs this request on macOS. Developer-services
    requests are a different host and keep the original anisette string.
    """
    if not value:
        return value
    return _BLOCKED_GSA_CLIENT_TOKEN.sub(_GSA_CLIENT_TOKEN, value)


def _html_error_page(content: bytes) -> bool:
    snippet = content.lstrip()[:64].lower()
    return snippet.startswith(b"<html") or snippet.startswith(b"<!doctype")


_TRACE_FILE = "gsa-trace.log"
_TRACE_MAX_BYTES = 200_000
_TRACE_STATUS_KEYS = ("ec", "em", "hsc", "au")


def _json_trace_parts(content: bytes) -> list[str]:
    """What a JSON reply (the text-message endpoints) adds to the trace: field names, Apple's error
    code and how many trusted numbers it listed. Never a value."""
    try:
        parsed = json.loads(content)
    except Exception:  # noqa: BLE001 - empty bodies and HTML are not JSON
        return []
    if not isinstance(parsed, dict):
        return []
    parts = ["json=" + ",".join(sorted(str(k) for k in parsed))]
    errors = parsed.get("serviceErrors")
    if isinstance(errors, list) and errors and isinstance(errors[0], dict):
        code = errors[0].get("code")
        if isinstance(code, (str, int)):
            parts.append(f"svc={code}")
    numbers = parsed.get("trustedPhoneNumbers")
    if isinstance(numbers, list):
        parts.append(f"numbers={len(numbers)}")
    return parts


def _trace(label: str, response: requests.Response) -> None:
    """Append one line about a GrandSlam exchange to ``gsa-trace.log`` in the data folder.

    Apple changes what sign-in answers often, and a failure report is far more useful with the
    step, HTTP status and Apple's own error code than with a bare message. Only those are written:
    never a request, a response body, a password or a token. Failures to write are ignored.
    """
    try:
        parts = [
            datetime.now().isoformat(timespec="seconds"),
            label,
            f"http={response.status_code}",
        ]
        content = response.content
        if _html_error_page(content):
            parts.append("html")
        else:
            try:
                parsed = _loads_plist(content)
            except Exception:  # noqa: BLE001 - not every endpoint answers with a plist
                parsed = None
            if isinstance(parsed, dict):
                body = parsed.get("Response", parsed)
                if isinstance(body, dict):
                    status = body.get("Status", body)
                    if isinstance(status, dict):
                        for key in _TRACE_STATUS_KEYS:
                            if key in status:
                                parts.append(f"{key}={status[key]!r}")
                    parts.append("keys=" + ",".join(sorted(str(k) for k in body)))
            else:
                parts.extend(_json_trace_parts(content))
        path = paths.data_dir() / _TRACE_FILE
        if path.exists() and path.stat().st_size > _TRACE_MAX_BYTES:
            tail = path.read_bytes()[-_TRACE_MAX_BYTES // 2 :]
            path.write_bytes(tail[tail.find(b"\n") + 1 :])
        with path.open("a", encoding="utf-8") as handle:
            handle.write(" ".join(parts) + "\n")
    except Exception:  # noqa: BLE001 - diagnostics must never break sign-in
        return


def _gsa_spent_connection(response: requests.Response) -> bool:
    """True when Apple answered HTML 503 on a spent keep-alive connection."""
    if response.status_code != 503:
        return False
    content_type = (response.headers.get("Content-Type") or "").lower()
    if "html" in content_type:
        return True
    return _html_error_page(response.content)


def _gsa_http_once(
    method: str,
    url: str,
    *,
    headers: dict[str, str],
    data: bytes | None = None,
) -> requests.Response:
    """One GrandSlam request on a throwaway session, then invalidate it.

    Equivalent to AltStore's per-request ``NSURLSession`` plus
    ``finishTasksAndInvalidate``. ``Connection: close`` tells Apple not to keep
    the socket; ``max_retries=0`` stops urllib3 from retrying on that socket.
    """
    request_headers = dict(headers)
    request_headers["Connection"] = "close"
    verify: str | bool = tls.ca_bundle() if url.lower().startswith("https://") else False
    with requests.Session() as session:
        adapter = HTTPAdapter(pool_connections=1, pool_maxsize=1, max_retries=0)
        session.mount("https://", adapter)
        session.mount("http://", adapter)
        response = session.request(
            method,
            url,
            headers=request_headers,
            data=data,
            timeout=_TIMEOUT,
            verify=verify,
        )
        response.content
        return response


def _gsa_http(
    method: str,
    url: str,
    *,
    headers: dict[str, str],
    data: bytes | None = None,
) -> requests.Response:
    """Send a GSA request, never reusing a spent GrandSlam connection.

    If the first attempt still returns HTML 503 (proxy coalescing, or a pool
    that ignored ``Connection: close``), try once more on a *new* session.
    Retrying the spent connection is what AltStore measured as still 503.
    """
    response = _gsa_http_once(method, url, headers=headers, data=data)
    if _gsa_spent_connection(response):
        response = _gsa_http_once(method, url, headers=headers, data=data)
    return response


def _raise_for_gsa_http(response: requests.Response) -> None:
    """Map Apple HTTP failures to :class:`GsaError` instead of a raw HTTPError."""
    if _gsa_spent_connection(response):
        raise GsaError(
            "Apple sign-in failed with HTTP 503 from "
            f"{response.url}. Try signing in again."
        )
    if response.status_code >= 400:
        raise GsaError(
            f"Apple sign-in failed (HTTP {response.status_code}). "
            "Check your connection and try again."
        )


def _parse_gs_response(response: requests.Response) -> dict[str, Any]:
    """Parse a GsService2 body. HTML 503 must not be treated as a plist."""
    _raise_for_gsa_http(response)
    parsed = _parse_gsa_plist(response.content)
    body = parsed.get("Response")
    if not isinstance(body, dict):
        raise GsaError("Apple sign-in returned a response that could not be read.")
    return body


def _parse_gsa_plist(content: bytes) -> dict[str, Any]:
    """Parse a GrandSlam plist. HTML and other garbage become :class:`GsaError`."""
    if _html_error_page(content):
        raise GsaError(
            "Apple sign-in returned an HTML error page instead of a GrandSlam "
            "response. Try signing in again."
        )
    try:
        parsed = _loads_plist(content)
    except Exception as exc:
        raise GsaError(
            "Apple sign-in returned a response that could not be read."
        ) from exc
    if not isinstance(parsed, dict):
        raise GsaError("Apple sign-in returned a response that could not be read.")
    return parsed


def _gs_request(params: dict[str, Any], headers: dict[str, str]) -> dict[str, Any]:
    body = {
        "Header": {"Version": "1.0.1"},
        "Request": {"cpd": _cpd(headers), **params},
    }
    req_headers = {
        "Content-Type": "text/x-xml-plist",
        "Accept": "*/*",
        "User-Agent": anisette.gsa_user_agent() or _GS_USER_AGENT,
        "X-MMe-Client-Info": _gsa_client_info(headers.get("X-MMe-Client-Info", "")),
    }
    resp = _gsa_http(
        "POST",
        _GS_ENDPOINT,
        headers=req_headers,
        data=plistlib.dumps(body),
    )
    _trace(f"GsService2/{params.get('o', '?')}", resp)
    return _parse_gs_response(resp)


def _init_ec(response: dict[str, Any]) -> Any:
    status = response.get("Status", response)
    return status.get("ec", 0) if isinstance(status, dict) else 0


def _check(response: dict[str, Any]) -> None:
    status = response.get("Status", response)
    ec = status.get("ec", 0)
    if ec != 0:
        raise GsaError(f"Apple error {ec}: {status.get('em', 'unknown error')}")


# --------------------------------------------------------------------------- #
# SRP crypto
# --------------------------------------------------------------------------- #
def _derive_password(password: str, salt: bytes, iterations: int, s2k_fo: bool) -> bytes:
    digest = hashlib.sha256(password.encode("utf-8")).digest()
    if s2k_fo:
        digest = digest.hex().encode("utf-8")
    return hashlib.pbkdf2_hmac("sha256", digest, salt, iterations, 32)


def _session_hmac(session_key: bytes, label: str) -> bytes:
    return hmac.new(session_key, label.encode(), hashlib.sha256).digest()


def _loads_plist(raw: bytes) -> dict[str, Any]:
    """Parse a GSA plist payload, tolerating a missing XML prolog."""
    try:
        return plistlib.loads(raw)
    except Exception:  # noqa: BLE001 - GSA omits the XML prolog on some responses
        return plistlib.loads(_PLIST_PROLOG + raw)


def _decrypt_spd(session_key: bytes, data: bytes) -> dict[str, Any]:
    key = _session_hmac(session_key, "extra data key:")
    iv = _session_hmac(session_key, "extra data iv:")[:16]
    cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
    decryptor = cipher.decryptor()
    raw = decryptor.update(data) + decryptor.finalize()
    unpadder = padding.PKCS7(128).unpadder()
    raw = unpadder.update(raw) + unpadder.finalize()
    return _loads_plist(raw)


def _secondary_auth(status: dict[str, Any]) -> str | None:
    """The second factor a ``complete`` reply asks for, or None when the sign-in is done.

    Returns Apple's ``au`` value, or :data:`SECONDARY_CODE_ALREADY_SENT` when Apple has already
    pushed a trusted-device code (``hsc 409``) without naming one. A text-message challenge that
    also carries ``hsc 409`` is :data:`SECONDARY_SMS_CODE_ALREADY_SENT`.
    """
    au = status.get("au")
    if au == _AU_NO_SECOND_FACTOR:
        return None
    if au == _AU_SMS and status.get("hsc") == _HSC_CODE_ALREADY_SENT:
        return SECONDARY_SMS_CODE_ALREADY_SENT
    if au:
        return au
    if status.get("hsc") == _HSC_CODE_ALREADY_SENT:
        return SECONDARY_CODE_ALREADY_SENT
    return None


def _authenticate_once(
    email: str, password: str, headers: dict[str, str]
) -> tuple[dict[str, Any], str | None]:
    """Run one full SRP handshake. Returns (session-data, secondary-auth-kind)."""
    usr = srp.User(email, b"", hash_alg=srp.SHA256, ng_type=srp.NG_2048)
    _, a_pub = usr.start_authentication()

    init_params = {"A2k": a_pub, "ps": ["s2k", "s2k_fo"], "u": email, "o": "init"}
    init = _gs_request(init_params, headers)
    if _init_ec(init) == _EC_NEWER_MAC_REQUIRED and anisette.use_newer_mac_identity():
        # The services' Mac identity says macOS 10.12.6 and this account wants 10.13 or later.
        # Present a newer one and ask again; ``headers`` is updated in place so the rest of this
        # sign-in (and the token request the caller makes with it) uses the same identity.
        fresh = anisette.get_headers()
        headers.clear()
        headers.update(fresh)
        init = _gs_request(init_params, headers)
        if _init_ec(init) == _EC_NEWER_MAC_REQUIRED:
            raise GsaError(
                f"Apple error {_EC_NEWER_MAC_REQUIRED}: this Apple Account needs a Mac with macOS "
                "10.13 or later, and Apple still refused the newer Mac identity iPASide then tried. "
                "Nothing is wrong with your password."
            )
    _check(init)

    protocol = init.get("sp", "s2k")
    if protocol not in ("s2k", "s2k_fo"):
        raise GsaError(f"unsupported SRP protocol from server: {protocol}")

    # Feed the salted-and-iterated password in now that we have the salt.
    usr.p = _derive_password(password, init["s"], init["i"], protocol == "s2k_fo")
    m1 = usr.process_challenge(init["s"], init["B"])
    if m1 is None:
        raise GsaError("failed to process SRP challenge (bad server response)")

    complete = _gs_request(
        {"c": init["c"], "M1": m1, "u": email, "o": "complete"}, headers
    )
    _check(complete)

    usr.verify_session(complete["M2"])
    if not usr.authenticated():
        raise GsaError("server session verification failed (possible MITM)")

    spd = _decrypt_spd(usr.get_session_key(), complete["spd"])
    secondary = _secondary_auth(complete.get("Status", {}))
    return spd, secondary


# --------------------------------------------------------------------------- #
# Two-factor auth
# --------------------------------------------------------------------------- #
def _identity_token(adsid: str, idms_token: str) -> str:
    return base64.b64encode(f"{adsid}:{idms_token}".encode()).decode()


def _twofa_headers(adsid: str, idms_token: str, headers: dict[str, str]) -> dict[str, str]:
    out = {
        "Content-Type": "text/x-xml-plist",
        "User-Agent": "Xcode",
        "Accept": "text/x-xml-plist",
        "Accept-Language": "en-us",
        "X-Apple-Identity-Token": _identity_token(adsid, idms_token),
        "X-Apple-App-Info": _XCODE_APP_INFO,
        "X-Xcode-Version": _XCODE_VERSION,
    }
    out.update(headers)
    raw = out.get("X-MMe-Client-Info", "")
    if isinstance(raw, str):
        out["X-MMe-Client-Info"] = _gsa_client_info(raw)
    return out


def _trigger_trusted(adsid: str, idms_token: str, headers: dict[str, str]) -> None:
    """Ask Apple to push a trusted-device 2FA code.

    The response body is an HTML interstitial even on success, so status is the signal.
    Ignoring a non-2xx (as we used to) is what made issue #5 look like "code was sent"
    in the UI while Apple had actually rejected the request with HTTP 500 over a bad
    ``X-Apple-Locale``.
    """
    resp = _gsa_http(
        "GET",
        _TRUSTED_TRIGGER,
        headers=_twofa_headers(adsid, idms_token, headers),
    )
    _trace("trusteddevice/trigger", resp)
    if resp.status_code != 200:
        raise GsaError(
            f"Apple did not send a verification code (HTTP {resp.status_code}). "
            "Check your connection and try signing in again."
        )


def _submit_trusted(adsid: str, idms_token: str, code: str, headers: dict[str, str]) -> None:
    req_headers = _twofa_headers(adsid, idms_token, headers)
    req_headers["security-code"] = code
    resp = _gsa_http("GET", _VALIDATE, headers=req_headers)
    _trace("trusteddevice/validate", resp)
    _raise_for_gsa_http(resp)
    _check(_parse_gsa_plist(resp.content))


def _sms_headers(adsid: str, idms_token: str, headers: dict[str, str]) -> dict[str, str]:
    """Headers for GrandSlam SMS 2FA JSON endpoints on gsa.apple.com."""
    out = {
        "Content-Type": "application/json; charset=utf-8",
        "Accept": "application/json, text/html",
        "Accept-Language": "en-us",
        "User-Agent": "Xcode",
        "X-Apple-Identity-Token": _identity_token(adsid, idms_token),
        "X-Apple-App-Info": _XCODE_APP_INFO,
        "X-Xcode-Version": _XCODE_VERSION,
    }
    out.update(headers)
    raw = out.get("X-MMe-Client-Info", "")
    if isinstance(raw, str):
        out["X-MMe-Client-Info"] = _gsa_client_info(raw)
    out["Content-Type"] = "application/json; charset=utf-8"
    return out


def _sms_body(code: str | None = None, phone_id: int = _DEFAULT_PHONE_ID) -> bytes:
    """JSON body for SMS trigger (no code) or submit (with code).

    ``phone_id`` is one of the ids Apple lists for the account's trusted numbers. Id ``1`` is only
    the guess community GrandSlam clients make, right when the account has a single number that
    was never replaced.
    """
    payload: dict[str, Any] = {"phoneNumber": {"id": phone_id}, "mode": "sms"}
    if code is not None:
        payload["securityCode"] = {"code": code}
    return json.dumps(payload).encode("utf-8")


def _trusted_phone_numbers(
    adsid: str, idms_token: str, headers: dict[str, str]
) -> list[dict[str, Any]]:
    """The account's trusted phone numbers as Apple lists them, or [] when it will not say.

    Each entry carries an ``id`` (what the text-message endpoints take), ``lastTwoDigits`` and
    ``pushMode``. Asking is best effort: a failure here must not stop a sign-in that would work
    with the default id.
    """
    try:
        resp = _gsa_http("GET", _AUTH_INFO, headers=_sms_headers(adsid, idms_token, headers))
    except Exception:  # noqa: BLE001 - the trigger below still has a sensible default
        return []
    _trace("auth/numbers", resp)
    try:
        parsed = json.loads(resp.content)
    except Exception:  # noqa: BLE001 - an HTML page or an empty body
        return []
    listed = parsed.get("trustedPhoneNumbers") if isinstance(parsed, dict) else None
    if not isinstance(listed, list):
        return []
    return [
        item
        for item in listed
        if isinstance(item, dict)
        and isinstance(item.get("id"), int)
        and not isinstance(item.get("id"), bool)
    ]


def _pick_phone_id(numbers: list[dict[str, Any]]) -> int:
    """The number to text: the first one Apple delivers by SMS, else the first, else the default."""
    for item in numbers:
        if item.get("pushMode") == "sms":
            return item["id"]
    if numbers:
        return numbers[0]["id"]
    return _DEFAULT_PHONE_ID


def _sms_code_still_valid(resp: requests.Response) -> bool:
    """True when Apple declined a new text only because one is already on its way.

    That is ``serviceErrors`` -22979 / -22981 (too many codes sent, enter the last one), or HTTP 412
    carrying an open challenge. The person still has a valid code, so they should be asked for it.
    """
    if _gsa_json_service_code(resp) in _SMS_CODE_STILL_VALID:
        return True
    if resp.status_code != 412:
        return False
    try:
        parsed = json.loads(resp.content)
    except Exception:  # noqa: BLE001
        return False
    code = parsed.get("securityCode") if isinstance(parsed, dict) else None
    if not isinstance(code, dict):
        return False
    blocked = ("tooManyCodesValidated", "securityCodeLocked", "securityCodeCooldown")
    return not any(code.get(flag) for flag in blocked)


def _trigger_sms(adsid: str, idms_token: str, headers: dict[str, str]) -> int:
    """Ask Apple to SMS a verification code to the account's trusted number.

    Returns the phone id the text went to; the code has to be submitted with the same one.

    ``PUT /auth/verify/phone`` (no trailing slash) is the live method: a
    trailing slash is HTTP 405. HTTP 423 means Apple already sent codes
    (``tooManyCodesSent``); the user should enter the last one.
    """
    phone_id = _pick_phone_id(_trusted_phone_numbers(adsid, idms_token, headers))
    resp = _gsa_http(
        "PUT",
        _SMS_ENDPOINT,
        headers=_sms_headers(adsid, idms_token, headers),
        data=_sms_body(phone_id=phone_id),
    )
    _trace("sms/trigger", resp)
    if _gsa_spent_connection(resp):
        _raise_for_gsa_http(resp)
    if resp.status_code in (200, 201, 202, 423) or _sms_code_still_valid(resp):
        return phone_id
    detail = _gsa_json_service_error(resp)
    message = f"Apple would not text a verification code (HTTP {resp.status_code})"
    if detail:
        raise GsaError(f"{message}: {detail}.")
    raise GsaError(
        f"{message}. That is Apple's answer to the request, not a wrong password; "
        "wait a few minutes and try again."
    )


def _gsa_json_service_code(response: requests.Response) -> str | None:
    """Apple's JSON 2FA error code (``-22979``...), or None if the body has none."""
    try:
        parsed = json.loads(response.content)
    except Exception:  # noqa: BLE001 - HTML 503 and empty bodies are not JSON
        return None
    errors = parsed.get("serviceErrors") if isinstance(parsed, dict) else None
    if not isinstance(errors, list) or not errors or not isinstance(errors[0], dict):
        return None
    code = errors[0].get("code")
    if isinstance(code, bool) or not isinstance(code, (str, int)):
        return None
    return str(code)


def _gsa_json_service_error(response: requests.Response) -> str | None:
    """Return Apple's JSON 2FA error text, or None if the body is not that."""
    try:
        parsed = json.loads(response.content)
    except Exception:  # noqa: BLE001 - HTML 503 and empty bodies are not JSON
        return None
    if not isinstance(parsed, dict):
        return None
    errors = parsed.get("serviceErrors")
    if not isinstance(errors, list) or not errors:
        return None
    first = errors[0]
    if not isinstance(first, dict):
        return None
    parts = [
        part.strip()
        for part in (first.get("title"), first.get("message"))
        if isinstance(part, str) and part.strip()
    ]
    if not parts:
        return None
    return ": ".join(dict.fromkeys(parts))


def _submit_sms(
    adsid: str,
    idms_token: str,
    code: str,
    headers: dict[str, str],
    phone_id: int = _DEFAULT_PHONE_ID,
) -> None:
    resp = _gsa_http(
        "POST",
        _SMS_SUBMIT,
        headers=_sms_headers(adsid, idms_token, headers),
        data=_sms_body(code, phone_id),
    )
    _trace("sms/submit", resp)
    if _gsa_spent_connection(resp):
        _raise_for_gsa_http(resp)
    if resp.status_code == 200:
        return
    detail = _gsa_json_service_error(resp)
    if detail:
        raise GsaError(detail)
    _raise_for_gsa_http(resp)
    raise GsaError(
        f"Apple rejected the verification code (HTTP {resp.status_code}). "
        "Check the code and try again."
    )


# --------------------------------------------------------------------------- #
# App-token exchange (per-service Xcode token for developer services)
# --------------------------------------------------------------------------- #
_XCODE_AUTH_APP = "com.apple.gs.xcode.auth"


def _coerce_bytes(value: Any) -> bytes:
    if isinstance(value, bytes):
        return value
    if isinstance(value, str):
        return base64.b64decode(value)
    raise GsaError("expected bytes or base64 string in session data")


def _app_tokens_checksum(sk: bytes, adsid: str, apps: list[str]) -> bytes:
    mac = hmac.new(sk, b"apptokens" + adsid.encode(), hashlib.sha256)
    for app in apps:
        mac.update(app.encode())
    return mac.digest()


def _decrypt_gcm(sk: bytes, encrypted: bytes) -> bytes:
    # Apple wire format: "XYZ" (3 bytes, also the AAD) | IV (16) | ciphertext | tag (16).
    if len(encrypted) < 35 or encrypted[:3] != b"XYZ":
        raise GsaError("malformed encrypted app token")
    iv = encrypted[3:19]
    ciphertext_and_tag = encrypted[19:]
    return AESGCM(sk).decrypt(iv, ciphertext_and_tag, b"XYZ")


def _fetch_app_token(
    spd: dict[str, Any], headers: dict[str, str], app: str = _XCODE_AUTH_APP
) -> dict[str, Any]:
    """Exchange the GSA session for a scoped Xcode token (X-Apple-GS-Token)."""
    sk = _coerce_bytes(spd["sk"])
    adsid = spd["adsid"]
    params = {
        "u": adsid,
        "app": [app],
        "c": spd["c"],
        "t": spd["GsIdmsToken"],
        "checksum": _app_tokens_checksum(sk, adsid, [app]),
        "o": "apptokens",
    }
    response = _gs_request(params, headers)
    _check(response)
    token_plist = _loads_plist(_decrypt_gcm(sk, response["et"]))
    token_info = token_plist["t"][app]
    return {"token": token_info["token"], "expiry": token_info.get("expiry")}


def _finalize_session(email: str, spd: dict[str, Any], headers: dict[str, str]) -> dict[str, Any]:
    """Mint the developer-services token and persist the authenticated session."""
    app_token = _fetch_app_token(spd, headers)
    _save_account(email, spd, app_token)
    _clear_pending()
    return {
        "status": "authenticated",
        "adsid": spd.get("adsid"),
        "auth_token_acquired": bool(app_token.get("token")),
    }


# --------------------------------------------------------------------------- #
# Session persistence
# --------------------------------------------------------------------------- #
def _save_pending(
    email: str, adsid: str, idms_token: str, method: str, *, phone_id: int = _DEFAULT_PHONE_ID
) -> None:
    paths.pending_2fa_file().write_text(
        json.dumps(
            {"email": email, "adsid": adsid, "idms": idms_token, "method": method, "phone_id": phone_id}
        ),
        encoding="utf-8",
    )


def _load_pending() -> dict[str, Any] | None:
    path = paths.pending_2fa_file()
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def _clear_pending() -> None:
    paths.pending_2fa_file().unlink(missing_ok=True)


def _migrate_legacy_account() -> None:
    """Moves a single-account build's session into the multi-account layout.

    Runs on every read, which is cheap and means an upgrade never signs anyone out
    and never leaves two copies of the same session to disagree with each other.
    """
    legacy = paths.legacy_account_file()
    if not legacy.exists():
        return
    try:
        data = json.loads(legacy.read_text(encoding="utf-8"))
        email = data.get("email")
        if email:
            target = paths.account_file(email)
            if not target.exists():
                target.write_text(json.dumps(data), encoding="utf-8")
            _set_active(email)
    except (OSError, ValueError):
        # A corrupt legacy file is not worth failing a login over; it is about to
        # be replaced anyway.
        pass
    legacy.unlink(missing_ok=True)


def _set_active(email: str) -> None:
    paths.active_account_file().write_text(
        json.dumps({"email": email}), encoding="utf-8"
    )


def _active_email() -> str | None:
    path = paths.active_account_file()
    if path.exists():
        try:
            email = json.loads(path.read_text(encoding="utf-8")).get("email")
        except ValueError:
            email = None
        if email and paths.account_file(email).exists():
            return email

    # No pointer, or it names an account that has since been removed: fall back to
    # the only one there is, so a single-account user never has to choose.
    known = list_accounts()
    if len(known) == 1:
        _set_active(known[0]["email"])
        return known[0]["email"]
    return None


def _save_account(email: str, spd: dict[str, Any], app_token: dict[str, Any]) -> None:
    path = paths.account_file(email)
    existing: dict[str, Any] = {}
    if path.exists():
        try:
            existing = json.loads(path.read_text(encoding="utf-8"))
        except ValueError:
            existing = {}

    path.write_text(
        json.dumps(
            {
                # team_id is learned later, by provisioning; carry it across a
                # re-login so refreshing an app can still find its account.
                **{k: v for k, v in existing.items() if k == "team_id"},
                "email": email,
                "adsid": spd.get("adsid"),
                "GsIdmsToken": spd.get("GsIdmsToken"),
                "auth_token": app_token.get("token"),
                "auth_token_expiry": app_token.get("expiry"),
            }
        ),
        encoding="utf-8",
    )
    _set_active(email)


def _read_account(email: str) -> dict[str, Any]:
    path = paths.account_file(email)
    if not path.exists():
        raise GsaError(f"{email} is not signed in.")
    return json.loads(path.read_text(encoding="utf-8"))


def list_accounts() -> list[dict[str, Any]]:
    """Every signed-in Apple ID, newest sign-in first."""
    accounts = []
    for path in paths.accounts_dir().glob("*.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not data.get("email"):
            continue
        accounts.append(
            {
                "email": data["email"],
                "adsid": data.get("adsid"),
                "team_id": data.get("team_id"),
                "has_auth_token": bool(data.get("auth_token")),
                "signed_in_at": path.stat().st_mtime,
            }
        )
    accounts.sort(key=lambda a: a["signed_in_at"], reverse=True)
    return accounts


def accounts() -> dict[str, Any]:
    """The signed-in accounts and which one is active."""
    _migrate_legacy_account()
    known = list_accounts()
    active = _active_email()
    for account in known:
        account["active"] = account["email"] == active
    return {"accounts": known, "active": active}


def use_account(email: str) -> dict[str, Any]:
    """Make an already signed-in account the active one."""
    _migrate_legacy_account()
    data = _read_account(email)
    if not data.get("auth_token"):
        raise GsaError(f"{email} has no developer token; sign in again.")
    _set_active(data["email"])
    return {"status": "active", "email": data["email"]}


def remember_team(email: str, team_id: str) -> None:
    """Records which developer team an account provisions under.

    This is what lets a refresh run under the Apple ID that signed the app rather
    than whichever one happens to be active — sign in with a second account and
    refresh without it, and the app is re-signed by a different team, which iOS
    will not install over the existing copy.
    """
    path = paths.account_file(email)
    if not path.exists():
        return
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except ValueError:
        return
    if data.get("team_id") == team_id:
        return
    data["team_id"] = team_id
    path.write_text(json.dumps(data), encoding="utf-8")


def account_for_team(team_id: str) -> str | None:
    """The signed-in account that provisions under [team_id], if any."""
    _migrate_legacy_account()
    for account in list_accounts():
        if account.get("team_id") == team_id:
            return account["email"]
    return None


_acting_as: contextvars.ContextVar[str | None] = contextvars.ContextVar(
    "ipaside_acting_as", default=None
)


@contextlib.contextmanager
def acting_as(email: str | None) -> Iterator[None]:
    """Run a block as [email] rather than the active account.

    Every call into Apple's developer services loads the session for itself, so a
    refresh that must run under the Apple ID which signed the app cannot simply
    pass an address down — there are a dozen call sites in between. Overriding it
    here, for the duration of the block, keeps that plumbing out of the callers and
    leaves the user's own choice of active account untouched on disk.
    """
    if not email:
        yield
        return
    token = _acting_as.set(email)
    try:
        yield
    finally:
        _acting_as.reset(token)


def load_session(email: str | None = None) -> dict[str, Any]:
    """Return a cached developer-services session (adsid + Xcode auth token).

    Defaults to whichever account [acting_as] names, then the active one; pass
    [email] to be explicit.
    """
    _migrate_legacy_account()
    target = email or _acting_as.get() or _active_email()
    if not target:
        if list_accounts():
            raise GsaError(
                "more than one Apple ID is signed in and none is selected; "
                "choose one with 'login --use <email>'."
            )
        raise GsaError("not signed in; run 'login' first")

    data = _read_account(target)
    if not data.get("auth_token"):
        raise GsaError("cached session has no developer token; sign in again")
    return {
        "adsid": data["adsid"],
        "auth_token": data["auth_token"],
        "email": data.get("email"),
    }


# --------------------------------------------------------------------------- #
# Public API
# --------------------------------------------------------------------------- #
def begin_login(email: str, password: str) -> dict[str, Any]:
    """Start login. Returns an 'authenticated' or '2fa_required' result."""
    headers = anisette.get_headers()
    spd, secondary = _authenticate_once(email, password, headers)
    if not secondary:
        return _finalize_session(email, spd, headers)

    adsid, idms = spd["adsid"], spd["GsIdmsToken"]
    phone_id = _DEFAULT_PHONE_ID
    notice: str | None = None
    if secondary == SECONDARY_CODE_ALREADY_SENT:
        # Apple pushed the code during the sign-in itself, so there is nothing to trigger.
        method = "trusteddevice"
    elif secondary == "trustedDeviceSecondaryAuth":
        method = "trusteddevice"
        _trigger_trusted(adsid, idms, anisette.get_headers())
    else:
        method = "sms"
        try:
            phone_id = _trigger_sms(adsid, idms, anisette.get_headers())
        except GsaError as exc:
            if secondary != SECONDARY_SMS_CODE_ALREADY_SENT:
                raise
            # Apple flagged this sign-in as having sent a code already (hsc 409). If the text is
            # refused, that code, when it reached one of the person's devices, still works.
            method = "trusteddevice"
            notice = (
                f"{exc} If a code appeared on one of your Apple devices, enter it to finish. "
                "If none did, close this and sign in again later."
            )
    _save_pending(email, adsid, idms, method, phone_id=phone_id)
    result: dict[str, Any] = {"status": "2fa_required", "method": method}
    if notice:
        result["notice"] = notice
    return result


def complete_2fa(email: str, password: str, code: str) -> dict[str, Any]:
    """Submit a 2FA code, then re-authenticate to obtain the session tokens."""
    pending = _load_pending()
    if not pending:
        raise GsaError("no pending 2FA request; run 'login' first")
    if pending["method"] == "trusteddevice":
        _submit_trusted(pending["adsid"], pending["idms"], code, anisette.get_headers())
    else:
        phone_id = pending.get("phone_id")
        if not isinstance(phone_id, int) or isinstance(phone_id, bool):
            phone_id = _DEFAULT_PHONE_ID
        _submit_sms(pending["adsid"], pending["idms"], code, anisette.get_headers(), phone_id)

    headers = anisette.get_headers()
    spd, secondary = _authenticate_once(email, password, headers)
    if secondary in (SECONDARY_CODE_ALREADY_SENT, SECONDARY_SMS_CODE_ALREADY_SENT):
        raise GsaError(
            "Apple still wants a verification code. The code may have expired or been "
            "mistyped; sign in again to get a new one."
        )
    if secondary:
        raise GsaError(f"account still requires secondary auth after 2FA: {secondary}")
    return _finalize_session(email, spd, headers)


def status() -> dict[str, Any]:
    """Report the active account, and how many others are signed in."""
    _migrate_legacy_account()
    known = list_accounts()
    active = _active_email()
    if not active:
        return {"authenticated": False, "account_count": len(known)}

    data = _read_account(active)
    return {
        "authenticated": True,
        "email": data.get("email"),
        "adsid": data.get("adsid"),
        "team_id": data.get("team_id"),
        "has_idms_token": bool(data.get("GsIdmsToken")),
        "has_auth_token": bool(data.get("auth_token")),
        "account_count": len(known),
    }


def logout(email: str | None = None) -> dict[str, Any]:
    """Sign out one account, or every account when [email] is omitted."""
    _migrate_legacy_account()
    _clear_pending()

    if email is None:
        removed = [a["email"] for a in list_accounts()]
        for address in removed:
            paths.account_file(address).unlink(missing_ok=True)
        paths.active_account_file().unlink(missing_ok=True)
        return {"status": "signed_out", "removed": removed}

    path = paths.account_file(email)
    if not path.exists():
        raise GsaError(f"{email} is not signed in.")
    path.unlink()
    if _active_email() is None:
        paths.active_account_file().unlink(missing_ok=True)
    return {"status": "signed_out", "removed": [email], "active": _active_email()}
