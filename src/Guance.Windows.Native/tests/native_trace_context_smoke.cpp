#include "trace_context.h"

#include <cassert>
#include <cstring>
#include <string>

namespace {

const std::string& header(
    const guance::rum::TraceContext& context,
    const std::string& name) {
    for (const auto& item : context.headers) {
        if (item.name == name) {
            return item.value;
        }
    }
    assert(false && "trace header not found");
    return context.headers.front().value;
}

void assert_has_ids(const guance::rum::TraceContext& context) {
    assert(!context.trace_id.empty());
    assert(!context.span_id.empty());
}

int custom_context(
    const char*,
    const char*,
    guance_trace_context* context,
    void*) {
    context->sampled = 1;
    std::strcpy(context->trace_id, "company-trace-id");
    std::strcpy(context->span_id, "company-span-id");
    context->header_count = 1;
    std::strcpy(context->headers[0].name, "x-company-trace");
    std::strcpy(context->headers[0].value, "company-header");
    return 1;
}

} // namespace

int main() {
    auto config = guance::rum::default_trace_config("native-trace-tests");
    config.enable_auto_trace = true;
    config.enable_link_rum_data = true;
    config.sample_rate = 1.0;

    config.trace_type = GUANCE_TRACE_DDTRACE;
    auto context = guance::rum::create_trace_context(
        config,
        "https://api.example.com/v1/items",
        "GET");
    assert(context.has_value());
    assert_has_ids(*context);
    assert(header(*context, "x-datadog-trace-id") == context->trace_id);
    assert(header(*context, "x-datadog-parent-id") == context->span_id);
    assert(header(*context, "x-datadog-sampling-priority") == "2");

    config.trace_type = GUANCE_TRACE_ZIPKIN_MULTI_HEADER;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(header(*context, "X-B3-TraceId") == context->trace_id);
    assert(header(*context, "X-B3-SpanId") == context->span_id);
    assert(header(*context, "X-B3-Sampled") == "1");

    config.trace_type = GUANCE_TRACE_ZIPKIN_SINGLE_HEADER;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(header(*context, "b3") == context->trace_id + "-" + context->span_id + "-1");

    config.trace_type = GUANCE_TRACE_TRACEPARENT;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(header(*context, "traceparent") ==
        "00-" + context->trace_id + "-" + context->span_id + "-01");

    config.trace_type = GUANCE_TRACE_JAEGER;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(header(*context, "uber-trace-id") ==
        context->trace_id + ":" + context->span_id + ":0:1");

    config.trace_type = GUANCE_TRACE_SKYWALKING;
    context = guance::rum::create_trace_context(
        config,
        "https://api.example.com/v1/items",
        "GET");
    assert(context.has_value());
    assert_has_ids(*context);
    assert(header(*context, "sw8").rfind("1-", 0) == 0);

    config.sample_rate = 0.0;
    config.trace_type = GUANCE_TRACE_TRACEPARENT;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(header(*context, "traceparent").substr(header(*context, "traceparent").size() - 2) == "00");

    config.should_trace = [](const char*, const char*, void*) { return 0; };
    context = guance::rum::create_trace_context(config, "https://untrusted.example.com", "GET");
    assert(!context.has_value());

    config.should_trace = nullptr;
    config.context_provider = custom_context;
    context = guance::rum::create_trace_context(config, "https://api.example.com", "GET");
    assert(context.has_value());
    assert(context->trace_id == "company-trace-id");
    assert(context->span_id == "company-span-id");
    assert(header(*context, "x-company-trace") == "company-header");

    guance_trace_config c_config{};
    c_config.struct_size = sizeof(c_config);
    c_config.version = GUANCE_TRACE_CONFIG_VERSION;
    c_config.trace_type = GUANCE_TRACE_DDTRACE;
    c_config.sample_rate = 1.0;

    c_config.sample_rate = 2.0;
    guance::rum::TraceConfig parsed;
    assert(!guance::rum::trace_config_from_c(c_config, "service", parsed));
    return 0;
}
