#include "application_launch_state.h"

#include <algorithm>
#include <limits>

namespace guance::rum {

namespace {

int64_t non_negative_difference(int64_t end, int64_t start) {
    if (end <= start) {
        return 0;
    }
    if (start < 0 && end > (std::numeric_limits<int64_t>::max)() + start) {
        return (std::numeric_limits<int64_t>::max)();
    }
    return end - start;
}

int64_t saturated_add(int64_t left, int64_t right) {
    left = std::max<int64_t>(left, 0);
    right = std::max<int64_t>(right, 0);
    if (right > (std::numeric_limits<int64_t>::max)() - left) {
        return (std::numeric_limits<int64_t>::max)();
    }
    return left + right;
}

} // namespace

ApplicationLaunchState::ApplicationLaunchState(
    int64_t process_start_time_ns,
    ApplicationLaunchTimestamp sdk_initialized)
    : process_start_time_ns_(
          process_start_time_ns > 0 &&
                  process_start_time_ns <= sdk_initialized.unix_ns
              ? process_start_time_ns
              : std::max<int64_t>(sdk_initialized.unix_ns, 0)),
      sdk_initialized_(sdk_initialized) {}

void ApplicationLaunchState::window_created(int64_t monotonic_ns) {
    if (!cold_complete_ && !window_created_monotonic_ns_) {
        window_created_monotonic_ns_ = monotonic_ns;
    }
}

std::optional<ApplicationLaunchDecision> ApplicationLaunchState::first_frame(
    ApplicationLaunchTimestamp timestamp) {
    if (cold_complete_) {
        return std::nullopt;
    }

    const auto window_created = window_created_monotonic_ns_.value_or(
        sdk_initialized_.monotonic_ns);
    const auto pre_application = non_negative_difference(
        sdk_initialized_.unix_ns,
        process_start_time_ns_);
    const auto application = non_negative_difference(
        window_created,
        sdk_initialized_.monotonic_ns);
    const auto first_frame = non_negative_difference(
        timestamp.monotonic_ns,
        window_created);

    cold_complete_ = true;
    background_started_.reset();
    hot_started_.reset();
    return ApplicationLaunchDecision{
        ApplicationLaunchKind::cold,
        process_start_time_ns_,
        saturated_add(saturated_add(pre_application, application), first_frame),
        pre_application,
        application,
        first_frame};
}

std::optional<ApplicationLaunchTimestamp> ApplicationLaunchState::foreground_changed(
    bool belongs_to_process,
    ApplicationLaunchTimestamp timestamp) {
    if (!cold_complete_) {
        return std::nullopt;
    }

    if (!belongs_to_process) {
        if (!background_started_ && !hot_started_) {
            background_started_ = timestamp;
        }
        return std::nullopt;
    }

    if (!background_started_ || hot_started_) {
        return std::nullopt;
    }

    const auto background_duration = non_negative_difference(
        timestamp.monotonic_ns,
        background_started_->monotonic_ns);
    background_started_.reset();
    if (background_duration < hot_background_threshold_ns) {
        return std::nullopt;
    }

    hot_started_ = timestamp;
    return hot_started_;
}

std::optional<ApplicationLaunchDecision> ApplicationLaunchState::hot_first_frame(
    ApplicationLaunchTimestamp timestamp) {
    if (!hot_started_) {
        return std::nullopt;
    }

    const auto started = *hot_started_;
    hot_started_.reset();
    return ApplicationLaunchDecision{
        ApplicationLaunchKind::hot,
        std::max<int64_t>(started.unix_ns, 0),
        non_negative_difference(timestamp.monotonic_ns, started.monotonic_ns),
        0,
        0,
        0};
}

void ApplicationLaunchState::complete_cold_without_event() {
    cold_complete_ = true;
    background_started_.reset();
    hot_started_.reset();
}

bool ApplicationLaunchState::cold_complete() const noexcept {
    return cold_complete_;
}

} // namespace guance::rum
