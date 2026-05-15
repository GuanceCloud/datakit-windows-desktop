#include "queue_store.h"

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <tuple>

#if defined(GUANCE_RUM_HAS_SQLITE)
#include <sqlite3.h>
#endif

namespace guance::rum {

namespace {

#if defined(GUANCE_RUM_HAS_SQLITE)
void exec_sql(sqlite3* db, const char* sql) {
    char* error = nullptr;
    sqlite3_exec(db, sql, nullptr, nullptr, &error);
    if (error != nullptr) {
        sqlite3_free(error);
    }
}
#endif

int64_t parse_queue_id(const std::filesystem::path& path) {
    const auto stem = path.stem().string();
    try {
        std::size_t parsed = 0;
        const auto id = std::stoll(stem, &parsed);
        return parsed == stem.size() ? id : 0;
    } catch (...) {
        return 0;
    }
}

std::string read_file(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    std::ostringstream output;
    output << input.rdbuf();
    return output.str();
}

void quarantine_file(const std::filesystem::path& path) {
    std::error_code ec;
    const auto bad_dir = path.parent_path() / "bad";
    std::filesystem::create_directories(bad_dir, ec);
    auto target = bad_dir / (path.filename().string() + ".bad");
    if (std::filesystem::exists(target, ec)) {
        target = bad_dir / (path.filename().string() + "." + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".bad");
    }
    std::filesystem::rename(path, target, ec);
    if (ec) {
        std::filesystem::remove(path, ec);
    }
}

std::string queue_file_name(int64_t id) {
    std::ostringstream ss;
    ss << std::setw(20) << std::setfill('0') << id << ".q";
    return ss.str();
}

} // namespace

QueueStore::QueueStore(std::string database_path, int max_items, int64_t max_bytes)
    : database_path_(std::move(database_path)),
      max_items_(max_items > 0 ? max_items : 100000),
      max_bytes_(max_bytes > 0 ? max_bytes : 64LL * 1024 * 1024) {
    open();
}

QueueStore::~QueueStore() {
#if defined(GUANCE_RUM_HAS_SQLITE)
    if (db_ != nullptr) {
        sqlite3_close(db_);
        db_ = nullptr;
    }
#endif
}

void QueueStore::enqueue(const std::string& line) {
    std::lock_guard lock(mutex_);
#if defined(GUANCE_RUM_HAS_SQLITE)
    if (db_ != nullptr) {
        sqlite3_stmt* stmt = nullptr;
        if (sqlite3_prepare_v2(db_, "INSERT INTO rum_queue(line, created_at_unix_ms) VALUES (?1, strftime('%s','now') * 1000)", -1, &stmt, nullptr) == SQLITE_OK) {
            sqlite3_bind_text(stmt, 1, line.c_str(), static_cast<int>(line.size()), SQLITE_TRANSIENT);
            sqlite3_step(stmt);
        }
        sqlite3_finalize(stmt);
        trim();
        return;
    }
#endif
    fallback_enqueue(line);
}

std::vector<QueuedLine> QueueStore::peek(int limit) {
    std::lock_guard lock(mutex_);
    limit = std::max(1, limit);
#if defined(GUANCE_RUM_HAS_SQLITE)
    if (db_ != nullptr) {
        std::vector<QueuedLine> result;
        sqlite3_stmt* stmt = nullptr;
        if (sqlite3_prepare_v2(db_, "SELECT id, line FROM rum_queue ORDER BY id ASC LIMIT ?1", -1, &stmt, nullptr) == SQLITE_OK) {
            sqlite3_bind_int(stmt, 1, limit);
            while (sqlite3_step(stmt) == SQLITE_ROW) {
                const auto* text = reinterpret_cast<const char*>(sqlite3_column_text(stmt, 1));
                result.push_back({sqlite3_column_int64(stmt, 0), text == nullptr ? std::string{} : std::string(text)});
            }
        }
        sqlite3_finalize(stmt);
        return result;
    }
#endif
    return fallback_peek(limit);
}

void QueueStore::remove(const std::vector<int64_t>& ids) {
    if (ids.empty()) {
        return;
    }
    std::lock_guard lock(mutex_);
#if defined(GUANCE_RUM_HAS_SQLITE)
    if (db_ != nullptr) {
        sqlite3_stmt* stmt = nullptr;
        if (sqlite3_prepare_v2(db_, "DELETE FROM rum_queue WHERE id = ?1", -1, &stmt, nullptr) == SQLITE_OK) {
            for (const auto id : ids) {
                sqlite3_reset(stmt);
                sqlite3_bind_int64(stmt, 1, id);
                sqlite3_step(stmt);
            }
        }
        sqlite3_finalize(stmt);
        return;
    }
#endif
    fallback_remove(ids);
}

void QueueStore::trim() {
#if defined(GUANCE_RUM_HAS_SQLITE)
    if (db_ != nullptr) {
        sqlite3_stmt* stmt = nullptr;
        if (sqlite3_prepare_v2(db_, "DELETE FROM rum_queue WHERE id IN (SELECT id FROM rum_queue ORDER BY id ASC LIMIT (SELECT MAX(0, COUNT(*) - ?1) FROM rum_queue))", -1, &stmt, nullptr) == SQLITE_OK) {
            sqlite3_bind_int(stmt, 1, max_items_);
            sqlite3_step(stmt);
        }
        sqlite3_finalize(stmt);
        std::error_code ec;
        if (std::filesystem::exists(database_path_, ec) && static_cast<int64_t>(std::filesystem::file_size(database_path_, ec)) > max_bytes_) {
            sqlite3_stmt* bytes_stmt = nullptr;
            if (sqlite3_prepare_v2(db_, "DELETE FROM rum_queue WHERE id IN (SELECT id FROM rum_queue ORDER BY id ASC LIMIT (SELECT MAX(1, COUNT(*) / 10) FROM rum_queue))", -1, &bytes_stmt, nullptr) == SQLITE_OK) {
                sqlite3_step(bytes_stmt);
            }
            sqlite3_finalize(bytes_stmt);
            exec_sql(db_, "VACUUM;");
        }
        return;
    }
#endif
    fallback_trim();
}

void QueueStore::open() {
    if (database_path_.empty()) {
        database_path_ = default_queue_path();
    }
#if defined(GUANCE_RUM_HAS_SQLITE)
    std::filesystem::create_directories(std::filesystem::path(database_path_).parent_path());
    if (sqlite3_open(database_path_.c_str(), &db_) == SQLITE_OK) {
        exec_sql(db_, "CREATE TABLE IF NOT EXISTS rum_queue (id INTEGER PRIMARY KEY AUTOINCREMENT, line TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL);");
        exec_sql(db_, "CREATE INDEX IF NOT EXISTS idx_rum_queue_id ON rum_queue(id);");
    } else {
        if (db_ != nullptr) {
            sqlite3_close(db_);
            db_ = nullptr;
        }
    }
#endif
    std::filesystem::create_directories(fallback_directory());
    fallback_recover();
    for (const auto& entry : std::filesystem::directory_iterator(fallback_directory())) {
        if (!entry.is_regular_file()) {
            continue;
        }
        next_memory_id_ = std::max(next_memory_id_, parse_queue_id(entry.path()));
    }
}

void QueueStore::fallback_enqueue(const std::string& line) {
    const auto id = ++next_memory_id_;
    const auto final_path = fallback_directory() / queue_file_name(id);
    const auto temp_path = fallback_directory() / (queue_file_name(id) + ".tmp");
    {
        std::ofstream output(temp_path, std::ios::binary | std::ios::trunc);
        output.write(line.data(), static_cast<std::streamsize>(line.size()));
    }
    std::error_code ec;
    std::filesystem::rename(temp_path, final_path, ec);
    if (ec) {
        std::filesystem::remove(temp_path, ec);
        memory_lines_.push_back({id, line});
    }
    trim();
}

std::filesystem::path QueueStore::fallback_directory() const {
    auto path = std::filesystem::path(database_path_);
    path.replace_extension(".queue");
    return path;
}

std::vector<QueuedLine> QueueStore::fallback_peek(int limit) const {
    std::vector<QueuedLine> result;
    std::vector<std::pair<int64_t, std::filesystem::path>> files;
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(fallback_directory(), ec)) {
        if (ec || !entry.is_regular_file()) {
            continue;
        }
        const auto id = parse_queue_id(entry.path());
        if (id > 0) {
            files.push_back({id, entry.path()});
        }
    }
    std::sort(files.begin(), files.end(), [](const auto& left, const auto& right) { return left.first < right.first; });
    for (const auto& [id, path] : files) {
        if (static_cast<int>(result.size()) >= limit) {
            break;
        }
        std::error_code ec;
        const auto size = std::filesystem::file_size(path, ec);
        if (ec || size == 0 || static_cast<int64_t>(size) > max_bytes_) {
            quarantine_file(path);
            continue;
        }
        const auto line = read_file(path);
        if (line.empty()) {
            quarantine_file(path);
            continue;
        }
        result.push_back({id, line});
    }
    if (result.empty()) {
        const auto count = std::min<std::size_t>(static_cast<std::size_t>(limit), memory_lines_.size());
        return std::vector<QueuedLine>(memory_lines_.begin(), memory_lines_.begin() + static_cast<std::ptrdiff_t>(count));
    }
    return result;
}

void QueueStore::fallback_remove(const std::vector<int64_t>& ids) {
    std::error_code ec;
    for (const auto id : ids) {
        std::filesystem::remove(fallback_directory() / queue_file_name(id), ec);
    }
    memory_lines_.erase(
        std::remove_if(memory_lines_.begin(), memory_lines_.end(), [&](const QueuedLine& line) {
            return std::find(ids.begin(), ids.end(), line.id) != ids.end();
        }),
        memory_lines_.end());
}

void QueueStore::fallback_trim() {
    std::vector<std::tuple<int64_t, std::filesystem::path, uintmax_t>> files;
    uintmax_t total_bytes = 0;
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(fallback_directory(), ec)) {
        if (ec || !entry.is_regular_file()) {
            continue;
        }
        const auto id = parse_queue_id(entry.path());
        if (id > 0) {
            const auto size = entry.file_size(ec);
            if (ec || size == 0 || static_cast<int64_t>(size) > max_bytes_) {
                quarantine_file(entry.path());
                ec.clear();
                continue;
            }
            files.push_back({id, entry.path(), size});
            total_bytes += size;
        }
    }
    std::sort(files.begin(), files.end(), [](const auto& left, const auto& right) { return std::get<0>(left) < std::get<0>(right); });
    while (!files.empty() && (static_cast<int>(files.size()) > max_items_ || static_cast<int64_t>(total_bytes) > max_bytes_)) {
        total_bytes -= std::get<2>(files.front());
        std::filesystem::remove(std::get<1>(files.front()), ec);
        files.erase(files.begin());
    }
    if (static_cast<int>(memory_lines_.size()) > max_items_) {
        memory_lines_.erase(memory_lines_.begin(), memory_lines_.begin() + (static_cast<int>(memory_lines_.size()) - max_items_));
    }
    while (!memory_lines_.empty() && memory_size_bytes() > max_bytes_) {
        memory_lines_.erase(memory_lines_.begin());
    }
}

void QueueStore::fallback_recover() {
    std::error_code ec;
    for (const auto& entry : std::filesystem::directory_iterator(fallback_directory(), ec)) {
        if (ec || !entry.is_regular_file()) {
            continue;
        }

        const auto path = entry.path();
        if (path.extension() == ".tmp") {
            std::filesystem::remove(path, ec);
            ec.clear();
            continue;
        }

        if (parse_queue_id(path) <= 0 && path.extension() != ".bad") {
            quarantine_file(path);
        }
    }
}

int64_t QueueStore::memory_size_bytes() const {
    int64_t size = 0;
    for (const auto& line : memory_lines_) {
        size += static_cast<int64_t>(line.line.size());
    }
    return size;
}

std::string default_queue_path() {
#if defined(_WIN32)
    const char* local_app_data = std::getenv("LOCALAPPDATA");
    const std::string root = local_app_data == nullptr ? "." : local_app_data;
    return root + "\\Guance\\Rum\\native-rum-queue.db";
#else
    const char* home = std::getenv("HOME");
    const std::string root = home == nullptr ? "/tmp" : home;
    return root + "/.guance/rum/native-rum-queue.db";
#endif
}

} // namespace guance::rum
