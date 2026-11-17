#include "iPhoneMirror/CoreApi.h"
#include "Renderer/D3D11PreviewRenderer.h"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <exception>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {
using namespace std::chrono_literals;

void require(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

void require_api(std::int32_t result, const std::string& stage) {
    if (result == 0) return;
    const auto* error = im_last_error();
    const auto length = error ? WideCharToMultiByte(CP_UTF8, 0, error, -1,
        nullptr, 0, nullptr, nullptr) : 0;
    std::string detail(length > 0 ? length : 1, '\0');
    if (length > 0) WideCharToMultiByte(CP_UTF8, 0, error, -1,
        detail.data(), length, nullptr, nullptr);
    throw std::runtime_error(stage + ": result=" + std::to_string(result) + " " + detail.c_str());
}

void pump() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

struct Window {
    HWND handle{};
    ~Window() { if (handle) DestroyWindow(handle); }
};

struct Session {
    iPhoneMirror::SessionHandle handle{};
    Session(const wchar_t* identity, std::uint8_t gray) {
        iPhoneMirror::CaptureOptions options{};
        options.struct_size = sizeof(options);
        options.api_version = iPhoneMirror::ApiVersion;
        options.target_fps = 60;
        require_api(im_external_session_create(identity, &options, &handle), "create synthetic session");
        try {
            std::vector<std::uint8_t> i420(64 * 64, gray);
            i420.resize(64 * 64 * 3 / 2, 128);
            require_api(im_external_session_push(handle, 1, 64, 64, i420.data(),
                static_cast<std::uint32_t>(i420.size())), "publish synthetic frame");
        } catch (...) {
            im_session_destroy(handle);
            throw;
        }
    }
    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    ~Session() { destroy(); }
    void destroy() { if (handle) im_session_destroy(std::exchange(handle, 0)); }
};

void require_owner(iPhoneMirror::SessionHandle handle, HWND window, bool expected) {
    iPhoneMirror::VideoOutputStatus status{};
    status.struct_size = sizeof(status);
    const auto result = im_session_get_video_output_status(handle, window, &status);
    if (expected) require_api(result, "expected preview owner");
    else require(result != 0, "Displaced session still owns the preview HWND");
}

void pump_window(HWND window, const std::string& stage) {
    const auto deadline = std::chrono::steady_clock::now() + 250ms;
    do {
        pump();
        std::this_thread::sleep_for(5ms);
    } while (std::chrono::steady_clock::now() < deadline);
    require(IsWindow(window) != FALSE, stage + ": preview HWND was destroyed");
}

// D3D operations on a worker can synchronously message the owning UI thread.
// Keep pumping while joining; a timeout must not hang in a thread destructor.
template <typename Action>
void run_worker(Action action) {
    std::atomic_bool done{};
    std::exception_ptr failure;
    std::thread worker([&] {
        try { action(); } catch (...) { failure = std::current_exception(); }
        done.store(true, std::memory_order_release);
    });
    const auto deadline = std::chrono::steady_clock::now() + 10s;
    while (!done.load(std::memory_order_acquire)) {
        pump();
        if (std::chrono::steady_clock::now() >= deadline) {
            std::cerr << "Preview ownership operation deadlocked\n" << std::flush;
            std::quick_exit(2);
        }
        std::this_thread::sleep_for(1ms);
    }
    worker.join();
    if (failure) std::rethrow_exception(failure);
}

void run_surface(bool composition, const RECT& work, const wchar_t* window_class) {
    const std::string name = composition ? "DirectComposition" : "child HWND";
    const auto instance = GetModuleHandleW(nullptr);
    Window parent{CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        window_class, L"Preview ownership test background", WS_POPUP | WS_VISIBLE,
        work.left + 30, work.top + 30, 310, 170, nullptr, nullptr, instance, nullptr)};
    require(parent.handle != nullptr, name + ": cannot create background");
    const auto create_window = [&](int offset) {
        return CreateWindowExW(composition
            ? WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP : 0,
            window_class, L"Preview ownership test", composition
                ? WS_POPUP | WS_VISIBLE : WS_CHILD | WS_VISIBLE,
            composition ? work.left + offset + 30 : offset,
            composition ? work.top + 50 : 20,
            128, 128, parent.handle, nullptr, instance, nullptr);
    };
    Window main{create_window(10)};
    Window companion{create_window(162)};
    require(main.handle && companion.handle, name + ": cannot create previews");

    // Fail after binding a back buffer. A throwing constructor has no Impl
    // destructor, yet the very next owner must be able to reuse the HWND.
    bool injected{};
    iPhoneMirror::renderer::PreviewTestHooks hooks;
    hooks.after_initialize = [&] {
        injected = true;
        throw std::runtime_error("injected preview initialization failure");
    };
    try {
        iPhoneMirror::renderer::D3D11PreviewRenderer preview(main.handle,
            [] { return nullptr; }, std::move(hooks));
        throw std::runtime_error("Initialization fault was not injected");
    } catch (const std::exception& error) {
        require(injected && std::string(error.what()) == "injected preview initialization failure",
            name + ": constructor failed before the intended fault boundary");
    }

    {
        Session first(L"preview-ownership-first", 80);
        Session second(L"preview-ownership-second", 180);
        require_api(im_session_attach_preview(first.handle, companion.handle), "attach companion");
        pump_window(companion.handle, name + " companion");
        for (unsigned iteration = 0; iteration < 8; ++iteration) {
            require_api(im_attach_preview_window(main.handle), "attach empty legacy owner");
            require_api(im_session_attach_preview(first.handle, main.handle), "legacy to first session");
            require_api(im_session_attach_preview(first.handle, main.handle), "reuse first owner");
            require_owner(first.handle, main.handle, true);
            pump_window(main.handle, name + " first owner");
            require_api(im_session_attach_preview(second.handle, main.handle), "first to second session");
            require_owner(first.handle, main.handle, false);
            require_owner(first.handle, companion.handle, true);
            pump_window(main.handle, name + " second owner");
            pump_window(companion.handle, name + " isolated companion");
            require_api(im_attach_preview_window(main.handle), "second to legacy owner");
            require_owner(second.handle, main.handle, false);
        }
        im_detach_preview_window();
    }

    // Stop/destroy race another session's attachment on the same HWND.
    for (const bool stop : {true, false}) {
        Session replacement(L"preview-ownership-replacement", 180);
        for (unsigned iteration = 0; iteration < 8; ++iteration) {
            Session old(L"preview-ownership-old", 80);
            require_api(im_session_attach_preview(old.handle, main.handle), "attach retiring owner");
            const auto old_handle = old.handle;
            run_worker([&] {
                std::atomic_bool retiring_started{};
                std::exception_ptr retiring_failure;
                std::thread retiring([&] {
                    retiring_started.store(true, std::memory_order_release);
                    try {
                        if (stop) require_api(im_session_stop(old_handle), "stop retiring owner");
                        else im_session_destroy(old_handle);
                    } catch (...) { retiring_failure = std::current_exception(); }
                });
                while (!retiring_started.load(std::memory_order_acquire)) std::this_thread::yield();
                // Always join the retiring operation, including an attach failure.
                std::exception_ptr failure;
                try {
                    require_api(im_session_attach_preview(replacement.handle, main.handle),
                        "attach while previous owner retires");
                } catch (...) { failure = std::current_exception(); }
                retiring.join();
                if (retiring_failure) std::rethrow_exception(retiring_failure);
                if (failure) std::rethrow_exception(failure);
            });
            if (!stop) old.handle = 0;
            require_owner(replacement.handle, main.handle, true);
            pump_window(main.handle, name + " concurrent replacement");
        }
    }

    for (unsigned iteration = 0; iteration < 4; ++iteration) {
        Session old(L"preview-ownership-shutdown", 80);
        require_api(im_session_attach_preview(old.handle, main.handle), "attach before shutdown");
        const auto old_handle = old.handle;
        run_worker([&] {
            std::atomic_bool closing_started{};
            std::thread closing([&] {
                closing_started.store(true, std::memory_order_release);
                im_shutdown();
            });
            while (!closing_started.load(std::memory_order_acquire)) std::this_thread::yield();
            // This can linearize before or after shutdown. After both finish,
            // no attachment may survive, even if this call initially succeeded.
            (void)im_session_attach_preview(old_handle, main.handle);
            closing.join();
        });
        old.handle = 0;
        require(im_session_attach_preview(old_handle, main.handle) != 0,
            "Closed core accepted a preview attachment");
        require(im_attach_preview_window(main.handle) != 0,
            "Closed core accepted a legacy preview attachment");
        require_api(im_initialize(), "reinitialize core");
        require_api(im_attach_preview_window(main.handle), "reattach after shutdown");
        im_detach_preview_window();
    }
    std::cout << name << ": constructor rollback, public API handoff, companion isolation, "
        "concurrent stop/destroy, and shutdown/reinitialize passed\n";
}
} // namespace

// Synthetic CPU frames only; no receiver, USB device or audio output is started.
int run_preview_ownership_smoke() {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        WNDCLASSW type{};
        type.lpfnWndProc = DefWindowProcW;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"PreviewOwnershipSmoke";
        require(RegisterClassW(&type) != 0, "Cannot register preview ownership test class");
        RECT work{};
        require(SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0) != FALSE,
            "Cannot locate preview ownership test area");
        require_api(im_initialize(), "initialize core");
        for (const bool composition : {false, true}) run_surface(composition, work, type.lpszClassName);
        im_shutdown();
        UnregisterClassW(type.lpszClassName, type.hInstance);
        std::cout << "Preview ownership smoke passed (public API ownership).\n";
        return 0;
    } catch (const std::exception& error) {
        im_shutdown();
        std::cerr << error.what() << '\n';
        return 1;
    }
}
