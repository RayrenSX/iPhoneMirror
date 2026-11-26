# Support

To report a problem, copy the full version from About and specify whether you used Setup, the portable ZIP, or a local build. The local `main` build is v1.8.5-pre7, the final preview before 1.8.5. [pre7 release notes](docs/en/RELEASE_NOTES.md)

[简体中文问题反馈](docs/zh-CN/SUPPORT.md)

## Before requesting help

1. Reproduce with the latest [published release](https://github.com/RayrenSX/iPhoneMirror/releases) or the current `main` build. Check the [troubleshooting guide](docs/en/USER_GUIDE.md#troubleshooting).
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
6. For USB/wireless touch control, confirm iOS/iPadOS 17.4 or later, Developer Mode, the DDI/HID status
   and device binding in the [user guide](docs/en/USER_GUIDE.md#usb-and-wi-fi-touch-control).
   Include the bridge error code; a media-stream `9021` response alone does not
   establish HID compatibility. Keep `iUsbBridge.exe`, `_internal`, and its runtime
   manifest together. Bluetooth control has separate BLE/AssistiveTouch requirements.
7. Paired-wireless discovery does not mean capture has started. The local CoreDevice
   video path requires iOS/iPadOS 27 or later and currently supplies video only;
   ordinary AirPlay and the 17.4 touch-control requirement are separate.
8. For app, file mapping or IPA problems, include the selected device, operation,
   destination type and error. State whether the issue occurred in the device panel,
   Windows Explorer or the signing/install window; omit credentials and private keys.

Report Windows version, ProductType, iOS version, connection type, AirPlay backend
(original/UxPlay), reproduction steps, expected/actual behavior, and the relevant timestamp.
Start from the [documentation index](docs/en/README.md) for topic-specific guidance.

Attach the relevant files from `%LOCALAPPDATA%\iPhoneMirror\Logs` and, for
driver issues, `%LOCALAPPDATA%\iPhoneMirror.Driver\Logs\driver-ui.log`.
Redact UDIDs, device names, user paths, account data and screen content before
uploading.

For reproducible bugs, use the repository's Bug Report form. For feature ideas,
use the Feature Request form.

Security vulnerabilities must be reported privately through
[GitHub Security Advisories](https://github.com/RayrenSX/iPhoneMirror/security/advisories/new).

## Reproduction details / 问题反馈模板

Copy this template into the Bug Report form. Leave unknown values as unknown.
For a local build, include its commit and whether the working tree has extra changes.

```text
App version / 软件版本（About / 关于）:
Package / 安装方式: Setup / ZIP / local build
Windows version and architecture / 系统版本与架构:
Device ProductType and iOS / 设备型号标识与 iOS:
Connection / 连接方式: USB / AirPlay / paired-wireless CoreDevice
Receiver backend / 无线接收方案（如适用）: original / UxPlay
Control transport / 反控方式（如适用）: Bluetooth / USB / wireless
Steps / 复现步骤:
1.
2.
Expected result / 预期结果:
Actual result and error code / 实际结果与错误码:
Failure time and time zone / 失败时间与时区:
Frequency / 复现频率:
Sanitized logs or screenshots / 已脱敏日志或截图:
```

Do not include the API key, control session token, UDID, pairing files or private
screen content. Logs around the failure time are usually more useful than a full
unfiltered log history. Use the private security reporting link above for vulnerabilities.
