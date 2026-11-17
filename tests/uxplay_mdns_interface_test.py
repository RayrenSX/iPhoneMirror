"""Compile the shipped UxPlay Windows selector against deterministic adapter fixtures.

The selector is extracted from the production patch rather than reimplemented.
Run on Windows with CMake/Visual Studio, or pass --compiler <MinGW gcc.exe>.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(add_help=False)
parser.add_argument("--compiler")
parser.add_argument("--patch", type=Path,
                    default=ROOT / "scripts/patches/uxplay-windows-mdns-interface.patch")
options, remaining = parser.parse_known_args()

FIXTURE = r'''
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <stdint.h>
#include <limits.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <wctype.h>

static IP_ADAPTER_ADDRESSES fixtures[8];
static IP_ADAPTER_UNICAST_ADDRESS unicast[8];
static struct sockaddr_in addresses[8];
static IP_ADAPTER_GATEWAY_ADDRESS_LH gateway;
static DWORD requested_index;
static size_t count;

static DWORD mock_best_interface(IPAddr address, PDWORD index) {
    (void)address; *index = requested_index; return NO_ERROR;
}
static ULONG mock_adapter_addresses(ULONG family, ULONG flags, PVOID reserved,
    PIP_ADAPTER_ADDRESSES result, PULONG size) {
    (void)family; (void)flags; (void)reserved;
    const ULONG required = (ULONG)(sizeof(fixtures[0]) * count);
    if (*size < required) { *size = required; return ERROR_BUFFER_OVERFLOW; }
    if (!count) return ERROR_NO_DATA;
    memcpy(result, fixtures, required);
    for (size_t i = 0; i < count; ++i)
        result[i].Next = i + 1 < count ? &result[i + 1] : NULL;
    return NO_ERROR;
}
#define GetBestInterface mock_best_interface
#define GetAdaptersAddresses mock_adapter_addresses
static uint32_t select_ipv4(void) {
@SELECTOR@
    return 0; /* Isolate the explicit Windows selection from upstream route fallback. */
}

static void reset(DWORD default_index) {
    memset(fixtures, 0, sizeof(fixtures));
    memset(unicast, 0, sizeof(unicast));
    memset(addresses, 0, sizeof(addresses));
    count = 0; requested_index = default_index;
}
static void add(DWORD index, ULONG type, wchar_t *name, const char *ip, ULONG metric) {
    const size_t slot = count++;
    IP_ADAPTER_ADDRESSES *adapter = &fixtures[slot];
    adapter->IfIndex = index; adapter->IfType = type;
    adapter->FriendlyName = name; adapter->OperStatus = IfOperStatusUp;
    adapter->Ipv4Metric = metric; adapter->FirstGatewayAddress = &gateway;
    adapter->FirstUnicastAddress = &unicast[slot];
    unicast[slot].DadState = IpDadStatePreferred;
    unicast[slot].Address.lpSockaddr = (struct sockaddr *)&addresses[slot];
    addresses[slot].sin_family = AF_INET;
    addresses[slot].sin_addr.s_addr = inet_addr(ip);
}
static int failures;
static void expect(const char *scenario, const char *ip) {
    const uint32_t selected = select_ipv4();
    if (selected != (ip ? inet_addr(ip) : 0)) {
        fprintf(stderr, "FAIL %s: selected 0x%08lx\n", scenario, (unsigned long)selected);
        failures++;
    } else printf("PASS %s\n", scenario);
}
int main(void) {
    reset(2);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    expect("default physical Wi-Fi", "192.168.1.2");

    reset(99);
    add(99, IF_TYPE_TUNNEL, L"VPN Tunnel", "10.1.0.2", 1);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    expect("VPN default route still selects LAN", "192.168.1.2");

    reset(0);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    expect("first eligible adapter without a default route", "192.168.1.2");

    reset(99);
    add(99, IF_TYPE_ETHERNET_CSMACD, L"Tailscale VPN", "100.64.1.2", 1);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    expect("physical LAN outranks virtual default", "192.168.1.2");

    reset(99);
    add(99, IF_TYPE_ETHERNET_CSMACD, L"Hyper-V Virtual", "172.16.1.1", 1);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    add(3, IF_TYPE_ETHERNET_CSMACD, L"Ethernet", "192.168.2.2", 20);
    expect("physical adapter metric tie break", "192.168.2.2");

    reset(2);
    add(3, IF_TYPE_ETHERNET_CSMACD, L"Ethernet", "192.168.2.2", 20);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    expect("physical default route preference", "192.168.1.2");

    reset(99);
    add(2, IF_TYPE_IEEE80211, L"Wi-Fi", "192.168.1.2", 35);
    fixtures[0].OperStatus = IfOperStatusDown;
    add(3, IF_TYPE_ETHERNET_CSMACD, L"Ethernet", "192.168.2.2", 20);
    fixtures[1].Flags = IP_ADAPTER_NO_MULTICAST;
    add(4, IF_TYPE_ETHERNET_CSMACD, L"Ethernet 2", "169.254.1.2", 10);
    add(5, IF_TYPE_SOFTWARE_LOOPBACK, L"Loopback", "127.0.0.1", 1);
    expect("offline non-multicast link-local and loopback are excluded", NULL);

    reset(0);
    expect("no adapters allows upstream fallback", NULL);
    return failures ? 1 : 0;
}
'''


@unittest.skipUnless(sys.platform == "win32", "Windows adapter selection")
class UxPlayMdnsInterfaceTests(unittest.TestCase):
    def test_windows_interface_selection(self):
        patch = options.patch.read_text(encoding="utf-8")
        body = patch.split(" static uint32_t mdns_get_default_ipv4(void)\n {\n", 1)[1]
        body = body.split("     int fd;", 1)[0]
        selector = "\n".join(line[1:] for line in body.splitlines() if line.startswith("+"))
        self.assertTrue(selector.startswith("#ifdef WIN32"))
        self.assertTrue(selector.endswith("#endif"))
        with tempfile.TemporaryDirectory(prefix="uxplay-mdns-") as directory:
            stage = Path(directory)
            (stage / "fixture.c").write_text(FIXTURE.replace("@SELECTOR@", selector), encoding="utf-8")
            compiler = options.compiler or shutil.which("gcc")
            if compiler:
                executable = stage / "fixture.exe"
                environment = os.environ.copy()
                environment["PATH"] = str(Path(compiler).resolve().parent) + os.pathsep + environment.get("PATH", "")
                subprocess.run([compiler, "-std=c11", "-Wall", "-Wextra", "-Werror",
                                "-Wno-error=parentheses", "-DWIN32", str(stage / "fixture.c"),
                                "-lws2_32", "-o", str(executable)], check=True, env=environment)
            else:
                (stage / "CMakeLists.txt").write_text(
                    "cmake_minimum_required(VERSION 3.25)\nproject(UxPlayMdnsRegression LANGUAGES C)\n"
                    "set(CMAKE_C_STANDARD 11)\nadd_executable(fixture fixture.c)\n"
                    "target_link_libraries(fixture PRIVATE ws2_32)\n", encoding="utf-8")
                subprocess.run(["cmake", "-S", str(stage), "-B", str(stage / "build")], check=True)
                subprocess.run(["cmake", "--build", str(stage / "build"), "--config", "Release"], check=True)
                executable = stage / "build/Release/fixture.exe"
            subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0], *remaining])
