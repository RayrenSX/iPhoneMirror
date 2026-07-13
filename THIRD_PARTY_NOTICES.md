# Third-party notices

This file describes third-party material intentionally included in the source
tree or release package. Each component remains under its upstream license.
The root MIT License applies only to original iPhoneMirror material and does
not replace, narrow or relicense any component listed below.

## quicktime_video_hack protocol fixtures

The binary fixtures in `src/Core/tests/fixtures/quicktime_video_hack/` are
unmodified protocol captures from Daniel Paulus' `quicktime_video_hack`
project. They are used only as interoperability test vectors; no upstream
implementation source is copied into iPhoneMirror.

- Project: https://github.com/danielpaulus/quicktime_video_hack
- Copyright (c) 2019 danielpaulus
- License: MIT
- Included license: `src/Core/tests/fixtures/quicktime_video_hack/LICENSE`

## libusb 1.0.29

`third_party/libusb/` contains the public headers, x64 runtime DLL and import
library used by the optional libusb-1.0 transport. iPhoneMirror dynamically
links to the library.

- Project: https://github.com/libusb/libusb
- License: GNU Lesser General Public License 2.1 or later
- Included license: `third_party/libusb/COPYING`

## libusb-win32 1.2.6.0

`third_party/libusb-win32/` contains the public compatibility header and the
x64 dynamic import library used by the native core. The standalone driver
manager also carries the signed upstream runtime payload under
`src/DriverInstaller/Assets/libusb-win32-1.2.6.0/`; it is intentionally absent
from the main iPhoneMirror application output.

- Project: https://github.com/mcuee/libusb-win32
- Release archive: https://sourceforge.net/projects/libusb-win32/files/libusb-win32-releases/1.2.6.0/
- Dynamic import library: GNU Lesser General Public License version 3
- Driver payload licenses: `src/DriverInstaller/Assets/libusb-win32-1.2.6.0/COPYING_GPL.txt`,
  `COPYING_LGPL.txt`, `README.txt` and `AUTHORS.txt`
- Driver payload hashes and signature validation are enforced by
  `src/DriverInstaller/Services/DriverConstants.cs` and `DriverPayload.cs`.

The driver manager does not copy proprietary Aisi binaries. Apple USB support
is installed from a signed offline AppleMobileDeviceSupport MSI when available,
or from Apple's official iTunes installer when the user authorizes that fallback.
Apple software is not redistributed in this repository.

## AirPlayServer 1.1.0 wireless receiver

`third_party/airplay-server/` contains a pinned runtime subset of the
AirPlayServer x64 release with local compatibility patches. The GPL-licensed
`iPhoneMirror.WirelessHost.exe` process loads its protocol/decoder DLL and sends
decoded I420 video and PCM audio to the MIT application over a named pipe. The
MIT application and native capture core do not link to the receiver DLL.

- Project: https://github.com/xenos1337/AirPlayServer
- Version/commit: v1.1.0 / `ff149b2e768bf9ae93199de941ab170571a941a4`
- AirPlayServer wrapper: MIT
- PlayFair implementation and receiver runtime: GPL version 3
- FFmpeg 4.4.2 runtime: LGPL version 2.1 or later
- Fraunhofer FDK AAC: Fraunhofer FDK AAC license
- Exact hashes, source links and license files:
  `third_party/airplay-server/SOURCE.md`

All third-party components are provided without warranty.
