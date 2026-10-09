cmake_minimum_required(VERSION 3.25)
project(AirPlayUpstreamTests LANGUAGES C CXX)
add_executable(airplay_upstream_smoke ../airplay_upstream_smoke.c)
target_compile_definitions(airplay_upstream_smoke PRIVATE WIN32 _CRT_SECURE_NO_WARNINGS)
target_include_directories(airplay_upstream_smoke PRIVATE
    "${RECEIVER}/AirPlayServerLib/include" "${RECEIVER}/AirPlayServerLib/lib"
    "${RECEIVER}/AirPlayServerLib/lib/ed25519" "${RECEIVER}/external/ffmpeg/include")
target_link_libraries(airplay_upstream_smoke PRIVATE "${RECEIVER}/AirPlayLib.lib"
    "${RECEIVER}/external/ffmpeg/lib/x64/avcodec.lib"
    "${RECEIVER}/external/ffmpeg/lib/x64/avutil.lib"
    "${RECEIVER}/external/plist/lib/x64/libplist.a" ws2_32 winmm legacy_stdio_definitions)
if(NOT CHANNEL_SOURCE)
    set(CHANNEL_SOURCE "${RECEIVER}/airplay2dll")
endif()
add_executable(airplay_channel_smoke ../airplay_channel_smoke.cpp
    "${CHANNEL_SOURCE}/FgAirplayChannel.cpp" "${CHANNEL_SOURCE}/src/CAutoLock.cpp")
target_compile_features(airplay_channel_smoke PRIVATE cxx_std_17)
target_compile_definitions(airplay_channel_smoke PRIVATE WIN32 AIRPLAYSERVER_EXPORTS _CRT_SECURE_NO_WARNINGS)
target_include_directories(airplay_channel_smoke PRIVATE "${CHANNEL_SOURCE}"
    "${CHANNEL_SOURCE}/include" "${RECEIVER}/external/ffmpeg/include")
target_link_libraries(airplay_channel_smoke PRIVATE "${RECEIVER}/external/ffmpeg/lib/x64/avcodec.lib"
    "${RECEIVER}/external/ffmpeg/lib/x64/avutil.lib" "${RECEIVER}/external/ffmpeg/lib/x64/swscale.lib")
