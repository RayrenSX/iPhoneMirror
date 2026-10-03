#!/usr/bin/env bash
# Run under MSYS2 UCRT64; inputs are verified/extracted by build_compact_ffmpeg.ps1.
set -euo pipefail
export PATH="/ucrt64/bin:/usr/bin:$PATH"
source_dir="$1"
build_dir="$2"
jobs="$3"
recipe_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH="$recipe_dir:$PATH"
mkdir -p "$build_dir"
cd "$build_dir"
for command in gcc make nasm pkg-config cmp sed objdump; do
    command -v "$command" >/dev/null || { echo "Missing build dependency: $command" >&2; exit 1; }
done
# Keep the exact application inputs, filters, output protocols and hardware
# encoders. Do not drag in AV1/VP9 encoders, subtitle renderers, SVG/JXL, etc.
"$source_dir/configure" \
    --arch=x86_64 --target-os=mingw32 --cc=gcc --cxx=g++ --ld=g++ \
    --disable-autodetect --disable-everything --disable-doc --disable-debug \
    --disable-ffplay --disable-ffprobe --enable-ffmpeg --enable-small \
    --disable-shared --enable-static --pkg-config-flags=--static \
    --pkg-config=ffmpeg-pkg-config-static.sh \
    --extra-ldflags=-static --enable-gpl --enable-version3 \
    --enable-libx264 --enable-libopus --enable-libsrt --enable-openssl \
    --enable-libvpl --enable-amf --enable-ffnvcodec --enable-nvenc --enable-mediafoundation \
    --enable-d3d11va --enable-dxva2 \
    --enable-encoder=libx264,h264_nvenc,h264_amf,h264_qsv,h264_mf,aac,libopus,pcm_s16le,wrapped_avframe \
    --enable-decoder=rawvideo,wrapped_avframe,pcm_s16le,pcm_f32le,aac,aac_latm,mp3,mp3float,opus,vorbis,flac,alac,ac3,eac3,h264,hevc \
    --enable-parser=h264,hevc,aac,aac_latm,mpegaudio,ac3,opus,vorbis,flac \
    --enable-demuxer=rawvideo,pcm_s16le,pcm_f32le,hls,mov,mpegts,flv,aac,mp3,wav,ogg,matroska,flac \
    --enable-muxer=mp4,mov,flv,mpegts,whip,pcm_s16le,null \
    --enable-protocol=file,pipe,http,https,tcp,tls,udp,rtp,rtmp,rtmps,libsrt,crypto,data,httpproxy \
    --enable-indev=dshow,lavfi \
    --enable-filter=buffer,buffersink,abuffer,abuffersink,scale,format,aformat,aresample,asetpts,atempo,amix,alimiter,anull,null,anullsrc,sine,testsrc2 \
    --enable-bsf=aac_adtstoasc,h264_mp4toannexb,hevc_mp4toannexb,extract_extradata
make -j"$jobs" ffmpeg.exe
strip ffmpeg.exe
# Check before running the EXE: otherwise Windows may report an opaque missing
# DLL error (or the developer's PATH may conceal the packaging defect).
imports=$(objdump -p ffmpeg.exe | sed -n 's/.*DLL Name: //p')
if printf '%s\n' "$imports" | grep -Eiq '(^lib|^msys-|^avcodec|^avformat|^avutil|^vpl|^srt)'; then
    printf 'Unexpected non-system DLL dependency:\n%s\n' "$imports" >&2
    exit 1
fi
