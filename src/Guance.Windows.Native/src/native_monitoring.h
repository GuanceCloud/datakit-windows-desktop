#pragma once

#include "guance_sdk.h"

#include <cstdint>
#include <memory>
#include <optional>
#include <string>

namespace guance::rum {

class RumCore;

struct NativeMonitoringConfig {
    bool enable_ui_hang_monitoring = false;
    bool enable_native_crash_reporting = false;
    uintptr_t main_window_handle = 0;
    int ui_probe_interval_ms = 250;
    int long_task_threshold_ms = 500;
    int hang_threshold_ms = 5'000;
    int hang_report_cooldown_ms = 5'000;
    std::string crash_cache_path;
    bool enable_minidump = false;
    int max_crash_files = 3;
    int64_t max_crash_file_bytes = 32LL * 1024 * 1024;
};

std::optional<NativeMonitoringConfig> from_c_native_monitoring_config(
    const guance_sdk_native_monitoring_config& config);

class NativeMonitoring {
public:
    explicit NativeMonitoring(RumCore& core);
    ~NativeMonitoring();

    bool enable(const guance_sdk_native_monitoring_config& config);
    void disable();

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace guance::rum
