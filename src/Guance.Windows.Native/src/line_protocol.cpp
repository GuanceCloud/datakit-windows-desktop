#include "line_protocol.h"

#include <chrono>
#include <iomanip>
#include <limits>
#include <locale>
#include <random>
#include <sstream>
#include <vector>
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
            ss.imbue(std::locale::classic());
            ss << std::setprecision(std::numeric_limits<double>::max_digits10) << v;
            auto result = ss.str();
            if (result.find_first_of(".eE") == std::string::npos) {
                result += ".0";
            }
            return result;
        } else {
            return "\"" + escape_field_string(v) + "\"";
        }
    }, value);
}

std::string unescape(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    bool escaped = false;
    for (const char character : value) {
        if (escaped) {
            result.push_back(character);
            escaped = false;
        } else if (character == '\\') {
            escaped = true;
        } else {
            result.push_back(character);
        }
    }
    if (escaped) {
        result.push_back('\\');
    }
    return result;
}

std::string unescape_field_string(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    for (std::size_t index = 0; index < value.size(); ++index) {
        if (value[index] != '\\' || index + 1 >= value.size()) {
            result.push_back(value[index]);
            continue;
        }
        const char escaped = value[++index];
        if (escaped == 'r') {
            result.push_back('\r');
        } else if (escaped == 'n') {
            result.push_back('\n');
        } else {
            result.push_back(escaped);
        }
    }
    return result;
}

std::vector<std::string_view> split_unescaped(
    std::string_view value,
    char separator,
    bool honor_quotes) {
    std::vector<std::string_view> parts;
    std::size_t start = 0;
    bool escaped = false;
    bool quoted = false;
    for (std::size_t index = 0; index < value.size(); ++index) {
        const char character = value[index];
        if (escaped) {
            escaped = false;
            continue;
        }
        if (character == '\\') {
            escaped = true;
            continue;
        }
        if (honor_quotes && character == '"') {
            quoted = !quoted;
            continue;
        }
        if (!quoted && character == separator) {
            parts.push_back(value.substr(start, index - start));
            start = index + 1;
        }
    }
    if (quoted) {
        return {};
    }
    parts.push_back(value.substr(start));
    return parts;
}

std::size_t find_unescaped(std::string_view value, char character) {
    bool escaped = false;
    for (std::size_t index = 0; index < value.size(); ++index) {
        if (escaped) {
            escaped = false;
        } else if (value[index] == '\\') {
            escaped = true;
        } else if (value[index] == character) {
            return index;
        }
    }
    return std::string_view::npos;
}

bool parse_int64(std::string_view value, int64_t& result) {
    try {
        std::size_t consumed = 0;
        const auto parsed = std::stoll(std::string(value), &consumed);
        if (consumed != value.size()) {
            return false;
        }
        result = parsed;
        return true;
    } catch (...) {
        return false;
    }
}

bool parse_field_value(std::string_view value, FieldValue& result) {
    if (value.size() >= 2 && value.front() == '"' && value.back() == '"') {
        result = unescape_field_string(value.substr(1, value.size() - 2));
        return true;
    }
    if (value == "true" || value == "false") {
        result = value == "true";
        return true;
    }
    if (!value.empty() && value.back() == 'i') {
        int64_t parsed = 0;
        if (!parse_int64(value.substr(0, value.size() - 1), parsed)) {
            return false;
        }
        result = parsed;
        return true;
    }
    std::istringstream input{std::string(value)};
    input.imbue(std::locale::classic());
    double parsed = 0;
    input >> std::noskipws >> parsed;
    if (!input || input.peek() != std::char_traits<char>::eof()) {
        return false;
    }
    result = parsed;
    return true;
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

bool parse_line_protocol(std::string_view line, RumEvent& event) {
    if (line.empty() || line.back() != '\n') {
        return false;
    }
    line.remove_suffix(1);
    if (line.empty() ||
        line.find_first_of("\r\n") != std::string_view::npos ||
        line.find('\0') != std::string_view::npos) {
        return false;
    }

    const auto first_space = find_unescaped(line, ' ');
    if (first_space == std::string_view::npos) {
        return false;
    }
    bool escaped = false;
    bool quoted = false;
    std::size_t second_space = std::string_view::npos;
    for (std::size_t index = first_space + 1; index < line.size(); ++index) {
        const char character = line[index];
        if (escaped) {
            escaped = false;
        } else if (character == '\\') {
            escaped = true;
        } else if (character == '"') {
            quoted = !quoted;
        } else if (!quoted && character == ' ') {
            second_space = index;
            break;
        }
    }
    if (second_space == std::string_view::npos || quoted) {
        return false;
    }

    RumEvent parsed;
    const auto head_parts = split_unescaped(line.substr(0, first_space), ',', false);
    if (head_parts.empty() || head_parts.front().empty()) {
        return false;
    }
    parsed.measurement = unescape(head_parts.front());
    for (std::size_t index = 1; index < head_parts.size(); ++index) {
        const auto equals = find_unescaped(head_parts[index], '=');
        if (equals == std::string_view::npos || equals == 0) {
            return false;
        }
        parsed.tags[unescape(head_parts[index].substr(0, equals))] =
            unescape(head_parts[index].substr(equals + 1));
    }

    const auto field_parts = split_unescaped(
        line.substr(first_space + 1, second_space - first_space - 1),
        ',',
        true);
    if (field_parts.empty()) {
        return false;
    }
    for (const auto part : field_parts) {
        const auto equals = find_unescaped(part, '=');
        if (equals == std::string_view::npos || equals == 0) {
            return false;
        }
        FieldValue value;
        if (!parse_field_value(part.substr(equals + 1), value)) {
            return false;
        }
        parsed.fields[unescape(part.substr(0, equals))] = std::move(value);
    }
    if (!parse_int64(line.substr(second_space + 1), parsed.timestamp_ns)) {
        return false;
    }
    event = std::move(parsed);
    return true;
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
