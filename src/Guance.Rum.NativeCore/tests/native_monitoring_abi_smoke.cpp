#include "guance_rum.h"

#include <cassert>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>

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

    assert(guance_rum_enable_native_monitoring(nullptr, &monitoring) == 0);
    assert(guance_rum_enable_native_monitoring(handle, nullptr) == 0);

    auto invalid = monitoring;
    invalid.struct_size = static_cast<uint32_t>(offsetof(
        guance_rum_native_monitoring_config,
        max_crash_file_bytes));
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    invalid = monitoring;
    invalid.version++;
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    invalid = monitoring;
    invalid.ui_probe_interval_ms = 0;
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    invalid = monitoring;
    invalid.long_task_threshold_ms = invalid.ui_probe_interval_ms - 1;
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    invalid = monitoring;
    invalid.hang_threshold_ms = invalid.long_task_threshold_ms;
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    invalid = monitoring;
    invalid.enable_minidump = 1;
    invalid.max_crash_files = 1;
    assert(guance_rum_enable_native_monitoring(handle, &invalid) == 0);

    assert(guance_rum_enable_native_monitoring(handle, &monitoring) == 1);
    assert(guance_rum_enable_native_monitoring(handle, &monitoring) == 1);
    guance_rum_disable_native_monitoring(handle);
    guance_rum_disable_native_monitoring(handle);
    guance_rum_shutdown(handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
