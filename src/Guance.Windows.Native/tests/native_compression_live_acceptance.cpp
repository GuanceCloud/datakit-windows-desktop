#include "guance_sdk.h"

#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>

namespace {

void require(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

std::string required_environment(const char* name) {
    const char* value = std::getenv(name);
    if (value == nullptr || value[0] == '\0') {
        throw std::runtime_error(std::string{"Missing environment variable: "} + name);
    }
    return value;
}

} // namespace

int main() {
    std::filesystem::path cache_directory;
    guance_sdk_handle sdk = nullptr;
    try {
        const auto datakit = required_environment(
            "GUANCE_RUM_NATIVE_ACCEPTANCE_DATAKIT_URL");
        const auto acceptance_id = "native-compression-" + std::to_string(
            std::chrono::system_clock::now().time_since_epoch().count());
        cache_directory = std::filesystem::temp_directory_path() / acceptance_id;
        std::filesystem::create_directories(cache_directory);
        const auto cache_path = (cache_directory / "cache").string();

        guance_sdk_config config{};
        guance_sdk_config_init(&config);
        require(config.compress_intake_requests == 1, "Compression was not enabled by default.");
        config.datakit_url = datakit.c_str();
        config.rum_app_id = "windows-native-compression-live-acceptance";
        config.service_name = "windows-native-compression-live-acceptance";
        config.env = "local";
        config.version = "1.0.0";
        config.cache_path = cache_path.c_str();
        config.sample_rate = 1.0;
        config.http_timeout_ms = 10000;
        config.flush_interval_ms = 1000;
        sdk = guance_sdk_init(&config);
        require(sdk != nullptr, "Unable to initialize native SDK.");

        guance_log_config logging{};
        guance_log_config_init(&logging);
        logging.enable_custom_log = 1;
        logging.enable_link_rum_data = 1;
        require(guance_log_configure(sdk, &logging) == 1, "Unable to configure logging.");

        guance_sdk_add_global_context(sdk, "acceptance_id", acceptance_id.c_str());
        guance_rum_start_view(sdk, "NativeCompressionLiveAcceptance");
        const auto action_id = guance_rum_start_action_ext(
            sdk,
            "SendCompressedIntake",
            "test",
            1);
        const guance_log_property properties[] = {
            {"acceptance_id", acceptance_id.c_str()},
            {"compression", "deflate"}
        };
        require(
            guance_log_add(
                sdk,
                "Native compressed intake live acceptance.",
                "info",
                properties,
                2) == 1,
            "Unable to enqueue live acceptance log.");
        guance_rum_add_long_task(sdk, 25'000'000, "native compression live acceptance");
        if (action_id != nullptr && action_id[0] != '\0') {
            guance_rum_stop_action(sdk, action_id);
        }
        guance_rum_stop_view(sdk);

        guance_sdk_diagnostics rum{};
        guance_log_diagnostics log{};
        guance_log_diagnostics_init(&log);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(15);
        while (std::chrono::steady_clock::now() < deadline) {
            require(guance_sdk_get_diagnostics(sdk, &rum) == 1, "Unable to read RUM diagnostics.");
            require(guance_log_get_diagnostics(sdk, &log) == 1, "Unable to read log diagnostics.");
            if (rum.rum_upload_success_count >= 1 && log.upload_success_count >= 1) {
                break;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        require(rum.rum_upload_success_count >= 1, "Compressed RUM upload did not succeed.");
        require(log.upload_success_count >= 1, "Compressed log upload did not succeed.");

        std::cout << "native_compression_acceptance=passed"
                  << " acceptance_id=" << acceptance_id
                  << " rum_status=" << rum.last_rum_upload_status_code
                  << " log_status=" << log.last_upload_status_code
                  << " rum_upload_success=" << rum.rum_upload_success_count
                  << " log_upload_success=" << log.upload_success_count
                  << std::endl;
        guance_sdk_shutdown(sdk);
        sdk = nullptr;
    } catch (const std::exception& error) {
        if (sdk != nullptr) {
            guance_sdk_shutdown(sdk);
        }
        std::cerr << "native_compression_acceptance=failed error="
                  << error.what() << std::endl;
        if (!cache_directory.empty()) {
            std::error_code cleanup_error;
            std::filesystem::remove_all(cache_directory, cleanup_error);
        }
        return 1;
    }

    if (!cache_directory.empty()) {
        std::error_code cleanup_error;
        std::filesystem::remove_all(cache_directory, cleanup_error);
    }
    return 0;
}
