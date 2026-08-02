#pragma once

#include "guance_rum.hpp"

#if defined(_WIN32)

#include <windows.h>
#include <winhttp.h>

#include <cerrno>
#include <cstdint>
#include <cwchar>
#include <limits>
#include <utility>

namespace guance::rum {

enum class WinHttpRequestMode {
    synchronous,
    asynchronous
};

// Non-owning request adapter. The SDK handle and WinHTTP request must outlive
// this object, and access from WinHTTP callbacks must be externally serialized.
// For async WinHTTP, keep this object alive through the terminal callback and
// call complete_from_response after WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE.
class WinHttpResource final {
public:
    WinHttpResource(
        guance_rum_handle handle,
        HINTERNET request,
        const char* url,
        const char* method,
        WinHttpRequestMode mode = WinHttpRequestMode::synchronous) noexcept
        : request_(request),
          mode_(mode),
          resource_(
              handle,
              url,
              method,
              "http",
              ResourceCollectionKind::automatic) {
        if (request_ == nullptr) {
            resource_.fail();
        }
    }

    WinHttpResource(const WinHttpResource&) = delete;
    WinHttpResource& operator=(const WinHttpResource&) = delete;

    WinHttpResource(WinHttpResource&& other) noexcept
        : request_(std::exchange(other.request_, nullptr)),
          request_size_(other.request_size_),
          mode_(other.mode_),
          resource_(std::move(other.resource_)) {
        other.request_size_ = -1;
    }

    WinHttpResource& operator=(WinHttpResource&& other) noexcept {
        if (this == &other) {
            return *this;
        }
        resource_ = std::move(other.resource_);
        request_ = std::exchange(other.request_, nullptr);
        request_size_ = other.request_size_;
        mode_ = other.mode_;
        other.request_size_ = -1;
        return *this;
    }

    BOOL send(
        LPCWSTR additional_headers = WINHTTP_NO_ADDITIONAL_HEADERS,
        DWORD headers_length = 0,
        LPVOID optional_data = WINHTTP_NO_REQUEST_DATA,
        DWORD optional_length = 0,
        DWORD total_length = 0,
        DWORD_PTR context = 0) noexcept {
        if (request_ == nullptr) {
            SetLastError(ERROR_INVALID_HANDLE);
            return FALSE;
        }

        request_size_ = static_cast<int64_t>(total_length);
        const BOOL sent = WinHttpSendRequest(
            request_,
            additional_headers,
            headers_length,
            optional_data,
            optional_length,
            total_length,
            context);
        if (!sent) {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING) {
                resource_.fail();
            }
            SetLastError(error);
        }
        return sent;
    }

    BOOL receive(LPVOID reserved = nullptr) noexcept {
        if (request_ == nullptr) {
            SetLastError(ERROR_INVALID_HANDLE);
            return FALSE;
        }

        const BOOL received = WinHttpReceiveResponse(request_, reserved);
        if (!received) {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING) {
                resource_.fail();
            }
            SetLastError(error);
            return FALSE;
        }

        if (mode_ == WinHttpRequestMode::synchronous) {
            complete_from_response();
        }
        return TRUE;
    }

    bool complete_from_response() noexcept {
        if (request_ == nullptr) {
            return false;
        }

        DWORD status_code = 0;
        DWORD status_size = sizeof(status_code);
        const BOOL has_status = WinHttpQueryHeaders(
            request_,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX,
            &status_code,
            &status_size,
            WINHTTP_NO_HEADER_INDEX);

        wchar_t content_length[32]{};
        DWORD content_length_size = sizeof(content_length);
        const BOOL has_content_length = WinHttpQueryHeaders(
            request_,
            WINHTTP_QUERY_CONTENT_LENGTH,
            WINHTTP_HEADER_NAME_BY_INDEX,
            &content_length,
            &content_length_size,
            WINHTTP_NO_HEADER_INDEX);
        int64_t response_size = -1;
        if (has_content_length) {
            wchar_t* parse_end = nullptr;
            errno = 0;
            const auto parsed = std::wcstoull(content_length, &parse_end, 10);
            if (errno == 0 &&
                parse_end != content_length &&
                *parse_end == L'\0' &&
                parsed <= static_cast<unsigned long long>(
                    (std::numeric_limits<int64_t>::max)())) {
                response_size = static_cast<int64_t>(parsed);
            }
        }

        wchar_t version[32]{};
        DWORD version_size = sizeof(version);
        const BOOL has_version = WinHttpQueryHeaders(
            request_,
            WINHTTP_QUERY_VERSION,
            WINHTTP_HEADER_NAME_BY_INDEX,
            version,
            &version_size,
            WINHTTP_NO_HEADER_INDEX);
        char protocol[32]{};
        if (has_version) {
            for (std::size_t index = 0; index + 1 < sizeof(protocol); ++index) {
                const wchar_t character = version[index];
                if (character == L'\0') {
                    break;
                }
                if (character > 0x7f) {
                    protocol[0] = '\0';
                    break;
                }
                protocol[index] = static_cast<char>(character);
            }
        }

        resource_.complete(
            has_status ? static_cast<int>(status_code) : 0,
            response_size,
            request_size_,
            nullptr,
            nullptr,
            protocol[0] == '\0' ? nullptr : protocol);
        return has_status != FALSE;
    }

    void fail() noexcept {
        resource_.fail();
    }

    [[nodiscard]] bool active() const noexcept {
        return resource_.active();
    }

private:
    HINTERNET request_ = nullptr;
    int64_t request_size_ = -1;
    WinHttpRequestMode mode_ = WinHttpRequestMode::synchronous;
    ResourceScope resource_;
};

} // namespace guance::rum

#endif
