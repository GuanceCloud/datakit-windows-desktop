#include <winsock2.h>

#include "deflate_test_utils.h"
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
#include <utility>
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
    explicit SmokeServer(
        int expected_requests,
        std::vector<int> response_status_codes = {})
        : expected_requests_(expected_requests),
          response_status_codes_(std::move(response_status_codes)) {
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
                const auto status = index < static_cast<int>(response_status_codes_.size())
                    ? response_status_codes_[static_cast<std::size_t>(index)]
                    : 200;
                const auto response = std::string{"HTTP/1.1 "} +
                    (status == 200 ? "200 OK" : "500 Internal Server Error") +
                    "\r\nContent-Length: 0\r\n"
                    "Set-Cookie: response-secret\r\n"
                    "Connection: close\r\n\r\n";
                send(client, response.data(), static_cast<int>(response.size()), 0);
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
        if (requests_.wait_for(std::chrono::seconds(10)) != std::future_status::ready) {
            throw std::runtime_error("timed out waiting for intake requests");
        }
        return requests_.get();
    }

private:
    SOCKET listen_socket_ = INVALID_SOCKET;
    int port_ = 0;
    int expected_requests_ = 0;
    std::vector<int> response_status_codes_;
    std::thread worker_;
    std::future<std::vector<std::string>> requests_;
};

bool contains(const std::string& text, const std::string& expected) {
    return text.find(expected) != std::string::npos;
}

std::string line_tag_value(const std::string& request, const std::string& name) {
    const auto value_start = request.find(name + "=");
    if (value_start == std::string::npos) {
        return {};
    }
    const auto start = value_start + name.size() + 1;
    const auto end = request.find_first_of(", \r\n", start);
    return request.substr(start, end - start);
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
    assert(config.compress_intake_requests == 1);
    assert(config.flush_interval_ms == 15000);
    config.datakit_url = datakit_url.c_str();
    config.rum_app_id = "native-winhttp-smoke";
    config.cache_path = cache_path.c_str();
    config.sample_rate = 1.0;
    config.flush_interval_ms = 100;
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
    assert(contains(requests[2], "Content-Encoding: deflate"));
    const auto rum_body = guance::test::inflate_http_request_body(requests[2]);
    assert(guance::test::http_request_body(requests[2]).size() < rum_body.size());
    assert(contains(
        rum_body,
        std::string("sdk_version=") + guance_sdk_get_version()));
    assert(contains(rum_body, "resource_status=200"));
    assert(contains(rum_body, "resource_type=http"));
    assert(contains(rum_body, "resource_http_protocol=HTTP/1.1"));
    assert(contains(rum_body, "resource_size=0i"));
    assert(contains(rum_body, "resource_request_size=0i"));
    assert(contains(rum_body, "trace_id=" + trace_id));
    assert(contains(rum_body, "span_id=" + span_id));
    assert(contains(rum_body, "token\\=%3Credacted%3E&keep\\=1"));
    assert(!contains(rum_body, "token\\=secret"));
    assert(contains(rum_body, "Authorization: <redacted>"));
    assert(contains(rum_body, "Set-Cookie: <redacted>"));
    assert(!contains(rum_body, "request-secret"));
    assert(!contains(rum_body, "response-secret"));
    assert(!contains(rum_body, "/ignored"));
    assert(contains(requests[3], "POST /v1/write/logging"));
    assert(contains(requests[3], "Content-Encoding: deflate"));
    const auto log_body = guance::test::inflate_http_request_body(requests[3]);
    assert(contains(log_body, "df_rum_windows_log,"));
    assert(contains(
        log_body,
        std::string("sdk_version=") + guance_sdk_get_version()));
    const auto rum_session_id = line_tag_value(rum_body, "session_id");
    const auto log_session_id = line_tag_value(log_body, "session_id");
    assert(!rum_session_id.empty());
    assert(log_session_id == rum_session_id);
    assert(contains(log_body, "view_name=NativeLogView"));
    assert(contains(log_body, "action_name=NativeLogAction"));
    assert(contains(log_body, "message=\"native log message\""));
    assert(contains(log_body, "status=\"warning\""));
    assert(contains(log_body, "operation=\"line-modified\""));
    assert(saw_raw_url);

    guance_sdk_diagnostics diagnostics{};
    guance_log_diagnostics log_diagnostics{};
    guance_log_diagnostics_init(&log_diagnostics);
    for (int attempt = 0; attempt < 100; ++attempt) {
        assert(guance_sdk_get_diagnostics(handle, &diagnostics) == 1);
        assert(guance_log_get_diagnostics(handle, &log_diagnostics) == 1);
        if (diagnostics.rum_upload_success_count >= 1 &&
            log_diagnostics.upload_success_count >= 1) {
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    assert(diagnostics.rum_upload_success_count == 1);
    assert(log_diagnostics.logs_enqueued == 1);
    assert(log_diagnostics.upload_success_count == 1);
    guance_sdk_shutdown(handle);

    SmokeServer uncompressed_server(1);
    const auto uncompressed_datakit_url =
        "http://127.0.0.1:" + std::to_string(uncompressed_server.port());
    const auto uncompressed_cache_path = (cache_directory / "uncompressed-cache").string();
    guance_sdk_config uncompressed_config{};
    guance_sdk_config_init(&uncompressed_config);
    uncompressed_config.datakit_url = uncompressed_datakit_url.c_str();
    uncompressed_config.rum_app_id = "native-uncompressed-smoke";
    uncompressed_config.cache_path = uncompressed_cache_path.c_str();
    uncompressed_config.compress_intake_requests = 0;
    const auto uncompressed_handle = guance_sdk_init(&uncompressed_config);
    assert(uncompressed_handle != nullptr);
    guance_rum_start_view(uncompressed_handle, "UncompressedView");
    guance_rum_stop_view(uncompressed_handle);
    guance_sdk_flush(uncompressed_handle);
    const auto uncompressed_requests = uncompressed_server.wait();
    assert(uncompressed_requests.size() == 1);
    assert(contains(uncompressed_requests[0], "POST /v1/write/rum"));
    assert(!contains(uncompressed_requests[0], "Content-Encoding:"));
    assert(contains(
        guance::test::http_request_body(uncompressed_requests[0]),
        "view_name=UncompressedView"));
    guance_sdk_shutdown(uncompressed_handle);

    SmokeServer retry_server(2, {500, 200});
    const auto retry_datakit_url =
        "http://127.0.0.1:" + std::to_string(retry_server.port());
    const auto retry_cache_path = (cache_directory / "retry-cache").string();
    guance_sdk_config retry_config{};
    guance_sdk_config_init(&retry_config);
    retry_config.datakit_url = retry_datakit_url.c_str();
    retry_config.rum_app_id = "native-auto-retry-smoke";
    retry_config.cache_path = retry_cache_path.c_str();
    retry_config.flush_interval_ms = 50;
    const auto retry_handle = guance_sdk_init(&retry_config);
    assert(retry_handle != nullptr);
    guance_rum_start_view(retry_handle, "AutoRetryView");
    guance_rum_stop_view(retry_handle);
    const auto retry_requests = retry_server.wait();
    assert(retry_requests.size() == 2);
    assert(contains(retry_requests[0], "POST /v1/write/rum"));
    assert(contains(retry_requests[1], "POST /v1/write/rum"));
    assert(contains(retry_requests[0], "Content-Encoding: deflate"));
    assert(guance::test::inflate_http_request_body(retry_requests[0]) ==
           guance::test::inflate_http_request_body(retry_requests[1]));
    guance_sdk_diagnostics retry_diagnostics{};
    for (int attempt = 0; attempt < 100; ++attempt) {
        assert(guance_sdk_get_diagnostics(retry_handle, &retry_diagnostics) == 1);
        if (retry_diagnostics.rum_upload_retry_count >= 1 &&
            retry_diagnostics.rum_upload_success_count >= 1) {
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    assert(retry_diagnostics.rum_upload_retry_count >= 1);
    assert(retry_diagnostics.rum_upload_success_count >= 1);
    guance_sdk_shutdown(retry_handle);

    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
