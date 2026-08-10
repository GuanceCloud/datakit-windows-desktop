cmake_minimum_required(VERSION 3.20)

project(GuanceWindowsNative VERSION 0.1.0 LANGUAGES C CXX)

include(CMakePackageConfigHelpers)
include(CTest)
include(GNUInstallDirs)

option(BUILD_SHARED_LIBS "Build Guance Windows Native as a shared library" ON)
option(
    GUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE
    "Build the optional Electron native bridge executable"
    OFF)
option(
    GUANCE_WINDOWS_NATIVE_STAGE_RUNTIME
    "Write local Windows runtime artifacts for managed/Electron packaging"
    OFF)
set(
    GUANCE_WINDOWS_NATIVE_RUNTIME_ARCH
    "x64"
    CACHE STRING
    "Runtime architecture directory used when staging Windows artifacts")
set_property(
    CACHE GUANCE_WINDOWS_NATIVE_RUNTIME_ARCH
    PROPERTY STRINGS x64 x86 arm64)

if(MSVC)
    add_compile_options($<$<COMPILE_LANGUAGE:CXX>:/EHsc>)
endif()

add_library(guance_windows_native
    src/data_modifier.cpp
    src/guance_sdk.cpp
    src/line_protocol.cpp
    src/crash_envelope.cpp
    src/hang_state_machine.cpp
    src/native_crash_reporter.cpp
    src/native_monitoring.cpp
    src/queue_store.cpp
    src/resource_collection.cpp
    src/trace_context.cpp
    src/rum_core.cpp
    src/thread_stack_sampler.cpp
    src/transport.cpp
    src/ui_hang_monitor.cpp
)
add_library(Guance::WindowsNative ALIAS guance_windows_native)

set_target_properties(guance_windows_native PROPERTIES
    EXPORT_NAME WindowsNative
    OUTPUT_NAME guance_windows_native
    VERSION "${PROJECT_VERSION}"
    SOVERSION "${PROJECT_VERSION_MAJOR}")

target_compile_features(guance_windows_native PUBLIC cxx_std_17)
target_include_directories(guance_windows_native
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
    PRIVATE
        src)
target_compile_definitions(guance_windows_native
    PRIVATE
        $<$<BOOL:${BUILD_SHARED_LIBS}>:GUANCE_WINDOWS_NATIVE_BUILDING_DLL=1>
    PUBLIC
        $<$<NOT:$<BOOL:${BUILD_SHARED_LIBS}>>:GUANCE_WINDOWS_NATIVE_STATIC=1>)

if(WIN32)
    target_compile_definitions(guance_windows_native PRIVATE
        GUANCE_WINDOWS_NATIVE_WINDOWS=1
        WINVER=0x0601
        _WIN32_WINNT=0x0601)
    target_link_libraries(guance_windows_native PRIVATE
        dbghelp
        winhttp
        ole32
        oleaut32
        uiautomationcore)
endif()

function(guance_enable_assertions target_name)
    if(MSVC)
        target_compile_options(${target_name} PRIVATE /UNDEBUG)
    else()
        target_compile_options(${target_name} PRIVATE -UNDEBUG)
    endif()
endfunction()

set(GUANCE_WINDOWS_NATIVE_LOCAL_TARGETS guance_windows_native)

if(WIN32 AND GUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE)
    add_executable(guance_windows_electron_bridge src/electron_bridge_host.cpp)
    target_include_directories(guance_windows_electron_bridge PRIVATE include)
    target_compile_features(guance_windows_electron_bridge PRIVATE cxx_std_17)
    target_compile_definitions(guance_windows_electron_bridge PRIVATE
        WINVER=0x0601
        _WIN32_WINNT=0x0601)
    target_link_libraries(guance_windows_electron_bridge PRIVATE guance_windows_native)
    set_target_properties(guance_windows_electron_bridge PROPERTIES
        OUTPUT_NAME guance_windows_electron_bridge)

    list(APPEND GUANCE_WINDOWS_NATIVE_LOCAL_TARGETS
        guance_windows_electron_bridge)
endif()

if(BUILD_TESTING)
    add_executable(native_line_protocol_smoke
        tests/native_line_protocol_smoke.cpp
        src/line_protocol.cpp)
    target_include_directories(native_line_protocol_smoke PRIVATE include src)
    target_compile_features(native_line_protocol_smoke PRIVATE cxx_std_17)
    add_test(NAME native_line_protocol_smoke COMMAND native_line_protocol_smoke)

    add_executable(native_monitoring_state_smoke
        tests/native_monitoring_state_smoke.cpp
        src/crash_envelope.cpp
        src/hang_state_machine.cpp)
    target_include_directories(native_monitoring_state_smoke PRIVATE include src)
    target_compile_features(native_monitoring_state_smoke PRIVATE cxx_std_17)
    add_test(NAME native_monitoring_state_smoke COMMAND native_monitoring_state_smoke)

    add_executable(native_resource_collection_state_smoke
        tests/native_resource_collection_state_smoke.cpp
        src/resource_collection.cpp)
    target_include_directories(native_resource_collection_state_smoke PRIVATE include src)
    target_compile_features(native_resource_collection_state_smoke PRIVATE cxx_std_17)
    add_test(NAME native_resource_collection_state_smoke COMMAND native_resource_collection_state_smoke)

    add_executable(native_trace_context_smoke
        tests/native_trace_context_smoke.cpp
        src/trace_context.cpp)
    target_include_directories(native_trace_context_smoke PRIVATE include src)
    target_compile_features(native_trace_context_smoke PRIVATE cxx_std_17)
    guance_enable_assertions(native_trace_context_smoke)
    add_test(NAME native_trace_context_smoke COMMAND native_trace_context_smoke)

    add_executable(native_batch_queue_smoke
        tests/native_batch_queue_smoke.cpp
        src/queue_store.cpp)
    target_include_directories(native_batch_queue_smoke PRIVATE include src)
    target_compile_features(native_batch_queue_smoke PRIVATE cxx_std_17)
    guance_enable_assertions(native_batch_queue_smoke)
    add_test(NAME native_batch_queue_smoke COMMAND native_batch_queue_smoke)

    list(APPEND GUANCE_WINDOWS_NATIVE_LOCAL_TARGETS
        native_line_protocol_smoke
        native_monitoring_state_smoke
        native_resource_collection_state_smoke
        native_trace_context_smoke
        native_batch_queue_smoke)

    if(WIN32)
        add_executable(native_browser_bridge_smoke tests/native_browser_bridge_smoke.cpp)
        target_include_directories(native_browser_bridge_smoke PRIVATE include)
        target_compile_features(native_browser_bridge_smoke PRIVATE cxx_std_17)
        target_link_libraries(native_browser_bridge_smoke PRIVATE guance_windows_native)
        add_test(NAME native_browser_bridge_smoke COMMAND native_browser_bridge_smoke)

        add_executable(native_monitoring_abi_smoke tests/native_monitoring_abi_smoke.cpp)
        target_include_directories(native_monitoring_abi_smoke PRIVATE include)
        target_compile_features(native_monitoring_abi_smoke PRIVATE cxx_std_17)
        target_link_libraries(native_monitoring_abi_smoke PRIVATE guance_windows_native)
        add_test(NAME native_monitoring_abi_smoke COMMAND native_monitoring_abi_smoke)

        add_executable(native_monitoring_c_abi_smoke tests/native_monitoring_c_abi_smoke.c)
        target_include_directories(native_monitoring_c_abi_smoke PRIVATE include)
        target_compile_features(native_monitoring_c_abi_smoke PRIVATE c_std_11)
        target_link_libraries(native_monitoring_c_abi_smoke PRIVATE guance_windows_native)
        add_test(NAME native_monitoring_c_abi_smoke COMMAND native_monitoring_c_abi_smoke)

        add_executable(native_ui_hang_smoke tests/native_ui_hang_smoke.cpp)
        target_include_directories(native_ui_hang_smoke PRIVATE include)
        target_compile_features(native_ui_hang_smoke PRIVATE cxx_std_17)
        target_link_libraries(native_ui_hang_smoke PRIVATE guance_windows_native user32)
        add_test(NAME native_ui_hang_smoke COMMAND native_ui_hang_smoke)

        add_executable(native_crash_recovery_smoke tests/native_crash_recovery_smoke.cpp)
        target_include_directories(native_crash_recovery_smoke PRIVATE include)
        target_compile_features(native_crash_recovery_smoke PRIVATE cxx_std_17)
        target_link_libraries(native_crash_recovery_smoke PRIVATE guance_windows_native)
        add_test(NAME native_crash_recovery_smoke COMMAND native_crash_recovery_smoke)

        add_executable(native_win32_replay_smoke tests/native_win32_replay_smoke.cpp)
        target_include_directories(native_win32_replay_smoke PRIVATE include)
        target_compile_features(native_win32_replay_smoke PRIVATE cxx_std_17)
        guance_enable_assertions(native_win32_replay_smoke)
        target_link_libraries(native_win32_replay_smoke PRIVATE guance_windows_native ws2_32)
        add_test(NAME native_win32_replay_smoke COMMAND native_win32_replay_smoke)

        add_executable(native_winhttp_resource_smoke tests/native_winhttp_resource_smoke.cpp)
        target_include_directories(native_winhttp_resource_smoke PRIVATE include)
        target_compile_features(native_winhttp_resource_smoke PRIVATE cxx_std_17)
        guance_enable_assertions(native_winhttp_resource_smoke)
        target_link_libraries(native_winhttp_resource_smoke PRIVATE
            guance_windows_native
            winhttp
            ws2_32)
        add_test(NAME native_winhttp_resource_smoke COMMAND native_winhttp_resource_smoke)

        add_executable(native_trace_live_acceptance EXCLUDE_FROM_ALL
            tests/native_trace_live_acceptance.cpp)
        target_include_directories(native_trace_live_acceptance PRIVATE include)
        target_compile_features(native_trace_live_acceptance PRIVATE cxx_std_17)
        target_link_libraries(native_trace_live_acceptance PRIVATE
            guance_windows_native
            winhttp)

        list(APPEND GUANCE_WINDOWS_NATIVE_LOCAL_TARGETS
            native_browser_bridge_smoke
            native_monitoring_abi_smoke
            native_monitoring_c_abi_smoke
            native_ui_hang_smoke
            native_crash_recovery_smoke
            native_win32_replay_smoke
            native_winhttp_resource_smoke
            native_trace_live_acceptance)
    endif()
endif()

if(WIN32 AND GUANCE_WINDOWS_NATIVE_STAGE_RUNTIME)
    if(NOT GUANCE_WINDOWS_NATIVE_RUNTIME_ARCH MATCHES "^(x64|x86|arm64)$")
        message(FATAL_ERROR
            "GUANCE_WINDOWS_NATIVE_RUNTIME_ARCH must be x64, x86, or arm64")
    endif()
    set(GUANCE_WINDOWS_NATIVE_RUNTIME_DIR
        "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-${GUANCE_WINDOWS_NATIVE_RUNTIME_ARCH}")
    foreach(target_name IN LISTS GUANCE_WINDOWS_NATIVE_LOCAL_TARGETS)
        set_target_properties(${target_name} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${GUANCE_WINDOWS_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_DEBUG "${GUANCE_WINDOWS_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELEASE "${GUANCE_WINDOWS_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${GUANCE_WINDOWS_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${GUANCE_WINDOWS_NATIVE_RUNTIME_DIR}")
    endforeach()
endif()

install(TARGETS guance_windows_native
    EXPORT GuanceWindowsNativeTargets
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    INCLUDES DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
if(TARGET guance_windows_electron_bridge)
    install(TARGETS guance_windows_electron_bridge
        RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
endif()
install(DIRECTORY include/
    DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
    FILES_MATCHING
        PATTERN "*.h"
        PATTERN "*.hpp")

set(GUANCE_WINDOWS_NATIVE_INSTALL_CMAKEDIR
    "${CMAKE_INSTALL_LIBDIR}/cmake/GuanceWindowsNative")
configure_package_config_file(
    cmake/GuanceWindowsNativeConfig.cmake.in
    "${CMAKE_CURRENT_BINARY_DIR}/GuanceWindowsNativeConfig.cmake"
    INSTALL_DESTINATION "${GUANCE_WINDOWS_NATIVE_INSTALL_CMAKEDIR}")
write_basic_package_version_file(
    "${CMAKE_CURRENT_BINARY_DIR}/GuanceWindowsNativeConfigVersion.cmake"
    VERSION "${PROJECT_VERSION}"
    COMPATIBILITY SameMajorVersion)

install(EXPORT GuanceWindowsNativeTargets
    FILE GuanceWindowsNativeTargets.cmake
    NAMESPACE Guance::
    DESTINATION "${GUANCE_WINDOWS_NATIVE_INSTALL_CMAKEDIR}")
install(FILES
    "${CMAKE_CURRENT_BINARY_DIR}/GuanceWindowsNativeConfig.cmake"
    "${CMAKE_CURRENT_BINARY_DIR}/GuanceWindowsNativeConfigVersion.cmake"
    DESTINATION "${GUANCE_WINDOWS_NATIVE_INSTALL_CMAKEDIR}")
