cmake_minimum_required(VERSION 3.20)
project(guance_rum_native VERSION 0.1.0 LANGUAGES C CXX)

find_package(SQLite3 QUIET)

add_library(guance_rum_native SHARED
    src/guance_rum.cpp
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

target_compile_features(guance_rum_native PUBLIC cxx_std_17)
target_include_directories(guance_rum_native PUBLIC include PRIVATE src)
target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_BUILDING_DLL=1)

if(WIN32)
    target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_WINDOWS=1)
    target_link_libraries(guance_rum_native PRIVATE dbghelp winhttp ole32 oleaut32 uiautomationcore)
endif()

if(SQLite3_FOUND)
    target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_HAS_SQLITE=1)
    target_link_libraries(guance_rum_native PRIVATE SQLite::SQLite3)
endif()

add_executable(native_line_protocol_smoke tests/native_line_protocol_smoke.cpp src/line_protocol.cpp)
target_include_directories(native_line_protocol_smoke PRIVATE include src)
target_compile_features(native_line_protocol_smoke PRIVATE cxx_std_17)

add_executable(native_monitoring_state_smoke
    tests/native_monitoring_state_smoke.cpp
    src/crash_envelope.cpp
    src/hang_state_machine.cpp
)
target_include_directories(native_monitoring_state_smoke PRIVATE include src)
target_compile_features(native_monitoring_state_smoke PRIVATE cxx_std_17)

add_executable(native_resource_collection_state_smoke
    tests/native_resource_collection_state_smoke.cpp
    src/resource_collection.cpp
)
target_include_directories(native_resource_collection_state_smoke PRIVATE include src)
target_compile_features(native_resource_collection_state_smoke PRIVATE cxx_std_17)

add_executable(native_trace_context_smoke
    tests/native_trace_context_smoke.cpp
    src/trace_context.cpp
)
target_include_directories(native_trace_context_smoke PRIVATE include src)
target_compile_features(native_trace_context_smoke PRIVATE cxx_std_17)
if(MSVC)
    target_compile_options(native_trace_context_smoke PRIVATE /UNDEBUG)
else()
    target_compile_options(native_trace_context_smoke PRIVATE -UNDEBUG)
endif()

if(WIN32)
    add_executable(guance_rum_electron_bridge src/electron_bridge_host.cpp)
    target_include_directories(guance_rum_electron_bridge PRIVATE include)
    target_compile_features(guance_rum_electron_bridge PRIVATE cxx_std_17)
    target_compile_definitions(guance_rum_electron_bridge PRIVATE WINVER=0x0601 _WIN32_WINNT=0x0601)
    target_link_libraries(guance_rum_electron_bridge PRIVATE guance_rum_native)
    set_target_properties(guance_rum_electron_bridge PROPERTIES
        OUTPUT_NAME "guance_rum_electron_bridge")

    add_executable(native_browser_bridge_smoke tests/native_browser_bridge_smoke.cpp)
    target_include_directories(native_browser_bridge_smoke PRIVATE include)
    target_compile_features(native_browser_bridge_smoke PRIVATE cxx_std_17)
    target_link_libraries(native_browser_bridge_smoke PRIVATE guance_rum_native)

    add_executable(native_monitoring_abi_smoke tests/native_monitoring_abi_smoke.cpp)
    target_include_directories(native_monitoring_abi_smoke PRIVATE include)
    target_compile_features(native_monitoring_abi_smoke PRIVATE cxx_std_17)
    target_link_libraries(native_monitoring_abi_smoke PRIVATE guance_rum_native)

    add_executable(native_monitoring_c_abi_smoke tests/native_monitoring_c_abi_smoke.c)
    target_include_directories(native_monitoring_c_abi_smoke PRIVATE include)
    target_compile_features(native_monitoring_c_abi_smoke PRIVATE c_std_11)
    target_link_libraries(native_monitoring_c_abi_smoke PRIVATE guance_rum_native)

    add_executable(native_ui_hang_smoke tests/native_ui_hang_smoke.cpp)
    target_include_directories(native_ui_hang_smoke PRIVATE include)
    target_compile_features(native_ui_hang_smoke PRIVATE cxx_std_17)
    target_link_libraries(native_ui_hang_smoke PRIVATE guance_rum_native user32)

    add_executable(native_crash_recovery_smoke tests/native_crash_recovery_smoke.cpp)
    target_include_directories(native_crash_recovery_smoke PRIVATE include)
    target_compile_features(native_crash_recovery_smoke PRIVATE cxx_std_17)
    target_link_libraries(native_crash_recovery_smoke PRIVATE guance_rum_native)

    add_executable(native_win32_replay_smoke tests/native_win32_replay_smoke.cpp)
    target_include_directories(native_win32_replay_smoke PRIVATE include)
    target_compile_features(native_win32_replay_smoke PRIVATE cxx_std_17)
    if(MSVC)
        target_compile_options(native_win32_replay_smoke PRIVATE /UNDEBUG)
    else()
        target_compile_options(native_win32_replay_smoke PRIVATE -UNDEBUG)
    endif()
    target_link_libraries(native_win32_replay_smoke PRIVATE guance_rum_native ws2_32)

    add_executable(native_winhttp_resource_smoke tests/native_winhttp_resource_smoke.cpp)
    target_include_directories(native_winhttp_resource_smoke PRIVATE include)
    target_compile_features(native_winhttp_resource_smoke PRIVATE cxx_std_17)
    if(MSVC)
        target_compile_options(native_winhttp_resource_smoke PRIVATE /UNDEBUG)
    else()
        target_compile_options(native_winhttp_resource_smoke PRIVATE -UNDEBUG)
    endif()
    target_link_libraries(native_winhttp_resource_smoke PRIVATE guance_rum_native winhttp ws2_32)

    add_executable(native_trace_live_acceptance EXCLUDE_FROM_ALL
        tests/native_trace_live_acceptance.cpp)
    target_include_directories(native_trace_live_acceptance PRIVATE include)
    target_compile_features(native_trace_live_acceptance PRIVATE cxx_std_17)
    target_link_libraries(native_trace_live_acceptance PRIVATE guance_rum_native winhttp)

    set(GUANCE_RUM_NATIVE_RUNTIME_DIR "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-x64")
    target_compile_definitions(guance_rum_native PRIVATE WINVER=0x0601 _WIN32_WINNT=0x0601)
    if(MSVC)
        set_property(TARGET guance_rum_native guance_rum_electron_bridge
            PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()

    foreach(target_name guance_rum_native guance_rum_electron_bridge native_line_protocol_smoke native_monitoring_state_smoke native_resource_collection_state_smoke native_trace_context_smoke native_browser_bridge_smoke native_monitoring_abi_smoke native_monitoring_c_abi_smoke native_ui_hang_smoke native_crash_recovery_smoke native_win32_replay_smoke native_winhttp_resource_smoke native_trace_live_acceptance)
        set_target_properties(${target_name} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_DEBUG "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELEASE "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${GUANCE_RUM_NATIVE_RUNTIME_DIR}")
    endforeach()
endif()
