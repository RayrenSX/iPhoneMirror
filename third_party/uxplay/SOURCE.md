# UxPlay source and runtime

The optional UxPlay wireless fallback is built from the FDH2/UxPlay source
repository at the pinned commit recorded by `scripts/prepare_uxplay.ps1`.
UxPlay is GPLv3 software. The build script uses the MSYS2 UCRT64 toolchain and
copies only the executable, the GStreamer plugins needed by the adapter, their
runtime DLL dependencies, the upstream license, and this source record into
`Wireless/UxPlay`.

- Project: https://github.com/FDH2/UxPlay
- License: GNU General Public License v3.0
- Build script: `scripts/prepare_uxplay.ps1`
