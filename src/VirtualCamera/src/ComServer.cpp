#include "MediaSourceActivate.h"

#include "ModuleState.h"
#include "VirtualCameraShared.h"

#include <windows.h>
#include <wrl.h>

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::virtual_camera {
namespace {

class ClassFactory final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IClassFactory>,
      public ModuleObject {
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid,
                                 void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        ComPtr<MediaSourceActivate> activate;
        HRESULT hr = Microsoft::WRL::MakeAndInitialize<MediaSourceActivate>(
            &activate);
        return FAILED(hr) ? hr : activate->QueryInterface(riid, object);
    }

    IFACEMETHODIMP LockServer(BOOL lock) override {
        if (lock)
            module_object_count.fetch_add(1, std::memory_order_relaxed);
        else
            module_object_count.fetch_sub(1, std::memory_order_relaxed);
        return S_OK;
    }
};

} // namespace
} // namespace iPhoneMirror::virtual_camera

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID class_id,
                                                REFIID riid,
                                                void** object) {
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    if (class_id != iPhoneMirror::virtual_camera::MediaSourceClsid)
        return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = Microsoft::WRL::Make<
        iPhoneMirror::virtual_camera::ClassFactory>();
    return factory == nullptr ? E_OUTOFMEMORY
                              : factory->QueryInterface(riid, object);
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return iPhoneMirror::virtual_camera::module_object_count.load(
               std::memory_order_relaxed) == 0
        ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
