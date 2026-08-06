cmake_minimum_required(VERSION 3.20)

project(GuanceWindowsNativePackageConsumer LANGUAGES C)

find_package(GuanceWindowsNative CONFIG REQUIRED)

add_executable(guance_windows_native_package_consumer main.c)
target_link_libraries(
    guance_windows_native_package_consumer
    PRIVATE
        Guance::WindowsNative)
