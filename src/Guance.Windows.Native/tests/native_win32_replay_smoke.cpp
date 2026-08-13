#include "deflate_test_utils.h"
#include "guance_sdk.h"

#include <winsock2.h>
#include <windows.h>

#include <cassert>
#include <cstdint>
#include <chrono>
#include <filesystem>
#include <future>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr wchar_t kWindowClass[] = L"GuanceRumNativeReplaySmokeWindow";

std::filesystem::path unique_queue_directory() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
           ("guance-rum-win32-replay-" + std::to_string(suffix));
}

LRESULT CALLBACK smoke_wnd_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void pump_messages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

HWND create_smoke_window(HWND& edit, HWND& button) {
    WNDCLASSW wc{};
    wc.lpfnWndProc = smoke_wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kWindowClass;
    RegisterClassW(&wc);

    HWND window = CreateWindowExW(
        0,
        kWindowClass,
        L"Native RUM Smoke",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        480,
        280,
        nullptr,
        nullptr,
        GetModuleHandleW(nullptr),
        nullptr);
    if (window == nullptr) {
        throw std::runtime_error("CreateWindowExW failed");
    }

    CreateWindowExW(0, L"STATIC", L"Replay Label", WS_CHILD | WS_VISIBLE, 24, 24, 160, 24, window, nullptr, GetModuleHandleW(nullptr), nullptr);
    edit = CreateWindowExW(0, L"EDIT", L"secret-value", WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL, 24, 60, 180, 28, window, nullptr, GetModuleHandleW(nullptr), nullptr);
    button = CreateWindowExW(0, L"BUTTON", L"Smoke Button", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 24, 104, 140, 32, window, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (edit == nullptr || button == nullptr) {
        throw std::runtime_error("Create child controls failed");
    }

    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    pump_messages();
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    pump_messages();
    return window;
}

std::string recv_all(SOCKET socket, int content_length, std::string initial) {
    std::string request = std::move(initial);
    while (static_cast<int>(request.size()) < content_length) {
        char buffer[4096]{};
        const int received = recv(socket, buffer, static_cast<int>(sizeof(buffer)), 0);
        if (received <= 0) {
            break;
        }
        request.append(buffer, static_cast<std::size_t>(received));
    }
    return request;
}

int parse_content_length(const std::string& headers) {
    const std::string key = "Content-Length:";
    const auto pos = headers.find(key);
    if (pos == std::string::npos) {
        return 0;
    }
    const auto value_start = pos + key.size();
    const auto value_end = headers.find("\r\n", value_start);
    return std::stoi(headers.substr(value_start, value_end - value_start));
}

std::string read_http_request(SOCKET client) {
    std::string request;
    while (request.find("\r\n\r\n") == std::string::npos) {
        char buffer[4096]{};
        const int received = recv(client, buffer, static_cast<int>(sizeof(buffer)), 0);
        if (received <= 0) {
            return request;
        }
        request.append(buffer, static_cast<std::size_t>(received));
    }

    const auto header_end = request.find("\r\n\r\n") + 4;
    const auto content_length = parse_content_length(request.substr(0, header_end));
    return recv_all(client, static_cast<int>(header_end) + content_length, request);
}

class SmokeServer {
public:
    explicit SmokeServer(int expected_requests) : expected_requests_(expected_requests) {
        WSADATA data{};
        if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
            throw std::runtime_error("WSAStartup failed");
        }

        listen_socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listen_socket_ == INVALID_SOCKET) {
            throw std::runtime_error("socket failed");
        }

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (bind(listen_socket_, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
            throw std::runtime_error("bind failed");
        }
        if (listen(listen_socket_, expected_requests_) == SOCKET_ERROR) {
            throw std::runtime_error("listen failed");
        }

        int length = sizeof(address);
        if (getsockname(listen_socket_, reinterpret_cast<sockaddr*>(&address), &length) == SOCKET_ERROR) {
            throw std::runtime_error("getsockname failed");
        }
        port_ = ntohs(address.sin_port);

        auto promise = std::make_shared<std::promise<std::vector<std::string>>>();
        request_ = promise->get_future();
        worker_ = std::thread([this, promise]() {
            std::vector<std::string> requests;
            for (int i = 0; i < expected_requests_; i++) {
                SOCKET client = accept(listen_socket_, nullptr, nullptr);
                if (client == INVALID_SOCKET) {
                    break;
                }

                auto request = read_http_request(client);
                const char response[] = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                send(client, response, static_cast<int>(sizeof(response) - 1), 0);
                closesocket(client);
                requests.push_back(std::move(request));
            }
            promise->set_value(std::move(requests));
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

    int port() const {
        return port_;
    }

    std::vector<std::string> wait_for_requests() {
        if (request_.wait_for(std::chrono::seconds(10)) != std::future_status::ready) {
            throw std::runtime_error("Timed out waiting for replay request");
        }
        return request_.get();
    }

private:
    SOCKET listen_socket_ = INVALID_SOCKET;
    int port_ = 0;
    int expected_requests_ = 1;
    std::thread worker_;
    std::future<std::vector<std::string>> request_;
};

bool contains(const std::string& text, const std::string& needle) {
    return text.find(needle) != std::string::npos;
}

std::string multipart_segment(const std::string& request) {
    const auto segment_part = request.find("name=\"segment\"");
    assert(segment_part != std::string::npos);
    assert(contains(
        request.substr(segment_part, 256),
        "Content-Type: application/octet-stream"));

    const auto data_marker = request.find("\r\n\r\n", segment_part);
    assert(data_marker != std::string::npos);
    const auto data_start = data_marker + 4;
    const auto data_end = request.find("\r\n--guance-rum-replay-", data_start);
    assert(data_end != std::string::npos);
    return request.substr(data_start, data_end - data_start);
}

uint32_t adler32(const std::string& value) {
    constexpr uint32_t modulus = 65521;
    uint32_t a = 1;
    uint32_t b = 0;
    for (const unsigned char byte : value) {
        a = (a + byte) % modulus;
        b = (b + a) % modulus;
    }
    return (b << 16) | a;
}

std::string inflate_stored_zlib(const std::string& encoded) {
    assert(encoded.size() >= 6);
    const auto cmf = static_cast<unsigned char>(encoded[0]);
    const auto flg = static_cast<unsigned char>(encoded[1]);
    assert((cmf & 0x0f) == 8);
    assert(((static_cast<unsigned int>(cmf) << 8) | flg) % 31 == 0);
    assert((flg & 0x20) == 0);

    std::string decoded;
    std::size_t offset = 2;
    bool final_block = false;
    while (!final_block) {
        assert(offset + 5 <= encoded.size() - 4);
        const auto header = static_cast<unsigned char>(encoded[offset++]);
        final_block = (header & 0x01) != 0;
        assert((header & 0x06) == 0);

        const auto length =
            static_cast<uint16_t>(static_cast<unsigned char>(encoded[offset])) |
            static_cast<uint16_t>(static_cast<unsigned char>(encoded[offset + 1]) << 8);
        const auto inverse_length =
            static_cast<uint16_t>(static_cast<unsigned char>(encoded[offset + 2])) |
            static_cast<uint16_t>(static_cast<unsigned char>(encoded[offset + 3]) << 8);
        offset += 4;
        assert(static_cast<uint16_t>(length ^ inverse_length) == 0xffff);
        assert(offset + length <= encoded.size() - 4);
        decoded.append(encoded, offset, length);
        offset += length;
    }

    assert(offset + 4 == encoded.size());
    const auto expected_adler =
        (static_cast<uint32_t>(static_cast<unsigned char>(encoded[offset])) << 24) |
        (static_cast<uint32_t>(static_cast<unsigned char>(encoded[offset + 1])) << 16) |
        (static_cast<uint32_t>(static_cast<unsigned char>(encoded[offset + 2])) << 8) |
        static_cast<uint32_t>(static_cast<unsigned char>(encoded[offset + 3]));
    assert(adler32(decoded) == expected_adler);
    return decoded;
}

} // namespace

int main() {
    const auto queue_directory = unique_queue_directory();
    const auto queue_path_text = (queue_directory / "cache").string();
    HWND edit = nullptr;
    HWND button = nullptr;
    HWND window = create_smoke_window(edit, button);

    SmokeServer server(4);
    const auto datakit_url = "http://127.0.0.1:" + std::to_string(server.port());

    guance_sdk_config config{};
    guance_sdk_config_init(&config);
    config.enable_app_launch_tracking = 0;
    config.dataway_url = datakit_url.c_str();
    config.client_token = "token value+plus";
    config.rum_app_id = "native-rum-smoke";
    config.service_name = "native-smoke";
    config.env = "local";
    config.version = "0.1.0";
    config.sample_rate = 1.0;
    config.session_replay_sample_rate = 1.0;
    config.max_cache_files = 100;
    config.max_upload_bytes_per_second = 16LL * 1024 * 1024;
    config.upload_burst_bytes = 16LL * 1024 * 1024;
    config.max_upload_requests_per_second = 1000.0;
    config.cache_path = queue_path_text.c_str();

    guance_sdk_handle disabled_handle = guance_sdk_init(&config);
    assert(disabled_handle != nullptr);
    guance_rum_start_session_replay(disabled_handle);
    guance_sdk_diagnostics disabled_diagnostics{};
    assert(guance_sdk_get_diagnostics(disabled_handle, &disabled_diagnostics) == 1);
    assert(disabled_diagnostics.session_replay_sampled == 0);
    guance_sdk_shutdown(disabled_handle);

    config.session_replay_enabled = 1;
    guance_sdk_handle handle = guance_sdk_init(&config);
    assert(handle != nullptr);

    guance_rum_start_session_replay(handle);
    guance_sdk_flush(handle);

    const std::string browser_record =
        "{\"type\":2,\"timestamp\":1722300000123,\"data\":{\"node\":{\"id\":42}}}";
    assert(guance_rum_capture_browser_replay_record(
               handle,
               "browser-native-session",
               "browser-native-view",
               browser_record.data(),
               browser_record.size(),
               1722300000123,
               1) == 1);

    guance_rum_register_replay_window(handle, reinterpret_cast<uintptr_t>(window));
    guance_rum_start_view(handle, "NativeWin32Smoke");
    guance_sdk_flush(handle);
    guance_rum_capture_replay_click(handle, reinterpret_cast<uintptr_t>(button), "Smoke Button", 32, 120);
    guance_rum_capture_replay_input(handle, reinterpret_cast<uintptr_t>(edit), "Smoke Edit");
    guance_rum_capture_replay_resize(handle, reinterpret_cast<uintptr_t>(window), "Native RUM Smoke", 480, 280);
    guance_sdk_flush(handle);

    const char* action_id = guance_rum_start_action_ext(handle, "Native Action", "click", 1);
    assert(action_id != nullptr && action_id[0] != '\0');
    const char* resource_id = guance_rum_start_resource(handle, "https://example.com/api/42", "GET");
    assert(resource_id != nullptr && resource_id[0] != '\0');
    guance_rum_stop_resource_ext(handle, resource_id, 200, 1024, 64, "http", "trace-native", "span-native", "HTTP/2");
    guance_rum_add_long_task(handle, 25'000'000, "native long task");
    guance_rum_stop_action(handle, action_id);
    guance_rum_stop_view(handle);
    guance_sdk_flush(handle);

    const auto requests = server.wait_for_requests();
    assert(requests.size() == 4);
    for (const auto& replay_request : requests) {
        if (contains(replay_request, "POST /v1/write/rum/replay")) {
            if (!contains(replay_request, "name=\"view_id\"\r\n\r\n") ||
                contains(replay_request, "name=\"view_id\"\r\n\r\n\r\n")) {
                std::cerr << "Replay request contains an empty view_id\n";
                return 1;
            }
        }
    }

    assert(contains(requests[0], "POST /v1/write/rum/replay"));
    assert(!contains(requests[0], "browser-native-session"));
    assert(contains(requests[0], "name=\"session_id\"\r\n\r\n"));
    assert(contains(requests[0], "name=\"view_id\"\r\n\r\nbrowser-native-view\r\n"));
    const auto browser_segment = inflate_stored_zlib(multipart_segment(requests[0]));
    assert(!contains(browser_segment, "browser-native-session"));
    assert(contains(browser_segment, "\"view\":{\"id\":\"browser-native-view\"}"));
    assert(contains(browser_segment, browser_record));

    const auto& request = requests[1];
    assert(contains(request, "POST /v1/write/rum/replay"));
    assert(contains(request, "token=token%20value%2Bplus"));
    assert(contains(request, "Content-Type: multipart/form-data; boundary="));
    assert(contains(request, "name=\"segment\"; filename=\""));
    assert(contains(request, "name=\"source\""));
    assert(contains(request, "name=\"source\"\r\n\r\nwindows\r\n"));
    assert(contains(request, "name=\"sdk_name\"\r\n\r\ndf_windows_rum_sdk\r\n"));
    assert(contains(
        request,
        std::string("name=\"sdk_version\"\r\n\r\n") + guance_sdk_get_version() + "\r\n"));
    assert(contains(request, "name=\"has_full_snapshot\""));
    assert(contains(request, "true"));
    const auto initial_segment = inflate_stored_zlib(multipart_segment(request));
    assert(contains(initial_segment, "\"wireframes\":["));
    assert(contains(initial_segment, "\"label\":\"Window\""));
    assert(!contains(initial_segment, "secret-value"));
    assert(contains(requests[2], "name=\"records_count\"\r\n\r\n3\r\n"));
    const auto interaction_segment = inflate_stored_zlib(multipart_segment(requests[2]));
    assert(contains(interaction_segment, "\"source\":2"));
    assert(contains(interaction_segment, "Smoke Button"));
    assert(contains(interaction_segment, "\"event_type\":\"input\""));
    assert(contains(interaction_segment, "Smoke Edit"));
    assert(!contains(interaction_segment, "secret-value"));
    assert(contains(interaction_segment, "\"source\":4"));
    assert(contains(interaction_segment, "Native RUM Smoke"));
    assert(contains(requests[3], "POST /v1/write/rum"));
    assert(contains(requests[3], "Content-Encoding: deflate"));
    const auto rum_body = guance::test::inflate_http_request_body(requests[3]);
    assert(contains(rum_body, "sdk_name=df_windows_rum_sdk"));
    assert(!contains(rum_body, "df_android_rum_sdk"));
    assert(contains(rum_body, "resource_request_size=64i"));
    assert(contains(rum_body, "resource_type=http"));
    assert(contains(rum_body, "trace_id=trace-native"));
    assert(contains(rum_body, "span_id=span-native"));
    assert(contains(rum_body, "action_resource_count=1i"));

    guance_sdk_diagnostics diagnostics{};
    assert(guance_sdk_get_diagnostics(handle, &diagnostics) == 1);
    assert(diagnostics.replay_upload_success_count >= 3);
    assert(diagnostics.rum_upload_success_count >= 1);
    assert(diagnostics.last_replay_upload_status_code == 200);
    assert(diagnostics.last_rum_upload_status_code == 200);
    assert(diagnostics.last_rum_upload_latency_ms >= 0);
    assert(diagnostics.session_replay_sampled == 1);

    guance_rum_stop_session_replay(handle);
    guance_sdk_shutdown(handle);
    DestroyWindow(button);
    DestroyWindow(edit);
    DestroyWindow(window);

    std::error_code cleanup_error;
    std::filesystem::remove_all(queue_directory, cleanup_error);

    std::cout << "native Win32 replay smoke passed\n";
    return 0;
}
