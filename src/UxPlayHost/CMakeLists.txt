add_executable(iPhoneMirror.UxPlayHost UxPlayHost.cpp)

target_include_directories(iPhoneMirror.UxPlayHost PRIVATE
    ${PROJECT_SOURCE_DIR}/src/WirelessHost)

target_compile_definitions(iPhoneMirror.UxPlayHost PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(iPhoneMirror.UxPlayHost PRIVATE
    /W4
    /permissive-
    /utf-8
    /EHsc
)

target_link_libraries(iPhoneMirror.UxPlayHost PRIVATE advapi32)

set_target_properties(iPhoneMirror.UxPlayHost PROPERTIES
    OUTPUT_NAME "iPhoneMirror.UxPlayHost"
)

install(TARGETS iPhoneMirror.UxPlayHost
    RUNTIME DESTINATION Wireless)

if(IPHONEMIRROR_BUILD_TESTS)
    add_executable(iPhoneMirror.UxPlayHost.Fixture
        tests/UxPlayHostFixture.cpp)
    target_compile_definitions(iPhoneMirror.UxPlayHost.Fixture PRIVATE
        UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(iPhoneMirror.UxPlayHost.Fixture PRIVATE
        /W4 /permissive- /utf-8 /EHsc)
    set_target_properties(iPhoneMirror.UxPlayHost.Fixture PROPERTIES
        OUTPUT_NAME "iPhoneMirror.UxPlayHost.Fixture")

    add_executable(iPhoneMirror.UxPlayHost.Smoke tests/UxPlayHostSmoke.cpp)
    target_include_directories(iPhoneMirror.UxPlayHost.Smoke PRIVATE
        ${PROJECT_SOURCE_DIR}/src/WirelessHost)
    target_compile_definitions(iPhoneMirror.UxPlayHost.Smoke PRIVATE
        UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(iPhoneMirror.UxPlayHost.Smoke PRIVATE
        /W4 /permissive- /utf-8 /EHsc)
    add_dependencies(iPhoneMirror.UxPlayHost.Smoke
        iPhoneMirror.UxPlayHost iPhoneMirror.UxPlayHost.Fixture)
    add_test(NAME UxPlayHostSmoke COMMAND iPhoneMirror.UxPlayHost.Smoke
        $<TARGET_FILE:iPhoneMirror.UxPlayHost>
        $<TARGET_FILE:iPhoneMirror.UxPlayHost.Fixture>)
    set_tests_properties(UxPlayHostSmoke PROPERTIES TIMEOUT 20)
endif()
