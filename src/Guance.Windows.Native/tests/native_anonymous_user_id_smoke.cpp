#include "anonymous_user_id.h"
#include "queue_store.h"

#include <cassert>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

namespace {

std::filesystem::path unique_cache_directory() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
        ("guance-native-anonymous-user-" + std::to_string(suffix));
}

} // namespace

int main() {
    using guance::rum::AnonymousUserIdResolution;
    using guance::rum::CacheQuota;
    using guance::rum::anonymous_user_id_path;
    using guance::rum::is_valid_anonymous_user_id;
    using guance::rum::load_or_create_anonymous_user_id;

    const auto root = unique_cache_directory();
    const auto first = load_or_create_anonymous_user_id(root, "app");
    const auto second = load_or_create_anonymous_user_id(root, "app");
    assert(first.persistence_error.empty());
    assert(first.value == second.value);
    assert(is_valid_anonymous_user_id(first.value));
    assert(anonymous_user_id_path(root, "app").filename() == "e74fa719056a838c.id");

    const auto other_app = load_or_create_anonymous_user_id(root, "other-app");
    assert(other_app.persistence_error.empty());
    assert(other_app.value != first.value);

    const auto corrupt_path = anonymous_user_id_path(root, "corrupt-app");
    std::filesystem::create_directories(corrupt_path.parent_path());
    {
        std::ofstream corrupt(corrupt_path, std::ios::binary | std::ios::trunc);
        corrupt << "invalid";
    }
    const auto repaired = load_or_create_anonymous_user_id(root, "corrupt-app");
    assert(repaired.persistence_error.empty());
    assert(is_valid_anonymous_user_id(repaired.value));

    const auto recovery_path = anonymous_user_id_path(root, "recovery-app");
    auto temporary_recovery_path = recovery_path;
    temporary_recovery_path += ".recovery.new";
    const std::string recovered_value =
        "ft.rd_0123456789abcdef0123456789abcdef";
    {
        std::ofstream temporary(temporary_recovery_path, std::ios::binary | std::ios::trunc);
        temporary << recovered_value;
    }
    const auto recovered = load_or_create_anonymous_user_id(root, "recovery-app");
    assert(recovered.persistence_error.empty());
    assert(recovered.value == recovered_value);
    assert(!std::filesystem::exists(temporary_recovery_path));

    std::vector<AnonymousUserIdResolution> concurrent(12);
    std::vector<std::thread> threads;
    threads.reserve(concurrent.size());
    for (std::size_t index = 0; index < concurrent.size(); ++index) {
        threads.emplace_back([&, index] {
            concurrent[index] = load_or_create_anonymous_user_id(root, "concurrent-app");
        });
    }
    for (auto& thread : threads) {
        thread.join();
    }
    for (const auto& item : concurrent) {
        assert(item.persistence_error.empty());
        assert(item.value == concurrent.front().value);
    }

    auto quota = std::make_shared<CacheQuota>(root, 64 * 1024, 8);
    assert(quota->allocated_bytes() == 0);
    assert(quota->file_count() == 0);

    const auto unusable_root = root / "not-a-directory";
    {
        std::ofstream file(unusable_root, std::ios::binary | std::ios::trunc);
        file << "file";
    }
    const auto ephemeral = load_or_create_anonymous_user_id(unusable_root, "app");
    assert(is_valid_anonymous_user_id(ephemeral.value));
    assert(!ephemeral.persistence_error.empty());

    std::error_code cleanup_error;
    std::filesystem::remove_all(root, cleanup_error);
    return 0;
}
