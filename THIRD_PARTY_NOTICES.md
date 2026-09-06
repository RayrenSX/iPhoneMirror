# Third-party notices

This file describes third-party material intentionally included in the source
tree or release package for the current `v1.8.3` baseline. Each component
remains under its upstream license.
The root GNU General Public License v3.0 only applies to original iPhoneMirror
material and does not replace, narrow or relicense any component listed below.

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
x64 dynamic import library used by the native core. The main application ships
the signed upstream x64 `libusb0.dll` user-mode runtime so it can start before
the capture filter is installed. The standalone driver manager carries the
remaining signed runtime and kernel-driver payload under
`src/DriverInstaller/Assets/libusb-win32-1.2.6.0/`.

- Project: https://github.com/mcuee/libusb-win32
- Release archive: https://sourceforge.net/projects/libusb-win32/files/libusb-win32-releases/1.2.6.0/
- Dynamic import library: GNU Lesser General Public License version 3
- Driver payload licenses: `src/DriverInstaller/Assets/libusb-win32-1.2.6.0/COPYING_GPL.txt`,
  `COPYING_LGPL.txt`, `README.txt` and `AUTHORS.txt`
- Driver payload hashes and signature validation are enforced by
  `src/DriverInstaller/Services/DriverConstants.cs` and `DriverPayload.cs`.

The driver manager does not copy proprietary Aisi binaries. Apple USB support
is installed from a signed offline AppleMobileDeviceSupport MSI when available,
from Apple's Software Update catalog as a standalone MSI, or from Apple's
official HTTPS iTunes installer as a compatibility fallback. Apple Devices
from Microsoft Store remains a supported manual installation path. Apple
software is not redistributed in this repository or its release assets.

## Microsoft Visual C++ runtime

The Windows release includes app-local x64 copies of `msvcp140.dll`,
`vcruntime140.dll` and `vcruntime140_1.dll`. They satisfy the runtime imports of
the bundled libusb and AirPlayServer binaries on clean Windows installations.
The build copies these files only from an installed Visual Studio Redistributable
directory after validating their Microsoft Authenticode signatures.

- Publisher: Microsoft Corporation
- Deployment documentation:
  https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files
- License terms:
  https://visualstudio.microsoft.com/license-terms/

## AirPlayServer 1.1.2 wireless receiver

`third_party/airplay-server/` contains a pinned runtime subset of the
AirPlayServer x64 release with local compatibility patches and selected fixes
from upstream commits `c788d6fe` and `37d7fd0f`. The GPL-licensed
`iPhoneMirror.WirelessHost.exe` process loads its protocol/decoder DLL and sends
decoded I420 video and PCM audio to the GPL-3.0-only application over a named
pipe. The application and native capture core do not link to the receiver DLL.

- Project: https://github.com/xenos1337/AirPlayServer
- Version/commit: v1.1.2 / `34ba6cfd49b2432cf30e89913d66decb775763e4`
- AirPlayServer wrapper: MIT
- PlayFair implementation and receiver runtime: GPL version 3
- FFmpeg 4.4.2 runtime: LGPL version 2.1 or later
- Fraunhofer FDK AAC: Fraunhofer FDK AAC license
- Exact hashes, source links and license files:
  `third_party/airplay-server/SOURCE.md`

## FDH2/UxPlay wireless fallback

The optional `Wireless/UxPlay` fallback is built on demand from the FDH2/UxPlay
source tree by `scripts/prepare_uxplay.ps1`. The adapter keeps UxPlay's
GStreamer-rendered raw I420/PCM output on the existing iPhoneMirror named-pipe
capture path. UxPlay is GPLv3 software; its pinned source and build details are
recorded in `third_party/uxplay/SOURCE.md`. The fallback is disabled for the
application's URL-video/DLNA media-cast path because UxPlay does not implement
the iPhoneMirror media-control IPC contract.

## FFmpeg 8.1.2 media-output runtime

The default release uses an FFmpeg essentials build staged at `tools/ffmpeg/`.
An explicitly requested compact edition may omit this runtime and use a
user-installed FFmpeg from `PATH`. The bundled runtime is prepared by
`scripts/prepare_ffmpeg.ps1`, which verifies the published archive SHA-256
before copying `ffmpeg.exe`, the license, build README and source record into
the application output. This runtime is independent of the older FFmpeg DLLs
distributed with AirPlayServer.

- Binary package: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip
- Upstream FFmpeg source: https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz
- License for the distributed essentials build: GNU General Public License v3
- Included license and build/source metadata: `tools/ffmpeg/LICENSE.txt`,
  `tools/ffmpeg/README.txt` and `tools/ffmpeg/SOURCE.txt`
- Pinned archive and extracted-file hashes:
  `scripts/ffmpeg-runtime-manifest.psd1`
- The build enables GPL components such as libx264 and is therefore distributed
  under GPLv3 terms. It encodes projection video and, when available, muxes the
  captured iPhone PCM audio into recordings and live-streaming output.

## Markdig Markdown processor

The in-app update window uses Markdig 1.3.2 to parse GitHub Release notes into
safe WPF document elements. Markdig is distributed under the BSD 2-Clause
License. Source and license: https://github.com/xoofx/markdig

## WPF-UI 4.3.0 and Microsoft Fluent System Icons

The main application and standalone driver manager use WPF-UI 4.3.0. Its
`SymbolIcon` control embeds the Microsoft Fluent System Icons font in the
WPF-UI assembly, so application icons do not depend on fonts installed in
Windows.

- WPF-UI: https://github.com/lepoco/wpfui
- Microsoft Fluent System Icons: https://github.com/microsoft/fluentui-system-icons
- WPF-UI copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors
- Microsoft Fluent System Icons copyright (c) 2020 Microsoft Corporation
- License: MIT
- Exact upstream license and bundled component notices are copied into release
  packages as `licenses/WPF-UI-LICENSE.md` and
  `licenses/WPF-UI-ThirdPartyNotices.txt`.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Inno Setup installer engine

The Windows Setup executable is built with Inno Setup 6.7.3. The compiler is
downloaded from the official JRSoftware GitHub release and verified against the
SHA-256 recorded in `scripts/inno-runtime-manifest.psd1`. The generated Setup
engine is redistributed under the Inno Setup License:
https://jrsoftware.org/files/is/license.txt

All third-party components are provided without warranty.
