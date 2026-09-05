// SPDX-License-Identifier: GPL-3.0-only

#include "IpcProtocol.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <format>
#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace {

[[nodiscard]] std::wstring quote_argument(std::wstring_view value) {
    std::wstring quoted{L"\""};
    std::size_t slashes{};
    for (const auto character : value) {
        if (character == L'\\') {
            ++slashes;
            continue;
        }
        if (character == L'\"') {
            quoted.append(slashes * 2 + 1, L'\\');
            quoted.push_back(L'\"');
            slashes = 0;
            continue;
        }
        quoted.append(slashes, L'\\');
        slashes = 0;
        quoted.push_back(character);
    }
    quoted.append(slashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

[[nodiscard]] bool read_exact(HANDLE handle, void* output, std::size_t size) {
    auto* bytes = static_cast<std::uint8_t*>(output);
    while (size != 0) {
        DWORD read{};
        if (!ReadFile(handle, bytes, static_cast<DWORD>(std::min<std::size_t>(size,
                1024U * 1024U)), &read, nullptr) || read == 0) {
            return false;
        }
        bytes += read;
        size -= read;
    }
    return true;
}

[[nodiscard]] std::string header_text(const char* value, std::size_t size) {
    return std::string(value, strnlen_s(value, size));
}

[[nodiscard]] bool launch(const std::filesystem::path& host,
    const std::filesystem::path& fixture, const std::wstring& pipe,
    const std::wstring& stop_event, PROCESS_INFORMATION& process) {
    const auto command = quote_argument(host.wstring()) + L" --pipe " +
        quote_argument(pipe) + L" --stop-event " + quote_argument(stop_event) +
        L" --name \"Fixture Receiver\" --parent-pid " +
        std::to_wstring(GetCurrentProcessId()) + L" --width 960 --height 540 --fps 30 --uxplay " +
        quote_argument(fixture.wstring());
    STARTUPINFOW startup{.cb = sizeof(startup)};
    auto mutable_command = command;
    return CreateProcessW(host.c_str(), mutable_command.data(), nullptr, nullptr,
        FALSE, CREATE_NO_WINDOW, nullptr, host.parent_path().c_str(), &startup,
        &process) != FALSE;
}

[[nodiscard]] int fail(std::string_view message) {
    std::cerr << message << '\n';
    return 1;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 3 || argc > 4) return fail("expected UxPlay host and fixture paths");
    const bool startup_only = argc == 4 && std::wstring_view(argv[3]) == L"--startup-only";
    const auto host = std::filesystem::absolute(argv[1]);
    const auto fixture = std::filesystem::absolute(argv[2]);
    if (!std::filesystem::is_regular_file(host) || !std::filesystem::is_regular_file(fixture))
        return fail("test executable is missing");

    const auto suffix = std::format(L"{}-{}", GetCurrentProcessId(), GetTickCount64());
    const auto pipe_name = L"\\\\.\\pipe\\iPhoneMirror-UxPlay-Smoke-" + suffix;
    const auto stop_name = L"Local\\iPhoneMirror-UxPlay-Smoke-Stop-" + suffix;
    const auto pipe = CreateNamedPipeW(pipe_name.c_str(), PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 64U * 1024U, 8U * 1024U * 1024U, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return fail("could not create smoke IPC pipe");
    const auto stop_event = CreateEventW(nullptr, TRUE, FALSE, stop_name.c_str());
    if (!stop_event) {
        CloseHandle(pipe);
        return fail("could not create smoke stop event");
    }

    PROCESS_INFORMATION host_process{};
    if (!launch(host, fixture, pipe_name, stop_name, host_process)) {
        CloseHandle(stop_event);
        CloseHandle(pipe);
        return fail("could not launch UxPlay host");
    }
    CloseHandle(host_process.hThread);
    const auto connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
        GetLastError() == ERROR_PIPE_CONNECTED;
    if (!connected) {
        TerminateProcess(host_process.hProcess, 1);
        CloseHandle(host_process.hProcess);
        CloseHandle(stop_event);
        CloseHandle(pipe);
        return fail("UxPlay host did not connect to core IPC");
    }

    bool ready{};
    bool provisional_connected{};
    bool provisional_disconnected{};
    bool connected_device{};
    bool device_info{};
    bool video{};
    bool audio{};
    std::uint64_t first_audio_sequence{};
    std::uint64_t first_video_sequence{};
    std::uint64_t provisional_connected_sequence{};
    std::uint64_t provisional_disconnected_sequence{};
    std::uint64_t connected_sequence{};
    std::uint64_t device_info_sequence{};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
    while (std::chrono::steady_clock::now() < deadline &&
        (startup_only ? !ready : !(ready && provisional_connected &&
            provisional_disconnected && connected_device && device_info && video && audio))) {
        iPhoneMirror::wireless::MessageHeader header;
        if (!read_exact(pipe, &header, sizeof(header))) break;
        if (header.magic != iPhoneMirror::wireless::IpcMagic ||
            header.version != iPhoneMirror::wireless::IpcVersion ||
            header.payload_size > iPhoneMirror::wireless::MaxPayloadBytes) {
            SetEvent(stop_event);
            WaitForSingleObject(host_process.hProcess, 2000);
            CloseHandle(host_process.hProcess);
            CloseHandle(stop_event);
            CloseHandle(pipe);
            return fail("UxPlay host emitted an invalid IPC header");
        }
        std::vector<std::uint8_t> payload(header.payload_size);
        if (!payload.empty() && !read_exact(pipe, payload.data(), payload.size())) break;
        switch (header.type) {
        case iPhoneMirror::wireless::MessageType::Ready:
            ready = true;
            break;
        case iPhoneMirror::wireless::MessageType::Connected:
            if (header_text(header.device_id,
                    iPhoneMirror::wireless::DeviceIdBytes) == "uxplay-client") {
                provisional_connected = true;
                provisional_connected_sequence = header.sequence;
            } else if (header_text(header.device_id,
                           iPhoneMirror::wireless::DeviceIdBytes) == "fixture-id" &&
                header_text(header.device_name,
                    iPhoneMirror::wireless::DeviceNameBytes) == "Fixture iPhone") {
                connected_device = true;
                connected_sequence = header.sequence;
            }
            break;
        case iPhoneMirror::wireless::MessageType::Disconnected:
            provisional_disconnected = header_text(header.device_id,
                iPhoneMirror::wireless::DeviceIdBytes) == "uxplay-client";
            if (provisional_disconnected)
                provisional_disconnected_sequence = header.sequence;
            break;
        case iPhoneMirror::wireless::MessageType::DeviceInfo:
            if (header_text(header.device_id,
                    iPhoneMirror::wireless::DeviceIdBytes) == "fixture-id" &&
                header_text(header.product_type,
                    iPhoneMirror::wireless::ProductTypeBytes) == "iPhone14,2") {
                device_info = true;
                device_info_sequence = header.sequence;
            }
            break;
        case iPhoneMirror::wireless::MessageType::Video:
            if (first_video_sequence == 0) first_video_sequence = header.sequence;
            video = header.width == 540 && header.height == 960 &&
                header.stride[0] == 540 && header.stride[1] == 270 &&
                header.stride[2] == 270 &&
                payload.size() == static_cast<std::size_t>(540) * 960 * 3U / 2U &&
                !payload.empty() && payload.front() == 0x80;
            break;
        case iPhoneMirror::wireless::MessageType::Audio:
            if (first_audio_sequence == 0) first_audio_sequence = header.sequence;
            audio = header.sample_rate == 44100 && header.channels == 2 &&
                header.bits_per_sample == 16 && payload.size() == 1764 &&
                payload[0] == 0x34 && payload[1] == 0x12;
            break;
        default:
            break;
        }
    }

    SetEvent(stop_event);
    WaitForSingleObject(host_process.hProcess, 3000);
    CloseHandle(host_process.hProcess);
    CloseHandle(stop_event);
    CloseHandle(pipe);
    if (!ready || (!startup_only &&
        (!provisional_connected || !provisional_disconnected || !connected_device ||
            !device_info || !video || !audio ||
            !(provisional_connected_sequence < provisional_disconnected_sequence &&
                provisional_disconnected_sequence < connected_sequence &&
                connected_sequence < device_info_sequence &&
                first_audio_sequence != 0 && first_video_sequence != 0))))
        return fail("UxPlay host did not relay every expected IPC message");

    return 0;
}
