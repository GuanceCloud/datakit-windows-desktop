#include "guance_sdk.h"

#if defined(_WIN32)

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>

namespace {

constexpr uint32_t kMaximumMessageBytes = 2u * 1024u * 1024u;
constexpr std::size_t kMaximumHandshakeBytes = 16u * 1024u;

bool is_safe_pipe_name(const char* value) {
    if (value == nullptr) return false;
    const std::string name(value);
    return !name.empty() && name.size() <= 96 &&
        std::all_of(name.begin(), name.end(), [](unsigned char character) {
            return (character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') ||
                character == '_' || character == '.' || character == '-';
        });
}

bool is_replay_privacy_level(const char* value) {
    if (value == nullptr) return false;
    const std::string level(value);
    return level == "allow" || level == "mask-user-input" || level == "mask";
}

bool is_trace_type(const char* value) {
    if (value == nullptr) return false;
    const std::string type(value);
    return type == "ddtrace" || type == "zipkin" ||
        type == "zipkin_single_header" || type == "w3c_traceparent" ||
        type == "skywalking_v3" || type == "jaeger";
}

bool is_boolean(int value) {
    return value == 0 || value == 1;
}

std::string percent_encode(const std::string& input) {
    static constexpr char hex[] = "0123456789ABCDEF";
    std::string output;
    output.reserve(input.size());
    for (const unsigned char character : input) {
        if ((character >= 'a' && character <= 'z') ||
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') ||
            character == '-' || character == '_' || character == '.' ||
            character == '~' || character == ',') {
            output.push_back(static_cast<char>(character));
        } else {
            output.push_back('%');
            output.push_back(hex[character >> 4]);
            output.push_back(hex[character & 0x0f]);
        }
    }
    return output;
}

struct ServerConfiguration {
    std::string pipe_name;
    uint32_t max_message_bytes = 0;
    bool logging_enabled = false;
    bool session_replay_enabled = false;
    std::string replay_privacy_level;
    bool trace_enabled = false;
    double trace_sample_rate = 1.0;
    std::string trace_type;
    std::string trace_allowed_urls;
    bool debug = false;
    std::string handshake;
};

bool copy_configuration(
    const guance_electron_bridge_server_options* options,
    ServerConfiguration& output) {
    if (options == nullptr ||
        options->struct_size < sizeof(guance_electron_bridge_server_options) ||
        options->version != GUANCE_ELECTRON_BRIDGE_SERVER_OPTIONS_VERSION ||
        !is_safe_pipe_name(options->pipe_name) ||
        options->max_message_bytes == 0 ||
        options->max_message_bytes > kMaximumMessageBytes ||
        !is_boolean(options->logging_enabled) ||
        !is_boolean(options->session_replay_enabled) ||
        !is_replay_privacy_level(options->replay_privacy_level) ||
        !is_boolean(options->trace_enabled) ||
        !std::isfinite(options->trace_sample_rate) ||
        options->trace_sample_rate < 0.0 || options->trace_sample_rate > 1.0 ||
        !is_trace_type(options->trace_type) ||
        !is_boolean(options->debug)) {
        return false;
    }

    output.pipe_name = options->pipe_name;
    output.max_message_bytes = options->max_message_bytes;
    output.logging_enabled = options->logging_enabled != 0;
    output.session_replay_enabled = options->session_replay_enabled != 0;
    output.replay_privacy_level = options->replay_privacy_level;
    output.trace_enabled = options->trace_enabled != 0;
    output.trace_sample_rate = options->trace_sample_rate;
    output.trace_type = options->trace_type;
    output.trace_allowed_urls = options->trace_allowed_urls == nullptr
        ? std::string{}
        : std::string(options->trace_allowed_urls);
    output.debug = options->debug != 0;

    std::ostringstream handshake;
    handshake << "@guance-capabilities"
              << "\tprotocol=1"
              << "\trum=1"
              << "\tlog=" << (output.logging_enabled ? 1 : 0)
              << "\treplay=" << (output.session_replay_enabled ? 1 : 0)
              << "\treplay_privacy=" << output.replay_privacy_level
              << "\ttrace=" << (output.trace_enabled ? 1 : 0)
              << "\ttrace_sample_rate=" << (output.trace_sample_rate * 100.0)
              << "\ttrace_type=" << output.trace_type
              << "\ttrace_allowed_urls=" << percent_encode(output.trace_allowed_urls)
              << "\tdebug=" << (output.debug ? 1 : 0)
              << '\n';
    output.handshake = handshake.str();
    return output.handshake.size() <= kMaximumHandshakeBytes;
}

class ElectronBridgeServer {
public:
    ElectronBridgeServer(guance_sdk_handle sdk, ServerConfiguration configuration)
        : sdk_(sdk), configuration_(std::move(configuration)) {}

    ~ElectronBridgeServer() {
        stop();
    }

    bool start() {
        std::wstring pipe_path = L"\\\\.\\pipe\\";
        pipe_path.append(configuration_.pipe_name.begin(), configuration_.pipe_name.end());
        pipe_path_ = std::move(pipe_path);

        stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (stop_event_ == nullptr) {
            return false;
        }

        HANDLE pipe = CreateNamedPipeW(
            pipe_path_.c_str(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            64u * 1024u,
            64u * 1024u,
            0,
            nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            CloseHandle(stop_event_);
            stop_event_ = nullptr;
            return false;
        }
        pipe_.store(pipe);

        try {
            worker_ = std::thread([this]() { run(); });
        } catch (...) {
            pipe_.store(INVALID_HANDLE_VALUE);
            CloseHandle(pipe);
            CloseHandle(stop_event_);
            stop_event_ = nullptr;
            return false;
        }
        return true;
    }

    void stop() {
        std::call_once(stop_once_, [this]() {
            stopping_.store(true);
            if (stop_event_ != nullptr) SetEvent(stop_event_);
            const HANDLE pipe = pipe_.load();
            if (pipe != INVALID_HANDLE_VALUE) {
                CancelIoEx(pipe, nullptr);
            }
            if (worker_.joinable()) {
                worker_.join();
            }
            if (stop_event_ != nullptr) {
                CloseHandle(stop_event_);
                stop_event_ = nullptr;
            }
        });
    }

private:
    void log_error(const char* message, DWORD error = ERROR_SUCCESS) const {
        if (!configuration_.debug) return;
        std::cerr << "[Guance.RUM.ElectronBridgeServer] " << message;
        if (error != ERROR_SUCCESS) std::cerr << " error=" << error;
        std::cerr << std::endl;
    }

    bool wait_for_io(HANDLE pipe, OVERLAPPED& operation, DWORD& transferred) {
        const HANDLE handles[] = {stop_event_, operation.hEvent};
        const DWORD wait = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
        if (wait == WAIT_OBJECT_0) {
            CancelIoEx(pipe, &operation);
            GetOverlappedResult(pipe, &operation, &transferred, TRUE);
            return false;
        }
        return wait == WAIT_OBJECT_0 + 1 &&
            GetOverlappedResult(pipe, &operation, &transferred, FALSE) != FALSE;
    }

    bool connect_client(HANDLE pipe) {
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr) return false;
        OVERLAPPED operation{};
        operation.hEvent = event;

        bool connected = ConnectNamedPipe(pipe, &operation) != FALSE;
        if (!connected) {
            const DWORD error = GetLastError();
            if (error == ERROR_PIPE_CONNECTED) {
                connected = true;
            } else if (error == ERROR_IO_PENDING) {
                DWORD transferred = 0;
                connected = wait_for_io(pipe, operation, transferred);
            } else if (!stopping_.load()) {
                log_error("pipe connection failed", error);
            }
        }
        CloseHandle(event);
        return connected;
    }

    bool write_all(HANDLE pipe, const std::string& value) {
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr) return false;
        std::size_t offset = 0;
        while (offset < value.size() && !stopping_.load()) {
            ResetEvent(event);
            OVERLAPPED operation{};
            operation.hEvent = event;
            DWORD written = 0;
            const DWORD remaining = static_cast<DWORD>(value.size() - offset);
            bool completed = WriteFile(
                pipe,
                value.data() + offset,
                remaining,
                &written,
                &operation) != FALSE;
            if (!completed && GetLastError() == ERROR_IO_PENDING) {
                completed = wait_for_io(pipe, operation, written);
            }
            if (!completed || written == 0) {
                CloseHandle(event);
                return false;
            }
            offset += written;
        }
        CloseHandle(event);
        return offset == value.size();
    }

    void serve_client(HANDLE pipe) {
        std::string pending;
        char buffer[64u * 1024u];
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr) return;
        while (!stopping_.load()) {
            ResetEvent(event);
            OVERLAPPED operation{};
            operation.hEvent = event;
            DWORD read = 0;
            bool completed = ReadFile(
                    pipe,
                    buffer,
                    static_cast<DWORD>(sizeof(buffer)),
                    &read,
                    &operation) != FALSE;
            if (!completed && GetLastError() == ERROR_IO_PENDING) {
                completed = wait_for_io(pipe, operation, read);
            }
            if (!completed) {
                const DWORD error = GetLastError();
                if (!stopping_.load() && error != ERROR_BROKEN_PIPE &&
                    error != ERROR_NO_DATA) {
                    log_error("pipe read failed", error);
                }
                CloseHandle(event);
                return;
            }

            pending.append(buffer, read);
            std::size_t newline = 0;
            while ((newline = pending.find('\n')) != std::string::npos) {
                if (newline > configuration_.max_message_bytes) {
                    log_error("rejected oversized bridge input");
                    CloseHandle(event);
                    return;
                }
                std::string line = pending.substr(0, newline);
                pending.erase(0, newline + 1);
                if (!line.empty() && line.back() == '\r') line.pop_back();
                if (guance_sdk_write_electron_bridge_line(
                        sdk_, line.data(), line.size()) != 1) {
                    log_error("rejected invalid bridge input");
                }
            }
            if (pending.size() > configuration_.max_message_bytes) {
                log_error("rejected oversized bridge input");
                CloseHandle(event);
                return;
            }
        }
        CloseHandle(event);
    }

    void run() {
        const HANDLE pipe = pipe_.load();
        while (!stopping_.load()) {
            const bool connected = connect_client(pipe);
            if (!connected) {
                if (stopping_.load()) break;
                continue;
            }

            if (!stopping_.load() && write_all(pipe, configuration_.handshake)) {
                serve_client(pipe);
            }
            if (!stopping_.load()) FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
        }

        pipe_.store(INVALID_HANDLE_VALUE);
        CloseHandle(pipe);
    }

    guance_sdk_handle sdk_ = nullptr;
    ServerConfiguration configuration_;
    std::wstring pipe_path_;
    std::atomic<HANDLE> pipe_{INVALID_HANDLE_VALUE};
    HANDLE stop_event_ = nullptr;
    std::atomic<bool> stopping_{false};
    std::thread worker_;
    std::once_flag stop_once_;
};

std::mutex registry_mutex;
std::unordered_map<uintptr_t, std::shared_ptr<ElectronBridgeServer>> registry;
std::atomic<uintptr_t> next_server_token{1};

} // namespace

extern "C" {

guance_electron_bridge_server_handle guance_electron_bridge_server_start(
    guance_sdk_handle sdk,
    const guance_electron_bridge_server_options* options) {
    if (sdk == nullptr) return nullptr;

    try {
        ServerConfiguration configuration;
        if (!copy_configuration(options, configuration)) return nullptr;

        auto server = std::make_shared<ElectronBridgeServer>(
            sdk, std::move(configuration));
        if (!server->start()) return nullptr;

        uintptr_t token = next_server_token.fetch_add(1);
        if (token == 0) token = next_server_token.fetch_add(1);
        {
            std::lock_guard lock(registry_mutex);
            registry.emplace(token, server);
        }
        return reinterpret_cast<guance_electron_bridge_server_handle>(token);
    } catch (...) {
        return nullptr;
    }
}

void guance_electron_bridge_server_stop(
    guance_electron_bridge_server_handle bridge) {
    const uintptr_t token = reinterpret_cast<uintptr_t>(bridge);
    if (token == 0) return;

    std::shared_ptr<ElectronBridgeServer> server;
    {
        std::lock_guard lock(registry_mutex);
        const auto found = registry.find(token);
        if (found == registry.end()) return;
        server = found->second;
    }

    server->stop();
    {
        std::lock_guard lock(registry_mutex);
        const auto found = registry.find(token);
        if (found != registry.end() && found->second == server) {
            registry.erase(found);
        }
    }
}

} // extern "C"

#else

extern "C" {

guance_electron_bridge_server_handle guance_electron_bridge_server_start(
    guance_sdk_handle,
    const guance_electron_bridge_server_options*) {
    return nullptr;
}

void guance_electron_bridge_server_stop(guance_electron_bridge_server_handle) {}

} // extern "C"

#endif
