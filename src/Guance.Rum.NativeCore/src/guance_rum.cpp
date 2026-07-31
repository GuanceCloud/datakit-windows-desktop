#include "rum_core.h"

#include "native_crash_reporter.h"

#include <memory>
#include <string>

using guance::rum::RumCore;

extern "C" {

void guance_rum_config_init(guance_rum_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_rum_config{};
    config->service_name = "df_rum_windows_native";
    config->env = "prod";
    config->version = "1.0.0";
    config->sample_rate = 1.0;
    config->session_error_sample_rate = 0.0;
    config->session_replay_sample_rate = 1.0;
    config->session_replay_on_error_sample_rate = 0.0;
    config->max_queue_items = 100000;
    config->max_queue_bytes = 64LL * 1024 * 1024;
    config->http_timeout_ms = 10000;
    config->session_replay_segment_record_limit = 500;
    config->session_replay_segment_bytes_limit = 1024 * 1024;
}

void guance_rum_native_monitoring_config_init(
    guance_rum_native_monitoring_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_rum_native_monitoring_config{};
    config->struct_size = sizeof(guance_rum_native_monitoring_config);
    config->version = GUANCE_RUM_NATIVE_MONITORING_CONFIG_VERSION;
    config->ui_probe_interval_ms = 250;
    config->long_task_threshold_ms = 500;
    config->hang_threshold_ms = 5'000;
    config->hang_report_cooldown_ms = 5'000;
    config->max_crash_files = 3;
    config->max_crash_file_bytes = 32LL * 1024 * 1024;
}

guance_rum_handle guance_rum_init(const guance_rum_config* config) {
    try {
        return new RumCore(guance::rum::from_c_config(config));
    } catch (...) {
        return nullptr;
    }
}

void guance_rum_shutdown(guance_rum_handle handle) {
    if (handle == nullptr) {
        return;
    }
    auto* core = static_cast<RumCore*>(handle);
    core->shutdown();
    delete core;
}

void guance_rum_flush(guance_rum_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->flush();
    }
}

int guance_rum_get_diagnostics(guance_rum_handle handle, guance_rum_diagnostics* diagnostics) {
    if (handle == nullptr || diagnostics == nullptr) {
        return 0;
    }

    const auto snapshot = static_cast<RumCore*>(handle)->diagnostics();
    diagnostics->rum_events_enqueued = snapshot.rum_events_enqueued;
    diagnostics->rum_upload_success_count = snapshot.rum_upload_success_count;
    diagnostics->rum_upload_retry_count = snapshot.rum_upload_retry_count;
    diagnostics->rum_upload_terminal_failure_count = snapshot.rum_upload_terminal_failure_count;
    diagnostics->replay_upload_success_count = snapshot.replay_upload_success_count;
    diagnostics->replay_upload_retry_count = snapshot.replay_upload_retry_count;
    diagnostics->replay_upload_terminal_failure_count = snapshot.replay_upload_terminal_failure_count;
    diagnostics->last_rum_upload_status_code = snapshot.last_rum_upload_status_code;
    diagnostics->last_replay_upload_status_code = snapshot.last_replay_upload_status_code;
    diagnostics->last_rum_upload_error_code = snapshot.last_rum_upload_error_code;
    diagnostics->last_replay_upload_error_code = snapshot.last_replay_upload_error_code;
    diagnostics->last_rum_upload_latency_ms = snapshot.last_rum_upload_latency_ms;
    diagnostics->last_replay_upload_latency_ms = snapshot.last_replay_upload_latency_ms;
    diagnostics->session_sampled = snapshot.session_sampled ? 1 : 0;
    diagnostics->session_error_sampled = snapshot.session_error_sampled ? 1 : 0;
    diagnostics->session_replay_sampled = snapshot.session_replay_sampled ? 1 : 0;
    diagnostics->session_replay_error_sampled = snapshot.session_replay_error_sampled ? 1 : 0;
    return 1;
}

int guance_rum_write_line(guance_rum_handle handle, const char* line, size_t length) {
    if (handle == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->write_line(line, length) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_rum_enable_native_monitoring(
    guance_rum_handle handle,
    const guance_rum_native_monitoring_config* config) {
    if (handle == nullptr || config == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->enable_native_monitoring(*config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

void guance_rum_disable_native_monitoring(guance_rum_handle handle) {
    if (handle == nullptr) {
        return;
    }
    try {
        static_cast<RumCore*>(handle)->disable_native_monitoring();
    } catch (...) {
        // Public C ABI calls must not propagate C++ exceptions.
    }
}

void guance_rum_capture_cpp_terminate(void) {
    guance::rum::NativeCrashReporter::capture_cpp_terminate_now();
}

void guance_rum_set_user(guance_rum_handle handle, const char* id, const char* name, const char* email) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_user(id, name, email);
    }
}

void guance_rum_clear_user(guance_rum_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->clear_user();
    }
}

void guance_rum_add_global_context(guance_rum_handle handle, const char* key, const char* value) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_global_context(key, value);
    }
}

void guance_rum_add_rum_context(guance_rum_handle handle, const char* key, const char* value) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_rum_context(key, value);
    }
}

void guance_rum_start_view(guance_rum_handle handle, const char* name) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->start_view(name);
    }
}

void guance_rum_stop_view(guance_rum_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_view();
    }
}

void guance_rum_add_action(guance_rum_handle handle, const char* name, const char* type, int64_t duration_ns) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_action(name, type, duration_ns);
    }
}

void guance_rum_add_launch_action(
    guance_rum_handle handle,
    const guance_rum_launch* launch) {
    if (handle == nullptr || launch == nullptr) {
        return;
    }
    try {
        static_cast<RumCore*>(handle)->add_launch_action(*launch);
    } catch (...) {
        // Public C ABI calls must not propagate C++ exceptions.
    }
}

const char* guance_rum_start_action(guance_rum_handle handle, const char* name, const char* type) {
    thread_local std::string last_id;
    if (handle == nullptr) {
        last_id.clear();
        return last_id.c_str();
    }
    last_id = static_cast<RumCore*>(handle)->start_action(name, type);
    return last_id.c_str();
}

void guance_rum_stop_action(guance_rum_handle handle, const char* action_id) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_action(action_id);
    }
}

const char* guance_rum_start_resource(guance_rum_handle handle, const char* url, const char* method) {
    thread_local std::string last_id;
    if (handle == nullptr) {
        last_id.clear();
        return last_id.c_str();
    }
    last_id = static_cast<RumCore*>(handle)->start_resource(url, method);
    return last_id.c_str();
}

void guance_rum_stop_resource(guance_rum_handle handle, const char* resource_id, int status_code, int64_t response_size) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_resource(resource_id, status_code, response_size);
    }
}

void guance_rum_stop_resource_ext(
    guance_rum_handle handle,
    const char* resource_id,
    int status_code,
    int64_t response_size,
    int64_t request_size,
    const char* resource_type,
    const char* trace_id,
    const char* span_id,
    const char* http_protocol) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_resource_ext(resource_id, status_code, response_size, request_size, resource_type, trace_id, span_id, http_protocol);
    }
}

void guance_rum_add_error(guance_rum_handle handle, const char* stack, const char* message, const char* error_type, const char* source) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_error(stack, message, error_type, source);
    }
}

void guance_rum_add_long_task(guance_rum_handle handle, int64_t duration_ns, const char* stack) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_long_task(duration_ns, stack);
    }
}

void guance_rum_start_session_replay(guance_rum_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->start_session_replay();
    }
}

void guance_rum_stop_session_replay(guance_rum_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_session_replay();
    }
}

void guance_rum_register_replay_window(guance_rum_handle handle, uintptr_t hwnd) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->register_replay_window(hwnd);
    }
}

void guance_rum_capture_replay_click(guance_rum_handle handle, uintptr_t hwnd, const char* target, double x, double y) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_click(hwnd, target, x, y);
    }
}

void guance_rum_capture_replay_input(guance_rum_handle handle, uintptr_t hwnd, const char* target) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_input(hwnd, target);
    }
}

void guance_rum_capture_replay_resize(guance_rum_handle handle, uintptr_t hwnd, const char* target, double width, double height) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_resize(hwnd, target, width, height);
    }
}

void guance_rum_set_session_replay_text_privacy(guance_rum_handle handle, uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_text_privacy(hwnd, privacy);
    }
}

void guance_rum_set_session_replay_touch_privacy(guance_rum_handle handle, uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_touch_privacy(hwnd, privacy);
    }
}

void guance_rum_set_session_replay_hidden(guance_rum_handle handle, uintptr_t hwnd, int hidden) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_hidden(hwnd, hidden != 0);
    }
}

}
