#include "guance_sdk.h"

#include <windows.h>

#include <cmath>
#include <iostream>
#include <string>

namespace {

constexpr const char* kDefaultPipeName = "guance-rum-electron-native-owned";

std::wstring read_wide_environment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return {};
    std::wstring value(static_cast<std::size_t>(required), L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) return {};
    value.resize(written);
    return value;
}

std::string utf8_from_wide(const std::wstring& value) {
    if (value.empty()) return {};
    const int required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) return {};
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
    return utf8_from_wide(read_wide_environment(name));
}

bool read_boolean_environment(const wchar_t* name) {
    const auto value = read_environment(name);
    return value == "1" || value == "true" || value == "TRUE";
}

double read_rate_environment(const wchar_t* name, double fallback) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    try {
        const double parsed = std::stod(value);
        return std::isfinite(parsed) && parsed >= 0.0 && parsed <= 1.0
            ? parsed
            : fallback;
    } catch (...) {
        return fallback;
    }
}

int read_integer_environment(const wchar_t* name, int fallback) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    try {
        return std::stoi(value);
    } catch (...) {
        return fallback;
    }
}

guance_trace_type trace_type_from_name(const std::string& value) {
    if (value == "ddtrace") return GUANCE_TRACE_DDTRACE;
    if (value == "zipkin") return GUANCE_TRACE_ZIPKIN_MULTI_HEADER;
    if (value == "zipkin_single_header") return GUANCE_TRACE_ZIPKIN_SINGLE_HEADER;
    if (value == "skywalking_v3") return GUANCE_TRACE_SKYWALKING;
    if (value == "jaeger") return GUANCE_TRACE_JAEGER;
    return GUANCE_TRACE_TRACEPARENT;
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
    std::string pipe_name;
    double rum_sample_rate = 1.0;
    bool logging_enabled = false;
    double logging_sample_rate = 1.0;
    bool replay_enabled = false;
    double replay_sample_rate = 1.0;
    std::string replay_privacy = "mask";
    bool trace_enabled = false;
    double trace_sample_rate = 1.0;
    std::string trace_type = "w3c_traceparent";
    std::string trace_allowed_urls;
    bool debug = false;
    int http_timeout_ms = 10'000;
};

HostConfiguration load_configuration() {
    HostConfiguration host;
    host.dataway_url = read_environment(L"GUANCE_RUM_NATIVE_DATAWAY_URL");
    host.datakit_url = read_environment(L"GUANCE_RUM_NATIVE_DATAKIT_URL");
    host.client_token = read_environment(L"GUANCE_RUM_NATIVE_CLIENT_TOKEN");
    host.app_id = read_environment(L"GUANCE_RUM_NATIVE_APP_ID");
    host.service = read_environment(L"GUANCE_RUM_NATIVE_SERVICE");
    host.env = read_environment(L"GUANCE_RUM_NATIVE_ENV");
    host.version = read_environment(L"GUANCE_RUM_NATIVE_VERSION");
    host.cache_path = read_environment(L"GUANCE_RUM_NATIVE_CACHE_PATH");
    host.pipe_name = read_environment(L"GUANCE_RUM_NATIVE_OWNED_PIPE_NAME");
    if (host.pipe_name.empty()) host.pipe_name = kDefaultPipeName;
    host.rum_sample_rate = read_rate_environment(L"GUANCE_RUM_NATIVE_SAMPLE_RATE", 1.0);
    host.logging_enabled = read_boolean_environment(L"GUANCE_RUM_NATIVE_LOG_ENABLED");
    host.logging_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE", 1.0);
    host.replay_enabled = read_boolean_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED");
    host.replay_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE", 1.0);
    const auto replay_privacy = read_environment(
        L"GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL");
    if (!replay_privacy.empty()) host.replay_privacy = replay_privacy;
    host.trace_enabled = read_boolean_environment(L"GUANCE_RUM_NATIVE_TRACE_ENABLED");
    host.trace_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_TRACE_SAMPLE_RATE", 1.0);
    const auto trace_type = read_environment(L"GUANCE_RUM_NATIVE_TRACE_TYPE");
    if (!trace_type.empty()) host.trace_type = trace_type;
    host.trace_allowed_urls = read_environment(
        L"GUANCE_RUM_NATIVE_TRACE_ALLOWED_URLS");
    host.debug = read_boolean_environment(L"GUANCE_RUM_NATIVE_DEBUG");
    host.http_timeout_ms = read_integer_environment(
        L"GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS", 10'000);
    return host;
}

void wait_for_test_stop() {
    const auto stop_event_name = read_wide_environment(
        L"GUANCE_RUM_NATIVE_OWNED_STOP_EVENT");
    if (!stop_event_name.empty()) {
        HANDLE stop_event = OpenEventW(SYNCHRONIZE, FALSE, stop_event_name.c_str());
        if (stop_event != nullptr) {
            WaitForSingleObject(stop_event, INFINITE);
            CloseHandle(stop_event);
            return;
        }
    }
    std::string ignored;
    std::getline(std::cin, ignored);
}

} // namespace

int main() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    const auto host = load_configuration();
    if ((host.dataway_url.empty() && host.datakit_url.empty()) || host.app_id.empty()) {
        std::cerr << "[Guance.RUM.NativeOwnedHost] missing ingestion URL or RUM app id"
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
    config.sample_rate = host.rum_sample_rate;
    config.session_replay_enabled = host.replay_enabled ? 1 : 0;
    config.session_replay_sample_rate = host.replay_sample_rate;
    config.debug = host.debug ? 1 : 0;
    config.http_timeout_ms = host.http_timeout_ms;

    guance_sdk_handle sdk = guance_sdk_init(&config);
    if (sdk == nullptr) {
        std::cerr << "[Guance.RUM.NativeOwnedHost] guance_sdk_init failed" << std::endl;
        return 3;
    }

    guance_log_config logging{};
    guance_log_config_init(&logging);
    logging.enable_custom_log = host.logging_enabled ? 1 : 0;
    logging.enable_link_rum_data = 1;
    logging.sample_rate = host.logging_sample_rate;

    guance_trace_config trace{};
    guance_trace_config_init(&trace);
    trace.enable_auto_trace = host.trace_enabled ? 1 : 0;
    trace.enable_link_rum_data = 1;
    trace.sample_rate = host.trace_sample_rate;
    trace.trace_type = trace_type_from_name(host.trace_type);
    if (guance_log_configure(sdk, &logging) != 1 ||
        guance_trace_configure(sdk, &trace) != 1) {
        guance_sdk_shutdown(sdk);
        return 3;
    }
    if (host.replay_enabled) guance_rum_start_session_replay(sdk);

    guance_electron_bridge_server_options options{};
    guance_electron_bridge_server_options_init(&options);
    options.pipe_name = host.pipe_name.c_str();
    options.logging_enabled = host.logging_enabled ? 1 : 0;
    options.session_replay_enabled = host.replay_enabled ? 1 : 0;
    options.replay_privacy_level = host.replay_privacy.c_str();
    options.trace_enabled = host.trace_enabled ? 1 : 0;
    options.trace_sample_rate = host.trace_sample_rate;
    options.trace_type = host.trace_type.c_str();
    options.trace_allowed_urls = host.trace_allowed_urls.c_str();
    options.debug = host.debug ? 1 : 0;

    guance_electron_bridge_server_handle bridge =
        guance_electron_bridge_server_start(sdk, &options);
    if (bridge == nullptr) {
        if (host.replay_enabled) guance_rum_stop_session_replay(sdk);
        guance_sdk_shutdown(sdk);
        std::cerr << "[Guance.RUM.NativeOwnedHost] Bridge Server start failed"
                  << std::endl;
        return 4;
    }

    std::cout << "[Guance.RUM.NativeOwnedHost] ready pipe=\\\\.\\pipe\\"
              << host.pipe_name
              << " sdk_version=" << guance_sdk_get_version()
              << std::endl;
    wait_for_test_stop();

    guance_electron_bridge_server_stop(bridge);
    guance_sdk_flush(sdk);
    if (host.replay_enabled) guance_rum_stop_session_replay(sdk);
    guance_sdk_shutdown(sdk);
    std::cout << "[Guance.RUM.NativeOwnedHost] stopped" << std::endl;
    return 0;
}
