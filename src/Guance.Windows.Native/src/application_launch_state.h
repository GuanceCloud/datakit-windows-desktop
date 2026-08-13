#pragma once

#include <cstdint>
#include <optional>

namespace guance::rum {

struct ApplicationLaunchTimestamp {
    int64_t unix_ns = 0;
    int64_t monotonic_ns = 0;
};

enum class ApplicationLaunchKind {
    cold,
    hot,
};

struct ApplicationLaunchDecision {
    ApplicationLaunchKind kind = ApplicationLaunchKind::cold;
    int64_t start_time_ns = 0;
    int64_t duration_ns = 0;
    int64_t pre_application_duration_ns = 0;
    int64_t application_duration_ns = 0;
    int64_t first_frame_duration_ns = 0;
};

class ApplicationLaunchState {
public:
    static constexpr int64_t hot_background_threshold_ns = 10'000'000'000LL;

    ApplicationLaunchState(
        int64_t process_start_time_ns,
        ApplicationLaunchTimestamp sdk_initialized);

    void window_created(int64_t monotonic_ns);
    std::optional<ApplicationLaunchDecision> first_frame(
        ApplicationLaunchTimestamp timestamp);
    std::optional<ApplicationLaunchTimestamp> foreground_changed(
        bool belongs_to_process,
        ApplicationLaunchTimestamp timestamp);
    std::optional<ApplicationLaunchDecision> hot_first_frame(
        ApplicationLaunchTimestamp timestamp);
    void complete_cold_without_event();

    bool cold_complete() const noexcept;

private:
    int64_t process_start_time_ns_ = 0;
    ApplicationLaunchTimestamp sdk_initialized_;
    std::optional<int64_t> window_created_monotonic_ns_;
    std::optional<ApplicationLaunchTimestamp> background_started_;
    std::optional<ApplicationLaunchTimestamp> hot_started_;
    bool cold_complete_ = false;
};

} // namespace guance::rum
