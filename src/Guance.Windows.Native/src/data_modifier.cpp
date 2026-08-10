#include "data_modifier.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <iomanip>
#include <limits>
#include <locale>
#include <optional>
#include <sstream>
#include <string_view>
#include <type_traits>
#include <utility>
#include <vector>

namespace guance::rum {

namespace {

guance_data_value to_c_value(const FieldValue& value) {
    guance_data_value result{};
    std::visit([&result](const auto& current) {
        using T = std::decay_t<decltype(current)>;
        if constexpr (std::is_same_v<T, std::nullptr_t>) {
            result.type = GUANCE_DATA_VALUE_NULL;
        } else if constexpr (std::is_same_v<T, bool>) {
            result.type = GUANCE_DATA_VALUE_BOOL;
            result.value.bool_value = current ? 1 : 0;
        } else if constexpr (std::is_same_v<T, int64_t>) {
            result.type = GUANCE_DATA_VALUE_INT64;
            result.value.int64_value = current;
        } else if constexpr (std::is_same_v<T, double>) {
            result.type = GUANCE_DATA_VALUE_DOUBLE;
            result.value.double_value = current;
        } else {
            result.type = GUANCE_DATA_VALUE_STRING;
            result.value.string_value = current.c_str();
        }
    }, value);
    return result;
}

guance_data_value to_c_value(const std::string& value) {
    guance_data_value result{};
    result.type = GUANCE_DATA_VALUE_STRING;
    result.value.string_value = value.c_str();
    return result;
}

bool from_c_value(const guance_data_value& value, FieldValue& destination) {
    switch (value.type) {
    case GUANCE_DATA_VALUE_BOOL:
        destination = value.value.bool_value != 0;
        return true;
    case GUANCE_DATA_VALUE_INT64:
        destination = value.value.int64_value;
        return true;
    case GUANCE_DATA_VALUE_DOUBLE:
        destination = value.value.double_value;
        return true;
    case GUANCE_DATA_VALUE_STRING:
        if (value.value.string_value != nullptr) {
            destination = std::string(value.value.string_value);
            return true;
        }
        return false;
    case GUANCE_DATA_VALUE_NULL:
    default:
        return false;
    }
}

bool from_c_value(const guance_data_value& value, std::string& destination) {
    switch (value.type) {
    case GUANCE_DATA_VALUE_BOOL:
        destination = value.value.bool_value != 0 ? "true" : "false";
        return true;
    case GUANCE_DATA_VALUE_INT64:
        destination = std::to_string(value.value.int64_value);
        return true;
    case GUANCE_DATA_VALUE_DOUBLE: {
        std::ostringstream output;
        output.imbue(std::locale::classic());
        output << std::setprecision(std::numeric_limits<double>::max_digits10)
               << value.value.double_value;
        destination = output.str();
        return true;
    }
    case GUANCE_DATA_VALUE_STRING:
        if (value.value.string_value != nullptr) {
            destination = value.value.string_value;
            return true;
        }
        return false;
    case GUANCE_DATA_VALUE_NULL:
    default:
        return false;
    }
}

template <typename Map>
void apply_each(Map& values, const DataModifierConfig& config) {
    if (config.data_modifier == nullptr) {
        return;
    }

    for (auto& [key, value] : values) {
        const auto current = to_c_value(value);
        guance_data_value replacement{};
        try {
            if (config.data_modifier(
                    key.c_str(),
                    &current,
                    &replacement,
                    config.user_data) != 0) {
                from_c_value(replacement, value);
            }
        } catch (...) {
            // Never allow a C++ callback exception to cross the C ABI or telemetry pipeline.
        }
    }
}

void apply_privacy(RumEvent& event, const ResourceCollectionConfig& privacy) {
    const auto is_url_key = [](const std::string& key) {
        const auto ends_with = [&key](const char* suffix) {
            const std::string_view suffix_view(suffix);
            return key.size() >= suffix_view.size() &&
                std::equal(suffix_view.rbegin(), suffix_view.rend(), key.rbegin(),
                    [](char left, char right) {
                        return std::tolower(static_cast<unsigned char>(left)) ==
                            std::tolower(static_cast<unsigned char>(right));
                    });
        };
        return key == "resource_url" || ends_with("_url") || ends_with("_referrer");
    };
    const auto is_url_view_name = [](const std::string& key, const std::string& value) {
        if (key != "view_name") {
            return false;
        }
        std::string prefix = value.substr(0, std::min<std::size_t>(value.size(), 8));
        std::transform(prefix.begin(), prefix.end(), prefix.begin(), [](unsigned char character) {
            return static_cast<char>(std::tolower(character));
        });
        return prefix.rfind("http://", 0) == 0 || prefix.rfind("https://", 0) == 0;
    };
    const auto redact = [&privacy, &is_url_key, &is_url_view_name](auto& values) {
        for (auto& [key, value] : values) {
            if constexpr (std::is_same_v<typename std::decay_t<decltype(values)>::mapped_type, std::string>) {
                if (is_url_key(key) || is_url_view_name(key, value)) {
                    value = sanitize_resource_url(value, privacy);
                } else if (key == "request_header" || key == "response_header") {
                    value = sanitize_http_headers(value, privacy);
                }
            } else if (auto* text = std::get_if<std::string>(&value)) {
                if (is_url_key(key) || is_url_view_name(key, *text)) {
                    *text = sanitize_resource_url(*text, privacy);
                } else if (key == "request_header" || key == "response_header") {
                    *text = sanitize_http_headers(*text, privacy);
                }
            }
        }
    };
    redact(event.tags);
    redact(event.fields);
}

} // namespace

bool data_modifier_config_from_c(
    const guance_data_modifier_config& source,
    DataModifierConfig& destination) {
    constexpr std::size_t required_size =
        offsetof(guance_data_modifier_config, user_data) + sizeof(source.user_data);
    if (source.struct_size < required_size ||
        source.version != GUANCE_DATA_MODIFIER_CONFIG_VERSION) {
        return false;
    }

    destination = DataModifierConfig{
        source.data_modifier,
        source.line_data_modifier,
        source.user_data};
    return true;
}

void apply_data_modifiers(
    RumEvent& event,
    const ResourceCollectionConfig& privacy,
    const DataModifierConfig& modifiers) {
    apply_each(event.tags, modifiers);
    apply_each(event.fields, modifiers);

    if (modifiers.line_data_modifier != nullptr) {
        struct ItemTarget {
            bool field;
            const std::string* key;
        };
        std::vector<guance_data_item> items;
        std::vector<ItemTarget> targets;
        items.reserve(event.tags.size() + event.fields.size());
        targets.reserve(event.tags.size() + event.fields.size());
        for (const auto& [key, value] : event.tags) {
            if (event.fields.find(key) != event.fields.end()) {
                continue;
            }
            items.push_back(guance_data_item{key.c_str(), to_c_value(value)});
            targets.push_back(ItemTarget{false, &key});
        }
        for (const auto& [key, value] : event.fields) {
            items.push_back(guance_data_item{key.c_str(), to_c_value(value)});
            targets.push_back(ItemTarget{true, &key});
        }

        bool callback_succeeded = true;
        try {
            modifiers.line_data_modifier(
                event.measurement.c_str(),
                items.data(),
                static_cast<uint32_t>(items.size()),
                modifiers.user_data);
        } catch (...) {
            callback_succeeded = false;
        }

        if (callback_succeeded) {
            std::vector<std::optional<FieldValue>> replacements;
            replacements.reserve(items.size());
            for (std::size_t index = 0; index < items.size(); ++index) {
                FieldValue replacement;
                bool valid = false;
                if (targets[index].field) {
                    valid = from_c_value(items[index].value, replacement);
                } else {
                    std::string tag_replacement;
                    valid = from_c_value(items[index].value, tag_replacement);
                    if (valid) {
                        replacement = std::move(tag_replacement);
                    }
                }
                replacements.push_back(valid
                    ? std::optional<FieldValue>(std::move(replacement))
                    : std::nullopt);
            }

            for (std::size_t index = 0; index < targets.size(); ++index) {
                if (!replacements[index]) {
                    continue;
                }
                if (targets[index].field) {
                    event.fields[*targets[index].key] = std::move(*replacements[index]);
                } else if (auto* text = std::get_if<std::string>(&*replacements[index])) {
                    event.tags[*targets[index].key] = std::move(*text);
                }
            }
        }
    }

    apply_privacy(event, privacy);
}

} // namespace guance::rum
