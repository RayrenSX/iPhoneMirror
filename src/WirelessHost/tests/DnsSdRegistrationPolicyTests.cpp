#include "DnsSdRegistrationPolicy.h"

#include <array>
#include <cstdint>
#include <iostream>
#include <limits>

int main() {
    constexpr std::array cases{
        std::array{std::uint32_t{0}, std::uint32_t{7}, std::uint32_t{7}},
        std::array{std::uint32_t{0}, std::uint32_t{0}, std::uint32_t{0}},
        std::array{std::uint32_t{3}, std::uint32_t{0}, std::uint32_t{3}},
        std::array{std::uint32_t{21}, std::uint32_t{42}, std::uint32_t{42}},
        std::array{std::numeric_limits<std::uint32_t>::max(),
            std::uint32_t{0}, std::numeric_limits<std::uint32_t>::max()},
    };

    for (const auto& [requested, preferred, expected] : cases) {
        if (iPhoneMirror::wireless::dns_sd_registration_interface(
                requested, preferred) != expected) {
            std::cerr << "DNS-SD interface selection ignored the preferred adapter\n";
            return 1;
        }
    }

    iPhoneMirror::wireless::DnsSdRegistrationLeases leases;
    const auto first = leases.acquire(100, 7);
    if (!first.first || !first.owner || leases.size() != 1 ||
        leases.owner() != 100 || leases.owner_interface() != 7) {
        std::cerr << "first DNS-SD reference did not become the owner\n";
        return 1;
    }
    const auto duplicate = leases.acquire(200, 42);
    if (duplicate.first || duplicate.owner || leases.size() != 2 ||
        leases.owner() != 100 || leases.owner_interface() != 7) {
        std::cerr << "duplicate DNS-SD reference changed ownership\n";
        return 1;
    }
    const auto repeated = leases.acquire(200, 99);
    if (repeated.first || repeated.owner || leases.size() != 2) {
        std::cerr << "repeated DNS-SD lease id was inserted twice\n";
        return 1;
    }
    const auto unknown_release = leases.release(999);
    if (unknown_release.known || leases.size() != 2) {
        std::cerr << "unknown DNS-SD release changed the live leases\n";
        return 1;
    }
    const auto owner_release = leases.release(100);
    if (!owner_release.known || owner_release.last ||
        !owner_release.owner_released || owner_release.new_owner != 200 ||
        owner_release.new_owner_interface != 42 || leases.size() != 1) {
        std::cerr << "owner release dropped the shared registration\n";
        return 1;
    }
    const auto final_release = leases.release(200);
    if (!final_release.known || !final_release.last ||
        final_release.new_owner != 0 || leases.size() != 0 ||
        leases.owner() != 0 || leases.owner_interface() != 0) {
        std::cerr << "last DNS-SD reference did not close the lease\n";
        return 1;
    }

    iPhoneMirror::wireless::DnsSdRegistrationLeases pending;
    pending.acquire(300, 0);
    pending.acquire(301, 0);
    pending.assign_missing_interfaces(19);
    if (pending.owner_interface() != 19) {
        std::cerr << "pending DNS-SD leases did not adopt a recovered interface\n";
        return 1;
    }

    iPhoneMirror::wireless::DnsSdCompletionGate completion;
    if (!completion.try_claim_cancel() || completion.try_claim_callback() ||
        completion.callback_started()) {
        std::cerr << "DNS-SD cancellation completion gate was not exclusive\n";
        return 1;
    }
    iPhoneMirror::wireless::DnsSdCompletionGate callback_completion;
    if (!callback_completion.try_claim_callback() ||
        callback_completion.try_claim_cancel() ||
        !callback_completion.callback_started()) {
        std::cerr << "DNS-SD callback completion gate was not exclusive\n";
        return 1;
    }

    std::cout << "DNS-SD interface selection and lease lifecycle passed\n";
    return 0;
}
