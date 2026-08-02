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
    return 0;
}
