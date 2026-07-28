#pragma once

#include "VirtualCameraShared.h"

#include <cstdint>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>

namespace iPhoneMirror::virtual_camera {

struct FrameSnapshot {
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t stride{};
    std::int64_t timestamp_100ns{};
    std::uint64_t published_frames{};
    std::vector<std::uint8_t> pixels;
};

class FramePublisher {
public:
    FramePublisher() = default;
    FramePublisher(const FramePublisher&) = delete;
    FramePublisher& operator=(const FramePublisher&) = delete;
    ~FramePublisher();

    HRESULT open_for_current_user();
    HRESULT publish(const std::uint8_t* pixels, std::uint32_t width,
                    std::uint32_t height, std::uint32_t stride,
                    std::int64_t timestamp_100ns);
    void close() noexcept;

    [[nodiscard]] const std::wstring& channel_path() const noexcept {
        return backing_path_;
    }
    [[nodiscard]] std::uint64_t published_frames() const noexcept;
    [[nodiscard]] std::uint32_t published_width() const noexcept;
    [[nodiscard]] std::uint32_t published_height() const noexcept;

private:
    HANDLE backing_file_{INVALID_HANDLE_VALUE};
    HANDLE mapping_{};
    SharedFrameHeader* view_{};
    std::wstring backing_path_;
    std::jthread channel_worker_;

    void serve_channel_path(std::stop_token stop_token) const noexcept;
};

class FrameReader {
public:
    FrameReader() = default;
    FrameReader(const FrameReader&) = delete;
    FrameReader& operator=(const FrameReader&) = delete;
    ~FrameReader();

    HRESULT open(const wchar_t* channel_path);
    bool read(FrameSnapshot& snapshot) const;
    void close() noexcept;
    [[nodiscard]] bool is_open() const noexcept { return view_ != nullptr; }

private:
    HANDLE backing_file_{INVALID_HANDLE_VALUE};
    HANDLE mapping_{};
    const SharedFrameHeader* view_{};
};

} // namespace iPhoneMirror::virtual_camera
