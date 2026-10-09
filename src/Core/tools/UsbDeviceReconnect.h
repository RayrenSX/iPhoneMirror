#pragma once

#include <string_view>
#include <cstdint>

// Cycles only the hub port currently occupied by the exact selected iPhone.
// All phone/stream handles must have been released before calling this.
// 0: request accepted; 44: Windows requires administrator access.
int reconnect_selected_apple_usb(std::string_view serial,
    std::uint64_t deadline = 0);
