#include <guance_sdk.h>

#include <windows.h>

#include <chrono>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <thread>

namespace {

LRESULT CALLBACK bridge_window_proc(
    HWND window,
    UINT message,
    WPARAM wparam,
    LPARAM lparam) {
    return DefWindowProcW(window, message, wparam, lparam);
}

HWND create_bridge_window() {
    WNDCLASSW window_class{};
    window_class.lpfnWndProc = bridge_window_proc;
    window_class.hInstance = GetModuleHandleW(nullptr);
    window_class.lpszClassName = L"GuanceElectronBridgeServerSmoke";
    RegisterClassW(&window_class);
    return CreateWindowExW(
        0,
        window_class.lpszClassName,
        L"Electron helper host window",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        320,
        240,
        nullptr,
        nullptr,
        window_class.hInstance,
        nullptr);
}

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

HANDLE connect_pipe(const std::string& pipe_name) {
    const std::wstring wide_name(pipe_name.begin(), pipe_name.end());
    const std::wstring pipe_path = L"\\\\.\\pipe\\" + wide_name;
    for (int attempt = 0; attempt < 50; ++attempt) {
        if (WaitNamedPipeW(pipe_path.c_str(), 100)) {
            HANDLE pipe = CreateFileW(
                pipe_path.c_str(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);
            if (pipe != INVALID_HANDLE_VALUE) return pipe;
        }
        Sleep(20);
    }
    return INVALID_HANDLE_VALUE;
}

std::string read_handshake(HANDLE pipe) {
    char buffer[16u * 1024u]{};
    DWORD read = 0;
    require(
        ReadFile(pipe, buffer, static_cast<DWORD>(sizeof(buffer)), &read, nullptr) != FALSE,
        "capabilities handshake read failed");
    return std::string(buffer, read);
}

void write_pipe(HANDLE pipe, const std::string& value) {
    DWORD written = 0;
    require(
        WriteFile(
            pipe,
            value.data(),
            static_cast<DWORD>(value.size()),
            &written,
            nullptr) != FALSE && written == value.size(),
        "Bridge Server input write failed");
}

} // namespace

int main() {
    guance_electron_bridge_server_options options{};
    guance_electron_bridge_server_options_init(&options);

    require(
        options.struct_size == sizeof(guance_electron_bridge_server_options),
        "bridge server options size was not initialized");
    require(
        options.version == GUANCE_ELECTRON_BRIDGE_SERVER_OPTIONS_VERSION,
        "bridge server options version was not initialized");
    require(options.pipe_name != nullptr, "default pipe name is missing");
    require(
        options.max_message_bytes == 2u * 1024u * 1024u,
        "default bridge message limit is invalid");
    require(options.replay_privacy_level != nullptr, "default Replay privacy is missing");
    require(options.trace_type != nullptr, "default trace type is missing");

    guance_sdk_config sdk_config{};
    guance_sdk_config_init(&sdk_config);
    const auto cache_path = std::filesystem::temp_directory_path() /
        ("guance-electron-bridge-server-smoke-" +
         std::to_string(
             std::chrono::steady_clock::now().time_since_epoch().count()));
    const std::string cache_path_text = cache_path.string();
    sdk_config.datakit_url = "http://127.0.0.1:9";
    sdk_config.rum_app_id = "electron-bridge-server-smoke";
    sdk_config.cache_path = cache_path_text.c_str();
    sdk_config.http_timeout_ms = 50;
    guance_sdk_handle sdk = guance_sdk_init(&sdk_config);
    require(sdk != nullptr, "SDK initialization failed");

    require(
        guance_electron_bridge_server_start(nullptr, &options) == nullptr,
        "Bridge Server accepted a null SDK Handle");
    require(
        guance_electron_bridge_server_start(sdk, nullptr) == nullptr,
        "Bridge Server accepted null options");

    auto invalid_options = options;
    invalid_options.struct_size = 0;
    require(
        guance_electron_bridge_server_start(sdk, &invalid_options) == nullptr,
        "Bridge Server accepted an invalid options size");
    invalid_options = options;
    invalid_options.pipe_name = "unsafe/pipe";
    require(
        guance_electron_bridge_server_start(sdk, &invalid_options) == nullptr,
        "Bridge Server accepted an unsafe pipe name");
    invalid_options = options;
    invalid_options.max_message_bytes = 0;
    require(
        guance_electron_bridge_server_start(sdk, &invalid_options) == nullptr,
        "Bridge Server accepted an invalid message limit");

    const std::string pipe_name = "guance-bridge-server-smoke-" +
        std::to_string(GetCurrentProcessId()) + "-" +
        std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count());
    options.pipe_name = pipe_name.c_str();
    guance_electron_bridge_server_handle bridge =
        guance_electron_bridge_server_start(sdk, &options);
    require(bridge != nullptr, "Bridge Server failed to start");
    require(
        guance_electron_bridge_server_start(sdk, &options) == nullptr,
        "Bridge Server allowed a second owner for the same pipe");

    HWND helper_window = create_bridge_window();
    require(helper_window != nullptr, "helper window fixture failed to initialize");
    ShowWindow(helper_window, SW_SHOW);
    UpdateWindow(helper_window);
    Sleep(100);
    guance_sdk_diagnostics helper_diagnostics{};
    require(
        guance_sdk_get_diagnostics(sdk, &helper_diagnostics) == 1 &&
            helper_diagnostics.rum_events_enqueued == 0,
        "Bridge Server did not disable Helper Host automatic window discovery");
    DestroyWindow(helper_window);

    HANDLE pipe = connect_pipe(pipe_name);
    require(pipe != INVALID_HANDLE_VALUE, "failed to connect to Bridge Server");
    const std::string handshake = read_handshake(pipe);
    require(
        handshake.rfind("@guance-capabilities\tprotocol=1\trum=1", 0) == 0,
        "Bridge Server capabilities handshake is missing");
    require(
        handshake.find("\tlog=0") != std::string::npos &&
            handshake.find("\treplay=0") != std::string::npos &&
            handshake.find("\ttrace=0") != std::string::npos,
        "Bridge Server default capabilities are invalid");

    guance_sdk_diagnostics before{};
    require(
        guance_sdk_get_diagnostics(sdk, &before) == 1,
        "SDK diagnostics failed before forwarding");
    write_pipe(
        pipe,
        "view,app_id=renderer-app,env=renderer-env,service=renderer-service,"
        "version=renderer-version,sdk_name=renderer-sdk,sdk_version=renderer-sdk,"
        "session_id=renderer-session,view_id=electron-bridge-server-smoke,"
        "view_name=ControlRoom,view_referrer=file:///index.html "
        "is_active=false,time_spent=1i "
        "1722300000000000000\n");
    guance_sdk_diagnostics after{};
    for (int attempt = 0; attempt < 50; ++attempt) {
        require(
            guance_sdk_get_diagnostics(sdk, &after) == 1,
            "SDK diagnostics failed after forwarding");
        if (after.rum_events_enqueued > before.rum_events_enqueued) break;
        Sleep(20);
    }
    require(
        after.rum_events_enqueued == before.rum_events_enqueued + 1,
        "Bridge Server did not forward through the borrowed SDK Handle");

    std::thread first_stop([&]() {
        guance_electron_bridge_server_stop(bridge);
    });
    std::thread concurrent_stop([&]() {
        guance_electron_bridge_server_stop(bridge);
    });
    first_stop.join();
    concurrent_stop.join();
    guance_electron_bridge_server_stop(bridge);
    CloseHandle(pipe);

    guance_sdk_diagnostics diagnostics{};
    require(
        guance_sdk_get_diagnostics(sdk, &diagnostics) == 1,
        "Bridge Server stop invalidated the borrowed SDK Handle");

    const std::string concurrent_pipe_name = pipe_name + "-concurrent";
    options.pipe_name = concurrent_pipe_name.c_str();
    options.max_message_bytes = 2u * 1024u * 1024u;
    guance_electron_bridge_server_handle concurrent_bridges[2]{};
    std::thread first_start([&]() {
        concurrent_bridges[0] = guance_electron_bridge_server_start(sdk, &options);
    });
    std::thread second_start([&]() {
        concurrent_bridges[1] = guance_electron_bridge_server_start(sdk, &options);
    });
    first_start.join();
    second_start.join();
    require(
        (concurrent_bridges[0] != nullptr) != (concurrent_bridges[1] != nullptr),
        "concurrent same-pipe starts did not select exactly one owner");
    guance_electron_bridge_server_stop(concurrent_bridges[0]);
    guance_electron_bridge_server_stop(concurrent_bridges[1]);

    const std::string limited_pipe_name = pipe_name + "-limited";
    options.pipe_name = limited_pipe_name.c_str();
    options.max_message_bytes = 128;
    bridge = guance_electron_bridge_server_start(sdk, &options);
    require(bridge != nullptr, "limited Bridge Server failed to start");
    pipe = connect_pipe(limited_pipe_name);
    require(pipe != INVALID_HANDLE_VALUE, "failed to connect to limited Bridge Server");
    read_handshake(pipe);
    const std::string oversized_line =
        "view,view_id=oversized message=\"" + std::string(128, 'x') +
        "\" 1722300000000000000\n";
    require(
        oversized_line.size() - 1 > options.max_message_bytes,
        "oversized test record does not exceed the configured limit");
    write_pipe(pipe, oversized_line);
    bool disconnected = false;
    for (int attempt = 0; attempt < 50; ++attempt) {
        DWORD available = 0;
        if (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) == FALSE) {
            disconnected = true;
            break;
        }
        Sleep(20);
    }
    require(disconnected, "Bridge Server did not disconnect oversized input");
    guance_electron_bridge_server_stop(bridge);
    CloseHandle(pipe);
    require(
        guance_sdk_get_diagnostics(sdk, &after) == 1 &&
            after.rum_events_enqueued == diagnostics.rum_events_enqueued,
        "Bridge Server accepted an oversized message");

    guance_sdk_shutdown(sdk);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_path, cleanup_error);
    return 0;
}
