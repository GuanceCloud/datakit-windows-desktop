#include "guance_rum_winhttp.hpp"

#include <windows.h>
#include <winhttp.h>

#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

class InternetHandle final {
public:
    explicit InternetHandle(HINTERNET value = nullptr) noexcept : value_(value) {}
    ~InternetHandle() {
        if (value_ != nullptr) {
            WinHttpCloseHandle(value_);
        }
    }
    InternetHandle(const InternetHandle&) = delete;
    InternetHandle& operator=(const InternetHandle&) = delete;
    [[nodiscard]] HINTERNET get() const noexcept { return value_; }

private:
    HINTERNET value_ = nullptr;
};

class RumHandle final {
public:
    explicit RumHandle(guance_rum_handle value) noexcept : value_(value) {}
    ~RumHandle() {
        if (value_ != nullptr) {
            guance_rum_shutdown(value_);
        }
    }
    RumHandle(const RumHandle&) = delete;
    RumHandle& operator=(const RumHandle&) = delete;
    [[nodiscard]] guance_rum_handle get() const noexcept { return value_; }

private:
    guance_rum_handle value_ = nullptr;
};

void require(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

std::string required_environment(const char* name) {
    const char* value = std::getenv(name);
    if (value == nullptr || value[0] == '\0') {
        throw std::runtime_error(std::string{"Missing environment variable: "} + name);
    }
    return value;
}

std::wstring to_wide(const std::string& value) {
    const int length = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    require(length > 0, "Invalid UTF-8 URL.");
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    require(
        MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            length) == length,
        "Unable to convert URL to UTF-16.");
    return result;
}

std::string to_ascii(const std::wstring& value) {
    std::string result;
    result.reserve(value.size());
    for (const auto character : value) {
        require(character >= 0 && character <= 0x7f, "Expected an ASCII trace header.");
        result.push_back(static_cast<char>(character));
    }
    return result;
}

std::wstring query_header(
    HINTERNET request,
    const wchar_t* name,
    bool request_header) {
    const DWORD flags = WINHTTP_QUERY_CUSTOM |
        (request_header ? WINHTTP_QUERY_FLAG_REQUEST_HEADERS : 0);
    DWORD size = 0;
    WinHttpQueryHeaders(
        request,
        flags,
        name,
        WINHTTP_NO_OUTPUT_BUFFER,
        &size,
        WINHTTP_NO_HEADER_INDEX);
    require(GetLastError() == ERROR_INSUFFICIENT_BUFFER, "Trace header was not available.");
    std::vector<wchar_t> value(
        (size + sizeof(wchar_t) - 1) / sizeof(wchar_t),
        L'\0');
    require(
        WinHttpQueryHeaders(
            request,
            flags,
            name,
            value.data(),
            &size,
            WINHTTP_NO_HEADER_INDEX) != FALSE,
        "Unable to read trace header.");
    return value.data();
}

} // namespace

int main() {
    std::filesystem::path cache_directory;
    try {
        const auto target = required_environment("GUANCE_RUM_TRACE_TEST_URL");
        const auto datakit = required_environment(
            "GUANCE_RUM_NATIVE_ACCEPTANCE_DATAKIT_URL");
        const auto app_id = required_environment(
            "GUANCE_RUM_NATIVE_ACCEPTANCE_APP_ID");

        cache_directory = std::filesystem::temp_directory_path() /
            ("guance-native-trace-acceptance-" + std::to_string(
                std::chrono::steady_clock::now().time_since_epoch().count()));
        std::filesystem::create_directories(cache_directory);
        const auto cache_path = (cache_directory / "rum.db").string();

        guance_rum_config config{};
        guance_rum_config_init(&config);
        config.datakit_url = datakit.c_str();
        config.rum_app_id = app_id.c_str();
        config.service_name = "windows-native-trace-live-acceptance";
        config.env = "local";
        config.cache_path = cache_path.c_str();
        config.sample_rate = 1.0;
        config.debug = 1;
        RumHandle rum(guance_rum_init(&config));
        require(rum.get() != nullptr, "Unable to initialize native RUM.");

        guance_rum_trace_config trace{};
        guance_rum_trace_config_init(&trace);
        trace.enable_auto_trace = 1;
        trace.enable_link_rum_data = 1;
        trace.trace_type = GUANCE_RUM_TRACE_DDTRACE;
        require(
            guance_rum_configure_trace(rum.get(), &trace) == 1,
            "Unable to configure native trace propagation.");

        const auto wide_target = to_wide(target);
        URL_COMPONENTS components{};
        components.dwStructSize = sizeof(components);
        components.dwSchemeLength = static_cast<DWORD>(-1L);
        components.dwHostNameLength = static_cast<DWORD>(-1L);
        components.dwUrlPathLength = static_cast<DWORD>(-1L);
        components.dwExtraInfoLength = static_cast<DWORD>(-1L);
        require(
            WinHttpCrackUrl(
                wide_target.c_str(),
                static_cast<DWORD>(wide_target.size()),
                0,
                &components) != FALSE,
            "Unable to parse target URL.");

        const std::wstring host(
            components.lpszHostName,
            components.dwHostNameLength);
        std::wstring path;
        if (components.dwUrlPathLength > 0) {
            path.assign(components.lpszUrlPath, components.dwUrlPathLength);
        }
        if (components.dwExtraInfoLength > 0) {
            path.append(components.lpszExtraInfo, components.dwExtraInfoLength);
        }
        if (path.empty()) {
            path = L"/";
        }

        InternetHandle session(WinHttpOpen(
            L"GuanceRumNativeTraceAcceptance/0.1",
            WINHTTP_ACCESS_TYPE_NO_PROXY,
            WINHTTP_NO_PROXY_NAME,
            WINHTTP_NO_PROXY_BYPASS,
            0));
        require(session.get() != nullptr, "WinHttpOpen failed.");
        InternetHandle connection(WinHttpConnect(
            session.get(),
            host.c_str(),
            components.nPort,
            0));
        require(connection.get() != nullptr, "WinHttpConnect failed.");
        const DWORD request_flags = components.nScheme == INTERNET_SCHEME_HTTPS
            ? WINHTTP_FLAG_SECURE
            : 0;
        InternetHandle request(WinHttpOpenRequest(
            connection.get(),
            L"GET",
            path.c_str(),
            nullptr,
            WINHTTP_NO_REFERER,
            WINHTTP_DEFAULT_ACCEPT_TYPES,
            request_flags));
        require(request.get() != nullptr, "WinHttpOpenRequest failed.");

        std::string trace_id;
        std::string parent_id;
        int status_code = 0;
        {
            guance::rum::WinHttpResource resource(
                rum.get(),
                request.get(),
                target.c_str(),
                "GET");
            require(resource.active(), "Native RUM Resource was not started.");
            trace_id = to_ascii(query_header(
                request.get(),
                L"x-datadog-trace-id",
                true));
            parent_id = to_ascii(query_header(
                request.get(),
                L"x-datadog-parent-id",
                true));
            require(resource.send() != FALSE, "WinHttpSendRequest failed.");
            require(resource.receive() != FALSE, "WinHttpReceiveResponse failed.");

            DWORD status_size = sizeof(status_code);
            require(
                WinHttpQueryHeaders(
                    request.get(),
                    WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                    WINHTTP_HEADER_NAME_BY_INDEX,
                    &status_code,
                    &status_size,
                    WINHTTP_NO_HEADER_INDEX) != FALSE,
                "Unable to read response status.");
        }

        require(status_code >= 200 && status_code < 300, "Backend returned a non-2xx status.");
        const auto backend_trace_id = to_ascii(query_header(
            request.get(),
            L"trace_id",
            false));
        require(
            backend_trace_id == trace_id,
            "Backend did not continue the injected DDTrace trace ID.");

        guance_rum_flush(rum.get());
        guance_rum_diagnostics diagnostics{};
        require(
            guance_rum_get_diagnostics(rum.get(), &diagnostics) == 1,
            "Unable to read native diagnostics.");
        require(
            diagnostics.rum_upload_success_count >= 1,
            "Native RUM Resource upload did not succeed.");

        std::cout << "native_trace_acceptance=passed status=" << status_code
                  << " trace_id=" << trace_id
                  << " span_id=" << parent_id
                  << " rum_upload_success="
                  << diagnostics.rum_upload_success_count << std::endl;
    } catch (const std::exception& error) {
        std::cerr << "native_trace_acceptance=failed error=" << error.what()
                  << std::endl;
        if (!cache_directory.empty()) {
            std::error_code cleanup_error;
            std::filesystem::remove_all(cache_directory, cleanup_error);
        }
        return 1;
    }

    if (!cache_directory.empty()) {
        std::error_code cleanup_error;
        std::filesystem::remove_all(cache_directory, cleanup_error);
    }
    return 0;
}
