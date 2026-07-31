#pragma once

#include "hang_state_machine.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace guance::rum {

struct UiHangMonitorConfig {
    uintptr_t main_window_handle = 0;
    int probe_interval_ms = 250;
    int long_task_threshold_ms = 500;
    int hang_threshold_ms = 5'000;
    int cooldown_ms = 5'000;
};

class UiHangMonitor {
public:
    using EventCallback = std::function<void(const HangEvent&, const std::string& stack)>;
    using DiagnosticCallback = std::function<void(const std::string& message)>;

    UiHangMonitor(EventCallback callback, DiagnosticCallback diagnostic_callback);
    ~UiHangMonitor();

    bool configure(const UiHangMonitorConfig& config);
    void stop();

private:
    void run();

    EventCallback callback_;
    DiagnosticCallback diagnostic_callback_;
    UiHangMonitorConfig config_;
    std::atomic<bool> stop_requested_{false};
    std::mutex wait_mutex_;
    std::condition_variable wake_;
    std::thread thread_;
};

} // namespace guance::rum
