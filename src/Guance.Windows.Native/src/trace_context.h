#pragma once

#include "guance_sdk.h"

#include <optional>
#include <string>
#include <vector>

namespace guance::rum {

struct TraceHeader {
    std::string name;
    std::string value;
};

struct TraceContext {
    bool sampled = false;
    bool link_rum_data = false;
    std::string trace_id;
    std::string span_id;
    std::vector<TraceHeader> headers;
};

struct TraceConfig {
    bool enable_auto_trace = false;
    bool enable_link_rum_data = false;
    double sample_rate = 1.0;
    guance_trace_type trace_type = GUANCE_TRACE_DDTRACE;
    std::string service_name;
    guance_trace_should_trace_callback should_trace = nullptr;
    guance_trace_context_provider_callback context_provider = nullptr;
    void* user_data = nullptr;
};

TraceConfig default_trace_config(std::string service_name);
bool trace_config_from_c(
    const guance_trace_config& source,
    const std::string& default_service_name,
    TraceConfig& destination);
std::optional<TraceContext> create_trace_context(
    const TraceConfig& config,
    const std::string& url,
    const std::string& method) noexcept;
bool trace_context_to_c(
    const TraceContext& source,
    guance_trace_context& destination) noexcept;

} // namespace guance::rum
