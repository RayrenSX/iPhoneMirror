// DNS-SD compatibility layer for AirPlayServer using the Windows 10+ DNS API.

#include <Windows.h>
#include <WinDNS.h>
#include <WinSock2.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <format>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace {

// Preserve the authentication/audio transport bits required by screen
// mirroring, while clearing video, photo, HLS, slideshow, rotation-advertising,
// playback-queue, and second-word cloud-media capabilities.
constexpr std::wstring_view MirroringOnlyFeatures = L"0x5A7FFEC0,0x0";
// Match UxPlay's HLS-enabled legacy feature set. Advertising the newer
// playback-queue/cloud/TLS bits makes some video apps require AirPlay 2
// services that this receiver intentionally does not implement, so they hide
// the route before attempting a connection.
constexpr std::wstring_view MediaCastFeatures = L"0x5A7FFEF7,0x0";
constexpr std::wstring_view LegacyAirPlayModel = L"AppleTV3,2";
constexpr std::wstring_view LegacyAirPlayVersion = L"220.68";
constexpr std::wstring_view AirPlayPairingIdentity =
    L"2e388006-13ba-4041-9a67-25dd4a43d536";

std::wstring receiver_mode() {
    wchar_t mode[16]{};
    const auto length = GetEnvironmentVariableW(
        L"IPHONE_MIRROR_AIRPLAY_MODE", mode, static_cast<DWORD>(std::size(mode)));
    return length < std::size(mode) ? std::wstring(mode, length) : std::wstring{};
}

std::wstring environment_value(const wchar_t* name, std::size_t capacity) {
    std::wstring value(capacity, L'\0');
    const auto length = GetEnvironmentVariableW(
        name, value.data(), static_cast<DWORD>(value.size()));
    if (length == 0 || length >= value.size()) return {};
    value.resize(length);
    return value;
}

bool is_lower_hex(std::wstring_view value, std::size_t length) noexcept {
    return value.size() == length && std::ranges::all_of(value,
        [](wchar_t character) {
            return (character >= L'0' && character <= L'9') ||
                (character >= L'a' && character <= L'f');
        });
}

using DNSServiceRef = struct Registration*;
using DNSServiceFlags = std::uint32_t;
using DNSServiceErrorType = std::int32_t;
using DNSServiceRegisterReply = void (__stdcall*)(DNSServiceRef, DNSServiceFlags,
    DNSServiceErrorType, const char*, const char*, const char*, void*);

union TXTRecordRef {
    char private_data[16];
    char* force_alignment;
};

struct TxtRecordState {
    std::vector<std::pair<std::string, std::vector<std::uint8_t>>> entries;
    std::vector<std::uint8_t> bytes;
};

struct Registration {
    DNS_SERVICE_CANCEL cancel{};
    PDNS_SERVICE_INSTANCE instance{};
    DNS_SERVICE_REGISTER_REQUEST request{};
    DNSServiceRegisterReply callback{};
    void* callback_context{};
    std::string name;
    std::string regtype;
    std::wstring service_identity;
    std::atomic_bool owns_service_identity{};
    HANDLE completion{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
};

std::mutex active_services_mutex;
std::unordered_set<std::wstring> active_services;
std::atomic_uint16_t screen_mirroring_network_port{};

void release_service_identity(Registration& registration) noexcept {
    if (!registration.owns_service_identity.exchange(false)) return;
    std::scoped_lock lock(active_services_mutex);
    active_services.erase(registration.service_identity);
}

TxtRecordState* txt_state(const TXTRecordRef* record) noexcept {
    TxtRecordState* state{};
    if (record) std::memcpy(&state, record->private_data, sizeof(state));
    return state;
}

void set_txt_state(TXTRecordRef* record, TxtRecordState* state) noexcept {
    if (!record) return;
    std::memset(record->private_data, 0, sizeof(record->private_data));
    std::memcpy(record->private_data, &state, sizeof(state));
}

bool rebuild_txt(TxtRecordState& state) {
    std::vector<std::uint8_t> bytes;
    for (const auto& [key, value] : state.entries) {
        const auto length = key.size() + (value.empty() ? 0U : 1U + value.size());
        if (length > 255 || bytes.size() + length + 1U > 65535) return false;
        bytes.push_back(static_cast<std::uint8_t>(length));
        bytes.insert(bytes.end(), key.begin(), key.end());
        if (!value.empty()) {
            bytes.push_back('=');
            bytes.insert(bytes.end(), value.begin(), value.end());
        }
    }
    state.bytes = std::move(bytes);
    return true;
}

std::wstring widen(std::string_view value) {
    if (value.empty()) return {};
    const auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring result(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), count);
    return result;
}

std::wstring host_name() {
    DWORD size{};
    GetComputerNameExW(ComputerNameDnsHostname, nullptr, &size);
    std::wstring result(size, L'\0');
    if (size == 0 || !GetComputerNameExW(ComputerNameDnsHostname,
            result.data(), &size)) return L"iPhoneMirror.local";
    result.resize(size);
    result += L".local";
    return result;
}

std::array<std::uint8_t, 6> media_device_id() noexcept {
    wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1]{};
    DWORD length = static_cast<DWORD>(std::size(computer));
    if (!GetComputerNameW(computer, &length)) {
        constexpr wchar_t fallback[] = L"iPhoneMirror";
        std::copy(std::begin(fallback), std::end(fallback), computer);
        length = static_cast<DWORD>(std::size(fallback) - 1);
    }
    std::uint64_t hash = 1469598103934665603ULL;
    for (DWORD index = 0; index < length; ++index) {
        hash ^= static_cast<std::uint8_t>(computer[index]);
        hash *= 1099511628211ULL;
    }
    // Bump the media-route identity when its advertised protocol profile
    // changes. iOS and third-party apps otherwise keep the old audio-only
    // classification cached even after the TXT record is corrected.
    constexpr std::string_view profile = "video-cast-v2";
    for (const auto byte : profile) {
        hash ^= static_cast<std::uint8_t>(byte);
        hash *= 1099511628211ULL;
    }
    return {0x02, static_cast<std::uint8_t>(hash),
        static_cast<std::uint8_t>(hash >> 8),
        static_cast<std::uint8_t>(hash >> 16),
        static_cast<std::uint8_t>(hash >> 24),
        static_cast<std::uint8_t>(hash >> 32)};
}

std::wstring media_device_id_text(bool compact) {
    wchar_t configured[32]{};
    const auto length = GetEnvironmentVariableW(L"IPHONE_MIRROR_AIRPLAY_DEVICE_ID",
        configured, static_cast<DWORD>(std::size(configured)));
    if (length == 17) {
        const std::wstring_view value(configured, length);
        const auto valid = std::ranges::all_of(value, [index = std::size_t{}]
            (wchar_t character) mutable {
                const auto separator = index++ % 3 == 2;
                return separator ? character == L':' : std::iswxdigit(character) != 0;
            });
        if (valid) {
            if (!compact) return std::wstring(value);
            std::wstring result;
            result.reserve(12);
            for (const auto character : value)
                if (character != L':') result.push_back(character);
            return result;
        }
    }
    const auto id = media_device_id();
    return compact
        ? std::format(L"{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}",
            id[0], id[1], id[2], id[3], id[4], id[5])
        : std::format(L"{:02X}:{:02X}:{:02X}:{:02X}:{:02X}:{:02X}",
            id[0], id[1], id[2], id[3], id[4], id[5]);
}

std::vector<std::pair<std::wstring, std::wstring>> parse_txt(
    std::uint16_t length, const void* source) {
    std::vector<std::pair<std::wstring, std::wstring>> result;
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    std::size_t offset{};
    while (bytes && offset < length) {
        const auto item_length = bytes[offset++];
        if (offset + item_length > length) break;
        const std::string_view item(reinterpret_cast<const char*>(bytes + offset),
            item_length);
        const auto separator = item.find('=');
        auto key = widen(item.substr(0, separator));
        auto value = separator == std::string_view::npos
            ? std::wstring{} : widen(item.substr(separator + 1));
        if (!key.empty()) result.emplace_back(std::move(key), std::move(value));
        offset += item_length;
    }
    return result;
}

void WINAPI registration_complete(DWORD status, void* context,
    PDNS_SERVICE_INSTANCE) {
    const auto registration = static_cast<Registration*>(context);
    if (!registration) return;
    if (status != ERROR_SUCCESS) release_service_identity(*registration);
    if (registration->callback) {
        registration->callback(registration, 0,
            status == ERROR_SUCCESS ? 0 : -65537,
            registration->name.c_str(), registration->regtype.c_str(),
            "local.", registration->callback_context);
    }
    if (registration->completion) SetEvent(registration->completion);
}

} // namespace

extern "C" __declspec(dllexport) void __stdcall TXTRecordCreate(
    TXTRecordRef* record, std::uint16_t, void*) {
    if (!record) return;
    set_txt_state(record, new (std::nothrow) TxtRecordState());
}

extern "C" __declspec(dllexport) DNSServiceErrorType __stdcall TXTRecordSetValue(
    TXTRecordRef* record, const char* key, std::uint8_t value_size,
    const void* value) {
    auto* state = txt_state(record);
    if (!state || !key || !*key || std::strchr(key, '=') ||
        (value_size != 0 && !value)) return -65540;
    auto existing = std::find_if(state->entries.begin(), state->entries.end(),
        [key](const auto& entry) { return entry.first == key; });
    std::vector<std::uint8_t> bytes(value_size);
    if (value_size != 0) std::memcpy(bytes.data(), value, value_size);
    if (existing == state->entries.end())
        state->entries.emplace_back(key, std::move(bytes));
    else
        existing->second = std::move(bytes);
    return rebuild_txt(*state) ? 0 : -65540;
}

extern "C" __declspec(dllexport) std::uint16_t __stdcall TXTRecordGetLength(
    const TXTRecordRef* record) {
    const auto* state = txt_state(record);
    return state ? static_cast<std::uint16_t>(state->bytes.size()) : 0;
}

extern "C" __declspec(dllexport) const void* __stdcall TXTRecordGetBytesPtr(
    const TXTRecordRef* record) {
    const auto* state = txt_state(record);
    return !state || state->bytes.empty() ? nullptr : state->bytes.data();
}

extern "C" __declspec(dllexport) void __stdcall TXTRecordDeallocate(
    TXTRecordRef* record) {
    delete txt_state(record);
    set_txt_state(record, nullptr);
}

extern "C" __declspec(dllexport) DNSServiceErrorType __stdcall DNSServiceRegister(
    DNSServiceRef* output, DNSServiceFlags, std::uint32_t interface_index,
    const char* name, const char* regtype, const char*, const char*,
    std::uint16_t network_port, std::uint16_t txt_length, const void* txt_record,
    DNSServiceRegisterReply callback, void* callback_context) {
    if (!output || !name || !*name || !regtype || !*regtype) return -65540;
    *output = nullptr;

    auto registration = std::make_unique<Registration>();
    if (!registration->completion) return -65539;
    registration->callback = callback;
    registration->callback_context = callback_context;
    registration->name = name;
    registration->regtype = regtype;
    const auto mode = receiver_mode();
    const auto media_mode = mode == L"media" || mode == L"combined";
    const auto service_type = std::string_view(regtype);

    if (service_type == "_raop._tcp") {
        // Legacy mirror-only mode exposes RAOP through its redirected AirPlay
        // record. Combined mode follows AirPlay receivers such as UxPlay and
        // publishes the matching RAOP and AirPlay records as one device.
        if (!media_mode) {
            screen_mirroring_network_port.store(network_port, std::memory_order_release);
            *output = registration.release();
            return 0;
        }
    }

    if (service_type == "_airplay._tcp" && !media_mode) {
        const auto mirroring_port = screen_mirroring_network_port.load(
            std::memory_order_acquire);
        if (mirroring_port != 0) network_port = mirroring_port;
    }

    auto instance_name = widen(name);
    if (service_type == "_raop._tcp" && media_mode) {
        const auto separator = instance_name.find(L'@');
        const auto display_name = separator == std::wstring::npos
            ? instance_name : instance_name.substr(separator + 1);
        instance_name = media_device_id_text(true) + L"@" + display_name;
    }
    auto service_name = instance_name + L"." + widen(regtype) + L".local";
    registration->service_identity = service_name;
    {
        std::scoped_lock lock(active_services_mutex);
        const auto [_, inserted] = active_services.insert(service_name);
        if (!inserted) {
            // Upstream registers once per network adapter. Windows DNS-SD can
            // already advertise one registration on every adapter, so keeping
            // the duplicates would make iPhone display "name (1)" and leave
            // stale siblings after a receiver rename.
            *output = registration.release();
            return 0;
        }
        registration->owns_service_identity.store(true);
    }
    const auto host = host_name();
    auto properties = parse_txt(txt_length, txt_record);
    const auto set_property = [&properties](std::wstring_view key,
                                  std::wstring_view value) {
        const auto property = std::ranges::find_if(properties,
            [key](const auto& item) { return item.first == key; });
        if (property == properties.end()) properties.emplace_back(key, value);
        else property->second.assign(value);
    };
    if (service_type == "_airplay._tcp") {
        const auto advertised = media_mode ? MediaCastFeatures : MirroringOnlyFeatures;
        set_property(L"features", advertised);
        if (media_mode) {
            const auto public_key = environment_value(
                L"IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", 65);
            set_property(L"deviceid", media_device_id_text(false));
            set_property(L"model", LegacyAirPlayModel);
            set_property(L"srcvers", LegacyAirPlayVersion);
            set_property(L"pi", AirPlayPairingIdentity);
            if (is_lower_hex(public_key, 64)) set_property(L"pk", public_key);
            set_property(L"pw", L"false");
        }
    }
    else if (service_type == "_raop._tcp" && media_mode) {
        // UxPlay publishes the video/HLS feature mask on both service records.
        // Without RAOP `ft`, iOS route pickers classify this target as a pure
        // AirTunes speaker and never open the /play video-control channel.
        set_property(L"ft", MediaCastFeatures);
        const auto public_key = environment_value(
            L"IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", 65);
        set_property(L"am", LegacyAirPlayModel);
        set_property(L"vs", LegacyAirPlayVersion);
        if (is_lower_hex(public_key, 64)) set_property(L"pk", public_key);
        set_property(L"vv", L"2");
        set_property(L"cn", L"0,1,2,3");
        set_property(L"rhd", L"5.6.0.0");
    }
    std::vector<PCWSTR> keys;
    std::vector<PCWSTR> values;
    keys.reserve(properties.size());
    values.reserve(properties.size());
    for (const auto& property : properties) {
        keys.push_back(property.first.c_str());
        values.push_back(property.second.c_str());
    }
    registration->instance = DnsServiceConstructInstance(service_name.c_str(),
        host.c_str(), nullptr, nullptr, ntohs(network_port), 0, 0,
        static_cast<DWORD>(properties.size()), keys.data(), values.data());
    // Registration is best-effort. The AirPlay HTTP/RTP servers are useful
    // even when a particular Windows network profile rejects an advertisement.
    // Keep a live opaque ref so the upstream library does not tear down those
    // servers just because DNS-SD is unavailable on one adapter.
    if (!registration->instance) {
        release_service_identity(*registration);
        *output = registration.release();
        return 0;
    }
    registration->request.Version = 1;
    // Keep only the first upstream registration for each service type and
    // publish it on that one adapter. Publishing a single logical instance on
    // every adapter makes iPhone expose duplicate names when two adapters are
    // connected to the same LAN.
    registration->request.InterfaceIndex = interface_index;
    registration->request.pServiceInstance = registration->instance;
    registration->request.pRegisterCompletionCallback = registration_complete;
    registration->request.pQueryContext = registration.get();
    registration->request.unicastEnabled = FALSE;
    const auto status = DnsServiceRegister(&registration->request,
        &registration->cancel);
    // The Windows DNS-SD API is asynchronous. DNS_REQUEST_PENDING is its
    // documented success return, while ERROR_SUCCESS is accepted for
    // compatibility with synchronous implementations. Treating 9506 as a
    // failure used to drop the active-service guard immediately, so the
    // upstream per-adapter loop published the same receiver twice.
    if (status != DNS_REQUEST_PENDING && status != ERROR_SUCCESS) {
        release_service_identity(*registration);
        DnsServiceFreeInstance(registration->instance);
        registration->instance = nullptr;
        *output = registration.release();
        return 0;
    }
    *output = registration.release();
    return 0;
}

extern "C" __declspec(dllexport) void __stdcall DNSServiceRefDeallocate(
    DNSServiceRef registration) {
    if (!registration) return;
    if (registration->instance) {
        ResetEvent(registration->completion);
        auto deregister = registration->request;
        deregister.pRegisterCompletionCallback = registration_complete;
        // DnsServiceDeRegister requires a null cancel parameter and reports
        // asynchronous success as DNS_REQUEST_PENDING as well.
        const auto status = DnsServiceDeRegister(&deregister, nullptr);
        if (status == DNS_REQUEST_PENDING || status == ERROR_SUCCESS) {
            if (WaitForSingleObject(registration->completion, 2000) != WAIT_OBJECT_0) {
                // The callback still owns this context. The wireless host is
                // process-isolated and is about to exit, so leaking this one
                // registration is safer than freeing callback-owned memory.
                return;
            }
        }
        DnsServiceFreeInstance(registration->instance);
    }
    release_service_identity(*registration);
    CloseHandle(registration->completion);
    delete registration;
}
