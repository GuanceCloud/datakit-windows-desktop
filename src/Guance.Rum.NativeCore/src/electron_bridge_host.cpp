#include "guance_rum.h"

#include <windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>

namespace {

constexpr std::size_t kMaxInputLineBytes = 1024 * 1024;
constexpr auto kFlushInterval = std::chrono::seconds(1);

std::string utf8_from_wide(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }
    const int required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) {
        return {};
    }
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required,
        nullptr,
        nullptr);
    return result;
}

std::string read_environment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) {
        return {};
    }
    std::wstring value(static_cast<std::size_t>(required), L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) {
        return {};
    }
    value.resize(written);
    return utf8_from_wide(value);
}

bool read_boolean_environment(const wchar_t* name) {
    const auto value = read_environment(name);
    return value == "1" || value == "true" || value == "TRUE";
}

double read_rate_environment(const wchar_t* name, double fallback) {
    const auto value = read_environment(name);
    if (value.empty()) {
        return fallback;
    }
    try {
        return std::stod(value);
    } catch (...) {
        return fallback;
    }
}

int read_integer_environment(const wchar_t* name, int fallback) {
    const auto value = read_environment(name);
    if (value.empty()) {
        return fallback;
    }
    try {
        return std::stoi(value);
    } catch (...) {
        return fallback;
    }
}

struct HostConfiguration {
    std::string dataway_url;
    std::string datakit_url;
    std::string client_token;
    std::string app_id;
    std::string service;
    std::string env;
    std::string version;
    std::string cache_path;
    std::string proxy_url;
    double sample_rate = 1.0;
    bool debug = false;
    int http_timeout_ms = 10000;
};

HostConfiguration load_configuration() {
    HostConfiguration config;
    config.dataway_url = read_environment(L"GUANCE_RUM_NATIVE_DATAWAY_URL");
    config.datakit_url = read_environment(L"GUANCE_RUM_NATIVE_DATAKIT_URL");
    config.client_token = read_environment(L"GUANCE_RUM_NATIVE_CLIENT_TOKEN");
    config.app_id = read_environment(L"GUANCE_RUM_NATIVE_APP_ID");
    config.service = read_environment(L"GUANCE_RUM_NATIVE_SERVICE");
    config.env = read_environment(L"GUANCE_RUM_NATIVE_ENV");
    config.version = read_environment(L"GUANCE_RUM_NATIVE_VERSION");
    config.cache_path = read_environment(L"GUANCE_RUM_NATIVE_CACHE_PATH");
    config.proxy_url = read_environment(L"GUANCE_RUM_NATIVE_PROXY_URL");
    config.sample_rate = read_rate_environment(L"GUANCE_RUM_NATIVE_SAMPLE_RATE", 1.0);
    config.debug = read_boolean_environment(L"GUANCE_RUM_NATIVE_DEBUG");
    config.http_timeout_ms =
        read_integer_environment(L"GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS", 10000);
    return config;
}

bool same_diagnostics(
    const guance_rum_diagnostics& left,
    const guance_rum_diagnostics& right) {
    return left.rum_events_enqueued == right.rum_events_enqueued &&
           left.rum_upload_success_count == right.rum_upload_success_count &&
           left.rum_upload_retry_count == right.rum_upload_retry_count &&
           left.rum_upload_terminal_failure_count == right.rum_upload_terminal_failure_count &&
           left.last_rum_upload_status_code == right.last_rum_upload_status_code &&
           left.last_rum_upload_error_code == right.last_rum_upload_error_code &&
           left.last_rum_upload_latency_ms == right.last_rum_upload_latency_ms;
}

void log_diagnostics(
    guance_rum_handle handle,
    guance_rum_diagnostics& previous,
    bool& has_previous) {
    guance_rum_diagnostics diagnostics{};
    if (guance_rum_get_diagnostics(handle, &diagnostics) != 1) {
        return;
    }
    if (has_previous && same_diagnostics(previous, diagnostics)) {
        return;
    }
    previous = diagnostics;
    has_previous = true;
    std::cout
        << "[Guance.RUM.NativeBridge] flush"
        << " enqueued=" << diagnostics.rum_events_enqueued
        << " success=" << diagnostics.rum_upload_success_count
        << " retry=" << diagnostics.rum_upload_retry_count
        << " terminal=" << diagnostics.rum_upload_terminal_failure_count
        << " status=" << diagnostics.last_rum_upload_status_code
        << " error=" << diagnostics.last_rum_upload_error_code
        << " latency_ms=" << diagnostics.last_rum_upload_latency_ms
        << std::endl;
}

} // namespace

int main() {
    try {
        const auto host = load_configuration();
        if ((host.dataway_url.empty() && host.datakit_url.empty()) || host.app_id.empty()) {
            std::cerr
                << "[Guance.RUM.NativeBridge] missing ingestion URL or RUM application id"
                << std::endl;
            return 2;
        }

        guance_rum_config config{};
        guance_rum_config_init(&config);
        config.dataway_url = host.dataway_url.c_str();
        config.datakit_url = host.datakit_url.c_str();
        config.client_token = host.client_token.c_str();
        config.rum_app_id = host.app_id.c_str();
        config.service_name = host.service.c_str();
        config.env = host.env.c_str();
        config.version = host.version.c_str();
        config.cache_path = host.cache_path.c_str();
        config.proxy_url = host.proxy_url.c_str();
        config.sample_rate = host.sample_rate;
        config.debug = host.debug ? 1 : 0;
        config.http_timeout_ms = host.http_timeout_ms;

        guance_rum_handle handle = guance_rum_init(&config);
        if (handle == nullptr) {
            std::cerr << "[Guance.RUM.NativeBridge] C++ Core initialization failed" << std::endl;
            return 3;
        }

        std::cout
            << "[Guance.RUM.NativeBridge] ready"
            << " transport=" << (host.dataway_url.empty() ? "datakit" : "dataway")
            << " app_id=" << host.app_id
            << std::endl;

        std::atomic<bool> stopping{false};
        std::mutex flush_mutex;
        std::condition_variable flush_wakeup;
        guance_rum_diagnostics previous_diagnostics{};
        bool has_previous_diagnostics = false;
        std::thread flush_worker([&]() {
            std::unique_lock lock(flush_mutex);
            while (!flush_wakeup.wait_for(lock, kFlushInterval, [&]() {
                return stopping.load();
            })) {
                lock.unlock();
                guance_rum_flush(handle);
                if (host.debug) {
                    log_diagnostics(
                        handle,
                        previous_diagnostics,
                        has_previous_diagnostics);
                }
                lock.lock();
            }
        });

        std::string line;
        while (std::getline(std::cin, line)) {
            if (line.size() >= kMaxInputLineBytes) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected oversized input line" << std::endl;
                continue;
            }
            line.push_back('\n');
            if (guance_rum_write_line(handle, line.data(), line.size()) != 1) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid RUM line" << std::endl;
            }
        }

        stopping.store(true);
        flush_wakeup.notify_all();
        flush_worker.join();
        guance_rum_flush(handle);
        if (host.debug) {
            log_diagnostics(
                handle,
                previous_diagnostics,
                has_previous_diagnostics);
        }
        guance_rum_shutdown(handle);
        std::cout << "[Guance.RUM.NativeBridge] stopped" << std::endl;
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "[Guance.RUM.NativeBridge] fatal: " << error.what() << std::endl;
        return 4;
    } catch (...) {
        std::cerr << "[Guance.RUM.NativeBridge] fatal: unknown error" << std::endl;
        return 5;
    }
}
