#pragma once

#include "guance_sdk.h"

#include <string>
#include <vector>

namespace guance::rum {

struct ResourceCollectionConfig {
    bool enabled = true;
    bool capture_url_query = true;
    bool redact_all_url_query_values = false;
    std::string redacted_value = "<redacted>";
    std::vector<std::string> redacted_query_parameter_names;
    bool capture_http_headers = true;
    std::vector<std::string> redacted_header_names;
    guance_rum_resource_should_collect_callback should_collect = nullptr;
    void* user_data = nullptr;
};

ResourceCollectionConfig default_resource_collection_config();
const char* const* default_redacted_query_parameter_names() noexcept;
uint32_t default_redacted_query_parameter_name_count() noexcept;
const char* const* default_redacted_header_names() noexcept;
uint32_t default_redacted_header_name_count() noexcept;
bool resource_collection_config_from_c(
    const guance_rum_resource_collection_config& source,
    ResourceCollectionConfig& destination);
bool should_collect_resource(
    const ResourceCollectionConfig& config,
    const std::string& url,
    const std::string& method) noexcept;
std::string sanitize_resource_url(
    const std::string& url,
    const ResourceCollectionConfig& config);
std::string sanitize_http_headers(
    const std::string& headers,
    const ResourceCollectionConfig& config);

bool resource_collection_suppressed() noexcept;

class ResourceCollectionSuppressionScope final {
public:
    ResourceCollectionSuppressionScope() noexcept;
    ~ResourceCollectionSuppressionScope();

    ResourceCollectionSuppressionScope(const ResourceCollectionSuppressionScope&) = delete;
    ResourceCollectionSuppressionScope& operator=(const ResourceCollectionSuppressionScope&) = delete;
};

} // namespace guance::rum
