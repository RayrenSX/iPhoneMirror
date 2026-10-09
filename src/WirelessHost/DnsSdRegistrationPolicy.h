#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <atomic>

namespace iPhoneMirror::wireless {

enum class DnsSdCompletionOwner : std::uint8_t {
    Pending,
    Callback,
    Cancel,
};

class DnsSdCompletionGate {
public:
    [[nodiscard]] bool try_claim_callback() noexcept {
        auto expected = static_cast<std::uint8_t>(DnsSdCompletionOwner::Pending);
        return owner_.compare_exchange_strong(expected,
            static_cast<std::uint8_t>(DnsSdCompletionOwner::Callback),
            std::memory_order_acq_rel, std::memory_order_acquire);
    }

    [[nodiscard]] bool try_claim_cancel() noexcept {
        auto expected = static_cast<std::uint8_t>(DnsSdCompletionOwner::Pending);
        return owner_.compare_exchange_strong(expected,
            static_cast<std::uint8_t>(DnsSdCompletionOwner::Cancel),
            std::memory_order_acq_rel, std::memory_order_acquire);
    }

    [[nodiscard]] bool callback_started() const noexcept {
        return owner_.load(std::memory_order_acquire) ==
            static_cast<std::uint8_t>(DnsSdCompletionOwner::Callback);
    }

private:
    std::atomic_uint8_t owner_{
        static_cast<std::uint8_t>(DnsSdCompletionOwner::Pending)};
};

// Windows DNS-SD uses InterfaceIndex=0 to publish on every eligible
// interface. Keep that value distinct from the internal "not selected yet"
// value used by the registration retry state machine.
inline constexpr std::uint32_t DnsSdAllInterfaces = 0xFFFFFFFFu;

constexpr std::uint32_t dns_sd_registration_interface(
    std::uint32_t requested, std::uint32_t preferred) noexcept {
    // Upstream registers the same receiver once per adapter. Those references
    // share one logical registration here, so its native registration must
    // cover every interface rather than retain the first reference's adapter.
    // Otherwise Ethernet ownership hides the receiver from Mobile Hotspot.
    if (preferred != 0) return DnsSdAllInterfaces;
    return requested;
}

constexpr std::uint32_t dns_sd_native_interface(
    std::uint32_t interface_index) noexcept {
    return interface_index == DnsSdAllInterfaces ? 0 : interface_index;
}

struct DnsSdLeaseAcquire {
    bool first{};
    bool owner{};
};

struct DnsSdLeaseRelease {
    bool known{};
    bool last{};
    bool owner_released{};
    std::uintptr_t new_owner{};
    std::uint32_t new_owner_interface{};
};

// Keeps the one real DNS-SD registration alive while upstream AirPlayServer
// holds one or more per-interface opaque references. This is deliberately
// platform-independent so the ownership edge cases are testable without
// publishing a service on the local network.
class DnsSdRegistrationLeases {
public:
    DnsSdLeaseAcquire acquire(std::uintptr_t id, std::uint32_t interface_index) {
        if (id == 0 || leases_.contains(id)) return {};
        const auto first = leases_.empty();
        leases_.emplace(id, interface_index);
        if (first) owner_ = id;
        return {first, owner_ == id};
    }

    DnsSdLeaseRelease release(std::uintptr_t id) {
        const auto found = leases_.find(id);
        if (found == leases_.end()) return {};
        const auto owner_released = owner_ == id;
        leases_.erase(found);
        if (leases_.empty()) {
            owner_ = 0;
            return {true, true, owner_released, 0, 0};
        }
        if (!owner_released) return {true, false, false, owner_, interface_for(owner_)};
        const auto replacement = leases_.begin();
        owner_ = replacement->first;
        return {true, false, true, owner_, replacement->second};
    }

    [[nodiscard]] std::size_t size() const noexcept { return leases_.size(); }
    [[nodiscard]] std::uintptr_t owner() const noexcept { return owner_; }
    [[nodiscard]] std::uint32_t owner_interface() const noexcept {
        return interface_for(owner_);
    }

    void assign_missing_interfaces(std::uint32_t interface_index) noexcept {
        if (interface_index == 0) return;
        for (auto& [_, value] : leases_)
            if (value == 0) value = interface_index;
    }

private:
    [[nodiscard]] std::uint32_t interface_for(std::uintptr_t id) const noexcept {
        const auto found = leases_.find(id);
        return found == leases_.end() ? 0 : found->second;
    }

    std::map<std::uintptr_t, std::uint32_t> leases_;
    std::uintptr_t owner_{};
};

} // namespace iPhoneMirror::wireless
