#pragma once

#include <filesystem>
#include <string>

namespace guance::rum {

struct AnonymousUserIdResolution {
    std::string value;
    std::string persistence_error;
};

AnonymousUserIdResolution load_or_create_anonymous_user_id(
    const std::filesystem::path& cache_root,
    const std::string& rum_app_id);

std::filesystem::path anonymous_user_id_path(
    const std::filesystem::path& cache_root,
    const std::string& rum_app_id);

bool is_valid_anonymous_user_id(const std::string& value);

} // namespace guance::rum
