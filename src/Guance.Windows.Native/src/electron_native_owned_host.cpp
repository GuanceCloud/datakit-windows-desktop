#include "guance_sdk.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

namespace {

constexpr std::size_t kMaxInputLineBytes = 2 * 1024 * 1024;
constexpr const wchar_t* kDefaultPipeName = L"guance-rum-electron-native-owned";

std::string utf8_from_wide(const std::wstring& value) {
    if (value.empty()) return {};
    const int required = WideCharToMultiByte(
        CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), required, nullptr, nullptr);
    return result;
}

std::wstring read_wide_environment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return {};
    std::wstring value(static_cast<std::size_t>(required), L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) return {};
    value.resize(written);
    return value;
}

std::string read_environment(const wchar_t* name) {
    return utf8_from_wide(read_wide_environment(name));
}

bool read_boolean_environment(const wchar_t* name, bool fallback = false) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    return value == "1" || value == "true" || value == "TRUE";
}

double read_rate_environment(const wchar_t* name, double fallback) {
    const auto value = read_environment(name);
    if (value.empty()) return fallback;
    try {
        const auto parsed = std::stod(value);
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

std::wstring pipe_path() {
    auto pipe_name = read_wide_environment(L"GUANCE_RUM_NATIVE_OWNED_PIPE_NAME");
    if (pipe_name.empty()) pipe_name = kDefaultPipeName;
    const bool safe = pipe_name.size() <= 96 &&
        std::all_of(pipe_name.begin(), pipe_name.end(), [](wchar_t character) {
            return (character >= L'a' && character <= L'z') ||
                   (character >= L'A' && character <= L'Z') ||
                   (character >= L'0' && character <= L'9') ||
                   character == L'_' || character == L'.' || character == L'-';
        });
    if (!safe) return {};
    return L"\\\\.\\pipe\\" + pipe_name;
}

std::string percent_encode(const std::string& input) {
    static constexpr char hex[] = "0123456789ABCDEF";
    std::string output;
    for (const unsigned char character : input) {
        if (std::isalnum(character) || character == '-' || character == '_' ||
            character == '.' || character == '~' || character == ',') {
            output.push_back(static_cast<char>(character));
        } else {
            output.push_back('%');
            output.push_back(hex[character >> 4]);
            output.push_back(hex[character & 0x0f]);
        }
    }
    return output;
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
    double rum_sample_rate = 1.0;
    bool log_enabled = false;
    double log_sample_rate = 1.0;
    bool replay_enabled = false;
    double replay_sample_rate = 1.0;
    std::string replay_privacy = "mask";
    bool trace_enabled = false;
    double trace_sample_rate = 1.0;
    std::string trace_type = "w3c_traceparent";
    std::string trace_allowed_urls;
    bool debug = false;
    bool exit_on_disconnect = false;
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
    config.rum_sample_rate = read_rate_environment(L"GUANCE_RUM_NATIVE_SAMPLE_RATE", 1.0);
    config.log_enabled = read_boolean_environment(L"GUANCE_RUM_NATIVE_LOG_ENABLED");
    config.log_sample_rate = read_rate_environment(L"GUANCE_RUM_NATIVE_LOG_SAMPLE_RATE", 1.0);
    config.replay_enabled = read_boolean_environment(L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED");
    config.replay_sample_rate = read_rate_environment(
        L"GUANCE_RUM_NATIVE_SESSION_REPLAY_SAMPLE_RATE", 1.0);
    const auto replay_privacy = read_environment(L"GUANCE_RUM_NATIVE_REPLAY_PRIVACY_LEVEL");
    if (replay_privacy == "allow" || replay_privacy == "mask-user-input" ||
        replay_privacy == "mask") {
        config.replay_privacy = replay_privacy;
    }
    config.trace_enabled = read_boolean_environment(L"GUANCE_RUM_NATIVE_TRACE_ENABLED");
    config.trace_sample_rate = read_rate_environment(L"GUANCE_RUM_NATIVE_TRACE_SAMPLE_RATE", 1.0);
    const auto trace_type = read_environment(L"GUANCE_RUM_NATIVE_TRACE_TYPE");
    if (!trace_type.empty()) config.trace_type = trace_type;
    config.trace_allowed_urls = read_environment(L"GUANCE_RUM_NATIVE_TRACE_ALLOWED_URLS");
    config.debug = read_boolean_environment(L"GUANCE_RUM_NATIVE_DEBUG");
    config.exit_on_disconnect = read_boolean_environment(
        L"GUANCE_RUM_NATIVE_OWNED_EXIT_ON_DISCONNECT");
    config.http_timeout_ms = read_integer_environment(
        L"GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS", 10000);
    return config;
}

std::string capabilities(const HostConfiguration& host) {
    std::ostringstream output;
    output << "@guance-capabilities"
           << "\tprotocol=1"
           << "\trum=" << (host.rum_sample_rate > 0.0 ? 1 : 0)
           << "\tlog=" << (host.log_enabled ? 1 : 0)
           << "\treplay=" << (host.replay_enabled ? 1 : 0)
           << "\treplay_privacy=" << host.replay_privacy
           << "\ttrace=" << (host.trace_enabled ? 1 : 0)
           << "\ttrace_sample_rate=" << (host.trace_sample_rate * 100.0)
           << "\ttrace_type=" << host.trace_type
           << "\ttrace_allowed_urls=" << percent_encode(host.trace_allowed_urls)
           << "\tdebug=" << (host.debug ? 1 : 0)
           << "\n";
    return output.str();
}

bool write_all(HANDLE pipe, const std::string& value) {
    std::size_t offset = 0;
    while (offset < value.size()) {
        DWORD written = 0;
        if (!WriteFile(
                pipe,
                value.data() + offset,
                static_cast<DWORD>(value.size() - offset),
                &written,
                nullptr) || written == 0) {
            return false;
        }
        offset += written;
    }
    return true;
}

void serve_client(HANDLE pipe, guance_sdk_handle sdk, bool debug) {
    std::string pending;
    char buffer[64 * 1024];
    while (true) {
        DWORD read = 0;
        if (!ReadFile(pipe, buffer, static_cast<DWORD>(sizeof(buffer)), &read, nullptr)) {
            const auto error = GetLastError();
            if (error != ERROR_BROKEN_PIPE && error != ERROR_NO_DATA && debug) {
                std::cerr << "[Guance.RUM.NativeOwnedHost] pipe read failed error=" << error << std::endl;
            }
            return;
        }
        pending.append(buffer, read);
        if (pending.size() > kMaxInputLineBytes && pending.find('\n') == std::string::npos) {
            std::cerr << "[Guance.RUM.NativeOwnedHost] rejected oversized bridge input" << std::endl;
            return;
        }
        std::size_t newline = 0;
        while ((newline = pending.find('\n')) != std::string::npos) {
            auto line = pending.substr(0, newline);
            pending.erase(0, newline + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (guance_sdk_write_electron_bridge_line(
                    sdk,
                    line.data(),
                    line.size()) != 1) {
                std::cerr << "[Guance.RUM.NativeOwnedHost] rejected invalid bridge input" << std::endl;
            }
        }
    }
}

} // namespace

int main() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    const auto host = load_configuration();
    const auto native_pipe_path = pipe_path();
    if (native_pipe_path.empty() ||
        (host.dataway_url.empty() && host.datakit_url.empty()) ||
        host.app_id.empty()) {
        std::cerr << "[Guance.RUM.NativeOwnedHost] missing pipe, ingestion URL, or RUM app id" << std::endl;
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
    logging.enable_custom_log = host.log_enabled ? 1 : 0;
    logging.enable_link_rum_data = 1;
    logging.sample_rate = host.log_sample_rate;
    if (guance_log_configure(sdk, &logging) != 1) {
        guance_sdk_shutdown(sdk);
        return 3;
    }

    guance_trace_config trace{};
    guance_trace_config_init(&trace);
    trace.enable_auto_trace = host.trace_enabled ? 1 : 0;
    trace.enable_link_rum_data = 1;
    trace.sample_rate = host.trace_sample_rate;
    trace.trace_type = trace_type_from_name(host.trace_type);
    if (guance_trace_configure(sdk, &trace) != 1) {
        guance_sdk_shutdown(sdk);
        return 3;
    }
    if (host.replay_enabled) guance_rum_start_session_replay(sdk);

    std::atomic<bool> stopping{false};
    std::mutex flush_mutex;
    std::condition_variable flush_wakeup;
    std::thread flush_worker([&]() {
        std::unique_lock lock(flush_mutex);
        while (!flush_wakeup.wait_for(lock, std::chrono::seconds(1), [&]() {
            return stopping.load();
        })) {
            lock.unlock();
            guance_sdk_flush(sdk);
            lock.lock();
        }
    });

    const auto handshake = capabilities(host);
    std::cout << "[Guance.RUM.NativeOwnedHost] ready pipe="
              << utf8_from_wide(native_pipe_path)
              << " sdk_version=" << guance_sdk_get_version()
              << std::endl;

    bool keep_running = true;
    while (keep_running) {
        HANDLE pipe = CreateNamedPipeW(
            native_pipe_path.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            64 * 1024,
            64 * 1024,
            0,
            nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            std::cerr << "[Guance.RUM.NativeOwnedHost] CreateNamedPipe failed error="
                      << GetLastError() << std::endl;
            break;
        }
        const bool connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
            GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected && write_all(pipe, handshake)) {
            serve_client(pipe, sdk, host.debug);
        }
        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        guance_sdk_flush(sdk);
        keep_running = !host.exit_on_disconnect;
    }

    stopping.store(true);
    flush_wakeup.notify_all();
    flush_worker.join();
    if (host.replay_enabled) guance_rum_stop_session_replay(sdk);
    guance_sdk_shutdown(sdk);
    return 0;
}
