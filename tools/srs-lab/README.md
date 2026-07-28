# iPhoneMirror Stream Lab

This folder provides a loopback media server and an operational browser page
for verifying iPhoneMirror recording, RTMP, SRT, WHIP/WebRTC, WHEP playback,
and the Windows virtual camera.

Prerequisites:

- Node.js 18 or later for the dashboard.
- FFmpeg for the optional synthetic-signal script.
- Optional: Docker Desktop for the preferred official `ossrs/srs:6` backend.
  When Docker is unavailable, the launcher downloads the pinned MediaMTX
  Windows runtime, verifies its SHA-256 hash, and uses it automatically.

Start the lab from PowerShell:

```powershell
.\tools\srs-lab\Start-SrsLab.ps1
```

Open `http://127.0.0.1:8090`. Configure iPhoneMirror's media output with one
of the publish endpoints displayed by the page. The addresses are selected for
the active backend: SRS uses its `live/iphone-mirror` route, while MediaMTX uses
the `iphone-mirror` path. The WHEP monitor checks browser playback and the
camera picker checks `iPhoneMirror Virtual Camera` after it has been started in
the app.

For a protocol check before connecting a device:

```powershell
.\tools\srs-lab\Publish-TestSignal.ps1 -Protocol Rtmp
```

Change `Rtmp` to `Srt` or `Whip` to exercise the other ingress paths. The
synthetic test stream is stopped with Ctrl+C.

Backend selection can also be explicit:

```powershell
.\tools\srs-lab\Start-SrsLab.ps1 -Backend Srs
.\tools\srs-lab\Start-SrsLab.ps1 -Backend MediaMtx
```

To serve only the dashboard, without starting a media server:

```powershell
.\tools\srs-lab\Start-SrsLab.ps1 -DashboardOnly
```

The lab is intentionally loopback-only. MediaMTX listens on RTMP 1935, WebRTC
HTTP 1985, WebRTC UDP 8000, SRT 10080, and its status API 9997. It is not a
production streaming configuration and does not expose authentication, TLS,
or internet-facing WebRTC candidates.
