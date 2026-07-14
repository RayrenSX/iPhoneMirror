cmake_minimum_required(VERSION 3.25)

project(iPhoneMirror VERSION 1.0.3 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "iPhoneMirror currently targets Windows 10/11 only")
endif()

option(IPHONEMIRROR_BUILD_TESTS "Build native protocol tests" ON)

if(IPHONEMIRROR_BUILD_TESTS)
    enable_testing()
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_subdirectory(src/Core)
add_subdirectory(src/WirelessHost)
