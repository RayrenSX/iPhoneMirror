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
x64 dynamic import library used by the native core.

The release package also intentionally includes the unmodified libusb-win32
1.2.6.0 filter driver, user-mode DLLs and filter installer under
`src/App/Drivers/libusb-win32-1.2.6.0/`. The complete corresponding upstream
source archive is distributed beside those binaries.

- Project: https://github.com/mcuee/libusb-win32
- Release archive: https://sourceforge.net/projects/libusb-win32/files/libusb-win32-releases/1.2.6.0/
- Kernel driver: GNU General Public License version 3
- Library and installer: GNU Lesser General Public License version 3
- Included licenses: `COPYING_GPL.txt` and `COPYING_LGPL.txt`
- Corresponding source: `libusb-win32-src-1.2.6.0.zip`

The bundled `libusb0.sys` and DLL files carry valid upstream Authenticode
signatures. The upstream `install-filter.exe` is not Authenticode-signed; the
iPhoneMirror compiled helper verifies its exact SHA-256 before execution.

All third-party components are provided without warranty.
