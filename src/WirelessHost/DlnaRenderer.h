// SPDX-License-Identifier: GPL-3.0-only

#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <string_view>

namespace iPhoneMirror::wireless {

class DlnaRenderer final {
public:
    struct Callbacks {
        std::function<void(std::string_view, double, double, double, bool,
            std::uint64_t)> play;
        std::function<void(std::uint64_t)> stop;
        std::function<void(std::uint64_t)> pause;
        std::function<void(std::uint64_t)> resume;
        std::function<void(double, std::uint64_t)> seek;
        std::function<void(double)> set_volume;
        std::function<void(bool, double)> set_mute;
        std::function<void(double*, double*, double*)> get_play_info;
        std::function<void(std::string_view)> log;
    };

    DlnaRenderer();
    ~DlnaRenderer();
    DlnaRenderer(const DlnaRenderer&) = delete;
    DlnaRenderer& operator=(const DlnaRenderer&) = delete;

    bool start(std::string friendly_name, std::string uuid,
        std::uint16_t http_port, std::uint16_t ssdp_port, Callbacks callbacks);
    void stop() noexcept;
    bool update_transport_playback(std::uint64_t generation,
        std::string_view state) noexcept;
    void notify_transport_changed();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace iPhoneMirror::wireless
