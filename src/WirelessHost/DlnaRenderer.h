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
        std::function<void(std::string_view, double)> play;
        std::function<void()> stop;
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

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace iPhoneMirror::wireless
