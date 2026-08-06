#include "guance_sdk.hpp"

#include <windows.h>

#include <crtdbg.h>

#include <chrono>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>

namespace {

wchar_t previous_handler_marker[MAX_PATH]{};

void write_previous_handler_marker() noexcept {
    if (previous_handler_marker[0] == L'\0') {
        return;
    }
    const HANDLE marker = CreateFileW(
        previous_handler_marker,
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr);
    if (marker == INVALID_HANDLE_VALUE) {
        return;
    }
    const char value = '1';
    DWORD written = 0;
    WriteFile(marker, &value, sizeof(value), &written, nullptr);
    FlushFileBuffers(marker);
    CloseHandle(marker);
}

LONG WINAPI previous_exception_filter(EXCEPTION_POINTERS*) {
    guance_sdk_capture_cpp_terminate();
    write_previous_handler_marker();
    return EXCEPTION_CONTINUE_SEARCH;
}

[[noreturn]] void previous_terminate_handler() {
    guance_sdk_capture_cpp_terminate();
    write_previous_handler_marker();
    std::abort();
}

std::filesystem::path unique_directory() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
           ("guance-native-crash-smoke-" + std::to_string(suffix));
}

std::wstring quote(const std::wstring& value) {
    return L"\"" + value + L"\"";
}

bool launch_crash_child(
    const std::filesystem::path& executable,
    const std::filesystem::path& crash_directory,
    const wchar_t* mode) {
    std::wstring command = quote(executable.wstring()) + L" --child " + mode + L" " +
                           quote(crash_directory.wstring());
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(
            executable.c_str(),
            command.data(),
            nullptr,
            nullptr,
            FALSE,
            0,
            nullptr,
            nullptr,
            &startup,
            &process)) {
        return false;
    }
    const DWORD wait_result = WaitForSingleObject(process.hProcess, 15'000);
    DWORD exit_code = 0;
    GetExitCodeProcess(process.hProcess, &exit_code);
    if (wait_result != WAIT_OBJECT_0) {
        TerminateProcess(process.hProcess, 0xDEADu);
        WaitForSingleObject(process.hProcess, 5'000);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return wait_result == WAIT_OBJECT_0 && exit_code != 0 && exit_code != STILL_ACTIVE;
}

int child_main(const std::wstring& mode, const std::filesystem::path& crash_directory) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);

    if (mode == L"access-chain" || mode == L"terminate-chain") {
        const auto marker_path = crash_directory / (mode + L".marker");
        wcsncpy_s(previous_handler_marker, marker_path.c_str(), _TRUNCATE);
        if (mode == L"access-chain") {
            SetUnhandledExceptionFilter(&previous_exception_filter);
        } else {
            std::set_terminate(&previous_terminate_handler);
        }
    }

    guance_sdk_config core_config{};
    guance_sdk_config_init(&core_config);
    core_config.rum_app_id = "native-crash-child";
    core_config.sample_rate = 1.0;
    const auto queue_path = (crash_directory / "cache").string();
    core_config.cache_path = queue_path.c_str();
    const auto handle = guance_sdk_init(&core_config);
    if (handle == nullptr) {
        return 10;
    }

    const auto path = crash_directory.string();
    guance_sdk_native_monitoring_config monitoring{};
    guance_sdk_native_monitoring_config_init(&monitoring);
    monitoring.enable_native_crash_reporting = 1;
    monitoring.enable_minidump = mode == L"access-dump" ? 1 : 0;
    if (mode != L"access-default") {
        monitoring.crash_cache_path = path.c_str();
    }
    if (!guance_sdk_enable_native_monitoring(handle, &monitoring)) {
        return 11;
    }

    if (mode == L"capture-twice") {
        guance_sdk_capture_cpp_terminate();
        guance_sdk_capture_cpp_terminate();
        ExitProcess(22);
    }
    if (mode == L"access" || mode == L"access-dump" || mode == L"access-default" ||
        mode == L"access-chain") {
        *reinterpret_cast<volatile int*>(0x1) = 42;
    }
    std::terminate();
}

int64_t recover_once(const std::filesystem::path& crash_directory) {
    const auto queue_path = (crash_directory / "cache").string();
    guance_sdk_config core_config{};
    guance_sdk_config_init(&core_config);
    core_config.rum_app_id = "native-crash-parent";
    core_config.sample_rate = 1.0;
    core_config.cache_path = queue_path.c_str();
    const auto handle = guance_sdk_init(&core_config);
    if (handle == nullptr) {
        return -1;
    }

    const auto path = crash_directory.string();
    guance_sdk_native_monitoring_config monitoring{};
    guance_sdk_native_monitoring_config_init(&monitoring);
    monitoring.enable_native_crash_reporting = 1;
    monitoring.crash_cache_path = path.c_str();
    if (!guance_sdk_enable_native_monitoring(handle, &monitoring)) {
        guance_sdk_shutdown(handle);
        return -1;
    }

    guance_sdk_diagnostics diagnostics{};
    guance_sdk_get_diagnostics(handle, &diagnostics);
    guance_sdk_disable_native_monitoring(handle);
    guance_sdk_shutdown(handle);
    return diagnostics.rum_events_enqueued;
}

int64_t recover_default_on_init(const std::filesystem::path& root_directory) {
    const auto queue_path = (root_directory / "cache").string();
    guance_sdk_config core_config{};
    guance_sdk_config_init(&core_config);
    core_config.rum_app_id = "native-crash-parent";
    core_config.sample_rate = 1.0;
    core_config.cache_path = queue_path.c_str();
    const auto handle = guance_sdk_init(&core_config);
    if (handle == nullptr) {
        return -1;
    }
    guance_sdk_diagnostics diagnostics{};
    guance_sdk_get_diagnostics(handle, &diagnostics);
    guance_sdk_shutdown(handle);
    return diagnostics.rum_events_enqueued;
}

bool handlers_are_restored(const std::filesystem::path& crash_directory) {
    const auto queue_path = (crash_directory / "restore-cache").string();
    guance_sdk_config core_config{};
    guance_sdk_config_init(&core_config);
    core_config.rum_app_id = "native-handler-restore";
    core_config.cache_path = queue_path.c_str();
    const auto handle = guance_sdk_init(&core_config);
    if (handle == nullptr) {
        return false;
    }

    const auto original_exception = SetUnhandledExceptionFilter(&previous_exception_filter);
    const auto original_terminate = std::set_terminate(&previous_terminate_handler);
    const auto path = crash_directory.string();
    guance_sdk_native_monitoring_config monitoring{};
    guance_sdk_native_monitoring_config_init(&monitoring);
    monitoring.enable_native_crash_reporting = 1;
    monitoring.crash_cache_path = path.c_str();
    const bool enabled = guance_sdk_enable_native_monitoring(handle, &monitoring) == 1;
    std::thread concurrent_capture([] { guance_sdk_capture_cpp_terminate(); });
    guance_sdk_disable_native_monitoring(handle);
    concurrent_capture.join();
    const auto restored_exception = SetUnhandledExceptionFilter(original_exception);
    const auto restored_terminate = std::set_terminate(original_terminate);
    guance_sdk_shutdown(handle);
    return enabled && restored_exception == &previous_exception_filter &&
           restored_terminate == &previous_terminate_handler;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc == 4 && std::wstring(argv[1]) == L"--child") {
        return child_main(argv[2], argv[3]);
    }

    wchar_t executable_buffer[MAX_PATH]{};
    GetModuleFileNameW(nullptr, executable_buffer, MAX_PATH);
    const std::filesystem::path executable(executable_buffer);
    const auto crash_directory = unique_directory();
    std::filesystem::create_directories(crash_directory);

    if (!launch_crash_child(executable, crash_directory, L"access") ||
        recover_once(crash_directory) != 1 ||
        recover_once(crash_directory) != 0) {
        std::cerr << "Access Violation recovery did not enqueue exactly once" << std::endl;
        return 1;
    }

    if (!launch_crash_child(executable, crash_directory, L"terminate") ||
        recover_once(crash_directory) != 1 ||
        recover_once(crash_directory) != 0) {
        std::cerr << "std::terminate recovery did not enqueue exactly once" << std::endl;
        return 2;
    }

    if (!launch_crash_child(executable, crash_directory, L"capture-twice") ||
        recover_once(crash_directory) != 1 ||
        recover_once(crash_directory) != 0) {
        std::cerr << "Crash handler reentry was not deduplicated" << std::endl;
        return 3;
    }

    for (const wchar_t* mode : {L"access-chain", L"terminate-chain"}) {
        const auto marker = crash_directory / (std::wstring(mode) + L".marker");
        if (!launch_crash_child(executable, crash_directory, mode) ||
            !std::filesystem::exists(marker) ||
            recover_once(crash_directory) != 1 ||
            recover_once(crash_directory) != 0) {
            std::cerr << "Previous crash handler was not chained" << std::endl;
            return 4;
        }
    }
    if (!handlers_are_restored(crash_directory)) {
        std::cerr << "Crash handlers were not restored after disable" << std::endl;
        return 5;
    }

    const auto default_directory = crash_directory / "default-recovery";
    std::filesystem::create_directories(default_directory);
    if (!launch_crash_child(executable, default_directory, L"access-default") ||
        recover_default_on_init(default_directory) != 1 ||
        recover_default_on_init(default_directory) != 0) {
        std::cerr << "Default crash path was not recovered during initialization" << std::endl;
        return 6;
    }

    if (!launch_crash_child(executable, crash_directory, L"access-dump")) {
        std::cerr << "Minidump child did not terminate" << std::endl;
        return 7;
    }
    bool found_minidump = false;
    for (const auto& entry : std::filesystem::directory_iterator(crash_directory)) {
        if (entry.path().extension() == ".dmp" && entry.file_size() > 0) {
            found_minidump = true;
        }
    }
    if (!found_minidump ||
        recover_once(crash_directory) != 1 ||
        recover_once(crash_directory) != 0) {
        std::cerr << "Minidump recovery did not enqueue exactly once" << std::endl;
        return 8;
    }

    std::error_code ec;
    std::filesystem::remove_all(crash_directory, ec);
    return 0;
}
