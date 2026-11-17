# Independent AirPlay video control transport

Upstream: https://github.com/jkiddo/go-atv
Pinned commit: f47a3ffcfa9458aac40562d73c314de1447df013

The `internal/fairplay` and `internal/fpcontrol` directories are copied unchanged
from that commit, including their tests. Upstream attribution and LGPL-3.0-or-later
license texts are retained. The C ABI in `main.go`, bounded receiver adapter in
`receiver`, and native integration smoke client in `cmd/smoke` are iPhoneMirror
additions. No non-standard Go dependencies are required for this subset.

Build with Go 1.25+ and MinGW GCC:

    go test ./...
    go build -buildmode=c-shared -trimpath -o iPhoneMirror.AirPlayControl.dll .

From the repository, `scripts/build_airplay_control.ps1` also records source and
binary hashes. Replace this DLL beside `airplay2dll.dll` when rebuilding or
modifying the library. The application verifies the packaged hash, so update it
using `scripts/install_airplay_runtime.ps1 -ControlBinary <rebuilt DLL>` together
with its receiver and FFmpeg build record parameters, then rebuild the application.
The complete corresponding library source and licenses ship with the application.

Only the dedicated Video HTTP endpoint enables this transport. Each TCP
connection owns an independent handshake, authenticated record states and
nonces. The successful fp-setup2 response is sent in plaintext before encryption
starts. Failed authentication closes the connection. Mirroring RAOP retains its
existing implementation. The `media` adapter bridges mlhls playlists through
encrypted FCUP reverse events and phone `/action` replies, serving rewritten HLS
playlists on a tokenized loopback HTTP listener. FCUP field layout follows UxPlay's
`fcup_request.h` (fduncanh, 2022, LGPL-2.1-or-later); the adapter is a separate Go
implementation. HTTP(S) segment URLs remain unchanged. Session IDs, request IDs,
timeouts and response size limits isolate and bound the bridge. The library does
not decode media; local protocol tests do not establish YouTube compatibility.
