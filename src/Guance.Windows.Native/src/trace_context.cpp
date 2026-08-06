#include "trace_context.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <iomanip>
#include <limits>
#include <random>
#include <sstream>
#include <thread>
#include <utility>

namespace guance::rum {

namespace {

constexpr std::size_t kMaxServiceNameBytes = 256;
thread_local bool invoking_trace_callback = false;

class TraceCallbackScope final {
public:
    TraceCallbackScope() noexcept {
        invoking_trace_callback = true;
    }

    ~TraceCallbackScope() {
        invoking_trace_callback = false;
    }
};

std::mt19937_64& random_engine() {
    thread_local std::mt19937_64 engine([] {
        std::random_device device;
        std::seed_seq seed{
            device(),
            device(),
            device(),
            device(),
            static_cast<unsigned int>(
                std::chrono::high_resolution_clock::now().time_since_epoch().count()),
            static_cast<unsigned int>(
                std::hash<std::thread::id>{}(std::this_thread::get_id()))
        };
        return std::mt19937_64(seed);
    }());
    return engine;
}

bool sampled(double rate) {
    if (rate <= 0.0) {
        return false;
    }
    if (rate >= 1.0) {
        return true;
    }
    return std::generate_canonical<double, std::numeric_limits<double>::digits>(
        random_engine()) < rate;
}

std::string random_hex(std::size_t bytes) {
    static constexpr char alphabet[] = "0123456789abcdef";
    std::uniform_int_distribution<unsigned int> distribution(0, 255);
    std::string value(bytes * 2, '0');
    bool any_non_zero = false;
    do {
        any_non_zero = false;
        for (std::size_t index = 0; index < bytes; ++index) {
            const auto byte = distribution(random_engine());
            any_non_zero = any_non_zero || byte != 0;
            value[index * 2] = alphabet[(byte >> 4) & 0x0f];
            value[index * 2 + 1] = alphabet[byte & 0x0f];
        }
    } while (!any_non_zero);
    return value;
}

std::string random_decimal() {
    std::uniform_int_distribution<uint64_t> distribution(
        1,
        (std::numeric_limits<uint64_t>::max)());
    return std::to_string(distribution(random_engine()));
}

std::string base64(const std::string& value) {
    static constexpr char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string encoded;
    encoded.reserve(((value.size() + 2) / 3) * 4);
    for (std::size_t index = 0; index < value.size(); index += 3) {
        const auto first = static_cast<unsigned char>(value[index]);
        const bool has_second = index + 1 < value.size();
        const bool has_third = index + 2 < value.size();
        const auto second = has_second
            ? static_cast<unsigned char>(value[index + 1])
            : static_cast<unsigned char>(0);
        const auto third = has_third
            ? static_cast<unsigned char>(value[index + 2])
            : static_cast<unsigned char>(0);
        const uint32_t block =
            (static_cast<uint32_t>(first) << 16) |
            (static_cast<uint32_t>(second) << 8) |
            static_cast<uint32_t>(third);
        encoded.push_back(alphabet[(block >> 18) & 0x3f]);
        encoded.push_back(alphabet[(block >> 12) & 0x3f]);
        encoded.push_back(has_second ? alphabet[(block >> 6) & 0x3f] : '=');
        encoded.push_back(has_third ? alphabet[block & 0x3f] : '=');
    }
    return encoded;
}

std::pair<std::string, std::string> path_and_peer(const std::string& url) {
    const auto scheme_end = url.find("://");
    const auto authority_start = scheme_end == std::string::npos ? 0 : scheme_end + 3;
    const auto authority_end = url.find_first_of("/?#", authority_start);
    const auto peer = url.substr(
        authority_start,
        authority_end == std::string::npos
            ? std::string::npos
            : authority_end - authority_start);
    const auto path_start = url.find('/', authority_start);
    if (path_start == std::string::npos) {
        return {"/", peer};
    }
    const auto path_end = url.find_first_of("?#", path_start);
    return {
        url.substr(
            path_start,
            path_end == std::string::npos ? std::string::npos : path_end - path_start),
        peer
    };
}

TraceContext make_context(
    bool is_sampled,
    bool link_rum_data,
    std::string trace_id,
    std::string span_id,
    std::vector<TraceHeader> headers) {
    return TraceContext{
        is_sampled,
        link_rum_data,
        std::move(trace_id),
        std::move(span_id),
        std::move(headers)
    };
}

template <std::size_t Capacity>
bool read_buffer(const char (&buffer)[Capacity], std::string& value) {
    const auto end = std::find(buffer, buffer + Capacity, '\0');
    if (end == buffer + Capacity) {
        return false;
    }
    value.assign(buffer, end);
    return true;
}

template <std::size_t Capacity>
bool write_buffer(const std::string& value, char (&buffer)[Capacity]) {
    if (value.size() >= Capacity) {
        return false;
    }
    std::memcpy(buffer, value.data(), value.size());
    buffer[value.size()] = '\0';
    return true;
}

void initialize_c_context(guance_trace_context& context) {
    context = guance_trace_context{};
    context.struct_size = sizeof(guance_trace_context);
    context.version = GUANCE_TRACE_CONTEXT_VERSION;
}

std::optional<TraceContext> custom_context(
    const TraceConfig& config,
    const std::string& url,
    const std::string& method) {
    guance_trace_context supplied{};
    initialize_c_context(supplied);
    int provided = 0;
    {
        TraceCallbackScope scope;
        provided = config.context_provider(
            url.c_str(),
            method.c_str(),
            &supplied,
            config.user_data);
    }
    constexpr std::size_t required_size =
        offsetof(guance_trace_context, headers) + sizeof(supplied.headers);
    if (provided == 0 ||
        supplied.struct_size < required_size ||
        supplied.version != GUANCE_TRACE_CONTEXT_VERSION ||
        supplied.header_count > GUANCE_TRACE_MAX_HEADERS) {
        return std::nullopt;
    }

    TraceContext parsed;
    parsed.sampled = supplied.sampled != 0;
    parsed.link_rum_data = config.enable_link_rum_data;
    if (!read_buffer(supplied.trace_id, parsed.trace_id) ||
        !read_buffer(supplied.span_id, parsed.span_id)) {
        return std::nullopt;
    }
    parsed.headers.reserve(supplied.header_count);
    for (uint32_t index = 0; index < supplied.header_count; ++index) {
        TraceHeader item;
        if (!read_buffer(supplied.headers[index].name, item.name) ||
            !read_buffer(supplied.headers[index].value, item.value) ||
            item.name.empty()) {
            return std::nullopt;
        }
        parsed.headers.push_back(std::move(item));
    }
    return parsed;
}

bool valid_trace_type(guance_trace_type value) {
    return value >= GUANCE_TRACE_DDTRACE && value <= GUANCE_TRACE_JAEGER;
}

} // namespace

TraceConfig default_trace_config(std::string service_name) {
    TraceConfig config;
    config.service_name = std::move(service_name);
    return config;
}

bool trace_config_from_c(
    const guance_trace_config& source,
    const std::string& default_service_name,
    TraceConfig& destination) {
    constexpr std::size_t required_size =
        offsetof(guance_trace_config, user_data) + sizeof(source.user_data);
    if (source.struct_size < required_size ||
        source.version != GUANCE_TRACE_CONFIG_VERSION ||
        !std::isfinite(source.sample_rate) ||
        source.sample_rate < 0.0 ||
        source.sample_rate > 1.0 ||
        !valid_trace_type(source.trace_type)) {
        return false;
    }

    TraceConfig parsed = default_trace_config(default_service_name);
    parsed.enable_auto_trace = source.enable_auto_trace != 0;
    parsed.enable_link_rum_data = source.enable_link_rum_data != 0;
    parsed.sample_rate = source.sample_rate;
    parsed.trace_type = source.trace_type;
    parsed.should_trace = source.should_trace;
    parsed.context_provider = source.context_provider;
    parsed.user_data = source.user_data;
    if (source.service_name != nullptr && source.service_name[0] != '\0') {
        const auto length = std::char_traits<char>::length(source.service_name);
        if (length > kMaxServiceNameBytes) {
            return false;
        }
        parsed.service_name.assign(source.service_name, length);
    }
    destination = std::move(parsed);
    return true;
}

std::optional<TraceContext> create_trace_context(
    const TraceConfig& config,
    const std::string& url,
    const std::string& method) noexcept {
    try {
        if (!config.enable_auto_trace || url.empty() || invoking_trace_callback) {
            return std::nullopt;
        }
        if (config.should_trace != nullptr) {
            int should_trace = 0;
            {
                TraceCallbackScope scope;
                should_trace = config.should_trace(
                    url.c_str(),
                    method.c_str(),
                    config.user_data);
            }
            if (should_trace == 0) {
                return std::nullopt;
            }
        }
        if (config.context_provider != nullptr) {
            return custom_context(config, url, method);
        }

        const bool is_sampled = sampled(config.sample_rate);
        if (config.trace_type == GUANCE_TRACE_DDTRACE) {
            auto trace_id = random_decimal();
            auto span_id = random_decimal();
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {
                    {"x-datadog-origin", "rum"},
                    {"x-datadog-sampling-priority", is_sampled ? "2" : "-1"},
                    {"x-datadog-parent-id", span_id},
                    {"x-datadog-trace-id", trace_id}
                });
        }

        auto trace_id = random_hex(16);
        auto span_id = random_hex(8);
        switch (config.trace_type) {
        case GUANCE_TRACE_ZIPKIN_MULTI_HEADER:
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {
                    {"X-B3-TraceId", trace_id},
                    {"X-B3-SpanId", span_id},
                    {"X-B3-Sampled", is_sampled ? "1" : "0"}
                });
        case GUANCE_TRACE_ZIPKIN_SINGLE_HEADER:
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {{"b3", trace_id + "-" + span_id + "-" + (is_sampled ? "1" : "0")}});
        case GUANCE_TRACE_TRACEPARENT:
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {{"traceparent", "00-" + trace_id + "-" + span_id + "-" + (is_sampled ? "01" : "00")}});
        case GUANCE_TRACE_SKYWALKING: {
            const auto [path, peer] = path_and_peer(url);
            const auto parent_trace_id = span_id;
            span_id += "0";
            static const std::string instance = random_hex(8) + "@windows";
            const auto sw8 =
                std::string(is_sampled ? "1-" : "0-") +
                base64(trace_id) + "-" +
                base64(parent_trace_id) + "-0-" +
                base64(config.service_name) + "-" +
                base64(instance) + "-" +
                base64(path) + "-" +
                base64(peer);
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {{"sw8", sw8}});
        }
        case GUANCE_TRACE_JAEGER:
            return make_context(
                is_sampled,
                config.enable_link_rum_data,
                trace_id,
                span_id,
                {{"uber-trace-id", trace_id + ":" + span_id + ":0:" + (is_sampled ? "1" : "0")}});
        case GUANCE_TRACE_DDTRACE:
            break;
        }
    } catch (...) {
        return std::nullopt;
    }
    return std::nullopt;
}

bool trace_context_to_c(
    const TraceContext& source,
    guance_trace_context& destination) noexcept {
    try {
        if (source.headers.size() > GUANCE_TRACE_MAX_HEADERS) {
            return false;
        }
        guance_trace_context output{};
        initialize_c_context(output);
        output.sampled = source.sampled ? 1 : 0;
        output.link_rum_data = source.link_rum_data ? 1 : 0;
        if (!write_buffer(source.trace_id, output.trace_id) ||
            !write_buffer(source.span_id, output.span_id)) {
            return false;
        }
        output.header_count = static_cast<uint32_t>(source.headers.size());
        for (std::size_t index = 0; index < source.headers.size(); ++index) {
            if (!write_buffer(source.headers[index].name, output.headers[index].name) ||
                !write_buffer(source.headers[index].value, output.headers[index].value)) {
                return false;
            }
        }
        destination = output;
        return true;
    } catch (...) {
        return false;
    }
}

} // namespace guance::rum
