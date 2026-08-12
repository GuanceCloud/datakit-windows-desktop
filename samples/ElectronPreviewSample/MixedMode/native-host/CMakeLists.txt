cmake_minimum_required(VERSION 3.21)

project(GuanceElectronMixedPreview LANGUAGES CXX)

find_package(GuanceWindowsNative CONFIG REQUIRED)

add_executable(guance_windows_electron_mixed_host main.cpp)
target_compile_features(guance_windows_electron_mixed_host PRIVATE cxx_std_17)
target_compile_definitions(guance_windows_electron_mixed_host PRIVATE
    WINVER=0x0A00
    _WIN32_WINNT=0x0A00)
target_link_libraries(guance_windows_electron_mixed_host PRIVATE
    Guance::WindowsNative)
