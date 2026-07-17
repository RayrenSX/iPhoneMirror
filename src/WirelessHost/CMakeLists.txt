add_library(iPhoneMirror.DnsSdShim SHARED DnsSdShim.cpp DnsSdShim.def)
target_compile_definitions(iPhoneMirror.DnsSdShim PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(iPhoneMirror.DnsSdShim PRIVATE
    /W4 /permissive- /utf-8 /EHsc)
target_link_libraries(iPhoneMirror.DnsSdShim PRIVATE dnsapi ws2_32)
set_target_properties(iPhoneMirror.DnsSdShim PROPERTIES
    PREFIX ""
    OUTPUT_NAME "dnssd")

add_executable(iPhoneMirror.WirelessHost WirelessHost.cpp DlnaRenderer.cpp)
add_dependencies(iPhoneMirror.WirelessHost iPhoneMirror.DnsSdShim)
target_link_libraries(iPhoneMirror.WirelessHost PRIVATE bcrypt ws2_32)

target_compile_definitions(iPhoneMirror.WirelessHost PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(iPhoneMirror.WirelessHost PRIVATE
    /W4
    /permissive-
    /utf-8
    /EHsc
)

set_target_properties(iPhoneMirror.WirelessHost PROPERTIES
    OUTPUT_NAME "iPhoneMirror.WirelessHost"
)

foreach(runtime IN ITEMS
    airplay2dll.dll
    avcodec-58.dll
    avutil-56.dll
    swresample-3.dll
    swscale-5.dll)
    add_custom_command(TARGET iPhoneMirror.WirelessHost POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${PROJECT_SOURCE_DIR}/third_party/airplay-server/bin/x64/${runtime}
            $<TARGET_FILE_DIR:iPhoneMirror.WirelessHost>/${runtime})
endforeach()

add_custom_command(TARGET iPhoneMirror.WirelessHost POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:iPhoneMirror.DnsSdShim>
        $<TARGET_FILE_DIR:iPhoneMirror.WirelessHost>/dnssd.dll)

install(TARGETS iPhoneMirror.WirelessHost iPhoneMirror.DnsSdShim
    RUNTIME DESTINATION Wireless)

if(IPHONEMIRROR_BUILD_TESTS)
    add_library(iPhoneMirror.WirelessHost.Stub SHARED
        tests/WirelessHostStub.cpp
        tests/WirelessHostStub.def)
    set_target_properties(iPhoneMirror.WirelessHost.Stub PROPERTIES
        PREFIX ""
        OUTPUT_NAME "airplay2dll.stub")

    add_executable(iPhoneMirror.WirelessHost.Smoke tests/WirelessHostSmoke.cpp)
    target_include_directories(iPhoneMirror.WirelessHost.Smoke PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_definitions(iPhoneMirror.WirelessHost.Smoke PRIVATE
        UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(iPhoneMirror.WirelessHost.Smoke PRIVATE
        /W4 /permissive- /utf-8 /EHsc)
    target_link_libraries(iPhoneMirror.WirelessHost.Smoke PRIVATE ws2_32)
    add_dependencies(iPhoneMirror.WirelessHost.Smoke
        iPhoneMirror.WirelessHost iPhoneMirror.WirelessHost.Stub)
    add_test(NAME WirelessHostSmoke COMMAND iPhoneMirror.WirelessHost.Smoke
        $<TARGET_FILE:iPhoneMirror.WirelessHost>
        $<TARGET_FILE:iPhoneMirror.WirelessHost.Stub>)
    set_tests_properties(WirelessHostSmoke PROPERTIES TIMEOUT 20)
endif()
