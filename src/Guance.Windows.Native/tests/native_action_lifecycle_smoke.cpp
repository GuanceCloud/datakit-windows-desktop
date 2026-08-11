#include "guance_sdk.h"

#include <cassert>
#include <chrono>
#include <filesystem>
#include <string>
#include <thread>

namespace {

int64_t rum_events_enqueued(guance_sdk_handle handle) {
    guance_sdk_diagnostics diagnostics{};
    assert(guance_sdk_get_diagnostics(handle, &diagnostics) == 1);
    return diagnostics.rum_events_enqueued;
}

bool wait_for_rum_events(
    guance_sdk_handle handle,
    int64_t expected,
    std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    do {
        if (rum_events_enqueued(handle) >= expected) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    } while (std::chrono::steady_clock::now() < deadline);
    return rum_events_enqueued(handle) >= expected;
}

} // namespace

int main() {
    guance_sdk_config config{};
    guance_sdk_config_init(&config);
    config.sample_rate = 1.0;

    const auto cache_directory = std::filesystem::temp_directory_path() /
        ("guance-native-action-lifecycle-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()));
    std::filesystem::create_directories(cache_directory);
    const auto cache_path = (cache_directory / "cache").string();
    config.cache_path = cache_path.c_str();

    const auto handle = guance_sdk_init(&config);
    assert(handle != nullptr);
    guance_rum_start_view(handle, "ActionLifecycle");

    const std::string first = guance_rum_start_action(handle, "First", "click");
    assert(!first.empty());
    guance_rum_add_action(handle, "KnownDurationOne", "custom", 5'000'000);
    guance_rum_add_action(handle, "KnownDurationTwo", "custom", 5'000'000);
    assert(std::string(guance_rum_start_action(handle, "Protected", "click")).empty());
    assert(rum_events_enqueued(handle) == 2);

    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    const std::string replacement = guance_rum_start_action(handle, "Replacement", "click");
    assert(!replacement.empty());
    assert(replacement != first);
    assert(rum_events_enqueued(handle) == 3);

    guance_rum_stop_action(handle, replacement.c_str());
    assert(rum_events_enqueued(handle) == 3);
    assert(std::string(guance_rum_start_action(handle, "StillProtected", "click")).empty());

    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    const std::string waiting = guance_rum_start_action_ext(handle, "Waiting", "custom", 1);
    assert(!waiting.empty());
    assert(rum_events_enqueued(handle) == 4);

    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    assert(std::string(guance_rum_start_action(handle, "Blocked", "click")).empty());
    guance_rum_stop_action(handle, waiting.c_str());
    assert(rum_events_enqueued(handle) == 5);

    const std::string after_stop = guance_rum_start_action(handle, "AfterStop", "click");
    assert(!after_stop.empty());
    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    const std::string timed = guance_rum_start_action_ext(handle, "Timed", "custom", 1);
    assert(!timed.empty());
    assert(rum_events_enqueued(handle) == 6);

    assert(wait_for_rum_events(handle, 7, std::chrono::milliseconds(5500)));
    const std::string after_timeout = guance_rum_start_action(handle, "AfterTimeout", "click");
    assert(!after_timeout.empty());
    guance_rum_stop_view(handle);
    assert(rum_events_enqueued(handle) == 9);

    guance_sdk_shutdown(handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
