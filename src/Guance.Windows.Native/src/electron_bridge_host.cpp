#include "guance_sdk.h"

#include <windows.h>

#include <crtdbg.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <cstdint>
#include <exception>
#include <iostream>
#include <iterator>
#include <limits>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace {

constexpr std::size_t kMaxInputLineBytes = 2 * 1024 * 1024;
constexpr auto kFlushInterval = std::chrono::seconds(1);
constexpr const char* kLaunchCommandPrefix = "@guance-launch\t";
constexpr const char* kErrorCommandPrefix = "@guance-error\t";
constexpr const char* kReplayCommandPrefix = "@guance-replay\t";
constexpr const char* kLogCommandPrefix = "@guance-log\t";
constexpr const char* kNativeScenarioCommandPrefix = "@guance-native-scenario\t";
constexpr const char* kNativeCrashCommand = "@guance-native-crash";

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

int64_t read_int64_environment(const wchar_t* name, int64_t fallback) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    try {
        return std::stoll(value);
    } catch (...) {
        return fallback;
    }
}

double read_double_environment(const wchar_t* name, double fallback) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    try {
        const auto parsed = std::stod(value);
        return std::isfinite(parsed) && parsed >= 0 ? parsed : fallback;
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
    bool logging_enabled = false;
    double log_sample_rate = 1.0;
    bool session_replay_enabled = false;
    double session_replay_sample_rate = 1.0;
    double session_replay_on_error_sample_rate = 0.0;
    std::string session_replay_privacy = "mask";
    bool debug = false;
    int http_timeout_ms = 10000;
    int64_t max_cache_bytes = 128LL * 1024 * 1024;
    int max_cache_files = 1024;
    int64_t max_cache_age_seconds = 7LL * 24 * 60 * 60;
    int max_batch_items = 50;
    int64_t max_batch_bytes = 512LL * 1024;
    int64_t max_upload_bytes_per_second = 256LL * 1024;
    int64_t upload_burst_bytes = 2LL * 1024 * 1024;
    double max_upload_requests_per_second = 2.0;
    int max_upload_batches_per_cycle = 4;
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
    config.logging_enabled =
        read_boolean_environment(L"GUANCE_RUM_NATIVE_LOG_ENABLED");
    config.log_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE",
        1.0);
    config.session_replay_enabled =
        read_boolean_environment(L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED");
    config.session_replay_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE",
        1.0);
    config.session_replay_on_error_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE",
        0.0);
    const auto replay_privacy = read_environment(
        L"GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL");
    if (replay_privacy == "allow" || replay_privacy == "mask-user-input" ||
        replay_privacy == "mask") {
        config.session_replay_privacy = replay_privacy;
    }
    config.debug = read_boolean_environment(L"GUANCE_RUM_NATIVE_DEBUG");
    config.http_timeout_ms =
        read_integer_environment(L"GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS", 10000);
    config.max_cache_bytes = read_int64_environment(
        L"GUANCE_RUM_NATIVE_MAX_CACHE_BYTES", config.max_cache_bytes);
    config.max_cache_files = read_integer_environment(
        L"GUANCE_RUM_NATIVE_MAX_CACHE_FILES", config.max_cache_files);
    config.max_cache_age_seconds = read_int64_environment(
        L"GUANCE_RUM_NATIVE_MAX_CACHE_AGE_SECONDS", config.max_cache_age_seconds);
    config.max_batch_items = read_integer_environment(
        L"GUANCE_RUM_NATIVE_MAX_BATCH_ITEMS", config.max_batch_items);
    config.max_batch_bytes = read_int64_environment(
        L"GUANCE_RUM_NATIVE_MAX_BATCH_BYTES", config.max_batch_bytes);
    config.max_upload_bytes_per_second = read_int64_environment(
        L"GUANCE_RUM_NATIVE_MAX_UPLOAD_BYTES_PER_SECOND", config.max_upload_bytes_per_second);
    config.upload_burst_bytes = read_int64_environment(
        L"GUANCE_RUM_NATIVE_UPLOAD_BURST_BYTES", config.upload_burst_bytes);
    config.max_upload_requests_per_second = read_double_environment(
        L"GUANCE_RUM_NATIVE_MAX_UPLOAD_REQUESTS_PER_SECOND", config.max_upload_requests_per_second);
    config.max_upload_batches_per_cycle = read_integer_environment(
        L"GUANCE_RUM_NATIVE_MAX_UPLOAD_BATCHES_PER_CYCLE", config.max_upload_batches_per_cycle);
    return config;
}

bool same_diagnostics(
    const guance_sdk_diagnostics& left,
    const guance_sdk_diagnostics& right) {
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
           left.last_replay_upload_latency_ms == right.last_replay_upload_latency_ms &&
           left.cache_allocated_bytes == right.cache_allocated_bytes &&
           left.cache_file_count == right.cache_file_count;
}

void log_diagnostics(
    guance_sdk_handle handle,
    guance_sdk_diagnostics& previous,
    bool& has_previous) {
    guance_sdk_diagnostics diagnostics{};
    if (guance_sdk_get_diagnostics(handle, &diagnostics) != 1) {
        return;
    }
    guance_log_diagnostics log_diagnostics{};
    guance_log_diagnostics_init(&log_diagnostics);
    if (guance_log_get_diagnostics(handle, &log_diagnostics) != 1) {
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
        << " log_enqueued=" << log_diagnostics.logs_enqueued
        << " log_dropped=" << log_diagnostics.logs_dropped
        << " log_success=" << log_diagnostics.upload_success_count
        << " log_retry=" << log_diagnostics.upload_retry_count
        << " log_terminal=" << log_diagnostics.upload_terminal_failure_count
        << " log_status=" << log_diagnostics.last_upload_status_code
        << " log_error=" << log_diagnostics.last_upload_error_code
        << " log_latency_ms=" << log_diagnostics.last_upload_latency_ms
        << " cache_bytes=" << diagnostics.cache_allocated_bytes
        << " cache_files=" << diagnostics.cache_file_count
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

bool percent_decode(const std::string& input, std::string& output);

bool valid_launch_view_value(
    const std::string& value,
    std::size_t maximum_size,
    bool allow_empty) {
    return (allow_empty || !value.empty()) && value.size() <= maximum_size &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return character >= 0x20 && character != 0x7f;
           });
}

bool parse_launch_view(
    const std::unordered_map<std::string, std::string>& fields,
    std::string& view_id,
    std::string& view_name,
    std::string& view_referrer) {
    if (fields.size() == 6) return true;
    if (fields.size() != 9) return false;

    const auto id = fields.find("view_id");
    const auto name = fields.find("view_name");
    const auto referrer = fields.find("view_referrer");
    return id != fields.end() && name != fields.end() && referrer != fields.end() &&
           percent_decode(id->second, view_id) &&
           percent_decode(name->second, view_name) &&
           percent_decode(referrer->second, view_referrer) &&
           valid_launch_view_value(view_id, 128, false) &&
           valid_launch_view_value(view_name, 4096, true) &&
           valid_launch_view_value(view_referrer, 4096, true);
}

ControlCommandResult handle_launch_command(
    guance_sdk_handle handle,
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
    std::string view_id;
    std::string view_name;
    std::string view_referrer;
    if (!parse_launch_view(fields, view_id, view_name, view_referrer)) {
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
    if (view_id.empty()) {
        guance_rum_add_launch_action(handle, &launch);
    } else {
        guance_rum_add_launch_action_ext(
            handle,
            &launch,
            view_id.c_str(),
            view_name.c_str(),
            view_referrer.c_str());
    }
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

bool contains_nul(const std::string& value) {
    return value.find('\0') != std::string::npos;
}

bool valid_log_status(const std::string& value) {
    return !value.empty() && value.size() <= 64 &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return std::isalnum(character) || character == '_' ||
                      character == '.' || character == '-';
           });
}

bool valid_log_property_key(const std::string& value) {
    return !value.empty() && value.size() <= 128 &&
           value != "__proto__" && value != "constructor" && value != "prototype" &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return std::isalnum(character) || character == '_' ||
                      character == '.' || character == '-';
           });
}

bool valid_scenario_id(const std::string& value) {
    return !value.empty() && value.size() <= 96 &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return std::isalnum(character) || character == '_' ||
                      character == '.' || character == '-';
           });
}

ControlCommandResult handle_native_scenario_command(
    guance_sdk_handle handle,
    const std::string& line,
    const HostConfiguration& host) {
    if (line.rfind(kNativeScenarioCommandPrefix, 0) != 0) {
        return ControlCommandResult::not_command;
    }

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-native-scenario") {
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
    if (fields.size() != 4) {
        return ControlCommandResult::rejected;
    }

    std::string scenario_id;
    int64_t window_handle_value = 0;
    int64_t width = 0;
    int64_t height = 0;
    if (!percent_decode(fields["scenario_id"], scenario_id) ||
        !valid_scenario_id(scenario_id) ||
        !parse_int64(fields["window_handle"], window_handle_value) ||
        window_handle_value <= 0 ||
        static_cast<uint64_t>(window_handle_value) >
            static_cast<uint64_t>((std::numeric_limits<uintptr_t>::max)()) ||
        !parse_int64(fields["width"], width) || width <= 0 || width > 10000 ||
        !parse_int64(fields["height"], height) || height <= 0 || height > 10000) {
        return ControlCommandResult::rejected;
    }

    const auto window_handle = static_cast<uintptr_t>(window_handle_value);
    if (!IsWindow(reinterpret_cast<HWND>(window_handle))) {
        return ControlCommandResult::rejected;
    }

    guance_rum_add_rum_context(handle, "acceptance_scenario_id", scenario_id.c_str());
    guance_rum_add_rum_context(handle, "acceptance_origin", "native_cpp");
    guance_rum_add_rum_context(handle, "desktop_runtime", "electron");
    guance_rum_start_view(handle, "electron.native.acceptance");

    if (host.session_replay_enabled) {
        guance_rum_register_replay_window(handle, window_handle);
        guance_rum_start_session_replay(handle);
        guance_rum_capture_replay_resize(
            handle,
            window_handle,
            "Electron native acceptance window",
            static_cast<double>(width),
            static_cast<double>(height));
        guance_rum_capture_replay_click(
            handle,
            window_handle,
            "Run complete native scenario",
            static_cast<double>(width / 2),
            static_cast<double>(height / 2));
        guance_rum_capture_replay_input(
            handle,
            window_handle,
            "Native acceptance masked input");
    }

    const std::string action_id = guance_rum_start_action_ext(
        handle,
        "Complete native acceptance scenario",
        "click",
        1);
    if (action_id.empty()) {
        guance_rum_stop_view(handle);
        return ControlCommandResult::rejected;
    }

    const std::string success_resource_id = guance_rum_start_resource(
        handle,
        "https://native.acceptance.guance.invalid/api/orders/42?outcome=success",
        "GET");
    if (success_resource_id.empty()) {
        guance_rum_stop_action(handle, action_id.c_str());
        guance_rum_stop_view(handle);
        return ControlCommandResult::rejected;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(24));
    guance_rum_stop_resource_ext(
        handle,
        success_resource_id.c_str(),
        200,
        4096,
        384,
        "application/json",
        "0123456789abcdef0123456789abcdef",
        "0123456789abcdef",
        "HTTP/2");

    const std::string failure_resource_id = guance_rum_start_resource(
        handle,
        "https://native.acceptance.guance.invalid/api/orders/42?outcome=failure",
        "POST");
    if (failure_resource_id.empty()) {
        guance_rum_stop_action(handle, action_id.c_str());
        guance_rum_stop_view(handle);
        return ControlCommandResult::rejected;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(16));
    guance_rum_stop_resource_ext(
        handle,
        failure_resource_id.c_str(),
        503,
        512,
        768,
        "application/json",
        "fedcba9876543210fedcba9876543210",
        "fedcba9876543210",
        "HTTP/1.1");

    guance_rum_add_long_task(
        handle,
        320'000'000,
        "NativeAcceptanceScenario::Run -> NativeWorker::Complete");
    guance_rum_add_error(
        handle,
        "NativeAcceptanceScenario::Run\nNativeFailureFixture::Emit",
        "Synthetic handled native acceptance error",
        "NativeAcceptanceError",
        "logger");

    const guance_log_property log_properties[] = {
        {"acceptance_scenario_id", scenario_id.c_str()},
        {"acceptance_origin", "native_cpp"},
        {"native_resource_success", "200"},
        {"native_resource_failure", "503"},
    };
    if (host.logging_enabled) {
        guance_log_add(
            handle,
            "Complete Electron native acceptance scenario emitted",
            "info",
            log_properties,
            static_cast<uint32_t>(std::size(log_properties)));
    }

    guance_rum_stop_action(handle, action_id.c_str());
    guance_rum_add_action(handle, "Native background synchronization", "custom", 180'000'000);
    guance_rum_stop_view(handle);

    if (host.debug) {
        std::cout
            << "[Guance.RUM.NativeBridge] native acceptance scenario"
            << " scenario_id=" << scenario_id
            << " signals=view,action,resource,error,long_task"
            << " resources=2"
            << " log=" << (host.logging_enabled ? "enabled" : "disabled")
            << " replay=" << (host.session_replay_enabled ? "enabled" : "disabled")
            << std::endl;
    }
    return ControlCommandResult::accepted;
}

__declspec(noinline) void controlled_native_crash_leaf() {
    *reinterpret_cast<volatile int*>(0x1) = 42;
}

__declspec(noinline) void controlled_native_crash_worker() {
    volatile int preserve_frame = 1;
    controlled_native_crash_leaf();
    if (preserve_frame != 1) {
        std::abort();
    }
}

[[noreturn]] __declspec(noinline) void trigger_controlled_native_access_violation() {
    controlled_native_crash_worker();
    std::abort();
}

ControlCommandResult handle_native_crash_command(
    const std::string& line,
    bool debug) {
    if (line.rfind(kNativeCrashCommand, 0) != 0) {
        return ControlCommandResult::not_command;
    }
    if (!debug || line != kNativeCrashCommand) {
        return ControlCommandResult::rejected;
    }

    std::cout
        << "[Guance.RUM.NativeBridge] triggering controlled native access violation"
        << std::endl;
    trigger_controlled_native_access_violation();
}

ControlCommandResult handle_log_command(
    guance_sdk_handle handle,
    const std::string& line,
    bool debug) {
    if (line.rfind(kLogCommandPrefix, 0) != 0) {
        return ControlCommandResult::not_command;
    }

    std::istringstream input(line);
    std::vector<std::string> parts;
    std::string part;
    while (std::getline(input, part, '\t')) {
        parts.push_back(std::move(part));
    }
    if (parts.size() < 3 || parts[0] != "@guance-log" ||
        parts[1].rfind("status=", 0) != 0 ||
        parts[2].rfind("message=", 0) != 0 ||
        (parts.size() - 3) % 2 != 0 ||
        (parts.size() - 3) / 2 > 256) {
        return ControlCommandResult::rejected;
    }

    const auto status = parts[1].substr(std::string("status=").size());
    std::string message;
    if (!valid_log_status(status) ||
        !base64_decode(parts[2].substr(std::string("message=").size()), message) ||
        message.empty() || message.size() > 256 * 1024 || contains_nul(message)) {
        return ControlCommandResult::rejected;
    }

    std::vector<std::pair<std::string, std::string>> property_storage;
    property_storage.reserve((parts.size() - 3) / 2);
    for (std::size_t index = 3; index < parts.size(); index += 2) {
        if (parts[index].rfind("property-key=", 0) != 0 ||
            parts[index + 1].rfind("property-value=", 0) != 0) {
            return ControlCommandResult::rejected;
        }
        std::string key;
        std::string value;
        const auto encoded_key =
            parts[index].substr(std::string("property-key=").size());
        const auto encoded_value =
            parts[index + 1].substr(std::string("property-value=").size());
        if (!base64_decode(
                encoded_key,
                key) ||
            (!encoded_value.empty() && !base64_decode(encoded_value, value)) ||
            !valid_log_property_key(key) || value.size() > 64 * 1024 ||
            contains_nul(value)) {
            return ControlCommandResult::rejected;
        }
        property_storage.emplace_back(std::move(key), std::move(value));
    }

    std::vector<guance_log_property> properties;
    properties.reserve(property_storage.size());
    for (const auto& [key, value] : property_storage) {
        properties.push_back({key.c_str(), value.c_str()});
    }
    if (guance_log_add(
            handle,
            message.c_str(),
            status.c_str(),
            properties.data(),
            static_cast<uint32_t>(properties.size())) != 1) {
        return ControlCommandResult::rejected;
    }
    if (debug) {
        std::cout
            << "[Guance.RUM.NativeBridge] Browser log"
            << " status=" << status
            << " properties=" << properties.size()
            << std::endl;
    }
    return ControlCommandResult::accepted;
}

ControlCommandResult handle_replay_command(
    guance_sdk_handle handle,
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
    if (fields.size() != 4) {
        return ControlCommandResult::rejected;
    }

    std::string view_id;
    std::string record_json;
    int64_t timestamp_ms = 0;
    const auto full_snapshot = fields.find("full_snapshot");
    if (!percent_decode(fields["view_id"], view_id) ||
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
            nullptr,
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
            << " view_id=" << view_id
            << " bytes=" << record_json.size()
            << std::endl;
    }
    return ControlCommandResult::accepted;
}

ControlCommandResult handle_error_command(
    guance_sdk_handle handle,
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
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);

    try {
        const auto host = load_configuration();
        if ((host.dataway_url.empty() && host.datakit_url.empty()) || host.app_id.empty()) {
            std::cerr
                << "[Guance.RUM.NativeBridge] missing ingestion URL or RUM application id"
                << std::endl;
            return 2;
        }

        guance_sdk_config config{};
        guance_sdk_config_init(&config);
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
        config.max_cache_bytes = host.max_cache_bytes;
        config.max_cache_files = host.max_cache_files;
        config.max_cache_age_seconds = host.max_cache_age_seconds;
        config.max_batch_items = host.max_batch_items;
        config.max_batch_bytes = host.max_batch_bytes;
        config.max_upload_bytes_per_second = host.max_upload_bytes_per_second;
        config.upload_burst_bytes = host.upload_burst_bytes;
        config.max_upload_requests_per_second = host.max_upload_requests_per_second;
        config.max_upload_batches_per_cycle = host.max_upload_batches_per_cycle;

        guance_sdk_handle handle = guance_sdk_init(&config);
        if (handle == nullptr) {
            std::cerr << "[Guance.RUM.NativeBridge] C++ Core initialization failed" << std::endl;
            return 3;
        }

        guance_log_config logging{};
        guance_log_config_init(&logging);
        logging.enable_custom_log = host.logging_enabled ? 1 : 0;
        logging.enable_link_rum_data = 1;
        logging.sample_rate = host.log_sample_rate;
        if (guance_log_configure(handle, &logging) != 1) {
            guance_sdk_shutdown(handle);
            std::cerr << "[Guance.RUM.NativeBridge] logging configuration failed" << std::endl;
            return 3;
        }
        if (host.session_replay_enabled) {
            guance_rum_start_session_replay(handle);
        }

        if (host.debug) {
            guance_sdk_native_monitoring_config monitoring{};
            guance_sdk_native_monitoring_config_init(&monitoring);
            monitoring.enable_native_crash_reporting = 1;
            monitoring.enable_minidump = 1;
            if (guance_sdk_enable_native_monitoring(handle, &monitoring) != 1) {
                guance_sdk_shutdown(handle);
                std::cerr
                    << "[Guance.RUM.NativeBridge] native crash reporting configuration failed"
                    << std::endl;
                return 3;
            }
        }

        std::cout
            << "@guance-capabilities"
            << "\tprotocol=1"
            << "\trum=1"
            << "\tlog=" << (host.logging_enabled ? 1 : 0)
            << "\treplay=" << (host.session_replay_enabled ? 1 : 0)
            << "\treplay_privacy=" << host.session_replay_privacy
            << "\ttrace=0"
            << "\ttrace_sample_rate=0"
            << "\ttrace_type=w3c_traceparent"
            << "\ttrace_allowed_urls="
            << "\tdebug=" << (host.debug ? 1 : 0)
            << std::endl;
        std::cout
            << "[Guance.RUM.NativeBridge] ready"
            << " transport=" << (host.dataway_url.empty() ? "datakit" : "dataway")
            << " app_id=" << host.app_id
            << " logging=" << (host.logging_enabled ? "enabled" : "disabled")
            << " replay_experimental=" << (host.session_replay_enabled ? "enabled" : "disabled")
            << " crash_recovery=" << (host.debug ? "enabled" : "disabled")
            << std::endl;

        std::atomic<bool> stopping{false};
        std::mutex flush_mutex;
        std::condition_variable flush_wakeup;
        guance_sdk_diagnostics previous_diagnostics{};
        bool has_previous_diagnostics = false;
        std::thread flush_worker([&]() {
            std::unique_lock lock(flush_mutex);
            while (!flush_wakeup.wait_for(lock, kFlushInterval, [&]() {
                return stopping.load();
            })) {
                lock.unlock();
                guance_sdk_flush(handle);
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
            const auto scenario_result = handle_native_scenario_command(handle, line, host);
            if (scenario_result == ControlCommandResult::accepted) {
                continue;
            }
            if (scenario_result == ControlCommandResult::rejected) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid native scenario command" << std::endl;
                continue;
            }
            const auto crash_result = handle_native_crash_command(line, host.debug);
            if (crash_result == ControlCommandResult::accepted) {
                continue;
            }
            if (crash_result == ControlCommandResult::rejected) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid native crash command" << std::endl;
                continue;
            }
            if (guance_sdk_write_electron_bridge_line(
                    handle,
                    line.data(),
                    line.size()) != 1) {
                std::cerr << "[Guance.RUM.NativeBridge] rejected invalid bridge input" << std::endl;
            }
        }

        stopping.store(true);
        flush_wakeup.notify_all();
        flush_worker.join();
        guance_sdk_flush(handle);
        if (host.debug) {
            log_diagnostics(
                handle,
                previous_diagnostics,
                has_previous_diagnostics);
        }
        if (host.session_replay_enabled) {
            guance_rum_stop_session_replay(handle);
        }
        guance_sdk_shutdown(handle);
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
