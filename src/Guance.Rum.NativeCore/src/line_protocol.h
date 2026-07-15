#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <variant>

namespace guance::rum {

using FieldValue = std::variant<std::nullptr_t, bool, int64_t, double, std::string>;
using Tags = std::map<std::string, std::string>;
using Fields = std::map<std::string, FieldValue>;

struct RumEvent {
    std::string measurement;
    Tags tags;
    Fields fields;
    int64_t timestamp_ns = 0;
};

std::string format_line_protocol(const RumEvent& event);
int64_t unix_time_nanoseconds();
int64_t monotonic_time_nanoseconds();
bool is_within_forward_window(int64_t timestamp, int64_t previous_timestamp, int64_t window);
std::string uuid32();

} // namespace guance::rum
