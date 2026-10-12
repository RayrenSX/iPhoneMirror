// Shim-level regression for the DNS-SD interface contract (issue #19,
// PR #50 review evidence). This target compiles DnsSdShim.cpp with mock
// dnsapi entry points and inspects the final DNS_SERVICE_REGISTER_REQUEST
// the shim hands to Windows. The intended contract is all-adapter coverage:
// whichever adapter the upstream per-interface registration arrives on, the
// native request must keep InterfaceIndex == 0 (all interfaces) so Mobile
// Hotspot and multi-NIC clients keep discovering the receiver. A nonzero
// value here is exactly the issue #19 failure mode.

#include <WinSock2.h>
#include <Windows.h>
#include <WinDNS.h>
#include <iphlpapi.h>

#include <cstdint>
#include <iostream>
#include <new>

namespace {

DNS_SERVICE_REGISTER_REQUEST last_register_request{};
std::uint32_t register_calls{};
std::uint32_t deregister_calls{};
bool upstream_callback_ran{};

} // namespace

// --- Mock dnsapi -----------------------------------------------------------
// The shim normally links these five functions from dnsapi.dll. Defining
// them here (and deliberately not linking dnsapi.lib) lets the test observe
// the exact request the shim passes to Windows.

PDNS_SERVICE_INSTANCE WINAPI DnsServiceConstructInstance(
    PCWSTR, PCWSTR, PIP4_ADDRESS, PIP6_ADDRESS, WORD, WORD, WORD, DWORD,
    PCWSTR*, PCWSTR*) {
    return new (std::nothrow) DNS_SERVICE_INSTANCE{};
}

VOID WINAPI DnsServiceFreeInstance(PDNS_SERVICE_INSTANCE instance) {
    delete instance;
}

DWORD WINAPI DnsServiceRegister(PDNS_SERVICE_REGISTER_REQUEST request,
    PDNS_SERVICE_CANCEL) {
    last_register_request = *request;
    ++register_calls;
    // Complete synchronously, mirroring a compatibility implementation; the
    // shim's completion gate treats this as an accepted registration.
    if (request->pRegisterCompletionCallback) {
        request->pRegisterCompletionCallback(ERROR_SUCCESS,
            request->pQueryContext, request->pServiceInstance);
    }
    return ERROR_SUCCESS;
}

DWORD WINAPI DnsServiceDeRegister(PDNS_SERVICE_REGISTER_REQUEST,
    PDNS_SERVICE_CANCEL) {
    ++deregister_calls;
    return DNS_REQUEST_PENDING;
}

DWORD WINAPI DnsServiceRegisterCancel(PDNS_SERVICE_CANCEL) {
    return ERROR_SUCCESS;
}

// --- Shim C ABI ------------------------------------------------------------

extern "C" {
using DNSServiceRef = void*;
using DNSServiceRegisterReply = void (__stdcall*)(DNSServiceRef,
    std::uint32_t, std::int32_t, const char*, const char*, const char*,
    void*);
std::int32_t __stdcall DNSServiceRegister(DNSServiceRef*, std::uint32_t,
    std::uint32_t, const char*, const char*, const char*, const char*,
    std::uint16_t, std::uint16_t, const void*, DNSServiceRegisterReply,
    void*);
void __stdcall DNSServiceRefDeallocate(DNSServiceRef);
}

namespace {

void __stdcall evidence_callback(DNSServiceRef, std::uint32_t, std::int32_t,
    const char*, const char*, const char*, void*) {
    upstream_callback_ran = true;
}

} // namespace

int main() {
    // Mirror the upstream AirPlayServer arrival order: it registers the same
    // service once per enumerated adapter, and the first arrival can be any
    // adapter, including a virtual one. Use this machine's real default
    // route as the first arrival so the run reflects the live route table.
    ULONG default_route = 0;
    const auto have_route =
        GetBestInterface(0x08080808U, &default_route) == NO_ERROR;
    const auto first_arrival =
        static_cast<std::uint32_t>(have_route ? default_route : 7);
    // A second arrival on a different adapter, as upstream's loop produces.
    const std::uint32_t second_arrival = first_arrival == 19 ? 42 : 19;

    const auto register_service = [](std::uint32_t interface_index,
                                     DNSServiceRef* out) {
        return DNSServiceRegister(out, 0, interface_index, "Evidence",
            "_airplay._tcp", nullptr, nullptr, htons(7001), 0, nullptr,
            evidence_callback, nullptr);
    };

    DNSServiceRef first{};
    if (register_service(first_arrival, &first) != 0 || !first) {
        std::cerr << "shim rejected the first registration\n";
        return 1;
    }
    if (register_calls == 0) {
        // No eligible adapter on this machine: the shim kept the logical
        // registration pending and there is no final request to inspect.
        std::cout << "no eligible adapter; interface regression skipped\n";
        DNSServiceRefDeallocate(first);
        return 77;
    }
    if (last_register_request.InterfaceIndex != 0) {
        std::cerr << "native registration is limited to interface "
                  << last_register_request.InterfaceIndex
                  << "; Mobile Hotspot clients lose discovery (issue #19)\n";
        DNSServiceRefDeallocate(first);
        return 1;
    }
    if (!upstream_callback_ran) {
        std::cerr << "shim did not deliver the registration callback\n";
        DNSServiceRefDeallocate(first);
        return 1;
    }

    // A second upstream arrival for the same service on a different adapter
    // must share the existing all-interfaces registration instead of
    // re-registering natively on that adapter.
    DNSServiceRef second{};
    if (register_service(second_arrival, &second) != 0 || !second) {
        std::cerr << "shim rejected the second per-adapter registration\n";
        DNSServiceRefDeallocate(first);
        return 1;
    }
    if (register_calls != 1) {
        std::cerr << "per-adapter arrivals produced " << register_calls
                  << " native registrations instead of one shared\n";
        DNSServiceRefDeallocate(first);
        DNSServiceRefDeallocate(second);
        return 1;
    }

    std::cout << "first arrival ifIndex=" << first_arrival
              << " second arrival ifIndex=" << second_arrival
              << " final InterfaceIndex="
              << last_register_request.InterfaceIndex
              << " native register calls=" << register_calls << "\n";

    DNSServiceRefDeallocate(first);
    DNSServiceRefDeallocate(second);
    if (deregister_calls == 0) {
        std::cerr << "releasing all references did not deregister natively\n";
        return 1;
    }

    std::cout << "DNS-SD shim keeps one all-interfaces registration\n";
    return 0;
}
