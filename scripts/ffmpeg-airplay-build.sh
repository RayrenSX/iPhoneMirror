#!/usr/bin/env bash
# Match the ABI of the receiver's original FFmpeg build; ship only four DLLs.
set -euo pipefail
export PATH="/ucrt64/bin:/usr/bin:$PATH"
source_dir="$1"
build_dir="$2"
jobs="$3"
mkdir -p "$build_dir"
cd "$build_dir"
"$source_dir/configure" \
    --arch=x86_64 --target-os=mingw32 --cc=gcc \
    --disable-autodetect --disable-everything --disable-doc --disable-debug \
    --disable-programs --disable-avdevice --disable-avformat --disable-avfilter \
    --disable-postproc --disable-avresample --enable-small \
    --enable-shared --disable-static --extra-ldflags=-static-libgcc \
    --extra-libs='-Wl,-Bstatic -lwinpthread -Wl,-Bdynamic' \
    --enable-decoder=h264,alac --enable-parser=h264 \
    --enable-swresample --enable-swscale
make -j"$jobs"
for library in libavcodec/avcodec-58.dll libavutil/avutil-56.dll \
    libswresample/swresample-3.dll libswscale/swscale-5.dll; do
    strip "$library"
    imports=$(objdump -p "$library" | sed -n 's/.*DLL Name: //p')
    if printf '%s\n' "$imports" | grep -Eiq '(^lib|^msys-|^avformat|^avfilter)'; then
        printf 'Unexpected DLL dependency in %s:\n%s\n' "$library" "$imports" >&2
        exit 1
    fi
done
