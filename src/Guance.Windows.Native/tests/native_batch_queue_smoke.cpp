#include "queue_store.h"

#include <cassert>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <string>

using guance::rum::CacheQuota;
using guance::rum::QueueStore;
using guance::rum::QueueStreamKind;

namespace {

std::filesystem::path unique_cache_directory() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
        ("guance-native-batch-queue-" + std::to_string(suffix));
}

} // namespace

int main() {
    const auto root = unique_cache_directory();
    auto quota = std::make_shared<CacheQuota>(root, 64 * 1024, 8);

    {
        QueueStore queue(root, "rum", QueueStreamKind::rum, quota, 2, 4096, 3600);
        assert(queue.enqueue("first\n"));
        assert(queue.enqueue("second\n"));

        auto first_lease = queue.acquire();
        assert(first_lease);
        assert(first_lease.lines.size() == 2);
        assert(first_lease.lines[0] == "first\n");
        assert(first_lease.lines[1] == "second\n");

        queue.abandon(first_lease.lease_id);
        auto retried_lease = queue.acquire();
        assert(retried_lease);
        assert(retried_lease.lines == first_lease.lines);
        queue.complete(retried_lease.lease_id);
        assert(!queue.acquire());
    }

    {
        QueueStore queue(root, "timed", QueueStreamKind::rum, quota, 50, 4096, 3600);
        assert(queue.enqueue("timed-seal\n"));
        assert(!queue.acquire());
        assert(!queue.seal_if_older(60'000));
        assert(queue.seal_if_older(0));
        const auto lease = queue.acquire();
        assert(lease);
        assert(lease.lines.front() == "timed-seal\n");
        queue.complete(lease.lease_id);
    }

    {
        QueueStore queue(root, "rum", QueueStreamKind::rum, quota, 1, 4096, 3600);
        assert(queue.enqueue("recover-after-crash\n"));
        const auto interrupted_lease = queue.acquire();
        assert(interrupted_lease);
    }
    {
        QueueStore recovered(root, "rum", QueueStreamKind::rum, quota, 1, 4096, 3600);
        const auto lease = recovered.acquire();
        assert(lease);
        assert(lease.lines.front() == "recover-after-crash\n");
        recovered.complete(lease.lease_id);
    }

    {
        QueueStore queue(root, "rum", QueueStreamKind::rum, quota, 1, 4096, 3600);
        assert(queue.enqueue("checksum\n"));
        const auto ready = root / "rum" / "ready";
        const auto path = std::filesystem::directory_iterator(ready)->path();
        std::fstream file(path, std::ios::binary | std::ios::in | std::ios::out);
        file.seekg(-1, std::ios::end);
        char value = 0;
        file.read(&value, 1);
        value ^= 0x7f;
        file.seekp(-1, std::ios::end);
        file.write(&value, 1);
        file.close();
        assert(!queue.acquire());
    }

    {
        auto limited_quota = std::make_shared<CacheQuota>(root, 64 * 1024, 2);
        QueueStore rum(root, "rum", QueueStreamKind::rum, limited_quota, 1, 4096, 3600);
        QueueStore logs(root, "logs", QueueStreamKind::log, limited_quota, 1, 4096, 3600);
        assert(rum.enqueue("rum-1\n"));
        assert(logs.enqueue("log-1\n"));
        assert(rum.enqueue("rum-2\n"));
        assert(limited_quota->file_count() == 2);
        assert(limited_quota->allocated_bytes() <= 64 * 1024);
    }

    std::error_code ec;
    std::filesystem::remove_all(root, ec);
    return 0;
}
