#include "anonymous_user_id.h"

#include "line_protocol.h"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iterator>
#include <sstream>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/file.h>
#include <unistd.h>
#endif

namespace guance::rum {

namespace {

constexpr char kPrefix[] = "ft.rd_";
constexpr std::size_t kIdentifierLength = 32;
constexpr int kLockAttemptCount = 40;

class IdentityLock {
public:
    explicit IdentityLock(const std::filesystem::path& path) {
#if defined(_WIN32)
        for (int attempt = 0; attempt < kLockAttemptCount; ++attempt) {
            handle_ = CreateFileW(
                path.c_str(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (handle_ != INVALID_HANDLE_VALUE) {
                return;
            }
            const auto error = GetLastError();
            if (error != ERROR_SHARING_VIOLATION && error != ERROR_LOCK_VIOLATION) {
                error_ = "failed to open identity lock: " + std::to_string(error);
                return;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        }
        error_ = "timed out acquiring identity lock";
#else
        descriptor_ = ::open(path.c_str(), O_CREAT | O_RDWR, 0600);
        if (descriptor_ < 0) {
            error_ = "failed to open identity lock";
            return;
        }
        if (::flock(descriptor_, LOCK_EX) != 0) {
            error_ = "failed to acquire identity lock";
            ::close(descriptor_);
            descriptor_ = -1;
        }
#endif
    }

    ~IdentityLock() {
#if defined(_WIN32)
        if (handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
        }
#else
        if (descriptor_ >= 0) {
            ::flock(descriptor_, LOCK_UN);
            ::close(descriptor_);
        }
#endif
    }

    IdentityLock(const IdentityLock&) = delete;
    IdentityLock& operator=(const IdentityLock&) = delete;

    bool acquired() const {
#if defined(_WIN32)
        return handle_ != INVALID_HANDLE_VALUE;
#else
        return descriptor_ >= 0;
#endif
    }

    const std::string& error() const { return error_; }

private:
#if defined(_WIN32)
    HANDLE handle_ = INVALID_HANDLE_VALUE;
#else
    int descriptor_ = -1;
#endif
    std::string error_;
};

std::string create_identifier() {
    return std::string(kPrefix) + uuid32();
}

std::string application_scope(const std::string& rum_app_id) {
    constexpr std::uint64_t offset = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    std::uint64_t hash = offset;
    for (const unsigned char value : rum_app_id) {
        hash ^= value;
        hash *= prime;
    }

    std::ostringstream output;
    output << std::hex << std::nouppercase << std::setw(16) << std::setfill('0') << hash;
    return output.str();
}

bool read_valid_identifier(const std::filesystem::path& path, std::string& value) {
    std::error_code error;
    if (!std::filesystem::is_regular_file(path, error) || error ||
        std::filesystem::file_size(path, error) > 128 || error) {
        return false;
    }

    std::ifstream input(path, std::ios::binary);
    if (!input) {
        return false;
    }
    std::string stored{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    if (!is_valid_anonymous_user_id(stored)) {
        return false;
    }
    value = std::move(stored);
    return true;
}

bool flush_file(const std::filesystem::path& path) {
#if defined(_WIN32)
    const auto handle = CreateFileW(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return false;
    }
    const bool flushed = FlushFileBuffers(handle) != FALSE;
    CloseHandle(handle);
    return flushed;
#else
    const auto descriptor = ::open(path.c_str(), O_RDONLY);
    if (descriptor < 0) {
        return false;
    }
    const bool flushed = ::fsync(descriptor) == 0;
    ::close(descriptor);
    return flushed;
#endif
}

bool replace_file(
    const std::filesystem::path& source,
    const std::filesystem::path& destination,
    std::string& error_message) {
#if defined(_WIN32)
    if (MoveFileExW(
            source.c_str(),
            destination.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        return true;
    }
    error_message = "failed to replace identity file: " + std::to_string(GetLastError());
    return false;
#else
    std::error_code error;
    std::filesystem::rename(source, destination, error);
    if (!error) {
        return true;
    }
    error_message = "failed to replace identity file: " + error.message();
    return false;
#endif
}

void remove_file(const std::filesystem::path& path) {
    std::error_code error;
    std::filesystem::remove(path, error);
}

std::vector<std::filesystem::path> temporary_files(const std::filesystem::path& path) {
    std::vector<std::filesystem::path> files;
    std::error_code error;
    const auto directory = path.parent_path();
    const auto prefix = path.filename().string() + ".";
    for (std::filesystem::directory_iterator iterator(directory, error), end;
         !error && iterator != end;
         iterator.increment(error)) {
        if (!iterator->is_regular_file(error)) {
            error.clear();
            continue;
        }
        const auto name = iterator->path().filename().string();
        if (name.size() > prefix.size() + 4 &&
            name.compare(0, prefix.size(), prefix) == 0 &&
            iterator->path().extension() == ".new") {
            files.push_back(iterator->path());
        }
    }
    std::sort(files.begin(), files.end());
    return files;
}

bool recover_temporary(
    const std::filesystem::path& path,
    std::string& value,
    std::string& error_message) {
    const auto candidates = temporary_files(path);
    for (const auto& candidate : candidates) {
        std::string recovered;
        if (!read_valid_identifier(candidate, recovered)) {
            remove_file(candidate);
            continue;
        }
        if (!replace_file(candidate, path, error_message)) {
            return false;
        }
        value = std::move(recovered);
        for (const auto& leftover : candidates) {
            if (leftover != candidate) {
                remove_file(leftover);
            }
        }
        return true;
    }
    return false;
}

bool write_atomic(
    const std::filesystem::path& path,
    const std::string& value,
    std::string& error_message) {
    auto temporary = path;
    temporary += "." + uuid32() + ".new";
    {
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        if (!output) {
            error_message = "failed to create temporary identity file";
            return false;
        }
        output.write(value.data(), static_cast<std::streamsize>(value.size()));
        output.flush();
        if (!output) {
            error_message = "failed to write temporary identity file";
            output.close();
            remove_file(temporary);
            return false;
        }
    }
    if (!flush_file(temporary)) {
        error_message = "failed to flush temporary identity file";
        remove_file(temporary);
        return false;
    }
    if (!replace_file(temporary, path, error_message)) {
        remove_file(temporary);
        return false;
    }
    return true;
}

} // namespace

AnonymousUserIdResolution load_or_create_anonymous_user_id(
    const std::filesystem::path& cache_root,
    const std::string& rum_app_id) {
    const auto fallback = create_identifier();
    try {
        const auto path = anonymous_user_id_path(cache_root, rum_app_id);
        std::error_code directory_error;
        std::filesystem::create_directories(path.parent_path(), directory_error);
        if (directory_error) {
            return {fallback, "failed to create identity directory: " + directory_error.message()};
        }

        auto lock_path = path;
        lock_path += ".lock";
        IdentityLock identity_lock(lock_path);
        if (!identity_lock.acquired()) {
            return {fallback, identity_lock.error()};
        }

        std::string stored;
        if (read_valid_identifier(path, stored)) {
            return {stored, {}};
        }

        std::string persistence_error;
        if (recover_temporary(path, stored, persistence_error)) {
            return {stored, {}};
        }
        if (!persistence_error.empty()) {
            return {fallback, persistence_error};
        }
        if (!write_atomic(path, fallback, persistence_error)) {
            return {fallback, persistence_error};
        }
        return {fallback, {}};
    } catch (const std::exception& exception) {
        return {fallback, exception.what()};
    } catch (...) {
        return {fallback, "unknown anonymous identity persistence failure"};
    }
}

std::filesystem::path anonymous_user_id_path(
    const std::filesystem::path& cache_root,
    const std::string& rum_app_id) {
    return cache_root / "identity" / (application_scope(rum_app_id) + ".id");
}

bool is_valid_anonymous_user_id(const std::string& value) {
    const std::string prefix(kPrefix);
    if (value.size() != prefix.size() + kIdentifierLength ||
        value.compare(0, prefix.size(), prefix) != 0) {
        return false;
    }
    return std::all_of(
        value.begin() + static_cast<std::ptrdiff_t>(prefix.size()),
        value.end(),
        [](unsigned char character) {
            return (character >= '0' && character <= '9') ||
                   (character >= 'a' && character <= 'f');
        });
}

} // namespace guance::rum
