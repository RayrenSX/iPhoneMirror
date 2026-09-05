# Local SRS Media-Output Test Environment (legacy Docker backend)

This directory provides a local SRS server and a browser test page for the
iPhoneMirror recording and live-video output feature. It does not install
Docker, WSL, drivers, or a virtual camera. Docker Desktop must already be
installed and running.

For new protocol and virtual-camera validation, prefer the backend-selecting
[`tools/srs-lab`](../srs-lab/README.md). This folder remains useful when a fixed
Docker SRS topology is required.

## Start

From this directory, run:

```powershell
.\Start-SrsTest.ps1 -OpenBrowser
```

The default binding is loopback-only. The test page is available at
`http://127.0.0.1:8080/iphoneMirror-test/`. The official SRS WHEP player is
also available at `http://127.0.0.1:8080/players/whep.html`.

For a LAN test, pass the host LAN address as the WebRTC candidate and explicitly
opt in to exposing the ports:

```powershell
.\Start-SrsTest.ps1 -Candidate 192.168.1.50 -ExposeLan -OpenBrowser
```

Allow UDP port 8000 through the firewall for a LAN WebRTC test. Stop the
container with:

```powershell
.\Stop-SrsTest.ps1
```

## Application Endpoints

Use one publishing session at a time, then open the browser page and press
"Start WHEP playback".

| Protocol | iPhoneMirror destination |
|---|---|
| RTMP | `rtmp://127.0.0.1/live/livestream` |
| SRT | `srt://127.0.0.1:10080?streamid=#!::r=live/livestream,m=publish` |
| WebRTC WHIP | `http://127.0.0.1:1985/rtc/v1/whip/?app=live&stream=livestream` |

The SRS configuration bridges RTMP and SRT into WebRTC and bridges WHIP into
RTMP, so the same WHEP browser player verifies all three ingestion paths.

The current iPhoneMirror media-output pipeline can include source PCM audio when
it is available. This legacy browser page focuses on video delivery and does not
assert audio synchronization; use the app's output status and a receiving client
when validating audio. It must not be used as a substitute for the broader
`tools/srs-lab` protocol checks.

## Browser Page

The custom page can receive the current `live/livestream` through WHEP. It can
also publish a browser camera through WHIP to validate the SRS WebRTC path
without iPhoneMirror. Browser camera access remains optional and is never
requested until the user presses its publish button.

The page runs from SRS HTTP port 8080, so it performs WHIP/WHEP signaling on
the same host. It is intended for localhost development; use HTTPS and a
publicly reachable ICE candidate for production or remote-browser WebRTC.
