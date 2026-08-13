#pragma once

#include "application_launch_state.h"

#include <functional>
#include <memory>
#include <string>

namespace guance::rum {

class ApplicationLaunchMonitor {
public:
    using LaunchCallback = std::function<void(const ApplicationLaunchDecision&)>;
    using LogCallback = std::function<void(const std::string&)>;

    ApplicationLaunchMonitor(
        ApplicationLaunchTimestamp sdk_initialized,
        LaunchCallback launch_callback,
        LogCallback log_callback);
    ~ApplicationLaunchMonitor();

    ApplicationLaunchMonitor(const ApplicationLaunchMonitor&) = delete;
    ApplicationLaunchMonitor& operator=(const ApplicationLaunchMonitor&) = delete;

    bool start();
    void stop();
    void complete_cold_without_event();

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace guance::rum
