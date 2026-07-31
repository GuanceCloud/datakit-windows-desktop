#include "crash_envelope.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <fstream>
#include <set>
#include <string>
#include <system_error>
#include <vector>

namespace guance::rum {

namespace {

uint32_t crc32(const uint8_t* data, std::size_t size) noexcept {
    uint32_t crc = 0xFFFFFFFFu;
    for (std::size_t index = 0; index < size; index++) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; bit++) {
            const uint32_t mask = 0u - (crc & 1u);
            crc = (crc >> 1u) ^ (0xEDB88320u & mask);
        }
    }
    return ~crc;
}

uint32_t envelope_checksum(CrashEnvelope envelope) noexcept {
    envelope.checksum = 0;
    return crc32(reinterpret_cast<const uint8_t*>(&envelope), sizeof(envelope));
}

bool is_envelope_file(const std::filesystem::path& path) {
    return path.extension() == ".envelope";
}

} // namespace

CrashEnvelope finalize_crash_envelope(CrashEnvelope envelope) noexcept {
    envelope.magic = kCrashEnvelopeMagic;
    envelope.version = kCrashEnvelopeVersion;
    envelope.struct_size = static_cast<uint16_t>(sizeof(CrashEnvelope));
    envelope.completed = kCrashEnvelopeComplete;
    envelope.checksum = envelope_checksum(envelope);
    return envelope;
}

bool validate_crash_envelope(const CrashEnvelope& envelope) noexcept {
    return envelope.magic == kCrashEnvelopeMagic &&
           envelope.version == kCrashEnvelopeVersion &&
           envelope.struct_size == sizeof(CrashEnvelope) &&
           envelope.completed == kCrashEnvelopeComplete &&
           envelope.checksum == envelope_checksum(envelope);
}

CrashEnvelopeStore::CrashEnvelopeStore(
    std::filesystem::path directory,
    int max_files,
    int64_t max_bytes)
    : directory_(std::move(directory)),
      max_files_(max_files > 0 ? max_files : 3),
      max_bytes_(max_bytes > 0 ? max_bytes : 32LL * 1024 * 1024) {
    std::error_code ec;
    std::filesystem::create_directories(directory_, ec);
}

CrashRecoveryResult CrashEnvelopeStore::recover(const RecoveryCallback& callback) {
    CrashRecoveryResult result;
    std::error_code ec;
    if (!std::filesystem::exists(directory_, ec)) {
        return result;
    }

    std::vector<std::filesystem::path> paths;
    for (const auto& entry : std::filesystem::directory_iterator(directory_, ec)) {
        if (ec) {
            break;
        }
        if (entry.is_regular_file(ec) && is_envelope_file(entry.path())) {
            paths.push_back(entry.path());
        }
    }
    std::sort(paths.begin(), paths.end());

    for (const auto& path : paths) {
        CrashEnvelope envelope{};
        const auto size = std::filesystem::file_size(path, ec);
        if (ec || size != sizeof(CrashEnvelope)) {
            if (quarantine(path)) {
                result.quarantined++;
            } else {
                result.deferred++;
            }
            ec.clear();
            continue;
        }

        std::ifstream input(path, std::ios::binary);
        input.read(reinterpret_cast<char*>(&envelope), sizeof(envelope));
        if (!input || !validate_crash_envelope(envelope)) {
            input.close();
            if (quarantine(path)) {
                result.quarantined++;
            } else {
                result.deferred++;
            }
            continue;
        }
        input.close();

        bool consumed = false;
        try {
            consumed = callback && callback(envelope, minidump_path(envelope));
        } catch (...) {
            consumed = false;
        }
        if (!consumed) {
            result.deferred++;
            continue;
        }

        std::filesystem::remove(path, ec);
        if (!ec) {
            result.recovered++;
        } else {
            result.deferred++;
            ec.clear();
        }
    }
    return result;
}

bool CrashEnvelopeStore::trim(
    int reserve_files,
    int64_t reserve_bytes,
    int64_t* remaining_bytes) {
    struct StoredFile {
        std::filesystem::path path;
        std::filesystem::file_time_type modified;
        int64_t size = 0;
    };

    std::error_code ec;
    std::vector<StoredFile> files;
    int64_t total_bytes = 0;
    for (const auto& entry : std::filesystem::recursive_directory_iterator(directory_, ec)) {
        if (ec) {
            return false;
        }
        if (!entry.is_regular_file(ec)) {
            continue;
        }
        const auto extension = entry.path().extension();
        const bool top_level_crash_file = entry.path().parent_path() == directory_ &&
            (extension == ".envelope" || extension == ".dmp");
        const bool quarantined_file = entry.path().parent_path() == directory_ / "bad";
        if (!top_level_crash_file && !quarantined_file) {
            continue;
        }
        const auto size = static_cast<int64_t>(entry.file_size(ec));
        if (ec) {
            ec.clear();
            continue;
        }
        files.push_back({entry.path(), entry.last_write_time(ec), size});
        total_bytes += size;
    }

    std::sort(files.begin(), files.end(), [](const StoredFile& left, const StoredFile& right) {
        return left.modified < right.modified;
    });

    std::set<std::filesystem::path> protected_files;
    for (const auto& file : files) {
        if (file.path.parent_path() != directory_ || file.path.extension() != ".envelope" ||
            file.size != sizeof(CrashEnvelope)) {
            continue;
        }
        CrashEnvelope envelope{};
        std::ifstream input(file.path, std::ios::binary);
        input.read(reinterpret_cast<char*>(&envelope), sizeof(envelope));
        if (!input || !validate_crash_envelope(envelope)) {
            continue;
        }
        // Durable, unconsumed metadata is never discarded. Its optional Dump is
        // expendable so repeated queue failures cannot grow the cache forever.
        protected_files.insert(file.path);
    }

    const auto allowed_files = static_cast<std::size_t>(
        std::max(0, max_files_ - std::max(0, reserve_files)));
    std::size_t remaining = files.size();
    std::set<std::filesystem::path> removed;
    for (const auto& file : files) {
        if (remaining <= allowed_files &&
            total_bytes <= max_bytes_ - std::max<int64_t>(0, reserve_bytes)) {
            break;
        }
        if (removed.find(file.path) != removed.end()) {
            continue;
        }

        std::vector<std::filesystem::path> group{file.path};
        if (file.path.parent_path() == directory_ &&
            (file.path.extension() == ".envelope" || file.path.extension() == ".dmp")) {
            const auto stem = file.path.parent_path() / file.path.stem();
            group = {stem.string() + ".envelope", stem.string() + ".dmp"};
            const auto envelope = std::filesystem::path(stem.string() + ".envelope");
            if (file.path.extension() == ".dmp" &&
                protected_files.find(envelope) != protected_files.end()) {
                group = {file.path};
            }
        }
        if (std::any_of(group.begin(), group.end(), [&](const auto& candidate) {
                return protected_files.find(candidate) != protected_files.end();
            })) {
            continue;
        }
        for (const auto& candidate : group) {
            const auto found = std::find_if(
                files.begin(), files.end(),
                [&](const StoredFile& stored) { return stored.path == candidate; });
            if (found == files.end() || removed.find(candidate) != removed.end()) {
                continue;
            }
            ec.clear();
            std::filesystem::remove(candidate, ec);
            if (!ec) {
                removed.insert(candidate);
                total_bytes -= found->size;
                remaining--;
            }
        }
    }

    const auto required_bytes = std::max<int64_t>(0, reserve_bytes);
    if (remaining_bytes != nullptr) {
        *remaining_bytes = std::max<int64_t>(0, max_bytes_ - total_bytes - required_bytes);
    }
    return remaining + static_cast<std::size_t>(std::max(0, reserve_files)) <=
               static_cast<std::size_t>(max_files_) &&
           total_bytes <= max_bytes_ - required_bytes;
}

const std::filesystem::path& CrashEnvelopeStore::directory() const noexcept {
    return directory_;
}

bool CrashEnvelopeStore::quarantine(const std::filesystem::path& path) {
    std::error_code ec;
    const auto bad_directory = directory_ / "bad";
    std::filesystem::create_directories(bad_directory, ec);
    if (ec) {
        return false;
    }

    const auto base_name = path.filename().string();
    for (uint32_t suffix = 0; suffix < 1'000; suffix++) {
        const auto target = bad_directory /
            (base_name + (suffix == 0 ? std::string{} : "." + std::to_string(suffix)) + ".bad");
        ec.clear();
        if (std::filesystem::exists(target, ec) || ec) {
            continue;
        }
        std::filesystem::rename(path, target, ec);
        if (!ec) {
            return true;
        }
    }
    return false;
}

std::filesystem::path CrashEnvelopeStore::minidump_path(const CrashEnvelope& envelope) const {
    const auto length = std::find(
                            std::begin(envelope.dump_file_name),
                            std::end(envelope.dump_file_name),
                            '\0') -
                        std::begin(envelope.dump_file_name);
    if (length == 0) {
        return {};
    }
    const std::string name(envelope.dump_file_name, static_cast<std::size_t>(length));
    const std::filesystem::path relative(name);
    if (relative.is_absolute() || relative.filename() != relative) {
        return {};
    }
    const auto path = directory_ / relative;
    std::error_code ec;
    if (!std::filesystem::is_regular_file(path, ec) || ec ||
        std::filesystem::file_size(path, ec) == 0 || ec) {
        return {};
    }
    return path;
}

} // namespace guance::rum
