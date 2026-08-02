#include "guance_rum.hpp"

#include <cassert>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>

namespace {

int collect_non_ignored(const char* url, const char*, void*) {
    return std::string(url).find("ignored") == std::string::npos ? 1 : 0;
}

} // namespace

int main() {
    guance_rum_native_monitoring_config monitoring{};
    guance_rum_native_monitoring_config_init(&monitoring);

    assert(monitoring.struct_size == sizeof(monitoring));
    assert(monitoring.version == GUANCE_RUM_NATIVE_MONITORING_CONFIG_VERSION);
    assert(monitoring.enable_ui_hang_monitoring == 0);
    assert(monitoring.enable_native_crash_reporting == 0);
    assert(monitoring.ui_probe_interval_ms == 250);
    assert(monitoring.long_task_threshold_ms == 500);
    assert(monitoring.hang_threshold_ms == 5'000);
    assert(monitoring.hang_report_cooldown_ms == 5'000);
    assert(monitoring.enable_minidump == 0);
    assert(monitoring.max_crash_files == 3);
    assert(monitoring.max_crash_file_bytes == 32LL * 1024 * 1024);

    guance_rum_config core_config{};
    guance_rum_config_init(&core_config);
    core_config.sample_rate = 1.0;
    const auto cache_directory = std::filesystem::temp_directory_path() /
        ("guance-native-monitoring-abi-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()));
    std::filesystem::create_directories(cache_directory);
    const auto cache_path = (cache_directory / "rum.db").string();
    core_config.cache_path = cache_path.c_str();
    const auto handle = guance_rum_init(&core_config);
    assert(handle != nullptr);

    guance_rum_resource_collection_config resources{};
    guance_rum_resource_collection_config_init(&resources);
    assert(resources.struct_size == sizeof(resources));
    assert(resources.version == GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION);
    assert(resources.enabled == 1);
    assert(resources.capture_url_query == 1);

    auto invalid_resources = resources;
    invalid_resources.struct_size = static_cast<uint32_t>(offsetof(
        guance_rum_resource_collection_config,
        user_data));
    const int invalid_size_result =
        guance_rum_configure_resource_collection(handle, &invalid_resources);
    assert(invalid_size_result == 0);
    invalid_resources = resources;
    invalid_resources.version++;
    const int invalid_version_result =
        guance_rum_configure_resource_collection(handle, &invalid_resources);
    assert(invalid_version_result == 0);

    resources.should_collect = collect_non_ignored;
    const int configured = guance_rum_configure_resource_collection(handle, &resources);
    assert(configured == 1);
    guance::rum::ResourceScope ignored(
        handle,
        "https://example.com/ignored",
        "GET",
        "http",
        guance::rum::ResourceCollectionKind::automatic);
    assert(!ignored.active());
    {
        guance::rum::ResourceScope collected(
            handle,
            "https://example.com/collected?token=secret",
            "POST",
            "http",
            guance::rum::ResourceCollectionKind::automatic);
        assert(collected.active());
        collected.complete(204, 32, 16, nullptr, nullptr, "HTTP/2");
        assert(!collected.active());
    }
    {
        guance::rum::ResourceScope failed(
            handle,
            "https://example.com/failed",
            "GET",
            "http",
            guance::rum::ResourceCollectionKind::automatic);
        assert(failed.active());
    }
    resources.enabled = 0;
    const int disabled_configured =
        guance_rum_configure_resource_collection(handle, &resources);
    assert(disabled_configured == 1);
    guance::rum::ResourceScope disabled_auto(
        handle,
        "https://example.com/disabled-auto",
        "GET",
        "http",
        guance::rum::ResourceCollectionKind::automatic);
    assert(!disabled_auto.active());
    guance::rum::ResourceScope manual(
        handle,
        "https://example.com/manual",
        "GET",
        "http");
    assert(manual.active());
    manual.complete(200);
    ignored.fail();
    disabled_auto.fail();

    const int null_handle_result = guance_rum_enable_native_monitoring(nullptr, &monitoring);
    assert(null_handle_result == 0);
    const int null_config_result = guance_rum_enable_native_monitoring(handle, nullptr);
    assert(null_config_result == 0);

    auto invalid = monitoring;
    invalid.struct_size = static_cast<uint32_t>(offsetof(
        guance_rum_native_monitoring_config,
        max_crash_file_bytes));
    int invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    invalid = monitoring;
    invalid.version++;
    invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    invalid = monitoring;
    invalid.ui_probe_interval_ms = 0;
    invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    invalid = monitoring;
    invalid.long_task_threshold_ms = invalid.ui_probe_interval_ms - 1;
    invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    invalid = monitoring;
    invalid.hang_threshold_ms = invalid.long_task_threshold_ms;
    invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    invalid = monitoring;
    invalid.enable_minidump = 1;
    invalid.max_crash_files = 1;
    invalid_result = guance_rum_enable_native_monitoring(handle, &invalid);
    assert(invalid_result == 0);

    const int enabled = guance_rum_enable_native_monitoring(handle, &monitoring);
    assert(enabled == 1);
    const int enabled_again = guance_rum_enable_native_monitoring(handle, &monitoring);
    assert(enabled_again == 1);
    guance_rum_disable_native_monitoring(handle);
    guance_rum_disable_native_monitoring(handle);
    guance_rum_shutdown(handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
