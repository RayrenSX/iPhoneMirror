# Windows Driver Dependency Inventory

This document defines every driver-level dependency used by iPhoneMirror and
whether it is bundled, supplied by Windows, or acquired from an official vendor.

| Component | Purpose | Delivery | Verification |
|---|---|---|---|
| `libusb0.sys` 1.2.6.0 | Per-device USB capture UpperFilter | Embedded in `iPhoneMirror.Driver.exe` | Fixed SHA256 and Windows Authenticode trust |
| `libusb0.dll` x64/x86 | User-mode access to the capture filter | x64 app copy plus x64/x86 copies embedded in the driver manager | Fixed SHA256; release build rejects a mismatch |
| `install-filter.exe` 1.2.6.0 | Registers the shared libusb0 filter service | Embedded in `iPhoneMirror.Driver.exe` | Fixed SHA256 before every elevated operation |
| Apple USB driver (`appleusb.inf`) | Modern Apple Devices USB transport | Installed from Microsoft Store product `9NP83LWLPZ9K` through `winget` | Microsoft Store source and exact product ID |
| Apple USB driver (`usbaapl64.inf` / `usbaapl.inf`) | Desktop iTunes USB transport | User-provided signed MSI or Apple official HTTPS compatibility fallback | Windows Authenticode signer must be Apple Inc. |
| Apple Mobile Device Service | Pairing and usbmux service | Installed with Apple Devices or Apple Mobile Device Support; the official package supplements Store installations that provide only the INF | Service presence/running state checked separately from the INF package |
| `usbccgp.sys` | USB composite parent used by the per-device filter | Windows inbox driver | Never replaced or redistributed |
| WinUSB | Recovery target when a third-party tool replaced the Apple parent | Windows inbox driver | Only known incorrect parent bindings are removed; no WinUSB payload is bundled |

`applekis.inf` is an Apple recovery/DFU driver and does not by itself satisfy
the normal wired-mirroring requirement. The driver manager therefore requires
`appleusb.inf`, `usbaapl64.inf`, or `usbaapl.inf` in DriverStore in addition to
a running Apple Mobile Device Service.

The virtual camera is a registered user-mode Media Foundation component, not a
kernel driver. Wireless AirPlay uses bundled user-mode libraries and Windows
network APIs; it does not require Bonjour or an additional network driver.

Apple packages are absent from normal public iPhoneMirror Setup and ZIP assets
because Apple has not granted this project redistribution rights. A clean
machine still receives the required Apple driver automatically from Microsoft
Store, with an Apple-signed official iTunes download as the compatibility
fallback. If a supported Apple USB INF is already present and only the service
is missing, the driver manager skips Store reinstallation and uses that signed
compatibility fallback directly. The distributable installation therefore
contains all project-owned or redistributable driver payloads and acquires the
only proprietary dependency from its vendor-controlled channel.

The compatibility fallback does not install the complete iTunes application.
The driver manager extracts `AppleMobileDeviceSupport64.msi` from Apple's
official signed package, validates the extracted MSI signature and SHA256,
then installs only that component. The downloaded package and extracted MSI
are never redistributed as iPhoneMirror release assets. The UI reports the
large vendor download's progress, preserves a bounded MSI diagnostic log, and
explains when Windows Installer requires a reboot before the service is ready.

An organization that holds Apple redistribution rights can produce a fully
offline Setup by supplying its authorized MSI explicitly:

```powershell
./scripts/package_release.ps1 -Version 1.5.8 -GenerateSbom `
  -AppleSupportPackagePath C:\Authorized\AppleMobileDeviceSupport64.msi `
  -ConfirmAppleRedistributionRights
```

The build rejects non-MSI files, reparse points, packages larger than 512 MB,
untrusted signatures, and signer subjects other than Apple Inc. It verifies the
copied SHA256 and validates the signature again before packaging. The MSI is
never downloaded by the build and is ignored by Git if placed in the workspace.
