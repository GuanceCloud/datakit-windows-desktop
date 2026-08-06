#include "native_monitoring.h"

#include "rum_core.h"

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
#include "native_crash_reporter.h"
#include "ui_hang_monitor.h"
#endif

#include <cstddef>
#include <utility>

namespace guance::rum {

namespace {

std::string nullable_string(const char* value) {
    return value == nullptr ? std::string{} : std::string(value);
}

} // namespace

std::optional<NativeMonitoringConfig> from_c_native_monitoring_config(
    const guance_sdk_native_monitoring_config& config) {
    if (config.struct_size < sizeof(guance_sdk_native_monitoring_config) ||
        config.version != GUANCE_SDK_NATIVE_MONITORING_CONFIG_VERSION ||
        config.ui_probe_interval_ms <= 0 ||
        config.long_task_threshold_ms < config.ui_probe_interval_ms ||
        config.hang_threshold_ms <= config.long_task_threshold_ms ||
        config.hang_report_cooldown_ms < 0 ||
        config.max_crash_files <= 0 ||
        (config.enable_minidump != 0 && config.max_crash_files < 2) ||
        config.max_crash_file_bytes <= 0) {
        return std::nullopt;
    }

    NativeMonitoringConfig result;
    result.enable_ui_hang_monitoring = config.enable_ui_hang_monitoring != 0;
    result.enable_native_crash_reporting = config.enable_native_crash_reporting != 0;
    result.main_window_handle = config.main_window_handle;
    result.ui_probe_interval_ms = config.ui_probe_interval_ms;
    result.long_task_threshold_ms = config.long_task_threshold_ms;
    result.hang_threshold_ms = config.hang_threshold_ms;
    result.hang_report_cooldown_ms = config.hang_report_cooldown_ms;
    result.crash_cache_path = nullable_string(config.crash_cache_path);
    result.enable_minidump = config.enable_minidump != 0;
    result.max_crash_files = config.max_crash_files;
    result.max_crash_file_bytes = config.max_crash_file_bytes;
    return result;
}

class NativeMonitoring::Impl {
public:
    explicit Impl(RumCore& core) : core_(core) {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
        ui_hang_monitor_ = std::make_unique<UiHangMonitor>(
            [this](const HangEvent& event, const std::string& stack) {
                core_.add_ui_hang_event(event, stack);
            },
            [this](const std::string& message) {
                core_.log_native_monitoring(message);
            });
        create_crash_reporter();
        native_crash_reporter_->recover_pending({
            core_.default_native_crash_path(),
            false,
            3,
            32LL * 1024 * 1024,
        });
#endif
    }

    ~Impl() {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
        ui_hang_monitor_->stop();
        stop_crash_reporter();
#endif
    }

    bool enable(const guance_sdk_native_monitoring_config& c_config) {
        const auto converted = from_c_native_monitoring_config(c_config);
        if (!converted) {
            return false;
        }
#if !defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
        if (converted->enable_ui_hang_monitoring || converted->enable_native_crash_reporting) {
            return false;
        }
#endif
        if (converted->enable_ui_hang_monitoring && converted->main_window_handle != 0) {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
            if (!ui_hang_monitor_->configure({
                    converted->main_window_handle,
                    converted->ui_probe_interval_ms,
                    converted->long_task_threshold_ms,
                    converted->hang_threshold_ms,
                    converted->hang_report_cooldown_ms,
                })) {
                return false;
            }
#endif
        } else {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
            ui_hang_monitor_->stop();
#endif
        }
        if (converted->enable_native_crash_reporting) {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
            if (native_crash_reporter_ && !native_crash_reporter_->stop()) {
                NativeCrashReporter::handoff_deferred_cleanup(
                    std::move(native_crash_reporter_));
                ui_hang_monitor_->stop();
                return false;
            }
            if (!native_crash_reporter_) {
                create_crash_reporter();
            }
            auto crash_path = std::filesystem::path(converted->crash_cache_path);
            if (crash_path.empty()) {
                crash_path = core_.default_native_crash_path();
            }
            if (!native_crash_reporter_->configure({
                    std::move(crash_path),
                    converted->enable_minidump,
                    converted->max_crash_files,
                    converted->max_crash_file_bytes,
                })) {
                ui_hang_monitor_->stop();
                return false;
            }
#endif
        } else {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
            stop_crash_reporter();
#endif
        }
        return true;
    }

    void disable() {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
        ui_hang_monitor_->stop();
        stop_crash_reporter();
#endif
    }

private:
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    void create_crash_reporter() {
        native_crash_reporter_ = std::make_unique<NativeCrashReporter>(
            [this](const CrashEnvelope& envelope, const std::filesystem::path& dump_path) {
                return core_.add_recovered_crash(envelope, dump_path);
            });
    }

    void stop_crash_reporter() {
        if (native_crash_reporter_ && !native_crash_reporter_->stop()) {
            // stop() transferred ownership to its deferred cleanup worker (or
            // quarantined the instance in the CreateThread failure fallback).
            NativeCrashReporter::handoff_deferred_cleanup(
                std::move(native_crash_reporter_));
        }
    }
#endif

    RumCore& core_;
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    std::unique_ptr<UiHangMonitor> ui_hang_monitor_;
    std::unique_ptr<NativeCrashReporter> native_crash_reporter_;
#endif
};

NativeMonitoring::NativeMonitoring(RumCore& core)
    : impl_(std::make_unique<Impl>(core)) {}

NativeMonitoring::~NativeMonitoring() = default;

bool NativeMonitoring::enable(const guance_sdk_native_monitoring_config& config) {
    return impl_->enable(config);
}

void NativeMonitoring::disable() {
    impl_->disable();
}

} // namespace guance::rum
