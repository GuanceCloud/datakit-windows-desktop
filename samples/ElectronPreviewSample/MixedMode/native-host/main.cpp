#include <guance_sdk.h>

#include <windows.h>

#include <cmath>
#include <iostream>
#include <string>

namespace {

constexpr const char* kDefaultPipeName = "guance-rum-electron-preview";

std::wstring read_wide_environment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return {};
    std::wstring value(static_cast<std::size_t>(required), L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) return {};
    value.resize(written);
    return value;
}

std::string utf8_from_wide(const std::wstring& value) {
    if (value.empty()) return {};
    const int required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required,
        nullptr,
        nullptr);
    return result;
}

std::string read_environment(const wchar_t* name) {
    return utf8_from_wide(read_wide_environment(name));
}

double read_sample_rate() {
    const auto value = read_environment(L"GUANCE_RUM_NATIVE_SAMPLE_RATE");
    if (value.empty()) return 1.0;
    try {
        const double parsed = std::stod(value);
        return std::isfinite(parsed) && parsed >= 0.0 && parsed <= 1.0
            ? parsed
            : 1.0;
    } catch (...) {
        return 1.0;
    }
}

bool read_debug() {
    const auto value = read_environment(L"GUANCE_RUM_NATIVE_DEBUG");
    return value == "1" || value == "true" || value == "TRUE";
}

} // namespace

int main() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    const auto datakit_url = read_environment(L"GUANCE_RUM_NATIVE_DATAKIT_URL");
    const auto app_id = read_environment(L"GUANCE_RUM_NATIVE_APP_ID");
    const auto service = read_environment(L"GUANCE_RUM_NATIVE_SERVICE");
    const auto environment = read_environment(L"GUANCE_RUM_NATIVE_ENV");
    const auto version = read_environment(L"GUANCE_RUM_NATIVE_VERSION");
    const auto cache_path = read_environment(L"GUANCE_RUM_NATIVE_CACHE_PATH");
    auto pipe_name = read_environment(L"GUANCE_RUM_NATIVE_OWNED_PIPE_NAME");
    if (pipe_name.empty()) pipe_name = kDefaultPipeName;
    if (datakit_url.empty() || app_id.empty()) {
        std::cerr << "[mixed-host] missing DataKit URL or RUM app id" << std::endl;
        return 2;
    }

    guance_sdk_config config{};
    guance_sdk_config_init(&config);
    config.datakit_url = datakit_url.c_str();
    config.rum_app_id = app_id.c_str();
    config.service_name = service.c_str();
    config.env = environment.c_str();
    config.version = version.c_str();
    config.cache_path = cache_path.c_str();
    config.sample_rate = read_sample_rate();
    config.debug = read_debug() ? 1 : 0;

    guance_sdk_handle sdk = guance_sdk_init(&config);
    if (sdk == nullptr) {
        std::cerr << "[mixed-host] guance_sdk_init failed" << std::endl;
        return 3;
    }

    guance_electron_bridge_server_options options{};
    guance_electron_bridge_server_options_init(&options);
    options.pipe_name = pipe_name.c_str();
    options.debug = read_debug() ? 1 : 0;
    guance_electron_bridge_server_handle bridge =
        guance_electron_bridge_server_start(sdk, &options);
    if (bridge == nullptr) {
        std::cerr << "[mixed-host] Bridge Server start failed" << std::endl;
        guance_sdk_shutdown(sdk);
        return 4;
    }

    std::cout << "[mixed-host] ready pipe=\\\\.\\pipe\\" << pipe_name
              << " sdk_version=" << guance_sdk_get_version() << std::endl;
    std::string ignored;
    std::getline(std::cin, ignored);

    guance_electron_bridge_server_stop(bridge);
    guance_sdk_flush(sdk);
    guance_sdk_diagnostics diagnostics{};
    if (guance_sdk_get_diagnostics(sdk, &diagnostics) == 1) {
        std::cout << "[mixed-host] rum_events_enqueued="
                  << diagnostics.rum_events_enqueued << std::endl;
    }
    guance_sdk_shutdown(sdk);
    std::cout << "[mixed-host] stopped" << std::endl;
    return 0;
}
