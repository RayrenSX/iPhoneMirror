#pragma once

#include <atomic>

namespace iPhoneMirror::virtual_camera {

inline std::atomic_ulong module_object_count{};

struct ModuleObject {
    ModuleObject() noexcept { module_object_count.fetch_add(1, std::memory_order_relaxed); }
    ModuleObject(const ModuleObject&) = delete;
    ModuleObject& operator=(const ModuleObject&) = delete;
    virtual ~ModuleObject() {
        module_object_count.fetch_sub(1, std::memory_order_relaxed);
    }
};

} // namespace iPhoneMirror::virtual_camera
