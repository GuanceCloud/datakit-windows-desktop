#include "application_launch_state.h"

#include <cassert>
#include <cstdint>
#include <limits>

using guance::rum::ApplicationLaunchKind;
using guance::rum::ApplicationLaunchState;
using guance::rum::ApplicationLaunchTimestamp;

int main() {
    constexpr int64_t second = 1'000'000'000LL;

    ApplicationLaunchState cold(
        100 * second,
        ApplicationLaunchTimestamp{102 * second, 20 * second});
    cold.window_created(23 * second);
    const auto cold_launch = cold.first_frame(
        ApplicationLaunchTimestamp{106 * second, 24 * second});
    assert(cold_launch);
    assert(cold_launch->kind == ApplicationLaunchKind::cold);
    assert(cold_launch->start_time_ns == 100 * second);
    assert(cold_launch->pre_application_duration_ns == 2 * second);
    assert(cold_launch->application_duration_ns == 3 * second);
    assert(cold_launch->first_frame_duration_ns == second);
    assert(cold_launch->duration_ns == 6 * second);
    assert(!cold.first_frame(ApplicationLaunchTimestamp{107 * second, 25 * second}));

    assert(!cold.foreground_changed(
        false,
        ApplicationLaunchTimestamp{110 * second, 30 * second}));
    assert(!cold.foreground_changed(
        true,
        ApplicationLaunchTimestamp{119 * second, 39 * second}));
    assert(!cold.hot_first_frame(
        ApplicationLaunchTimestamp{119 * second, 39 * second}));

    assert(!cold.foreground_changed(
        false,
        ApplicationLaunchTimestamp{120 * second, 40 * second}));
    const auto hot_start = cold.foreground_changed(
        true,
        ApplicationLaunchTimestamp{130 * second, 50 * second});
    assert(hot_start);
    const auto first_hot = cold.hot_first_frame(
        ApplicationLaunchTimestamp{130 * second + 20, 50 * second + 20});
    assert(first_hot);
    assert(first_hot->kind == ApplicationLaunchKind::hot);
    assert(first_hot->start_time_ns == 130 * second);
    assert(first_hot->duration_ns == 20);

    assert(!cold.foreground_changed(
        false,
        ApplicationLaunchTimestamp{140 * second, 60 * second}));
    assert(cold.foreground_changed(
        true,
        ApplicationLaunchTimestamp{151 * second, 71 * second}));
    assert(cold.hot_first_frame(
        ApplicationLaunchTimestamp{151 * second + 10, 71 * second + 10}));

    ApplicationLaunchState regressed(
        300 * second,
        ApplicationLaunchTimestamp{299 * second, 90 * second});
    regressed.window_created(89 * second);
    const auto regressed_launch = regressed.first_frame(
        ApplicationLaunchTimestamp{298 * second, 88 * second});
    assert(regressed_launch);
    assert(regressed_launch->start_time_ns == 299 * second);
    assert(regressed_launch->duration_ns == 0);

    ApplicationLaunchState saturated(
        1,
        ApplicationLaunchTimestamp{
            (std::numeric_limits<int64_t>::max)(),
            0});
    saturated.window_created((std::numeric_limits<int64_t>::max)());
    const auto saturated_launch = saturated.first_frame(
        ApplicationLaunchTimestamp{
            (std::numeric_limits<int64_t>::max)(),
            (std::numeric_limits<int64_t>::max)()});
    assert(saturated_launch);
    assert(saturated_launch->duration_ns == (std::numeric_limits<int64_t>::max)());

    ApplicationLaunchState manual(
        10,
        ApplicationLaunchTimestamp{20, 30});
    manual.complete_cold_without_event();
    assert(!manual.first_frame(ApplicationLaunchTimestamp{40, 50}));
    assert(!manual.foreground_changed(false, ApplicationLaunchTimestamp{60, 60}));
    assert(manual.foreground_changed(
        true,
        ApplicationLaunchTimestamp{70, 60 + ApplicationLaunchState::hot_background_threshold_ns}));

    return 0;
}
