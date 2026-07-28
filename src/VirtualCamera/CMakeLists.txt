set(IPHONEMIRROR_VIRTUAL_CAMERA_SOURCES
    src/ComServer.cpp
    src/FrameExchange.cpp
    src/MediaSource.cpp
    src/MediaSourceActivate.cpp
    src/VirtualCameraControl.cpp
    src/VirtualCamera.def
)

add_library(iPhoneMirror.VirtualCamera SHARED
    ${IPHONEMIRROR_VIRTUAL_CAMERA_SOURCES})

target_compile_definitions(iPhoneMirror.VirtualCamera PRIVATE
    IPHONEMIRROR_VIRTUAL_CAMERA_EXPORTS
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    NTDDI_VERSION=0x0A00000B
)
target_compile_options(iPhoneMirror.VirtualCamera PRIVATE
    /W4 /permissive- /utf-8 /EHsc)
target_include_directories(iPhoneMirror.VirtualCamera
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(iPhoneMirror.VirtualCamera PRIVATE
    advapi32
    mf
    mfplat
    mfuuid
    ole32
    shell32
)
set_target_properties(iPhoneMirror.VirtualCamera PROPERTIES
    OUTPUT_NAME "iPhoneMirror.VirtualCamera"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF)

add_executable(iPhoneMirror.VirtualCamera.Admin
    tools/VirtualCameraAdmin.cpp)
target_compile_definitions(iPhoneMirror.VirtualCamera.Admin PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(iPhoneMirror.VirtualCamera.Admin PRIVATE
    /W4 /permissive- /utf-8 /EHsc)
target_include_directories(iPhoneMirror.VirtualCamera.Admin PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(iPhoneMirror.VirtualCamera.Admin PRIVATE
    iPhoneMirror.VirtualCamera
    ole32)

add_executable(iPhoneMirror.VirtualCamera.SystemProbe
    tools/VirtualCameraSystemProbe.cpp)
target_compile_definitions(iPhoneMirror.VirtualCamera.SystemProbe PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(iPhoneMirror.VirtualCamera.SystemProbe PRIVATE
    /W4 /permissive- /utf-8 /EHsc)
target_link_libraries(iPhoneMirror.VirtualCamera.SystemProbe PRIVATE
    mf mfplat mfreadwrite mfuuid ole32)

add_executable(iPhoneMirror.VirtualCamera.SignalPublisher
    tools/VirtualCameraSignalPublisher.cpp)
target_compile_definitions(iPhoneMirror.VirtualCamera.SignalPublisher PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(iPhoneMirror.VirtualCamera.SignalPublisher PRIVATE
    /W4 /permissive- /utf-8 /EHsc)
target_include_directories(iPhoneMirror.VirtualCamera.SignalPublisher PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(iPhoneMirror.VirtualCamera.SignalPublisher PRIVATE
    iPhoneMirror.VirtualCamera ole32)

if(IPHONEMIRROR_BUILD_TESTS)
    add_executable(iPhoneMirror.VirtualCamera.Component.Tests
        tests/VirtualCameraComponentTests.cpp
        src/FrameExchange.cpp)
    target_compile_definitions(iPhoneMirror.VirtualCamera.Component.Tests PRIVATE
        UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
        NTDDI_VERSION=0x0A00000B)
    target_compile_options(iPhoneMirror.VirtualCamera.Component.Tests PRIVATE
        /W4 /permissive- /utf-8 /EHsc)
    target_include_directories(iPhoneMirror.VirtualCamera.Component.Tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CMAKE_CURRENT_SOURCE_DIR}/src)
    target_link_libraries(iPhoneMirror.VirtualCamera.Component.Tests PRIVATE
        advapi32 mf mfplat mfuuid ole32 shell32)
    add_dependencies(iPhoneMirror.VirtualCamera.Component.Tests
        iPhoneMirror.VirtualCamera)
    add_test(NAME VirtualCameraComponentTests
        COMMAND iPhoneMirror.VirtualCamera.Component.Tests)
    set_tests_properties(VirtualCameraComponentTests PROPERTIES
        WORKING_DIRECTORY $<TARGET_FILE_DIR:iPhoneMirror.VirtualCamera.Component.Tests>)
endif()

install(TARGETS
    iPhoneMirror.VirtualCamera
    iPhoneMirror.VirtualCamera.Admin
    iPhoneMirror.VirtualCamera.SystemProbe
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .)
