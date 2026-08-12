vcpkg_check_linkage(ONLY_DYNAMIC_LIBRARY)

if(NOT VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
    message(FATAL_ERROR "guance-windows-native currently supports only x64-windows")
endif()

if(DEFINED ENV{GUANCE_WINDOWS_NATIVE_SOURCE_PATH} AND
   NOT "$ENV{GUANCE_WINDOWS_NATIVE_SOURCE_PATH}" STREQUAL "")
    file(TO_CMAKE_PATH "$ENV{GUANCE_WINDOWS_NATIVE_SOURCE_PATH}" SOURCE_PATH)
    if(NOT EXISTS "${SOURCE_PATH}/src/Guance.Windows.Native/CMakeLists.txt")
        message(FATAL_ERROR
            "GUANCE_WINDOWS_NATIVE_SOURCE_PATH does not point to the repository root")
    endif()
else()
    vcpkg_from_github(
        OUT_SOURCE_PATH SOURCE_PATH
        REPO GuanceCloud/datakit-windows-desktop
        REF "@SOURCE_REF@"
        SHA512 @SOURCE_SHA512@
        HEAD_REF main)
endif()

set(GUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE OFF)
if("electron-bridge" IN_LIST FEATURES)
    set(GUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE ON)
endif()
set(GUANCE_WINDOWS_NATIVE_INSTALL_ELECTRON_ADAPTER OFF)
if("electron-adapter" IN_LIST FEATURES OR "electron-bridge" IN_LIST FEATURES)
    set(GUANCE_WINDOWS_NATIVE_INSTALL_ELECTRON_ADAPTER ON)
endif()

vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}/src/Guance.Windows.Native"
    OPTIONS
        -DBUILD_SHARED_LIBS=ON
        -DBUILD_TESTING=OFF
        -DGUANCE_WINDOWS_NATIVE_SDK_VERSION=@VERSION@
        -DGUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE=${GUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE}
        -DGUANCE_WINDOWS_NATIVE_STAGE_RUNTIME=OFF)
vcpkg_cmake_install()
vcpkg_cmake_config_fixup(
    PACKAGE_NAME GuanceWindowsNative
    CONFIG_PATH lib/cmake/GuanceWindowsNative)
vcpkg_copy_pdbs()
if("electron-bridge" IN_LIST FEATURES)
    vcpkg_copy_tools(
        TOOL_NAMES guance_windows_electron_bridge
        AUTO_CLEAN)
endif()
if(GUANCE_WINDOWS_NATIVE_INSTALL_ELECTRON_ADAPTER)
    set(GUANCE_WINDOWS_NATIVE_ELECTRON_SOURCE
        "${SOURCE_PATH}/src/Guance.Windows.Native/electron")
    set(GUANCE_WINDOWS_NATIVE_ELECTRON_DESTINATION
        "${CURRENT_PACKAGES_DIR}/tools/${PORT}/electron")
    file(INSTALL "${GUANCE_WINDOWS_NATIVE_ELECTRON_SOURCE}/package.json"
        DESTINATION "${GUANCE_WINDOWS_NATIVE_ELECTRON_DESTINATION}")
    foreach(GUANCE_WINDOWS_NATIVE_ELECTRON_DIRECTORY IN ITEMS main preload internal)
        file(INSTALL
            "${GUANCE_WINDOWS_NATIVE_ELECTRON_SOURCE}/${GUANCE_WINDOWS_NATIVE_ELECTRON_DIRECTORY}"
            DESTINATION "${GUANCE_WINDOWS_NATIVE_ELECTRON_DESTINATION}")
    endforeach()
endif()

file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include")
file(INSTALL "${SOURCE_PATH}/LICENSE"
    DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}"
    RENAME copyright)
file(INSTALL "${CMAKE_CURRENT_LIST_DIR}/usage"
    DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")
