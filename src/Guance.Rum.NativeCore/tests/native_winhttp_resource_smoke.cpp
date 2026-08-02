#include <winsock2.h>

#include "guance_rum_winhttp.hpp"

#include <cassert>
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

int collect_non_ignored(const char* url, const char*, void* user_data) {
    auto* calls = static_cast<int*>(user_data);
    ++(*calls);
    return std::string(url).find("/ignored") == std::string::npos ? 1 : 0;
}

} // namespace

int main() {
    SmokeServer server(3);
    const auto datakit_url = "http://127.0.0.1:" + std::to_string(server.port());
    const auto cache_directory = std::filesystem::temp_directory_path() /
        ("guance-native-winhttp-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()));
    std::filesystem::create_directories(cache_directory);
    const auto cache_path = (cache_directory / "rum.db").string();

    guance_rum_config config{};
    guance_rum_config_init(&config);
    config.datakit_url = datakit_url.c_str();
    config.rum_app_id = "native-winhttp-smoke";
    config.cache_path = cache_path.c_str();
    config.sample_rate = 1.0;
    const auto handle = guance_rum_init(&config);
    assert(handle != nullptr);

    int filter_calls = 0;
    guance_rum_resource_collection_config resources{};
    guance_rum_resource_collection_config_init(&resources);
    resources.should_collect = collect_non_ignored;
    resources.user_data = &filter_calls;
    const int configured = guance_rum_configure_resource_collection(handle, &resources);
    assert(configured == 1);
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

    guance_rum_flush(handle);
    const auto requests = server.wait();
    assert(requests.size() == 3);
    assert(contains(requests[0], "GET /ignored"));
    assert(contains(requests[1], "GET /instrumented?token=secret&keep=1"));
    assert(contains(requests[2], "POST /v1/write/rum"));
    assert(contains(requests[2], "resource_status=200"));
    assert(contains(requests[2], "resource_type=http"));
    assert(contains(requests[2], "resource_http_protocol=HTTP/1.1"));
    assert(contains(requests[2], "resource_size=0i"));
    assert(contains(requests[2], "resource_request_size=0i"));
    assert(contains(requests[2], "token\\=%3Credacted%3E&keep\\=1"));
    assert(!contains(requests[2], "token\\=secret"));
    assert(!contains(requests[2], "/ignored"));

    guance_rum_diagnostics diagnostics{};
    const int has_diagnostics = guance_rum_get_diagnostics(handle, &diagnostics);
    assert(has_diagnostics == 1);
    assert(diagnostics.rum_upload_success_count == 1);
    guance_rum_shutdown(handle);

    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
