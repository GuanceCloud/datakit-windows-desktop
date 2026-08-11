#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <regex>
#include <stdexcept>
#include <string>

namespace {

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

std::filesystem::path executable_directory() {
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    require(length > 0 && length < path.size(), "GetModuleFileNameW failed");
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

void set_environment(const wchar_t* name, const std::wstring& value) {
    require(SetEnvironmentVariableW(name, value.c_str()) != FALSE, "SetEnvironmentVariableW failed");
}

std::string read_queue(const std::filesystem::path& root) {
    std::string content;
    if (!std::filesystem::exists(root)) return content;
    for (const auto& entry : std::filesystem::recursive_directory_iterator(root)) {
        if (!entry.is_regular_file()) continue;
        std::ifstream input(entry.path(), std::ios::binary);
        content.append(
            std::istreambuf_iterator<char>(input),
            std::istreambuf_iterator<char>());
    }
    return content;
}

} // namespace

int main() {
    const auto suffix = std::to_wstring(
        std::chrono::steady_clock::now().time_since_epoch().count());
    const auto pipe_name = L"guance-native-owned-smoke-" + suffix;
    const auto pipe_path = L"\\\\.\\pipe\\" + pipe_name;
    const auto cache_path = std::filesystem::temp_directory_path() /
        (L"guance-native-owned-smoke-" + suffix);
    const auto host_path = executable_directory() /
        L"guance_windows_electron_native_owned_host.exe";
    require(std::filesystem::exists(host_path), "native-owned host executable is missing");

    set_environment(L"GUANCE_RUM_NATIVE_OWNED_PIPE_NAME", pipe_name);
    set_environment(L"GUANCE_RUM_NATIVE_OWNED_EXIT_ON_DISCONNECT", L"1");
    set_environment(L"GUANCE_RUM_NATIVE_DATAKIT_URL", L"http://127.0.0.1:9");
    set_environment(L"GUANCE_RUM_NATIVE_APP_ID", L"native-owned-app");
    set_environment(L"GUANCE_RUM_NATIVE_SERVICE", L"native-owned-service");
    set_environment(L"GUANCE_RUM_NATIVE_ENV", L"local");
    set_environment(L"GUANCE_RUM_NATIVE_VERSION", L"1.2.3");
    set_environment(L"GUANCE_RUM_NATIVE_CACHE_PATH", cache_path.wstring());
    set_environment(L"GUANCE_RUM_NATIVE_SAMPLE_RATE", L"1");
    set_environment(L"GUANCE_RUM_NATIVE_LOG_ENABLED", L"0");
    set_environment(L"GUANCE_RUM_NATIVE_SESSION_REPLAY_ENABLED", L"0");
    set_environment(L"GUANCE_RUM_NATIVE_TRACE_ENABLED", L"1");
    set_environment(L"GUANCE_RUM_NATIVE_TRACE_SAMPLE_RATE", L"0.75");
    set_environment(L"GUANCE_RUM_NATIVE_TRACE_TYPE", L"w3c_traceparent");
    set_environment(L"GUANCE_RUM_NATIVE_HTTP_TIMEOUT_MS", L"50");

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    std::wstring command_line = L"\"" + host_path.wstring() + L"\"";
    require(CreateProcessW(
        nullptr,
        command_line.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW,
        nullptr,
        executable_directory().c_str(),
        &startup,
        &process) != FALSE, "CreateProcessW failed");
    CloseHandle(process.hThread);

    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 50 && pipe == INVALID_HANDLE_VALUE; ++attempt) {
        if (WaitNamedPipeW(pipe_path.c_str(), 200)) {
            pipe = CreateFileW(
                pipe_path.c_str(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);
        }
        if (pipe == INVALID_HANDLE_VALUE) Sleep(50);
    }
    require(pipe != INVALID_HANDLE_VALUE, "failed to connect to native-owned host pipe");

    char handshake_buffer[16 * 1024]{};
    DWORD handshake_size = 0;
    require(ReadFile(
        pipe,
        handshake_buffer,
        static_cast<DWORD>(sizeof(handshake_buffer)),
        &handshake_size,
        nullptr) != FALSE, "capabilities handshake read failed");
    const std::string handshake(handshake_buffer, handshake_size);
    require(handshake.rfind("@guance-capabilities\tprotocol=1", 0) == 0,
        "native-owned capabilities handshake is missing");
    require(handshake.find("\trum=1") != std::string::npos &&
        handshake.find("\ttrace=1") != std::string::npos &&
        handshake.find("\ttrace_sample_rate=75") != std::string::npos,
        "native-owned capabilities are incomplete");
    require(handshake.find("native-owned-app") == std::string::npos &&
        handshake.find("127.0.0.1") == std::string::npos,
        "native-owned handshake leaked sensitive SDK configuration");

    const std::string browser_line =
        "view,app_id=renderer-app,env=renderer-env,service=renderer-service,"
        "version=renderer-version,session_id=renderer-session,sdk_name=renderer-sdk,"
        "sdk_version=renderer-sdk,view_id=native-owned-view value=1i "
        "1722300000000000000\n";
    const std::string launch_line =
        "@guance-launch\ttype=cold\tstart_time_ns=1722300000000000000\t"
        "duration_ns=300000000\tpre_application_duration_ns=100000000\t"
        "application_duration_ns=100000000\tfirst_frame_duration_ns=100000000\t"
        "view_id=native-owned-view\tview_name=Main%20View\t"
        "view_referrer=file%3A%2F%2F%2Fsplash.html\n";
    const std::string bridge_input = browser_line + launch_line;
    DWORD written = 0;
    require(WriteFile(
        pipe,
        bridge_input.data(),
        static_cast<DWORD>(bridge_input.size()),
        &written,
        nullptr) != FALSE && written == bridge_input.size(),
        "native-owned bridge write failed");
    FlushFileBuffers(pipe);
    CloseHandle(pipe);

    const DWORD wait = WaitForSingleObject(process.hProcess, 15'000);
    if (wait != WAIT_OBJECT_0) {
        TerminateProcess(process.hProcess, 9);
        WaitForSingleObject(process.hProcess, 2'000);
    }
    DWORD exit_code = 0;
    GetExitCodeProcess(process.hProcess, &exit_code);
    CloseHandle(process.hProcess);
    require(wait == WAIT_OBJECT_0 && exit_code == 0, "native-owned host did not stop cleanly");

    const auto queued = read_queue(cache_path);
    require(queued.find("app_id=native-owned-app") != std::string::npos,
        "native-owned SDK handle did not override the renderer app id");
    require(queued.find("service=native-owned-service") != std::string::npos &&
        queued.find("env=local") != std::string::npos &&
        queued.find("version=1.2.3") != std::string::npos,
        "native-owned SDK handle did not apply its trusted application context");
    require(queued.find("renderer-session") == std::string::npos &&
        queued.find("renderer-sdk") == std::string::npos,
        "native-owned SDK handle retained renderer-owned identity");
    require(std::regex_search(
        queued,
        std::regex(
            "action,[^\\r\\n]*action_type=launch_cold[^\\r\\n]*"
            "view_id=native-owned-view[^\\r\\n]*view_name=Main\\\\ View")),
        "native-owned launch did not retain the Browser View identity");

    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_path, cleanup_error);
    std::cout << "native Electron-owned host smoke passed\n";
    return 0;
}
