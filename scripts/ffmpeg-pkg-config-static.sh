#!/usr/bin/env bash
set -euo pipefail
# MSYS2's SRT Libs.private explicitly requests the shared GCC unwinder even
# under --static. Use its static counterpart so the shipped EXE needs no
# libgcc_s_seh-1.dll from the developer's PATH.
output=$(/ucrt64/bin/pkg-config "$@")
printf '%s\n' "$output" | sed 's/-lgcc_s\b/-lgcc_eh/g'
