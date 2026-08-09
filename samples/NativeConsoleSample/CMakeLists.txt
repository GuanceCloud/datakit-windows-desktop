cmake_minimum_required(VERSION 3.20)

project(GuanceWindowsNativeConsoleSample LANGUAGES C)

find_package(GuanceWindowsNative CONFIG REQUIRED)

add_executable(guance_windows_native_console_sample main.c)
target_link_libraries(
    guance_windows_native_console_sample
    PRIVATE
        Guance::WindowsNative)

