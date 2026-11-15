#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
source_dir=$1
build_dir=$root/work/coredevice-decoder-build
mkdir -p "$build_dir" "$root/dist/CoreDevice"
cd "$build_dir"
config_key=$(sha256sum "$root/scripts/build_coredevice_decoder.sh" | cut -d' ' -f1)
if [[ ! -f config.h || ! -f .configure-success || $(cat .configure-success) != "$config_key" ]]; then
"$source_dir/configure" --target-os=mingw32 --arch=x86_64 --cc=gcc --cxx=g++ \
  --disable-everything --disable-autodetect --disable-programs --disable-doc \
  --disable-network --disable-avdevice --disable-avformat --disable-avfilter \
  --disable-debug --disable-shared --enable-static --enable-gpl --enable-version3 \
  --enable-decoder=hevc,aac --enable-swscale --enable-swresample \
  --extra-cflags=-O2 --extra-ldflags=-static
printf '%s\n' "$config_key" > .configure-success
fi
make -j"${NUMBER_OF_PROCESSORS:-4}" > build.log 2>&1 || { tail -80 build.log; exit 1; }
gcc -O2 -static -I"$source_dir" -I. "$root/tools/coredevice_decoder.c" \
  libavcodec/libavcodec.a libswscale/libswscale.a libswresample/libswresample.a libavutil/libavutil.a \
  -lws2_32 -lbcrypt -lole32 -luuid -lm -lwinpthread \
  -o "$root/dist/CoreDevice/iPhoneMirror.MediaDecoder.exe"
cp "$root/third_party/rplayhub/LICENSE" "$root/dist/CoreDevice/LICENSE.rplayhub"
cp "$source_dir/COPYING.GPLv3" "$root/dist/CoreDevice/LICENSE.FFmpeg"
