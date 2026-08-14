#include "Transport/LibUsb0Transport.h"
#include "Transport/AppleUsbIdentityCache.h"
#include "Logging.h"

#include <Windows.h>
#include <lusb0_usb.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <climits>
#include <chrono>
#include <filesystem>
#include <format>
#include <mutex>
#include <shared_mutex>
#include <stdexcept>
#include <thread>
#include <utility>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint16_t AppleVendorId = 0x05ac;
constexpr std::uint8_t QuickTimeSubclass = 0x2a;
constexpr std::uint8_t QuickTimePlaceholderSubclass = 0xfd;
constexpr int LibUsb0TransferTimedOut = -116;
// libusb-win32 returns this value when the handle has no active configuration
// (its internal pipe table is still at configuration 0).  It is distinct from
// a genuinely busy interface and is the only claim failure that can be
// repaired with one descriptor-verified SET_CONFIGURATION.
constexpr int LibUsb0InvalidConfiguration = -22;
constexpr auto ActivatedDescriptorTimeout = std::chrono::seconds(5);
constexpr auto ActivatedDescriptorProbeInterval = std::chrono::milliseconds(100);
constexpr std::size_t MaximumConfigurationDescriptorBytes = 64U * 1024U;
// libusb-win32 keeps a process-global bus/device list. Configuration, discovery
// and close operations take exclusive access so they cannot overlap any in-
// flight transfer. Bulk I/O uses shared access because independent, already-
// opened devices must keep draining concurrently to avoid media starvation.
std::shared_mutex api_mutex;
std::once_flag usb_init_once;

void ensure_usb_initialized() {
    std::call_once(usb_init_once, [] { usb_init(); });
}

std::wstring widen_ascii(std::string_view value) {
    std::wstring result;
    result.reserve(value.size());
    for (std::size_t index{}; index < value.size(); ++index) {
        const auto ch = value[index];
        if (ch == '\0') {
            if (!std::all_of(value.begin() + static_cast<std::ptrdiff_t>(index),
                    value.end(), [](char tail) { return tail == '\0'; }))
                return {};
            break;
        }
        const auto byte = static_cast<unsigned char>(ch);
        if (byte > 0x7f) return {};
        result.push_back(static_cast<wchar_t>(byte));
    }
    return result;
}

std::wstring quote_process_argument(std::wstring_view argument) {
    std::wstring result(1, L'"');
    std::size_t backslashes{};
    for (const auto ch : argument) {
        if (ch == L'\\') {
            ++backslashes;
            continue;
        }
        if (ch == L'"') {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(L'"');
        } else {
            result.append(backslashes, L'\\');
            result.push_back(ch);
        }
        backslashes = 0;
    }
    result.append(backslashes * 2, L'\\');
    result.push_back(L'"');
    return result;
}

DWORD run_configuration_helper(const AppleUsbIdentity& identity,
    std::wstring_view operation) {
    if (identity.serial.empty() ||
        identity.expected_quicktime_configuration == 0)
        throw std::runtime_error(
            "USB configuration switch requires an exact serial and expected QuickTime configuration");

    std::wstring module_path(32768, L'\0');
    const auto module_length = GetModuleFileNameW(nullptr, module_path.data(),
        static_cast<DWORD>(module_path.size()));
    if (module_length == 0 || module_length >= module_path.size())
        throw std::runtime_error(std::format(
            "locate USB configuration helper: win32_error={}", GetLastError()));
    module_path.resize(module_length);
    const auto helper_path = std::filesystem::path(module_path).parent_path() /
        L"iPhoneMirror.UsbConfigurationSwitch.exe";
    if (!std::filesystem::is_regular_file(helper_path))
        throw std::runtime_error("USB configuration helper is missing");
    const auto serial = widen_ascii(identity.serial);
    if (serial.empty())
        throw std::runtime_error("USB configuration helper rejected a non-ASCII serial");

    auto command = quote_process_argument(helper_path.wstring()) + L" " +
        std::wstring(operation) + L" " +
        quote_process_argument(serial) + L" " +
        std::to_wstring(identity.expected_quicktime_configuration) + L" " +
        quote_process_argument(widen_ascii(identity.topology_id));
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(helper_path.c_str(), command.data(), nullptr, nullptr,
            FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr,
            helper_path.parent_path().c_str(), &startup, &process)) {
        throw std::runtime_error(std::format(
            "start USB configuration helper: win32_error={}", GetLastError()));
    }

    const auto job = CreateJobObjectW(nullptr, nullptr);
    if (!job) {
        TerminateProcess(process.hProcess, 1);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        throw std::runtime_error(std::format(
            "create USB configuration helper job: win32_error={}", GetLastError()));
    }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
            &limits, sizeof(limits)) ||
        !AssignProcessToJobObject(job, process.hProcess)) {
        const auto error = GetLastError();
        TerminateProcess(process.hProcess, 1);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        CloseHandle(job);
        throw std::runtime_error(std::format(
            "secure USB configuration helper process: win32_error={}", error));
    }
    ResumeThread(process.hThread);
    CloseHandle(process.hThread);

    // The helper owns the invalidated libusb0 handle until the exact Apple
    // interface completes removal/arrival and remains stable. Its internal
    // transition is bounded at 15 seconds; leave a small process-exit margin.
    const auto wait_result = WaitForSingleObject(process.hProcess, 18000);
    if (wait_result == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 124);
        WaitForSingleObject(process.hProcess, 1000);
        CloseHandle(process.hProcess);
        CloseHandle(job);
        throw std::runtime_error("USB configuration helper timed out");
    }
    if (wait_result != WAIT_OBJECT_0) {
        const auto error = GetLastError();
        TerminateProcess(process.hProcess, 125);
        WaitForSingleObject(process.hProcess, 1000);
        CloseHandle(process.hProcess);
        CloseHandle(job);
        throw std::runtime_error(std::format(
            "wait for USB configuration helper: win32_error={}", error));
    }
    DWORD exit_code{};
    const bool exit_read = GetExitCodeProcess(process.hProcess, &exit_code) != FALSE;
    CloseHandle(process.hProcess);
    CloseHandle(job);
    if (!exit_read)
        throw std::runtime_error(std::format(
            "read USB configuration helper result: win32_error={}", GetLastError()));
    const auto operation_name = operation == L"activate" ? "activate" :
        operation == L"restore" ? "restore" : "verify";
    logging::write(std::format(
        "usb_configuration_helper operation={} exit_code={}",
        operation_name, exit_code));
    return exit_code;
}

bool run_configuration_switch_helper(const AppleUsbIdentity& identity,
    bool activate) {
    const auto operation = activate ? std::wstring_view(L"activate")
                                    : std::wstring_view(L"restore");
    const auto exit_code = run_configuration_helper(identity, operation);
    if (exit_code == 0 || exit_code == 20) return true;
    if (exit_code == 10) return false;
    if (exit_code == 32)
        throw std::runtime_error(std::format(
            "USB configuration helper observed {} removal but the exact interface did not return before timeout",
            activate ? "QuickTime activation" : "normal-configuration restore"));
    if (exit_code == 33)
        throw std::runtime_error(std::format(
            "USB configuration helper sent the {} request but observed no exact interface transition",
            activate ? "QuickTime activation" : "normal-configuration restore"));
    throw std::runtime_error(std::format(
        "USB configuration helper failed: operation={} exit_code={}",
        activate ? "activate" : "restore", exit_code));
}

std::string normalize(std::string_view source) {
    std::string value(source);
    const auto embedded_null = value.find('\0');
    if (embedded_null != std::string::npos) {
        if (!std::all_of(value.begin() + static_cast<std::ptrdiff_t>(embedded_null),
                value.end(), [](char ch) { return ch == '\0'; })) {
            return {};
        }
        value.resize(embedded_null);
    }
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
        value.pop_back();
    std::size_t leading{};
    while (leading < value.size() &&
        std::isspace(static_cast<unsigned char>(value[leading]))) ++leading;
    if (leading != 0) value.erase(0, leading);
    if (value.size() == 24 && value.find('-') == std::string::npos && value.find('&') == std::string::npos) {
        value.insert(8, "-");
    }
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

std::vector<UsbEndpointSet> endpoint_candidates_for(
    const struct usb_device& device, std::uint8_t subclass) {
    std::vector<UsbEndpointSet> candidates;
    for (int c = 0; c < device.descriptor.bNumConfigurations; ++c) {
        const auto& config = device.config[c];
        for (int i = 0; i < config.bNumInterfaces; ++i) {
            const auto& group = config.interface[i];
            for (int a = 0; a < group.num_altsetting; ++a) {
                const auto& interface_descriptor = group.altsetting[a];
                if (interface_descriptor.bInterfaceClass != 0xff ||
                    interface_descriptor.bInterfaceSubClass != subclass) continue;
                UsbEndpointSet endpoints;
                endpoints.configuration = config.bConfigurationValue;
                endpoints.interface_number = interface_descriptor.bInterfaceNumber;
                endpoints.alternate_setting = interface_descriptor.bAlternateSetting;
                for (int e = 0; e < interface_descriptor.bNumEndpoints; ++e) {
                    const auto& endpoint = interface_descriptor.endpoint[e];
                    if ((endpoint.bmAttributes & 3U) != 2U) continue;
                    if ((endpoint.bEndpointAddress & 0x80U) != 0) {
                        if (endpoint.wMaxPacketSize >= endpoints.bulk_in_packet_size) {
                            endpoints.bulk_in = endpoint.bEndpointAddress;
                            endpoints.bulk_in_packet_size = endpoint.wMaxPacketSize;
                        }
                    } else if (endpoint.wMaxPacketSize >= endpoints.bulk_out_packet_size) {
                        endpoints.bulk_out = endpoint.bEndpointAddress;
                        endpoints.bulk_out_packet_size = endpoint.wMaxPacketSize;
                    }
                }
                if (endpoints.bulk_in && endpoints.bulk_out)
                    candidates.push_back(endpoints);
            }
        }
    }
    return candidates;
}

UsbEndpointSet endpoints_for(const struct usb_device& device, std::uint8_t subclass) {
    const auto candidates = endpoint_candidates_for(device, subclass);
    return select_best_quicktime_endpoints(candidates);
}

struct HandleConfigurationObservation {
    UsbEndpointSet endpoints;
    std::uint8_t configuration_count{};
    std::uint8_t active_configuration{};
    bool active_configuration_known{};
    int last_descriptor_result{};
};

HandleConfigurationObservation observe_handle_configuration(
    usb_dev_handle* handle, std::uint8_t expected_configuration) {
    HandleConfigurationObservation observation;
    usb_device_descriptor device_descriptor{};
    observation.last_descriptor_result = usb_get_descriptor(handle,
        USB_DT_DEVICE, 0, &device_descriptor, sizeof(device_descriptor));
    if (observation.last_descriptor_result <
            static_cast<int>(sizeof(device_descriptor)))
        return observation;
    observation.configuration_count = device_descriptor.bNumConfigurations;

    for (std::uint8_t index{}; index < device_descriptor.bNumConfigurations;
         ++index) {
        std::array<std::uint8_t, USB_DT_CONFIG_SIZE> header{};
        observation.last_descriptor_result = usb_get_descriptor(handle,
            USB_DT_CONFIG, index, header.data(), static_cast<int>(header.size()));
        if (observation.last_descriptor_result < USB_DT_CONFIG_SIZE ||
            header[0] < USB_DT_CONFIG_SIZE || header[1] != USB_DT_CONFIG)
            continue;
        const auto total_length = static_cast<std::size_t>(header[2]) |
            (static_cast<std::size_t>(header[3]) << 8U);
        if (total_length < USB_DT_CONFIG_SIZE ||
            total_length > MaximumConfigurationDescriptorBytes)
            continue;
        std::vector<std::uint8_t> descriptor(total_length);
        observation.last_descriptor_result = usb_get_descriptor(handle,
            USB_DT_CONFIG, index, descriptor.data(),
            static_cast<int>(descriptor.size()));
        if (observation.last_descriptor_result < USB_DT_CONFIG_SIZE) continue;
        descriptor.resize((std::min)(descriptor.size(),
            static_cast<std::size_t>(observation.last_descriptor_result)));
        const auto endpoints = parse_libusb0_quicktime_configuration(
            descriptor, expected_configuration);
        if (endpoints.configuration != 0) {
            observation.endpoints = endpoints;
            break;
        }
    }

    char active_configuration{};
    if (usb_control_msg(handle, 0x80, USB_REQ_GET_CONFIGURATION, 0, 0,
            &active_configuration, 1, 1000) == 1) {
        observation.active_configuration =
            static_cast<std::uint8_t>(active_configuration);
        observation.active_configuration_known = true;
    }
    return observation;
}

std::string topology_for(const struct usb_device& device) {
    if (!device.bus) return {};
    // libusb-win32 does not expose USB port numbers. Its bus location and
    // device path are nevertheless stable across the filter driver's normal
    // refresh on Windows and are only used after an exact serial match fails.
    return std::format("{}:{:08x}:{}", device.bus->dirname,
        device.bus->location, device.filename);
}

void populate_basic_summary(const struct usb_device& raw,
    AppleUsbDevice& info) noexcept {
    info.configuration_count = raw.descriptor.bNumConfigurations;
    for (int index = 0; index < raw.descriptor.bNumConfigurations; ++index) {
        info.highest_configuration_value = (std::max)(
            info.highest_configuration_value,
            raw.config[index].bConfigurationValue);
    }
    try { info.topology_id = topology_for(raw); } catch (...) {}
}

void populate_descriptor_summary(const struct usb_device& raw,
    AppleUsbDevice& info) noexcept {
    populate_basic_summary(raw, info);
}

int mux_configuration_for(const struct usb_device& device) {
    int selected{};
    for (int c = 0; c < device.descriptor.bNumConfigurations; ++c) {
        const auto& config = device.config[c];
        bool has_mux{};
        bool has_quicktime{};
        for (int i = 0; i < config.bNumInterfaces; ++i) {
            const auto& group = config.interface[i];
            for (int a = 0; a < group.num_altsetting; ++a) {
                const auto subclass = group.altsetting[a].bInterfaceSubClass;
                has_mux = has_mux || subclass == 0xfe;
                // Older iOS descriptors expose the screen-capture interface
                // as 0xFD before activation and change it to 0x2A afterwards.
                // Neither descriptor belongs to the normal USBMux
                // configuration.  Ignoring 0xFD makes iOS 18 restore config
                // 4 instead of config 3, leaving AppleMobileDeviceService
                // unable to rediscover the phone after capture stops.
                has_quicktime = has_quicktime || subclass == QuickTimeSubclass ||
                    subclass == QuickTimePlaceholderSubclass;
            }
        }
        if (has_mux && !has_quicktime) {
            selected = (std::max)(selected, static_cast<int>(config.bConfigurationValue));
        }
    }
    return selected;
}

void throw_last_error(const char* operation) {
    throw std::runtime_error(std::string(operation) + ": " + usb_strerror());
}

void refresh_usb_devices() {
    ensure_usb_initialized();
    if (usb_find_busses() < 0) throw_last_error("usb_find_busses");
    if (usb_find_devices() < 0) throw_last_error("usb_find_devices");
}

struct usb_device* find_device(const AppleUsbIdentity& identity,
    usb_dev_handle** opened = nullptr, bool require_quicktime = false,
    AppleUsbDevice* selected_info = nullptr,
    AppleUsbMatchKind* selected_match_kind = nullptr) {
    refresh_usb_devices();
    std::vector<AppleUsbDevice> candidates;
    std::vector<struct usb_device*> raw_candidates;
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId) continue;
            std::string topology;
            try { topology = topology_for(*device); } catch (...) {}
            // A libusb-win32 device filename contains the synthetic filter
            // slot and PID ("libusb0-0001--0x05ac-0x12a8"). Both can change
            // when iOS switches into the appended QuickTime configuration.
            // When a real serial is available, keep every Apple candidate and
            // let select_apple_usb_device perform the exact serial match. A
            // topology-only filter is safe only while the serial is absent.
            if (!apple_usb_candidate_in_scope(topology, identity)) continue;
            AppleUsbDevice candidate;
            candidate.vendor_id = device->descriptor.idVendor;
            candidate.product_id = device->descriptor.idProduct;
            candidate.bus = bus
                ? static_cast<std::uint8_t>(bus->location & 0xffU) : 0;
            candidate.address = device->devnum;
            populate_basic_summary(*device, candidate);
            if (!topology.empty()) candidate.topology_id = std::move(topology);
            // A retained identity is useful for passive enumeration, but it
            // is not authoritative after a libusb0/PnP re-enumeration. The
            // device filename can be reused for another Apple node while the
            // cache still contains the previous phone's serial. For an exact
            // capture request, force a fresh descriptor read below.
            candidate.serial = identity.serial.empty()
                ? cached_active_apple_usb_serial(candidate.topology_id)
                : std::string{};
            candidate.can_open = !candidate.serial.empty();
            candidates.push_back(std::move(candidate));
            raw_candidates.push_back(device);
        }
    }
    auto selection = select_apple_usb_device(candidates, identity, false);
    usb_dev_handle* matched_handle{};
    if (!selection.index) {
        std::vector<std::size_t> probe_order(candidates.size());
        for (std::size_t index{}; index < candidates.size(); ++index)
            probe_order[index] = index;
        std::stable_sort(probe_order.begin(), probe_order.end(),
            [&](std::size_t left, std::size_t right) {
                const bool left_topology = !identity.topology_id.empty() &&
                    candidates[left].topology_id == identity.topology_id;
                const bool right_topology = !identity.topology_id.empty() &&
                    candidates[right].topology_id == identity.topology_id;
                if (left_topology != right_topology)
                    return left_topology;
                const bool left_pid = identity.original_product_id != 0 &&
                    candidates[left].product_id == identity.original_product_id;
                const bool right_pid = identity.original_product_id != 0 &&
                    candidates[right].product_id == identity.original_product_id;
                return left_pid && !right_pid;
            });
        for (const auto index : probe_order) {
            auto& candidate = candidates[index];
            if (!candidate.serial.empty()) continue;
            auto* device = raw_candidates[index];
            if (device->descriptor.iSerialNumber == 0) continue;
            auto* handle = usb_open(device);
            if (!handle) continue;
            candidate.can_open = true;
            char value[256]{};
            const int length = usb_get_string_simple(handle,
                device->descriptor.iSerialNumber, value, sizeof(value));
            char active_configuration{};
            if (usb_control_msg(handle, 0x80, 0x08, 0, 0,
                    &active_configuration, 1, 1000) == 1) {
                candidate.active_configuration =
                    static_cast<std::uint8_t>(active_configuration);
                candidate.active_configuration_known = true;
            }
            if (length > 0) candidate.serial.assign(value,
                static_cast<std::size_t>(length));
            if (apple_usb_serial_equal(candidate.serial, identity.serial)) {
                selection.index = index;
                selection.match_kind = AppleUsbMatchKind::Serial;
                matched_handle = handle;
                break;
            }
            usb_close(handle);
        }
    }
    if (!selection.index) return nullptr;
    const auto selected_index = *selection.index;
    auto* selected = raw_candidates[selected_index];
    auto& info = candidates[selected_index];
    populate_descriptor_summary(*selected, info);
    info.quicktime_endpoints = endpoints_for(*selected, QuickTimeSubclass);
    info.quicktime_configuration =
        info.quicktime_endpoints.configuration != 0;
    auto* active_configuration_handle = matched_handle;
    // Public exact-device lookups pass selected_info. Read GET_CONFIGURATION
    // for that one selected phone even during the normal preflight path. The
    // descriptor can retain configuration 5/6 after iOS has returned to its
    // normal active configuration, so descriptor presence is not session
    // state. This deliberately does not open every enumerated Apple device.
    if (!active_configuration_handle &&
        (opened || require_quicktime || selected_info))
        active_configuration_handle = usb_open(selected);
    if (active_configuration_handle) {
        // The process-local topology cache is only a hint. A phone can be
        // unplugged and another Apple device can reuse the same libusb0 slot;
        // before any configuration-changing request, verify the selected raw
        // node's current iSerialNumber when the driver exposes it. A mismatch
        // invalidates the cache and fails closed instead of sending 0x52 to a
        // different phone.
        if (selected_info && !identity.serial.empty() &&
            selected->descriptor.iSerialNumber != 0) {
            char serial[256]{};
            const int length = usb_get_string_simple(active_configuration_handle,
                selected->descriptor.iSerialNumber, serial, sizeof(serial));
            if (length > 0 && !apple_usb_serial_equal(
                    std::string_view(serial, static_cast<std::size_t>(length)),
                    identity.serial)) {
                forget_active_apple_usb_identity(info.topology_id, info.serial);
                usb_close(active_configuration_handle);
                throw std::runtime_error(
                    "selected libusb0 USB node serial differs from the requested iPhone; stale topology identity was discarded");
            }
        }
        char active_configuration{};
        if (usb_control_msg(active_configuration_handle, 0x80, 0x08, 0, 0,
                &active_configuration, 1, 1000) == 1) {
            info.active_configuration =
                static_cast<std::uint8_t>(active_configuration);
            info.active_configuration_known = true;
        }
    }
    if (require_quicktime &&
        !is_libusb0_quicktime_configuration_active(info)) {
        if (active_configuration_handle) usb_close(active_configuration_handle);
        return nullptr;
    }
    if (opened) {
        *opened = active_configuration_handle
            ? active_configuration_handle : usb_open(selected);
        matched_handle = nullptr;
        if (!*opened) return nullptr;
    } else if (active_configuration_handle) {
        usb_close(active_configuration_handle);
        matched_handle = nullptr;
    }
    if (selected_info) *selected_info = info;
    if (selected_match_kind) *selected_match_kind = selection.match_kind;
    return selected;
}

struct usb_device* find_device(const std::string& serial,
    usb_dev_handle** opened = nullptr, bool require_quicktime = false) {
    return find_device(AppleUsbIdentity{.serial = serial}, opened,
        require_quicktime);
}

} // namespace

AppleUsbIdentity make_apple_usb_identity(const AppleUsbDevice& device) noexcept {
    AppleUsbIdentity identity;
    try {
        identity.serial = device.serial;
        identity.topology_id = device.topology_id;
    } catch (...) {
        return {};
    }
    identity.original_product_id = device.product_id;
    if (device.quicktime_endpoints.configuration != 0) {
        identity.expected_quicktime_configuration =
            device.quicktime_endpoints.configuration;
    } else if (device.highest_configuration_value != 0 &&
        device.highest_configuration_value != 0xff) {
        identity.expected_quicktime_configuration = static_cast<std::uint8_t>(
            device.highest_configuration_value + 1U);
    }
    return identity;
}

AppleUsbSelection select_apple_usb_device(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity,
    bool require_quicktime) noexcept {
    AppleUsbSelection result;
    std::optional<std::size_t> serial_index;
    std::optional<std::size_t> serial_topology_index;
    std::size_t serial_topology_matches{};
    std::optional<std::size_t> topology_index;

    for (std::size_t index{}; index < devices.size(); ++index) {
        const auto& device = devices[index];
        if (require_quicktime && !device.quicktime_configuration) continue;
        const bool serial_match = !identity.serial.empty() &&
            apple_usb_serial_equal(device.serial, identity.serial);
        const bool topology_match = !identity.topology_id.empty() &&
            device.topology_id == identity.topology_id;
        bool candidate_serial_available{};
        try {
            candidate_serial_available = !normalize(device.serial).empty();
        } catch (...) {}
        if (serial_match) {
            ++result.serial_matches;
            serial_index = index;
            if (topology_match) {
                ++serial_topology_matches;
                serial_topology_index = index;
            }
        }
        // A physical-port fallback only bridges a descriptor whose serial is
        // temporarily unreadable. A known, different serial is authoritative
        // and must never be rebound to this capture session.
        if (topology_match && !candidate_serial_available) {
            ++result.topology_matches;
            topology_index = index;
        }
    }

    if (result.serial_matches == 1) {
        result.index = serial_index;
        result.match_kind = AppleUsbMatchKind::Serial;
        return result;
    }
    if (result.serial_matches > 1) {
        if (serial_topology_matches == 1) {
            result.index = serial_topology_index;
            result.match_kind = AppleUsbMatchKind::Serial;
        } else {
            result.ambiguous = true;
        }
        return result;
    }
    if (result.topology_matches == 1) {
        result.index = topology_index;
        result.match_kind = AppleUsbMatchKind::Topology;
        return result;
    }
    result.ambiguous = result.topology_matches > 1;
    return result;
}

bool apple_usb_candidate_in_scope(std::string_view candidate_topology,
    const AppleUsbIdentity& identity) noexcept {
    return !identity.serial.empty() || identity.topology_id.empty() ||
        candidate_topology == identity.topology_id;
}

UsbEndpointSet select_best_quicktime_endpoints(
    std::span<const UsbEndpointSet> candidates) noexcept {
    UsbEndpointSet selected;
    for (const auto& candidate : candidates) {
        if (candidate.configuration == 0 || candidate.bulk_in == 0 ||
            candidate.bulk_out == 0 || (candidate.bulk_in & 0x80U) == 0 ||
            (candidate.bulk_out & 0x80U) != 0) continue;
        const auto candidate_packet = (std::min)(candidate.bulk_in_packet_size,
            candidate.bulk_out_packet_size);
        const auto selected_packet = (std::min)(selected.bulk_in_packet_size,
            selected.bulk_out_packet_size);
        if (selected.configuration == 0 ||
            candidate.configuration > selected.configuration ||
            (candidate.configuration == selected.configuration &&
                candidate_packet > selected_packet)) {
            selected = candidate;
        }
    }
    return selected;
}

UsbEndpointSet conventional_quicktime_endpoints(
    const AppleUsbIdentity& identity) noexcept {
    if (identity.expected_quicktime_configuration == 0) return {};
    return {
        .configuration = identity.expected_quicktime_configuration,
        .interface_number = 2,
        .alternate_setting = 0,
        .bulk_in = 0x86,
        .bulk_out = 0x05,
        .bulk_in_packet_size = 512,
        .bulk_out_packet_size = 512,
    };
}

std::string describe_apple_usb_candidates(
    std::span<const AppleUsbDevice> devices, const AppleUsbIdentity& identity) {
    std::string description = std::format("count={}", devices.size());
    for (std::size_t index{}; index < devices.size(); ++index) {
        const auto& device = devices[index];
        description += std::format(
            " [{} vid={:04x} pid={:04x} bus={} addr={} open={} serial_match={} topology_match={} configs={}/{} qt={}:{}:{}:{:02x}:{:02x}]",
            index, device.vendor_id, device.product_id, device.bus, device.address,
            device.can_open,
            !identity.serial.empty() && apple_usb_serial_equal(device.serial, identity.serial),
            !identity.topology_id.empty() && device.topology_id == identity.topology_id,
            device.configuration_count, device.highest_configuration_value,
            device.quicktime_endpoints.configuration,
            device.quicktime_endpoints.interface_number,
            device.quicktime_endpoints.alternate_setting,
            device.quicktime_endpoints.bulk_in,
            device.quicktime_endpoints.bulk_out);
    }
    return description;
}

bool libusb0_installed() noexcept {
    try {
        wchar_t system_directory[MAX_PATH]{};
        const auto length = GetSystemDirectoryW(system_directory, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) return false;
        std::wstring path(system_directory, length);
        path += L"\\libusb0.dll";
        const auto attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES &&
            (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    } catch (...) {
        return false;
    }
}

bool libusb0_available() noexcept {
    HMODULE module = LoadLibraryExW(L"libusb0.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return false;
    FreeLibrary(module);
    return true;
}

bool apple_usb_serial_equal(std::string_view left, std::string_view right) noexcept {
    try {
        const auto normalized_left = normalize(left);
        const auto normalized_right = normalize(right);
        return !normalized_left.empty() && !normalized_right.empty() &&
            normalized_left == normalized_right;
    } catch (...) {
        // This helper is also used by the C ABI readiness probe. Treat an
        // allocation failure as a non-match instead of allowing an exception
        // to cross a noexcept/native boundary.
        return false;
    }
}

std::vector<AppleUsbDevice> enumerate_libusb0(
    bool probe_serial_descriptors, bool use_identity_cache) {
    std::unique_lock lock(api_mutex);
    refresh_usb_devices();
    std::vector<AppleUsbDevice> result;
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != AppleVendorId) continue;
            AppleUsbDevice info;
            info.vendor_id = device->descriptor.idVendor;
            info.product_id = device->descriptor.idProduct;
            info.bus = device->bus
                ? static_cast<std::uint8_t>(device->bus->location & 0xffU) : 0;
            info.address = device->devnum;
            populate_descriptor_summary(*device, info);
            info.serial = use_identity_cache
                ? cached_active_apple_usb_serial(info.topology_id)
                : std::string{};
            info.can_open = !info.serial.empty();
            if (probe_serial_descriptors && info.serial.empty()) {
                if (usb_dev_handle* handle = usb_open(device)) {
                    info.can_open = true;
                    char serial[256]{};
                    if (usb_get_string_simple(handle, device->descriptor.iSerialNumber, serial, sizeof(serial)) > 0) {
                        info.serial = serial;
                    }
                    usb_close(handle);
                }
            }
            info.quicktime_endpoints = endpoints_for(*device, QuickTimeSubclass);
            info.quicktime_configuration =
                info.quicktime_endpoints.configuration != 0;
            result.push_back(std::move(info));
        }
    }
    return result;
}

std::optional<AppleUsbDevice> find_libusb0_device(std::string_view serial) {
    return find_libusb0_device(AppleUsbIdentity{.serial = std::string(serial)});
}

std::optional<AppleUsbDevice> find_libusb0_device(
    const AppleUsbIdentity& identity, bool require_quicktime) {
    if ((identity.serial.empty() && identity.topology_id.empty()) ||
        !libusb0_available()) return std::nullopt;
    std::unique_lock lock(api_mutex);
    AppleUsbDevice info;
    if (!find_device(identity, nullptr, require_quicktime, &info))
        return std::nullopt;
    return info;
}

bool is_libusb0_device_available(std::string_view serial) {
    const auto device = find_libusb0_device(serial);
    return device && device->can_open;
}

bool can_initialize_libusb0_quicktime_configuration(
    AppleUsbMatchKind match_kind, const AppleUsbDevice& selected,
    const UsbEndpointSet& endpoints) noexcept {
    return match_kind == AppleUsbMatchKind::Serial &&
        is_libusb0_quicktime_configuration_active(selected) &&
        endpoints.configuration != 0 &&
        endpoints.bulk_in != 0 && endpoints.bulk_out != 0 &&
        selected.quicktime_endpoints.configuration == endpoints.configuration &&
        selected.quicktime_endpoints.interface_number == endpoints.interface_number &&
        selected.quicktime_endpoints.alternate_setting == endpoints.alternate_setting &&
        selected.quicktime_endpoints.bulk_in == endpoints.bulk_in &&
        selected.quicktime_endpoints.bulk_out == endpoints.bulk_out;
}

bool is_libusb0_quicktime_configuration_active(
    const AppleUsbDevice& device) noexcept {
    return device.quicktime_configuration &&
        device.active_configuration_known &&
        device.active_configuration != 0 &&
        device.active_configuration ==
            device.quicktime_endpoints.configuration;
}

bool is_libusb0_invalid_configuration_claim(
    int result, std::string_view detail) noexcept {
    return result == LibUsb0InvalidConfiguration &&
        detail.find("invalid configuration 0") != std::string_view::npos;
}

UsbEndpointSet parse_libusb0_quicktime_configuration(
    std::span<const std::uint8_t> descriptor,
    std::uint8_t expected_configuration) noexcept {
    UsbEndpointSet result;
    if (expected_configuration == 0 || descriptor.size() < USB_DT_CONFIG_SIZE ||
        descriptor[0] < USB_DT_CONFIG_SIZE ||
        descriptor[1] != USB_DT_CONFIG ||
        descriptor[5] != expected_configuration)
        return result;

    UsbEndpointSet candidate;
    candidate.configuration = expected_configuration;
    bool in_quicktime_interface{};
    for (std::size_t offset{}; offset + 2 <= descriptor.size();) {
        const auto length = descriptor[offset];
        const auto type = descriptor[offset + 1];
        if (length < 2 || offset + length > descriptor.size()) return {};
        if (type == USB_DT_INTERFACE) {
            in_quicktime_interface = length >= USB_DT_INTERFACE_SIZE &&
                descriptor[offset + 5] == USB_CLASS_VENDOR_SPEC &&
                descriptor[offset + 6] == QuickTimeSubclass;
            if (in_quicktime_interface) {
                candidate.interface_number = descriptor[offset + 2];
                candidate.alternate_setting = descriptor[offset + 3];
                candidate.bulk_in = 0;
                candidate.bulk_out = 0;
                candidate.bulk_in_packet_size = 0;
                candidate.bulk_out_packet_size = 0;
            }
        } else if (in_quicktime_interface && type == USB_DT_ENDPOINT &&
            length >= USB_DT_ENDPOINT_SIZE &&
            (descriptor[offset + 3] & USB_ENDPOINT_TYPE_MASK) ==
                USB_ENDPOINT_TYPE_BULK) {
            const auto address = descriptor[offset + 2];
            const auto packet_size = static_cast<std::uint16_t>(
                descriptor[offset + 4] |
                (static_cast<std::uint16_t>(descriptor[offset + 5]) << 8U));
            if ((address & USB_ENDPOINT_DIR_MASK) != 0) {
                if (packet_size >= candidate.bulk_in_packet_size) {
                    candidate.bulk_in = address;
                    candidate.bulk_in_packet_size = packet_size;
                }
            } else if (packet_size >= candidate.bulk_out_packet_size) {
                candidate.bulk_out = address;
                candidate.bulk_out_packet_size = packet_size;
            }
            if (candidate.bulk_in != 0 && candidate.bulk_out != 0)
                result = candidate;
        }
        offset += length;
    }
    return result;
}

LibUsb0Connection::~LibUsb0Connection() { close(); }
LibUsb0Connection::LibUsb0Connection(LibUsb0Connection&& other) noexcept
    : handle_(other.handle_), endpoints_(other.endpoints_), claimed_(other.claimed_),
      active_identity_retained_(other.active_identity_retained_),
      active_topology_(std::move(other.active_topology_)),
      active_serial_(std::move(other.active_serial_)) {
    other.handle_ = nullptr;
    other.claimed_ = false;
    other.active_identity_retained_ = false;
    other.active_topology_.clear();
    other.active_serial_.clear();
}
LibUsb0Connection& LibUsb0Connection::operator=(LibUsb0Connection&& other) noexcept {
    if (this != &other) {
        close();
        handle_ = other.handle_;
        endpoints_ = other.endpoints_;
        claimed_ = other.claimed_;
        active_identity_retained_ = other.active_identity_retained_;
        active_topology_ = std::move(other.active_topology_);
        active_serial_ = std::move(other.active_serial_);
        other.handle_ = nullptr;
        other.claimed_ = false;
        other.active_identity_retained_ = false;
        other.active_topology_.clear();
        other.active_serial_.clear();
    }
    return *this;
}

void LibUsb0Connection::remember_active_identity(
    const AppleUsbIdentity& identity) noexcept {
    if (identity.topology_id.empty() || identity.serial.empty()) return;
    try {
        active_topology_ = identity.topology_id;
        active_serial_ = identity.serial;
        active_identity_retained_ = retain_active_apple_usb_identity(identity);
    } catch (...) {
        active_identity_retained_ = false;
        active_topology_.clear();
        active_serial_.clear();
    }
}

bool LibUsb0Connection::enable_quicktime_configuration(const std::string& serial) {
    return enable_quicktime_configuration(AppleUsbIdentity{.serial = serial});
}

bool LibUsb0Connection::enable_quicktime_configuration(
    const AppleUsbIdentity& identity) {
    return run_configuration_switch_helper(identity, true);
}

bool LibUsb0Connection::disable_quicktime_configuration(const std::string& serial) {
    return disable_quicktime_configuration(AppleUsbIdentity{.serial = serial});
}

bool LibUsb0Connection::disable_quicktime_configuration(
    const AppleUsbIdentity& identity) {
    return run_configuration_switch_helper(identity, false);
}

LibUsb0Connection LibUsb0Connection::open_quicktime(const std::string& serial) {
    return open_quicktime(AppleUsbIdentity{.serial = serial});
}

LibUsb0Connection LibUsb0Connection::open_quicktime(
    const AppleUsbIdentity& identity, LibUsb0QuickTimeOpenOptions options) {
    std::unique_lock lock(api_mutex);
    LibUsb0Connection connection;
    auto fail = [&](std::string detail) -> void {
        connection.close_unlocked();
        lock.unlock();
        throw std::runtime_error(
            "open QuickTime USB interface: " + std::move(detail));
    };

    AppleUsbDevice selected_info;
    AppleUsbMatchKind selected_match_kind{AppleUsbMatchKind::None};
    struct usb_device* device = find_device(identity, &connection.handle_,
        false, &selected_info, &selected_match_kind);
    if (!device || !connection.handle_)
        fail("libusb0 cannot find the selected Apple device");

    // A unique physical-port match may identify the re-enumerated node before
    // its serial has been read. Verify the serial on the already selected
    // handle before allowing the configuration-changing request. This never
    // probes another phone and performs no PnP/configuration operation.
    if (selected_match_kind != AppleUsbMatchKind::Serial &&
        !identity.serial.empty() && device->descriptor.iSerialNumber != 0) {
        char serial[256]{};
        const int length = usb_get_string_simple(connection.handle_,
            device->descriptor.iSerialNumber, serial, sizeof(serial));
        if (length > 0) {
            selected_info.serial.assign(serial, static_cast<std::size_t>(length));
            if (apple_usb_serial_equal(selected_info.serial, identity.serial))
                selected_match_kind = AppleUsbMatchKind::Serial;
        }
    }

    connection.endpoints_ = endpoints_for(*device, QuickTimeSubclass);
    if (!connection.endpoints_.configuration &&
        options.wait_for_activated_descriptor) {
        const auto deadline = std::chrono::steady_clock::now() +
            ActivatedDescriptorTimeout;
        HandleConfigurationObservation observation;
        unsigned probes{};
        do {
            if (options.stop_token.stop_requested())
                fail("cancelled while waiting for the activated QuickTime descriptor");
            observation = observe_handle_configuration(connection.handle_,
                identity.expected_quicktime_configuration);
            ++probes;
            if (observation.endpoints.configuration != 0 &&
                observation.active_configuration_known &&
                observation.active_configuration ==
                    observation.endpoints.configuration) {
                connection.endpoints_ = observation.endpoints;
                selected_info.quicktime_endpoints = observation.endpoints;
                selected_info.quicktime_configuration = true;
                selected_info.configuration_count = observation.configuration_count;
                selected_info.active_configuration =
                    observation.active_configuration;
                selected_info.active_configuration_known = true;
                logging::write(std::format(
                    "libusb0_descriptor_ready source=single_handle probes={} configuration={} interface={} active_configuration={}",
                    probes, connection.endpoints_.configuration,
                    connection.endpoints_.interface_number,
                    selected_info.active_configuration));
                break;
            }
            if (std::chrono::steady_clock::now() >= deadline) {
                fail(std::format(
                    "activated QuickTime descriptor was not committed within {} ms "
                    "(probes={}, configurations={}, active_configuration={}, active_known={}, last_descriptor_result={})",
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        ActivatedDescriptorTimeout).count(),
                    probes, observation.configuration_count,
                    observation.active_configuration,
                    observation.active_configuration_known,
                    observation.last_descriptor_result));
            }
            std::this_thread::sleep_for(ActivatedDescriptorProbeInterval);
        } while (true);
    }
    const bool descriptor_quicktime =
        connection.endpoints_.configuration != 0;
    if (!descriptor_quicktime && options.allow_conventional_fallback)
        connection.endpoints_ = conventional_quicktime_endpoints(identity);
    if (!connection.endpoints_.configuration)
        fail("Apple device has no QuickTime 0x2A interface");

    if (!is_libusb0_quicktime_configuration_active(selected_info)) {
        fail(std::format(
            "selected Apple device is not in the QuickTime configuration "
            "(active={}, descriptor={})",
            selected_info.active_configuration,
            connection.endpoints_.configuration));
    }

    // Claim first. A node that has already streamed has a valid libusb0 pipe
    // table, and repeating SET_CONFIGURATION on it can race Apple's lower
    // filter while the previous PnP transition is still completing.
    int claim_result = usb_claim_interface(connection.handle_,
        connection.endpoints_.interface_number);
    bool configuration_initialized{};
    if (claim_result < 0) {
        const auto first_claim_win32 = GetLastError();
        const std::string first_claim_detail = usb_strerror();
        // A QuickTime descriptor may be visible while a freshly opened
        // libusb0 handle still has an uninitialized pipe table. Initialize it
        // only when this CaptureSession sent the activation request and the
        // selected phone still reports the expected active configuration.
        // An invalid-configuration error by itself is not authorization to
        // reconfigure a retained descriptor from a previous session.
        const bool allow_configuration_initialization =
            options.allow_configuration_initialization &&
            is_libusb0_invalid_configuration_claim(claim_result,
                first_claim_detail);
        if (!allow_configuration_initialization) {
            fail(std::format(
                "claim QuickTime interface {} without changing configuration: "
                "libusb0_result={} win32_error={} detail={}",
                connection.endpoints_.interface_number, claim_result,
                first_claim_win32, first_claim_detail));
        }
        if (!descriptor_quicktime ||
            !can_initialize_libusb0_quicktime_configuration(
                selected_match_kind, selected_info, connection.endpoints_)) {
            fail("refusing to initialize an Apple USB configuration without an exact serial and descriptor-backed QuickTime interface");
        }
        const int configuration_result = usb_set_configuration(
            connection.handle_, connection.endpoints_.configuration);
        if (configuration_result < 0) {
            const auto configuration_win32 = GetLastError();
            fail(std::format(
                "initialize libusb0 QuickTime configuration {} after activation: "
                "libusb0_result={} win32_error={} detail={}; first_claim_result={} "
                "first_claim_win32_error={}",
                connection.endpoints_.configuration, configuration_result,
                configuration_win32, usb_strerror(), claim_result,
                first_claim_win32));
        }
        configuration_initialized = true;
        claim_result = usb_claim_interface(connection.handle_,
            connection.endpoints_.interface_number);
    }
    if (claim_result < 0) {
        const auto claim_win32 = GetLastError();
        fail(std::format(
            "claim QuickTime interface {}: libusb0_result={} win32_error={} detail={}",
            connection.endpoints_.interface_number, claim_result,
            claim_win32, usb_strerror()));
    }
    connection.claimed_ = true;
    logging::write(std::format(
        "libusb0_quicktime_open claim=success configuration_initialized={} "
        "configuration={} interface={} exact_serial={}",
        configuration_initialized, connection.endpoints_.configuration,
        connection.endpoints_.interface_number,
        selected_match_kind == AppleUsbMatchKind::Serial));
    if (connection.endpoints_.alternate_setting != 0 &&
        usb_set_altinterface(connection.handle_,
            connection.endpoints_.alternate_setting) < 0) {
        fail(std::format("select QuickTime alternate setting {}: {}",
            connection.endpoints_.alternate_setting, usb_strerror()));
    }
    auto active_identity = make_apple_usb_identity(selected_info);
    if (active_identity.serial.empty()) active_identity.serial = identity.serial;
    connection.remember_active_identity(active_identity);
    lock.unlock();
    return connection;
}

std::size_t LibUsb0Connection::read(std::span<std::uint8_t> destination, unsigned timeout_ms) {
    std::shared_lock lock(api_mutex);
    auto* handle = handle_;
    if (!handle || destination.empty())
        throw std::invalid_argument("invalid QuickTime USB read");
    const int count = usb_bulk_read(handle, endpoints_.bulk_in,
        reinterpret_cast<char*>(destination.data()),
        static_cast<int>(std::min<std::size_t>(destination.size(), INT_MAX)),
        static_cast<int>(timeout_ms));
    if (count == LibUsb0TransferTimedOut) return 0;
    if (count < 0)
        throw std::runtime_error(std::format(
            "QuickTime bulk read: libusb0 error {}", count));
    return static_cast<std::size_t>(count);
}

void LibUsb0Connection::write(std::span<const std::uint8_t> source, unsigned timeout_ms) {
    std::shared_lock lock(api_mutex);
    auto* handle = handle_;
    if (!handle || source.empty())
        throw std::invalid_argument("invalid QuickTime USB write");
    std::size_t offset{};
    while (offset < source.size()) {
        const int count = usb_bulk_write(handle, endpoints_.bulk_out,
            reinterpret_cast<char*>(const_cast<std::uint8_t*>(source.data() + offset)),
            static_cast<int>(std::min<std::size_t>(source.size() - offset, INT_MAX)),
            static_cast<int>(timeout_ms));
        if (count <= 0)
            throw std::runtime_error(std::format(
                "QuickTime bulk write: libusb0 error {}", count));
        offset += static_cast<std::size_t>(count);
    }
}

void LibUsb0Connection::clear_halt() {
    std::unique_lock lock(api_mutex);
    if (!handle_) return;
    if (usb_clear_halt(handle_, endpoints_.bulk_in) < 0) throw_last_error("clear QuickTime IN halt");
    if (usb_clear_halt(handle_, endpoints_.bulk_out) < 0) throw_last_error("clear QuickTime OUT halt");
}

void LibUsb0Connection::recover_handshake() {
    std::unique_lock lock(api_mutex);
    if (!handle_) return;
    if (usb_control_msg(handle_, 0x40, 0x40, 0x6400, 0x6400, nullptr, 0, 1000) < 0) {
        throw_last_error("recover QuickTime handshake");
    }
}

bool LibUsb0Connection::request_normal_configuration() {
    std::unique_lock lock(api_mutex);
    if (!handle_)
        throw std::runtime_error(
            "cannot restore normal USB configuration: QuickTime handle is closed");
    // usb_close implicitly releases a claimed interface. If 0x52 disconnects
    // the device first, that implicit release targets a stale PnP handle and
    // can violate lifetime rules in legacy WDF/UCX filter stacks. Release
    // while the QuickTime node is still connected, then use the same open,
    // unclaimed device handle for the one disconnecting control request.
    if (claimed_) {
        if (usb_release_interface(handle_, endpoints_.interface_number) < 0)
            throw_last_error("release QuickTime interface before USB restore");
        claimed_ = false;
    }
    const int result = usb_control_msg(handle_, 0x40, 0x52, 0, 0,
        nullptr, 0, 1000);
    // The request intentionally disconnects this handle. Some libusb0 builds
    // surface the accepted disconnect as an I/O error, so re-enumeration is
    // authoritative and the return value is diagnostic only.
    return result >= 0;
}

void LibUsb0Connection::cancel_pending_io() noexcept {
    // Do not issue usb_cancel_async through the legacy Apple/libusb0 filter
    // stack. Synchronous bulk calls use bounded timeouts and return naturally.
}

void LibUsb0Connection::clear_io_cancellation() noexcept {
}

void LibUsb0Connection::close() noexcept {
    try {
        std::unique_lock lock(api_mutex);
        close_unlocked();
    } catch (...) {
        // A failed lock must not turn cleanup into process termination. Leave
        // the handle for the owning process to reclaim rather than racing the
        // legacy library from an unprotected thread.
    }
}

void LibUsb0Connection::close_unlocked() noexcept {
    auto* handle = std::exchange(handle_, nullptr);
    claimed_ = false;
    // usb_close performs the interface release. Calling usb_release_interface
    // first can make libusb-win32 issue a second release when the first one
    // fails during PnP teardown.
    if (handle) usb_close(handle);
    if (active_identity_retained_) {
        release_active_apple_usb_identity(active_topology_, active_serial_);
        active_identity_retained_ = false;
    }
    active_topology_.clear();
    active_serial_.clear();
}

} // namespace iPhoneMirror::transport
