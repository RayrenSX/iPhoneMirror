# Support

iPhoneMirror is a community-maintained public preview. Use the latest
[published release](https://github.com/RayrenSX/iPhoneMirror/releases) or `main`
build when reproducing a problem, and include the exact version from About.
Source-tree test versions and published releases may differ.

## Before requesting help

1. Use the latest release and read the [README troubleshooting and driver notes](README.md).
2. Unlock the iPhone, reconnect it and confirm “Trust This Computer”.
3. For USB capture, open **Driver manager** from the main window and run the
   one-click repair or installation flow.
4. For AirPlay, connect the computer and iPhone to the same private network;
   wireless sources do not require the USB capture driver. An administrator
   Setup creates a process-scoped local-subnet AirPlay firewall rule; portable
   users must allow the WirelessHost process's dynamic TCP/UDP media ports
   manually if necessary.
5. For video-app casting or recording/streaming failures, confirm that the
   release contains `tools/ffmpeg/ffmpeg.exe` and include the output status and
   timestamp in the report. The virtual camera requires Windows 11 and a
   one-time administrator installation.
6. For USB/wireless reverse control, check Developer Mode, the DDI/HID status
   and device binding in the [user guide](docs/USER_GUIDE.md#有线与无线反向控制).
   Include the bridge error code; a media-stream `9021` response alone does not
   establish HID compatibility. Keep `iUsbBridge.exe`, `_internal`, and its runtime
   manifest together. Bluetooth control has separate BLE/AssistiveTouch requirements.

Report Windows version, ProductType, iOS version, connection type, AirPlay backend
(original/UxPlay), reproduction steps, expected/actual behavior, and the relevant timestamp.
Start from the [documentation index](docs/README.md) for topic-specific guidance.

Attach the relevant files from `%LOCALAPPDATA%\iPhoneMirror\Logs` and, for
driver issues, `%LOCALAPPDATA%\iPhoneMirror.Driver\Logs\driver-ui.log`.
Redact UDIDs, device names, user paths, account data and screen content before
uploading.

For reproducible bugs, use the repository's Bug Report form. For feature ideas,
use the Feature Request form.

Security vulnerabilities must be reported privately through
[GitHub Security Advisories](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).
