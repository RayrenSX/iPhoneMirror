#include "iPhoneMirror/CoreApi.h"

#include <algorithm>
#include <atomic>
#include <barrier>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {
void check(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
constexpr auto ok = static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
constexpr auto stopped = static_cast<std::int32_t>(iPhoneMirror::Result::NotInitialized);
constexpr auto invalid = static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);

std::int32_t environment() {
    iPhoneMirror::EnvironmentInfo value{};
    value.struct_size = sizeof(value);
    return im_get_environment(&value);
}
}

int main() {
    try {
        check(im_get_environment(nullptr) == invalid, "reject null environment output");
        iPhoneMirror::EnvironmentInfo value{};
        check(im_get_environment(&value) == invalid, "reject wrong environment size");
        check(environment() == stopped, "environment before initialization is rejected");
        check(im_initialize() == ok, "initialize core");

        std::atomic_int failures{};
        std::atomic_int readers{3};
        std::barrier start(4);
        std::vector<std::jthread> workers;
        for (int index = 0; index < 3; ++index) {
            workers.emplace_back([&] {
                start.arrive_and_wait();
                for (int pass = 0; pass < 8; ++pass)
                    if (environment() != ok) ++failures;
                --readers;
            });
        }
        start.arrive_and_wait();
        std::size_t samples{};
        double longest_ms{};
        while (readers != 0) {
            std::int32_t running{}, ready{};
            const auto before = std::chrono::steady_clock::now();
            if (im_wireless_receiver_get_status(&running, &ready) != ok || running || ready)
                ++failures;
            if (im_media_cast_receiver_get_status(&running, &ready) != ok || running || ready)
                ++failures;
            longest_ms = std::max(longest_ms, std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - before).count());
            ++samples;
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        workers.clear();
        check(failures == 0 && samples != 0,
            "parallel environment reads preserve receiver status without starting receivers");
        std::cout << "Concurrent environment/status: samples=" << samples
            << " longest_pair_ms=" << longest_ms << '\n';

        // Race shutdown with active callers. Each call either finishes under
        // the initialization lease or reports NotInitialized; no stale state
        // may escape shutdown, and a subsequent initialization must still work.
        for (int cycle = 0; cycle < 4; ++cycle) {
            std::barrier race(3);
            std::vector<std::jthread> callers;
            for (int index = 0; index < 2; ++index)
                callers.emplace_back([&] {
                    race.arrive_and_wait();
                    for (int pass = 0; pass < 8; ++pass) {
                        const auto result = environment();
                        if (result != ok && result != stopped) ++failures;
                        if (result == stopped) break;
                    }
                });
            race.arrive_and_wait();
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            im_shutdown();
            callers.clear();
            check(environment() == stopped, "shutdown rejects new environment reads");
            check(im_initialize() == ok && environment() == ok,
                "core reinitializes after concurrent environment/shutdown");
        }
        im_shutdown();
        check(failures == 0, "concurrent shutdown returned an unexpected result");
        std::cout << "PASS environment validation, parallel status, shutdown and reinitialization\n";
        return 0;
    } catch (const std::exception& error) {
        im_shutdown();
        std::cerr << error.what() << '\n';
        return 1;
    }
}
