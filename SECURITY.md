# Security Policy

## Supported versions

iPhoneMirror is currently a public preview. Security fixes are provided only for
the latest [published release](https://github.com/RayrenSX/iPhoneMirror/releases)
and the current `main` branch. A source-tree test version is not necessarily published.

| Version | Supported |
|---|---|
| Latest preview | Yes |
| Older previews | No |

## Reporting a vulnerability

Please use
[GitHub private vulnerability reporting](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).
Do not open a public issue for vulnerabilities involving:

- unsafe external-driver detection or accidental registry mutation;
- privilege escalation or unsafe file handling;
- memory safety in USB, protocol, decoder or renderer code;
- wireless-host, AirPlay/DLNA, named-pipe or virtual-camera isolation;
- exposure of UDIDs, pairing data, logs, screen content or audio;
- crafted USB/CoreMedia/H.264 data that crashes or compromises the host.

Include the affected version, Windows version, impact, reproduction steps and a
minimal sanitized proof of concept. Do not attach real pairing records or
unredacted USB captures.

You should receive an acknowledgement within seven days. Please allow time for
investigation and a coordinated fix before public disclosure.
