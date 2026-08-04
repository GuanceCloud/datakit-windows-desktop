#include "guance_rum.h"

#include <assert.h>

int main(void) {
    guance_rum_native_monitoring_config config;
    guance_rum_native_monitoring_config_init(&config);

    assert(config.struct_size == sizeof(config));
    assert(config.version == GUANCE_RUM_NATIVE_MONITORING_CONFIG_VERSION);
    assert(config.enable_ui_hang_monitoring == 0);
    assert(config.enable_native_crash_reporting == 0);
    assert(config.ui_probe_interval_ms == 250);
    assert(config.long_task_threshold_ms == 500);
    assert(config.hang_threshold_ms == 5000);
    assert(guance_rum_enable_native_monitoring(NULL, &config) == 0);
    guance_rum_disable_native_monitoring(NULL);

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
    assert(guance_rum_configure_resource_collection(NULL, &resources) == 0);

    guance_rum_trace_config trace;
    guance_rum_trace_config_init(&trace);
    assert(trace.struct_size == sizeof(trace));
    assert(trace.version == GUANCE_RUM_TRACE_CONFIG_VERSION);
    assert(trace.enable_auto_trace == 0);
    assert(trace.enable_link_rum_data == 0);
    assert(trace.sample_rate == 1.0);
    assert(trace.trace_type == GUANCE_RUM_TRACE_DDTRACE);
    assert(guance_rum_configure_trace(NULL, &trace) == 0);

    guance_rum_trace_context context;
    guance_rum_trace_context_init(&context);
    assert(context.struct_size == sizeof(context));
    assert(context.version == GUANCE_RUM_TRACE_CONTEXT_VERSION);
    assert(context.header_count == 0);
    assert(guance_rum_create_trace_context(NULL, "https://example.com", "GET", &context) == 0);

    guance_rum_log_config logging;
    guance_rum_log_config_init(&logging);
    assert(logging.struct_size == sizeof(logging));
    assert(logging.version == GUANCE_RUM_LOG_CONFIG_VERSION);
    assert(logging.sample_rate == 1.0);
    assert(logging.max_queue_items == 5000);
    assert(logging.discard_strategy == GUANCE_RUM_LOG_DISCARD_NEW);
    assert(guance_rum_configure_logging(NULL, &logging) == 0);
    assert(guance_rum_add_log(NULL, "message", "info", NULL, 0) == 0);
    assert(guance_rum_add_logs(NULL, NULL, 0) == 0);
    guance_rum_log_diagnostics log_diagnostics;
    guance_rum_log_diagnostics_init(&log_diagnostics);
    assert(log_diagnostics.struct_size == sizeof(log_diagnostics));
    assert(log_diagnostics.version == GUANCE_RUM_LOG_DIAGNOSTICS_VERSION);
    assert(guance_rum_get_log_diagnostics(NULL, &log_diagnostics) == 0);
    return 0;
}
