#include "guance_rum.h"

#include <array>
#include <chrono>
#include <filesystem>
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

    guance_rum_diagnostics diagnostics{};
    require(
        guance_rum_get_diagnostics(handle, &diagnostics) == 1,
        "native diagnostics were unavailable");
    require(
        diagnostics.rum_events_enqueued == measurements.size(),
        "all five Phase 1 Browser RUM lines were not enqueued");

    guance_rum_shutdown(handle);
    auto queue_directory = queue_path;
    queue_directory.replace_extension(".queue");
    std::error_code cleanup_error;
    std::filesystem::remove_all(queue_directory, cleanup_error);
    std::cout << "native browser bridge smoke passed\n";
    return 0;
}
