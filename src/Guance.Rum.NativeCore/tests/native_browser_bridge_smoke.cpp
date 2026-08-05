#include "guance_rum.h"

#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {

std::filesystem::path unique_queue_path() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
           ("guance-rum-browser-bridge-" + std::to_string(suffix) + ".db");
}

void require(bool condition, const char* message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

} // namespace

int main() {
    const auto queue_path = unique_queue_path();
    const auto queue_path_text = queue_path.string();

    guance_rum_config config{};
    guance_rum_config_init(&config);
    config.datakit_url = "http://127.0.0.1:9";
    config.rum_app_id = "electron-browser-bridge-smoke";
    config.cache_path = queue_path_text.c_str();
    config.http_timeout_ms = 50;
    config.session_replay_enabled = 1;
    config.session_replay_sample_rate = 1.0;

    guance_rum_handle handle = guance_rum_init(&config);
    require(handle != nullptr, "native core failed to initialize");

    const std::array<const char*, 5> measurements{
        "view",
        "action",
        "resource",
        "error",
        "long_task",
    };
    std::string valid;
    for (const char* measurement : measurements) {
        valid =
            std::string(measurement) +
            ",app_id=electron-browser-bridge-smoke,sdk_name=df_windows_rum_sdk "
            "is_active=false,time_spent=1i 1722300000000000000\n";
        require(
            guance_rum_write_line(handle, valid.data(), valid.size()) == 1,
            "valid Phase 1 Browser RUM line was rejected");
    }

    const std::string unsupported =
        "log,app_id=electron-browser-bridge-smoke message=\"not rum\" "
        "1722300000000000000\n";
    require(
        guance_rum_write_line(handle, unsupported.data(), unsupported.size()) == 0,
        "unsupported measurement was accepted");

    const std::string multiple_lines = valid + valid;
    require(
        guance_rum_write_line(handle, multiple_lines.data(), multiple_lines.size()) == 0,
        "multiple line payload was accepted");

    const std::string browser_full_snapshot =
        "{\"type\":2,\"timestamp\":1722300000123,\"data\":{\"node\":{\"id\":1}}}";
    require(
        guance_rum_capture_browser_replay_record(
            handle,
            "browser-native-session",
            "browser-view-id",
            browser_full_snapshot.data(),
            browser_full_snapshot.size(),
            1722300000123,
            1) == 1,
        "valid Browser Session Replay record was rejected");
    require(
        guance_rum_capture_browser_replay_record(
            handle,
            "browser-native-session",
            "",
            browser_full_snapshot.data(),
            browser_full_snapshot.size(),
            1722300000123,
            1) == 0,
        "invalid Browser Session Replay view id was accepted");

    const guance_rum_launch cold_launch{
        GUANCE_RUM_LAUNCH_COLD,
        1722300000000000000,
        300000000,
        100000000,
        100000000,
        100000000};
    guance_rum_add_launch_action(handle, &cold_launch);
    const guance_rum_launch hot_launch{
        GUANCE_RUM_LAUNCH_HOT,
        1722300010000000000,
        50000000,
        0,
        0,
        0};
    guance_rum_add_launch_action(handle, &hot_launch);

    guance_rum_diagnostics diagnostics{};
    require(
        guance_rum_get_diagnostics(handle, &diagnostics) == 1,
        "native diagnostics were unavailable");
    require(
        diagnostics.rum_events_enqueued == measurements.size() + 2,
        "Browser RUM and launch actions were not all enqueued");
    require(
        diagnostics.session_replay_sampled == 1,
        "Browser Session Replay did not use native replay sampling");

    auto queue_directory = queue_path;
    queue_directory.replace_extension(".queue");
    if (std::filesystem::exists(queue_directory)) {
        std::string queued_lines;
        for (const auto& entry : std::filesystem::directory_iterator(queue_directory)) {
            std::ifstream input(entry.path(), std::ios::binary);
            queued_lines.append(
                std::istreambuf_iterator<char>(input),
                std::istreambuf_iterator<char>());
        }
        require(
            queued_lines.find("action_type=launch_cold") != std::string::npos &&
                queued_lines.find("action_name=app\\ cold\\ start") != std::string::npos,
            "cold launch action contract was not persisted");
        require(
            queued_lines.find(
                "app_pre_application_init_time=\"{\\\"start\\\":0,"
                "\\\"duration\\\":100000000}\"") != std::string::npos &&
                queued_lines.find("app_application_init_time=") != std::string::npos &&
                queued_lines.find("app_first_frame_init_time=") != std::string::npos,
            "cold launch phase fields were not persisted");
        require(
            queued_lines.find("action_type=launch_hot") != std::string::npos &&
                queued_lines.find("action_name=app\\ hot\\ start") != std::string::npos,
            "hot launch action contract was not persisted");
    }

    guance_rum_shutdown(handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(queue_directory, cleanup_error);
    std::cout << "native browser bridge smoke passed\n";
    return 0;
}
