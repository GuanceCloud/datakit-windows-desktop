#include "queue_store.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <sstream>
#include <tuple>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/statvfs.h>
#include <unistd.h>
#endif

namespace guance::rum {

namespace {

constexpr std::size_t kHeaderSize = 256;
constexpr int32_t kFormatVersion = 1;
constexpr std::array<char, 8> kMagic{'G', 'C', 'B', 'A', 'T', 'C', 'H', '1'};

int64_t unix_time_milliseconds() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
}

uint32_t crc32_update(uint32_t value, const char* data, std::size_t size) {
    for (std::size_t index = 0; index < size; ++index) {
        value ^= static_cast<unsigned char>(data[index]);
        for (int bit = 0; bit < 8; ++bit) {
            value = (value >> 1U) ^ (0xEDB88320U & static_cast<uint32_t>(-
                static_cast<int32_t>(value & 1U)));
        }
    }
    return value;
}

uint32_t crc32(const std::string& value) {
    return crc32_update(0xFFFFFFFFU, value.data(), value.size()) ^ 0xFFFFFFFFU;
}

template <typename T>
void write_number(std::array<char, kHeaderSize>& header, std::size_t offset, T value) {
    std::memcpy(header.data() + offset, &value, sizeof(value));
}

template <typename T>
T read_number(const char* bytes, std::size_t offset) {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

std::array<char, kHeaderSize> make_header(
    QueueStreamKind stream,
    int64_t created_ms,
    int record_count,
    int64_t payload_bytes,
    uint32_t checksum) {
    std::array<char, kHeaderSize> header{};
    std::copy(kMagic.begin(), kMagic.end(), header.begin());
    write_number(header, 8, kFormatVersion);
    write_number(header, 12, static_cast<int32_t>(stream));
    write_number(header, 16, created_ms);
    write_number(header, 24, static_cast<int32_t>(record_count));
    write_number(header, 28, static_cast<int32_t>(0));
    write_number(header, 32, payload_bytes);
    write_number(header, 40, checksum);
    return header;
}

std::string read_file(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        return {};
    }
    return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}

bool is_ready_batch(const std::filesystem::path& path) {
    return path.extension() == ".batch" && path.parent_path().filename() == "ready";
}

QueueStreamKind stream_from_path(const std::filesystem::path& path) {
    for (auto current = path.parent_path(); !current.empty(); current = current.parent_path()) {
        const auto name = current.filename().string();
        if (name == "rum") return QueueStreamKind::rum;
        if (name == "logs") return QueueStreamKind::log;
        if (name == "replay") return QueueStreamKind::replay;
        if (current == current.root_path()) break;
    }
    return QueueStreamKind::rum;
}

bool flush_file_to_disk(const std::filesystem::path& path) {
#if defined(_WIN32)
    const auto handle = CreateFileW(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    const bool flushed = FlushFileBuffers(handle) != FALSE;
    CloseHandle(handle);
    return flushed;
#else
    const auto descriptor = ::open(path.c_str(), O_RDONLY);
    if (descriptor < 0) return false;
    const bool flushed = ::fsync(descriptor) == 0;
    ::close(descriptor);
    return flushed;
#endif
}

int64_t allocation_unit_for(const std::filesystem::path& path) {
#if defined(_WIN32)
    const auto root = std::filesystem::absolute(path).root_path();
    DWORD sectors_per_cluster = 0;
    DWORD bytes_per_sector = 0;
    DWORD free_clusters = 0;
    DWORD total_clusters = 0;
    if (!root.empty() && GetDiskFreeSpaceW(
            root.c_str(),
            &sectors_per_cluster,
            &bytes_per_sector,
            &free_clusters,
            &total_clusters)) {
        return std::max<int64_t>(
            1,
            static_cast<int64_t>(sectors_per_cluster) * bytes_per_sector);
    }
#else
    struct statvfs stats {};
    if (::statvfs(path.c_str(), &stats) == 0) {
        return std::max<int64_t>(1, static_cast<int64_t>(stats.f_frsize));
    }
#endif
    return 4096;
}

} // namespace

CacheQuota::CacheQuota(std::filesystem::path cache_root, int64_t max_bytes, int max_files)
    : cache_root_(std::move(cache_root)),
      max_bytes_(max_bytes > 0 ? max_bytes : 128LL * 1024 * 1024),
      max_files_(max_files > 0 ? max_files : 1024) {
    std::error_code ec;
    std::filesystem::create_directories(cache_root_, ec);
    allocation_unit_ = allocation_unit_for(cache_root_);
    std::lock_guard lock(mutex_);
    reconcile_locked();
}

bool CacheQuota::reserve(
    const std::filesystem::path& path,
    int64_t projected_length,
    QueueStreamKind stream,
    bool discard_new) {
    std::lock_guard lock(mutex_);
    const auto key = std::filesystem::absolute(path).string();
    const auto existing = allocations_.find(key);
    const auto previous = existing == allocations_.end() ? 0 : existing->second;
    const auto projected = estimate_allocation(projected_length);
    const auto delta = projected - previous;
    const bool new_file = existing == allocations_.end();
    if (delta <= 0) {
        allocations_[key] = projected;
        allocated_bytes_ += delta;
        return true;
    }

    if (!discard_new &&
        (allocated_bytes_ + delta > max_bytes_ ||
         (new_file && static_cast<int>(allocations_.size()) + 1 > max_files_))) {
        evict_locked(delta, new_file, stream);
    }
    if (allocated_bytes_ + delta > max_bytes_ ||
        (new_file && static_cast<int>(allocations_.size()) + 1 > max_files_)) {
        return false;
    }
    allocations_[key] = projected;
    allocated_bytes_ += delta;
    return true;
}

void CacheQuota::move(const std::filesystem::path& source, const std::filesystem::path& destination) {
    std::lock_guard lock(mutex_);
    const auto source_key = std::filesystem::absolute(source).string();
    const auto destination_key = std::filesystem::absolute(destination).string();
    const auto existing = allocations_.find(source_key);
    if (existing != allocations_.end()) {
        allocations_[destination_key] = existing->second;
        allocations_.erase(existing);
    } else {
        std::error_code ec;
        const auto size = std::filesystem::file_size(destination, ec);
        if (!ec) {
            const auto allocation = estimate_allocation(static_cast<int64_t>(size));
            allocations_[destination_key] = allocation;
            allocated_bytes_ += allocation;
        }
    }
}

void CacheQuota::release(const std::filesystem::path& path) {
    std::lock_guard lock(mutex_);
    const auto key = std::filesystem::absolute(path).string();
    const auto existing = allocations_.find(key);
    if (existing != allocations_.end()) {
        allocated_bytes_ -= existing->second;
        allocations_.erase(existing);
    }
}

void CacheQuota::trim() {
    std::lock_guard lock(mutex_);
    if (allocated_bytes_ > max_bytes_ || static_cast<int>(allocations_.size()) > max_files_) {
        evict_locked(0, false, QueueStreamKind::rum);
    }
}

int64_t CacheQuota::allocated_bytes() const {
    std::lock_guard lock(mutex_);
    return std::max<int64_t>(0, allocated_bytes_);
}

int CacheQuota::file_count() const {
    std::lock_guard lock(mutex_);
    return static_cast<int>(allocations_.size());
}

int64_t CacheQuota::estimate_allocation(int64_t length) const {
    return length <= 0
        ? 0
        : ((length + allocation_unit_ - 1) / allocation_unit_) * allocation_unit_;
}

void CacheQuota::reconcile_locked() {
    allocations_.clear();
    allocated_bytes_ = 0;
    std::error_code ec;
    for (std::filesystem::recursive_directory_iterator iterator(cache_root_, ec), end;
         !ec && iterator != end;
         iterator.increment(ec)) {
        if (!iterator->is_regular_file(ec)) continue;
        const auto extension = iterator->path().extension();
        if (extension != ".batch" && extension != ".tmp") continue;
        const auto allocation = estimate_allocation(static_cast<int64_t>(iterator->file_size(ec)));
        if (ec) {
            ec.clear();
            continue;
        }
        allocations_[std::filesystem::absolute(iterator->path()).string()] = allocation;
        allocated_bytes_ += allocation;
    }
}

void CacheQuota::evict_locked(int64_t required_bytes, bool requires_file, QueueStreamKind requesting_stream) {
    struct Candidate {
        std::string key;
        std::filesystem::path path;
        int64_t bytes = 0;
        std::filesystem::file_time_type modified{};
        bool requesting_stream = false;
    };
    std::vector<Candidate> candidates;
    std::error_code ec;
    for (const auto& item : allocations_) {
        const std::filesystem::path path(item.first);
        if (!is_ready_batch(path)) continue;
        candidates.push_back({
            item.first,
            path,
            item.second,
            std::filesystem::last_write_time(path, ec),
            stream_from_path(path) == requesting_stream});
        ec.clear();
    }
    std::sort(candidates.begin(), candidates.end(), [](const Candidate& left, const Candidate& right) {
        if (left.requesting_stream != right.requesting_stream) return left.requesting_stream > right.requesting_stream;
        return left.modified < right.modified;
    });
    const auto target_bytes = std::max<int64_t>(0, static_cast<int64_t>(max_bytes_ * 0.9) - required_bytes);
    const auto target_files = requires_file ? max_files_ - 1 : max_files_;
    for (const auto& candidate : candidates) {
        if (allocated_bytes_ <= target_bytes && static_cast<int>(allocations_.size()) <= target_files) break;
        std::filesystem::remove(candidate.path, ec);
        if (!ec) {
            allocated_bytes_ -= candidate.bytes;
            allocations_.erase(candidate.key);
        }
        ec.clear();
    }
}

QueueStore::QueueStore(
    std::filesystem::path cache_root,
    std::string stream_name,
    QueueStreamKind stream_kind,
    std::shared_ptr<CacheQuota> quota,
    int max_batch_items,
    int64_t max_batch_bytes,
    int64_t max_cache_age_seconds,
    bool discard_new,
    bool single_item_batch)
    : stream_kind_(stream_kind),
      quota_(std::move(quota)),
      max_batch_items_(max_batch_items > 0 ? max_batch_items : 50),
      max_batch_bytes_(max_batch_bytes > 0 ? max_batch_bytes : 512LL * 1024),
      max_cache_age_seconds_(max_cache_age_seconds > 0 ? max_cache_age_seconds : 7LL * 24 * 60 * 60),
      discard_new_(discard_new),
      single_item_batch_(single_item_batch) {
    const auto stream_root = std::move(cache_root) / std::move(stream_name);
    active_directory_ = stream_root / "active";
    ready_directory_ = stream_root / "ready";
    sending_directory_ = stream_root / "sending";
    std::error_code ec;
    std::filesystem::create_directories(active_directory_, ec);
    std::filesystem::create_directories(ready_directory_, ec);
    std::filesystem::create_directories(sending_directory_, ec);
    std::lock_guard lock(mutex_);
    recover_locked();
}

QueueStore::~QueueStore() {
    std::lock_guard lock(mutex_);
    seal_locked();
}

bool QueueStore::enqueue(const std::string& line) {
    if (line.empty() || static_cast<int64_t>(line.size()) > max_batch_bytes_) return false;
    std::lock_guard lock(mutex_);
    if (single_item_batch_) return write_single_batch_locked(line);

    if (active_record_count_ > 0 &&
        (active_record_count_ >= max_batch_items_ || active_payload_bytes_ + static_cast<int64_t>(line.size()) > max_batch_bytes_)) {
        seal_locked();
    }
    const bool stored = active_stream_.is_open()
        ? append_active_locked(line)
        : create_active_locked(line);
    if (!stored) return false;
    if (active_record_count_ >= max_batch_items_ || active_payload_bytes_ >= max_batch_bytes_) {
        seal_locked();
    }
    return true;
}

bool QueueStore::seal() {
    std::lock_guard lock(mutex_);
    return seal_locked();
}

QueuedBatch QueueStore::acquire() {
    std::lock_guard lock(mutex_);
    std::vector<std::filesystem::path> files;
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(ready_directory_, ec)) {
        if (!ec && entry.is_regular_file() && entry.path().extension() == ".batch") files.push_back(entry.path());
    }
    std::sort(files.begin(), files.end());
    for (const auto& source : files) {
        const auto modified = std::filesystem::last_write_time(source, ec);
        if (!ec && std::filesystem::file_time_type::clock::now() - modified >
                std::chrono::seconds(max_cache_age_seconds_)) {
            delete_locked(source);
            continue;
        }
        ec.clear();
        const auto leased = sending_directory_ / source.filename();
        std::filesystem::rename(source, leased, ec);
        if (ec) {
            ec.clear();
            continue;
        }
        quota_->move(source, leased);
        auto batch = read_batch_locked(leased);
        if (batch) return batch;
        delete_locked(leased);
    }
    return {};
}

void QueueStore::complete(const std::string& lease_id) {
    std::lock_guard lock(mutex_);
    const std::filesystem::path path(lease_id);
    if (valid_lease_path(path)) delete_locked(path);
}

void QueueStore::abandon(const std::string& lease_id) {
    std::lock_guard lock(mutex_);
    const std::filesystem::path source(lease_id);
    if (!valid_lease_path(source) || !std::filesystem::exists(source)) return;
    auto destination = ready_directory_ / source.filename();
    if (std::filesystem::exists(destination)) destination = unique_path(ready_directory_, ".batch");
    std::error_code ec;
    std::filesystem::rename(source, destination, ec);
    if (!ec) quota_->move(source, destination);
}

void QueueStore::set_discard_new(bool discard_new) {
    std::lock_guard lock(mutex_);
    discard_new_ = discard_new;
}

bool QueueStore::create_active_locked(const std::string& first_line) {
    active_created_ms_ = unix_time_milliseconds();
    active_path_ = unique_path(active_directory_, ".tmp");
    const auto projected = static_cast<int64_t>(kHeaderSize + first_line.size());
    if (!quota_->reserve(active_path_, projected, stream_kind_, discard_new_)) {
        reset_active_locked();
        return false;
    }
    active_stream_.open(active_path_, std::ios::binary | std::ios::in | std::ios::out | std::ios::trunc);
    if (!active_stream_) {
        quota_->release(active_path_);
        reset_active_locked();
        return false;
    }
    const auto header = make_header(stream_kind_, active_created_ms_, 0, 0, 0);
    active_stream_.write(header.data(), static_cast<std::streamsize>(header.size()));
    active_checksum_ = 0xFFFFFFFFU;
    return append_active_locked(first_line);
}

bool QueueStore::append_active_locked(const std::string& line) {
    const auto previous = static_cast<int64_t>(kHeaderSize) + active_payload_bytes_;
    const auto projected = previous + static_cast<int64_t>(line.size());
    if (!quota_->reserve(active_path_, projected, stream_kind_, discard_new_)) return false;
    active_stream_.seekp(0, std::ios::end);
    active_stream_.write(line.data(), static_cast<std::streamsize>(line.size()));
    active_stream_.flush();
    if (!active_stream_) {
        active_stream_.clear();
        active_stream_.close();
        std::error_code resize_error;
        std::filesystem::resize_file(active_path_, previous, resize_error);
        if (!resize_error) {
            active_stream_.open(active_path_, std::ios::binary | std::ios::in | std::ios::out);
        }
        quota_->reserve(active_path_, previous, stream_kind_, true);
        return false;
    }
    active_record_count_++;
    active_payload_bytes_ += static_cast<int64_t>(line.size());
    active_checksum_ = crc32_update(active_checksum_, line.data(), line.size());
    return true;
}

bool QueueStore::seal_locked() {
    if (!active_stream_.is_open() || active_record_count_ <= 0) return false;
    const auto source = active_path_;
    const auto destination = ready_directory_ / (source.stem().string() + ".batch");
    const auto header = make_header(
        stream_kind_,
        active_created_ms_,
        active_record_count_,
        active_payload_bytes_,
        active_checksum_ ^ 0xFFFFFFFFU);
    active_stream_.seekp(0, std::ios::beg);
    active_stream_.write(header.data(), static_cast<std::streamsize>(header.size()));
    active_stream_.flush();
    active_stream_.close();
    if (!flush_file_to_disk(source)) {
        reset_active_locked();
        return false;
    }
    std::error_code ec;
    std::filesystem::rename(source, destination, ec);
    if (!ec) quota_->move(source, destination);
    reset_active_locked();
    return !ec;
}

bool QueueStore::write_single_batch_locked(const std::string& payload) {
    const auto created = unix_time_milliseconds();
    const auto temporary = unique_path(active_directory_, ".tmp");
    const auto destination = ready_directory_ / (temporary.stem().string() + ".batch");
    const auto total = static_cast<int64_t>(kHeaderSize + payload.size());
    if (!quota_->reserve(temporary, total, stream_kind_, discard_new_)) return false;
    std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
    const auto header = make_header(stream_kind_, created, 1, static_cast<int64_t>(payload.size()), crc32(payload));
    output.write(header.data(), static_cast<std::streamsize>(header.size()));
    output.write(payload.data(), static_cast<std::streamsize>(payload.size()));
    output.flush();
    output.close();
    if (!output || !flush_file_to_disk(temporary)) {
        delete_locked(temporary);
        return false;
    }
    std::error_code ec;
    std::filesystem::rename(temporary, destination, ec);
    if (ec) {
        delete_locked(temporary);
        return false;
    }
    quota_->move(temporary, destination);
    return true;
}

void QueueStore::recover_locked() {
    recover_sending_locked();
    recover_active_locked();
}

void QueueStore::recover_sending_locked() {
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(sending_directory_, ec)) {
        if (ec || !entry.is_regular_file() || entry.path().extension() != ".batch") continue;
        auto destination = ready_directory_ / entry.path().filename();
        if (std::filesystem::exists(destination)) destination = unique_path(ready_directory_, ".batch");
        std::filesystem::rename(entry.path(), destination, ec);
        if (!ec) quota_->move(entry.path(), destination);
        ec.clear();
    }
}

void QueueStore::recover_active_locked() {
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(active_directory_, ec)) {
        if (ec || !entry.is_regular_file() || entry.path().extension() != ".tmp") continue;
        auto batch = read_batch_locked(entry.path());
        if (batch) {
            auto destination = ready_directory_ / (entry.path().stem().string() + ".batch");
            std::filesystem::rename(entry.path(), destination, ec);
            if (!ec) quota_->move(entry.path(), destination);
            ec.clear();
            continue;
        }
        if (!single_item_batch_ && recover_line_batch_locked(entry.path())) continue;
        delete_locked(entry.path());
    }
}

bool QueueStore::recover_line_batch_locked(const std::filesystem::path& path) {
    std::error_code size_error;
    const auto file_length = std::filesystem::file_size(path, size_error);
    if (size_error || file_length > kHeaderSize + static_cast<uint64_t>(max_batch_bytes_)) return false;
    const auto bytes = read_file(path);
    if (bytes.size() <= kHeaderSize) return false;
    const auto newline = bytes.find_last_of('\n');
    if (newline == std::string::npos || newline < kHeaderSize) return false;
    const auto payload = bytes.substr(kHeaderSize, newline - kHeaderSize + 1);
    const auto record_count = static_cast<int>(std::count(payload.begin(), payload.end(), '\n'));
    const auto header = make_header(
        stream_kind_,
        unix_time_milliseconds(),
        record_count,
        static_cast<int64_t>(payload.size()),
        crc32(payload));
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(header.data(), static_cast<std::streamsize>(header.size()));
    output.write(payload.data(), static_cast<std::streamsize>(payload.size()));
    output.close();
    if (!output || !flush_file_to_disk(path)) return false;
    quota_->reserve(path, static_cast<int64_t>(kHeaderSize + payload.size()), stream_kind_, discard_new_);
    const auto destination = ready_directory_ / (path.stem().string() + ".batch");
    std::error_code ec;
    std::filesystem::rename(path, destination, ec);
    if (!ec) quota_->move(path, destination);
    return !ec;
}

QueuedBatch QueueStore::read_batch_locked(const std::filesystem::path& path) const {
    std::error_code size_error;
    const auto file_length = std::filesystem::file_size(path, size_error);
    if (size_error || file_length <= kHeaderSize ||
        file_length > kHeaderSize + static_cast<uint64_t>(max_batch_bytes_)) return {};
    const auto bytes = read_file(path);
    if (bytes.size() <= kHeaderSize || !std::equal(kMagic.begin(), kMagic.end(), bytes.begin())) return {};
    const auto version = read_number<int32_t>(bytes.data(), 8);
    const auto stream = read_number<int32_t>(bytes.data(), 12);
    const auto record_count = read_number<int32_t>(bytes.data(), 24);
    const auto payload_length = read_number<int64_t>(bytes.data(), 32);
    const auto checksum = read_number<uint32_t>(bytes.data(), 40);
    if (version != kFormatVersion || stream != static_cast<int32_t>(stream_kind_) ||
        record_count <= 0 || payload_length <= 0 ||
        static_cast<std::size_t>(payload_length) != bytes.size() - kHeaderSize) return {};
    const auto payload = bytes.substr(kHeaderSize);
    if (crc32(payload) != checksum) return {};
    QueuedBatch batch;
    batch.lease_id = std::filesystem::absolute(path).string();
    batch.payload_bytes = payload_length;
    if (single_item_batch_) {
        batch.lines.push_back(payload);
        return batch;
    }
    std::size_t start = 0;
    while (start < payload.size()) {
        const auto end = payload.find('\n', start);
        if (end == std::string::npos) return {};
        batch.lines.push_back(payload.substr(start, end - start + 1));
        start = end + 1;
    }
    return static_cast<int>(batch.lines.size()) == record_count ? batch : QueuedBatch{};
}

void QueueStore::delete_locked(const std::filesystem::path& path) {
    std::error_code ec;
    std::filesystem::remove(path, ec);
    if (!std::filesystem::exists(path, ec)) quota_->release(path);
}

std::filesystem::path QueueStore::unique_path(const std::filesystem::path& directory, const char* extension) const {
    static std::atomic<uint64_t> sequence{0};
    std::ostringstream name;
    name << unix_time_milliseconds() << '-' << ++sequence << extension;
    return directory / name.str();
}

bool QueueStore::valid_lease_path(const std::filesystem::path& path) const {
    std::error_code ec;
    return std::filesystem::equivalent(path.parent_path(), sending_directory_, ec) && !ec;
}

void QueueStore::reset_active_locked() {
    if (active_stream_.is_open()) active_stream_.close();
    active_path_.clear();
    active_record_count_ = 0;
    active_payload_bytes_ = 0;
    active_created_ms_ = 0;
    active_checksum_ = 0;
}

std::string default_queue_path() {
#if defined(_WIN32)
    const char* local_app_data = std::getenv("LOCALAPPDATA");
    const std::string root = local_app_data == nullptr ? "." : local_app_data;
    return root + "\\Guance\\Rum\\native\\v1";
#else
    const char* home = std::getenv("HOME");
    const std::string root = home == nullptr ? "/tmp" : home;
    return root + "/.guance/rum/native/v1";
#endif
}

} // namespace guance::rum
