#pragma once

#include <algorithm>
#include <cstdint>
#include <ranges>
#include <string>
#include <string_view>
#include <utility>
#include <vector>
#include "../../third_party/airplay-server/patches/airplay-mirror-codec-policy.h"

namespace iPhoneMirror::wireless {

using DnsSdProperties = std::vector<std::pair<std::wstring, std::wstring>>;

inline std::uint16_t airplay_service_port(std::wstring_view mode,
    std::uint16_t requested, std::uint16_t mirroring) noexcept {
    return mode != L"media" && mirroring != 0 ? mirroring : requested;
}

// Bit 42 requests explicit H.264/HEVC codec configuration from recent iPhones.
inline constexpr std::wstring_view MirroringFeatures = L"0x5A7FFEE6,0x400";
inline constexpr std::wstring_view AvcOnlyMirroringFeatures = L"0x5A7FFEE6,0x0";
inline std::wstring_view mirroring_features() noexcept {
    return iphone_mirror_mirror_features_high() == 0U ?
        AvcOnlyMirroringFeatures : MirroringFeatures;
}
// Dedicated video receivers must not advertise Screen (bit 7) or the screen
// multicodec capability (bit 42), otherwise iOS lists them under mirroring.
inline constexpr std::wstring_view MediaCastFeatures = L"0x5A7FFE73,0x0";
inline constexpr std::wstring_view LegacyAirPlayModel = L"AppleTV3,2";
inline constexpr std::wstring_view LegacyAirPlayVersion = L"220.68";
inline constexpr std::wstring_view AirPlayPairingIdentity =
    L"2e388006-13ba-4041-9a67-25dd4a43d536";

inline bool is_lower_hex(std::wstring_view value, std::size_t length) noexcept {
    if (value.size() != length) return false;
    return std::all_of(value.begin(), value.end(), [](wchar_t character) {
        return (character >= L'0' && character <= L'9') ||
            (character >= L'a' && character <= L'f');
    });
}

inline void set_dns_sd_property(DnsSdProperties& properties,
    std::wstring_view key, std::wstring_view value) {
    const auto property = std::ranges::find_if(properties,
        [key](const auto& item) { return item.first == key; });
    if (property == properties.end()) properties.emplace_back(key, value);
    else property->second.assign(value);
}

// Apply the mode-specific TXT contract before handing a record to Bonjour.
// Keeping this policy independent from registration lets tests verify the
// advertised AirPlay identity without depending on a live network adapter.
inline void apply_dns_sd_advertisement_policy(std::string_view service_type,
    bool media_mode, std::wstring_view device_id, std::wstring_view public_key,
    DnsSdProperties& properties, bool video_handoff = true) {
    if (service_type == "_airplay._tcp") {
        set_dns_sd_property(properties, L"features",
            media_mode && video_handoff ? MediaCastFeatures : mirroring_features());
        set_dns_sd_property(properties, L"deviceid", device_id);
        if (media_mode) {
            set_dns_sd_property(properties, L"model", LegacyAirPlayModel);
            set_dns_sd_property(properties, L"srcvers", LegacyAirPlayVersion);
            set_dns_sd_property(properties, L"pi", AirPlayPairingIdentity);
            if (is_lower_hex(public_key, 64))
                set_dns_sd_property(properties, L"pk", public_key);
            set_dns_sd_property(properties, L"pw", L"false");
        }
    }
    else if (service_type == "_raop._tcp") {
        set_dns_sd_property(properties, L"ft",
            media_mode && video_handoff ? MediaCastFeatures : mirroring_features());
        if (!media_mode) return;
        set_dns_sd_property(properties, L"am", LegacyAirPlayModel);
        set_dns_sd_property(properties, L"vs", LegacyAirPlayVersion);
        if (is_lower_hex(public_key, 64))
            set_dns_sd_property(properties, L"pk", public_key);
        set_dns_sd_property(properties, L"vv", L"2");
        set_dns_sd_property(properties, L"cn", L"0,1,2,3");
        set_dns_sd_property(properties, L"rhd", L"5.6.0.0");
    }
}

} // namespace iPhoneMirror::wireless
