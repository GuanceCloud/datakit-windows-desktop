#include <guance_log.h>
#include <guance_rum.h>
#include <guance_sdk.h>
#include <guance_trace.h>

int main(void) {
    guance_sdk_config sdk_config;
    guance_log_config log_config;
    guance_trace_config trace_config;
    guance_rum_resource_collection_config rum_config;

    guance_sdk_config_init(&sdk_config);
    guance_log_config_init(&log_config);
    guance_trace_config_init(&trace_config);
    guance_rum_resource_collection_config_init(&rum_config);

    return sdk_config.max_cache_bytes > 0 &&
            sdk_config.max_batch_items > 0 &&
            log_config.struct_size == sizeof(log_config) &&
            trace_config.struct_size == sizeof(trace_config) &&
            rum_config.struct_size == sizeof(rum_config)
        ? 0
        : 1;
}
