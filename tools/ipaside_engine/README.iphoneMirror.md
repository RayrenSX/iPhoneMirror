# Embedded signing engine

Selected MIT-licensed modules from [iPASide](https://github.com/pwnapplehat/iPASide),
revision `cd27ac986a2f048a7075f3d15ab8587ae4ac8730` (engine version 1.2.17).
`LICENSE` contains the complete upstream license; `UPSTREAM.json` records the
SHA-256 of every imported file before integration changes.

Local modifications:

- `paths.py` uses `iPhoneMirror/IpaSigning`, isolated from iPASide's own state.
- `protected_state.py` encrypts JSON session and private-key caches with Windows
  current-user DPAPI. Passwords are not persisted. Temporary signing identities
  are deleted by `ipa_signing.py` after each signing request.
- `provision.py` names new certificates `iPhoneMirror` and never automatically
  revokes a certificate. Account limits surface as an error to the owner.
- `ipa.py` and `signer/bundle.py` include nested `.app` bundles, such as Watch apps,
  in provisioning and signing; separate matching profiles are required.

The host calls the JSON adapter in `../ipa_signing.py` via stdin. No credentials
are passed in command arguments or diagnostic logs. It uses upstream remote
Anisette over HTTPS (optional custom endpoint) and does not enable the optional
local emulator, inject tweaks, or perform device installation. The normal bridge
`install_app` operation owns installation and its device identity checks.

Tests: `tests/ipa_signing_test.py` verifies real signatures on synthetic Mach-O
executables, profile checks, multiple identities, JSON protocol and DPAPI. No
synthetic fixture is represented as accepted by an iPhone or by Apple services.
