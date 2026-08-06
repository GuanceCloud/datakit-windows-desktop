#include "rum_core.h"

#include "native_crash_reporter.h"

#include <memory>
#include <string>

using guance::rum::RumCore;

extern "C" {

void guance_sdk_config_init(guance_sdk_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_sdk_config{};
    config->service_name = "df_rum_windows_native";
    config->env = "prod";
    config->version = "1.0.0";
    config->sample_rate = 1.0;
    config->session_error_sample_rate = 0.0;
    config->session_replay_sample_rate = 1.0;
    config->session_replay_on_error_sample_rate = 0.0;
    config->max_cache_bytes = 128LL * 1024 * 1024;
    config->max_cache_files = 1024;
    config->max_cache_age_seconds = 7LL * 24 * 60 * 60;
    config->max_batch_items = 50;
    config->max_batch_bytes = 512LL * 1024;
    config->max_upload_bytes_per_second = 256LL * 1024;
    config->upload_burst_bytes = 2LL * 1024 * 1024;
    config->max_upload_requests_per_second = 2.0;
    config->max_upload_batches_per_cycle = 4;
    config->http_timeout_ms = 10000;
    config->session_replay_segment_record_limit = 500;
    config->session_replay_segment_bytes_limit = 1024 * 1024;
}

void guance_sdk_native_monitoring_config_init(
    guance_sdk_native_monitoring_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_sdk_native_monitoring_config{};
    config->struct_size = sizeof(guance_sdk_native_monitoring_config);
    config->version = GUANCE_SDK_NATIVE_MONITORING_CONFIG_VERSION;
    config->ui_probe_interval_ms = 250;
    config->long_task_threshold_ms = 500;
    config->hang_threshold_ms = 5'000;
    config->hang_report_cooldown_ms = 5'000;
    config->max_crash_files = 3;
    config->max_crash_file_bytes = 32LL * 1024 * 1024;
}

void guance_rum_resource_collection_config_init(
    guance_rum_resource_collection_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_rum_resource_collection_config{};
    config->struct_size = sizeof(guance_rum_resource_collection_config);
    config->version = GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION;
    config->enabled = 1;
    config->capture_url_query = 1;
    config->redacted_value = "<redacted>";
    config->redacted_query_parameter_names =
        guance::rum::default_redacted_query_parameter_names();
    config->redacted_query_parameter_name_count =
        guance::rum::default_redacted_query_parameter_name_count();
}

void guance_trace_config_init(guance_trace_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_trace_config{};
    config->struct_size = sizeof(guance_trace_config);
    config->version = GUANCE_TRACE_CONFIG_VERSION;
    config->sample_rate = 1.0;
    config->trace_type = GUANCE_TRACE_DDTRACE;
}

void guance_trace_context_init(guance_trace_context* context) {
    if (context == nullptr) {
        return;
    }
    *context = guance_trace_context{};
    context->struct_size = sizeof(guance_trace_context);
    context->version = GUANCE_TRACE_CONTEXT_VERSION;
}

void guance_log_config_init(guance_log_config* config) {
    if (config == nullptr) {
        return;
    }
    *config = guance_log_config{};
    config->struct_size = sizeof(guance_log_config);
    config->version = GUANCE_LOG_CONFIG_VERSION;
    config->sample_rate = 1.0;
    config->discard_strategy = GUANCE_LOG_DISCARD_NEW;
}

void guance_log_diagnostics_init(guance_log_diagnostics* diagnostics) {
    if (diagnostics == nullptr) {
        return;
    }
    *diagnostics = guance_log_diagnostics{};
    diagnostics->struct_size = sizeof(guance_log_diagnostics);
    diagnostics->version = GUANCE_LOG_DIAGNOSTICS_VERSION;
}

guance_sdk_handle guance_sdk_init(const guance_sdk_config* config) {
    try {
        return new RumCore(guance::rum::from_c_config(config));
    } catch (...) {
        return nullptr;
    }
}

void guance_sdk_shutdown(guance_sdk_handle handle) {
    if (handle == nullptr) {
        return;
    }
    auto* core = static_cast<RumCore*>(handle);
    core->shutdown();
    delete core;
}

void guance_sdk_flush(guance_sdk_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->flush();
    }
}

int guance_sdk_get_diagnostics(guance_sdk_handle handle, guance_sdk_diagnostics* diagnostics) {
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
    diagnostics->cache_allocated_bytes = snapshot.cache_allocated_bytes;
    diagnostics->cache_file_count = snapshot.cache_file_count;
    diagnostics->session_sampled = snapshot.session_sampled ? 1 : 0;
    diagnostics->session_error_sampled = snapshot.session_error_sampled ? 1 : 0;
    diagnostics->session_replay_sampled = snapshot.session_replay_sampled ? 1 : 0;
    diagnostics->session_replay_error_sampled = snapshot.session_replay_error_sampled ? 1 : 0;
    return 1;
}

int guance_sdk_write_line(guance_sdk_handle handle, const char* line, size_t length) {
    if (handle == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->write_line(line, length) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_sdk_enable_native_monitoring(
    guance_sdk_handle handle,
    const guance_sdk_native_monitoring_config* config) {
    if (handle == nullptr || config == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->enable_native_monitoring(*config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_rum_configure_resource_collection(
    guance_sdk_handle handle,
    const guance_rum_resource_collection_config* config) {
    if (handle == nullptr || config == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->configure_resource_collection(*config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_trace_configure(
    guance_sdk_handle handle,
    const guance_trace_config* config) {
    if (handle == nullptr || config == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->configure_trace(*config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_log_configure(
    guance_sdk_handle handle,
    const guance_log_config* config) {
    if (handle == nullptr || config == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->configure_logging(*config) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_log_add(
    guance_sdk_handle handle,
    const char* content,
    const char* status,
    const guance_log_property* properties,
    uint32_t property_count) {
    if (handle == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->add_log(
            content,
            status,
            properties,
            property_count) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

int guance_log_add_batch(
    guance_sdk_handle handle,
    const guance_log_entry* entries,
    uint32_t entry_count) {
    if (handle == nullptr || (entry_count > 0 && entries == nullptr)) {
        return 0;
    }
    int accepted = 0;
    try {
        for (uint32_t index = 0; index < entry_count; ++index) {
            const auto& entry = entries[index];
            if (static_cast<RumCore*>(handle)->add_log(
                    entry.content,
                    entry.status,
                    entry.properties,
                    entry.property_count)) {
                ++accepted;
            }
        }
    } catch (...) {
        return accepted;
    }
    return accepted;
}

int guance_log_get_diagnostics(
    guance_sdk_handle handle,
    guance_log_diagnostics* diagnostics) {
    if (handle == nullptr || diagnostics == nullptr ||
        diagnostics->struct_size < sizeof(guance_log_diagnostics) ||
        diagnostics->version != GUANCE_LOG_DIAGNOSTICS_VERSION) {
        return 0;
    }
    try {
        const auto snapshot = static_cast<RumCore*>(handle)->log_diagnostics();
        diagnostics->logs_enqueued = snapshot.logs_enqueued;
        diagnostics->logs_dropped = snapshot.logs_dropped;
        diagnostics->upload_success_count = snapshot.upload_success_count;
        diagnostics->upload_retry_count = snapshot.upload_retry_count;
        diagnostics->upload_terminal_failure_count = snapshot.upload_terminal_failure_count;
        diagnostics->last_upload_status_code = snapshot.last_upload_status_code;
        diagnostics->last_upload_error_code = snapshot.last_upload_error_code;
        diagnostics->last_upload_latency_ms = snapshot.last_upload_latency_ms;
        return 1;
    } catch (...) {
        return 0;
    }
}

int guance_trace_create_context(
    guance_sdk_handle handle,
    const char* url,
    const char* method,
    guance_trace_context* context) {
    if (context == nullptr) {
        return 0;
    }
    guance_trace_context_init(context);
    if (handle == nullptr) {
        return 0;
    }
    try {
        const auto generated =
            static_cast<RumCore*>(handle)->create_trace_context(url, method);
        return generated.has_value() &&
                guance::rum::trace_context_to_c(*generated, *context)
            ? 1
            : 0;
    } catch (...) {
        return 0;
    }
}

void guance_sdk_disable_native_monitoring(guance_sdk_handle handle) {
    if (handle == nullptr) {
        return;
    }
    try {
        static_cast<RumCore*>(handle)->disable_native_monitoring();
    } catch (...) {
        // Public C ABI calls must not propagate C++ exceptions.
    }
}

void guance_sdk_capture_cpp_terminate(void) {
    guance::rum::NativeCrashReporter::capture_cpp_terminate_now();
}

void guance_sdk_set_user(guance_sdk_handle handle, const char* id, const char* name, const char* email) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_user(id, name, email);
    }
}

void guance_sdk_clear_user(guance_sdk_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->clear_user();
    }
}

void guance_sdk_add_global_context(guance_sdk_handle handle, const char* key, const char* value) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_global_context(key, value);
    }
}

void guance_rum_add_rum_context(guance_sdk_handle handle, const char* key, const char* value) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_rum_context(key, value);
    }
}

void guance_rum_start_view(guance_sdk_handle handle, const char* name) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->start_view(name);
    }
}

void guance_rum_stop_view(guance_sdk_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_view();
    }
}

void guance_rum_add_action(guance_sdk_handle handle, const char* name, const char* type, int64_t duration_ns) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_action(name, type, duration_ns);
    }
}

void guance_rum_add_launch_action(
    guance_sdk_handle handle,
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

const char* guance_rum_start_action(guance_sdk_handle handle, const char* name, const char* type) {
    thread_local std::string last_id;
    if (handle == nullptr) {
        last_id.clear();
        return last_id.c_str();
    }
    last_id = static_cast<RumCore*>(handle)->start_action(name, type);
    return last_id.c_str();
}

void guance_rum_stop_action(guance_sdk_handle handle, const char* action_id) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_action(action_id);
    }
}

const char* guance_rum_start_resource(guance_sdk_handle handle, const char* url, const char* method) {
    thread_local std::string last_id;
    if (handle == nullptr) {
        last_id.clear();
        return last_id.c_str();
    }
    try {
        last_id = static_cast<RumCore*>(handle)->start_resource(url, method);
    } catch (...) {
        last_id.clear();
    }
    return last_id.c_str();
}

const char* guance_rum_start_auto_resource(
    guance_sdk_handle handle,
    const char* url,
    const char* method) {
    thread_local std::string last_id;
    if (handle == nullptr) {
        last_id.clear();
        return last_id.c_str();
    }
    try {
        last_id = static_cast<RumCore*>(handle)->start_auto_resource(url, method);
    } catch (...) {
        last_id.clear();
    }
    return last_id.c_str();
}

void guance_rum_stop_resource(guance_sdk_handle handle, const char* resource_id, int status_code, int64_t response_size) {
    if (handle != nullptr) {
        try {
            static_cast<RumCore*>(handle)->stop_resource(resource_id, status_code, response_size);
        } catch (...) {
            // Public C ABI calls must not propagate C++ exceptions.
        }
    }
}

void guance_rum_stop_resource_ext(
    guance_sdk_handle handle,
    const char* resource_id,
    int status_code,
    int64_t response_size,
    int64_t request_size,
    const char* resource_type,
    const char* trace_id,
    const char* span_id,
    const char* http_protocol) {
    if (handle != nullptr) {
        try {
            static_cast<RumCore*>(handle)->stop_resource_ext(resource_id, status_code, response_size, request_size, resource_type, trace_id, span_id, http_protocol);
        } catch (...) {
            // Public C ABI calls must not propagate C++ exceptions.
        }
    }
}

void guance_rum_add_error(guance_sdk_handle handle, const char* stack, const char* message, const char* error_type, const char* source) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_error(stack, message, error_type, source);
    }
}

void guance_rum_add_long_task(guance_sdk_handle handle, int64_t duration_ns, const char* stack) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->add_long_task(duration_ns, stack);
    }
}

void guance_rum_start_session_replay(guance_sdk_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->start_session_replay();
    }
}

void guance_rum_stop_session_replay(guance_sdk_handle handle) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->stop_session_replay();
    }
}

void guance_rum_register_replay_window(guance_sdk_handle handle, uintptr_t hwnd) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->register_replay_window(hwnd);
    }
}

void guance_rum_capture_replay_click(guance_sdk_handle handle, uintptr_t hwnd, const char* target, double x, double y) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_click(hwnd, target, x, y);
    }
}

void guance_rum_capture_replay_input(guance_sdk_handle handle, uintptr_t hwnd, const char* target) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_input(hwnd, target);
    }
}

void guance_rum_capture_replay_resize(guance_sdk_handle handle, uintptr_t hwnd, const char* target, double width, double height) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->capture_replay_resize(hwnd, target, width, height);
    }
}

int guance_rum_capture_browser_replay_record(
    guance_sdk_handle handle,
    const char* session_id,
    const char* view_id,
    const char* record_json,
    size_t record_json_length,
    int64_t timestamp_ms,
    int is_full_snapshot) {
    if (handle == nullptr) {
        return 0;
    }
    try {
        return static_cast<RumCore*>(handle)->capture_browser_replay_record(
            session_id,
            view_id,
            record_json,
            record_json_length,
            timestamp_ms,
            is_full_snapshot != 0) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

void guance_rum_set_session_replay_text_privacy(guance_sdk_handle handle, uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_text_privacy(hwnd, privacy);
    }
}

void guance_rum_set_session_replay_touch_privacy(guance_sdk_handle handle, uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_touch_privacy(hwnd, privacy);
    }
}

void guance_rum_set_session_replay_hidden(guance_sdk_handle handle, uintptr_t hwnd, int hidden) {
    if (handle != nullptr) {
        static_cast<RumCore*>(handle)->set_session_replay_hidden(hwnd, hidden != 0);
    }
}

}
