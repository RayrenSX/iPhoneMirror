// DNS-SD compatibility layer for AirPlayServer using the Windows 10+ DNS API.

#include "DnsSdRegistrationPolicy.h"

#include <WinSock2.h>
#include <Windows.h>
#include <WinDNS.h>
#include <iphlpapi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <format>
#include <limits>
#include <memory>
#include <optional>
#include <ranges>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
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

struct Registration;
struct LogicalRegistration;
struct DnsOperationContext;

using DNSServiceRef = Registration*;
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
    std::atomic_uint32_t references{1};
    std::atomic_bool released{};
    SRWLOCK callback_lock = SRWLOCK_INIT;
    CONDITION_VARIABLE callback_idle = CONDITION_VARIABLE_INIT;
    std::uint32_t callbacks_in_flight{};
    DNSServiceRegisterReply callback{};
    void* callback_context{};
    std::shared_ptr<LogicalRegistration> logical;
    std::uint32_t interface_index{};
};

enum class NativeRegistrationState {
    Prepared,
    Registering,
    Registered,
    Deregistering,
    Closed,
};

struct LogicalRegistration {
    PDNS_SERVICE_INSTANCE instance{};
    DNS_SERVICE_REGISTER_REQUEST request{};
    std::string name;
    std::string regtype;
    std::wstring service_identity;
    iPhoneMirror::wireless::DnsSdRegistrationLeases leases;
    NativeRegistrationState state{NativeRegistrationState::Prepared};
    std::uint32_t active_interface{};
    std::uint32_t requested_interface{};
    DnsOperationContext* registering_operation{};
    bool registration_callback_active{};
    bool register_after_callback{};

    ~LogicalRegistration() {
        if (instance) DnsServiceFreeInstance(instance);
    }
};

enum class DnsOperationKind {
    Register,
    Deregister,
};

struct DnsOperationContext {
    std::atomic_uint32_t references{2};
    iPhoneMirror::wireless::DnsSdCompletionGate completion;
    std::atomic_bool register_pending{};
    std::atomic_bool cancel_requested{};
    std::atomic_bool cancel_started{};
    std::shared_ptr<LogicalRegistration> logical;
    DnsOperationKind kind{};
    DNS_SERVICE_REGISTER_REQUEST request{};
    DNS_SERVICE_CANCEL cancel{};

    DnsOperationContext(std::shared_ptr<LogicalRegistration> registration,
        DnsOperationKind operation) noexcept
        : logical(std::move(registration)), kind(operation) {}
};

SRWLOCK active_services_lock = SRWLOCK_INIT;
std::unordered_map<std::wstring, std::shared_ptr<LogicalRegistration>> active_services;
std::atomic_uint16_t screen_mirroring_network_port{};
const char dns_sd_module_anchor{};
std::atomic_bool interface_retry_worker_running{};

void pin_dns_sd_module() noexcept;
void retry_prepared_registrations() noexcept;
void ensure_interface_retry_worker() noexcept;

struct CallbackFrame {
    Registration* registration{};
    CallbackFrame* previous{};
};

thread_local CallbackFrame* current_callback_frame{};

class ActiveServicesGuard {
public:
    ActiveServicesGuard() noexcept { AcquireSRWLockExclusive(&active_services_lock); }
    ~ActiveServicesGuard() { ReleaseSRWLockExclusive(&active_services_lock); }

    ActiveServicesGuard(const ActiveServicesGuard&) = delete;
    ActiveServicesGuard& operator=(const ActiveServicesGuard&) = delete;
};

std::uintptr_t registration_id(const Registration* registration) noexcept {
    return reinterpret_cast<std::uintptr_t>(registration);
}

void retain_registration(Registration* registration) noexcept {
    registration->references.fetch_add(1, std::memory_order_relaxed);
}

void release_registration(Registration* registration) noexcept {
    if (registration && registration->references.fetch_sub(
            1, std::memory_order_acq_rel) == 1) {
        delete registration;
    }
}

bool callback_is_current(const Registration* registration) noexcept {
    for (auto* frame = current_callback_frame; frame; frame = frame->previous) {
        if (frame->registration == registration) return true;
    }
    return false;
}

void wait_for_registration_callbacks(Registration* registration) noexcept {
    if (!registration || callback_is_current(registration)) return;
    AcquireSRWLockExclusive(&registration->callback_lock);
    while (registration->callbacks_in_flight != 0) {
        SleepConditionVariableSRW(&registration->callback_idle,
            &registration->callback_lock, INFINITE, 0);
    }
    ReleaseSRWLockExclusive(&registration->callback_lock);
}

void release_operation(DnsOperationContext* operation) noexcept {
    if (operation && operation->references.fetch_sub(
            1, std::memory_order_acq_rel) == 1) {
        delete operation;
    }
}

void retain_operation(DnsOperationContext* operation) noexcept {
    operation->references.fetch_add(1, std::memory_order_relaxed);
}

void complete_cancelled_register(DnsOperationContext* operation) noexcept;

void start_register_cancel_if_requested(
    DnsOperationContext* operation) noexcept {
    if (!operation ||
        !operation->cancel_requested.load(std::memory_order_acquire)) return;
    if (operation->register_pending.load(std::memory_order_acquire) &&
        !operation->cancel_started.exchange(true, std::memory_order_acq_rel)) {
        if (DnsServiceRegisterCancel(&operation->cancel) == ERROR_SUCCESS)
            complete_cancelled_register(operation);
    }
}

void request_register_cancel(DnsOperationContext* operation) noexcept {
    if (!operation) return;
    operation->cancel_requested.store(true, std::memory_order_release);
    start_register_cancel_if_requested(operation);
}

void pin_dns_sd_module() noexcept {
    HMODULE module{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&dns_sd_module_anchor), &module);
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

std::wstring lower_adapter_name(const IP_ADAPTER_ADDRESSES* adapter) {
    if (!adapter || !adapter->FriendlyName) return {};
    std::wstring result(adapter->FriendlyName);
    std::ranges::transform(result, result.begin(), [](wchar_t character) {
        return static_cast<wchar_t>(std::towlower(character));
    });
    return result;
}

bool looks_like_virtual_adapter(const IP_ADAPTER_ADDRESSES* adapter) {
    const auto name = lower_adapter_name(adapter);
    constexpr std::array<std::wstring_view, 13> virtual_markers{
        L"virtual", L"veth", L"vmware", L"hyper-v", L"hyperv",
        L"virtualbox", L"tap", L"vpn", L"loopback", L"tunnel",
        L"wsl", L"docker", L"tailscale",
    };
    return std::ranges::any_of(virtual_markers,
        [&name](const auto marker) { return name.find(marker) != std::wstring::npos; });
}

std::uint32_t adapter_interface_index(const IP_ADAPTER_ADDRESSES* adapter) noexcept {
    if (!adapter) return 0;
    return adapter->IfIndex != 0 ? adapter->IfIndex : adapter->Ipv6IfIndex;
}

struct AdapterPreference {
    std::uint32_t index{};
    std::uint32_t metric{std::numeric_limits<std::uint32_t>::max()};
    int type_rank{};
    bool requested{};
    bool non_virtual{};
    bool physical{};
    bool gateway{};
};

std::optional<AdapterPreference> adapter_preference(
    const IP_ADAPTER_ADDRESSES* adapter, std::uint32_t requested) {
    if (!adapter || adapter->OperStatus != IfOperStatusUp ||
        !adapter->FirstUnicastAddress ||
        (adapter->Flags & IP_ADAPTER_NO_MULTICAST) != 0) return std::nullopt;

    const auto index = adapter_interface_index(adapter);
    if (index == 0 || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK ||
        adapter->IfType == IF_TYPE_TUNNEL) {
        return std::nullopt;
    }

    int type_rank{};
    bool physical{};
    switch (adapter->IfType) {
    case IF_TYPE_IEEE80211:
    case IF_TYPE_ETHERNET_CSMACD:
        type_rank = 3;
        physical = true;
        break;
    case IF_TYPE_WWANPP:
    case IF_TYPE_WWANPP2:
        type_rank = 2;
        physical = true;
        break;
    default:
        type_rank = 1;
        break;
    }
    const auto virtual_adapter = looks_like_virtual_adapter(adapter);
    const auto metric = adapter->IfIndex != 0
        ? adapter->Ipv4Metric : adapter->Ipv6Metric;
    return AdapterPreference{
        .index = index,
        .metric = metric,
        .type_rank = type_rank,
        .requested = index == requested && !virtual_adapter,
        .non_virtual = !virtual_adapter,
        .physical = physical,
        .gateway = adapter->FirstGatewayAddress != nullptr,
    };
}

bool better_adapter(const AdapterPreference& candidate,
    const AdapterPreference& current) noexcept {
    if (candidate.requested != current.requested)
        return candidate.requested;
    if (candidate.non_virtual != current.non_virtual)
        return candidate.non_virtual;
    if (candidate.physical != current.physical)
        return candidate.physical;
    if (candidate.gateway != current.gateway)
        return candidate.gateway;
    if (candidate.metric != current.metric)
        return candidate.metric < current.metric;
    if (candidate.type_rank != current.type_rank)
        return candidate.type_rank > current.type_rank;
    return candidate.index < current.index;
}

std::optional<std::uint32_t> preferred_dns_sd_interface_impl(
    std::uint32_t requested) {
    ULONG buffer_size = 16 * 1024;
    std::vector<std::uint8_t> buffer(buffer_size);
    auto* adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
    constexpr ULONG flags = GAA_FLAG_INCLUDE_PREFIX | GAA_FLAG_INCLUDE_GATEWAYS;
    auto status = GetAdaptersAddresses(AF_UNSPEC, flags,
        nullptr, adapters, &buffer_size);
    if (status == ERROR_BUFFER_OVERFLOW) {
        buffer.resize(buffer_size);
        adapters = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data());
        status = GetAdaptersAddresses(AF_UNSPEC, flags,
            nullptr, adapters, &buffer_size);
    }

    if (status != NO_ERROR) {
        // An explicit index came from the upstream library and remains a
        // useful fallback if adapter enumeration is temporarily unavailable.
        if (requested != 0) return requested;
        return std::nullopt;
    }

    std::optional<AdapterPreference> best;
    for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
        const auto candidate = adapter_preference(adapter, requested);
        if (candidate && (!best || better_adapter(*candidate, *best)))
            best = candidate;
    }

    if (best) {
        return iPhoneMirror::wireless::dns_sd_registration_interface(
            requested, best->index);
    }
    if (requested != 0) return requested;
    // Never intentionally pass DNS-SD's zero/all-interfaces sentinel when no
    // connected adapter can be selected. The caller keeps an opaque logical
    // registration and can retry when the upstream enumerates an adapter.
    return std::nullopt;
}

std::optional<std::uint32_t> preferred_dns_sd_interface(
    std::uint32_t requested) noexcept {
    try {
        return preferred_dns_sd_interface_impl(requested);
    } catch (...) {
        // This helper is called from the DNSServiceRegister C ABI. Adapter
        // enumeration is advisory, so an allocation failure must degrade to
        // the upstream concrete interface instead of escaping across the ABI.
        if (requested != 0) return requested;
        return std::nullopt;
    }
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

struct CallbackInvocation {
    Registration* registration{};
    DNSServiceRegisterReply callback{};
    void* context{};
};

CallbackInvocation acquire_callback_locked(
    const LogicalRegistration& logical) noexcept {
    const auto owner_id = logical.leases.owner();
    const auto owner = owner_id != 0
        ? reinterpret_cast<Registration*>(owner_id) : nullptr;
    if (!owner) return {};

    AcquireSRWLockExclusive(&owner->callback_lock);
    if (owner->released.load(std::memory_order_acquire) || !owner->callback) {
        ReleaseSRWLockExclusive(&owner->callback_lock);
        return {};
    }
    retain_registration(owner);
    ++owner->callbacks_in_flight;
    const CallbackInvocation invocation{
        owner, owner->callback, owner->callback_context};
    ReleaseSRWLockExclusive(&owner->callback_lock);
    return invocation;
}

void invoke_callback(const CallbackInvocation& invocation,
    const LogicalRegistration& logical, DWORD status) noexcept {
    if (!invocation.registration) return;
    CallbackFrame frame{invocation.registration, current_callback_frame};
    current_callback_frame = &frame;
    try {
        invocation.callback(invocation.registration, 0,
            status == ERROR_SUCCESS ? 0 : -65537,
            logical.name.c_str(), logical.regtype.c_str(), "local.",
            invocation.context);
    } catch (...) {
        // Never allow an upstream C callback implemented in C++ to unwind
        // through the Windows DNS callback trampoline.
    }
    current_callback_frame = frame.previous;

    AcquireSRWLockExclusive(&invocation.registration->callback_lock);
    if (invocation.registration->callbacks_in_flight != 0)
        --invocation.registration->callbacks_in_flight;
    if (invocation.registration->callbacks_in_flight == 0)
        WakeAllConditionVariable(&invocation.registration->callback_idle);
    ReleaseSRWLockExclusive(&invocation.registration->callback_lock);
    release_registration(invocation.registration);
}

void WINAPI dns_operation_complete(DWORD status, void* context,
    PDNS_SERVICE_INSTANCE callback_instance) noexcept;

void abandon_operation(DnsOperationContext* operation) noexcept {
    release_operation(operation);
    release_operation(operation);
}

void close_logical_registration_locked(
    const std::shared_ptr<LogicalRegistration>& logical) noexcept {
    logical->state = NativeRegistrationState::Closed;
    logical->active_interface = 0;
    const auto found = active_services.find(logical->service_identity);
    if (found != active_services.end() && found->second.get() == logical.get())
        active_services.erase(found);
}

void try_begin_register(const std::shared_ptr<LogicalRegistration>& logical,
    std::uint32_t interface_index) noexcept;

void complete_cancelled_register(DnsOperationContext* operation) noexcept {
    if (!operation || operation->kind != DnsOperationKind::Register ||
        !operation->completion.try_claim_cancel()) return;

    const auto logical = operation->logical;
    std::uint32_t retry_interface{};
    {
        ActiveServicesGuard lock;
        if (logical->registering_operation == operation) {
            logical->registering_operation = nullptr;
            logical->active_interface = 0;
            if (logical->leases.size() == 0) {
                close_logical_registration_locked(logical);
            } else {
                logical->state = NativeRegistrationState::Prepared;
                retry_interface = logical->leases.owner_interface();
            }
        }
    }

    if (retry_interface != 0)
        try_begin_register(logical, retry_interface);
    // The callback-side reference is consumed by this explicit cancellation
    // completion when Windows returns success without delivering a callback.
    release_operation(operation);
}

void retry_prepared_registrations() noexcept {
    std::vector<std::shared_ptr<LogicalRegistration>> pending;
    try {
        {
            ActiveServicesGuard lock;
            for (const auto& [_, logical] : active_services) {
                if (logical->state == NativeRegistrationState::Prepared &&
                    logical->leases.size() != 0 &&
                    logical->leases.owner_interface() == 0)
                    pending.push_back(logical);
            }
        }
        for (const auto& logical : pending) {
            const auto selected = preferred_dns_sd_interface(
                logical->requested_interface);
            if (!selected) continue;
            std::uint32_t desired{};
            {
                ActiveServicesGuard lock;
                if (logical->state != NativeRegistrationState::Prepared ||
                    logical->leases.size() == 0) continue;
                logical->leases.assign_missing_interfaces(*selected);
                desired = logical->leases.owner_interface();
            }
            if (desired != 0) try_begin_register(logical, desired);
        }
    } catch (...) {
    }
}

bool has_pending_interface_registration() noexcept {
    ActiveServicesGuard lock;
    return std::ranges::any_of(active_services, [](const auto& entry) {
        const auto& logical = entry.second;
        return logical->state == NativeRegistrationState::Prepared &&
            logical->leases.size() != 0 &&
            logical->leases.owner_interface() == 0;
    });
}

void ensure_interface_retry_worker() noexcept {
    retry_prepared_registrations();
    if (!has_pending_interface_registration()) return;
    bool expected{};
    if (!interface_retry_worker_running.compare_exchange_strong(expected, true,
            std::memory_order_acq_rel, std::memory_order_acquire)) return;
    pin_dns_sd_module();
    try {
        std::thread([] {
            for (;;) {
                retry_prepared_registrations();
                if (!has_pending_interface_registration()) {
                    interface_retry_worker_running.store(false,
                        std::memory_order_release);
                    // Close the race with a registration that became pending
                    // between the last scan and publishing the idle state.
                    if (has_pending_interface_registration())
                        ensure_interface_retry_worker();
                    return;
                }
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
        }).detach();
    } catch (...) {
        interface_retry_worker_running.store(false, std::memory_order_release);
    }
}

void try_begin_register(const std::shared_ptr<LogicalRegistration>& logical,
    std::uint32_t interface_index) noexcept {
    if (!logical || interface_index == 0) return;
    auto* operation = new (std::nothrow) DnsOperationContext(
        logical, DnsOperationKind::Register);
    if (!operation) return;

    {
        ActiveServicesGuard lock;
        if (logical->state != NativeRegistrationState::Prepared ||
            logical->leases.size() == 0) {
            abandon_operation(operation);
            return;
        }
        if (logical->registration_callback_active) {
            logical->register_after_callback = true;
            abandon_operation(operation);
            return;
        }
        logical->request.Version = 1;
        logical->request.InterfaceIndex = interface_index;
        logical->request.pServiceInstance = logical->instance;
        logical->request.pRegisterCompletionCallback = dns_operation_complete;
        logical->request.pQueryContext = operation;
        logical->request.unicastEnabled = FALSE;
        logical->state = NativeRegistrationState::Registering;
        logical->registering_operation = operation;
        operation->request = logical->request;
    }

    pin_dns_sd_module();
    const auto status = DnsServiceRegister(&operation->request,
        &operation->cancel);
    // DNS_REQUEST_PENDING is the documented asynchronous success result. A
    // callback that already started owns its completion reference even when a
    // compatibility implementation returns a synchronous status instead.
    const auto callback_started = operation->completion.callback_started();
    if (status == DNS_REQUEST_PENDING || callback_started) {
        if (status == DNS_REQUEST_PENDING) {
            operation->register_pending.store(true, std::memory_order_release);
            start_register_cancel_if_requested(operation);
        }
        release_operation(operation);
        return;
    }

    {
        ActiveServicesGuard lock;
        if (logical->registering_operation == operation) {
            logical->registering_operation = nullptr;
            if (logical->leases.size() == 0)
                close_logical_registration_locked(logical);
            else if (logical->state == NativeRegistrationState::Registering)
                logical->state = NativeRegistrationState::Prepared;
        }
    }
    abandon_operation(operation);
}

void try_begin_deregister(
    const std::shared_ptr<LogicalRegistration>& logical,
    std::uint32_t replacement_interface = 0) noexcept {
    if (!logical) return;
    auto* operation = new (std::nothrow) DnsOperationContext(
        logical, DnsOperationKind::Deregister);
    if (!operation) return;

    {
        ActiveServicesGuard lock;
        if (logical->state != NativeRegistrationState::Registered ||
            logical->registration_callback_active ||
            (logical->leases.size() != 0 &&
                (replacement_interface == 0 ||
                    replacement_interface == logical->active_interface))) {
            abandon_operation(operation);
            return;
        }
        logical->state = NativeRegistrationState::Deregistering;
        operation->request = logical->request;
        operation->request.pRegisterCompletionCallback = dns_operation_complete;
        operation->request.pQueryContext = operation;
    }

    pin_dns_sd_module();
    const auto status = DnsServiceDeRegister(&operation->request, nullptr);
    // DNS_REQUEST_PENDING is the documented asynchronous success result. The
    // callback_started handshake also supports a compatibility implementation
    // that invokes its completion callback synchronously before returning.
    if (status == DNS_REQUEST_PENDING ||
        operation->completion.callback_started()) {
        release_operation(operation);
        return;
    }

    {
        ActiveServicesGuard lock;
        if (logical->state == NativeRegistrationState::Deregistering)
            logical->state = NativeRegistrationState::Registered;
    }
    abandon_operation(operation);
}

void WINAPI dns_operation_complete(DWORD status, void* context,
    PDNS_SERVICE_INSTANCE callback_instance) noexcept {
    const auto operation = static_cast<DnsOperationContext*>(context);
    if (!operation) {
        if (callback_instance) DnsServiceFreeInstance(callback_instance);
        return;
    }

    if (!operation->completion.try_claim_callback()) {
        if (callback_instance) DnsServiceFreeInstance(callback_instance);
        return;
    }
    const auto& logical = operation->logical;
    try {
        if (operation->kind == DnsOperationKind::Register) {
            CallbackInvocation invocation;
            {
                ActiveServicesGuard lock;
                if (logical->registering_operation == operation)
                    logical->registering_operation = nullptr;
                if (logical->state == NativeRegistrationState::Registering) {
                    if (status == ERROR_SUCCESS) {
                        logical->state = NativeRegistrationState::Registered;
                        logical->active_interface =
                            operation->request.InterfaceIndex;
                    }
                    else if (logical->leases.size() == 0) {
                        close_logical_registration_locked(logical);
                    }
                    else {
                        logical->state = NativeRegistrationState::Prepared;
                        logical->active_interface = 0;
                    }
                }
                invocation = acquire_callback_locked(*logical);
                logical->registration_callback_active =
                    invocation.registration != nullptr;
            }

            invoke_callback(invocation, *logical, status);

            bool register_again{};
            bool deregister{};
            std::uint32_t desired_interface{};
            {
                ActiveServicesGuard lock;
                if (invocation.registration)
                    logical->registration_callback_active = false;

                if (logical->state == NativeRegistrationState::Registered) {
                    logical->register_after_callback = false;
                    desired_interface = logical->leases.owner_interface();
                    deregister = logical->leases.size() == 0 ||
                        (desired_interface != 0 &&
                            desired_interface != logical->active_interface);
                }
                else if (logical->state == NativeRegistrationState::Prepared) {
                    const auto retry = logical->register_after_callback ||
                        status == ERROR_CANCELLED;
                    logical->register_after_callback = false;
                    if (retry && logical->leases.size() != 0) {
                        desired_interface = logical->leases.owner_interface();
                        register_again = desired_interface != 0;
                    }
                }
                else {
                    logical->register_after_callback = false;
                }
            }

            if (deregister)
                try_begin_deregister(logical, desired_interface);
            else if (register_again)
                try_begin_register(logical, desired_interface);
        }
        else {
            std::uint32_t desired_interface{};
            {
                ActiveServicesGuard lock;
                if (status != ERROR_SUCCESS) {
                    logical->state = NativeRegistrationState::Registered;
                }
                else {
                    logical->active_interface = 0;
                    if (logical->leases.size() == 0) {
                        close_logical_registration_locked(logical);
                    }
                    else {
                        logical->state = NativeRegistrationState::Prepared;
                        desired_interface = logical->leases.owner_interface();
                    }
                }
            }
            if (desired_interface != 0)
                try_begin_register(logical, desired_interface);
        }
    } catch (...) {
        // The operation context still owns the logical registration. Swallow
        // any unexpected C++ failure so neither Windows nor the C ABI observes
        // an exception and release the operation normally below.
    }

    if (callback_instance && callback_instance != logical->instance)
        DnsServiceFreeInstance(callback_instance);
    release_operation(operation);
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
    try {
        auto existing = std::find_if(state->entries.begin(), state->entries.end(),
            [key](const auto& entry) { return entry.first == key; });
        std::vector<std::uint8_t> bytes(value_size);
        if (value_size != 0) std::memcpy(bytes.data(), value, value_size);
        if (existing == state->entries.end())
            state->entries.emplace_back(key, std::move(bytes));
        else
            existing->second = std::move(bytes);
        return rebuild_txt(*state) ? 0 : -65540;
    } catch (...) {
        return -65539;
    }
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

static DNSServiceErrorType dns_service_register_impl(
    DNSServiceRef* output, DNSServiceFlags, std::uint32_t interface_index,
    const char* name, const char* regtype, const char*, const char*,
    std::uint16_t network_port, std::uint16_t txt_length, const void* txt_record,
    DNSServiceRegisterReply callback, void* callback_context) {
    auto registration = std::make_unique<Registration>();
    registration->callback = callback;
    registration->callback_context = callback_context;
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
    const auto selected_interface = preferred_dns_sd_interface(interface_index);
    registration->interface_index = selected_interface.value_or(0);

    std::shared_ptr<LogicalRegistration> existing;
    NativeRegistrationState existing_state{};
    std::uint32_t existing_active_interface{};
    std::uint32_t existing_desired_interface{};
    {
        ActiveServicesGuard lock;
        const auto found = active_services.find(service_name);
        if (found != active_services.end()) {
            existing = found->second;
            if (registration->interface_index == 0)
                registration->interface_index = existing->leases.owner_interface();
            if (registration->interface_index == 0)
                registration->interface_index = existing->request.InterfaceIndex;
            existing->leases.acquire(registration_id(registration.get()),
                registration->interface_index);
            if (registration->interface_index != 0)
                existing->leases.assign_missing_interfaces(
                    registration->interface_index);
            registration->logical = existing;
            existing_state = existing->state;
            existing_active_interface = existing->active_interface;
            existing_desired_interface = existing->leases.owner_interface();
        }
    }

    if (existing) {
        *output = registration.release();
        if (existing_state == NativeRegistrationState::Prepared) {
            if (existing_desired_interface != 0)
                try_begin_register(existing, existing_desired_interface);
            else
                ensure_interface_retry_worker();
        }
        else if (existing_state == NativeRegistrationState::Registered &&
            existing_desired_interface != 0 &&
            existing_desired_interface != existing_active_interface) {
            try_begin_deregister(existing, existing_desired_interface);
        }
        return 0;
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

    auto logical = std::make_shared<LogicalRegistration>();
    logical->name = name;
    logical->regtype = regtype;
    logical->service_identity = service_name;
    logical->requested_interface = interface_index;
    logical->request.InterfaceIndex = registration->interface_index;
    logical->instance = DnsServiceConstructInstance(service_name.c_str(),
        host.c_str(), nullptr, nullptr, ntohs(network_port), 0, 0,
        static_cast<DWORD>(properties.size()), keys.data(), values.data());
    // Registration is best-effort. The AirPlay HTTP/RTP servers are useful
    // even when a particular Windows network profile rejects an advertisement.
    // Keep a live opaque ref so the upstream library does not tear down those
    // servers just because DNS-SD is unavailable on one adapter.
    if (!logical->instance) {
        *output = registration.release();
        return 0;
    }

    existing.reset();
    existing_state = NativeRegistrationState::Prepared;
    existing_active_interface = 0;
    existing_desired_interface = 0;
    {
        ActiveServicesGuard lock;
        const auto found = active_services.find(service_name);
        if (found != active_services.end()) {
            existing = found->second;
            existing->leases.acquire(registration_id(registration.get()),
                registration->interface_index);
            if (registration->interface_index != 0)
                existing->leases.assign_missing_interfaces(
                    registration->interface_index);
            registration->logical = existing;
            existing_state = existing->state;
            existing_active_interface = existing->active_interface;
            existing_desired_interface = existing->leases.owner_interface();
        }
        else {
            logical->leases.acquire(registration_id(registration.get()),
                registration->interface_index);
            registration->logical = logical;
            active_services.emplace(service_name, logical);
        }
    }

    *output = registration.release();
    if (existing) {
        if (existing_state == NativeRegistrationState::Prepared) {
            if (existing_desired_interface != 0)
                try_begin_register(existing, existing_desired_interface);
            else
                ensure_interface_retry_worker();
        }
        else if (existing_state == NativeRegistrationState::Registered &&
            existing_desired_interface != 0 &&
            existing_desired_interface != existing_active_interface) {
            try_begin_deregister(existing, existing_desired_interface);
        }
        return 0;
    }

    if (selected_interface)
        try_begin_register(logical, *selected_interface);
    else
        ensure_interface_retry_worker();
    return 0;
}

extern "C" __declspec(dllexport) DNSServiceErrorType __stdcall DNSServiceRegister(
    DNSServiceRef* output, DNSServiceFlags flags, std::uint32_t interface_index,
    const char* name, const char* regtype, const char* domain, const char* host,
    std::uint16_t network_port, std::uint16_t txt_length, const void* txt_record,
    DNSServiceRegisterReply callback, void* callback_context) {
    if (!output || !name || !*name || !regtype || !*regtype) return -65540;
    *output = nullptr;
    try {
        return dns_service_register_impl(output, flags, interface_index, name,
            regtype, domain, host, network_port, txt_length, txt_record,
            callback, callback_context);
    } catch (...) {
        // All throw-capable preparation happens before a ref/map is published.
        // Return the Bonjour-compatible no-memory error without crossing C ABI.
        *output = nullptr;
        return -65539;
    }
}

extern "C" __declspec(dllexport) void __stdcall DNSServiceRefDeallocate(
    DNSServiceRef registration) {
    if (!registration) return;
    if (registration->released.exchange(true, std::memory_order_acq_rel)) return;

    try {
        auto logical = std::move(registration->logical);
        if (!logical) {
            release_registration(registration);
            return;
        }

        bool deregister{};
        DnsOperationContext* cancel_operation{};
        std::uint32_t replacement_interface{};
        {
            ActiveServicesGuard lock;
            const auto released = logical->leases.release(
                registration_id(registration));
            if (released.known) {
                if (released.last) {
                    if (logical->state == NativeRegistrationState::Prepared) {
                        close_logical_registration_locked(logical);
                    }
                    else if (logical->state ==
                        NativeRegistrationState::Registering) {
                        cancel_operation = logical->registering_operation;
                        if (cancel_operation)
                            retain_operation(cancel_operation);
                    }
                    else if (logical->state == NativeRegistrationState::Registered) {
                        deregister = true;
                    }
                }
                else if (released.owner_released &&
                    released.new_owner_interface != 0 &&
                    released.new_owner_interface != logical->active_interface &&
                    logical->state == NativeRegistrationState::Registered) {
                    deregister = true;
                    replacement_interface = released.new_owner_interface;
                }
            }
        }

        // Once released is set and its lease is removed, no new callback can
        // be selected. A different thread waits for any already-selected
        // callback so its callback_context remains valid through this return;
        // a callback deallocating itself skips the wait and drops its last ref
        // at the callback tail instead.
        wait_for_registration_callbacks(registration);
        if (cancel_operation) {
            request_register_cancel(cancel_operation);
            release_operation(cancel_operation);
        }
        if (deregister)
            try_begin_deregister(logical, replacement_interface);
        release_registration(registration);
    } catch (...) {
        // Every normal path above is non-throwing. If a platform primitive ever
        // violates that assumption, retain the ref rather than freeing an
        // object that may still be named by the logical lease table.
    }
}
