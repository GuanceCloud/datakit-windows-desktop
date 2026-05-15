#pragma once

#include <stdint.h>

#if defined(_WIN32) && defined(GUANCE_RUM_BUILDING_DLL)
#define GUANCE_RUM_EXPORT __declspec(dllexport)
#elif defined(_WIN32)
#define GUANCE_RUM_EXPORT __declspec(dllimport)
#else
#define GUANCE_RUM_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct guance_rum_config {
    const char* dataway_url;
    const char* datakit_url;
    const char* client_token;
    const char* rum_app_id;
    const char* service_name;
    const char* env;
    const char* version;
    double sample_rate;
    double session_error_sample_rate;
    int session_replay_enabled;
    double session_replay_sample_rate;
    double session_replay_on_error_sample_rate;
    int debug;
    const char* cache_path;
    int max_queue_items;
    int64_t max_queue_bytes;
    int http_timeout_ms;
    const char* proxy_url;
    int session_replay_segment_record_limit;
    int64_t session_replay_segment_bytes_limit;
} guance_rum_config;

typedef struct guance_rum_diagnostics {
    int64_t rum_events_enqueued;
    int64_t rum_upload_success_count;
    int64_t rum_upload_retry_count;
    int64_t rum_upload_terminal_failure_count;
    int64_t replay_upload_success_count;
    int64_t replay_upload_retry_count;
    int64_t replay_upload_terminal_failure_count;
    int64_t last_rum_upload_status_code;
    int64_t last_replay_upload_status_code;
    int64_t last_rum_upload_error_code;
    int64_t last_replay_upload_error_code;
    int64_t last_rum_upload_latency_ms;
    int64_t last_replay_upload_latency_ms;
    int session_sampled;
    int session_error_sampled;
    int session_replay_sampled;
    int session_replay_error_sampled;
} guance_rum_diagnostics;

typedef enum guance_rum_session_replay_text_privacy {
    GUANCE_RUM_REPLAY_TEXT_ALLOW = 0,
    GUANCE_RUM_REPLAY_TEXT_MASK_SENSITIVE_INPUTS = 1,
    GUANCE_RUM_REPLAY_TEXT_MASK_ALL_INPUTS = 2,
    GUANCE_RUM_REPLAY_TEXT_MASK_ALL = 3
} guance_rum_session_replay_text_privacy;

typedef enum guance_rum_session_replay_touch_privacy {
    GUANCE_RUM_REPLAY_TOUCH_SHOW = 0,
    GUANCE_RUM_REPLAY_TOUCH_HIDE = 1
} guance_rum_session_replay_touch_privacy;

typedef void* guance_rum_handle;

GUANCE_RUM_EXPORT void guance_rum_config_init(guance_rum_config* config);
GUANCE_RUM_EXPORT guance_rum_handle guance_rum_init(const guance_rum_config* config);
GUANCE_RUM_EXPORT void guance_rum_shutdown(guance_rum_handle handle);
GUANCE_RUM_EXPORT void guance_rum_flush(guance_rum_handle handle);
GUANCE_RUM_EXPORT int guance_rum_get_diagnostics(guance_rum_handle handle, guance_rum_diagnostics* diagnostics);

GUANCE_RUM_EXPORT void guance_rum_set_user(guance_rum_handle handle, const char* id, const char* name, const char* email);
GUANCE_RUM_EXPORT void guance_rum_clear_user(guance_rum_handle handle);
GUANCE_RUM_EXPORT void guance_rum_add_global_context(guance_rum_handle handle, const char* key, const char* value);
GUANCE_RUM_EXPORT void guance_rum_add_rum_context(guance_rum_handle handle, const char* key, const char* value);

GUANCE_RUM_EXPORT void guance_rum_start_view(guance_rum_handle handle, const char* name);
GUANCE_RUM_EXPORT void guance_rum_stop_view(guance_rum_handle handle);
GUANCE_RUM_EXPORT void guance_rum_add_action(guance_rum_handle handle, const char* name, const char* type, int64_t duration_ns);
GUANCE_RUM_EXPORT const char* guance_rum_start_action(guance_rum_handle handle, const char* name, const char* type);
GUANCE_RUM_EXPORT void guance_rum_stop_action(guance_rum_handle handle, const char* action_id);
GUANCE_RUM_EXPORT const char* guance_rum_start_resource(guance_rum_handle handle, const char* url, const char* method);
GUANCE_RUM_EXPORT void guance_rum_stop_resource(guance_rum_handle handle, const char* resource_id, int status_code, int64_t response_size);
GUANCE_RUM_EXPORT void guance_rum_stop_resource_ext(
    guance_rum_handle handle,
    const char* resource_id,
    int status_code,
    int64_t response_size,
    int64_t request_size,
    const char* resource_type,
    const char* trace_id,
    const char* span_id,
    const char* http_protocol);
GUANCE_RUM_EXPORT void guance_rum_add_error(guance_rum_handle handle, const char* stack, const char* message, const char* error_type, const char* source);
GUANCE_RUM_EXPORT void guance_rum_add_long_task(guance_rum_handle handle, int64_t duration_ns, const char* stack);
GUANCE_RUM_EXPORT void guance_rum_start_session_replay(guance_rum_handle handle);
GUANCE_RUM_EXPORT void guance_rum_stop_session_replay(guance_rum_handle handle);
GUANCE_RUM_EXPORT void guance_rum_register_replay_window(guance_rum_handle handle, uintptr_t hwnd);
GUANCE_RUM_EXPORT void guance_rum_capture_replay_click(guance_rum_handle handle, uintptr_t hwnd, const char* target, double x, double y);
GUANCE_RUM_EXPORT void guance_rum_capture_replay_input(guance_rum_handle handle, uintptr_t hwnd, const char* target);
GUANCE_RUM_EXPORT void guance_rum_capture_replay_resize(guance_rum_handle handle, uintptr_t hwnd, const char* target, double width, double height);
GUANCE_RUM_EXPORT void guance_rum_set_session_replay_text_privacy(guance_rum_handle handle, uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy);
GUANCE_RUM_EXPORT void guance_rum_set_session_replay_touch_privacy(guance_rum_handle handle, uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy);
GUANCE_RUM_EXPORT void guance_rum_set_session_replay_hidden(guance_rum_handle handle, uintptr_t hwnd, int hidden);

#ifdef __cplusplus
}
#endif
