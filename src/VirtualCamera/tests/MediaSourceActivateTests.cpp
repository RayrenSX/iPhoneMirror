#include "VirtualCameraShared.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <wrl.h>
#include <array>
#include <cstdio>
#include <thread>

using Microsoft::WRL::ComPtr;
using namespace iPhoneMirror::virtual_camera;

namespace {

int failures{};

void check(bool value, const char* message) {
    if (value) return;
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
}

ComPtr<IMFActivate> create_activate(IClassFactory* factory) {
    ComPtr<IMFActivate> activate;
    check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&activate))),
        "create activation object");
    // An absent explicit channel avoids discovering a running publisher.
    if (activate != nullptr)
        check(SUCCEEDED(activate->SetString(FrameChannelPathAttribute,
            L"C:\\iPhoneMirror-activation-test-absent\\frame")),
            "set isolated absent frame channel");
    return activate;
}

bool same_identity(IUnknown* first, IUnknown* second) {
    ComPtr<IUnknown> first_identity, second_identity;
    return first != nullptr && second != nullptr &&
        SUCCEEDED(first->QueryInterface(IID_PPV_ARGS(&first_identity))) &&
        SUCCEEDED(second->QueryInterface(IID_PPV_ARGS(&second_identity))) &&
        first_identity == second_identity;
}

void test_cached_source(IClassFactory* factory) {
    auto activate = create_activate(factory);
    if (activate == nullptr) return;
    ComPtr<IMFMediaSource> first, second, replacement;
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&first))), "activate first source");
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&second))), "activate again");
    check(same_identity(first.Get(), second.Get()), "repeated activation returns the same COM object");
    ComPtr<IMFActivate> unsupported;
    check(activate->ActivateObject(IID_PPV_ARGS(&unsupported)) == E_NOINTERFACE &&
        unsupported == nullptr, "failed QI leaves the cached source in place");
    DWORD flags{};
    check(first != nullptr && SUCCEEDED(first->GetCharacteristics(&flags)),
        "failed QI does not shut down the cached source");
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown cached source");
    check(first != nullptr && first->GetCharacteristics(&flags) == MF_E_SHUTDOWN,
        "ShutdownObject shuts down the first activation");
    check(second != nullptr && second->GetCharacteristics(&flags) == MF_E_SHUTDOWN,
        "ShutdownObject shuts down every returned reference");
    check(SUCCEEDED(activate->ShutdownObject()), "repeat empty shutdown");
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&replacement))),
        "create a new source after shutdown");
    check(!same_identity(first.Get(), replacement.Get()),
        "shutdown clears the cached source");
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown replacement source");
    // Also clean up objects returned by a regressed implementation.
    if (first != nullptr) first->Shutdown();
    if (second != nullptr) second->Shutdown();
    if (replacement != nullptr) replacement->Shutdown();
}

void test_detach_source(IClassFactory* factory) {
    auto activate = create_activate(factory);
    if (activate == nullptr) return;
    ComPtr<IMFMediaSource> detached, replacement;
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&detached))),
        "activate source to detach");
    check(SUCCEEDED(activate->DetachObject()), "detach source");
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown empty activator after detach");
    DWORD flags{};
    check(detached != nullptr && SUCCEEDED(detached->GetCharacteristics(&flags)),
        "detached source remains owned by its caller");
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&replacement))),
        "activate after detach");
    check(!same_identity(detached.Get(), replacement.Get()), "detach clears the cache");
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown post-detach source");
    check(detached != nullptr && SUCCEEDED(detached->GetCharacteristics(&flags)),
        "new activation shutdown does not shut down detached source");
    if (detached != nullptr) detached->Shutdown();
    if (replacement != nullptr) replacement->Shutdown();
}

void test_parallel_activation(IClassFactory* factory) {
    auto activate = create_activate(factory);
    if (activate == nullptr) return;
    std::array<ComPtr<IMFMediaSource>, 8> sources;
    std::array<HRESULT, 8> results{};
    std::array<std::thread, 8> workers;
    for (std::size_t index = 0; index < workers.size(); ++index) {
        workers[index] = std::thread([&, index] {
            const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            results[index] = FAILED(initialized) ? initialized :
                activate->ActivateObject(IID_PPV_ARGS(&sources[index]));
            if (SUCCEEDED(initialized)) CoUninitialize();
        });
    }
    for (auto& worker : workers) worker.join();
    for (std::size_t index = 0; index < sources.size(); ++index) {
        check(SUCCEEDED(results[index]), "parallel activation succeeds");
        check(same_identity(sources.front().Get(), sources[index].Get()),
            "parallel activation creates exactly one source");
    }
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown parallel activation");
    DWORD flags{};
    for (auto& source : sources) {
        check(source != nullptr && source->GetCharacteristics(&flags) == MF_E_SHUTDOWN,
            "all parallel references observe shutdown");
        if (source != nullptr) source->Shutdown();
    }
}

void test_failed_initial_query(IClassFactory* factory) {
    auto activate = create_activate(factory);
    if (activate == nullptr) return;
    ComPtr<IMFActivate> unsupported;
    check(activate->ActivateObject(IID_PPV_ARGS(&unsupported)) == E_NOINTERFACE &&
        unsupported == nullptr, "unsupported first interface fails without publishing a source");
    ComPtr<IMFMediaSource> source;
    check(SUCCEEDED(activate->ActivateObject(IID_PPV_ARGS(&source))),
        "activate successfully after initial QI failure");
    check(SUCCEEDED(activate->ShutdownObject()), "shutdown after failed QI recovery");
    if (source != nullptr) source->Shutdown();
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return 2;
    if (FAILED(MFStartup(MF_VERSION))) {
        CoUninitialize();
        return 2;
    }
    const auto module = LoadLibraryW(argv[1]);
    check(module != nullptr, "load virtual camera DLL without registration");
    if (module != nullptr) {
        using GetClassObject = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
        using CanUnload = HRESULT (STDAPICALLTYPE*)();
        const auto get_class = reinterpret_cast<GetClassObject>(
            GetProcAddress(module, "DllGetClassObject"));
        const auto can_unload = reinterpret_cast<CanUnload>(
            GetProcAddress(module, "DllCanUnloadNow"));
        ComPtr<IClassFactory> factory;
        check(get_class != nullptr && can_unload != nullptr &&
            SUCCEEDED(get_class(MediaSourceClsid, IID_PPV_ARGS(&factory))),
            "create COM class factory");
        if (factory != nullptr) {
            test_cached_source(factory.Get());
            test_detach_source(factory.Get());
            test_parallel_activation(factory.Get());
            test_failed_initial_query(factory.Get());
        }
        factory.Reset();
        const bool unloaded = can_unload != nullptr && can_unload() == S_OK;
        check(unloaded, "all activation paths release source cycles and worker threads");
        // Keep a regressed DLL loaded until process exit if it leaked workers.
        if (unloaded) FreeLibrary(module);
    }
    MFShutdown();
    CoUninitialize();
    if (failures == 0) std::puts("Media source activation tests passed.");
    return failures == 0 ? 0 : 1;
}
