#pragma once

#include <cstdint>
#include <mutex>
#include <filesystem>
#include <string>
#include <vector>

#if defined(GUANCE_RUM_HAS_SQLITE)
struct sqlite3;
#endif

namespace guance::rum {

struct QueuedLine {
    int64_t id = 0;
    std::string line;
};

class QueueStore {
public:
    explicit QueueStore(std::string database_path, int max_items, int64_t max_bytes);
    ~QueueStore();

    bool enqueue(const std::string& line);
    std::vector<QueuedLine> peek(int limit);
    void remove(const std::vector<int64_t>& ids);
    void trim();

private:
    void open();
    bool fallback_enqueue(const std::string& line);
    std::filesystem::path fallback_directory() const;
    std::vector<QueuedLine> fallback_peek(int limit) const;
    void fallback_remove(const std::vector<int64_t>& ids);
    void fallback_trim();
    void fallback_recover();
    int64_t memory_size_bytes() const;

    std::string database_path_;
    int max_items_ = 100000;
    int64_t max_bytes_ = 64LL * 1024 * 1024;
    int64_t next_memory_id_ = 0;
    std::vector<QueuedLine> memory_lines_;
    std::mutex mutex_;

#if defined(GUANCE_RUM_HAS_SQLITE)
    ::sqlite3* db_ = nullptr;
#endif
};

std::string default_queue_path();

} // namespace guance::rum
