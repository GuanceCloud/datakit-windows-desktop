#include "guance_sdk.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cassert>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <thread>

namespace {

LRESULT CALLBACK launch_window_proc(
    HWND window,
    UINT message,
    WPARAM wparam,
    LPARAM lparam) {
    return DefWindowProcW(window, message, wparam, lparam);
}

HWND create_launch_window(const wchar_t* class_name) {
    WNDCLASSW window_class{};
    window_class.lpfnWndProc = launch_window_proc;
    window_class.hInstance = GetModuleHandleW(nullptr);
    window_class.lpszClassName = class_name;
    RegisterClassW(&window_class);
    return CreateWindowExW(
        0,
        class_name,
        L"Guance automatic launch smoke",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        480,
        320,
        nullptr,
        nullptr,
        window_class.hInstance,
        nullptr);
}

int64_t event_count(guance_sdk_handle handle) {
    guance_sdk_diagnostics diagnostics{};
    assert(guance_sdk_get_diagnostics(handle, &diagnostics) == 1);
    return diagnostics.rum_events_enqueued;
}

bool wait_for_event_count(guance_sdk_handle handle, int64_t expected) {
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::seconds(3);
    do {
        if (event_count(handle) >= expected) {
            return true;
        }
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    } while (std::chrono::steady_clock::now() < deadline);
    return event_count(handle) >= expected;
}

std::string queued_content(const std::filesystem::path& root) {
    std::string content;
    if (!std::filesystem::exists(root)) {
        return content;
    }
    for (const auto& entry : std::filesystem::recursive_directory_iterator(root)) {
        if (!entry.is_regular_file()) {
            continue;
        }
        std::ifstream input(entry.path(), std::ios::binary);
        content.append(
            std::istreambuf_iterator<char>(input),
            std::istreambuf_iterator<char>());
    }
    return content;
}

} // namespace

int main() {
    const auto cache_root = std::filesystem::temp_directory_path() /
        ("guance-native-auto-launch-" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count()));
    const auto enabled_cache = (cache_root / "enabled").string();

    HWND enabled_window = create_launch_window(L"GuanceAutoLaunchEnabled");
    assert(enabled_window != nullptr);

    guance_sdk_config enabled{};
    guance_sdk_config_init(&enabled);
    enabled.cache_path = enabled_cache.c_str();
    enabled.sample_rate = 1.0;
    const auto enabled_handle = guance_sdk_init(&enabled);
    assert(enabled_handle != nullptr);
    assert(event_count(enabled_handle) == 0);

    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    assert(event_count(enabled_handle) == 0);
    ShowWindow(enabled_window, SW_SHOW);
    UpdateWindow(enabled_window);
    assert(wait_for_event_count(enabled_handle, 1));

    const guance_rum_launch duplicate_cold{
        GUANCE_RUM_LAUNCH_COLD,
        1,
        3,
        1,
        1,
        1};
    guance_rum_add_launch_action(enabled_handle, &duplicate_cold);
    assert(event_count(enabled_handle) == 1);

    const auto enabled_content = queued_content(cache_root / "enabled");
    assert(enabled_content.find("action_type=launch_cold") != std::string::npos);
    assert(enabled_content.find("app_pre_application_init_time=") != std::string::npos);
    assert(enabled_content.find("app_application_init_time=") != std::string::npos);
    assert(enabled_content.find("app_first_frame_init_time=") != std::string::npos);

    DestroyWindow(enabled_window);
    guance_sdk_shutdown(enabled_handle);

    const auto disabled_cache = (cache_root / "disabled").string();
    guance_sdk_config disabled{};
    guance_sdk_config_init(&disabled);
    disabled.cache_path = disabled_cache.c_str();
    disabled.sample_rate = 1.0;
    disabled.enable_app_launch_tracking = 0;
    const auto disabled_handle = guance_sdk_init(&disabled);
    assert(disabled_handle != nullptr);

    HWND disabled_window = create_launch_window(L"GuanceAutoLaunchDisabled");
    assert(disabled_window != nullptr);
    ShowWindow(disabled_window, SW_SHOW);
    UpdateWindow(disabled_window);
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    assert(event_count(disabled_handle) == 0);
    guance_rum_add_launch_action(disabled_handle, &duplicate_cold);
    assert(event_count(disabled_handle) == 1);

    DestroyWindow(disabled_window);
    guance_sdk_shutdown(disabled_handle);
    std::error_code cleanup_error;
    std::filesystem::remove_all(cache_root, cleanup_error);
    return 0;
}
