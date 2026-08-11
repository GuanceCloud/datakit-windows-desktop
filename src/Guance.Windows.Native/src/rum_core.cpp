#include "rum_core.h"

#include "guance_sdk_version.h"
#include "native_monitoring.h"
#include "transport.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <filesystem>
#include <iostream>
#include <iomanip>
#include <iterator>
#include <limits>
#include <cmath>
#include <random>
#include <regex>
#include <sstream>
#include <stdexcept>

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <oleauto.h>
#include <uiautomation.h>
#endif

namespace guance::rum {

namespace {

constexpr int64_t kReplaySegmentFlushMilliseconds = 5000;
constexpr int64_t kReplayCoalesceMilliseconds = 200;
constexpr const char* kWindowsReplaySource = "windows";
constexpr const char* kWindowsSdkName = "df_windows_rum_sdk";
constexpr const char* kWindowsSdkVersion = GUANCE_WINDOWS_NATIVE_SDK_VERSION;
constexpr const char* kWindowsLogSource = "df_rum_windows_log";
constexpr std::size_t kMaxBridgeLineBytes = 1024 * 1024;
constexpr std::size_t kMaxBrowserReplayRecordBytes = 1024 * 1024;
constexpr std::size_t kMaxLogContentBytes = 30 * 1024;
constexpr int64_t kReplayEnvelopeAllowanceBytes = 64LL * 1024;
constexpr int64_t kActionFrequentProtectionNanoseconds = 100'000'000;
constexpr int64_t kActionMaxDurationNanoseconds = 5'000'000'000;

std::optional<std::string> bridge_measurement(const char* line, std::size_t length) {
    if (line == nullptr ||
        length < 4 ||
        length > kMaxBridgeLineBytes ||
        line[length - 1] != '\n') {
        return std::nullopt;
    }

    const auto payload_end = length - 1;
    std::size_t measurement_end = payload_end;
    for (std::size_t index = 0; index < payload_end; ++index) {
        const char character = line[index];
        if (character == '\0' || character == '\r' || character == '\n') {
            return std::nullopt;
        }
        if (measurement_end == payload_end && (character == ',' || character == ' ')) {
            measurement_end = index;
        }
    }

    if (measurement_end == 0 || measurement_end == payload_end) {
        return std::nullopt;
    }

    const std::string measurement(line, measurement_end);
    if (measurement != "view" &&
        measurement != "action" &&
        measurement != "resource" &&
        measurement != "error" &&
        measurement != "long_task") {
        return std::nullopt;
    }

    const auto fields_start = std::find(line + measurement_end, line + payload_end, ' ');
    if (fields_start == line + payload_end ||
        std::find(fields_start + 1, line + payload_end, ' ') == line + payload_end) {
        return std::nullopt;
    }

    return measurement;
}

int64_t non_negative_duration(int64_t duration_ns) {
    return std::max<int64_t>(duration_ns, 0);
}

int64_t elapsed_since(int64_t started_monotonic_ns) {
    return non_negative_duration(monotonic_time_nanoseconds() - started_monotonic_ns);
}

int64_t unix_time_before(int64_t duration_ns) {
    const auto now = unix_time_nanoseconds();
    const auto safe_duration_ns = non_negative_duration(duration_ns);
    return safe_duration_ns >= now ? 0 : now - safe_duration_ns;
}

std::string str_or_empty(const char* value) {
    return value == nullptr ? std::string{} : std::string(value);
}

bool hit_rate(double rate) {
    if (rate >= 1.0) {
        return true;
    }
    if (rate <= 0.0) {
        return false;
    }
    static thread_local std::mt19937_64 rng{std::random_device{}()};
    return std::uniform_real_distribution<double>(0.0, 1.0)(rng) < rate;
}

std::filesystem::path cache_root_path(const std::string& configured) {
    return configured.empty()
        ? std::filesystem::path(default_queue_path())
        : std::filesystem::path(configured);
}

std::string normalize_log_status(const char* status) {
    auto value = str_or_empty(status);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

uint32_t log_level_mask(const std::string& status) {
    if (status == "debug") return GUANCE_LOG_DEBUG;
    if (status == "info") return GUANCE_LOG_INFO;
    if (status == "warning") return GUANCE_LOG_WARNING;
    if (status == "error") return GUANCE_LOG_ERROR;
    if (status == "critical") return GUANCE_LOG_CRITICAL;
    if (status == "ok") return GUANCE_LOG_OK;
    return 0;
}

std::string truncate_log_content(const char* content) {
    auto value = str_or_empty(content);
    if (value.size() <= kMaxLogContentBytes) {
        return value;
    }

    std::size_t length = kMaxLogContentBytes;
    while (length > 0 &&
           (static_cast<unsigned char>(value[length]) & 0xC0u) == 0x80u) {
        --length;
    }
    value.resize(length);
    return value;
}

std::optional<std::string> multipart_boundary(const std::string& content_type) {
    const std::string marker = "boundary=";
    const auto marker_index = content_type.find(marker);
    if (marker_index == std::string::npos) {
        return std::nullopt;
    }

    auto boundary = content_type.substr(marker_index + marker.size());
    const auto separator_index = boundary.find(';');
    if (separator_index != std::string::npos) {
        boundary = boundary.substr(0, separator_index);
    }

    while (!boundary.empty() && std::isspace(static_cast<unsigned char>(boundary.front()))) {
        boundary.erase(boundary.begin());
    }

    while (!boundary.empty() && std::isspace(static_cast<unsigned char>(boundary.back()))) {
        boundary.pop_back();
    }

    if (boundary.size() >= 2 && boundary.front() == '"' && boundary.back() == '"') {
        boundary = boundary.substr(1, boundary.size() - 2);
    }

    if (boundary.empty()) {
        return std::nullopt;
    }

    return boundary;
}

std::optional<std::string> replay_segment_payload(const std::string& content_type, const std::string& body) {
    const auto boundary = multipart_boundary(content_type);
    if (!boundary) {
        return std::nullopt;
    }

    const auto segment_part_index = body.find("name=\"segment\"");
    if (segment_part_index == std::string::npos) {
        return std::nullopt;
    }

    const auto data_start_marker = body.find("\r\n\r\n", segment_part_index);
    if (data_start_marker == std::string::npos) {
        return std::nullopt;
    }

    const auto data_start = data_start_marker + 4;
    const auto data_end = body.find("\r\n--" + *boundary, data_start);
    if (data_end == std::string::npos || data_end <= data_start) {
        return std::nullopt;
    }

    return body.substr(data_start, data_end - data_start);
}

int64_t unix_time_milliseconds() {
    return unix_time_nanoseconds() / 1'000'000;
}

int replay_int(double value) {
    if (!std::isfinite(value)) {
        return 0;
    }

    return std::max(0, static_cast<int>(std::llround(value)));
}

bool is_hex_uuid32(const std::string& value) {
    if (value.size() != 32) {
        return false;
    }

    return std::all_of(value.begin(), value.end(), [](unsigned char c) {
        return std::isxdigit(c) != 0;
    });
}

std::string normalize_replay_id(const std::string& value) {
    if (!is_hex_uuid32(value)) {
        return value;
    }

    return value.substr(0, 8) + "-" +
           value.substr(8, 4) + "-" +
           value.substr(12, 4) + "-" +
           value.substr(16, 4) + "-" +
           value.substr(20);
}

std::string json_escape(const std::string& value) {
    std::ostringstream ss;
    for (const unsigned char c : value) {
        switch (c) {
        case '\\':
            ss << "\\\\";
            break;
        case '"':
            ss << "\\\"";
            break;
        case '\b':
            ss << "\\b";
            break;
        case '\f':
            ss << "\\f";
            break;
        case '\n':
            ss << "\\n";
            break;
        case '\r':
            ss << "\\r";
            break;
        case '\t':
            ss << "\\t";
            break;
        default:
            if (c < 0x20) {
                ss << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
            } else {
                ss << static_cast<char>(c);
            }
        }
    }
    return ss.str();
}

uint32_t replay_adler32(const std::string& value) {
    constexpr uint32_t modulus = 65521;
    uint32_t a = 1;
    uint32_t b = 0;
    for (const unsigned char byte : value) {
        a = (a + byte) % modulus;
        b = (b + a) % modulus;
    }
    return (b << 16) | a;
}

std::string zlib_store(const std::string& value) {
    constexpr std::size_t max_block_size = 65535;
    const auto block_count = std::max<std::size_t>(
        1,
        (value.size() + max_block_size - 1) / max_block_size);
    std::string encoded;
    encoded.reserve(value.size() + 6 + block_count * 5);

    // 0x78 0x01 is a valid zlib header for DEFLATE with no preset dictionary.
    encoded.push_back(static_cast<char>(0x78));
    encoded.push_back(static_cast<char>(0x01));

    std::size_t offset = 0;
    do {
        const auto block_size = std::min(max_block_size, value.size() - offset);
        const bool final_block = offset + block_size == value.size();
        const auto length = static_cast<uint16_t>(block_size);
        const auto inverse_length = static_cast<uint16_t>(~length);
        encoded.push_back(final_block ? '\x01' : '\x00');
        encoded.push_back(static_cast<char>(length & 0xff));
        encoded.push_back(static_cast<char>((length >> 8) & 0xff));
        encoded.push_back(static_cast<char>(inverse_length & 0xff));
        encoded.push_back(static_cast<char>((inverse_length >> 8) & 0xff));
        encoded.append(value, offset, block_size);
        offset += block_size;
    } while (offset < value.size());

    const auto checksum = replay_adler32(value);
    encoded.push_back(static_cast<char>((checksum >> 24) & 0xff));
    encoded.push_back(static_cast<char>((checksum >> 16) & 0xff));
    encoded.push_back(static_cast<char>((checksum >> 8) & 0xff));
    encoded.push_back(static_cast<char>(checksum & 0xff));
    return encoded;
}

std::string multipart_field(const std::string& boundary, const std::string& name, const std::string& value) {
    return "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n";
}

std::string multipart_file(const std::string& boundary, const std::string& name, const std::string& filename, const std::string& value) {
    return "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"; filename=\"" + filename + "\"\r\nContent-Type: application/octet-stream\r\n\r\n" + value + "\r\n";
}

bool contains_case_insensitive(std::string value, std::string needle) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    std::transform(needle.begin(), needle.end(), needle.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value.find(needle) != std::string::npos;
}

std::string status_group(int status_code) {
    if (status_code <= 0) {
        return {};
    }
    return std::to_string(status_code / 100) + "xx";
}

std::string host_from_url(const std::string& url) {
    static const std::regex re(R"(^[a-zA-Z][a-zA-Z0-9+.-]*://([^/:?#]+))");
    std::smatch match;
    return std::regex_search(url, match, re) ? match[1].str() : std::string{};
}

std::string path_from_url(const std::string& url) {
    static const std::regex re(R"(^[a-zA-Z][a-zA-Z0-9+.-]*://[^/]+([^?#]*))");
    std::smatch match;
    return std::regex_search(url, match, re) ? match[1].str() : std::string{};
}

std::string grouped_path(std::string path) {
    return std::regex_replace(path, std::regex(R"(/([^/]*)\d([^/]*))"), "/?");
}

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
std::string wide_to_utf8(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
    return result;
}

std::string bstr_to_utf8(BSTR value) {
    if (value == nullptr) {
        return {};
    }
    std::wstring text(value, SysStringLen(value));
    SysFreeString(value);
    return wide_to_utf8(text);
}

std::string tag_for_uia_control(CONTROLTYPEID control_type) {
    switch (control_type) {
    case UIA_ButtonControlTypeId:
    case UIA_MenuItemControlTypeId:
        return "button";
    case UIA_EditControlTypeId:
        return "input";
    case UIA_ComboBoxControlTypeId:
    case UIA_ListControlTypeId:
        return "select";
    case UIA_TextControlTypeId:
        return "span";
    default:
        return "div";
    }
}

bool is_sensitive_uia_control(CONTROLTYPEID control_type) {
    return control_type == UIA_EditControlTypeId ||
           control_type == UIA_ComboBoxControlTypeId ||
           control_type == UIA_CheckBoxControlTypeId ||
           control_type == UIA_RadioButtonControlTypeId;
}

std::string masked_text(const std::string& value);
std::string build_style(const RECT& rect);
std::string build_text_node(int& id, const std::string& text);

std::string capture_uia_text(
    IUIAutomationElement* element,
    CONTROLTYPEID control_type,
    guance_rum_session_replay_text_privacy privacy) {
    BSTR name = nullptr;
    if (FAILED(element->get_CurrentName(&name))) {
        return {};
    }
    const auto text = bstr_to_utf8(name);
    if (text.empty()) {
        return {};
    }
    if (privacy == GUANCE_RUM_REPLAY_TEXT_MASK_ALL ||
        privacy == GUANCE_RUM_REPLAY_TEXT_MASK_ALL_INPUTS ||
        (privacy == GUANCE_RUM_REPLAY_TEXT_MASK_SENSITIVE_INPUTS && is_sensitive_uia_control(control_type))) {
        return masked_text(text);
    }
    return text;
}

std::string build_uia_node(
    IUIAutomationElement* element,
    IUIAutomationTreeWalker* walker,
    int& id,
    const std::unordered_map<uintptr_t, guance_rum_session_replay_text_privacy>& text_privacy,
    const std::unordered_map<uintptr_t, bool>& hidden,
    guance_rum_session_replay_text_privacy inherited_privacy) {
    RECT rect{};
    element->get_CurrentBoundingRectangle(&rect);
    UIA_HWND native_handle = 0;
    element->get_CurrentNativeWindowHandle(&native_handle);
    CONTROLTYPEID control_type = 0;
    element->get_CurrentControlType(&control_type);

    const auto key = reinterpret_cast<uintptr_t>(native_handle);
    const auto privacy_it = text_privacy.find(key);
    const auto privacy = privacy_it == text_privacy.end() ? inherited_privacy : privacy_it->second;
    const bool is_hidden = key != 0 && hidden.find(key) != hidden.end() && hidden.at(key);
    const auto tag_name = tag_for_uia_control(control_type);
    const auto text = is_hidden ? std::string{} : capture_uia_text(element, control_type, privacy);
    const int node_id = id++;

    std::ostringstream children;
    bool has_child = false;
    if (is_hidden) {
        children << build_text_node(id, "Hidden");
        has_child = true;
    } else {
        if (!text.empty()) {
            children << build_text_node(id, text);
            has_child = true;
        }

        IUIAutomationElement* child = nullptr;
        if (SUCCEEDED(walker->GetFirstChildElement(element, &child)) && child != nullptr) {
            while (child != nullptr) {
                if (has_child) {
                    children << ",";
                }
                children << build_uia_node(child, walker, id, text_privacy, hidden, privacy);
                has_child = true;

                IUIAutomationElement* next = nullptr;
                walker->GetNextSiblingElement(child, &next);
                child->Release();
                child = next;
            }
        }
    }

    std::ostringstream ss;
    ss << "{\"type\":2,\"id\":" << node_id << ",\"tagName\":\"" << tag_name
       << "\",\"attributes\":{\"style\":\"" << json_escape(build_style(rect))
       << "\",\"data-uia-control-type\":\"" << control_type << "\"";
    if (key != 0) {
        ss << ",\"data-guance-hwnd\":\"" << key << "\"";
    }
    if (is_hidden) {
        ss << ",\"data-guance-hidden\":\"true\"";
    }
    ss << "},\"childNodes\":[" << children.str() << "]}";
    return ss.str();
}

std::string build_uia_window_node(
    HWND hwnd,
    int& id,
    const std::unordered_map<uintptr_t, guance_rum_session_replay_text_privacy>& text_privacy,
    const std::unordered_map<uintptr_t, bool>& hidden) {
    const HRESULT coinited = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool should_uninit = SUCCEEDED(coinited);
    if (FAILED(coinited) && coinited != RPC_E_CHANGED_MODE) {
        return {};
    }

    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&automation))) || automation == nullptr) {
        if (should_uninit) {
            CoUninitialize();
        }
        return {};
    }

    IUIAutomationElement* element = nullptr;
    IUIAutomationTreeWalker* walker = nullptr;
    std::string result;
    if (SUCCEEDED(automation->ElementFromHandle(hwnd, &element)) &&
        element != nullptr &&
        SUCCEEDED(automation->get_ControlViewWalker(&walker)) &&
        walker != nullptr) {
        result = build_uia_node(element, walker, id, text_privacy, hidden, GUANCE_RUM_REPLAY_TEXT_MASK_SENSITIVE_INPUTS);
    }

    if (walker != nullptr) {
        walker->Release();
    }
    if (element != nullptr) {
        element->Release();
    }
    automation->Release();
    if (should_uninit) {
        CoUninitialize();
    }
    return result;
}

std::string get_window_text(HWND hwnd) {
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) {
        return {};
    }
    std::wstring value(static_cast<std::size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, value.data(), length + 1);
    value.resize(static_cast<std::size_t>(length));
    return wide_to_utf8(value);
}

std::string get_class_name(HWND hwnd) {
    wchar_t buffer[256]{};
    const int length = GetClassNameW(hwnd, buffer, static_cast<int>(std::size(buffer)));
    return length <= 0 ? std::string{} : wide_to_utf8(std::wstring(buffer, static_cast<std::size_t>(length)));
}

std::string tag_for_window_class(const std::string& class_name) {
    if (contains_case_insensitive(class_name, "edit")) {
        return "input";
    }
    if (contains_case_insensitive(class_name, "button")) {
        return "button";
    }
    if (contains_case_insensitive(class_name, "combobox")) {
        return "select";
    }
    if (contains_case_insensitive(class_name, "static")) {
        return "span";
    }
    return "div";
}

bool is_sensitive_window(HWND hwnd, const std::string& class_name) {
    const auto style = static_cast<LONG_PTR>(GetWindowLongPtrW(hwnd, GWL_STYLE));
    return contains_case_insensitive(class_name, "edit") ||
           contains_case_insensitive(class_name, "combobox") ||
           ((style & ES_PASSWORD) == ES_PASSWORD);
}

std::string masked_text(const std::string& value) {
    if (value.empty()) {
        return {};
    }
    return std::string(std::min<std::size_t>(value.size(), 8), '*');
}

std::string capture_text(
    HWND hwnd,
    const std::string& class_name,
    guance_rum_session_replay_text_privacy privacy) {
    const auto text = get_window_text(hwnd);
    if (text.empty()) {
        return {};
    }
    if (privacy == GUANCE_RUM_REPLAY_TEXT_MASK_ALL) {
        return masked_text(text);
    }
    if (privacy == GUANCE_RUM_REPLAY_TEXT_MASK_ALL_INPUTS ||
        (privacy == GUANCE_RUM_REPLAY_TEXT_MASK_SENSITIVE_INPUTS && is_sensitive_window(hwnd, class_name))) {
        return masked_text(text);
    }
    return text;
}

std::string build_style(const RECT& rect) {
    std::ostringstream ss;
    ss << "position:absolute;left:" << rect.left << "px;top:" << rect.top
       << "px;width:" << std::max<LONG>(0, rect.right - rect.left)
       << "px;height:" << std::max<LONG>(0, rect.bottom - rect.top)
       << "px;box-sizing:border-box;overflow:hidden;";
    return ss.str();
}

std::string build_text_node(int& id, const std::string& text) {
    std::ostringstream ss;
    ss << "{\"type\":3,\"id\":" << id++ << ",\"textContent\":\"" << json_escape(text) << "\"}";
    return ss.str();
}

std::string build_hwnd_node(
    HWND hwnd,
    int& id,
    const std::unordered_map<uintptr_t, guance_rum_session_replay_text_privacy>& text_privacy,
    const std::unordered_map<uintptr_t, bool>& hidden,
    guance_rum_session_replay_text_privacy inherited_privacy) {
    RECT rect{};
    GetWindowRect(hwnd, &rect);
    const auto key = reinterpret_cast<uintptr_t>(hwnd);
    const auto privacy_it = text_privacy.find(key);
    const auto privacy = privacy_it == text_privacy.end() ? inherited_privacy : privacy_it->second;
    const bool is_hidden = hidden.find(key) != hidden.end() && hidden.at(key);
    const auto class_name = get_class_name(hwnd);
    const auto tag_name = tag_for_window_class(class_name);
    const auto text = is_hidden ? std::string{} : capture_text(hwnd, class_name, privacy);
    const int node_id = id++;

    std::ostringstream children;
    bool has_child = false;
    if (is_hidden) {
        children << build_text_node(id, "Hidden");
        has_child = true;
    } else {
        if (!text.empty()) {
            children << build_text_node(id, text);
            has_child = true;
        }

        for (HWND child = GetWindow(hwnd, GW_CHILD); child != nullptr; child = GetWindow(child, GW_HWNDNEXT)) {
            if (!IsWindowVisible(child)) {
                continue;
            }
            if (has_child) {
                children << ",";
            }
            children << build_hwnd_node(child, id, text_privacy, hidden, privacy);
            has_child = true;
        }
    }

    std::ostringstream ss;
    ss << "{\"type\":2,\"id\":" << node_id << ",\"tagName\":\"" << tag_name
       << "\",\"attributes\":{\"style\":\"" << json_escape(build_style(rect))
       << "\",\"data-guance-hwnd\":\"" << key
       << "\",\"data-guance-class\":\"" << json_escape(class_name) << "\"";
    if (is_hidden) {
        ss << ",\"data-guance-hidden\":\"true\"";
    }
    ss << "},\"childNodes\":[" << children.str() << "]}";
    return ss.str();
}

struct EnumWindowsContext {
    DWORD pid;
    std::vector<uintptr_t> windows;
};

BOOL CALLBACK collect_process_window(HWND hwnd, LPARAM lparam) {
    auto* context = reinterpret_cast<EnumWindowsContext*>(lparam);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == context->pid && IsWindowVisible(hwnd)) {
        context->windows.push_back(reinterpret_cast<uintptr_t>(hwnd));
    }
    return TRUE;
}

std::vector<uintptr_t> enumerate_process_windows() {
    EnumWindowsContext context{GetCurrentProcessId(), {}};

    EnumWindows(collect_process_window, reinterpret_cast<LPARAM>(&context));

    return context.windows;
}
#endif

} // namespace

Config from_c_config(const guance_sdk_config* c) {
    Config config;
    if (c == nullptr) {
        return config;
    }

    config.dataway_url = str_or_empty(c->dataway_url);
    config.datakit_url = str_or_empty(c->datakit_url);
    config.client_token = str_or_empty(c->client_token);
    config.rum_app_id = str_or_empty(c->rum_app_id);
    config.service_name = str_or_empty(c->service_name).empty() ? config.service_name : str_or_empty(c->service_name);
    config.env = str_or_empty(c->env).empty() ? config.env : str_or_empty(c->env);
    config.version = str_or_empty(c->version).empty() ? config.version : str_or_empty(c->version);
    config.sample_rate = std::clamp(c->sample_rate, 0.0, 1.0);
    config.session_error_sample_rate = std::clamp(c->session_error_sample_rate, 0.0, 1.0);
    config.session_replay_enabled = c->session_replay_enabled != 0;
    config.session_replay_sample_rate = std::clamp(c->session_replay_sample_rate, 0.0, 1.0);
    config.session_replay_on_error_sample_rate = std::clamp(c->session_replay_on_error_sample_rate, 0.0, 1.0);
    config.debug = c->debug != 0;
    config.cache_path = str_or_empty(c->cache_path);
    config.max_cache_bytes = c->max_cache_bytes <= 0 ? 128LL * 1024 * 1024 : c->max_cache_bytes;
    config.max_cache_files = c->max_cache_files <= 0 ? 1024 : c->max_cache_files;
    config.max_cache_age_seconds = c->max_cache_age_seconds <= 0
        ? 7LL * 24 * 60 * 60
        : c->max_cache_age_seconds;
    config.max_batch_items = c->max_batch_items <= 0 ? 50 : c->max_batch_items;
    config.max_batch_bytes = c->max_batch_bytes <= 0 ? 512LL * 1024 : c->max_batch_bytes;
    config.max_upload_bytes_per_second = c->max_upload_bytes_per_second < 0 ? 0 : c->max_upload_bytes_per_second;
    config.upload_burst_bytes = c->upload_burst_bytes <= 0 ? 2LL * 1024 * 1024 : c->upload_burst_bytes;
    config.max_upload_requests_per_second = c->max_upload_requests_per_second < 0
        ? 0.0
        : c->max_upload_requests_per_second;
    config.max_upload_batches_per_cycle = c->max_upload_batches_per_cycle <= 0
        ? 4
        : c->max_upload_batches_per_cycle;
    config.http_timeout_ms = c->http_timeout_ms <= 0 ? 10000 : c->http_timeout_ms;
    config.proxy_url = str_or_empty(c->proxy_url);
    config.session_replay_segment_record_limit = c->session_replay_segment_record_limit <= 0 ? 500 : c->session_replay_segment_record_limit;
    config.session_replay_segment_bytes_limit = c->session_replay_segment_bytes_limit <= 0 ? 1024 * 1024 : c->session_replay_segment_bytes_limit;
    return config;
}

RumCore::RumCore(Config config)
    : config_(std::move(config)),
      session_id_(uuid32()),
      trace_config_(default_trace_config(config_.service_name)) {
    const auto largest_upload = std::max(
        config_.max_batch_bytes,
        config_.session_replay_segment_bytes_limit + kReplayEnvelopeAllowanceBytes);
    const auto largest_cache_file = std::max(
        config_.max_batch_bytes,
        config_.session_replay_segment_bytes_limit + kReplayEnvelopeAllowanceBytes) + 256;
    if (config_.max_cache_bytes < largest_cache_file) {
        throw std::invalid_argument("max_cache_bytes must fit the largest configured batch file");
    }
    if (config_.max_upload_bytes_per_second > 0 && config_.upload_burst_bytes < largest_upload) {
        throw std::invalid_argument("upload_burst_bytes must fit the largest configured upload body");
    }
    session_sampled_ = hit_rate(config_.sample_rate);
    session_error_sampled_ = !session_sampled_ && hit_rate(config_.session_error_sample_rate);
    session_replay_sampled_ =
        config_.session_replay_enabled && hit_rate(config_.session_replay_sample_rate);
    session_replay_error_sampled_ =
        config_.session_replay_enabled && !session_replay_sampled_ &&
        hit_rate(config_.session_replay_on_error_sample_rate);
    session_replay_recording_ = session_replay_sampled_ || session_replay_error_sampled_;
    const auto cache_root = cache_root_path(config_.cache_path);
    cache_quota_ = std::make_shared<CacheQuota>(
        cache_root,
        config_.max_cache_bytes,
        config_.max_cache_files);
    queue_ = std::make_unique<QueueStore>(
        cache_root,
        "rum",
        QueueStreamKind::rum,
        cache_quota_,
        config_.max_batch_items,
        config_.max_batch_bytes,
        config_.max_cache_age_seconds);
    replay_queue_ = std::make_unique<QueueStore>(
        cache_root,
        "replay",
        QueueStreamKind::replay,
        cache_quota_,
        1,
        config_.session_replay_segment_bytes_limit + kReplayEnvelopeAllowanceBytes,
        config_.max_cache_age_seconds,
        false,
        true);
    log_queue_ = std::make_unique<QueueStore>(
        cache_root,
        "logs",
        QueueStreamKind::log,
        cache_quota_,
        config_.max_batch_items,
        config_.max_batch_bytes,
        config_.max_cache_age_seconds,
        true);
    cache_quota_->trim();
    upload_byte_tokens_ = static_cast<double>(config_.upload_burst_bytes);
    upload_request_tokens_ = std::max(1.0, config_.max_upload_requests_per_second);
    native_monitoring_ = std::make_unique<NativeMonitoring>(*this);
    action_timeout_thread_ = std::thread([this] { action_timeout_loop(); });
}

RumCore::~RumCore() {
    {
        std::lock_guard lock(mutex_);
        action_timeout_stopping_ = true;
    }
    action_timeout_cv_.notify_all();
    if (action_timeout_thread_.joinable()) {
        action_timeout_thread_.join();
    }
    disable_native_monitoring();
}

void RumCore::flush() {
    std::lock_guard upload_lock(upload_mutex_);
    {
        std::lock_guard lock(mutex_);
        flush_replay_pending_locked();
    }
    queue_->seal();
    log_queue_->seal();

    enum class UploadStream { rum, log, replay };
    static constexpr std::array<UploadStream, 7> schedule{
        UploadStream::rum,
        UploadStream::rum,
        UploadStream::rum,
        UploadStream::rum,
        UploadStream::log,
        UploadStream::log,
        UploadStream::replay,
    };

    int uploaded = 0;
    int probes = 0;
    while (uploaded < config_.max_upload_batches_per_cycle && probes < static_cast<int>(schedule.size()) * 2) {
        const auto stream = schedule[upload_schedule_cursor_++ % schedule.size()];
        ++probes;
        const auto stream_index = stream == UploadStream::rum
            ? std::size_t{0}
            : (stream == UploadStream::log ? std::size_t{1} : std::size_t{2});
        if (std::chrono::steady_clock::now() < upload_retry_not_before_[stream_index]) {
            continue;
        }

        QueueStore* store = nullptr;
        if (stream == UploadStream::rum) {
            store = queue_.get();
        } else if (stream == UploadStream::log) {
            std::lock_guard log_lock(log_mutex_);
            if (log_config_.enable_custom_log) store = log_queue_.get();
        } else {
            store = replay_queue_.get();
        }
        if (store == nullptr) continue;

        auto batch = store->acquire();
        if (!batch) continue;

        std::string replay_content_type;
        std::string replay_body;
        auto upload_bytes = batch.payload_bytes;
        if (stream == UploadStream::replay) {
            const auto separator = batch.lines.front().find('\n');
            if (separator == std::string::npos) {
                store->complete(batch.lease_id);
                continue;
            }
            replay_content_type = batch.lines.front().substr(0, separator);
            replay_body = batch.lines.front().substr(separator + 1);
            upload_bytes = static_cast<int64_t>(replay_body.size());
        }

        if (!consume_upload_budget(upload_bytes)) {
            store->abandon(batch.lease_id);
            break;
        }

        TransportResult result;
        if (stream == UploadStream::rum) {
            if (config_.debug) {
                std::cout << "[Guance.RUM.Native] uploading RUM batch count="
                          << batch.lines.size() << std::endl;
            }
            result = send_to_dataway(config_, batch.lines);
            record_rum_transport_result(result.delete_from_queue, result.retry_later, result.status_code, result.error_code, result.latency_ms);
        } else if (stream == UploadStream::log) {
            result = send_logging_to_dataway(config_, batch.lines);
            record_log_transport_result(result.delete_from_queue, result.retry_later, result.status_code, result.error_code, result.latency_ms);
        } else {
            result = send_session_replay_to_dataway(config_, replay_content_type, replay_body);
            record_replay_transport_result(result.delete_from_queue, result.retry_later, result.status_code, result.error_code, result.latency_ms);
        }

        ++uploaded;
        if (result.retry_later) {
            const auto attempt = std::min(upload_retry_attempts_[stream_index]++, 6);
            const auto backoff_ms = std::max<int64_t>(
                result.retry_after_ms,
                1000LL * (1LL << attempt));
            upload_retry_not_before_[stream_index] =
                std::chrono::steady_clock::now() + std::chrono::milliseconds(backoff_ms);
        } else {
            upload_retry_attempts_[stream_index] = 0;
            upload_retry_not_before_[stream_index] = {};
        }
        if (result.delete_from_queue) {
            store->complete(batch.lease_id);
        } else {
            store->abandon(batch.lease_id);
        }
        if (result.retry_later) break;
    }
}

void RumCore::shutdown() {
    disable_native_monitoring();
    {
        std::lock_guard lock(mutex_);
        close_current_action_locked(monotonic_time_nanoseconds());
    }
    action_timeout_cv_.notify_all();
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native] shutdown stopping active view" << std::endl;
    }
    stop_view();
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native] shutdown flushing queues" << std::endl;
    }
    flush();
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native] shutdown flush completed" << std::endl;
    }
}

bool RumCore::enable_native_monitoring(const guance_sdk_native_monitoring_config& config) {
    const bool enabled = native_monitoring_ && native_monitoring_->enable(config);
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native.Monitoring] enable "
                  << (enabled ? "completed" : "rejected") << std::endl;
    }
    return enabled;
}

void RumCore::disable_native_monitoring() {
    if (native_monitoring_) {
        native_monitoring_->disable();
    }
}

NativeDiagnostics RumCore::diagnostics() const {
    std::lock_guard lock(mutex_);
    NativeDiagnostics snapshot;
    snapshot.rum_events_enqueued = rum_events_enqueued_.load();
    snapshot.rum_upload_success_count = rum_upload_success_count_.load();
    snapshot.rum_upload_retry_count = rum_upload_retry_count_.load();
    snapshot.rum_upload_terminal_failure_count = rum_upload_terminal_failure_count_.load();
    snapshot.replay_upload_success_count = replay_upload_success_count_.load();
    snapshot.replay_upload_retry_count = replay_upload_retry_count_.load();
    snapshot.replay_upload_terminal_failure_count = replay_upload_terminal_failure_count_.load();
    snapshot.last_rum_upload_status_code = last_rum_upload_status_code_.load();
    snapshot.last_replay_upload_status_code = last_replay_upload_status_code_.load();
    snapshot.last_rum_upload_error_code = last_rum_upload_error_code_.load();
    snapshot.last_replay_upload_error_code = last_replay_upload_error_code_.load();
    snapshot.last_rum_upload_latency_ms = last_rum_upload_latency_ms_.load();
    snapshot.last_replay_upload_latency_ms = last_replay_upload_latency_ms_.load();
    snapshot.cache_allocated_bytes = cache_quota_->allocated_bytes();
    snapshot.cache_file_count = cache_quota_->file_count();
    snapshot.session_sampled = session_sampled_;
    snapshot.session_error_sampled = session_error_sampled_;
    snapshot.session_replay_sampled = session_replay_sampled_;
    snapshot.session_replay_error_sampled = session_replay_error_sampled_;
    return snapshot;
}

NativeLogDiagnostics RumCore::log_diagnostics() const {
    NativeLogDiagnostics snapshot;
    snapshot.logs_enqueued = logs_enqueued_.load();
    snapshot.logs_dropped = logs_dropped_.load();
    snapshot.upload_success_count = log_upload_success_count_.load();
    snapshot.upload_retry_count = log_upload_retry_count_.load();
    snapshot.upload_terminal_failure_count = log_upload_terminal_failure_count_.load();
    snapshot.last_upload_status_code = last_log_upload_status_code_.load();
    snapshot.last_upload_error_code = last_log_upload_error_code_.load();
    snapshot.last_upload_latency_ms = last_log_upload_latency_ms_.load();
    return snapshot;
}

bool RumCore::write_line(const char* line, std::size_t length) {
    const auto measurement = bridge_measurement(line, length);
    if (!measurement) {
        return false;
    }
    RumEvent event;
    if (!parse_line_protocol(std::string_view(line, length), event) ||
        event.measurement != *measurement) {
        return false;
    }
    if (!sampled_for(event.measurement)) {
        return true;
    }

    event.tags["app_id"] = config_.rum_app_id;
    event.tags["service"] = config_.service_name;
    event.tags["env"] = config_.env;
    event.tags["version"] = config_.version;
    event.tags["session_id"] = session_id_;
    event.tags["sdk_name"] = kWindowsSdkName;
    event.tags["sdk_version"] = kWindowsSdkVersion;
    event.fields["session_sample_rate"] = config_.sample_rate;
    event.fields["session_on_error_sample_rate"] = config_.session_error_sample_rate;
    {
        std::lock_guard lock(mutex_);
        event.fields["session_has_replay"] = session_replay_sampled_;
        for (const auto& [key, value] : global_context_) {
            event.tags[key] = value;
        }
        for (const auto& [key, value] : rum_context_) {
            event.tags[key] = value;
        }
        for (const auto& [key, value] : user_tags_) {
            event.tags[key] = value;
        }
    }
    apply_modifiers(event);
    const auto modified_line = format_line_protocol(event);
    const bool persisted = queue_->enqueue(modified_line);
    if (!persisted) return false;
    rum_events_enqueued_.fetch_add(1);
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native.BrowserBridge] enqueued "
                  << *measurement
                  << " (" << modified_line.size() << " bytes)"
                  << std::endl;
    }
    return true;
}

void RumCore::set_user(const char* id, const char* name, const char* email) {
    std::lock_guard lock(mutex_);
    user_tags_.clear();
    user_tags_["is_signin"] = "true";
    user_tags_["userid"] = str_or_empty(id);
    user_tags_["user_name"] = str_or_empty(name);
    user_tags_["user_email"] = str_or_empty(email);
}

void RumCore::clear_user() {
    std::lock_guard lock(mutex_);
    user_tags_.clear();
}

void RumCore::add_global_context(const char* key, const char* value) {
    std::lock_guard lock(mutex_);
    global_context_[str_or_empty(key)] = str_or_empty(value);
}

void RumCore::add_rum_context(const char* key, const char* value) {
    std::lock_guard lock(mutex_);
    rum_context_[str_or_empty(key)] = str_or_empty(value);
}

void RumCore::start_view(const char* name) {
    std::optional<View> previous;
    bool action_closed = false;
    {
        std::lock_guard lock(mutex_);
        action_closed = close_current_action_locked(monotonic_time_nanoseconds());
        if (active_view_) {
            flush_replay_pending_locked();
        } else {
            replay_pending_records_.clear();
            replay_pending_view_id_.clear();
            replay_pending_has_full_snapshot_ = false;
            replay_pending_creation_reason_ = "incremental";
            replay_pending_start_ms_ = 0;
            replay_pending_end_ms_ = 0;
            replay_pending_bytes_ = 0;
        }
        previous = active_view_;
        active_view_ = View{uuid32(), str_or_empty(name), previous ? previous->name : std::string{}, unix_time_nanoseconds(), monotonic_time_nanoseconds()};
        replay_index_in_view_ = 0;
        if (session_replay_recording_) {
            capture_session_replay_snapshot();
        }
    }
    if (action_closed) {
        action_timeout_cv_.notify_all();
    }
    if (previous) {
        RumEvent event = base_event("view", previous->started_ns);
        event.tags["view_id"] = previous->id;
        event.tags["view_name"] = previous->name;
        event.tags["view_referrer"] = previous->referrer;
        event.fields["time_spent"] = elapsed_since(previous->started_monotonic_ns);
        event.fields["is_active"] = false;
        event.fields["view_action_count"] = static_cast<int64_t>(previous->action_count);
        event.fields["view_resource_count"] = static_cast<int64_t>(previous->resource_count);
        event.fields["view_error_count"] = static_cast<int64_t>(previous->error_count);
        event.fields["view_long_task_count"] = static_cast<int64_t>(previous->long_task_count);
        enqueue(std::move(event));
    }
}

void RumCore::stop_view() {
    std::optional<View> view;
    bool action_closed = false;
    {
        std::lock_guard lock(mutex_);
        action_closed = close_current_action_locked(monotonic_time_nanoseconds());
        flush_replay_pending_locked();
        view = active_view_;
        active_view_.reset();
    }
    if (action_closed) {
        action_timeout_cv_.notify_all();
    }
    if (!view) {
        return;
    }
    RumEvent event = base_event("view", view->started_ns);
    event.tags["view_id"] = view->id;
    event.tags["view_name"] = view->name;
    event.tags["view_referrer"] = view->referrer;
    event.fields["time_spent"] = elapsed_since(view->started_monotonic_ns);
    event.fields["is_active"] = false;
    event.fields["view_action_count"] = static_cast<int64_t>(view->action_count);
    event.fields["view_resource_count"] = static_cast<int64_t>(view->resource_count);
    event.fields["view_error_count"] = static_cast<int64_t>(view->error_count);
    event.fields["view_long_task_count"] = static_cast<int64_t>(view->long_task_count);
    enqueue(std::move(event));
}

void RumCore::add_action(const char* name, const char* type, int64_t duration_ns) {
    std::lock_guard lock(mutex_);
    const auto safe_duration_ns = non_negative_duration(duration_ns);
    Action action{uuid32(), str_or_empty(name), str_or_empty(type), {}, {}, {}, unix_time_before(safe_duration_ns), monotonic_time_nanoseconds()};
    if (active_view_) {
        action.view_id = active_view_->id;
        action.view_name = active_view_->name;
        action.view_referrer = active_view_->referrer;
    }
    track_action(action, safe_duration_ns);
}

void RumCore::add_launch_action(
    const guance_rum_launch& launch,
    const char* view_id,
    const char* view_name,
    const char* view_referrer) {
    std::lock_guard lock(mutex_);
    const auto safe_duration_ns = non_negative_duration(launch.duration_ns);
    const auto is_hot = launch.type == GUANCE_RUM_LAUNCH_HOT;
    Action action{
        uuid32(),
        is_hot ? "app hot start" : "app cold start",
        is_hot ? "launch_hot" : "launch_cold",
        {},
        {},
        {},
        launch.start_time_ns > 0
            ? launch.start_time_ns
            : unix_time_before(safe_duration_ns),
        monotonic_time_nanoseconds()};
    const auto explicit_view_id = str_or_empty(view_id);
    if (!explicit_view_id.empty()) {
        action.view_id = explicit_view_id;
        action.view_name = str_or_empty(view_name);
        action.view_referrer = str_or_empty(view_referrer);
    } else if (active_view_) {
        action.view_id = active_view_->id;
        action.view_name = active_view_->name;
        action.view_referrer = active_view_->referrer;
    }
    if (!is_hot) {
        const auto pre_application =
            non_negative_duration(launch.pre_application_duration_ns);
        const auto application =
            non_negative_duration(launch.application_duration_ns);
        const auto first_frame =
            non_negative_duration(launch.first_frame_duration_ns);
        const auto application_start =
            pre_application > std::numeric_limits<int64_t>::max()
                ? std::numeric_limits<int64_t>::max()
                : pre_application;
        const auto first_frame_start =
            application > std::numeric_limits<int64_t>::max() - application_start
                ? std::numeric_limits<int64_t>::max()
                : application_start + application;
        action.fields["app_pre_application_init_time"] =
            "{\"start\":0,\"duration\":" + std::to_string(pre_application) + "}";
        action.fields["app_application_init_time"] =
            "{\"start\":" + std::to_string(application_start) +
            ",\"duration\":" + std::to_string(application) + "}";
        action.fields["app_first_frame_init_time"] =
            "{\"start\":" + std::to_string(first_frame_start) +
            ",\"duration\":" + std::to_string(first_frame) + "}";
    }
    track_action(action, safe_duration_ns);
}

std::string RumCore::start_action(const char* name, const char* type, bool need_wait) {
    std::lock_guard lock(mutex_);
    const auto now_monotonic_ns = monotonic_time_nanoseconds();
    close_current_action_if_needed_locked(now_monotonic_ns, true);
    if (current_action_locked()) {
        return {};
    }

    Action action{uuid32(), str_or_empty(name), str_or_empty(type), {}, {}, {}, unix_time_nanoseconds(), now_monotonic_ns, need_wait};
    if (active_view_) {
        action.view_id = active_view_->id;
        action.view_name = active_view_->name;
        action.view_referrer = active_view_->referrer;
    }
    const auto id = action.id;
    active_actions_[id] = std::move(action);
    action_timeout_cv_.notify_all();
    return id;
}

void RumCore::stop_action(const char* action_id) {
    Action action;
    {
        std::lock_guard lock(mutex_);
        const auto it = active_actions_.find(str_or_empty(action_id));
        if (it == active_actions_.end()) {
            return;
        }
        if (!it->second.need_wait) {
            return;
        }
        action = it->second;
        active_actions_.erase(it);
        track_action(action, elapsed_since(action.started_monotonic_ns));
    }
    action_timeout_cv_.notify_all();
}

bool RumCore::close_current_action_if_needed_locked(
    int64_t now_monotonic_ns,
    bool allow_normal_timeout) {
    const auto current = current_action_locked();
    if (!current) {
        return false;
    }

    const auto elapsed = now_monotonic_ns <= current->started_monotonic_ns
        ? 0
        : now_monotonic_ns - current->started_monotonic_ns;
    const bool max_timeout = elapsed >= kActionMaxDurationNanoseconds;
    const bool normal_timeout = allow_normal_timeout &&
        !current->need_wait &&
        elapsed >= kActionFrequentProtectionNanoseconds;
    if (!max_timeout && !normal_timeout) {
        return false;
    }

    return close_current_action_locked(now_monotonic_ns);
}

bool RumCore::close_current_action_locked(int64_t now_monotonic_ns) {
    const auto current = current_action_locked();
    if (!current) {
        return false;
    }

    const auto it = active_actions_.find(current->id);
    if (it == active_actions_.end()) {
        return false;
    }
    const auto action = it->second;
    active_actions_.erase(it);
    const auto elapsed = now_monotonic_ns <= action.started_monotonic_ns
        ? 0
        : now_monotonic_ns - action.started_monotonic_ns;
    track_action(action, elapsed);
    return true;
}

void RumCore::action_timeout_loop() {
    std::unique_lock lock(mutex_);
    while (!action_timeout_stopping_) {
        const auto current = current_action_locked();
        if (!current) {
            action_timeout_cv_.wait(lock, [this] {
                return action_timeout_stopping_ || !active_actions_.empty();
            });
            continue;
        }

        const auto elapsed = elapsed_since(current->started_monotonic_ns);
        if (elapsed >= kActionMaxDurationNanoseconds) {
            close_current_action_if_needed_locked(monotonic_time_nanoseconds(), false);
            continue;
        }

        action_timeout_cv_.wait_for(
            lock,
            std::chrono::nanoseconds(kActionMaxDurationNanoseconds - elapsed));
    }
}

void RumCore::track_action(const Action& action, int64_t duration_ns) {
    RumEvent event = base_event("action", action.started_ns);
    event.tags["action_id"] = action.id;
    event.tags["action_name"] = action.name;
    event.tags["action_type"] = action.type;
    event.tags["view_id"] = action.view_id;
    event.tags["view_name"] = action.view_name;
    event.tags["view_referrer"] = action.view_referrer;
    event.fields["duration"] = non_negative_duration(duration_ns);
    event.fields["action_resource_count"] = static_cast<int64_t>(action.resource_count);
    event.fields["action_error_count"] = static_cast<int64_t>(action.error_count);
    event.fields["action_long_task_count"] = static_cast<int64_t>(action.long_task_count);
    for (const auto& [key, value] : action.fields) {
        event.fields[key] = value;
    }
    if (active_view_ && active_view_->id == action.view_id) {
        active_view_->action_count++;
    }
    enqueue(std::move(event));
}

std::optional<RumCore::Action> RumCore::current_action_locked() const {
    std::optional<Action> current;
    for (const auto& [_, action] : active_actions_) {
        if (!current || action.started_ns > current->started_ns) {
            current = action;
        }
    }
    return current;
}

std::string RumCore::start_resource(const char* url, const char* method) {
    return start_resource_impl(url, method, false);
}

std::string RumCore::start_auto_resource(const char* url, const char* method) {
    return start_resource_impl(url, method, true);
}

std::string RumCore::start_resource_impl(
    const char* url,
    const char* method,
    bool automatic) {
    const std::string original_url = str_or_empty(url);
    const std::string resource_method = str_or_empty(method);
    ResourceCollectionConfig collection_config;
    {
        std::lock_guard lock(mutex_);
        collection_config = resource_collection_config_;
    }
    if (automatic &&
        !should_collect_resource(collection_config, original_url, resource_method)) {
        return {};
    }

    Resource resource{
        uuid32(),
        original_url,
        resource_method,
        {},
        {},
        {},
        {},
        unix_time_nanoseconds(),
        monotonic_time_nanoseconds()
    };

    std::lock_guard lock(mutex_);
    if (active_view_) {
        resource.view_id = active_view_->id;
        resource.view_name = active_view_->name;
    }
    if (const auto action = current_action_locked()) {
        resource.action_id = action->id;
        resource.action_name = action->name;
    }
    const auto id = resource.id;
    resources_[id] = resource;
    return id;
}

bool RumCore::configure_resource_collection(
    const guance_rum_resource_collection_config& config) {
    ResourceCollectionConfig parsed;
    if (!resource_collection_config_from_c(config, parsed)) {
        return false;
    }

    {
        std::lock_guard lock(mutex_);
        resource_collection_config_ = parsed;
    }
    {
        std::unique_lock lock(data_modifier_mutex_);
        modifier_privacy_config_ = std::move(parsed);
    }
    return true;
}

bool RumCore::configure_data_modifiers(const guance_data_modifier_config& config) {
    DataModifierConfig parsed;
    if (!data_modifier_config_from_c(config, parsed)) {
        return false;
    }
    std::unique_lock lock(data_modifier_mutex_);
    data_modifier_config_ = parsed;
    return true;
}

bool RumCore::configure_trace(const guance_trace_config& config) {
    TraceConfig parsed;
    if (!trace_config_from_c(config, config_.service_name, parsed)) {
        return false;
    }

    std::lock_guard lock(mutex_);
    trace_config_ = std::move(parsed);
    return true;
}

bool RumCore::configure_logging(const guance_log_config& config) {
    constexpr uint32_t known_level_mask =
        GUANCE_LOG_DEBUG |
        GUANCE_LOG_INFO |
        GUANCE_LOG_WARNING |
        GUANCE_LOG_ERROR |
        GUANCE_LOG_CRITICAL |
        GUANCE_LOG_OK;
    if (config.struct_size < sizeof(guance_log_config) ||
        config.version != GUANCE_LOG_CONFIG_VERSION ||
        config.sample_rate < 0.0 ||
        config.sample_rate > 1.0 ||
        (config.level_filter_mask & ~known_level_mask) != 0 ||
        (config.global_context_count > 0 && config.global_context == nullptr) ||
        config.global_context_count > 1024 ||
        (config.discard_strategy != GUANCE_LOG_DISCARD_NEW &&
         config.discard_strategy != GUANCE_LOG_DISCARD_OLDEST)) {
        return false;
    }

    LogConfig parsed;
    parsed.enable_custom_log = config.enable_custom_log != 0;
    parsed.enable_link_rum_data = config.enable_link_rum_data != 0;
    parsed.sample_rate = config.sample_rate;
    parsed.level_filter_mask = config.level_filter_mask;
    parsed.discard_strategy = config.discard_strategy;
    for (uint32_t index = 0; index < config.global_context_count; ++index) {
        const auto& property = config.global_context[index];
        const auto key = str_or_empty(property.key);
        if (!key.empty()) {
            parsed.global_context[key] = str_or_empty(property.value);
        }
    }

    std::lock_guard lock(log_mutex_);
    log_config_ = std::move(parsed);
    log_queue_->set_discard_new(log_config_.discard_strategy == GUANCE_LOG_DISCARD_NEW);
    return true;
}

bool RumCore::add_log(
    const char* content,
    const char* status,
    const guance_log_property* properties,
    uint32_t property_count) {
    if (content == nullptr || status == nullptr ||
        (property_count > 0 && properties == nullptr) ||
        property_count > 1024) {
        logs_dropped_.fetch_add(1);
        return false;
    }

    std::lock_guard log_lock(log_mutex_);
    const auto normalized_status = normalize_log_status(status);
    const auto status_level = log_level_mask(normalized_status);
    if (!log_config_.enable_custom_log ||
        normalized_status.empty() ||
        !hit_rate(log_config_.sample_rate) ||
        (log_config_.level_filter_mask != 0 &&
         (status_level == 0 || (log_config_.level_filter_mask & status_level) == 0))) {
        logs_dropped_.fetch_add(1);
        return false;
    }

    RumEvent event{kWindowsLogSource, {}, {}, unix_time_nanoseconds()};
    event.tags["app_id"] = config_.rum_app_id;
    event.tags["service"] = config_.service_name;
    event.tags["env"] = config_.env;
    event.tags["version"] = config_.version;
    event.tags["sdk_name"] = kWindowsSdkName;
    event.tags["sdk_version"] = kWindowsSdkVersion;
    {
        std::lock_guard state_lock(mutex_);
        for (const auto& [key, value] : global_context_) {
            event.tags.emplace(key, value);
        }
        for (const auto& [key, value] : log_config_.global_context) {
            event.tags.emplace(key, value);
        }
        for (const auto& [key, value] : user_tags_) {
            event.tags.emplace(key, value);
        }
        if (user_tags_.empty()) {
            event.tags["is_signin"] = "F";
        }
        if (log_config_.enable_link_rum_data) {
            event.tags["session_id"] = session_id_;
            event.tags["session_type"] = "user";
            if (user_tags_.find("userid") == user_tags_.end()) {
                event.tags["userid"] = session_id_;
            }
            if (active_view_) {
                event.tags["view_id"] = active_view_->id;
                event.tags["view_name"] = active_view_->name;
                event.tags["view_referrer"] = active_view_->referrer;
            }
            if (const auto action = current_action_locked()) {
                event.tags["action_id"] = action->id;
                event.tags["action_name"] = action->name;
            }
        }
    }

    for (uint32_t index = 0; index < property_count; ++index) {
        const auto key = str_or_empty(properties[index].key);
        if (!key.empty()) {
            event.fields[key] = str_or_empty(properties[index].value);
        }
    }
    event.fields["message"] = truncate_log_content(content);
    event.fields["status"] = normalized_status;

    apply_modifiers(event);
    if (!log_queue_->enqueue(format_line_protocol(event))) {
        logs_dropped_.fetch_add(1);
        return false;
    }
    logs_enqueued_.fetch_add(1);
    return true;
}

std::optional<TraceContext> RumCore::create_trace_context(
    const char* url,
    const char* method) const {
    TraceConfig trace_config;
    {
        std::lock_guard lock(mutex_);
        trace_config = trace_config_;
    }
    return guance::rum::create_trace_context(
        trace_config,
        str_or_empty(url),
        str_or_empty(method));
}

void RumCore::stop_resource(const char* resource_id, int status_code, int64_t response_size) {
    stop_resource_ext(resource_id, status_code, response_size, -1, nullptr, nullptr, nullptr, nullptr);
}

void RumCore::stop_resource_ext(const char* resource_id, int status_code, int64_t response_size, int64_t request_size, const char* resource_type, const char* trace_id, const char* span_id, const char* http_protocol, const char* request_header, const char* response_header) {
    Resource resource;
    {
        std::lock_guard lock(mutex_);
        const auto it = resources_.find(str_or_empty(resource_id));
        if (it == resources_.end()) {
            return;
        }
        resource = it->second;
        resources_.erase(it);
        if (active_view_ && active_view_->id == resource.view_id) {
            active_view_->resource_count++;
        }
        if (!resource.action_id.empty()) {
            const auto action = active_actions_.find(resource.action_id);
            if (action != active_actions_.end()) {
                action->second.resource_count++;
            }
        }
        close_current_action_if_needed_locked(monotonic_time_nanoseconds(), true);
    }

    RumEvent event = base_event("resource", resource.started_ns);
    const auto path = path_from_url(resource.url);
    event.tags["resource_id"] = resource.id;
    event.tags["resource_url"] = resource.url;
    event.tags["resource_url_host"] = host_from_url(resource.url);
    event.tags["resource_url_path"] = path;
    event.tags["resource_url_path_group"] = grouped_path(path);
    event.tags["resource_method"] = resource.method;
    event.tags["resource_status"] = std::to_string(status_code);
    event.tags["resource_status_group"] = status_group(status_code);
    event.tags["view_id"] = resource.view_id;
    event.tags["view_name"] = resource.view_name;
    event.tags["action_id"] = resource.action_id;
    event.tags["action_name"] = resource.action_name;
    event.tags["resource_type"] = str_or_empty(resource_type);
    event.tags["trace_id"] = str_or_empty(trace_id);
    event.tags["span_id"] = str_or_empty(span_id);
    event.tags["resource_http_protocol"] = str_or_empty(http_protocol);
    event.fields["duration"] = elapsed_since(resource.started_monotonic_ns);
    event.fields["request_header"] = str_or_empty(request_header);
    event.fields["response_header"] = str_or_empty(response_header);
    if (response_size >= 0) {
        event.fields["resource_size"] = response_size;
    }
    if (request_size >= 0) {
        event.fields["resource_request_size"] = request_size;
    }
    enqueue(std::move(event));
}

void RumCore::add_error(const char* stack, const char* message, const char* error_type, const char* source) {
    std::lock_guard lock(mutex_);
    RumEvent event = base_event("error", unix_time_nanoseconds());
    event.tags["error_type"] = str_or_empty(error_type);
    event.tags["error_source"] = str_or_empty(source).empty() ? "logger" : str_or_empty(source);
    event.tags["error_situation"] = "run";
    if (active_view_) {
        event.tags["view_id"] = active_view_->id;
        event.tags["view_name"] = active_view_->name;
        event.tags["view_referrer"] = active_view_->referrer;
        active_view_->error_count++;
    }
    if (const auto action = current_action_locked()) {
        event.tags["action_id"] = action->id;
        event.tags["action_name"] = action->name;
        const auto it = active_actions_.find(action->id);
        if (it != active_actions_.end()) {
            it->second.error_count++;
        }
    }
    event.fields["error_message"] = str_or_empty(message);
    event.fields["error_stack"] = str_or_empty(stack);
    enqueue(std::move(event));
    close_current_action_if_needed_locked(monotonic_time_nanoseconds(), true);
    if (!session_replay_sampled_ && config_.session_replay_enabled && session_replay_error_sampled_) {
        flush_replay_pending_locked();
        session_replay_sampled_ = true;
        session_replay_recording_ = true;
        for (const auto& segment : replay_error_buffer_) {
            replay_queue_->enqueue(segment.content_type + "\n" + segment.body);
        }
        replay_error_buffer_.clear();
        capture_session_replay_snapshot();
    }
}

void RumCore::add_long_task(int64_t duration_ns, const char* stack) {
    std::lock_guard lock(mutex_);
    const auto safe_duration_ns = non_negative_duration(duration_ns);
    RumEvent event = base_event("long_task", unix_time_before(safe_duration_ns));
    if (active_view_) {
        event.tags["view_id"] = active_view_->id;
        event.tags["view_name"] = active_view_->name;
        event.tags["view_referrer"] = active_view_->referrer;
        active_view_->long_task_count++;
    }
    if (const auto action = current_action_locked()) {
        event.tags["action_id"] = action->id;
        event.tags["action_name"] = action->name;
        const auto it = active_actions_.find(action->id);
        if (it != active_actions_.end()) {
            it->second.long_task_count++;
        }
    }
    event.fields["duration"] = safe_duration_ns;
    event.fields["long_task_stack"] = str_or_empty(stack);
    enqueue(std::move(event));
    close_current_action_if_needed_locked(monotonic_time_nanoseconds(), true);
}

void RumCore::add_ui_hang_event(const HangEvent& hang, const std::string& stack) {
    std::lock_guard lock(mutex_);
    const auto duration_ns = non_negative_duration(hang.duration_ms) * 1'000'000;
    const auto threshold_ns = non_negative_duration(hang.threshold_ms) * 1'000'000;

    if (hang.kind == HangEventKind::LongTask) {
        if (config_.debug) {
            std::cout << "[Guance.RUM.Native.Monitoring] UI recovered incident="
                      << hang.incident_id << " kind=long_task duration_ms="
                      << hang.duration_ms << std::endl;
        }
        RumEvent event = base_event("long_task", unix_time_before(duration_ns));
        event.tags["long_task_detection"] = "ui_watchdog";
        event.tags["hang_id"] = hang.incident_id;
        if (active_view_) {
            event.tags["view_id"] = active_view_->id;
            event.tags["view_name"] = active_view_->name;
            event.tags["view_referrer"] = active_view_->referrer;
            active_view_->long_task_count++;
        }
        if (const auto action = current_action_locked()) {
            event.tags["action_id"] = action->id;
            event.tags["action_name"] = action->name;
            const auto it = active_actions_.find(action->id);
            if (it != active_actions_.end()) {
                it->second.long_task_count++;
            }
        }
        event.fields["duration"] = duration_ns;
        event.fields["long_task_stack"] = stack;
        event.fields["long_task_threshold"] = threshold_ns;
        enqueue(std::move(event));
        close_current_action_if_needed_locked(monotonic_time_nanoseconds(), true);
        return;
    }

    if (config_.debug) {
        std::cout << "[Guance.RUM.Native.Monitoring] UI recovered incident="
                  << hang.incident_id << " kind=hang duration_ms="
                  << hang.duration_ms << std::endl;
    }
    RumEvent event = base_event("error", unix_time_before(duration_ns));
    event.tags["error_type"] = "anr_error";
    event.tags["error_source"] = "logger";
    event.tags["error_situation"] = "run";
    event.tags["hang_id"] = hang.incident_id;
    if (active_view_) {
        event.tags["view_id"] = active_view_->id;
        event.tags["view_name"] = active_view_->name;
        event.tags["view_referrer"] = active_view_->referrer;
        active_view_->error_count++;
    }
    if (const auto action = current_action_locked()) {
        event.tags["action_id"] = action->id;
        event.tags["action_name"] = action->name;
        const auto it = active_actions_.find(action->id);
        if (it != active_actions_.end()) {
            it->second.error_count++;
        }
    }
    event.fields["error_message"] = std::string{"UI thread was unresponsive for "} +
        std::to_string(hang.duration_ms) + " ms";
    event.fields["error_stack"] = stack;
    event.fields["duration"] = duration_ns;
    event.fields["hang_threshold"] = threshold_ns;
    enqueue(std::move(event));
    close_current_action_if_needed_locked(monotonic_time_nanoseconds(), true);
}

bool RumCore::add_recovered_crash(
    const CrashEnvelope& crash,
    const std::filesystem::path& minidump_path) {
    std::lock_guard lock(mutex_);
    RumEvent event = base_event("error", crash.timestamp_ns);
    const bool cpp_terminate = (crash.flags & CrashEnvelopeCppTerminate) != 0;
    event.tags["error_type"] = "native_crash";
    event.tags["error_source"] = "logger";
    event.tags["error_situation"] = "startup";

    std::ostringstream message;
    if (cpp_terminate) {
        message << "std::terminate was invoked";
    } else {
        message << "Unhandled native exception 0x"
                << std::hex << std::uppercase << crash.exception_code_value;
        if (crash.exception_address != 0) {
            message << " at 0x" << crash.exception_address;
        }
    }
    event.fields["error_message"] = message.str();

    std::ostringstream stack;
    const auto instruction_pointer = crash.instruction_pointer != 0
        ? crash.instruction_pointer
        : crash.exception_address;
    if (instruction_pointer != 0) {
        stack << "0x" << std::hex << instruction_pointer;
    }
    event.fields["error_stack"] = stack.str();
    const bool has_minidump =
        (crash.flags & CrashEnvelopeHasMinidump) != 0 && !minidump_path.empty();
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native.Monitoring] recovered previous-run crash type="
                  << "native_crash"
                  << " error_stack=" << (stack.str().empty() ? "<empty>" : stack.str())
                  << " minidump=" << (has_minidump ? "true" : "false")
                  << std::endl;
    }
    return enqueue(std::move(event));
}

std::filesystem::path RumCore::default_native_crash_path() const {
    return cache_root_path(config_.cache_path) / "crashes";
}

void RumCore::log_native_monitoring(const std::string& message) const {
    if (config_.debug) {
        std::cout << "[Guance.RUM.Native.Monitoring] " << message << std::endl;
    }
}

void RumCore::start_session_replay() {
    std::lock_guard lock(mutex_);
    if (!config_.session_replay_enabled) {
        return;
    }
    session_replay_sampled_ = true;
    session_replay_recording_ = true;
    capture_session_replay_snapshot();
}

void RumCore::stop_session_replay() {
    std::lock_guard lock(mutex_);
    flush_replay_pending_locked();
    session_replay_recording_ = false;
}

void RumCore::register_replay_window(uintptr_t hwnd) {
    if (hwnd == 0) {
        return;
    }
    std::lock_guard lock(mutex_);
    if (std::find(replay_windows_.begin(), replay_windows_.end(), hwnd) == replay_windows_.end()) {
        replay_windows_.push_back(hwnd);
    }
    if (session_replay_recording_) {
        capture_session_replay_snapshot();
    }
}

void RumCore::capture_replay_click(uintptr_t hwnd, const char* target, double x, double y) {
    std::lock_guard lock(mutex_);
    const auto now = unix_time_milliseconds();
    const bool hidden = hwnd != 0 && replay_hidden_.find(hwnd) != replay_hidden_.end() && replay_hidden_.at(hwnd);
    const bool hide_touch = hwnd != 0 &&
                            replay_touch_privacy_.find(hwnd) != replay_touch_privacy_.end() &&
                            replay_touch_privacy_.at(hwnd) == GUANCE_RUM_REPLAY_TOUCH_HIDE;
    const auto safe_target = hidden || hide_touch ? std::string{"hidden"} : str_or_empty(target);
    if (hidden || hide_touch) {
        x = 0;
        y = 0;
    }

    std::ostringstream record;
    record << "{\"type\":11,\"timestamp\":" << now
           << ",\"data\":{\"source\":2,\"target\":\"" << json_escape(safe_target)
           << "\",\"positions\":[{\"id\":0,\"x\":" << replay_int(x)
           << ",\"y\":" << replay_int(y)
           << ",\"timestamp\":" << now << "}]}}";
    add_replay_record(record.str(), false, "incremental", now, "click:" + safe_target);
}

void RumCore::capture_replay_input(uintptr_t hwnd, const char* target) {
    std::lock_guard lock(mutex_);
    const auto now = unix_time_milliseconds();
    const bool hidden = hwnd != 0 && replay_hidden_.find(hwnd) != replay_hidden_.end() && replay_hidden_.at(hwnd);
    const auto safe_target = hidden ? std::string{"hidden"} : str_or_empty(target);
    std::ostringstream record;
    record << "{\"type\":11,\"timestamp\":" << now
           << ",\"data\":{\"source\":0,\"adds\":[],\"removes\":[],\"updates\":[],\"event_type\":\"input\",\"target\":\""
           << json_escape(safe_target) << "\",\"x\":0,\"y\":0}}";
    add_replay_record(record.str(), false, "incremental", now, "input:" + safe_target);
}

void RumCore::capture_replay_resize(uintptr_t hwnd, const char* target, double width, double height) {
    (void)hwnd;
    std::lock_guard lock(mutex_);
    const auto now = unix_time_milliseconds();
    std::ostringstream record;
    record << "{\"type\":11,\"timestamp\":" << now
           << ",\"data\":{\"source\":4,\"target\":\"" << json_escape(str_or_empty(target))
           << "\",\"width\":" << replay_int(width) << ",\"height\":" << replay_int(height) << "}}";
    add_replay_record(record.str(), false, "incremental", now, "resize:" + str_or_empty(target));
}

bool RumCore::capture_browser_replay_record(
    const char* session_id,
    const char* view_id,
    const char* record_json,
    std::size_t record_json_length,
    int64_t timestamp_ms,
    bool is_full_snapshot) {
    (void)session_id;
    if (view_id == nullptr || record_json == nullptr || timestamp_ms <= 0 ||
        record_json_length < 2 || record_json_length > kMaxBrowserReplayRecordBytes) {
        return false;
    }

    const std::string browser_view_id(view_id);
    const auto invalid_identifier = [](const std::string& value) {
        return value.empty() || value.size() > 128 ||
               std::any_of(value.begin(), value.end(), [](unsigned char character) {
                   return std::iscntrl(character) != 0;
               });
    };
    if (invalid_identifier(browser_view_id) ||
        std::find(record_json, record_json + record_json_length, '\0') !=
            record_json + record_json_length) {
        return false;
    }

    std::string record(record_json, record_json_length);
    const auto first = std::find_if_not(record.begin(), record.end(), [](unsigned char character) {
        return std::isspace(character) != 0;
    });
    const auto last = std::find_if_not(record.rbegin(), record.rend(), [](unsigned char character) {
        return std::isspace(character) != 0;
    });
    if (first == record.end() || last == record.rend() || *first != '{' || *last != '}') {
        return false;
    }

    std::lock_guard lock(mutex_);
    if (!session_replay_recording_) {
        return false;
    }
    add_replay_record(
        std::move(record),
        is_full_snapshot,
        is_full_snapshot ? "full_snapshot" : "incremental",
        timestamp_ms,
        {},
        browser_view_id);
    return true;
}

void RumCore::set_session_replay_text_privacy(uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy) {
    if (hwnd == 0) {
        return;
    }
    std::lock_guard lock(mutex_);
    replay_text_privacy_[hwnd] = privacy;
}

void RumCore::set_session_replay_touch_privacy(uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy) {
    if (hwnd == 0) {
        return;
    }
    std::lock_guard lock(mutex_);
    replay_touch_privacy_[hwnd] = privacy;
}

void RumCore::set_session_replay_hidden(uintptr_t hwnd, bool hidden) {
    if (hwnd == 0) {
        return;
    }
    std::lock_guard lock(mutex_);
    replay_hidden_[hwnd] = hidden;
}

void RumCore::capture_session_replay_snapshot() {
    if (!session_replay_recording_) {
        return;
    }
    const auto timestamp_ms = unix_time_milliseconds();
    add_replay_record(build_session_replay_snapshot_record(timestamp_ms), true, "full_snapshot", timestamp_ms);
}

std::string RumCore::build_session_replay_snapshot_record(int64_t timestamp_ms) {
    std::vector<uintptr_t> windows = replay_windows_;
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    if (windows.empty()) {
        windows = enumerate_process_windows();
    }
#endif

    int wireframe_id = 1;
    std::ostringstream wireframes;
    bool has_child = false;
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    for (const auto hwnd_value : windows) {
        const auto hwnd = reinterpret_cast<HWND>(hwnd_value);
        if (hwnd == nullptr || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) {
            continue;
        }
        if (has_child) {
            wireframes << ",";
        }
        RECT rect{};
        if (GetWindowRect(hwnd, &rect)) {
            wireframes << "{\"id\":" << wireframe_id++
                       << ",\"type\":\"placeholder\",\"x\":" << std::max<LONG>(0, rect.left)
                       << ",\"y\":" << std::max<LONG>(0, rect.top)
                       << ",\"width\":" << std::max<LONG>(0, rect.right - rect.left)
                       << ",\"height\":" << std::max<LONG>(0, rect.bottom - rect.top)
                       << ",\"label\":\"Window\"}";
        } else {
            wireframes << "{\"id\":" << wireframe_id++
                       << ",\"type\":\"placeholder\",\"x\":0,\"y\":0,\"width\":0,\"height\":0,\"label\":\"Window\"}";
        }
        has_child = true;
    }
#else
    (void)windows;
#endif
    if (!has_child) {
        wireframes << "{\"id\":1,\"type\":\"placeholder\",\"x\":0,\"y\":0,\"width\":0,\"height\":0,\"label\":\"Desktop\"}";
    }

    std::ostringstream record;
    record << "{\"type\":10,\"timestamp\":" << timestamp_ms
           << ",\"data\":{\"wireframes\":[" << wireframes.str() << "]}}";

    return record.str();
}

std::pair<std::string, std::string> RumCore::build_session_replay_segment(
    std::string records_json,
    int records_count,
    bool has_full_snapshot,
    const std::string& creation_reason,
    int64_t start_ms,
    int64_t end_ms,
    const std::string& view_id_override) {
    const auto boundary = "guance-rum-replay-" + uuid32();
    const auto view_id = !view_id_override.empty()
        ? view_id_override
        : (active_view_ ? active_view_->id : std::string{});
    if (replay_segment_view_id_ != view_id) {
        replay_segment_view_id_ = view_id;
        replay_index_in_view_ = 0;
    }
    const auto index_in_view = replay_index_in_view_++;
    const auto replay_app_id = normalize_replay_id(config_.rum_app_id);
    const auto replay_session_id = normalize_replay_id(session_id_);
    const auto replay_view_id = normalize_replay_id(view_id);
    std::ostringstream segment;
    segment << "{\"application\":{\"id\":\"" << json_escape(replay_app_id)
            << "\"},\"session\":{\"id\":\"" << json_escape(replay_session_id)
            << "\"},\"view\":{\"id\":\"" << json_escape(replay_view_id)
            << "\"},\"start\":" << start_ms
            << ",\"end\":" << end_ms
            << ",\"records_count\":" << records_count
            << ",\"index_in_view\":" << index_in_view
            << ",\"has_full_snapshot\":" << (has_full_snapshot ? "true" : "false")
            << ",\"source\":\"" << kWindowsReplaySource << "\",\"records\":" << records_json << "}";
    const auto segment_json = segment.str() + "\n";
    const auto compressed_segment = zlib_store(segment_json);
    std::string body;
    body.reserve(compressed_segment.size() + 1024);
    body += multipart_field(boundary, "records_count", std::to_string(records_count));
    body += multipart_field(boundary, "index_in_view", std::to_string(index_in_view));
    body += multipart_field(boundary, "source", kWindowsReplaySource);
    body += multipart_field(boundary, "sdk_name", kWindowsSdkName);
    body += multipart_field(boundary, "sdk_version", kWindowsSdkVersion);
    body += multipart_field(boundary, "start", std::to_string(start_ms));
    body += multipart_field(boundary, "end", std::to_string(end_ms));
    body += multipart_field(boundary, "app_id", config_.rum_app_id);
    body += multipart_field(boundary, "view_id", view_id);
    (void)creation_reason;
    body += multipart_field(boundary, "session_id", session_id_);
    body += multipart_field(boundary, "env", config_.env);
    body += multipart_field(boundary, "service", config_.service_name);
    body += multipart_field(boundary, "version", config_.version);
    body += multipart_field(boundary, "raw_segment_size", std::to_string(segment_json.size()));
    body += multipart_field(boundary, "has_full_snapshot", has_full_snapshot ? "true" : "false");
    body += multipart_file(boundary, "segment", view_id.empty() ? "segment" : view_id, compressed_segment);
    body += "--" + boundary + "--\r\n";
    return {"multipart/form-data; boundary=" + boundary, body};
}

void RumCore::add_replay_record(
    std::string record_json,
    bool has_full_snapshot,
    const std::string& creation_reason,
    int64_t timestamp_ms,
    std::string coalesce_key,
    std::string view_id_override) {
    if (!session_replay_recording_) {
        return;
    }

    if (record_json.empty()) {
        return;
    }

    if (!replay_pending_records_.empty() &&
        replay_pending_view_id_ != view_id_override) {
        flush_replay_pending_locked();
    }
    if (replay_pending_records_.empty()) {
        replay_pending_view_id_ = std::move(view_id_override);
    }

    if (!coalesce_key.empty()) {
        for (auto it = replay_pending_records_.rbegin(); it != replay_pending_records_.rend(); ++it) {
            if (it->coalesce_key == coalesce_key &&
                is_within_forward_window(timestamp_ms, it->timestamp_ms, kReplayCoalesceMilliseconds)) {
                replay_pending_bytes_ -= it->json.size();
                it->json = std::move(record_json);
                it->timestamp_ms = timestamp_ms;
                replay_pending_bytes_ += it->json.size();
                replay_pending_end_ms_ = std::max(replay_pending_end_ms_, timestamp_ms);
                return;
            }
        }
    }

    if (replay_pending_records_.empty()) {
        replay_pending_start_ms_ = timestamp_ms;
        replay_pending_creation_reason_ = "incremental";
        replay_pending_has_full_snapshot_ = false;
        replay_pending_bytes_ = 0;
    }

    replay_pending_end_ms_ = std::max(replay_pending_end_ms_, timestamp_ms);
    replay_pending_has_full_snapshot_ = replay_pending_has_full_snapshot_ || has_full_snapshot;
    if (has_full_snapshot) {
        replay_pending_creation_reason_ = creation_reason;
    }
    replay_pending_bytes_ += record_json.size() + 1;
    replay_pending_records_.push_back({timestamp_ms, std::move(record_json), std::move(coalesce_key)});

    if (static_cast<int>(replay_pending_records_.size()) >= config_.session_replay_segment_record_limit ||
        replay_pending_bytes_ >= static_cast<std::size_t>(config_.session_replay_segment_bytes_limit) ||
        replay_pending_end_ms_ - replay_pending_start_ms_ >= kReplaySegmentFlushMilliseconds) {
        flush_replay_pending_locked();
    }
}

void RumCore::flush_replay_pending_locked() {
    if (replay_pending_records_.empty()) {
        return;
    }

    std::ostringstream records;
    records << "[";
    for (std::size_t i = 0; i < replay_pending_records_.size(); i++) {
        if (i != 0) {
            records << ",";
        }
        records << replay_pending_records_[i].json;
    }
    records << "]";

    auto [content_type, body] = build_session_replay_segment(
        records.str(),
        static_cast<int>(replay_pending_records_.size()),
        replay_pending_has_full_snapshot_,
        replay_pending_creation_reason_,
        replay_pending_start_ms_,
        replay_pending_end_ms_,
        replay_pending_view_id_);

    replay_pending_records_.clear();
    replay_pending_view_id_.clear();
    replay_pending_has_full_snapshot_ = false;
    replay_pending_creation_reason_ = "incremental";
    replay_pending_start_ms_ = 0;
    replay_pending_end_ms_ = 0;
    replay_pending_bytes_ = 0;

    enqueue_replay_segment(std::move(content_type), std::move(body));
}

void RumCore::enqueue_replay_segment(std::string content_type, std::string body) {
    if (body.empty()) {
        return;
    }

    if (session_replay_sampled_) {
        replay_queue_->enqueue(content_type + "\n" + body);
        return;
    }

    if (session_replay_error_sampled_) {
        const auto now = unix_time_milliseconds();
        replay_error_buffer_.push_back({now, std::move(content_type), std::move(body)});
        const auto cutoff = now - 60'000;
        replay_error_buffer_.erase(
            std::remove_if(replay_error_buffer_.begin(), replay_error_buffer_.end(), [cutoff](const ReplaySegment& segment) {
                return segment.created_ms < cutoff;
            }),
            replay_error_buffer_.end());
    }
}

RumEvent RumCore::base_event(const std::string& measurement, int64_t timestamp_ns) {
    RumEvent event{measurement, {}, {}, timestamp_ns};
    event.tags["app_id"] = config_.rum_app_id;
    event.tags["service"] = config_.service_name;
    event.tags["env"] = config_.env;
    event.tags["sdk_name"] = kWindowsSdkName;
    event.tags["sdk_version"] = kWindowsSdkVersion;
    event.tags["session_id"] = session_id_;
    event.fields["session_sample_rate"] = config_.sample_rate;
    event.fields["session_on_error_sample_rate"] = config_.session_error_sample_rate;
    for (const auto& [key, value] : global_context_) {
        event.tags[key] = value;
    }
    for (const auto& [key, value] : rum_context_) {
        event.tags[key] = value;
    }
    for (const auto& [key, value] : user_tags_) {
        event.tags[key] = value;
    }
    return event;
}

bool RumCore::enqueue(RumEvent event) {
    if (!sampled_for(event.measurement)) {
        // Sampling is a terminal decision. Recovered crash envelopes must not
        // be retried forever when the application intentionally samples them out.
        return true;
    }
    apply_modifiers(event);
    const bool persisted = queue_->enqueue(format_line_protocol(event));
    if (persisted) rum_events_enqueued_.fetch_add(1);
    return persisted;
}

void RumCore::apply_modifiers(RumEvent& event) {
    std::shared_lock lock(data_modifier_mutex_);
    apply_data_modifiers(event, modifier_privacy_config_, data_modifier_config_);
}

bool RumCore::consume_upload_budget(int64_t payload_bytes) {
    const auto now = std::chrono::steady_clock::now();
    const auto elapsed = std::chrono::duration<double>(now - upload_budget_updated_at_).count();
    upload_budget_updated_at_ = now;

    if (config_.max_upload_bytes_per_second > 0) {
        upload_byte_tokens_ = std::min(
            static_cast<double>(config_.upload_burst_bytes),
            upload_byte_tokens_ + elapsed * static_cast<double>(config_.max_upload_bytes_per_second));
    }
    if (config_.max_upload_requests_per_second > 0) {
        const auto request_capacity = std::max(1.0, config_.max_upload_requests_per_second);
        upload_request_tokens_ = std::min(
            request_capacity,
            upload_request_tokens_ + elapsed * config_.max_upload_requests_per_second);
    }

    if (config_.max_upload_bytes_per_second > 0 &&
        upload_byte_tokens_ < static_cast<double>(payload_bytes)) {
        return false;
    }
    if (config_.max_upload_requests_per_second > 0 && upload_request_tokens_ < 1.0) {
        return false;
    }

    if (config_.max_upload_bytes_per_second > 0) {
        upload_byte_tokens_ -= static_cast<double>(payload_bytes);
    }
    if (config_.max_upload_requests_per_second > 0) {
        upload_request_tokens_ -= 1.0;
    }
    return true;
}

void RumCore::record_rum_transport_result(bool delete_from_queue, bool retry_later, int status_code, int error_code, int64_t latency_ms) {
    last_rum_upload_status_code_.store(status_code);
    last_rum_upload_error_code_.store(error_code);
    last_rum_upload_latency_ms_.store(latency_ms);
    if (retry_later) {
        rum_upload_retry_count_.fetch_add(1);
    } else if (status_code >= 200 && status_code < 300) {
        rum_upload_success_count_.fetch_add(1);
    } else if (delete_from_queue) {
        rum_upload_terminal_failure_count_.fetch_add(1);
    }
}

void RumCore::record_replay_transport_result(bool delete_from_queue, bool retry_later, int status_code, int error_code, int64_t latency_ms) {
    last_replay_upload_status_code_.store(status_code);
    last_replay_upload_error_code_.store(error_code);
    last_replay_upload_latency_ms_.store(latency_ms);
    if (retry_later) {
        replay_upload_retry_count_.fetch_add(1);
    } else if (status_code >= 200 && status_code < 300) {
        replay_upload_success_count_.fetch_add(1);
    } else if (delete_from_queue) {
        replay_upload_terminal_failure_count_.fetch_add(1);
    }
}

void RumCore::record_log_transport_result(
    bool delete_from_queue,
    bool retry_later,
    int status_code,
    int error_code,
    int64_t latency_ms) {
    last_log_upload_status_code_.store(status_code);
    last_log_upload_error_code_.store(error_code);
    last_log_upload_latency_ms_.store(latency_ms);
    if (retry_later) {
        log_upload_retry_count_.fetch_add(1);
    } else if (status_code >= 200 && status_code < 300) {
        log_upload_success_count_.fetch_add(1);
    } else if (delete_from_queue) {
        log_upload_terminal_failure_count_.fetch_add(1);
    }
}

bool RumCore::sampled_for(const std::string& measurement) const {
    return session_sampled_ || (measurement == "error" && session_error_sampled_);
}

} // namespace guance::rum
