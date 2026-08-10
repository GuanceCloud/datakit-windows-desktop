#pragma once

#include "guance_sdk.hpp"

#if defined(_WIN32)

#include <windows.h>
#include <winhttp.h>

#include <cerrno>
#include <cstdint>
#include <cwchar>
#include <limits>
#include <string>
#include <utility>
#include <vector>

namespace guance::rum {

/** @brief WinHTTP request completion model used by WinHttpResource. */
enum class WinHttpRequestMode {
    synchronous, /**< Complete the Resource when WinHttpReceiveResponse returns. */
    asynchronous /**< The caller completes the Resource from its WinHTTP callback. */
};

/** @brief Non-owning WinHTTP adapter for RUM Resource and trace propagation.
 *
 * The SDK handle and WinHTTP request must outlive this object, and access from
 * WinHTTP callbacks must be externally serialized. For asynchronous WinHTTP,
 * keep this object alive through the terminal callback and call
 * complete_from_response after WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE.
 */
class WinHttpResource final {
public:
    /** Starts an automatically filtered HTTP Resource and prepares trace headers. */
    WinHttpResource(
        guance_sdk_handle handle,
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
            return;
        }
        initialize_trace(handle, url, method);
    }

    /** WinHTTP Resource adapters cannot be copied. */
    WinHttpResource(const WinHttpResource&) = delete;
    /** WinHTTP Resource adapters cannot be copy-assigned. */
    WinHttpResource& operator=(const WinHttpResource&) = delete;

    /** Stops an incomplete Resource as a failed request. */
    ~WinHttpResource() {
        fail();
    }

    /** Transfers ownership of an active request adapter. */
    WinHttpResource(WinHttpResource&& other) noexcept
        : request_(std::exchange(other.request_, nullptr)),
          request_size_(other.request_size_),
          mode_(other.mode_),
          trace_context_(other.trace_context_),
          trace_context_active_(other.trace_context_active_),
          resource_(std::move(other.resource_)) {
        other.request_size_ = -1;
        other.trace_context_active_ = false;
    }

    /** Stops the current Resource, then takes ownership from another adapter. */
    WinHttpResource& operator=(WinHttpResource&& other) noexcept {
        if (this == &other) {
            return *this;
        }
        fail();
        resource_ = std::move(other.resource_);
        request_ = std::exchange(other.request_, nullptr);
        request_size_ = other.request_size_;
        mode_ = other.mode_;
        trace_context_ = other.trace_context_;
        trace_context_active_ = other.trace_context_active_;
        other.request_size_ = -1;
        other.trace_context_active_ = false;
        return *this;
    }

    /** Calls WinHttpSendRequest after applying configured trace headers. */
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
                fail();
            }
            SetLastError(error);
        }
        return sent;
    }

    /** Calls WinHttpReceiveResponse and completes synchronous requests. */
    BOOL receive(LPVOID reserved = nullptr) noexcept {
        if (request_ == nullptr) {
            SetLastError(ERROR_INVALID_HANDLE);
            return FALSE;
        }

        const BOOL received = WinHttpReceiveResponse(request_, reserved);
        if (!received) {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING) {
                fail();
            }
            SetLastError(error);
            return FALSE;
        }

        if (mode_ == WinHttpRequestMode::synchronous) {
            complete_from_response();
        }
        return TRUE;
    }

    /** Reads response metadata and completes an asynchronous or synchronous Resource. */
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

        std::string request_headers;
        std::string response_headers;
        query_raw_headers(true, request_headers);
        query_raw_headers(false, response_headers);

        resource_.complete(
            has_status ? static_cast<int>(status_code) : 0,
            response_size,
            request_size_,
            linked_trace_id(),
            linked_span_id(),
            protocol[0] == '\0' ? nullptr : protocol,
            request_headers.empty() ? nullptr : request_headers.c_str(),
            response_headers.empty() ? nullptr : response_headers.c_str());
        return has_status != FALSE;
    }

    /** Completes the Resource without a response status. */
    void fail() noexcept {
        resource_.complete(
            0,
            -1,
            request_size_,
            linked_trace_id(),
            linked_span_id());
    }

    /** Returns true while the underlying Resource is active. */
    [[nodiscard]] bool active() const noexcept {
        return resource_.active();
    }

private:
    struct PreparedTraceHeader {
        std::wstring name;
        std::wstring line;
        std::wstring previous_value;
        bool had_previous_value = false;
    };

    void initialize_trace(
        guance_sdk_handle handle,
        const char* url,
        const char* method) noexcept {
        try {
            guance_trace_context_init(&trace_context_);
            if ((guance_trace_create_context)(
                    handle,
                    url,
                    method,
                    &trace_context_) == 0) {
                return;
            }
            if (!add_trace_headers()) {
                return;
            }
            trace_context_active_ = true;
        } catch (...) {
            trace_context_active_ = false;
        }
    }

    bool add_trace_headers() noexcept {
        try {
            std::vector<PreparedTraceHeader> headers;
            headers.reserve(trace_context_.header_count);
            for (uint32_t index = 0; index < trace_context_.header_count; ++index) {
                const std::string name = trace_context_.headers[index].name;
                const std::string value = trace_context_.headers[index].value;
                if (name.empty() ||
                    name.find_first_of("\r\n:") != std::string::npos ||
                    value.find_first_of("\r\n") != std::string::npos) {
                    return false;
                }
                PreparedTraceHeader prepared;
                if (!to_wide(name + ": " + value, prepared.line) ||
                    !to_wide(name, prepared.name) ||
                    !query_request_header(prepared)) {
                    return false;
                }
                headers.push_back(std::move(prepared));
            }

            std::size_t added_count = 0;
            for (const auto& header : headers) {
                if (WinHttpAddRequestHeaders(
                        request_,
                        header.line.c_str(),
                        static_cast<DWORD>(header.line.size()),
                        WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE) == FALSE) {
                    const DWORD add_error = GetLastError();
                    for (std::size_t index = 0; index < added_count; ++index) {
                        restore_request_header(headers[index]);
                    }
                    SetLastError(add_error);
                    return false;
                }
                ++added_count;
            }
            return true;
        } catch (...) {
            return false;
        }
    }

    bool query_request_header(PreparedTraceHeader& header) const {
        DWORD value_size = 0;
        if (WinHttpQueryHeaders(
                request_,
                WINHTTP_QUERY_CUSTOM | WINHTTP_QUERY_FLAG_REQUEST_HEADERS,
                header.name.c_str(),
                WINHTTP_NO_OUTPUT_BUFFER,
                &value_size,
                WINHTTP_NO_HEADER_INDEX) == FALSE) {
            const DWORD error = GetLastError();
            if (error == ERROR_WINHTTP_HEADER_NOT_FOUND) {
                return true;
            }
            if (error != ERROR_INSUFFICIENT_BUFFER) {
                return false;
            }
        }
        if (value_size == 0) {
            header.had_previous_value = true;
            return true;
        }

        std::vector<wchar_t> value(
            (value_size + sizeof(wchar_t) - 1) / sizeof(wchar_t),
            L'\0');
        if (WinHttpQueryHeaders(
                request_,
                WINHTTP_QUERY_CUSTOM | WINHTTP_QUERY_FLAG_REQUEST_HEADERS,
                header.name.c_str(),
                value.data(),
                &value_size,
                WINHTTP_NO_HEADER_INDEX) == FALSE) {
            return false;
        }
        header.previous_value.assign(value.data());
        header.had_previous_value = true;
        return true;
    }

    bool query_raw_headers(bool request_headers, std::string& destination) const {
        DWORD value_size = 0;
        const DWORD query = WINHTTP_QUERY_RAW_HEADERS_CRLF |
            (request_headers ? WINHTTP_QUERY_FLAG_REQUEST_HEADERS : 0);
        WinHttpQueryHeaders(
            request_,
            query,
            WINHTTP_HEADER_NAME_BY_INDEX,
            WINHTTP_NO_OUTPUT_BUFFER,
            &value_size,
            WINHTTP_NO_HEADER_INDEX);
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || value_size == 0) {
            return false;
        }

        std::vector<wchar_t> value(
            (value_size + sizeof(wchar_t) - 1) / sizeof(wchar_t),
            L'\0');
        if (WinHttpQueryHeaders(
                request_,
                query,
                WINHTTP_HEADER_NAME_BY_INDEX,
                value.data(),
                &value_size,
                WINHTTP_NO_HEADER_INDEX) == FALSE) {
            return false;
        }
        return to_utf8(std::wstring(value.data()), destination);
    }

    void restore_request_header(const PreparedTraceHeader& header) noexcept {
        try {
            if (header.had_previous_value) {
                const auto previous = header.name + L": " + header.previous_value;
                WinHttpAddRequestHeaders(
                    request_,
                    previous.c_str(),
                    static_cast<DWORD>(previous.size()),
                    WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE);
                return;
            }
            const auto removal = header.name + L":";
            WinHttpAddRequestHeaders(
                request_,
                removal.c_str(),
                static_cast<DWORD>(removal.size()),
                WINHTTP_ADDREQ_FLAG_REPLACE);
        } catch (...) {
            // Header rollback is best-effort and must not affect the host request.
        }
    }

    static bool to_wide(const std::string& value, std::wstring& destination) {
        const int wide_length = MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0);
        if (wide_length <= 0) {
            return false;
        }
        std::wstring converted(static_cast<std::size_t>(wide_length), L'\0');
        if (MultiByteToWideChar(
                CP_UTF8,
                MB_ERR_INVALID_CHARS,
                value.data(),
                static_cast<int>(value.size()),
                converted.data(),
                wide_length) != wide_length) {
            return false;
        }
        destination = std::move(converted);
        return true;
    }

    static bool to_utf8(const std::wstring& value, std::string& destination) {
        if (value.empty()) {
            destination.clear();
            return true;
        }
        const int utf8_length = WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (utf8_length <= 0) {
            return false;
        }
        std::string converted(static_cast<std::size_t>(utf8_length), '\0');
        if (WideCharToMultiByte(
                CP_UTF8,
                WC_ERR_INVALID_CHARS,
                value.data(),
                static_cast<int>(value.size()),
                converted.data(),
                utf8_length,
                nullptr,
                nullptr) != utf8_length) {
            return false;
        }
        destination = std::move(converted);
        return true;
    }

    const char* linked_trace_id() const noexcept {
        return linked_id(trace_context_.trace_id);
    }

    const char* linked_span_id() const noexcept {
        return linked_id(trace_context_.span_id);
    }

    const char* linked_id(const char* id) const noexcept {
        return trace_context_active_ &&
                trace_context_.link_rum_data != 0 &&
                id != nullptr &&
                id[0] != '\0'
            ? id
            : nullptr;
    }

    HINTERNET request_ = nullptr;
    int64_t request_size_ = -1;
    WinHttpRequestMode mode_ = WinHttpRequestMode::synchronous;
    guance_trace_context trace_context_{};
    bool trace_context_active_ = false;
    ResourceScope resource_;
};

} // namespace guance::rum

#endif
