set(IPHONEMIRROR_CORE_SOURCES
    resources/iPhoneMirror.Core.rc
    src/CoreApi.cpp
    src/Logging.cpp
    src/Protocol/Plist.cpp
    src/Protocol/QuickTimePacket.cpp
    src/Protocol/QuickTimeSession.cpp
    src/Transport/Socket.cpp
    src/Transport/UsbMuxClient.cpp
    src/Transport/QtUsbTransport.cpp
    src/Transport/LibUsb0Transport.cpp
    src/Device/AppleUsbDiscovery.cpp
    src/Device/DeviceManager.cpp
    src/Capture/CaptureSession.cpp
    src/Capture/WirelessReceiverHub.cpp
    src/Capture/WirelessCaptureSession.cpp
    src/Media/H264.cpp
    src/Media/CoreMedia.cpp
    src/Media/MediaFoundationDecoder.cpp
    src/Audio/WasapiRenderer.cpp
    src/Renderer/D3D11PreviewRenderer.cpp
)

add_library(iPhoneMirror.Core SHARED ${IPHONEMIRROR_CORE_SOURCES})

target_compile_definitions(iPhoneMirror.Core PRIVATE
    IPHONEMIRROR_CORE_EXPORTS
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(iPhoneMirror.Core PRIVATE
    /W4
    /permissive-
    /utf-8
    /EHsc
)

target_include_directories(iPhoneMirror.Core
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${PROJECT_SOURCE_DIR}/src/WirelessHost
        ${PROJECT_SOURCE_DIR}/third_party/libusb/include
        ${PROJECT_SOURCE_DIR}/third_party/libusb-win32/include
)

target_link_libraries(iPhoneMirror.Core PRIVATE
    ws2_32
    setupapi
    advapi32
    mfplat
    mf
    mfuuid
    wmcodecdspuuid
    d3d11
    d3dcompiler
    dcomp
    dxgi
    ole32
    avrt
    delayimp
    "${PROJECT_SOURCE_DIR}/third_party/libusb/lib/x64/libusb-1.0.lib"
    "${PROJECT_SOURCE_DIR}/third_party/libusb-win32/lib/x64/libusb0-dynamic.lib"
)
target_link_options(iPhoneMirror.Core PRIVATE /DELAYLOAD:libusb0.dll)

add_custom_command(TARGET iPhoneMirror.Core POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${PROJECT_SOURCE_DIR}/third_party/libusb/bin/x64/libusb-1.0.dll
        $<TARGET_FILE_DIR:iPhoneMirror.Core>/libusb-1.0.dll
)

set_target_properties(iPhoneMirror.Core PROPERTIES
    OUTPUT_NAME "iPhoneMirror.Core"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

if(IPHONEMIRROR_BUILD_TESTS)
    enable_testing()
    add_executable(iPhoneMirror.Core.Tests
        tests/CoreTests.cpp
        src/Protocol/Plist.cpp
        src/Protocol/QuickTimePacket.cpp
        src/Protocol/QuickTimeSession.cpp
        src/Media/H264.cpp
        src/Media/CoreMedia.cpp
        src/Media/MediaFoundationDecoder.cpp
        src/Audio/WasapiRenderer.cpp
        src/Transport/QtUsbTransport.cpp
        src/Transport/LibUsb0Transport.cpp
        src/Capture/CaptureSession.cpp
        src/Capture/WirelessReceiverHub.cpp
        src/Capture/WirelessCaptureSession.cpp
        src/Logging.cpp
    )
    target_include_directories(iPhoneMirror.Core.Tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${PROJECT_SOURCE_DIR}/src/WirelessHost
        ${PROJECT_SOURCE_DIR}/third_party/libusb/include
        ${PROJECT_SOURCE_DIR}/third_party/libusb-win32/include
    )
    target_compile_definitions(iPhoneMirror.Core.Tests PRIVATE
        UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    )
    target_compile_options(iPhoneMirror.Core.Tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_link_libraries(iPhoneMirror.Core.Tests PRIVATE
        "${PROJECT_SOURCE_DIR}/third_party/libusb/lib/x64/libusb-1.0.lib"
        "${PROJECT_SOURCE_DIR}/third_party/libusb-win32/lib/x64/libusb0-dynamic.lib"
        mfplat mf mfuuid wmcodecdspuuid d3d11 dxgi
        ole32 avrt
        delayimp
    )
    target_link_options(iPhoneMirror.Core.Tests PRIVATE /DELAYLOAD:libusb0.dll)
    add_custom_command(TARGET iPhoneMirror.Core.Tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${PROJECT_SOURCE_DIR}/third_party/libusb/bin/x64/libusb-1.0.dll
            $<TARGET_FILE_DIR:iPhoneMirror.Core.Tests>/libusb-1.0.dll
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            ${CMAKE_CURRENT_SOURCE_DIR}/tests/fixtures
            $<TARGET_FILE_DIR:iPhoneMirror.Core.Tests>/fixtures
    )
    add_test(NAME CoreProtocolTests COMMAND iPhoneMirror.Core.Tests)
    set_tests_properties(CoreProtocolTests PROPERTIES
        WORKING_DIRECTORY $<TARGET_FILE_DIR:iPhoneMirror.Core.Tests>
    )

    add_executable(iPhoneMirror.LibUsb0Probe
        tools/LibUsb0Probe.cpp
        src/Transport/LibUsb0Transport.cpp
        src/Protocol/QuickTimePacket.cpp
    )
    target_include_directories(iPhoneMirror.LibUsb0Probe PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${PROJECT_SOURCE_DIR}/third_party/libusb/include
        ${PROJECT_SOURCE_DIR}/third_party/libusb-win32/include)
    target_compile_options(iPhoneMirror.LibUsb0Probe PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_link_libraries(iPhoneMirror.LibUsb0Probe PRIVATE
        "${PROJECT_SOURCE_DIR}/third_party/libusb-win32/lib/x64/libusb0-dynamic.lib")
endif()

install(TARGETS iPhoneMirror.Core
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib
)
