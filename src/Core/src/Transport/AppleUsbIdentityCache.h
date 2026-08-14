#pragma once

#include "Transport/LibUsb0Readiness.h"
#include "Transport/QtUsbTransport.h"

#include <mutex>
#include <unordered_map>

namespace iPhoneMirror::transport {
namespace detail {

struct ActiveAppleUsbIdentity {
    std::string serial;
    std::size_t references{};
};

inline std::mutex& active_apple_usb_identity_mutex() {
    static std::mutex value;
    return value;
}

inline std::unordered_map<std::string, ActiveAppleUsbIdentity>&
active_apple_usb_identities() {
    static std::unordered_map<std::string, ActiveAppleUsbIdentity> value;
    return value;
}

} // namespace detail

inline std::string cached_active_apple_usb_serial(
    std::string_view topology) noexcept {
    if (topology.empty()) return {};
    try {
        std::scoped_lock lock(detail::active_apple_usb_identity_mutex());
        const auto& identities = detail::active_apple_usb_identities();
        const auto found = identities.find(std::string(topology));
        return found == identities.end() ? std::string{} : found->second.serial;
    } catch (...) {
        return {};
    }
}

inline bool retain_active_apple_usb_identity(
    const AppleUsbIdentity& identity) noexcept {
    if (identity.topology_id.empty() || identity.serial.empty()) return false;
    try {
        std::scoped_lock lock(detail::active_apple_usb_identity_mutex());
        auto& identities = detail::active_apple_usb_identities();
        auto [entry, inserted] = identities.try_emplace(identity.topology_id,
            detail::ActiveAppleUsbIdentity{identity.serial, 0});
        if (!inserted && !apple_usb_serial_equal(entry->second.serial,
                identity.serial)) return false;
        ++entry->second.references;
        return true;
    } catch (...) {
        return false;
    }
}

inline void release_active_apple_usb_identity(
    std::string_view topology, std::string_view serial) noexcept {
    if (topology.empty() || serial.empty()) return;
    try {
        std::scoped_lock lock(detail::active_apple_usb_identity_mutex());
        auto& identities = detail::active_apple_usb_identities();
        const auto found = identities.find(std::string(topology));
        if (found == identities.end() ||
            !apple_usb_serial_equal(found->second.serial, serial)) return;
        if (found->second.references > 1)
            --found->second.references;
        else
            identities.erase(found);
    } catch (...) {
    }
}

inline void forget_active_apple_usb_identity(
    std::string_view topology, std::string_view serial) noexcept {
    if (topology.empty() || serial.empty()) return;
    try {
        std::scoped_lock lock(detail::active_apple_usb_identity_mutex());
        auto& identities = detail::active_apple_usb_identities();
        const auto found = identities.find(std::string(topology));
        if (found == identities.end() ||
            !apple_usb_serial_equal(found->second.serial, serial)) return;
        identities.erase(found);
    } catch (...) {
    }
}

} // namespace iPhoneMirror::transport
