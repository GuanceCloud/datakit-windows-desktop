#pragma once

#include <cstdint>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace guance::rum {

enum class QueueStreamKind : int32_t {
    rum = 1,
    log = 2,
    replay = 3,
};

struct QueuedBatch {
    std::string lease_id;
    std::vector<std::string> lines;
    int64_t payload_bytes = 0;

    explicit operator bool() const noexcept {
        return !lease_id.empty();
    }
};

class CacheQuota {
public:
    CacheQuota(std::filesystem::path cache_root, int64_t max_bytes, int max_files);

    bool reserve(
        const std::filesystem::path& path,
        int64_t projected_length,
        QueueStreamKind stream,
        bool discard_new);
    void move(const std::filesystem::path& source, const std::filesystem::path& destination);
    void release(const std::filesystem::path& path);
    void trim();
    int64_t allocated_bytes() const;
    int file_count() const;

private:
    int64_t estimate_allocation(int64_t length) const;
    void reconcile_locked();
    void evict_locked(int64_t required_bytes, bool requires_file, QueueStreamKind requesting_stream);

    std::filesystem::path cache_root_;
    int64_t max_bytes_ = 128LL * 1024 * 1024;
    int max_files_ = 1024;
    int64_t allocation_unit_ = 4096;
    int64_t allocated_bytes_ = 0;
    std::unordered_map<std::string, int64_t> allocations_;
    mutable std::mutex mutex_;
};

class QueueStore {
public:
    QueueStore(
        std::filesystem::path cache_root,
        std::string stream_name,
        QueueStreamKind stream_kind,
        std::shared_ptr<CacheQuota> quota,
        int max_batch_items,
        int64_t max_batch_bytes,
        int64_t max_cache_age_seconds,
        bool discard_new = false,
        bool single_item_batch = false);
    ~QueueStore();

    bool enqueue(const std::string& line);
    bool seal();
    QueuedBatch acquire();
    void complete(const std::string& lease_id);
    void abandon(const std::string& lease_id);
    void set_discard_new(bool discard_new);

private:
    bool create_active_locked(const std::string& first_line);
    bool append_active_locked(const std::string& line);
    bool seal_locked();
    bool write_single_batch_locked(const std::string& payload);
    void recover_locked();
    void recover_sending_locked();
    void recover_active_locked();
    bool recover_line_batch_locked(const std::filesystem::path& path);
    QueuedBatch read_batch_locked(const std::filesystem::path& path) const;
    void delete_locked(const std::filesystem::path& path);
    std::filesystem::path unique_path(const std::filesystem::path& directory, const char* extension) const;
    bool valid_lease_path(const std::filesystem::path& path) const;
    void reset_active_locked();

    QueueStreamKind stream_kind_ = QueueStreamKind::rum;
    std::shared_ptr<CacheQuota> quota_;
    int max_batch_items_ = 50;
    int64_t max_batch_bytes_ = 512LL * 1024;
    int64_t max_cache_age_seconds_ = 7LL * 24 * 60 * 60;
    bool discard_new_ = false;
    bool single_item_batch_ = false;
    std::filesystem::path active_directory_;
    std::filesystem::path ready_directory_;
    std::filesystem::path sending_directory_;
    std::filesystem::path active_path_;
    std::fstream active_stream_;
    int active_record_count_ = 0;
    int64_t active_payload_bytes_ = 0;
    int64_t active_created_ms_ = 0;
    uint32_t active_checksum_ = 0;
    std::mutex mutex_;
};

std::string default_queue_path();

} // namespace guance::rum
