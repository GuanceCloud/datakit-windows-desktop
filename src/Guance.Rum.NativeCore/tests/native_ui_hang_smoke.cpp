#include "guance_rum.h"

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>

namespace {

constexpr UINT kBlockUiMessage = WM_APP + 42;

LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == kBlockUiMessage) {
        Sleep(static_cast<DWORD>(wparam));
        return 0;
    }
    if (message == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

bool pump_until_event_count(guance_rum_handle handle, int64_t expected, int timeout_ms) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        guance_rum_diagnostics diagnostics{};
        if (guance_rum_get_diagnostics(handle, &diagnostics) &&
            diagnostics.rum_events_enqueued == expected) {
            return true;
        }
        Sleep(5);
    }
    return false;
}

bool run_block(HWND window, guance_rum_handle handle, DWORD duration_ms, int64_t expected_count) {
    if (!PostMessageW(window, kBlockUiMessage, duration_ms, 0)) {
        return false;
    }
    return pump_until_event_count(handle, expected_count, static_cast<int>(duration_ms) + 2'000);
}

std::string environment_value(const char* name) {
    const DWORD required = GetEnvironmentVariableA(name, nullptr, 0);
    if (required == 0) {
        return {};
    }
    std::string value(required, '\0');
    const DWORD written = GetEnvironmentVariableA(name, value.data(), required);
    if (written == 0 || written >= required) {
        return {};
    }
    value.resize(written);
    return value;
}

} // namespace

int main() {
    const wchar_t* class_name = L"GuanceNativeUiHangSmoke";
    WNDCLASSW window_class{};
    window_class.lpfnWndProc = window_proc;
    window_class.hInstance = GetModuleHandleW(nullptr);
    window_class.lpszClassName = class_name;
    if (!RegisterClassW(&window_class) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        return 1;
    }

    const HWND window = CreateWindowExW(
        0,
        class_name,
        L"Guance UI Hang Smoke",
        WS_OVERLAPPED,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        320,
        200,
        nullptr,
        nullptr,
        window_class.hInstance,
        nullptr);
    if (window == nullptr) {
        return 2;
    }

    guance_rum_config core_config{};
    guance_rum_config_init(&core_config);
    const auto datakit_url = environment_value(
        "GUANCE_RUM_NATIVE_ACCEPTANCE_DATAKIT_URL");
    const auto acceptance_app_id = environment_value(
        "GUANCE_RUM_NATIVE_ACCEPTANCE_APP_ID");
    const auto cache_directory = std::filesystem::temp_directory_path() /
        ("guance-native-ui-hang-smoke-" + std::to_string(GetCurrentProcessId()) + "-" +
         std::to_string(GetTickCount64()));
    std::filesystem::create_directories(cache_directory);
    const auto cache_path = (cache_directory / "rum.db").string();
    core_config.rum_app_id = acceptance_app_id.empty()
        ? "native-ui-hang-smoke"
        : acceptance_app_id.c_str();
    core_config.sample_rate = 1.0;
    core_config.cache_path = cache_path.c_str();
    if (!datakit_url.empty()) {
        core_config.datakit_url = datakit_url.c_str();
    }
    const auto handle = guance_rum_init(&core_config);
    if (handle == nullptr) {
        DestroyWindow(window);
        return 3;
    }

    guance_rum_native_monitoring_config monitoring{};
    guance_rum_native_monitoring_config_init(&monitoring);
    monitoring.enable_ui_hang_monitoring = 1;
    monitoring.main_window_handle = reinterpret_cast<uintptr_t>(window);
    monitoring.ui_probe_interval_ms = 250;
    monitoring.long_task_threshold_ms = 500;
    monitoring.hang_threshold_ms = 5'000;
    monitoring.hang_report_cooldown_ms = 500;

    const HWND child_window = CreateWindowExW(
        0,
        L"STATIC",
        L"Not a top-level monitoring target",
        WS_CHILD,
        0,
        0,
        10,
        10,
        window,
        nullptr,
        window_class.hInstance,
        nullptr);
    auto invalid_monitoring = monitoring;
    invalid_monitoring.main_window_handle = reinterpret_cast<uintptr_t>(child_window);
    if (child_window == nullptr ||
        guance_rum_enable_native_monitoring(handle, &invalid_monitoring)) {
        guance_rum_shutdown(handle);
        DestroyWindow(window);
        return 4;
    }
    DestroyWindow(child_window);

    if (!guance_rum_enable_native_monitoring(handle, &monitoring) ||
        !guance_rum_enable_native_monitoring(handle, &monitoring)) {
        guance_rum_shutdown(handle);
        DestroyWindow(window);
        return 5;
    }

    if (!run_block(window, handle, 800, 1)) {
        std::cerr << "expected exactly one long_task event" << std::endl;
        guance_rum_shutdown(handle);
        DestroyWindow(window);
        return 6;
    }
    Sleep(600);
    if (!run_block(window, handle, 6'000, 2)) {
        std::cerr << "expected exactly one hang error event" << std::endl;
        guance_rum_shutdown(handle);
        DestroyWindow(window);
        return 7;
    }

    if (!datakit_url.empty()) {
        guance_rum_flush(handle);
        guance_rum_diagnostics diagnostics{};
        guance_rum_get_diagnostics(handle, &diagnostics);
        if (diagnostics.rum_upload_success_count < 1 ||
            diagnostics.last_rum_upload_status_code < 200 ||
            diagnostics.last_rum_upload_status_code >= 300) {
            std::cerr << "DataKit did not accept the long_task and error events" << std::endl;
            guance_rum_shutdown(handle);
            DestroyWindow(window);
            return 8;
        }
    }

    std::thread concurrent_disable([&] {
        Sleep(1);
        guance_rum_disable_native_monitoring(handle);
    });
    DestroyWindow(window);
    concurrent_disable.join();
    guance_rum_diagnostics after_destroy{};
    guance_rum_get_diagnostics(handle, &after_destroy);
    if (after_destroy.rum_events_enqueued != 2) {
        std::cerr << "destroyed HWND produced an extra monitoring event" << std::endl;
        guance_rum_shutdown(handle);
        return 9;
    }
    guance_rum_shutdown(handle);
    UnregisterClassW(class_name, window_class.hInstance);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_directory, cleanup_error);
    return 0;
}
