# Synthetic HEVC regression fixtures

Generated locally from FFmpeg's `testsrc2`; contains no phone capture or personal data.
Two independent 9-frame, 10 fps, 8-bit HEVC streams exercise portrait/landscape SPS changes.
`frames.sha256.json` records the per-frame I420 hashes from unmodified FFmpeg 8.1.2.

Reproduce each with FFmpeg 8.1.2 (`SIZE` = `144x256` or `256x144`):

```sh
ffmpeg -f lavfi -i testsrc2=size=SIZE:rate=10 -frames:v 9 -c:v libx265 \
  -preset ultrafast -x265-params 'repeat-headers=1:aud=1:bframes=0:keyint=3:min-keyint=3:scenecut=0:info=0' \
  -f hevc output.hevc
```

These fixtures prove standard decode, reassembly, loss gating and orientation handling.
They do not simulate Apple's proprietary reference resampling quality; that still requires
a consenting real-device recording and comparison against Apple's decoder.
