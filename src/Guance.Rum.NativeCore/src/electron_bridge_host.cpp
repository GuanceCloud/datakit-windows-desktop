#include "guance_rum.h"

#include <windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>

namespace {

constexpr std::size_t kMaxInputLineBytes = 2 * 1024 * 1024;
constexpr auto kFlushInterval = std::chrono::seconds(1);
constexpr const char* kLaunchCommandPrefix = "@guance-launch\t";
constexpr const char* kErrorCommandPrefix = "@guance-error\t";
constexpr const char* kReplayCommandPrefix = "@guance-replay\t";

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
    bool session_replay_enabled = false;
    double session_replay_sample_rate = 1.0;
    double session_replay_on_error_sample_rate = 0.0;
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
    config.session_replay_enabled =
        read_boolean_environment(L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED");
    config.session_replay_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE",
        1.0);
    config.session_replay_on_error_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE",
        0.0);
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
           left.replay_upload_success_count == right.replay_upload_success_count &&
           left.replay_upload_retry_count == right.replay_upload_retry_count &&
           left.replay_upload_terminal_failure_count == right.replay_upload_terminal_failure_count &&
           left.last_rum_upload_status_code == right.last_rum_upload_status_code &&
           left.last_replay_upload_status_code == right.last_replay_upload_status_code &&
           left.last_rum_upload_error_code == right.last_rum_upload_error_code &&
           left.last_replay_upload_error_code == right.last_replay_upload_error_code &&
           left.last_rum_upload_latency_ms == right.last_rum_upload_latency_ms &&
           left.last_replay_upload_latency_ms == right.last_replay_upload_latency_ms;
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
        << " replay_success=" << diagnostics.replay_upload_success_count
        << " replay_retry=" << diagnostics.replay_upload_retry_count
        << " replay_terminal=" << diagnostics.replay_upload_terminal_failure_count
        << " replay_status=" << diagnostics.last_replay_upload_status_code
        << " replay_error=" << diagnostics.last_replay_upload_error_code
        << " replay_latency_ms=" << diagnostics.last_replay_upload_latency_ms
        << std::endl;
}

enum class ControlCommandResult {
    not_command,
    accepted,
    rejected
};

bool parse_int64(const std::string& value, int64_t& result) {
    try {
        std::size_t consumed = 0;
        result = std::stoll(value, &consumed);
        return consumed == value.size();
    } catch (...) {
        return false;
    }
}

ControlCommandResult handle_launch_command(
    guance_rum_handle handle,
    const std::string& line,
    bool debug) {
    if (line.rfind(kLaunchCommandPrefix, 0) != 0) {
        return ControlCommandResult::not_command;
    }

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-launch") {
        return ControlCommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return ControlCommandResult::rejected;
        }
    }
    if (fields.size() != 6) {
        return ControlCommandResult::rejected;
    }

    const auto type = fields.find("type");
    if (type == fields.end() || (type->second != "cold" && type->second != "hot")) {
        return ControlCommandResult::rejected;
    }

    guance_rum_launch launch{};
    launch.type = type->second == "hot"
        ? GUANCE_RUM_LAUNCH_HOT
        : GUANCE_RUM_LAUNCH_COLD;
    if (!parse_int64(fields["start_time_ns"], launch.start_time_ns) ||
        !parse_int64(fields["duration_ns"], launch.duration_ns) ||
        !parse_int64(
            fields["pre_application_duration_ns"],
            launch.pre_application_duration_ns) ||
        !parse_int64(
            fields["application_duration_ns"],
            launch.application_duration_ns) ||
        !parse_int64(
            fields["first_frame_duration_ns"],
            launch.first_frame_duration_ns)) {
        return ControlCommandResult::rejected;
    }
    guance_rum_add_launch_action(handle, &launch);
    if (debug) {
        std::cout
            << "[Guance.RUM.NativeBridge] launch"
            << " type=launch_" << type->second
            << " duration_ns=" << launch.duration_ns
            << std::endl;
    }
    return ControlCommandResult::accepted;
}

int hex_value(char value) {
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    if (value >= 'a' && value <= 'f') {
        return value - 'a' + 10;
    }
    if (value >= 'A' && value <= 'F') {
        return value - 'A' + 10;
    }
    return -1;
}

bool percent_decode(const std::string& input, std::string& output) {
    output.clear();
    output.reserve(input.size());
    for (std::size_t index = 0; index < input.size(); index++) {
        if (input[index] != '%') {
            output.push_back(input[index]);
            continue;
        }
        if (index + 2 >= input.size()) {
            return false;
        }
        const int high = hex_value(input[index + 1]);
        const int low = hex_value(input[index + 2]);
        if (high < 0 || low < 0) {
            return false;
        }
        const char decoded = static_cast<char>((high << 4) | low);
        if (decoded == '\0' || decoded == '\r' || decoded == '\n') {
            return false;
        }
        output.push_back(decoded);
        index += 2;
    }
    return true;
}

int base64_value(char value) {
    if (value >= 'A' && value <= 'Z') return value - 'A';
    if (value >= 'a' && value <= 'z') return value - 'a' + 26;
    if (value >= '0' && value <= '9') return value - '0' + 52;
    if (value == '+') return 62;
    if (value == '/') return 63;
    return -1;
}

bool base64_decode(const std::string& input, std::string& output) {
    output.clear();
    if (input.empty() || input.size() % 4 != 0) {
        return false;
    }
    output.reserve((input.size() / 4) * 3);
    for (std::size_t index = 0; index < input.size(); index += 4) {
        const bool third_padding = input[index + 2] == '=';
        const bool fourth_padding = input[index + 3] == '=';
        if ((third_padding && !fourth_padding) ||
            (index + 4 != input.size() && (third_padding || fourth_padding))) {
            return false;
        }
        const int first = base64_value(input[index]);
        const int second = base64_value(input[index + 1]);
        const int third = third_padding ? 0 : base64_value(input[index + 2]);
        const int fourth = fourth_padding ? 0 : base64_value(input[index + 3]);
        if (first < 0 || second < 0 || third < 0 || fourth < 0) {
            return false;
        }
        output.push_back(static_cast<char>((first << 2) | (second >> 4)));
        if (!third_padding) {
            output.push_back(static_cast<char>(((second & 0x0f) << 4) | (third >> 2)));
        }
        if (!fourth_padding) {
            output.push_back(static_cast<char>(((third & 0x03) << 6) | fourth));
        }
    }
    return true;
}

ControlCommandResult handle_replay_command(
    guance_rum_handle handle,
    const std::string& line,
    bool debug) {
    if (line.rfind(kReplayCommandPrefix, 0) != 0) {
        return ControlCommandResult::not_command;
    }

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-replay") {
        return ControlCommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return ControlCommandResult::rejected;
        }
    }
    if (fields.size() != 5) {
        return ControlCommandResult::rejected;
    }

    std::string session_id;
    std::string view_id;
    std::string record_json;
    int64_t timestamp_ms = 0;
    const auto full_snapshot = fields.find("full_snapshot");
    if (!percent_decode(fields["session_id"], session_id) ||
        session_id.empty() || session_id.size() > 128 ||
        !percent_decode(fields["view_id"], view_id) ||
        view_id.empty() || view_id.size() > 128 ||
        !parse_int64(fields["timestamp_ms"], timestamp_ms) || timestamp_ms <= 0 ||
        full_snapshot == fields.end() ||
        (full_snapshot->second != "0" && full_snapshot->second != "1") ||
        !base64_decode(fields["record"], record_json) ||
        record_json.empty() || record_json.size() > 1024 * 1024) {
        return ControlCommandResult::rejected;
    }

    if (guance_rum_capture_browser_replay_record(
            handle,
            session_id.c_str(),
            view_id.c_str(),
            record_json.data(),
            record_json.size(),
            timestamp_ms,
            full_snapshot->second == "1" ? 1 : 0) != 1) {
        return ControlCommandResult::rejected;
    }
    if (debug) {
        std::cout
            << "[Guance.RUM.NativeBridge] Browser replay"
            << " session_id=" << session_id
            << " view_id=" << view_id
            << " bytes=" << record_json.size()
            << std::endl;
    }
    return ControlCommandResult::accepted;
}

ControlCommandResult handle_error_command(
    guance_rum_handle handle,
    const std::string& line,
    bool debug) {
    if (line.rfind(kErrorCommandPrefix, 0) != 0) {
        return ControlCommandResult::not_command;
    }

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-error") {
        return ControlCommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return ControlCommandResult::rejected;
        }
    }
    if (fields.size() != 2) {
        return ControlCommandResult::rejected;
    }

    const auto type = fields.find("type");
    const bool supported_type = type != fields.end() &&
                                (type->second == "ElectronRendererProcessGone" ||
                                 type->second == "ElectronRendererUnresponsive");
    std::string message;
    if (!supported_type ||
        !percent_decode(fields["message"], message) ||
        message.empty() ||
        message.size() > 2048) {
        return ControlCommandResult::rejected;
    }

    guance_rum_add_error(
        handle,
        "",
        message.c_str(),
        type->second.c_str(),
        "logger");
    if (debug) {
        std::cout
            << "[Guance.RUM.NativeBridge] Electron process failure"
            << " type=" << type->second
            << std::endl;
    }
    return ControlCommandResult::accepted;
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
        config.session_replay_enabled = host.session_replay_enabled ? 1 : 0;
        config.session_replay_sample_rate = host.session_replay_sample_rate;
        config.session_replay_on_error_sample_rate =
            host.session_replay_on_error_sample_rate;
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
            << " replay_experimental=" << (host.session_replay_enabled ? "enabled" : "disabled")
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
            const auto launch_result = handle_launch_command(handle, line, host.debug);
            if (launch_result == ControlCommandResult::accepted) {
                continue;
            }
            if (launch_result == ControlCommandResult::rejected) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid launch command" << std::endl;
                continue;
            }
            const auto error_result = handle_error_command(handle, line, host.debug);
            if (error_result == ControlCommandResult::accepted) {
                continue;
            }
            if (error_result == ControlCommandResult::rejected) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid error command" << std::endl;
                continue;
            }
            const auto replay_result = handle_replay_command(handle, line, host.debug);
            if (replay_result == ControlCommandResult::accepted) {
                continue;
            }
            if (replay_result == ControlCommandResult::rejected) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid replay command" << std::endl;
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
