#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && defined(GUANCE_WINDOWS_NATIVE_STATIC)
#define GUANCE_WINDOWS_NATIVE_EXPORT
#elif defined(_WIN32) && defined(GUANCE_WINDOWS_NATIVE_BUILDING_DLL)
#define GUANCE_WINDOWS_NATIVE_EXPORT __declspec(dllexport)
#elif defined(_WIN32)
#define GUANCE_WINDOWS_NATIVE_EXPORT __declspec(dllimport)
#else
#define GUANCE_WINDOWS_NATIVE_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

/** @brief Configuration used to initialize one Guance Windows native SDK instance. */
typedef struct guance_sdk_config {
    const char* dataway_url; /**< Public DataWay intake base URL, or NULL for DataKit mode. */
    const char* datakit_url; /**< Local DataKit intake base URL, or NULL for DataWay mode. */
    const char* client_token; /**< DataWay client token; not required for local DataKit intake. */
    const char* rum_app_id; /**< RUM application identifier created in Guance. */
    const char* service_name; /**< Service tag attached to RUM and log data. */
    const char* env; /**< Deployment environment: prod, gray, pre, common, or local. */
    const char* version; /**< Monitored application version. */
    double sample_rate; /**< Normal RUM session sampling rate from 0.0 through 1.0. */
    double session_error_sample_rate; /**< Additional error-session sampling rate. */
    int session_replay_enabled; /**< Non-zero to enable experimental Session Replay. */
    double session_replay_sample_rate; /**< Replay sampling rate for normal sessions. */
    double session_replay_on_error_sample_rate; /**< Additional Replay error-session sampling rate. */
    int debug; /**< Non-zero to enable SDK diagnostic output. */
    const char* cache_path; /**< Optional persistent queue directory. */
    int64_t max_cache_bytes; /**< Shared telemetry cache byte limit. */
    int max_cache_files; /**< Shared telemetry cache file-count limit. */
    int64_t max_cache_age_seconds; /**< Maximum queued batch age in seconds. */
    int max_batch_items; /**< Maximum records per RUM or log batch. */
    int64_t max_batch_bytes; /**< Maximum uncompressed bytes per RUM or log batch. */
    int64_t max_upload_bytes_per_second; /**< Aggregate upload byte rate; zero disables the limit. */
    int64_t upload_burst_bytes; /**< Aggregate upload token-bucket burst size. */
    double max_upload_requests_per_second; /**< Aggregate request rate; zero disables the limit. */
    int max_upload_batches_per_cycle; /**< Maximum batches sent by one scheduled drain. */
    int http_timeout_ms; /**< Intake request timeout in milliseconds. */
    const char* proxy_url; /**< Optional HTTP proxy URL. */
    int session_replay_segment_record_limit; /**< Maximum records per Replay segment. */
    int64_t session_replay_segment_bytes_limit; /**< Maximum uncompressed bytes per Replay segment. */
} guance_sdk_config;

/** @brief Snapshot of native RUM, Replay, cache, and upload diagnostics. */
typedef struct guance_sdk_diagnostics {
    int64_t rum_events_enqueued; /**< Accepted RUM event count. */
    int64_t rum_upload_success_count; /**< Successful RUM upload count. */
    int64_t rum_upload_retry_count; /**< Retried RUM upload count. */
    int64_t rum_upload_terminal_failure_count; /**< Terminal RUM upload failure count. */
    int64_t replay_upload_success_count; /**< Successful Replay upload count. */
    int64_t replay_upload_retry_count; /**< Retried Replay upload count. */
    int64_t replay_upload_terminal_failure_count; /**< Terminal Replay upload failure count. */
    int64_t last_rum_upload_status_code; /**< Last RUM intake HTTP status code. */
    int64_t last_replay_upload_status_code; /**< Last Replay intake HTTP status code. */
    int64_t last_rum_upload_error_code; /**< Last platform error code for a RUM upload. */
    int64_t last_replay_upload_error_code; /**< Last platform error code for a Replay upload. */
    int64_t last_rum_upload_latency_ms; /**< Last RUM upload latency in milliseconds. */
    int64_t last_replay_upload_latency_ms; /**< Last Replay upload latency in milliseconds. */
    int64_t cache_allocated_bytes; /**< Allocation-aware bytes used by telemetry queues. */
    int cache_file_count; /**< Number of telemetry queue files. */
    int session_sampled; /**< Non-zero when the current RUM session is sampled. */
    int session_error_sampled; /**< Non-zero when error-session sampling selected the session. */
    int session_replay_sampled; /**< Non-zero when normal Replay sampling selected the session. */
    int session_replay_error_sampled; /**< Non-zero when Replay error sampling selected the session. */
} guance_sdk_diagnostics;

/** Version written to guance_sdk_native_monitoring_config::version. */
#define GUANCE_SDK_NATIVE_MONITORING_CONFIG_VERSION 1u

/** @brief Versioned configuration for UI-hang monitoring and native crash recovery. */
typedef struct guance_sdk_native_monitoring_config {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_SDK_NATIVE_MONITORING_CONFIG_VERSION. */

    int enable_ui_hang_monitoring; /**< Non-zero to monitor the configured UI window. */
    int enable_native_crash_reporting; /**< Non-zero to recover native crashes on next launch. */

    uintptr_t main_window_handle; /**< HWND owned by the monitored process. */
    int ui_probe_interval_ms; /**< UI responsiveness probe interval in milliseconds. */
    int long_task_threshold_ms; /**< Minimum UI block reported as a Long Task. */
    int hang_threshold_ms; /**< Minimum UI block treated as an application hang. */
    int hang_report_cooldown_ms; /**< Cooldown between repeated hang reports. */

    const char* crash_cache_path; /**< Optional directory for crash envelopes and dumps. */
    int enable_minidump; /**< Non-zero to retain local minidumps; dumps are not uploaded. */
    int max_crash_files; /**< Maximum retained crash artifact count. */
    int64_t max_crash_file_bytes; /**< Maximum total bytes retained for crash artifacts. */
} guance_sdk_native_monitoring_config;

/** Version written to guance_rum_resource_collection_config::version. */
#define GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION 1u

/**
 * @brief Decides whether one HTTP request may be collected as a RUM Resource.
 * @return Non-zero to collect the request; zero to ignore it.
 */
typedef int (*guance_rum_resource_should_collect_callback)(
    const char* url,
    const char* method,
    void* user_data);

/** @brief Configures automatic native HTTP Resource filtering, URL redaction, and HTTP-header privacy.
 *
 * String and name-list values are copied by guance_rum_configure_resource_collection.
 * The callback and user_data are retained and must remain valid until the SDK
 * is reconfigured or shut down. The callback is synchronous and may be called
 * concurrently from multiple request threads.
 */
typedef struct guance_rum_resource_collection_config {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION. */

    int enabled; /**< Non-zero to collect eligible native HTTP Resources. */
    int capture_url_query; /**< Non-zero to retain query strings after redaction. */
    int redact_all_url_query_values; /**< Non-zero to redact every query value. */
    const char* redacted_value; /**< Replacement text for redacted query values. */
    const char* const* redacted_query_parameter_names; /**< Sensitive query parameter names. */
    uint32_t redacted_query_parameter_name_count; /**< Number of sensitive query names. */

    guance_rum_resource_should_collect_callback should_collect; /**< Optional request filter. */
    void* user_data; /**< Opaque value passed to should_collect. */

    int capture_http_headers; /**< Non-zero to retain request and response headers after redaction. */
    const char* const* redacted_header_names; /**< Sensitive HTTP header names. */
    uint32_t redacted_header_name_count; /**< Number of sensitive header names. */
} guance_rum_resource_collection_config;

/** Version written to guance_data_modifier_config::version. */
#define GUANCE_DATA_MODIFIER_CONFIG_VERSION 1u

/** @brief Value kinds exposed to native telemetry modifier callbacks. */
typedef enum guance_data_value_type {
    GUANCE_DATA_VALUE_NULL = 0,
    GUANCE_DATA_VALUE_BOOL = 1,
    GUANCE_DATA_VALUE_INT64 = 2,
    GUANCE_DATA_VALUE_DOUBLE = 3,
    GUANCE_DATA_VALUE_STRING = 4
} guance_data_value_type;

/** @brief One strongly typed telemetry value. String replacements are copied synchronously. */
typedef struct guance_data_value {
    guance_data_value_type type;
    union {
        int bool_value;
        int64_t int64_value;
        double double_value;
        const char* string_value;
    } value;
} guance_data_value;

/** @brief One existing tag or field supplied to a line modifier. */
typedef struct guance_data_item {
    const char* key;
    guance_data_value value;
} guance_data_item;

/**
 * @brief Modifies one existing tag or field.
 * @return Non-zero to apply replacement; zero, or a NULL replacement value, keeps the current value.
 */
typedef int (*guance_data_modifier_callback)(
    const char* key,
    const guance_data_value* value,
    guance_data_value* replacement,
    void* user_data);

/**
 * @brief Modifies existing values from one event in place.
 *
 * The item count and keys are immutable. Setting an item to GUANCE_DATA_VALUE_NULL keeps its
 * current value. Callbacks are synchronous and may be invoked concurrently. They must not re-enter
 * the same SDK instance.
 */
typedef void (*guance_line_data_modifier_callback)(
    const char* measurement,
    guance_data_item* data,
    uint32_t data_count,
    void* user_data);

/**
 * @brief Configures general RUM and log modifiers executed immediately before disk caching.
 *
 * Data modifiers run before line modifiers; configured HTTP privacy rules run last. Callback
 * pointers and user_data are retained until reconfiguration or shutdown. Reconfiguration waits
 * for callbacks already in progress before returning.
 */
typedef struct guance_data_modifier_config {
    uint32_t struct_size;
    uint32_t version;
    guance_data_modifier_callback data_modifier;
    guance_line_data_modifier_callback line_data_modifier;
    void* user_data;
} guance_data_modifier_config;

/** Version written to guance_trace_config::version. */
#define GUANCE_TRACE_CONFIG_VERSION 1u
/** Version written to guance_trace_context::version. */
#define GUANCE_TRACE_CONTEXT_VERSION 1u
/** Maximum propagated header count in one trace context. */
#define GUANCE_TRACE_MAX_HEADERS 4u
/** Capacity of guance_trace_header::name including the null terminator. */
#define GUANCE_TRACE_HEADER_NAME_CAPACITY 64u
/** Capacity of guance_trace_header::value including the null terminator. */
#define GUANCE_TRACE_HEADER_VALUE_CAPACITY 2048u
/** Capacity of trace and span identifier buffers including the null terminator. */
#define GUANCE_TRACE_ID_CAPACITY 128u

/** @brief Supported distributed trace propagation formats. */
typedef enum guance_trace_type {
    GUANCE_TRACE_DDTRACE = 0, /**< Datadog multi-header propagation. */
    GUANCE_TRACE_ZIPKIN_MULTI_HEADER = 1, /**< Zipkin B3 multi-header propagation. */
    GUANCE_TRACE_ZIPKIN_SINGLE_HEADER = 2, /**< Zipkin B3 single-header propagation. */
    GUANCE_TRACE_TRACEPARENT = 3, /**< W3C traceparent propagation. */
    GUANCE_TRACE_SKYWALKING = 4, /**< Apache SkyWalking propagation. */
    GUANCE_TRACE_JAEGER = 5 /**< Jaeger propagation. */
} guance_trace_type;

/** @brief One request header returned in a native trace context. */
typedef struct guance_trace_header {
    char name[GUANCE_TRACE_HEADER_NAME_CAPACITY]; /**< Null-terminated header name. */
    char value[GUANCE_TRACE_HEADER_VALUE_CAPACITY]; /**< Null-terminated header value. */
} guance_trace_header;

/** @brief Generated or application-provided trace headers and RUM identifiers. */
typedef struct guance_trace_context {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_TRACE_CONTEXT_VERSION. */
    int sampled; /**< Non-zero when the propagated sampling decision is enabled. */
    int link_rum_data; /**< Non-zero when identifiers should be attached to the RUM Resource. */
    char trace_id[GUANCE_TRACE_ID_CAPACITY]; /**< Null-terminated trace identifier. */
    char span_id[GUANCE_TRACE_ID_CAPACITY]; /**< Null-terminated span identifier. */
    uint32_t header_count; /**< Number of initialized entries in headers. */
    guance_trace_header headers[GUANCE_TRACE_MAX_HEADERS]; /**< Request headers to inject. */
} guance_trace_context;

/**
 * @brief Decides whether trace headers may be added to one HTTP request.
 * @return Non-zero to trace the request; zero to leave it unchanged.
 */
typedef int (*guance_trace_should_trace_callback)(
    const char* url,
    const char* method,
    void* user_data);

/**
 * @brief Supplies custom headers and identifiers for one HTTP request.
 * Initialize context with guance_trace_context_init before filling it.
 * @return Non-zero to use the supplied context; zero to skip tracing.
 */
typedef int (*guance_trace_context_provider_callback)(
    const char* url,
    const char* method,
    guance_trace_context* context,
    void* user_data);

/** @brief Configures native distributed trace propagation.
 *
 * String values are copied by guance_trace_configure. Callbacks and
 * user_data are retained and must remain valid until reconfiguration or SDK
 * shutdown. Callbacks are synchronous and may run concurrently.
 */
typedef struct guance_trace_config {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_TRACE_CONFIG_VERSION. */
    int enable_auto_trace; /**< Non-zero to enable automatic trace context creation. */
    int enable_link_rum_data; /**< Non-zero to attach identifiers to matching RUM Resources. */
    double sample_rate; /**< Propagated sampling rate from 0.0 through 1.0. */
    guance_trace_type trace_type; /**< Built-in propagation format. */
    const char* service_name; /**< Optional service name used by propagation formats that require it. */
    guance_trace_should_trace_callback should_trace; /**< Optional destination allow-list callback. */
    guance_trace_context_provider_callback context_provider; /**< Optional custom context provider. */
    void* user_data; /**< Opaque value passed to configured callbacks. */
} guance_trace_config;

/** Version written to guance_log_config::version. */
#define GUANCE_LOG_CONFIG_VERSION 2u
/** Version written to guance_log_diagnostics::version. */
#define GUANCE_LOG_DIAGNOSTICS_VERSION 1u

/** @brief Bit values used by guance_log_config::level_filter_mask. */
typedef enum guance_log_level {
    GUANCE_LOG_DEBUG = 1u << 0, /**< Debug diagnostic information. */
    GUANCE_LOG_INFO = 1u << 1, /**< Informational application output. */
    GUANCE_LOG_WARNING = 1u << 2, /**< Recoverable warning. */
    GUANCE_LOG_ERROR = 1u << 3, /**< Application error. */
    GUANCE_LOG_CRITICAL = 1u << 4, /**< Critical or fatal condition. */
    GUANCE_LOG_OK = 1u << 5 /**< Successful or healthy operation. */
} guance_log_level;

/** @brief Queue behavior when the in-memory native log queue is full. */
typedef enum guance_log_discard_strategy {
    GUANCE_LOG_DISCARD_NEW = 0, /**< Reject the newly submitted log. */
    GUANCE_LOG_DISCARD_OLDEST = 1 /**< Remove the oldest log before accepting the new log. */
} guance_log_discard_strategy;

/** @brief One string key-value property attached to a native log. */
typedef struct guance_log_property {
    const char* key; /**< Property name. */
    const char* value; /**< Property value. */
} guance_log_property;

/** @brief Configures native custom log collection.
 *
 * Property and context strings are copied synchronously. A zero level mask
 * accepts every status, including application-defined custom status values.
 */
typedef struct guance_log_config {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_LOG_CONFIG_VERSION. */
    int enable_custom_log; /**< Non-zero to accept custom logs. */
    int enable_link_rum_data; /**< Non-zero to attach active RUM context. */
    double sample_rate; /**< Log sampling rate from 0.0 through 1.0. */
    uint32_t level_filter_mask; /**< Accepted guance_log_level bits; zero accepts every status. */
    const guance_log_property* global_context; /**< Properties attached to every accepted log. */
    uint32_t global_context_count; /**< Number of entries in global_context. */
    guance_log_discard_strategy discard_strategy; /**< Full-queue discard behavior. */
} guance_log_config;

/** @brief One native log entry used by guance_log_add_batch. */
typedef struct guance_log_entry {
    const char* content; /**< Log message. */
    const char* status; /**< Predefined or application-defined status. */
    const guance_log_property* properties; /**< Optional entry properties. */
    uint32_t property_count; /**< Number of entries in properties. */
} guance_log_entry;

/** @brief Versioned snapshot of native log queue and upload diagnostics. */
typedef struct guance_log_diagnostics {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_LOG_DIAGNOSTICS_VERSION. */
    int64_t logs_enqueued; /**< Accepted log count. */
    int64_t logs_dropped; /**< Logs rejected by configuration, sampling, level, or capacity. */
    int64_t upload_success_count; /**< Successful log upload count. */
    int64_t upload_retry_count; /**< Retried log upload count. */
    int64_t upload_terminal_failure_count; /**< Terminal log upload failure count. */
    int64_t last_upload_status_code; /**< Last Logging intake HTTP status code. */
    int64_t last_upload_error_code; /**< Last platform error code for a log upload. */
    int64_t last_upload_latency_ms; /**< Last log upload latency in milliseconds. */
} guance_log_diagnostics;

/** @brief Application launch category used by guance_rum_launch. */
typedef enum guance_rum_launch_type {
    GUANCE_RUM_LAUNCH_COLD = 0, /**< Process cold start. */
    GUANCE_RUM_LAUNCH_HOT = 1 /**< Existing-process activation. */
} guance_rum_launch_type;

/** @brief Measured application launch phases reported as a RUM Action. */
typedef struct guance_rum_launch {
    guance_rum_launch_type type; /**< Cold or hot launch. */
    int64_t start_time_ns; /**< Unix start timestamp in nanoseconds. */
    int64_t duration_ns; /**< Total launch duration in nanoseconds. */
    int64_t pre_application_duration_ns; /**< Time before application code ran. */
    int64_t application_duration_ns; /**< Application initialization duration. */
    int64_t first_frame_duration_ns; /**< First-frame duration. */
} guance_rum_launch;

/** @brief Session Replay text and input masking behavior for a native window. */
typedef enum guance_rum_session_replay_text_privacy {
    GUANCE_RUM_REPLAY_TEXT_ALLOW = 0, /**< Record text and input values. */
    GUANCE_RUM_REPLAY_TEXT_MASK_SENSITIVE_INPUTS = 1, /**< Mask recognized sensitive inputs. */
    GUANCE_RUM_REPLAY_TEXT_MASK_ALL_INPUTS = 2, /**< Mask every input value. */
    GUANCE_RUM_REPLAY_TEXT_MASK_ALL = 3 /**< Mask all text and input values. */
} guance_rum_session_replay_text_privacy;

/** @brief Session Replay pointer and touch privacy for a native window. */
typedef enum guance_rum_session_replay_touch_privacy {
    GUANCE_RUM_REPLAY_TOUCH_SHOW = 0, /**< Record pointer and touch interactions. */
    GUANCE_RUM_REPLAY_TOUCH_HIDE = 1 /**< Suppress pointer and touch details. */
} guance_rum_session_replay_touch_privacy;

/** Version written to guance_electron_bridge_server_options::version. */
#define GUANCE_ELECTRON_BRIDGE_SERVER_OPTIONS_VERSION 1u

/** @brief Configures an in-process Electron Bridge Server for an existing SDK Handle.
 *
 * The server copies every option during start. It borrows the SDK Handle, which
 * must remain valid until the server is stopped. Capability fields describe
 * the application-owned SDK configuration returned to the Electron adapter.
 */
typedef struct guance_electron_bridge_server_options {
    uint32_t struct_size; /**< Size of this structure in bytes. */
    uint32_t version; /**< GUANCE_ELECTRON_BRIDGE_SERVER_OPTIONS_VERSION. */
    const char* pipe_name; /**< Local named-pipe identifier without the Windows path prefix. */
    uint32_t max_message_bytes; /**< Maximum private protocol line size accepted from Electron. */
    int logging_enabled; /**< Non-zero when Browser Log forwarding is configured. */
    int session_replay_enabled; /**< Non-zero when Browser Replay forwarding is configured. */
    const char* replay_privacy_level; /**< allow, mask-user-input, or mask. */
    int trace_enabled; /**< Non-zero when Browser trace propagation is configured. */
    double trace_sample_rate; /**< Trace sampling rate from 0.0 through 1.0. */
    const char* trace_type; /**< Browser trace propagation type returned in the handshake. */
    const char* trace_allowed_urls; /**< Browser trace destination policy returned in the handshake. */
    int debug; /**< Non-zero to emit Bridge Server diagnostic output. */
} guance_electron_bridge_server_options;

/** @brief Opaque handle returned by guance_sdk_init. */
typedef void* guance_sdk_handle;

/** @brief Opaque token returned by guance_electron_bridge_server_start. */
typedef void* guance_electron_bridge_server_handle;

/** Initializes guance_sdk_config with supported defaults before application overrides. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_config_init(guance_sdk_config* config);
/** Returns the immutable SDK version compiled into the native runtime. */
GUANCE_WINDOWS_NATIVE_EXPORT const char* guance_sdk_get_version(void);
/** Creates an SDK instance. Returns NULL when configuration or runtime initialization fails. */
GUANCE_WINDOWS_NATIVE_EXPORT guance_sdk_handle guance_sdk_init(const guance_sdk_config* config);
/** Flushes queued telemetry, releases the SDK instance, and invalidates the handle. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_shutdown(guance_sdk_handle handle);
/** Performs a best-effort synchronous drain of currently queued telemetry. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_flush(guance_sdk_handle handle);
/** Copies current RUM, Replay, cache, and upload counters into diagnostics. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_sdk_get_diagnostics(guance_sdk_handle handle, guance_sdk_diagnostics* diagnostics);
/** Enqueues one complete line-protocol record. Returns non-zero when accepted. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_sdk_write_line(guance_sdk_handle handle, const char* line, size_t length);
/**
 * Writes one validated Electron Native Bridge message to an existing SDK handle.
 * The message must not contain a trailing newline. Supported inputs are Browser
 * RUM line protocol plus the private launch, error, log, and Replay commands.
 */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_sdk_write_electron_bridge_line(
    guance_sdk_handle handle,
    const char* line,
    size_t length);
/** Initializes versioned Electron Bridge Server options with RUM-only defaults. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_electron_bridge_server_options_init(
    guance_electron_bridge_server_options* options);
/**
 * Starts a local named-pipe server that borrows an existing SDK Handle.
 * Returns NULL when validation, pipe creation, or worker startup fails.
 * Distinct pipe names may be started concurrently. Concurrent or repeated
 * attempts for the same pipe name allow exactly one owner; other calls fail.
 */
GUANCE_WINDOWS_NATIVE_EXPORT guance_electron_bridge_server_handle
guance_electron_bridge_server_start(
    guance_sdk_handle sdk,
    const guance_electron_bridge_server_options* options);
/**
 * Stops a Bridge Server and waits for its worker to finish. Concurrent and
 * repeated calls with the same token are safe. The SDK Handle remains owned
 * by the application and may be shut down after this function returns.
 */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_electron_bridge_server_stop(
    guance_electron_bridge_server_handle bridge);
/** Initializes a versioned native monitoring configuration with supported defaults. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_native_monitoring_config_init(
    guance_sdk_native_monitoring_config* config);
/** Initializes a versioned native HTTP Resource collection configuration. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_resource_collection_config_init(
    guance_rum_resource_collection_config* config);
/** Initializes a versioned general telemetry modifier configuration. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_data_modifier_config_init(
    guance_data_modifier_config* config);
/** Initializes a versioned trace configuration with supported defaults. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_trace_config_init(
    guance_trace_config* config);
/** Initializes a versioned trace context before it is filled or passed to a provider. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_trace_context_init(
    guance_trace_context* context);
/** Initializes a versioned log configuration with supported defaults. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_log_config_init(
    guance_log_config* config);
/** Initializes a versioned log diagnostics structure before querying it. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_log_diagnostics_init(
    guance_log_diagnostics* diagnostics);
/** Applies native HTTP Resource filtering and URL privacy configuration. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_rum_configure_resource_collection(
    guance_sdk_handle handle,
    const guance_rum_resource_collection_config* config);
/** Applies general RUM and log modifiers. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_configure_data_modifiers(
    guance_sdk_handle handle,
    const guance_data_modifier_config* config);
/** Applies distributed trace propagation configuration. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_trace_configure(
    guance_sdk_handle handle,
    const guance_trace_config* config);
/** Creates trace headers and identifiers for one request. Returns non-zero when a context is available. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_trace_create_context(
    guance_sdk_handle handle,
    const char* url,
    const char* method,
    guance_trace_context* context);
/** Applies native custom log configuration. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_log_configure(
    guance_sdk_handle handle,
    const guance_log_config* config);
/** Adds one custom log. Strings are copied before return; non-zero means accepted. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_log_add(
    guance_sdk_handle handle,
    const char* content,
    const char* status,
    const guance_log_property* properties,
    uint32_t property_count);
/** Adds a batch of custom logs. Entry strings are copied before return; returns the accepted count. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_log_add_batch(
    guance_sdk_handle handle,
    const guance_log_entry* entries,
    uint32_t entry_count);
/** Copies current log queue and upload counters into diagnostics. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_log_get_diagnostics(
    guance_sdk_handle handle,
    guance_log_diagnostics* diagnostics);
/** Enables configured UI-hang monitoring and native crash recovery. Returns non-zero on success. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_sdk_enable_native_monitoring(
    guance_sdk_handle handle,
    const guance_sdk_native_monitoring_config* config);
/** Disables UI-hang monitoring and native crash recovery hooks for the SDK instance. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_disable_native_monitoring(guance_sdk_handle handle);
/** Records a C++ terminate crash envelope for recovery during the next initialization. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_capture_cpp_terminate(void);

/** Sets user identity for telemetry created after this call. Strings are copied. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_set_user(guance_sdk_handle handle, const char* id, const char* name, const char* email);
/** Clears user identity for telemetry created after this call. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_clear_user(guance_sdk_handle handle);
/** Adds or replaces a string context value shared by RUM and logs. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_sdk_add_global_context(guance_sdk_handle handle, const char* key, const char* value);
/** Adds or replaces a string context value attached only to RUM events. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_rum_context(guance_sdk_handle handle, const char* key, const char* value);

/** Starts a RUM View and closes the previous active View. The name is copied. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_start_view(guance_sdk_handle handle, const char* name);
/** Stops the active RUM View. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_view(guance_sdk_handle handle);
/** Adds a completed RUM Action with duration_ns measured in nanoseconds. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_action(guance_sdk_handle handle, const char* name, const char* type, int64_t duration_ns);
/** Adds a measured cold- or hot-launch Action. The launch structure is copied. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_launch_action(
    guance_sdk_handle handle,
    const guance_rum_launch* launch);
/** Adds a measured launch Action associated with an existing Browser View. All strings are copied. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_launch_action_ext(
    guance_sdk_handle handle,
    const guance_rum_launch* launch,
    const char* view_id,
    const char* view_name,
    const char* view_referrer);
/** Starts an automatically completed RUM Action with 100 ms frequency protection and a five-second maximum duration. The returned identifier is empty when rejected. */
GUANCE_WINDOWS_NATIVE_EXPORT const char* guance_rum_start_action(guance_sdk_handle handle, const char* name, const char* type);
/** Starts a RUM Action and optionally requires an explicit stop when need_wait is non-zero. All Actions are limited to five seconds. */
GUANCE_WINDOWS_NATIVE_EXPORT const char* guance_rum_start_action_ext(guance_sdk_handle handle, const char* name, const char* type, int need_wait);
/** Stops an Action started with need_wait enabled. Other Actions ignore this call. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_action(guance_sdk_handle handle, const char* action_id);
/** Starts a manually collected Resource. The returned identifier remains SDK-owned. */
GUANCE_WINDOWS_NATIVE_EXPORT const char* guance_rum_start_resource(guance_sdk_handle handle, const char* url, const char* method);
/** Starts an automatically classified Resource after collection policy is applied. */
GUANCE_WINDOWS_NATIVE_EXPORT const char* guance_rum_start_auto_resource(guance_sdk_handle handle, const char* url, const char* method);
/** Stops a Resource with status and response size; pass -1 when size is unknown. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_resource(guance_sdk_handle handle, const char* resource_id, int status_code, int64_t response_size);
/** Stops a Resource with request, response, type, trace, span, and protocol metadata. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_resource_ext(
    guance_sdk_handle handle,
    const char* resource_id,
    int status_code,
    int64_t response_size,
    int64_t request_size,
    const char* resource_type,
    const char* trace_id,
    const char* span_id,
    const char* http_protocol);
/** Stops a Resource and additionally captures raw request and response headers for privacy filtering. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_resource_ext_with_headers(
    guance_sdk_handle handle,
    const char* resource_id,
    int status_code,
    int64_t response_size,
    int64_t request_size,
    const char* resource_type,
    const char* trace_id,
    const char* span_id,
    const char* http_protocol,
    const char* request_header,
    const char* response_header);
/** Adds a RUM Error. All supplied strings are copied before return. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_error(guance_sdk_handle handle, const char* stack, const char* message, const char* error_type, const char* source);
/** Adds a RUM Long Task with duration_ns measured in nanoseconds. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_add_long_task(guance_sdk_handle handle, int64_t duration_ns, const char* stack);
/** Starts Session Replay recording when Replay is enabled and the session is sampled. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_start_session_replay(guance_sdk_handle handle);
/** Stops Session Replay recording and flushes the active segment. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_stop_session_replay(guance_sdk_handle handle);
/** Registers a top-level HWND as a native Session Replay capture target. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_register_replay_window(guance_sdk_handle handle, uintptr_t hwnd);
/** Records one native pointer click for an already registered Replay window. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_capture_replay_click(guance_sdk_handle handle, uintptr_t hwnd, const char* target, double x, double y);
/** Records one native input change for an already registered Replay window. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_capture_replay_input(guance_sdk_handle handle, uintptr_t hwnd, const char* target);
/** Records one native resize for an already registered Replay window. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_capture_replay_resize(guance_sdk_handle handle, uintptr_t hwnd, const char* target, double width, double height);
/** Experimental bridge entry point for rrweb-compatible records collected by
 * Browser RUM inside Electron/WebView renderers. session_id is retained for ABI
 * compatibility but ignored; Native Core always owns the authoritative Session.
 * The record and view identity are copied before this function returns. */
GUANCE_WINDOWS_NATIVE_EXPORT int guance_rum_capture_browser_replay_record(
    guance_sdk_handle handle,
    const char* session_id,
    const char* view_id,
    const char* record_json,
    size_t record_json_length,
    int64_t timestamp_ms,
    int is_full_snapshot);
/** Overrides text and input privacy for one native Replay window. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_set_session_replay_text_privacy(guance_sdk_handle handle, uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy);
/** Overrides pointer and touch privacy for one native Replay window. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_set_session_replay_touch_privacy(guance_sdk_handle handle, uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy);
/** Hides or reveals one native Replay window subtree. */
GUANCE_WINDOWS_NATIVE_EXPORT void guance_rum_set_session_replay_hidden(guance_sdk_handle handle, uintptr_t hwnd, int hidden);

#ifdef __cplusplus
}
#endif
