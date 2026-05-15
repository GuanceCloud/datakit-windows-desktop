cmake_minimum_required(VERSION 3.20)
project(guance_rum_native VERSION 0.1.0 LANGUAGES CXX)

find_package(SQLite3 QUIET)

add_library(guance_rum_native SHARED
    src/guance_rum.cpp
    src/line_protocol.cpp
    src/queue_store.cpp
    src/rum_core.cpp
    src/transport.cpp
)

target_compile_features(guance_rum_native PUBLIC cxx_std_17)
target_include_directories(guance_rum_native PUBLIC include PRIVATE src)
target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_BUILDING_DLL=1)

if(WIN32)
    target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_WINDOWS=1)
    target_link_libraries(guance_rum_native PRIVATE winhttp ole32 oleaut32 uiautomationcore)
endif()

if(SQLite3_FOUND)
    target_compile_definitions(guance_rum_native PRIVATE GUANCE_RUM_HAS_SQLITE=1)
    target_link_libraries(guance_rum_native PRIVATE SQLite::SQLite3)
endif()

add_executable(native_line_protocol_smoke tests/native_line_protocol_smoke.cpp src/line_protocol.cpp)
target_include_directories(native_line_protocol_smoke PRIVATE include src)
target_compile_features(native_line_protocol_smoke PRIVATE cxx_std_17)

if(WIN32)
    add_executable(native_win32_replay_smoke tests/native_win32_replay_smoke.cpp)
    target_include_directories(native_win32_replay_smoke PRIVATE include)
    target_compile_features(native_win32_replay_smoke PRIVATE cxx_std_17)
    target_link_libraries(native_win32_replay_smoke PRIVATE guance_rum_native ws2_32)

    set(GUANCE_RUM_NATIVE_RUNTIME_DIR "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-x64")
    foreach(target_name guance_rum_native native_line_protocol_smoke native_win32_replay_smoke)
        set_target_properties(${target_name} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_DEBUG "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELEASE "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${GUANCE_RUM_NATIVE_RUNTIME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${GUANCE_RUM_NATIVE_RUNTIME_DIR}")
    endforeach()
endif()
