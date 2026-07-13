// DNS-SD compatibility layer for AirPlayServer using the Windows 10+ DNS API.

#include <Windows.h>
#include <WinDNS.h>
#include <WinSock2.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

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
    HANDLE completion{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
};

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

    auto service_name = widen(name) + L"." + widen(regtype) + L".local";
    const auto host = host_name();
    auto properties = parse_txt(txt_length, txt_record);
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
        *output = registration.release();
        return 0;
    }
    registration->request.Version = 1;
    registration->request.InterfaceIndex = interface_index;
    registration->request.pServiceInstance = registration->instance;
    registration->request.pRegisterCompletionCallback = registration_complete;
    registration->request.pQueryContext = registration.get();
    registration->request.unicastEnabled = FALSE;
    const auto status = DnsServiceRegister(&registration->request,
        &registration->cancel);
    if (status != ERROR_SUCCESS) {
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
    ResetEvent(registration->completion);
    auto deregister = registration->request;
    deregister.pRegisterCompletionCallback = registration_complete;
    const auto status = registration->instance
        ? DnsServiceDeRegister(&deregister, nullptr) : ERROR_INVALID_PARAMETER;
    if (status == ERROR_SUCCESS)
        WaitForSingleObject(registration->completion, 2000);
    DnsServiceRegisterCancel(&registration->cancel);
    DnsServiceFreeInstance(registration->instance);
    CloseHandle(registration->completion);
    delete registration;
}
