#include "resource_collection.h"

#include <cassert>
#include <string>

namespace {

int reject_private(const char* url, const char*, void*) {
    return std::string(url).find("/private") == std::string::npos ? 1 : 0;
}

} // namespace

int main() {
    auto config = guance::rum::default_resource_collection_config();
    assert(guance::rum::sanitize_http_headers(
        "Authorization: secret\r\nX-Keep: visible\r\nSet-Cookie: session",
        config) ==
        "Authorization: <redacted>\nX-Keep: visible\nSet-Cookie: <redacted>");
    assert(guance::rum::sanitize_resource_url(
        "https://example.com/items?TOKEN=secret&keep=1#result",
        config) ==
        "https://example.com/items?TOKEN=%3Credacted%3E&keep=1#result");
    assert(guance::rum::sanitize_resource_url(
        "https://example.com/items?api%5Fkey=secret",
        config) ==
        "https://example.com/items?api%5Fkey=%3Credacted%3E");

    config.capture_url_query = false;
    assert(guance::rum::sanitize_resource_url(
        "https://example.com/items?token=secret#result",
        config) ==
        "https://example.com/items#result");

    config.capture_url_query = true;
    config.redact_all_url_query_values = true;
    assert(guance::rum::sanitize_resource_url(
        "https://example.com/items?first=1&second=2",
        config) ==
        "https://example.com/items?first=%3Credacted%3E&second=%3Credacted%3E");

    const char* names[] = {"credential"};
    guance_rum_resource_collection_config c_config{};
    c_config.struct_size = sizeof(c_config);
    c_config.version = GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION;
    c_config.enabled = 1;
    c_config.capture_url_query = 1;
    c_config.redacted_value = "hidden";
    c_config.redacted_query_parameter_names = names;
    c_config.redacted_query_parameter_name_count = 1;
    const char* header_names[] = {"x-private"};
    c_config.capture_http_headers = 1;
    c_config.redacted_header_names = header_names;
    c_config.redacted_header_name_count = 1;
    c_config.should_collect = reject_private;

    guance::rum::ResourceCollectionConfig parsed;
    assert(guance::rum::resource_collection_config_from_c(c_config, parsed));
    assert(guance::rum::sanitize_resource_url(
        "https://example.com?credential=value",
        parsed) ==
        "https://example.com?credential=hidden");
    assert(guance::rum::sanitize_http_headers(
        "X-Private: value\nX-Keep: visible",
        parsed) ==
        "X-Private: hidden\nX-Keep: visible");
    assert(guance::rum::should_collect_resource(
        parsed,
        "https://example.com/public",
        "GET"));
    assert(!guance::rum::should_collect_resource(
        parsed,
        "https://example.com/private",
        "GET"));

    {
        guance::rum::ResourceCollectionSuppressionScope suppression;
        assert(!guance::rum::should_collect_resource(
            parsed,
            "https://example.com/public",
            "GET"));
    }
    assert(guance::rum::should_collect_resource(
        parsed,
        "https://example.com/public",
        "GET"));

    c_config.version++;
    assert(!guance::rum::resource_collection_config_from_c(c_config, parsed));
    return 0;
}
