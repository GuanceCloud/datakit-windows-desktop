#include "line_protocol.h"

#include <chrono>
#include <iomanip>
#include <random>
#include <sstream>
#include <stdexcept>

namespace guance::rum {

namespace {

std::string escape_measurement(std::string value) {
    std::string out;
    out.reserve(value.size());
    for (const char c : value) {
        if (c == ',' || c == ' ') {
            out.push_back('\\');
        }
        out.push_back(c);
    }
    return out;
}

std::string escape_key(std::string value) {
    std::string out;
    out.reserve(value.size());
    for (const char c : value) {
        if (c == ',' || c == ' ' || c == '=') {
            out.push_back('\\');
        }
        out.push_back(c);
    }
    return out;
}

std::string escape_field_string(std::string value) {
    std::string out;
    out.reserve(value.size());
    for (const char c : value) {
        if (c == '\r') {
            out.append("\\r");
            continue;
        }
        if (c == '\n') {
            out.append("\\n");
            continue;
        }
        if (c == '\\' || c == '"') {
            out.push_back('\\');
        }
        out.push_back(c);
    }
    return out;
}

std::string field_value_to_string(const FieldValue& value) {
    return std::visit([](const auto& v) -> std::string {
        using T = std::decay_t<decltype(v)>;
        if constexpr (std::is_same_v<T, std::nullptr_t>) {
            return "\"\"";
        } else if constexpr (std::is_same_v<T, bool>) {
            return v ? "true" : "false";
        } else if constexpr (std::is_same_v<T, int64_t>) {
            return std::to_string(v) + "i";
        } else if constexpr (std::is_same_v<T, double>) {
            std::ostringstream ss;
            ss << std::fixed << std::setprecision(6) << v;
            auto result = ss.str();
            while (result.size() > 3 && result.back() == '0') {
                result.pop_back();
            }
            if (result.back() == '.') {
                result.push_back('0');
            }
            return result;
        } else {
            return "\"" + escape_field_string(v) + "\"";
        }
    }, value);
}

} // namespace

std::string format_line_protocol(const RumEvent& event) {
    if (event.fields.empty()) {
        throw std::invalid_argument("line protocol requires at least one field");
    }

    std::ostringstream ss;
    ss << escape_measurement(event.measurement);
    for (const auto& [key, value] : event.tags) {
        if (value.empty()) {
            continue;
        }
        ss << "," << escape_key(key) << "=" << escape_key(value);
    }

    ss << " ";
    bool first = true;
    for (const auto& [key, value] : event.fields) {
        if (!first) {
            ss << ",";
        }
        first = false;
        ss << escape_key(key) << "=" << field_value_to_string(value);
    }

    ss << " " << event.timestamp_ns << "\n";
    return ss.str();
}

int64_t unix_time_nanoseconds() {
    const auto now = std::chrono::system_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
}

int64_t monotonic_time_nanoseconds() {
    const auto now = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
}

bool is_within_forward_window(int64_t timestamp, int64_t previous_timestamp, int64_t window) {
    if (timestamp < previous_timestamp || window < 0) {
        return false;
    }
    const auto elapsed = static_cast<uint64_t>(timestamp) - static_cast<uint64_t>(previous_timestamp);
    return elapsed <= static_cast<uint64_t>(window);
}

std::string uuid32() {
    static thread_local std::mt19937_64 rng{std::random_device{}()};
    static constexpr char hex[] = "0123456789abcdef";
    std::string out(32, '0');
    for (char& c : out) {
        c = hex[rng() & 0x0f];
    }
    return out;
}

} // namespace guance::rum
