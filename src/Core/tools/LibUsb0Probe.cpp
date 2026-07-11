#include <lusb0_usb.h>
#include "../src/Transport/LibUsb0Transport.h"
#include "../src/Protocol/QuickTimePacket.h"

#include <iomanip>
#include <iostream>
#include <string_view>
#include <thread>
#include <vector>
#include <chrono>

int main(int argc, char** argv) {
    if (argc > 1 && std::string_view(argv[1]) == "--raw") {
        if (argc < 3) {
            std::cerr << "usage: iPhoneMirror.LibUsb0Probe --raw <UDID>\n";
            return 2;
        }
        const std::string_view target_serial(argv[2]);
        usb_init(); usb_find_busses(); usb_find_devices();
        for (auto* bus = usb_get_busses(); bus; bus = bus->next) {
            for (auto* device = bus->devices; device; device = device->next) {
                if (device->descriptor.idVendor != 0x05ac) continue;
                auto* handle = usb_open(device);
                if (!handle) continue;
                char serial[256]{};
                usb_get_string_simple(handle, device->descriptor.iSerialNumber, serial, sizeof(serial));
                if (!iPhoneMirror::transport::apple_usb_serial_equal(serial, target_serial)) {
                    usb_close(handle);
                    continue;
                }
                const int config = usb_set_configuration(handle, 5);
                const int claim = usb_claim_interface(handle, 2);
                const auto ping = iPhoneMirror::quicktime::make_ping();
                const int write = usb_bulk_write(handle, 0x05,
                    reinterpret_cast<char*>(const_cast<std::uint8_t*>(ping.data())),
                    static_cast<int>(ping.size()), 3000);
                std::cout << "raw_config=" << config << " claim=" << claim << " write=" << write
                          << " error=" << usb_strerror() << '\n';
                usb_release_interface(handle, 2);
                usb_close(handle);
                return write < 0 ? 3 : 0;
            }
        }
        std::cerr << "raw device not found\n";
        return 2;
    }
    if (argc > 1 && std::string_view(argv[1]) == "--quicktime") {
        if (argc < 3) {
            std::cerr << "usage: iPhoneMirror.LibUsb0Probe --quicktime <UDID>\n";
            return 2;
        }
        try {
            auto connection = iPhoneMirror::transport::LibUsb0Connection::open_quicktime(argv[2]);
            const auto ping = iPhoneMirror::quicktime::make_ping();
            connection.write(ping, 2000);
            std::vector<std::uint8_t> buffer(1024 * 1024);
            const auto count = connection.read(buffer, 5000);
            std::cout << "quicktime_write=ok read_bytes=" << count;
            if (count >= 8) {
                std::cout << " header=" << std::hex;
                for (std::size_t i = 0; i < 8; ++i) std::cout << static_cast<int>(buffer[i]) << ' ';
                std::cout << std::dec;
            }
            std::cout << '\n';
            try { connection.disable_quicktime_configuration(); } catch (...) {}
            connection.close();
            return 0;
        } catch (const std::exception& error) {
            std::cerr << "quicktime_error=" << error.what() << '\n';
            return 3;
        }
    }
    const bool enable = argc > 1 && std::string_view(argv[1]) == "--enable";
    const bool disable = argc > 1 && std::string_view(argv[1]) == "--disable";
    const bool disable_only = argc > 1 && std::string_view(argv[1]) == "--disable-only";
    const bool quicktime_read = argc > 1 && std::string_view(argv[1]) == "--quicktime-read";
    usb_init();
    usb_find_busses();
    usb_find_devices();
    int apple_devices = 0;
    for (struct usb_bus* bus = usb_get_busses(); bus; bus = bus->next) {
        for (struct usb_device* device = bus->devices; device; device = device->next) {
            if (device->descriptor.idVendor != 0x05ac) continue;
            ++apple_devices;
            std::cout << "Apple " << std::hex << std::setw(4) << std::setfill('0')
                      << device->descriptor.idVendor << ':' << std::setw(4)
                      << device->descriptor.idProduct << std::dec
                      << " configurations=" << static_cast<int>(device->descriptor.bNumConfigurations) << '\n';
            usb_dev_handle* handle = usb_open(device);
            std::cout << "  open=" << (handle ? "yes" : "no")
                      << (handle ? "" : usb_strerror()) << '\n';
            if (handle) {
                char active_configuration{};
                const int active_result = usb_control_msg(handle, 0x80, 0x08, 0, 0,
                    &active_configuration, 1, 1000);
                std::cout << "  active_configuration="
                          << static_cast<int>(static_cast<unsigned char>(active_configuration))
                          << " result=" << active_result << '\n';
            }
            if (handle && device->descriptor.iSerialNumber) {
                char serial[256]{};
                const int length = usb_get_string_simple(handle, device->descriptor.iSerialNumber,
                    serial, static_cast<int>(sizeof(serial)));
                if (length > 0) std::cout << "  serial=" << serial << '\n';
            }
            if (handle && enable) {
                const int result = usb_control_msg(handle, 0x40, 0x52, 0, 2, nullptr, 0, 1000);
                std::cout << "  enable_quicktime=" << result;
                if (result < 0) std::cout << " error=" << usb_strerror();
                std::cout << '\n';
            }
            if (handle && (disable || disable_only)) {
                const int result = usb_control_msg(handle, 0x40, 0x52, 0, 0, nullptr, 0, 1000);
                std::cout << "  disable_quicktime=" << result;
                if (result < 0) std::cout << " error=" << usb_strerror();
                std::cout << '\n';
                int mux_configuration{};
                for (int c = 0; c < device->descriptor.bNumConfigurations; ++c) {
                    const auto& config = device->config[c];
                    bool has_mux{};
                    bool has_quicktime{};
                    for (int i = 0; i < config.bNumInterfaces; ++i) {
                        const auto& group = config.interface[i];
                        for (int a = 0; a < group.num_altsetting; ++a) {
                            has_mux = has_mux || group.altsetting[a].bInterfaceSubClass == 0xfe;
                            const auto subclass = group.altsetting[a].bInterfaceSubClass;
                            has_quicktime = has_quicktime || subclass == 0x2a || subclass == 0xfd;
                        }
                    }
                    if (has_mux && !has_quicktime) {
                        mux_configuration = (std::max)(mux_configuration,
                            static_cast<int>(config.bConfigurationValue));
                    }
                }
                if (disable && result >= 0 && mux_configuration > 0) {
                    const int restore = usb_set_configuration(handle, mux_configuration);
                    std::cout << "  restore_mux_configuration=" << mux_configuration
                              << " result=" << restore << '\n';
                }
            }
            int qt_configuration = -1;
            int qt_interface = -1;
            int qt_in = -1;
            int qt_out = -1;
            for (int c = 0; c < device->descriptor.bNumConfigurations; ++c) {
                const auto& config = device->config[c];
                std::cout << "  config=" << static_cast<int>(config.bConfigurationValue)
                          << " interfaces=" << static_cast<int>(config.bNumInterfaces) << '\n';
                for (int i = 0; i < config.bNumInterfaces; ++i) {
                    const auto& group = config.interface[i];
                    for (int a = 0; a < group.num_altsetting; ++a) {
                        const auto& interface = group.altsetting[a];
                        if (interface.bInterfaceClass == 0xff && interface.bInterfaceSubClass == 0x2a) {
                            qt_configuration = config.bConfigurationValue;
                            qt_interface = interface.bInterfaceNumber;
                            for (int e = 0; e < interface.bNumEndpoints; ++e) {
                                const int address = interface.endpoint[e].bEndpointAddress;
                                if ((address & 0x80) != 0) qt_in = address;
                                else qt_out = address;
                            }
                        }
                        std::cout << "    interface=" << static_cast<int>(interface.bInterfaceNumber)
                                  << " class=0x" << std::hex << static_cast<int>(interface.bInterfaceClass)
                                  << " subclass=0x" << static_cast<int>(interface.bInterfaceSubClass)
                                  << std::dec << " endpoints=" << static_cast<int>(interface.bNumEndpoints) << '\n';
                        for (int e = 0; e < interface.bNumEndpoints; ++e) {
                            const auto& endpoint = interface.endpoint[e];
                            std::cout << "      endpoint=0x" << std::hex
                                      << static_cast<int>(endpoint.bEndpointAddress) << std::dec
                                      << " maxPacket=" << endpoint.wMaxPacketSize << '\n';
                        }
                    }
                }
            }
            if (handle && quicktime_read && qt_configuration > 0 && qt_interface >= 0 &&
                qt_in > 0 && qt_out > 0) {
                std::cout << "  quicktime_read config=" << qt_configuration
                          << " interface=" << qt_interface << " in=0x" << std::hex << qt_in
                          << " out=0x" << qt_out << std::dec << '\n';
                const int set_result = usb_set_configuration(handle, qt_configuration);
                const int claim_result = usb_claim_interface(handle, qt_interface);
                std::cout << "  set_configuration=" << set_result
                          << " claim_interface=" << claim_result << '\n';
                std::this_thread::sleep_for(std::chrono::seconds(1));
                std::vector<char> buffer(5U * 1024U * 1024U);
                bool recovery_sent{};
                for (int attempt = 0; attempt < 10; ++attempt) {
                    const int count = usb_bulk_read(handle, qt_in, buffer.data(),
                        static_cast<int>(buffer.size()), 1000);
                    std::cout << "  bulk_read[" << attempt << "]=" << count;
                    if (count < 0) std::cout << " error=" << usb_strerror();
                    if (count > 0) {
                        std::cout << " bytes=";
                        for (int index = 0; index < (std::min)(count, 32); ++index) {
                            std::cout << std::hex << std::setw(2) << std::setfill('0')
                                      << (static_cast<unsigned>(static_cast<unsigned char>(buffer[index]))) << ' ';
                        }
                        std::cout << std::dec;
                    }
                    std::cout << '\n';
                    if (count > 0) break;
                    if (!recovery_sent) {
                        recovery_sent = true;
                        const int kick = usb_control_msg(handle, 0x40, 0x40, 0x6400, 0x6400,
                            nullptr, 0, 1000);
                        const char ping[16] = {0x10, 0, 0, 0, 0x67, 0x6e, 0x69, 0x70,
                            0, 0, 0, 0, 1, 0, 0, 0};
                        const int ping_result = usb_bulk_write(handle, qt_out,
                            const_cast<char*>(ping), sizeof(ping), 1000);
                        std::cout << "  recovery kick=" << kick << " ping=" << ping_result;
                        if (ping_result < 0) std::cout << " error=" << usb_strerror();
                        std::cout << '\n';
                    }
                }
                usb_release_interface(handle, qt_interface);
            }
            if (handle) usb_close(handle);
        }
    }
    std::cout << "apple_devices=" << apple_devices << '\n';
    return apple_devices == 0 ? 2 : 0;
}
