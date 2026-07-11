# Device-aware preview corners

The detached/OBS preview uses a device-family display outline instead of one
hard-coded iPhone radius. `DeviceCornerProfileResolver` selects the curve from
the Lockdown `ProductType` (`iPhone18,3`, `iPad14,1`, and so on) and falls back
to the decoded frame aspect ratio while model metadata is unavailable.

Profiles are grouped into:

- rectangular Home-button iPhone/iPad displays;
- iPhone X, later notched iPhones, and Dynamic Island iPhones;
- rounded iPad Pro, Air, mini, and all-screen base iPad displays.

The renderer stores radius as a fraction of the short edge, so rotation and
window resizing cannot change the physical-looking proportions. A curve
exponent drives an anti-aliased superellipse in the D3D11 pixel shader. The WPF
fallback uses the closest circular Win32 region because `CreateRoundRectRgn`
cannot represent a continuous superellipse.

## Accuracy boundary

Apple's technical specifications state whether a particular display has
rounded corners and publish the rectangular pixel resolution. Apple also
publishes device-specific Product Bezel artwork in its Design Resources:

- <https://developer.apple.com/design/resources/>
- <https://support.apple.com/125089> (iPhone 17, 2622 x 1206)
- <https://support.apple.com/122240> (iPad (A16), 2360 x 1640)

Apple does **not** publish a numeric physical display-corner radius or a CAD
equation in those specifications. The coefficients in this project are
therefore rendering-match profiles visually fitted by device family, not
claimed Apple hardware/CAD measurements. No Apple artwork is redistributed.

Unknown future `iPhoneN,M` and `iPadN,M` identifiers inherit their modern
family profile. A stream with no ProductType uses conservative phone/tablet
aspect windows; ambiguous geometry remains rectangular to avoid clipping
content.

Run the resolver smoke tests with:

```powershell
dotnet run --project src/App.Logic.Tests/IPhoneMirror.App.Logic.Tests.csproj -c Release
```
