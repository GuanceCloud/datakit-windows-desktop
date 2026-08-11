#include <winsock2.h>

#include "guance_rum_winhttp.hpp"

#include <cassert>
#include <algorithm>
#include <cctype>
#include <chrono>
#include <filesystem>
#include <future>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

std::string read_request(SOCKET client) {
    std::string request;
    while (request.find("\r\n\r\n") == std::string::npos) {
        char buffer[4096]{};
        const int received = recv(client, buffer, static_cast<int>(sizeof(buffer)), 0);
        if (received <= 0) {
            return request;
        }
        request.append(buffer, static_cast<std::size_t>(received));
    }

    const auto headers_end = request.find("\r\n\r\n") + 4;
    int content_length = 0;
    const auto content_length_start = request.find("Content-Length:");
    if (content_length_start != std::string::npos && content_length_start < headers_end) {
        const auto value_start = content_length_start + std::string("Content-Length:").size();
        const auto value_end = request.find("\r\n", value_start);
        content_length = std::stoi(request.substr(value_start, value_end - value_start));
    }

    while (request.size() - headers_end < static_cast<std::size_t>(content_length)) {
        char buffer[4096]{};
        const int received = recv(client, buffer, static_cast<int>(sizeof(buffer)), 0);
        if (received <= 0) {
            break;
        }
        request.append(buffer, static_cast<std::size_t>(received));
    }
    return request;
}

class SmokeServer final {
public:
    explicit SmokeServer(int expected_requests)
        : expected_requests_(expected_requests) {
        WSADATA data{};
        if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
            throw std::runtime_error("WSAStartup failed");
        }

        listen_socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listen_socket_ == INVALID_SOCKET) {
            WSACleanup();
            throw std::runtime_error("socket failed");
        }

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (bind(
                listen_socket_,
                reinterpret_cast<const sockaddr*>(&address),
                sizeof(address)) == SOCKET_ERROR ||
            listen(listen_socket_, expected_requests_) == SOCKET_ERROR) {
            closesocket(listen_socket_);
            WSACleanup();
            throw std::runtime_error("bind/listen failed");
        }

        int address_size = sizeof(address);
        if (getsockname(
                listen_socket_,
                reinterpret_cast<sockaddr*>(&address),
                &address_size) == SOCKET_ERROR) {
            closesocket(listen_socket_);
            WSACleanup();
            throw std::runtime_error("getsockname failed");
        }
        port_ = ntohs(address.sin_port);

        std::promise<std::vector<std::string>> promise;
        requests_ = promise.get_future();
        worker_ = std::thread([
            this,
            promise = std::move(promise)
        ]() mutable {
            std::vector<std::string> requests;
            for (int index = 0; index < expected_requests_; ++index) {
                SOCKET client = accept(listen_socket_, nullptr, nullptr);
                if (client == INVALID_SOCKET) {
                    promise.set_exception(std::make_exception_ptr(
                        std::runtime_error("accept failed")));
                    return;
                }
                requests.push_back(read_request(client));
                const char response[] =
                    "HTTP/1.1 200 OK\r\n"
                    "Content-Length: 0\r\n"
                    "Set-Cookie: response-secret\r\n"
                    "Connection: close\r\n\r\n";
                send(client, response, static_cast<int>(sizeof(response) - 1), 0);
                closesocket(client);
            }
            promise.set_value(std::move(requests));
        });
    }

    ~SmokeServer() {
        if (listen_socket_ != INVALID_SOCKET) {
            closesocket(listen_socket_);
        }
        if (worker_.joinable()) {
            worker_.join();
        }
        WSACleanup();
    }

    [[nodiscard]] int port() const noexcept {
        return port_;
    }

    std::vector<std::string> wait() {
        return requests_.get();
    }

private:
    SOCKET listen_socket_ = INVALID_SOCKET;
    int port_ = 0;
    int expected_requests_ = 0;
    std::thread worker_;
    std::future<std::vector<std::string>> requests_;
};

bool contains(const std::string& text, const std::string& expected) {
    return text.find(expected) != std::string::npos;
}

std::string header_value(const std::string& request, const std::string& name) {
    std::string lower = request;
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    std::string prefix = name;
    std::transform(prefix.begin(), prefix.end(), prefix.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    prefix += ":";
    const auto start = lower.find("\r\n" + prefix);
    assert(start != std::string::npos);
    const auto value_start = request.find_first_not_of(" \t", start + 2 + prefix.size());
    const auto value_end = request.find("\r\n", value_start);
    return request.substr(value_start, value_end - value_start);
}

int collect_non_ignored(const char* url, const char*, void* user_data) {
    auto* calls = static_cast<int*>(user_data);
    ++(*calls);
    return std::string(url).find("/ignored") == std::string::npos ? 1 : 0;
}

int modify_data(
    const char* key,
    const guance_data_value* value,
    guance_data_value* replacement,
    void* user_data) {
    auto* saw_raw_url = static_cast<bool*>(user_data);
    if (std::string(key) == "resource_url" &&
        value->type == GUANCE_DATA_VALUE_STRING &&
        value->value.string_value != nullptr) {
        const std::string url(value->value.string_value);
        *saw_raw_url = url.find("token=secret") != std::string::npos;
    }
    if (std::string(key) != "operation") {
        return 0;
    }

    replacement->type = GUANCE_DATA_VALUE_STRING;
    replacement->value.string_value = "data-modified";
    return 1;
}

void modify_line(
    const char* measurement,
    guance_data_item* data,
    uint32_t data_count,
    void*) {
    if (std::string(measurement) != "df_rum_windows_log") {
        return;
    }
    for (uint32_t index = 0; index < data_count; ++index) {
        if (std::string(data[index].key) == "operation") {
            assert(data[index].value.type == GUANCE_DATA_VALUE_STRING);
            assert(std::string(data[index].value.value.string_value) == "data-modified");
            data[index].value.value.string_value = "line-modified";
        }
    }
}

} // namespace

int main() {
    SmokeServer server(4);
    const auto datakit_url = "http://127.0.0.1:" + std::to_string(server.port());
    const auto cache_directory = std::filesystem::temp_directory_path() /
        ("guance-native-winhttp-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()));
    std::filesystem::create_directories(cache_directory);
    const auto cache_path = (cache_directory / "cache").string();

    guance_sdk_config config{};
    guance_sdk_config_init(&config);
    config.datakit_url = datakit_url.c_str();
    config.rum_app_id = "native-winhttp-smoke";
    config.cache_path = cache_path.c_str();
    config.sample_rate = 1.0;
    const auto handle = guance_sdk_init(&config);
    assert(handle != nullptr);

    int filter_calls = 0;
    guance_rum_resource_collection_config resources{};
    guance_rum_resource_collection_config_init(&resources);
    resources.should_collect = collect_non_ignored;
    resources.user_data = &filter_calls;
    const int configured = guance_rum_configure_resource_collection(handle, &resources);
    assert(configured == 1);
    bool saw_raw_url = false;
    guance_data_modifier_config modifiers{};
    guance_data_modifier_config_init(&modifiers);
    modifiers.data_modifier = modify_data;
    modifiers.line_data_modifier = modify_line;
    modifiers.user_data = &saw_raw_url;
    assert(guance_configure_data_modifiers(handle, &modifiers) == 1);
    guance_trace_config trace{};
    guance_trace_config_init(&trace);
    trace.enable_auto_trace = 1;
    trace.enable_link_rum_data = 1;
    trace.trace_type = GUANCE_TRACE_ZIPKIN_MULTI_HEADER;
    const int trace_configured = guance_trace_configure(handle, &trace); // Always invoke in Release builds.
    assert(trace_configured == 1);
    guance_log_config logging{};
    guance_log_config_init(&logging);
    logging.enable_custom_log = 1;
    logging.enable_link_rum_data = 1;
    const int logging_configured = guance_log_configure(handle, &logging);
    assert(logging_configured == 1);
    HINTERNET session = WinHttpOpen(
        L"GuanceRumNativeResourceSmoke/0.1",
        WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0);
    assert(session != nullptr);
    HINTERNET connection = WinHttpConnect(
        session,
        L"127.0.0.1",
        static_cast<INTERNET_PORT>(server.port()),
        0);
    assert(connection != nullptr);
    HINTERNET ignored_request = WinHttpOpenRequest(
        connection,
        L"GET",
        L"/ignored",
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        0);
    assert(ignored_request != nullptr);
    const auto ignored_url = datakit_url + "/ignored";
    {
        guance::rum::WinHttpResource ignored(
            handle,
            ignored_request,
            ignored_url.c_str(),
            "GET");
        assert(!ignored.active());
        const BOOL sent = ignored.send();
        assert(sent);
        const BOOL received = ignored.receive();
        assert(received);
    }
    WinHttpCloseHandle(ignored_request);

    HINTERNET request = WinHttpOpenRequest(
        connection,
        L"GET",
        L"/instrumented?token=secret&keep=1",
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        0);
    assert(request != nullptr);
    // Seed an existing value to verify the adapter replaces, rather than duplicates, it.
    const wchar_t existing_trace_id[] =
        L"X-B3-TraceId: 00000000000000000000000000000001";
    const BOOL added_existing_trace = WinHttpAddRequestHeaders(
        request,
        existing_trace_id,
        static_cast<DWORD>(-1L),
        WINHTTP_ADDREQ_FLAG_ADD);
    assert(added_existing_trace);
    const wchar_t authorization[] = L"Authorization: request-secret";
    assert(WinHttpAddRequestHeaders(
        request,
        authorization,
        static_cast<DWORD>(-1L),
        WINHTTP_ADDREQ_FLAG_ADD));

    const auto resource_url = datakit_url + "/instrumented?token=secret&keep=1";
    {
        guance::rum::WinHttpResource resource(
            handle,
            request,
            resource_url.c_str(),
            "GET",
            guance::rum::WinHttpRequestMode::asynchronous);
        assert(resource.active());
        const BOOL sent = resource.send();
        assert(sent);
        const BOOL received = resource.receive();
        assert(received);
        assert(resource.active());
        const bool completed = resource.complete_from_response();
        assert(completed);
        assert(!resource.active());
    }
    assert(filter_calls == 2);
    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);

    guance_rum_start_view(handle, "NativeLogView");
    const auto action_id = guance_rum_start_action_ext(handle, "NativeLogAction", "click", 1);
    guance_log_property log_properties[] = {{"operation", "save"}};
    assert(guance_log_add(handle, "native log message", "warning", log_properties, 1) == 1);
    guance_rum_stop_action(handle, action_id);
    guance_rum_stop_view(handle);

    guance_sdk_flush(handle);
    const auto requests = server.wait();
    assert(requests.size() == 4);
    assert(contains(requests[0], "GET /ignored"));
    assert(contains(requests[1], "GET /instrumented?token=secret&keep=1"));
    const auto trace_id = header_value(requests[1], "X-B3-TraceId");
    const auto span_id = header_value(requests[1], "X-B3-SpanId");
    assert(trace_id.size() == 32);
    assert(span_id.size() == 16);
    assert(trace_id != "00000000000000000000000000000001");
    assert(header_value(requests[1], "X-B3-Sampled") == "1");
    assert(contains(requests[2], "POST /v1/write/rum"));
    assert(contains(requests[2], "resource_status=200"));
    assert(contains(requests[2], "resource_type=http"));
    assert(contains(requests[2], "resource_http_protocol=HTTP/1.1"));
    assert(contains(requests[2], "resource_size=0i"));
    assert(contains(requests[2], "resource_request_size=0i"));
    assert(contains(requests[2], "trace_id=" + trace_id));
    assert(contains(requests[2], "span_id=" + span_id));
    assert(contains(requests[2], "token\\=%3Credacted%3E&keep\\=1"));
    assert(!contains(requests[2], "token\\=secret"));
    assert(contains(requests[2], "Authorization: <redacted>"));
    assert(contains(requests[2], "Set-Cookie: <redacted>"));
    assert(!contains(requests[2], "request-secret"));
    assert(!contains(requests[2], "response-secret"));
    assert(!contains(requests[2], "/ignored"));
    assert(contains(requests[3], "POST /v1/write/logging"));
    assert(contains(requests[3], "df_rum_windows_log,"));
    assert(contains(requests[3], "view_name=NativeLogView"));
    assert(contains(requests[3], "action_name=NativeLogAction"));
    assert(contains(requests[3], "message=\"native log message\""));
    assert(contains(requests[3], "status=\"warning\""));
    assert(contains(requests[3], "operation=\"line-modified\""));
    assert(saw_raw_url);

    guance_sdk_diagnostics diagnostics{};
    const int has_diagnostics = guance_sdk_get_diagnostics(handle, &diagnostics);
    assert(has_diagnostics == 1);
    assert(diagnostics.rum_upload_success_count == 1);
    guance_log_diagnostics log_diagnostics{};
    guance_log_diagnostics_init(&log_diagnostics);
    assert(guance_log_get_diagnostics(handle, &log_diagnostics) == 1);
    assert(log_diagnostics.logs_enqueued == 1);
    assert(log_diagnostics.upload_success_count == 1);
    guance_sdk_shutdown(handle);

    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
