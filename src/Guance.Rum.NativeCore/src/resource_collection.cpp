#include "resource_collection.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <iomanip>
#include <iterator>
#include <sstream>

namespace guance::rum {

namespace {

constexpr std::size_t kMaxRedactedQueryParameterNames = 128;
constexpr std::size_t kMaxRedactedValueBytes = 256;

const char* const kDefaultRedactedQueryParameterNames[] = {
    "token",
    "access_token",
    "refresh_token",
    "password",
    "passwd",
    "secret",
    "api_key",
    "apikey"
};

thread_local unsigned int suppression_depth = 0;
thread_local bool invoking_filter = false;

std::string ascii_lower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

int hex_value(char character) {
    if (character >= '0' && character <= '9') {
        return character - '0';
    }
    if (character >= 'a' && character <= 'f') {
        return character - 'a' + 10;
    }
    if (character >= 'A' && character <= 'F') {
        return character - 'A' + 10;
    }
    return -1;
}

std::string decode_query_name(const std::string& value) {
    std::string decoded;
    decoded.reserve(value.size());
    for (std::size_t index = 0; index < value.size(); ++index) {
        if (value[index] == '%' && index + 2 < value.size()) {
            const int high = hex_value(value[index + 1]);
            const int low = hex_value(value[index + 2]);
            if (high >= 0 && low >= 0) {
                decoded.push_back(static_cast<char>((high << 4) | low));
                index += 2;
                continue;
            }
        }
        decoded.push_back(value[index] == '+' ? ' ' : value[index]);
    }
    return ascii_lower(std::move(decoded));
}

std::string percent_encode(const std::string& value) {
    std::ostringstream encoded;
    encoded << std::uppercase << std::hex;
    for (const unsigned char character : value) {
        if (std::isalnum(character) ||
            character == '-' ||
            character == '_' ||
            character == '.' ||
            character == '~') {
            encoded << static_cast<char>(character);
        } else {
            encoded << '%' << std::setw(2) << std::setfill('0') << static_cast<int>(character);
        }
    }
    return encoded.str();
}

bool contains_name(const std::vector<std::string>& names, const std::string& name) {
    return std::find(names.begin(), names.end(), name) != names.end();
}

class FilterInvocationScope final {
public:
    FilterInvocationScope() noexcept {
        invoking_filter = true;
    }

    ~FilterInvocationScope() {
        invoking_filter = false;
    }
};

} // namespace

ResourceCollectionConfig default_resource_collection_config() {
    ResourceCollectionConfig config;
    config.redacted_query_parameter_names.assign(
        std::begin(kDefaultRedactedQueryParameterNames),
        std::end(kDefaultRedactedQueryParameterNames));
    return config;
}

const char* const* default_redacted_query_parameter_names() noexcept {
    return kDefaultRedactedQueryParameterNames;
}

uint32_t default_redacted_query_parameter_name_count() noexcept {
    return static_cast<uint32_t>(
        sizeof(kDefaultRedactedQueryParameterNames) /
        sizeof(kDefaultRedactedQueryParameterNames[0]));
}

bool resource_collection_config_from_c(
    const guance_rum_resource_collection_config& source,
    ResourceCollectionConfig& destination) {
    constexpr std::size_t required_size =
        offsetof(guance_rum_resource_collection_config, user_data) +
        sizeof(source.user_data);
    if (source.struct_size < required_size ||
        source.version != GUANCE_RUM_RESOURCE_COLLECTION_CONFIG_VERSION ||
        source.redacted_value == nullptr ||
        source.redacted_value[0] == '\0' ||
        std::char_traits<char>::length(source.redacted_value) > kMaxRedactedValueBytes ||
        source.redacted_query_parameter_name_count > kMaxRedactedQueryParameterNames ||
        (source.redacted_query_parameter_name_count > 0 &&
         source.redacted_query_parameter_names == nullptr)) {
        return false;
    }

    ResourceCollectionConfig parsed;
    parsed.enabled = source.enabled != 0;
    parsed.capture_url_query = source.capture_url_query != 0;
    parsed.redact_all_url_query_values = source.redact_all_url_query_values != 0;
    parsed.redacted_value = source.redacted_value;
    parsed.should_collect = source.should_collect;
    parsed.user_data = source.user_data;
    parsed.redacted_query_parameter_names.reserve(source.redacted_query_parameter_name_count);
    for (uint32_t index = 0; index < source.redacted_query_parameter_name_count; ++index) {
        const char* name = source.redacted_query_parameter_names[index];
        if (name == nullptr || name[0] == '\0') {
            return false;
        }
        parsed.redacted_query_parameter_names.push_back(ascii_lower(name));
    }

    destination = std::move(parsed);
    return true;
}

bool should_collect_resource(
    const ResourceCollectionConfig& config,
    const std::string& url,
    const std::string& method) noexcept {
    if (!config.enabled || resource_collection_suppressed() || invoking_filter) {
        return false;
    }
    if (config.should_collect == nullptr) {
        return true;
    }

    try {
        FilterInvocationScope scope;
        return config.should_collect(url.c_str(), method.c_str(), config.user_data) != 0;
    } catch (...) {
        return false;
    }
}

std::string sanitize_resource_url(
    const std::string& url,
    const ResourceCollectionConfig& config) {
    const auto query_start = url.find('?');
    if (query_start == std::string::npos) {
        return url;
    }

    const auto fragment_start = url.find('#', query_start + 1);
    const std::string fragment = fragment_start == std::string::npos
        ? std::string{}
        : url.substr(fragment_start);
    if (!config.capture_url_query) {
        return url.substr(0, query_start) + fragment;
    }

    const auto query_end = fragment_start == std::string::npos ? url.size() : fragment_start;
    const std::string query = url.substr(query_start + 1, query_end - query_start - 1);
    const std::string replacement = percent_encode(config.redacted_value);
    std::string sanitized;
    sanitized.reserve(url.size());
    sanitized.append(url, 0, query_start + 1);

    std::size_t part_start = 0;
    while (part_start <= query.size()) {
        const auto separator = query.find('&', part_start);
        const auto part_end = separator == std::string::npos ? query.size() : separator;
        const std::string part = query.substr(part_start, part_end - part_start);
        const auto equals = part.find('=');
        const std::string raw_name = equals == std::string::npos ? part : part.substr(0, equals);
        const bool redact = config.redact_all_url_query_values ||
            contains_name(config.redacted_query_parameter_names, decode_query_name(raw_name));

        sanitized += raw_name;
        if (equals != std::string::npos) {
            sanitized += '=';
            sanitized += redact ? replacement : part.substr(equals + 1);
        }
        if (separator == std::string::npos) {
            break;
        }
        sanitized += '&';
        part_start = separator + 1;
    }

    sanitized += fragment;
    return sanitized;
}

bool resource_collection_suppressed() noexcept {
    return suppression_depth > 0;
}

ResourceCollectionSuppressionScope::ResourceCollectionSuppressionScope() noexcept {
    ++suppression_depth;
}

ResourceCollectionSuppressionScope::~ResourceCollectionSuppressionScope() {
    if (suppression_depth > 0) {
        --suppression_depth;
    }
}

} // namespace guance::rum
