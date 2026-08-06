#include "transport.h"
#include "resource_collection.h"

#include <cctype>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <sstream>

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
#include <windows.h>
#include <winhttp.h>
#endif

namespace guance::rum {

namespace {

std::string join_lines(const std::vector<std::string>& lines) {
    std::string body;
    for (const auto& line : lines) {
        body += line;
    }
    return body;
}

std::string append_path(std::string base, const std::string& path) {
    while (!base.empty() && base.back() == '/') {
        base.pop_back();
    }
    return base + "/" + path;
}

std::string percent_encode(const std::string& value);

std::string build_url(const Config& config, const std::string& path) {
    const bool dataway = !config.dataway_url.empty();
    std::string url = append_path(dataway ? config.dataway_url : config.datakit_url, path);
    if (dataway) {
        url += (url.find('?') == std::string::npos ? "?" : "&");
        url += "token=" + percent_encode(config.client_token) + "&to_headless=true";
    }
    return url;
}

std::string percent_encode(const std::string& value) {
    std::ostringstream encoded;
    encoded << std::uppercase << std::hex;
    for (const unsigned char c : value) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') {
            encoded << static_cast<char>(c);
        } else {
            encoded << '%' << std::setw(2) << std::setfill('0') << static_cast<int>(c);
        }
    }
    return encoded.str();
}

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
std::wstring widen(const std::string& value) {
    if (value.empty()) {
        return {};
    }
    const int size = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size);
    return result;
}
#endif

int64_t elapsed_milliseconds(std::chrono::steady_clock::time_point started) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
}

TransportResult success_result(int status_code, int64_t latency_ms = 0) {
    return {true, false, status_code, 0, latency_ms, 0, {}};
}

TransportResult terminal_failure(int status_code, std::string message, int64_t latency_ms = 0) {
    return {true, false, status_code, 0, latency_ms, 0, std::move(message)};
}

TransportResult retry_result(int status_code, std::string message, int error_code = 0, int64_t latency_ms = 0) {
    return {false, true, status_code, error_code, latency_ms, 0, std::move(message)};
}

TransportResult post_body(const Config& config, const std::string& path, const std::string& content_type, const std::string& body) {
    ResourceCollectionSuppressionScope resource_suppression;
    const auto started = std::chrono::steady_clock::now();
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    const auto url = widen(build_url(config, path));
    URL_COMPONENTS components{};
    components.dwStructSize = sizeof(components);
    components.dwSchemeLength = static_cast<DWORD>(-1);
    components.dwHostNameLength = static_cast<DWORD>(-1);
    components.dwUrlPathLength = static_cast<DWORD>(-1);
    components.dwExtraInfoLength = static_cast<DWORD>(-1);

    if (!WinHttpCrackUrl(url.c_str(), static_cast<DWORD>(url.size()), 0, &components)) {
        return retry_result(0, "WinHttpCrackUrl failed", static_cast<int>(GetLastError()), elapsed_milliseconds(started));
    }

    std::wstring host(components.lpszHostName, components.dwHostNameLength);
    std::wstring url_path(components.lpszUrlPath, components.dwUrlPathLength);
    if (components.dwExtraInfoLength > 0) {
        url_path.append(components.lpszExtraInfo, components.dwExtraInfoLength);
    }

    const auto proxy = widen(config.proxy_url);
    HINTERNET session = WinHttpOpen(L"GuanceRUMWindowsNative/0.1",
                                   proxy.empty() ? WINHTTP_ACCESS_TYPE_DEFAULT_PROXY : WINHTTP_ACCESS_TYPE_NAMED_PROXY,
                                   proxy.empty() ? WINHTTP_NO_PROXY_NAME : proxy.c_str(),
                                   WINHTTP_NO_PROXY_BYPASS,
                                   0);
    if (!session) {
        return retry_result(0, "WinHttpOpen failed", static_cast<int>(GetLastError()), elapsed_milliseconds(started));
    }
    const int timeout = config.http_timeout_ms <= 0 ? 10000 : config.http_timeout_ms;
    WinHttpSetTimeouts(session, timeout, timeout, timeout, timeout);

    HINTERNET connect = WinHttpConnect(session, host.c_str(), components.nPort, 0);
    if (!connect) {
        const auto error = static_cast<int>(GetLastError());
        WinHttpCloseHandle(session);
        return retry_result(0, "WinHttpConnect failed", error, elapsed_milliseconds(started));
    }

    const DWORD flags = components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connect,
                                           L"POST",
                                           url_path.c_str(),
                                           nullptr,
                                           WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES,
                                           flags);
    if (!request) {
        const auto error = static_cast<int>(GetLastError());
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return retry_result(0, "WinHttpOpenRequest failed", error, elapsed_milliseconds(started));
    }

    const auto headers = widen("Content-Type: " + content_type + "\r\n");
    const BOOL sent = WinHttpSendRequest(request,
                                         headers.c_str(),
                                         static_cast<DWORD>(-1),
                                         const_cast<char*>(body.data()),
                                         static_cast<DWORD>(body.size()),
                                         static_cast<DWORD>(body.size()),
                                         0);
    TransportResult result;
    if (!sent) {
        result = retry_result(0, "WinHttpSendRequest failed", static_cast<int>(GetLastError()));
    } else if (WinHttpReceiveResponse(request, nullptr)) {
        DWORD status = 0;
        DWORD status_size = sizeof(status);
        if (WinHttpQueryHeaders(request,
                                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                WINHTTP_HEADER_NAME_BY_INDEX,
                                &status,
                                &status_size,
                                WINHTTP_NO_HEADER_INDEX)) {
            if (status >= 200 && status < 300) {
                result = success_result(static_cast<int>(status));
            } else if (status == 408 || status == 429 || status >= 500) {
                result = retry_result(static_cast<int>(status), "server error");
                DWORD retry_after_seconds = 0;
                DWORD retry_after_size = sizeof(retry_after_seconds);
                if (WinHttpQueryHeaders(
                        request,
                        WINHTTP_QUERY_RETRY_AFTER | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX,
                        &retry_after_seconds,
                        &retry_after_size,
                        WINHTTP_NO_HEADER_INDEX)) {
                    result.retry_after_ms = static_cast<int64_t>(retry_after_seconds) * 1000;
                }
            } else {
                result = terminal_failure(static_cast<int>(status), "terminal intake response");
            }
        } else {
            result = retry_result(0, "WinHttpQueryHeaders failed", static_cast<int>(GetLastError()));
        }
    } else if (sent) {
        result = retry_result(0, "WinHttpReceiveResponse failed", static_cast<int>(GetLastError()));
    }

    result.latency_ms = elapsed_milliseconds(started);
    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);
    return result;
#else
    if (config.debug) {
        std::cerr << body;
    }
    return success_result(204, elapsed_milliseconds(started));
#endif
}

} // namespace

TransportResult send_to_dataway(const Config& config, const std::vector<std::string>& lines) {
    if (lines.empty()) {
        return success_result(204);
    }

    return post_body(config, "v1/write/rum", "text/plain", join_lines(lines));
}

TransportResult send_logging_to_dataway(const Config& config, const std::vector<std::string>& lines) {
    if (lines.empty()) {
        return success_result(204);
    }

    return post_body(config, "v1/write/logging", "text/plain", join_lines(lines));
}

TransportResult send_session_replay_to_dataway(const Config& config, const std::string& content_type, const std::string& body) {
    if (body.empty()) {
        return success_result(204);
    }
    return post_body(config, "v1/write/rum/replay", content_type, body);
}

} // namespace guance::rum
