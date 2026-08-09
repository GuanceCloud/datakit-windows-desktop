#include <guance_log.h>
#include <guance_rum.h>
#include <guance_sdk.h>
#include <guance_trace.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char* value_or_default(const char* value, const char* fallback) {
    return value != NULL && value[0] != '\0' ? value : fallback;
}

int main(void) {
    guance_sdk_config sdk_config;
    guance_sdk_config_init(&sdk_config);

    const char* dataway_url = getenv("GUANCE_RUM_DATAWAY_URL");
    if (dataway_url != NULL && dataway_url[0] != '\0') {
        sdk_config.dataway_url = dataway_url;
        sdk_config.datakit_url = NULL;
        sdk_config.client_token = getenv("GUANCE_RUM_CLIENT_TOKEN");
    } else {
        sdk_config.dataway_url = NULL;
        sdk_config.datakit_url = value_or_default(
            getenv("GUANCE_RUM_DATAKIT_URL"),
            "http://127.0.0.1:9529");
        sdk_config.client_token = NULL;
    }

    sdk_config.rum_app_id = value_or_default(
        getenv("GUANCE_RUM_APP_ID"),
        "rum-native-console-demo");
    sdk_config.service_name = value_or_default(
        getenv("GUANCE_RUM_SERVICE_NAME"),
        "native-console-sample");
    sdk_config.env = value_or_default(getenv("GUANCE_RUM_ENV"), "local");
    sdk_config.version = value_or_default(getenv("GUANCE_RUM_VERSION"), "1.0.0");
    sdk_config.debug = 1;

    guance_sdk_handle sdk = guance_sdk_init(&sdk_config);
    if (sdk == NULL) {
        fputs("Guance SDK initialization failed.\n", stderr);
        return 1;
    }

    guance_log_config log_config;
    guance_log_config_init(&log_config);
    log_config.enable_custom_log = 1;
    log_config.enable_link_rum_data = 1;
    if (!guance_log_configure(sdk, &log_config)) {
        fputs("Guance log configuration failed.\n", stderr);
        guance_sdk_shutdown(sdk);
        return 1;
    }

    guance_sdk_set_user(sdk, "native-sample-user", "Native Sample", NULL);
    guance_sdk_add_global_context(sdk, "sample", "native-console");
    guance_rum_start_view(sdk, "NativeConsole/Main");

    const char* action_id = guance_rum_start_action(sdk, "RunSample", "command");
    const guance_log_property properties[] = {
        {"component", "NativeConsoleSample"}
    };
    guance_log_add(sdk, "Native sample started.", "info", properties, 1);

    const char* resource_id = guance_rum_start_resource(
        sdk,
        "https://example.com/native-sample",
        "GET");
    if (resource_id != NULL && resource_id[0] != '\0') {
        guance_rum_stop_resource_ext(
            sdk,
            resource_id,
            200,
            0,
            0,
            "http",
            NULL,
            NULL,
            "HTTP/1.1");
    }

    guance_rum_add_long_task(sdk, 600000000, "NativeConsoleSample simulated work");
    if (action_id != NULL && action_id[0] != '\0') {
        guance_rum_stop_action(sdk, action_id);
    }
    guance_rum_stop_view(sdk);

    guance_sdk_diagnostics rum_diagnostics;
    if (guance_sdk_get_diagnostics(sdk, &rum_diagnostics)) {
        printf("RUM events enqueued: %lld\n", (long long)rum_diagnostics.rum_events_enqueued);
    }

    guance_log_diagnostics log_diagnostics;
    guance_log_diagnostics_init(&log_diagnostics);
    if (guance_log_get_diagnostics(sdk, &log_diagnostics)) {
        printf("Logs enqueued: %lld\n", (long long)log_diagnostics.logs_enqueued);
    }

    guance_sdk_flush(sdk);
    guance_sdk_shutdown(sdk);
    return 0;
}

