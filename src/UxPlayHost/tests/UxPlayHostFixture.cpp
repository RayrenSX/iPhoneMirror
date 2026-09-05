// SPDX-License-Identifier: GPL-3.0-only

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace {

[[nodiscard]] std::wstring argument_value(int argc, wchar_t** argv,
    std::wstring_view name) {
    for (int index = 1; index + 1 < argc; ++index) {
        if (std::wstring_view(argv[index]) == name) return argv[index + 1];
    }
    return {};
}

[[nodiscard]] std::wstring pipe_location(std::wstring_view pipeline) {
    constexpr std::wstring_view marker = L"location=";
    const auto position = pipeline.find(marker);
    if (position == std::wstring_view::npos) return {};
    auto result = pipeline.substr(position + marker.size());
    if (!result.empty() && result.front() == L'"') {
        result.remove_prefix(1);
        const auto closing = result.find(L'"');
        if (closing == std::wstring_view::npos) return {};
        result = result.substr(0, closing);
        std::wstring unescaped;
        unescaped.reserve(result.size());
        for (std::size_t index = 0; index < result.size(); ++index) {
            if (result[index] == L'\\' && index + 1 < result.size() &&
                (result[index + 1] == L'\\' || result[index + 1] == L'"'))
                ++index;
            unescaped.push_back(result[index]);
        }
        return unescaped;
    }
    const auto delimiter = result.find_first_of(L" \t");
    if (delimiter != std::wstring_view::npos) result = result.substr(0, delimiter);
    return std::wstring(result);
}

[[nodiscard]] HANDLE connect_pipe(const std::wstring& name) {
    for (int attempt = 0; attempt < 100; ++attempt) {
        const auto pipe = CreateFileW(name.c_str(), GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        const auto error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)
            return INVALID_HANDLE_VALUE;
        WaitNamedPipeW(name.c_str(), 50);
    }
    return INVALID_HANDLE_VALUE;
}

[[nodiscard]] bool write_all(HANDLE pipe, const void* source,
    std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    while (size != 0) {
        DWORD written{};
        const auto request = static_cast<DWORD>(std::min<std::size_t>(size,
            1024U * 1024U));
        if (!WriteFile(pipe, bytes, request, &written, nullptr) || written == 0)
            return false;
        bytes += written;
        size -= written;
    }
    return true;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    for (int index = 1; index < argc; ++index) {
        if (std::wstring_view(argv[index]) == L"-v") return 0;
    }

    const auto receiver_name = argument_value(argc, argv, L"-n");
    const auto device_id = argument_value(argc, argv, L"-m");
    if (!receiver_name.starts_with(L"Fixture-Receiver-UxPlay-") ||
        !device_id.starts_with(L"02:") || device_id.size() != 17 ||
        argument_value(argc, argv, L"-s") != L"960x540@30" ||
        argument_value(argc, argv, L"-fps") != L"30") return 5;
    bool nofreeze{};
    for (int index = 1; index < argc; ++index) {
        nofreeze = nofreeze || std::wstring_view(argv[index]) == L"-nofreeze";
    }
    const auto parser = argument_value(argc, argv, L"-vp");
    const auto decoder = argument_value(argc, argv, L"-vd");
    const auto converter = argument_value(argc, argv, L"-vc");
    const auto video_sink = argument_value(argc, argv, L"-vs");
    if (!nofreeze ||
        parser.find(L"config-interval=-1") == std::wstring::npos ||
        parser.find(L"disable-passthrough=true") == std::wstring::npos ||
        decoder.find(L"avdec_h264") == std::wstring::npos ||
        decoder.find(L"output-corrupt=false") == std::wstring::npos ||
        decoder.find(L"discard-corrupted-frames=true") == std::wstring::npos ||
        decoder.find(L"automatic-request-sync-points=true") == std::wstring::npos ||
        converter.find(L"videoconvert") == std::wstring::npos ||
        converter.find(L"video/x-raw,format=I420") == std::wstring::npos ||
        video_sink.find(L"y4menc") == std::wstring::npos ||
        video_sink.find(L"filesink location=") == std::wstring::npos ||
        video_sink.find(L"videoscale") != std::wstring::npos) return 6;

    const auto video = pipe_location(video_sink);
    const auto audio = pipe_location(argument_value(argc, argv, L"-as"));
    if (video.empty() || audio.empty()) return 2;
    auto video_pipe = connect_pipe(video);
    const auto audio_pipe = connect_pipe(audio);
    if (video_pipe == INVALID_HANDLE_VALUE || audio_pipe == INVALID_HANDLE_VALUE) {
        if (video_pipe != INVALID_HANDLE_VALUE) CloseHandle(video_pipe);
        if (audio_pipe != INVALID_HANDLE_VALUE) CloseHandle(audio_pipe);
        return 3;
    }

    constexpr unsigned int width = 540;
    constexpr unsigned int height = 960;
    std::vector<std::uint8_t> frame(static_cast<std::size_t>(width) * height * 3U / 2U,
        0x40);
    std::fill_n(frame.begin(), static_cast<std::size_t>(width) * height,
        static_cast<std::uint8_t>(0x80));
    std::array<std::uint8_t, 1764> pcm{};
    for (std::size_t index = 0; index < pcm.size(); index += 4) {
        pcm[index] = 0x34;
        pcm[index + 1] = 0x12;
        pcm[index + 2] = 0x78;
        pcm[index + 3] = 0x56;
    }

    // Simulate a transport reset in the middle of a decoded frame. The host
    // must discard this partial payload before accepting the next Y4M stream.
    const std::string truncated_header =
        "YUV4MPEG2 W540 H960 F30:1 Ip A0:0 C420\nFRAME\n";
    if (!write_all(video_pipe, truncated_header.data(), truncated_header.size()) ||
        !write_all(video_pipe, frame.data(), frame.size() / 3U)) {
        CloseHandle(video_pipe);
        CloseHandle(audio_pipe);
        return 7;
    }
    FlushFileBuffers(video_pipe);
    CloseHandle(video_pipe);
    video_pipe = connect_pipe(video);
    if (video_pipe == INVALID_HANDLE_VALUE) {
        CloseHandle(audio_pipe);
        return 8;
    }

    // UxPlay can expose raw media before its connection log reaches the host.
    // Keep that order explicit so the smoke test covers replacement of the
    // provisional identity used for the first video/audio packets.
    const std::string y4m_header = "YUV4MPEG2 W540 H960 F30:1 Ip A0:0 C420\nFRAME\n";
    const auto video_written = write_all(video_pipe, y4m_header.data(), y4m_header.size()) &&
        write_all(video_pipe, frame.data(), frame.size());
    const auto audio_written = write_all(audio_pipe, pcm.data(), pcm.size());
    FlushFileBuffers(video_pipe);
    FlushFileBuffers(audio_pipe);
    Sleep(200);
    // Match FDH2/UxPlay's stable, user-facing connection log format.
    DWORD ignored{};
    const char connection[] =
        "connection request from Fixture iPhone (iPhone14,2) with deviceID = fixture-id\n";
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), connection,
        static_cast<DWORD>(sizeof(connection) - 1), &ignored, nullptr);
    Sleep(400);
    CloseHandle(video_pipe);
    CloseHandle(audio_pipe);
    return video_written && audio_written ? 0 : 4;
}
