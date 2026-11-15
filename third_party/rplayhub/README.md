# rPlayHub reference integration

Upstream: https://github.com/rPlayAI/rPlayHub

Pinned revision: `fe6a7a9f1cff431acfda142cd3564fc8e0e849a3`.
`ffmpeg-rvra.patch` and `LICENSE` are copied without modification from that revision.
The decoder is GPL, including the additional modifications made by
`scripts/patch_coredevice_ffmpeg.py`. It is built as a separate executable.
FFmpeg 8.1.2 source and SHA-256 are pinned in the build script. Reproduction requires
MSYS2 UCRT64 with gcc, make, patch, and nasm. Build sources, patches and scripts are
distributed in this repository; binary distributions must retain their licenses.

The media protocol implementation follows the project's RTP/RTCP observations and
pymobiledevice3 11.3.1. It deliberately negotiates FEC and LTR prediction off, drops
incomplete frames, sends receipts only for complete admitted frames, and gates
decoder recovery on an IRAP with parameter sets.
