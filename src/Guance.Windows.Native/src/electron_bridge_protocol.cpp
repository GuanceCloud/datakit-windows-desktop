#include "guance_sdk.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

constexpr std::size_t kMaxBridgeInputBytes = 2 * 1024 * 1024;
constexpr const char* kLaunchCommandPrefix = "@guance-launch\t";
constexpr const char* kErrorCommandPrefix = "@guance-error\t";
constexpr const char* kReplayCommandPrefix = "@guance-replay\t";
constexpr const char* kLogCommandPrefix = "@guance-log\t";

enum class CommandResult {
    not_command,
    accepted,
    rejected
};

bool parse_int64(const std::string& value, int64_t& result) {
    try {
        std::size_t consumed = 0;
        result = std::stoll(value, &consumed);
        return consumed == value.size();
    } catch (...) {
        return false;
    }
}

int hex_value(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

bool percent_decode(const std::string& input, std::string& output) {
    output.clear();
    output.reserve(input.size());
    for (std::size_t index = 0; index < input.size(); ++index) {
        if (input[index] != '%') {
            output.push_back(input[index]);
            continue;
        }
        if (index + 2 >= input.size()) return false;
        const int high = hex_value(input[index + 1]);
        const int low = hex_value(input[index + 2]);
        if (high < 0 || low < 0) return false;
        const char decoded = static_cast<char>((high << 4) | low);
        if (decoded == '\0' || decoded == '\r' || decoded == '\n') return false;
        output.push_back(decoded);
        index += 2;
    }
    return true;
}

int base64_value(char value) {
    if (value >= 'A' && value <= 'Z') return value - 'A';
    if (value >= 'a' && value <= 'z') return value - 'a' + 26;
    if (value >= '0' && value <= '9') return value - '0' + 52;
    if (value == '+') return 62;
    if (value == '/') return 63;
    return -1;
}

bool base64_decode(const std::string& input, std::string& output) {
    output.clear();
    if (input.empty() || input.size() % 4 != 0) return false;
    output.reserve((input.size() / 4) * 3);
    for (std::size_t index = 0; index < input.size(); index += 4) {
        const bool third_padding = input[index + 2] == '=';
        const bool fourth_padding = input[index + 3] == '=';
        if ((third_padding && !fourth_padding) ||
            (index + 4 != input.size() && (third_padding || fourth_padding))) {
            return false;
        }
        const int first = base64_value(input[index]);
        const int second = base64_value(input[index + 1]);
        const int third = third_padding ? 0 : base64_value(input[index + 2]);
        const int fourth = fourth_padding ? 0 : base64_value(input[index + 3]);
        if (first < 0 || second < 0 || third < 0 || fourth < 0) return false;
        output.push_back(static_cast<char>((first << 2) | (second >> 4)));
        if (!third_padding) {
            output.push_back(static_cast<char>(((second & 0x0f) << 4) | (third >> 2)));
        }
        if (!fourth_padding) {
            output.push_back(static_cast<char>(((third & 0x03) << 6) | fourth));
        }
    }
    return true;
}

bool contains_nul(const std::string& value) {
    return value.find('\0') != std::string::npos;
}

bool valid_log_status(const std::string& value) {
    return !value.empty() && value.size() <= 64 &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return std::isalnum(character) || character == '_' ||
                      character == '.' || character == '-';
           });
}

bool valid_log_property_key(const std::string& value) {
    return !value.empty() && value.size() <= 128 &&
           value != "__proto__" && value != "constructor" && value != "prototype" &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return std::isalnum(character) || character == '_' ||
                      character == '.' || character == '-';
           });
}

bool valid_launch_view_value(
    const std::string& value,
    std::size_t maximum_size,
    bool allow_empty) {
    return (allow_empty || !value.empty()) && value.size() <= maximum_size &&
           std::all_of(value.begin(), value.end(), [](unsigned char character) {
               return character >= 0x20 && character != 0x7f;
           });
}

bool parse_launch_view(
    const std::unordered_map<std::string, std::string>& fields,
    std::string& view_id,
    std::string& view_name,
    std::string& view_referrer) {
    if (fields.size() == 6) return true;
    if (fields.size() != 9) return false;

    const auto id = fields.find("view_id");
    const auto name = fields.find("view_name");
    const auto referrer = fields.find("view_referrer");
    return id != fields.end() && name != fields.end() && referrer != fields.end() &&
           percent_decode(id->second, view_id) &&
           percent_decode(name->second, view_name) &&
           percent_decode(referrer->second, view_referrer) &&
           valid_launch_view_value(view_id, 128, false) &&
           valid_launch_view_value(view_name, 4096, true) &&
           valid_launch_view_value(view_referrer, 4096, true);
}

CommandResult handle_launch_command(
    guance_sdk_handle handle,
    const std::string& line) {
    if (line.rfind(kLaunchCommandPrefix, 0) != 0) return CommandResult::not_command;

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-launch") {
        return CommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return CommandResult::rejected;
        }
    }
    std::string view_id;
    std::string view_name;
    std::string view_referrer;
    if (!parse_launch_view(fields, view_id, view_name, view_referrer)) {
        return CommandResult::rejected;
    }

    const auto type = fields.find("type");
    if (type == fields.end() || (type->second != "cold" && type->second != "hot")) {
        return CommandResult::rejected;
    }
    guance_rum_launch launch{};
    launch.type = type->second == "hot" ? GUANCE_RUM_LAUNCH_HOT : GUANCE_RUM_LAUNCH_COLD;
    if (!parse_int64(fields["start_time_ns"], launch.start_time_ns) ||
        !parse_int64(fields["duration_ns"], launch.duration_ns) ||
        !parse_int64(fields["pre_application_duration_ns"], launch.pre_application_duration_ns) ||
        !parse_int64(fields["application_duration_ns"], launch.application_duration_ns) ||
        !parse_int64(fields["first_frame_duration_ns"], launch.first_frame_duration_ns)) {
        return CommandResult::rejected;
    }
    if (view_id.empty()) {
        guance_rum_add_launch_action(handle, &launch);
    } else {
        guance_rum_add_launch_action_ext(
            handle,
            &launch,
            view_id.c_str(),
            view_name.c_str(),
            view_referrer.c_str());
    }
    return CommandResult::accepted;
}

CommandResult handle_error_command(
    guance_sdk_handle handle,
    const std::string& line) {
    if (line.rfind(kErrorCommandPrefix, 0) != 0) return CommandResult::not_command;

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-error") {
        return CommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return CommandResult::rejected;
        }
    }
    if (fields.size() != 2) return CommandResult::rejected;

    const auto type = fields.find("type");
    const bool supported_type = type != fields.end() &&
        (type->second == "ElectronRendererProcessGone" ||
         type->second == "ElectronRendererUnresponsive");
    std::string message;
    if (!supported_type || !percent_decode(fields["message"], message) ||
        message.empty() || message.size() > 2048) {
        return CommandResult::rejected;
    }
    guance_rum_add_error(handle, "", message.c_str(), type->second.c_str(), "logger");
    return CommandResult::accepted;
}

CommandResult handle_log_command(
    guance_sdk_handle handle,
    const std::string& line) {
    if (line.rfind(kLogCommandPrefix, 0) != 0) return CommandResult::not_command;

    std::istringstream input(line);
    std::vector<std::string> parts;
    std::string part;
    while (std::getline(input, part, '\t')) parts.push_back(std::move(part));
    if (parts.size() < 3 || parts[0] != "@guance-log" ||
        parts[1].rfind("status=", 0) != 0 ||
        parts[2].rfind("message=", 0) != 0 ||
        (parts.size() - 3) % 2 != 0 || (parts.size() - 3) / 2 > 256) {
        return CommandResult::rejected;
    }

    const auto status = parts[1].substr(std::string("status=").size());
    std::string message;
    if (!valid_log_status(status) ||
        !base64_decode(parts[2].substr(std::string("message=").size()), message) ||
        message.empty() || message.size() > 256 * 1024 || contains_nul(message)) {
        return CommandResult::rejected;
    }

    std::vector<std::pair<std::string, std::string>> property_storage;
    for (std::size_t index = 3; index < parts.size(); index += 2) {
        if (parts[index].rfind("property-key=", 0) != 0 ||
            parts[index + 1].rfind("property-value=", 0) != 0) {
            return CommandResult::rejected;
        }
        std::string key;
        std::string value;
        const auto encoded_key = parts[index].substr(std::string("property-key=").size());
        const auto encoded_value = parts[index + 1].substr(std::string("property-value=").size());
        if (!base64_decode(encoded_key, key) ||
            (!encoded_value.empty() && !base64_decode(encoded_value, value)) ||
            !valid_log_property_key(key) || value.size() > 64 * 1024 || contains_nul(value)) {
            return CommandResult::rejected;
        }
        property_storage.emplace_back(std::move(key), std::move(value));
    }

    std::vector<guance_log_property> properties;
    properties.reserve(property_storage.size());
    for (const auto& [key, value] : property_storage) {
        properties.push_back({key.c_str(), value.c_str()});
    }
    return guance_log_add(
        handle,
        message.c_str(),
        status.c_str(),
        properties.data(),
        static_cast<uint32_t>(properties.size())) == 1
        ? CommandResult::accepted
        : CommandResult::rejected;
}

CommandResult handle_replay_command(
    guance_sdk_handle handle,
    const std::string& line) {
    if (line.rfind(kReplayCommandPrefix, 0) != 0) return CommandResult::not_command;

    std::istringstream input(line);
    std::string part;
    if (!std::getline(input, part, '\t') || part != "@guance-replay") {
        return CommandResult::rejected;
    }
    std::unordered_map<std::string, std::string> fields;
    while (std::getline(input, part, '\t')) {
        const auto separator = part.find('=');
        if (separator == std::string::npos ||
            !fields.emplace(part.substr(0, separator), part.substr(separator + 1)).second) {
            return CommandResult::rejected;
        }
    }
    if (fields.size() != 4) return CommandResult::rejected;

    std::string view_id;
    std::string record_json;
    int64_t timestamp_ms = 0;
    const auto full_snapshot = fields.find("full_snapshot");
    if (!percent_decode(fields["view_id"], view_id) ||
        view_id.empty() || view_id.size() > 128 ||
        !parse_int64(fields["timestamp_ms"], timestamp_ms) || timestamp_ms <= 0 ||
        full_snapshot == fields.end() ||
        (full_snapshot->second != "0" && full_snapshot->second != "1") ||
        !base64_decode(fields["record"], record_json) ||
        record_json.empty() || record_json.size() > 1024 * 1024) {
        return CommandResult::rejected;
    }
    return guance_rum_capture_browser_replay_record(
        handle,
        nullptr,
        view_id.c_str(),
        record_json.data(),
        record_json.size(),
        timestamp_ms,
        full_snapshot->second == "1" ? 1 : 0) == 1
        ? CommandResult::accepted
        : CommandResult::rejected;
}

} // namespace

extern "C" int guance_sdk_write_electron_bridge_line(
    guance_sdk_handle handle,
    const char* line,
    size_t length) {
    if (handle == nullptr || line == nullptr || length == 0 ||
        length > kMaxBridgeInputBytes ||
        std::find(line, line + length, '\0') != line + length ||
        std::find(line, line + length, '\r') != line + length ||
        std::find(line, line + length, '\n') != line + length) {
        return 0;
    }

    try {
        const std::string input(line, length);
        const auto handle_command = [](CommandResult result) {
            if (result == CommandResult::accepted) return 1;
            if (result == CommandResult::rejected) return 0;
            return -1;
        };
        for (const auto handler : {
                 handle_launch_command,
                 handle_error_command,
                 handle_log_command,
                 handle_replay_command}) {
            const int result = handle_command(handler(handle, input));
            if (result >= 0) return result;
        }
        if (!input.empty() && input.front() == '@') return 0;
        const auto rum_line = input + "\n";
        return guance_sdk_write_line(handle, rum_line.data(), rum_line.size());
    } catch (...) {
        return 0;
    }
}
