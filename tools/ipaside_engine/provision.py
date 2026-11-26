"""Provisioning orchestration.

Turns an authenticated session into everything zsign needs to sign an app for a
specific device on a free (or paid) Apple ID:

1. a development certificate (generate key + CSR, submit, cache),
2. the target device registered to the team,
3. an App ID for the app's bundle identifier,
4. a provisioning profile binding the cert + device + App ID.

The private key never leaves this machine; the resulting PKCS#12 + profile are
cached under the per-user signing directory and reused until they expire (free
profiles last 7 days), so repeat signs don't burn Apple's rate limits.
"""

from __future__ import annotations

import base64
import json
import logging
import shutil
from typing import Any, Callable

from cryptography import x509
from cryptography.hazmat.primitives import serialization

from . import developer, gsa, paths, signing

log = logging.getLogger(__name__)

_CERT_CACHE = "certificate.json"
_BUNDLE_CACHE = "bundle.json"
_IDENTITY_P12 = "identity.p12"
_PROFILE = "profile.mobileprovision"

#: The machine name iPASide registers its certificates under.
#:
#: Load-bearing rather than cosmetic: Apple's per-machine certificate limit is what lets
#: one account hold a certificate for Xcode, another for SideStore, and this one at the
#: same time, so it is also how a re-issue tells ours apart from theirs. Changing it
#: orphans the certificate already issued under the old name.
CERTIFICATE_MACHINE_NAME = "iPhoneMirror"


def _account_slug(email: str | None) -> str | None:
    """The per-account signing folder name, or None before anyone has signed in."""
    return paths.account_slug(email) if email else None


def load_bundle() -> dict[str, Any]:
    """Return the cached signing bundle from a prior `provision`, or raise.

    Scoped to the active account: a bundle provisioned by a different Apple ID
    names an App ID on a team this session cannot see, and signing with it fails
    in a way that looks like a bug in signing rather than a stale cache.
    """
    session = gsa.load_session()
    path = paths.signing_dir(_account_slug(session.get("email"))) / _BUNDLE_CACHE
    if not path.exists():
        raise gsa.GsaError("no signing bundle; run 'provision' first")
    return json.loads(path.read_text(encoding="utf-8"))


def signing_identity() -> tuple[bytes, bytes, str]:
    """Return ``(private_key_pem, certificate_der, serial)`` for the active account.

    The raw material iPASide signs with, for baking into *another* signer's bundle - see
    :func:`ipaside_engine.livecontainer.seed_sidestore_certificate`. Read from the same
    per-account cache the signing bundle uses, so it is the identity that just signed,
    not a re-issue.
    """
    session = gsa.load_session()
    cache = paths.signing_dir(_account_slug(session.get("email"))) / _CERT_CACHE
    if not cache.exists():
        raise gsa.GsaError("no signing certificate cached; run 'provision' first")
    cached = json.loads(cache.read_text(encoding="utf-8"))
    return (
        base64.b64decode(cached["key_pem"]),
        base64.b64decode(cached["cert_der"]),
        cached["serial"],
    )


def certificate_machine_id(team_id: str, serial: str) -> str:
    """The ``machineId`` Apple recorded for a certificate.

    AltStore and SideStore encrypt an imported ``.p12`` with this value as its password -
    it is the identifier submitted with the CSR, and Apple returns it in the certificate
    listing. Recovered from there rather than kept locally, so it works for a certificate
    issued by any earlier build too.
    """
    for cert in developer.list_certificates(team_id):
        if cert.get("serialNumber") == serial:
            machine_id = cert.get("machineId")
            if machine_id:
                return str(machine_id)
    raise gsa.GsaError(
        f"could not read the machine id for certificate {serial} from Apple"
    )


def team_scoped_bundle_id(base_bundle_id: str) -> str:
    """Return a bundle id guaranteed registerable on a free account.

    A free Apple ID cannot register a real App Store app's identifier (Apple
    returns error 9401 "not available"). Appending the team id makes it unique
    and always registerable, which is why every free-account signing flow does it.
    """
    team_id = developer.list_teams()[0]["teamId"]
    return _with_team_suffix(base_bundle_id, team_id)


def team_scoped_app_group_identifier(identifier: str, team_id: str) -> str:
    """A ``group.*`` id this team can register when another team already holds the literal one.

    App Group identifiers are unique across every Apple Developer team. The
    literal string is still what an app and its extension expect, so this is
    only a fallback after Apple has refused that string.
    """
    return _with_team_suffix(identifier, team_id)


def _with_team_suffix(identifier: str, team_id: str) -> str:
    if identifier.lower().endswith("." + team_id.lower()):
        return identifier
    return f"{identifier}.{team_id}"


def _normalize_udid(udid: str) -> str:
    return udid.replace("-", "").lower()


def _cert_content(cert: dict[str, Any]) -> bytes | None:
    """Pull the DER certificate bytes out of a portal cert dict (varied shapes)."""
    for key in ("certContent", "certificateContent", "certificate"):
        value = cert.get(key)
        if isinstance(value, (bytes, bytearray)):
            return bytes(value)
        if isinstance(value, dict):
            inner = value.get("certContent") or value.get("certificateContent")
            if isinstance(inner, (bytes, bytearray)):
                return bytes(inner)
    return None


def _public_key_der(obj: Any) -> bytes:
    return obj.public_key().public_bytes(
        serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo
    )


def _find_cert_for_key(certs: list[dict[str, Any]], key_pem: bytes) -> tuple[bytes | None, str | None]:
    """Return (der, serial) for the certificate matching our private key's public key."""
    my_public = _public_key_der(serialization.load_pem_private_key(key_pem, password=None))
    for cert in certs:
        content = _cert_content(cert)
        if not content:
            continue
        try:
            parsed = x509.load_der_x509_certificate(content)
        except ValueError:
            continue
        if _public_key_der(parsed) == my_public:
            return content, cert.get("serialNumber")
    return None, None


def _is_certificate_limit(exc: Exception) -> bool:
    """True for Apple's "you already have a current certificate" refusal (error 7460)."""
    text = str(exc)
    return text.startswith("7460") or "already have a current" in text.lower()


def _foreign_certificate_names(certs: list[dict[str, Any]]) -> list[str]:
    """The machine names of the certificates on the team that iPASide did not create."""
    names: list[str] = []
    for cert in certs:
        if cert.get("machineName") == CERTIFICATE_MACHINE_NAME:
            continue
        name = str(cert.get("machineName") or "").strip() or "an unnamed certificate"
        if name not in names:
            names.append(name)
    return names


def _ensure_certificate(team_id: str, account_slug: str | None) -> tuple[bytes, bytes, str]:
    """Return (private_key_pem, certificate_der, serial), creating one if needed.

    The key cache is per account. Shared, a second Apple ID would find the first
    one's key, fail to match it against its own team's certificates, and respond by
    revoking a certificate — breaking every app that had signed.

    Re-issuing only ever revokes a certificate iPASide itself registered; see
    :data:`CERTIFICATE_MACHINE_NAME`.
    """
    cache_path = paths.signing_dir(account_slug) / _CERT_CACHE
    server_certs = developer.list_certificates(team_id)
    if cache_path.exists():
        cached = json.loads(cache_path.read_text(encoding="utf-8"))
        serial = cached.get("serial")
        if any(c.get("serialNumber") == serial for c in server_certs):
            return (
                base64.b64decode(cached["key_pem"]),
                base64.b64decode(cached["cert_der"]),
                serial,
            )

    # We need to issue a new certificate, and the account has limited room for them, so
    # an old one has to go. Only ever one of *ours*: Apple scopes the limit per machine
    # rather than per account, so the same team routinely holds a certificate for Xcode
    # on a Mac and another for SideStore on the phone, and both coexist with this one -
    # verified on a free account holding all three at once. Revoking those would stop
    # every app they signed from launching, in tools iPASide has nothing to do with.
    revoke_failure: Exception | None = None
    # iPhoneMirror never revokes certificates as a side effect of signing.
    # A lost key or a full account is reported for the owner to resolve.

    key_pem, csr_pem = signing.generate_key_and_csr()
    try:
        developer.submit_csr(team_id, csr_pem, machine_name=CERTIFICATE_MACHINE_NAME)
    except developer.DeveloperServicesError as exc:
        if revoke_failure is not None:
            raise gsa.GsaError(
                f"Apple would not issue a signing certificate ({exc}), and the old "
                f"iPASide certificate could not be revoked to make room for it "
                f"({revoke_failure}). Revoking it in the Apple Developer portal, under "
                "Certificates, will let this succeed."
            ) from exc
        others = _foreign_certificate_names(server_certs)
        if _is_certificate_limit(exc) and others:
            # Apple's limit is full of certificates that belong to other tools. Say so, and say
            # what the way out costs, instead of repeating Apple's sentence about "a current
            # certificate", which reads as if iPASide had made one.
            raise gsa.GsaError(
                f"Apple would not issue a signing certificate for iPASide ({exc}). This Apple ID "
                f"already holds development certificates that other tools made "
                f"({', '.join(others)}). iPASide never revokes a certificate it did not create, "
                "because that stops every app the certificate signed from opening. To continue, "
                "open the Account page, revoke one of them (the apps it signed stop opening "
                "until the tool that made it signs them again), then try again."
            ) from exc
        raise
    # Match the freshly-issued certificate to our key by public key (robust: the
    # CSR response's serial does not always line up with the listed serial).
    cert_der, serial = _find_cert_for_key(developer.list_certificates(team_id), key_pem)
    if cert_der is None:
        raise gsa.GsaError("could not obtain certificate content from Apple")

    cache_path.write_text(
        json.dumps(
            {
                "serial": serial,
                "key_pem": base64.b64encode(key_pem).decode(),
                "cert_der": base64.b64encode(cert_der).decode(),
            }
        ),
        encoding="utf-8",
    )
    return key_pem, cert_der, serial


def _ensure_device(team_id: str, udid: str, name: str) -> None:
    target = _normalize_udid(udid)
    for existing in developer.list_devices(team_id):
        if _normalize_udid(existing.get("deviceNumber", "")) == target:
            return
    try:
        developer.register_device(team_id, udid, name)
    except developer.DeveloperServicesError as exc:
        if "already exist" not in str(exc).lower():
            raise


def scoped_extension_bundle_id(original_app: str, scoped_app: str, extension_id: str) -> str:
    """The extension id nested under the app's scoped id.

    ``com.example.Widget`` with an app scoped to ``com.example.TEAM`` becomes
    ``com.example.TEAM.Widget``. Appending the team id to the end of the original
    extension id breaks the prefix iOS requires.
    """
    if not extension_id.startswith(original_app):
        raise gsa.GsaError(
            f"The {extension_id} extension does not start with {original_app}, "
            "so it cannot be given its own App ID."
        )
    return scoped_app + extension_id[len(original_app):]


def _ensure_app_id(team_id: str, bundle_id: str, name: str) -> dict[str, Any]:
    for existing in developer.list_app_ids(team_id):
        if existing.get("identifier") == bundle_id:
            return existing
    try:
        return developer.add_app_id(team_id, bundle_id, name)
    except developer.DeveloperServicesError:
        for existing in developer.list_app_ids(team_id):
            if existing.get("identifier") == bundle_id:
                return existing
        raise


def _ensure_app_groups(
    team_id: str,
    app_id: dict[str, Any],
    app_id_id: str,
    identifiers: list[str],
    name: str,
) -> list[str]:
    """Attach app groups to an App ID so the profile grants them; return their ids.

    Two steps in a specific order, both required: the app-groups capability has to be
    enabled on the App ID before a group can be assigned to it, and the profile has to
    be downloaded afterwards to pick the assignment up. Free accounts are allowed all
    of this - verified against one.
    """
    if not app_id.get("features", {}).get(developer.FEATURE_APP_GROUPS):
        developer.enable_app_id_feature(team_id, app_id_id, developer.FEATURE_APP_GROUPS)

    existing = _groups_by_identifier(team_id)
    group_ids: list[str] = []
    for identifier in identifiers:
        group = existing.get(identifier)
        if group is None:
            group = _register_app_group(team_id, identifier, name, existing)
        internal_id = group.get("applicationGroup")
        if not internal_id:
            label = group.get("identifier") or identifier
            raise gsa.GsaError(f"app group {label} has no id to assign")
        group_ids.append(internal_id)

    developer.assign_application_groups(team_id, app_id_id, group_ids)
    return group_ids


def _groups_by_identifier(team_id: str) -> dict[str, dict[str, Any]]:
    return {
        group["identifier"]: group
        for group in developer.list_application_groups(team_id)
        if group.get("identifier")
    }


def _register_app_group(
    team_id: str,
    identifier: str,
    name: str,
    existing: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    """Create ``identifier`` on this team, or a team-scoped substitute if another team owns it.

    The literal identifier is tried first. Substituting up front would break an
    app and its extension that share a group this team is free to register.
    """
    try:
        return developer.add_application_group(team_id, identifier, name)
    except developer.DeveloperServicesError as exc:
        log.warning(
            "add_application_group(%s) failed (%s); re-listing before giving up",
            identifier,
            exc,
        )
    fresh = _groups_by_identifier(team_id)
    existing.clear()
    existing.update(fresh)
    found = fresh.get(identifier)
    if found is not None:
        log.warning(
            "app group %s was absent from the first list and present after re-list; using it",
            identifier,
        )
        return found
    scoped = team_scoped_app_group_identifier(identifier, team_id)
    log.warning("app group %s is not on this team; using %s", identifier, scoped)
    already = fresh.get(scoped)
    if already is not None:
        return already
    return developer.add_application_group(team_id, scoped, name)


def ensure_signing_assets(
    bundle_id: str,
    udid: str,
    app_name: str = "iPASide App",
    device_name: str = "iPASide device",
    on_step: Callable[[str], None] | None = None,
    app_groups: list[str] | None = None,
    extension_bundle_ids: list[str] | None = None,
    original_bundle_id: str | None = None,
) -> dict[str, Any]:
    """Provision cert + device + App ID + profile and cache a signing bundle.

    ``app_groups`` names ``group.…`` identifiers to attach to the App ID. Ordinary
    sideloads do not need any; LiveContainer does, because a shared container is how
    it reaches the certificate it signs guest apps with.
    """
    step = on_step or (lambda _m: None)
    step("Contacting Apple\u2026")
    session = gsa.load_session()  # raises if not signed in
    account = session.get("email")
    team = developer.list_teams()[0]
    team_id = team["teamId"]
    # Remember which team this Apple ID provisions under, so a later refresh can
    # find its way back to the right account instead of using whichever is active.
    if account:
        gsa.remember_team(account, team_id)

    step("Preparing signing certificate\u2026")
    key_pem, cert_der, serial = _ensure_certificate(team_id, _account_slug(account))
    step("Registering your device\u2026")
    _ensure_device(team_id, udid, device_name)
    step("Creating the App ID\u2026")
    app_id = _ensure_app_id(team_id, bundle_id, app_name)
    app_id_id = app_id.get("appIdId") or app_id.get("appIdPlatform") or app_id.get("identifier")

    group_ids: list[str] = []
    if app_groups:
        step("Attaching app groups\u2026")
        group_ids = _ensure_app_groups(team_id, app_id, app_id_id, app_groups, app_name)

    step("Fetching the provisioning profile\u2026")
    profile = developer.download_profile(team_id, app_id_id)
    profile_data = profile.get("encodedProfile")
    if not isinstance(profile_data, (bytes, bytearray)):
        raise gsa.GsaError("provisioning profile download did not return profile data")

    signing_dir = paths.signing_dir(_account_slug(account))
    p12_path = signing_dir / _IDENTITY_P12
    profile_path = signing_dir / _PROFILE
    p12_path.write_bytes(signing.build_p12(cert_der, key_pem))
    profile_path.write_bytes(bytes(profile_data))

    extension_profiles: dict[str, str] = {}
    if extension_bundle_ids:
        if not original_bundle_id:
            raise gsa.GsaError("An extension App ID needs the app's original bundle id.")
        ext_dir = signing_dir / "extensions"
        if ext_dir.exists():
            shutil.rmtree(ext_dir)
        ext_dir.mkdir()
        for index, extension_id in enumerate(extension_bundle_ids):
            scoped = scoped_extension_bundle_id(original_bundle_id, bundle_id, extension_id)
            step(f"Creating the App ID for {extension_id}\u2026")
            extension_app = _ensure_app_id(team_id, scoped, f"{app_name} {index + 1}")
            extension_app_id = (
                extension_app.get("appIdId")
                or extension_app.get("appIdPlatform")
                or extension_app.get("identifier")
            )
            step(f"Fetching the profile for {extension_id}\u2026")
            downloaded = developer.download_profile(team_id, extension_app_id)
            data = downloaded.get("encodedProfile")
            if not isinstance(data, (bytes, bytearray)):
                raise gsa.GsaError(f"The profile for {extension_id} did not download.")
            path = ext_dir / f"{index}.mobileprovision"
            path.write_bytes(bytes(data))
            extension_profiles[extension_id] = str(path)

    bundle = {
        "team_id": team_id,
        "team_name": team.get("name"),
        "account": account,
        "bundle_id": bundle_id,
        "app_id_id": app_id_id,
        "certificate_serial": serial,
        "udid": udid,
        "p12_path": str(p12_path),
        "p12_password": signing.P12_PASSWORD,
        "profile_path": str(profile_path),
        "app_groups": app_groups or [],
        "app_group_ids": group_ids,
        "extension_profiles": extension_profiles,
    }
    (signing_dir / _BUNDLE_CACHE).write_text(json.dumps(bundle, indent=2), encoding="utf-8")
    return bundle
