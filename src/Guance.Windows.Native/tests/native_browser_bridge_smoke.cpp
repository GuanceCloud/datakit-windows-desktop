#include "guance_sdk.h"

#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <regex>
#include <set>
#include <stdexcept>
#include <string>

namespace {

std::filesystem::path unique_queue_path() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
           ("guance-rum-browser-bridge-" + std::to_string(suffix));
}

void require(bool condition, const char* message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

int modify_browser_data(
    const char* key,
    const guance_data_value*,
    guance_data_value* replacement,
    void*) {
    if (std::string(key) == "browser_numeric_tag") {
        replacement->type = GUANCE_DATA_VALUE_INT64;
        replacement->value.int64_value = 42;
        return 1;
    }
    if (std::string(key) == "is_active") {
        replacement->type = GUANCE_DATA_VALUE_BOOL;
        replacement->value.bool_value = 1;
        return 1;
    }
    return 0;
}

} // namespace

int main() {
    const auto queue_path = unique_queue_path();
    const auto queue_path_text = queue_path.string();

    guance_sdk_config config{};
    guance_sdk_config_init(&config);
    config.datakit_url = "http://127.0.0.1:9";
    config.rum_app_id = "electron-browser-bridge-smoke";
    config.cache_path = queue_path_text.c_str();
    config.http_timeout_ms = 50;
    config.session_replay_enabled = 1;
    config.session_replay_sample_rate = 1.0;

    guance_sdk_handle handle = guance_sdk_init(&config);
    require(handle != nullptr, "native core failed to initialize");
    guance_data_modifier_config modifiers{};
    guance_data_modifier_config_init(&modifiers);
    modifiers.data_modifier = modify_browser_data;
    require(
        guance_configure_data_modifiers(handle, &modifiers) == 1,
        "native browser modifiers failed to configure");

    const std::array<const char*, 5> measurements{
        "view",
        "action",
        "resource",
        "error",
        "long_task",
    };
    std::string valid;
    for (const char* measurement : measurements) {
        const std::string resource_url = std::string(measurement) == "resource"
            ? ",resource_url=https://api.example.test/items?token\\=secret"
            : "";
        const std::string view_referrer = std::string(measurement) == "view"
            ? ",view_referrer=https://ref.example.test/?token\\=secret"
            : "";
        valid = std::string(measurement) +
            ",app_id=renderer-app,browser_numeric_tag=raw,env=renderer-env," +
            "service=renderer-service,version=renderer-app-version,sdk_name=renderer-sdk," +
            "sdk_version=renderer-sdk-version,session_id=renderer-session" +
            resource_url + view_referrer + " "
            "is_active=false,time_spent=1i 1722300000000000000";
        require(
            guance_sdk_write_electron_bridge_line(handle, valid.data(), valid.size()) == 1,
            "valid Phase 1 Browser RUM line was rejected");
    }

    const std::string unsupported =
        "log,app_id=electron-browser-bridge-smoke message=\"not rum\" "
        "1722300000000000000";
    require(
        guance_sdk_write_electron_bridge_line(
            handle,
            unsupported.data(),
            unsupported.size()) == 0,
        "unsupported measurement was accepted");

    const std::string multiple_lines = valid + "\n" + valid;
    require(
        guance_sdk_write_electron_bridge_line(
            handle,
            multiple_lines.data(),
            multiple_lines.size()) == 0,
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

    guance_sdk_diagnostics diagnostics{};
    require(
        guance_sdk_get_diagnostics(handle, &diagnostics) == 1,
        "native diagnostics were unavailable");
    require(
        diagnostics.rum_events_enqueued == measurements.size() + 2,
        "Browser RUM and launch actions were not all enqueued");
    require(
        diagnostics.session_replay_sampled == 1,
        "Browser Session Replay did not use native replay sampling");

    const auto queue_directory = queue_path;
    if (std::filesystem::exists(queue_directory)) {
        std::string queued_lines;
        for (const auto& entry : std::filesystem::recursive_directory_iterator(queue_directory)) {
            if (!entry.is_regular_file()) continue;
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
        require(
            queued_lines.find("is_active=true") != std::string::npos,
            "browser bridge data modifier was bypassed");
        require(
            queued_lines.find("browser_numeric_tag=42") != std::string::npos,
            "browser bridge tag modifier did not preserve replacement semantics");
        require(
            queued_lines.find("renderer-session") == std::string::npos &&
                queued_lines.find("renderer-sdk-version") == std::string::npos &&
                queued_lines.find("sdk_name=renderer-sdk") == std::string::npos &&
                queued_lines.find("app_id=renderer-app") == std::string::npos &&
                queued_lines.find("service=renderer-service") == std::string::npos &&
                queued_lines.find("env=renderer-env") == std::string::npos &&
                queued_lines.find("version=renderer-app-version") == std::string::npos,
            "browser bridge retained renderer-owned Native identity");
        require(
            queued_lines.find("app_id=electron-browser-bridge-smoke") != std::string::npos,
            "browser bridge did not use the Native Core application id");
        require(
            queued_lines.find(std::string("sdk_version=") + guance_sdk_get_version()) !=
                std::string::npos,
            "browser bridge did not use the compiled native SDK version");
        std::set<std::string> session_ids;
        const std::regex session_pattern("session_id=([^, \\r\\n]+)");
        for (std::sregex_iterator it(queued_lines.begin(), queued_lines.end(), session_pattern), end;
             it != end;
             ++it) {
            session_ids.insert((*it)[1].str());
        }
        require(
            session_ids.size() == 1,
            "browser and native events did not share one Native Core Session");
        require(
            queued_lines.find("token\\=%3Credacted%3E") != std::string::npos &&
                queued_lines.find("token\\=secret") == std::string::npos,
            "browser bridge URL privacy was bypassed");
    }

    guance_sdk_shutdown(handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(queue_directory, cleanup_error);
    std::cout << "native browser bridge smoke passed\n";
    return 0;
}
