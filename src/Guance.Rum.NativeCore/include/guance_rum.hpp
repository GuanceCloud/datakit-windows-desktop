#pragma once

#include "guance_rum.h"

#include <atomic>
#include <cstdlib>
#include <exception>
#include <mutex>
#include <string>
#include <utility>

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

namespace guance::rum {

enum class ResourceCollectionKind {
    manual,
    automatic
};

// The SDK handle is non-owning and must remain valid until this scope is
// completed or destroyed. A scope must not be accessed concurrently.
class ResourceScope final {
public:
    ResourceScope(
        guance_rum_handle handle,
        const char* url,
        const char* method,
        const char* resource_type = "native",
        ResourceCollectionKind kind = ResourceCollectionKind::manual) noexcept
        : handle_(handle) {
        const char* started_id = nullptr;
        try {
            resource_type_ = resource_type == nullptr ? std::string{} : std::string(resource_type);
            started_id = kind == ResourceCollectionKind::automatic
                ? (guance_rum_start_auto_resource)(handle_, url, method)
                : (guance_rum_start_resource)(handle_, url, method);
            if (started_id != nullptr) {
                resource_id_ = started_id;
            }
        } catch (...) {
            if (started_id != nullptr && started_id[0] != '\0') {
                (guance_rum_stop_resource)(handle_, started_id, 0, -1);
            }
            handle_ = nullptr;
            resource_id_.clear();
            resource_type_.clear();
        }
    }

    ~ResourceScope() {
        fail();
    }

    ResourceScope(const ResourceScope&) = delete;
    ResourceScope& operator=(const ResourceScope&) = delete;

    ResourceScope(ResourceScope&& other) noexcept
        : handle_(std::exchange(other.handle_, nullptr)),
          resource_id_(std::move(other.resource_id_)),
          resource_type_(std::move(other.resource_type_)) {
        other.resource_id_.clear();
        other.resource_type_.clear();
    }

    ResourceScope& operator=(ResourceScope&& other) noexcept {
        if (this == &other) {
            return *this;
        }
        fail();
        handle_ = std::exchange(other.handle_, nullptr);
        resource_id_ = std::move(other.resource_id_);
        resource_type_ = std::move(other.resource_type_);
        other.resource_id_.clear();
        other.resource_type_.clear();
        return *this;
    }

    [[nodiscard]] bool active() const noexcept {
        return handle_ != nullptr && !resource_id_.empty();
    }

    [[nodiscard]] const char* id() const noexcept {
        return resource_id_.c_str();
    }

    void complete(
        int status_code,
        int64_t response_size = -1,
        int64_t request_size = -1,
        const char* trace_id = nullptr,
        const char* span_id = nullptr,
        const char* http_protocol = nullptr) noexcept {
        if (!active()) {
            return;
        }
        (guance_rum_stop_resource_ext)(
            handle_,
            resource_id_.c_str(),
            status_code,
            response_size,
            request_size,
            resource_type_.empty() ? nullptr : resource_type_.c_str(),
            trace_id,
            span_id,
            http_protocol);
        handle_ = nullptr;
        resource_id_.clear();
    }

    void fail() noexcept {
        complete(0);
    }

private:
    guance_rum_handle handle_ = nullptr;
    std::string resource_id_;
    std::string resource_type_;
};

} // namespace guance::rum

#define guance_rum_enable_native_monitoring(handle, config) \
    ::guance::rum::detail::enable_native_monitoring_cpp((handle), (config))
#define guance_rum_disable_native_monitoring(handle) \
    ::guance::rum::detail::disable_native_monitoring_cpp((handle))
#define guance_rum_shutdown(handle) \
    ::guance::rum::detail::shutdown_cpp((handle))
