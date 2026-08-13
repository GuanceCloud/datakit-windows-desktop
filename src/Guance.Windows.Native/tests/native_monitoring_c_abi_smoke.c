#include "guance_sdk.h"

#include <assert.h>
#include <string.h>

int main(void) {
    assert(guance_sdk_get_version() != NULL);
    assert(strlen(guance_sdk_get_version()) > 0);
    assert(guance_sdk_write_electron_bridge_line(NULL, "view value=1i", 13) == 0);

    guance_sdk_config sdk;
    guance_sdk_config_init(&sdk);
    assert(sdk.max_cache_bytes == 128LL * 1024 * 1024);
    assert(sdk.max_cache_files == 1024);
    assert(sdk.max_cache_age_seconds == 7LL * 24 * 60 * 60);
    assert(sdk.max_batch_items == 50);
    assert(sdk.compress_intake_requests == 1);
    assert(sdk.flush_interval_ms == 15000);
    assert(sdk.enable_app_launch_tracking == 1);
    assert(sdk.max_upload_bytes_per_second == 256LL * 1024);
    assert(sdk.upload_burst_bytes == 2LL * 1024 * 1024);
    assert(sdk.max_upload_requests_per_second == 2.0);

    guance_sdk_native_monitoring_config config;
    guance_sdk_native_monitoring_config_init(&config);

    assert(config.struct_size == sizeof(config));
    assert(config.version == GUANCE_SDK_NATIVE_MONITORING_CONFIG_VERSION);
    assert(config.enable_ui_hang_monitoring == 0);
    assert(config.enable_native_crash_reporting == 0);
    assert(config.ui_probe_interval_ms == 250);
    assert(config.long_task_threshold_ms == 500);
    assert(config.hang_threshold_ms == 5000);
    assert(guance_sdk_enable_native_monitoring(NULL, &config) == 0);
    guance_sdk_disable_native_monitoring(NULL);

    guance_rum_resource_collection_config resources;
    guance_rum_resource_collection_config_init(&resources);
    assert(resources.struct_size == sizeof(resources));
    assert(resources.version == GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION);
    assert(resources.enabled == 1);
    assert(resources.capture_url_query == 1);
    assert(resources.redact_all_url_query_values == 0);
    assert(resources.redacted_value != NULL);
    assert(resources.redacted_query_parameter_names != NULL);
    assert(resources.redacted_query_parameter_name_count > 0);
    assert(resources.capture_http_headers == 1);
    assert(resources.redacted_header_names != NULL);
    assert(resources.redacted_header_name_count > 0);
    assert(guance_rum_configure_resource_collection(NULL, &resources) == 0);

    guance_data_modifier_config modifiers;
    guance_data_modifier_config_init(&modifiers);
    assert(modifiers.struct_size == sizeof(modifiers));
    assert(modifiers.version == GUANCE_DATA_MODIFIER_CONFIG_VERSION);
    assert(modifiers.data_modifier == NULL);
    assert(modifiers.line_data_modifier == NULL);
    assert(guance_configure_data_modifiers(NULL, &modifiers) == 0);

    guance_trace_config trace;
    guance_trace_config_init(&trace);
    assert(trace.struct_size == sizeof(trace));
    assert(trace.version == GUANCE_TRACE_CONFIG_VERSION);
    assert(trace.enable_auto_trace == 0);
    assert(trace.enable_link_rum_data == 0);
    assert(trace.sample_rate == 1.0);
    assert(trace.trace_type == GUANCE_TRACE_DDTRACE);
    assert(guance_trace_configure(NULL, &trace) == 0);

    guance_trace_context context;
    guance_trace_context_init(&context);
    assert(context.struct_size == sizeof(context));
    assert(context.version == GUANCE_TRACE_CONTEXT_VERSION);
    assert(context.header_count == 0);
    assert(guance_trace_create_context(NULL, "https://example.com", "GET", &context) == 0);

    guance_log_config logging;
    guance_log_config_init(&logging);
    assert(logging.struct_size == sizeof(logging));
    assert(logging.version == GUANCE_LOG_CONFIG_VERSION);
    assert(logging.sample_rate == 1.0);
    assert(logging.discard_strategy == GUANCE_LOG_DISCARD_NEW);
    assert(guance_log_configure(NULL, &logging) == 0);
    assert(guance_log_add(NULL, "message", "info", NULL, 0) == 0);
    assert(guance_log_add_batch(NULL, NULL, 0) == 0);
    guance_log_diagnostics log_diagnostics;
    guance_log_diagnostics_init(&log_diagnostics);
    assert(log_diagnostics.struct_size == sizeof(log_diagnostics));
    assert(log_diagnostics.version == GUANCE_LOG_DIAGNOSTICS_VERSION);
    assert(guance_log_get_diagnostics(NULL, &log_diagnostics) == 0);
    return 0;
}
