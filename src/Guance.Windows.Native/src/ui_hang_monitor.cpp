#include "ui_hang_monitor.h"

#include "line_protocol.h"
#include "thread_stack_sampler.h"

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
#include <windows.h>
#endif

#include <algorithm>
#include <chrono>
#include <utility>

namespace guance::rum {

namespace {

int64_t steady_milliseconds() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
bool valid_owned_window(uintptr_t raw_window, DWORD* thread_id = nullptr) {
    const HWND window = reinterpret_cast<HWND>(raw_window);
    if (window == nullptr || !IsWindow(window) || GetAncestor(window, GA_ROOT) != window) {
        return false;
    }
    DWORD process_id = 0;
    const DWORD owner_thread = GetWindowThreadProcessId(window, &process_id);
    if (owner_thread == 0 || process_id != GetCurrentProcessId()) {
        return false;
    }
    if (thread_id != nullptr) {
        *thread_id = owner_thread;
    }
    return true;
}

bool probe_window(uintptr_t raw_window, int timeout_ms) {
    const HWND window = reinterpret_cast<HWND>(raw_window);
    DWORD_PTR ignored = 0;
    SetLastError(ERROR_SUCCESS);
    return SendMessageTimeoutW(
               window,
               WM_NULL,
               0,
               0,
               SMTO_ABORTIFHUNG | SMTO_BLOCK,
               static_cast<UINT>(std::clamp(timeout_ms, 1, 1'000)),
               &ignored) != 0;
}
#endif

} // namespace

UiHangMonitor::UiHangMonitor(
    EventCallback callback,
    DiagnosticCallback diagnostic_callback)
    : callback_(std::move(callback)),
      diagnostic_callback_(std::move(diagnostic_callback)) {}

UiHangMonitor::~UiHangMonitor() {
    stop();
}

bool UiHangMonitor::configure(const UiHangMonitorConfig& config) {
#if !defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    (void)config;
    return false;
#else
    if (!valid_owned_window(config.main_window_handle)) {
        return false;
    }

    stop();
    config_ = config;
    stop_requested_.store(false);
    thread_ = std::thread([this] { run(); });
    return true;
#endif
}

void UiHangMonitor::stop() {
    stop_requested_.store(true);
    wake_.notify_all();
    if (thread_.joinable()) {
        thread_.join();
    }
}

void UiHangMonitor::run() {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    DWORD ui_thread_id = 0;
    if (!valid_owned_window(config_.main_window_handle, &ui_thread_id)) {
        if (diagnostic_callback_) {
            diagnostic_callback_("watchdog stopped because the main window is invalid");
        }
        return;
    }

    HangStateMachine state({
        config_.probe_interval_ms,
        config_.long_task_threshold_ms,
        config_.hang_threshold_ms,
        config_.cooldown_ms,
    });
    std::string incident_stack;

    while (!stop_requested_.load()) {
        if (!valid_owned_window(config_.main_window_handle, &ui_thread_id)) {
            if (diagnostic_callback_) {
                diagnostic_callback_("watchdog stopped because the main window was destroyed");
            }
            return;
        }

        const auto probe_started_ms = steady_milliseconds();
        const bool responsive = probe_window(config_.main_window_handle, config_.probe_interval_ms);
        if (stop_requested_.load()) {
            return;
        }

        const auto observed_ms = responsive ? steady_milliseconds() : probe_started_ms;
        auto decision = state.advance(
            observed_ms,
            responsive,
            responsive ? std::string{} : uuid32());
        if (decision.capture_stack) {
            if (diagnostic_callback_) {
                diagnostic_callback_("watchdog incident detected");
            }
            incident_stack = sample_thread_stack(ui_thread_id, 100);
        }
        if (decision.event && callback_) {
            try {
                callback_(*decision.event, incident_stack);
            } catch (...) {
                // Monitoring must never terminate the watchdog thread.
            }
            if (diagnostic_callback_) {
                diagnostic_callback_("watchdog incident recovered");
            }
            incident_stack.clear();
        }

        std::unique_lock lock(wait_mutex_);
        wake_.wait_for(
            lock,
            std::chrono::milliseconds(config_.probe_interval_ms),
            [this] { return stop_requested_.load(); });
    }
#endif
}

} // namespace guance::rum
