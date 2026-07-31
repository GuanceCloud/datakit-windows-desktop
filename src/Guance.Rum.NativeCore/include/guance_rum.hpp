#pragma once

#include "guance_rum.h"

#include <atomic>
#include <cstdlib>
#include <exception>
#include <mutex>

namespace guance::rum::detail {

struct CppTerminateBridgeState {
    std::mutex mutex;
    std::atomic<std::terminate_handler> previous{nullptr};
    guance_rum_handle handle = nullptr;
};

inline CppTerminateBridgeState& cpp_terminate_bridge_state() {
    static CppTerminateBridgeState state;
    return state;
}

[[noreturn]] inline void cpp_terminate_bridge_handler() {
    guance_rum_capture_cpp_terminate();
    const auto previous = cpp_terminate_bridge_state().previous.load();
    if (previous != nullptr && previous != &cpp_terminate_bridge_handler) {
        previous();
    }
    std::abort();
}

inline void restore_cpp_terminate_bridge(guance_rum_handle handle) {
    auto& state = cpp_terminate_bridge_state();
    std::lock_guard lock(state.mutex);
    if (state.handle != handle) {
        return;
    }
    const auto previous = state.previous.load();
    if (std::get_terminate() == &cpp_terminate_bridge_handler) {
        std::set_terminate(previous);
    }
    state.previous.store(nullptr);
    state.handle = nullptr;
}

inline int enable_native_monitoring_cpp(
    guance_rum_handle handle,
    const guance_rum_native_monitoring_config* config) {
    const int enabled = (guance_rum_enable_native_monitoring)(handle, config);
    if (!enabled || config == nullptr) {
        return enabled;
    }
    if (!config->enable_native_crash_reporting) {
        restore_cpp_terminate_bridge(handle);
        return enabled;
    }

    auto& state = cpp_terminate_bridge_state();
    std::lock_guard lock(state.mutex);
    if (state.handle == handle && std::get_terminate() == &cpp_terminate_bridge_handler) {
        return enabled;
    }
    state.previous.store(std::set_terminate(&cpp_terminate_bridge_handler));
    state.handle = handle;
    return enabled;
}

inline void disable_native_monitoring_cpp(guance_rum_handle handle) {
    (guance_rum_disable_native_monitoring)(handle);
    restore_cpp_terminate_bridge(handle);
}

inline void shutdown_cpp(guance_rum_handle handle) {
    restore_cpp_terminate_bridge(handle);
    (guance_rum_shutdown)(handle);
}

} // namespace guance::rum::detail

#define guance_rum_enable_native_monitoring(handle, config) \
    ::guance::rum::detail::enable_native_monitoring_cpp((handle), (config))
#define guance_rum_disable_native_monitoring(handle) \
    ::guance::rum::detail::disable_native_monitoring_cpp((handle))
#define guance_rum_shutdown(handle) \
    ::guance::rum::detail::shutdown_cpp((handle))
