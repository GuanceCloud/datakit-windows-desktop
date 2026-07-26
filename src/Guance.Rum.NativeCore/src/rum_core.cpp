#include "rum_core.h"
#include "transport.h"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <iostream>
#include <iomanip>
#include <iterator>
#include <cmath>
#include <random>
#include <regex>
#include <sstream>

#if defined(GUANCE_RUM_WINDOWS)
#include <windows.h>
#include <oleauto.h>
#include <uiautomation.h>
#endif

namespace guance::rum {

namespace {

constexpr int64_t kReplaySegmentFlushMilliseconds = 5000;
constexpr int64_t kReplayCoalesceMilliseconds = 200;

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

std::string replay_queue_path(const std::string& configured) {
    std::filesystem::path path = configured.empty() ? default_queue_path() : configured;
    const auto stem = path.stem().string();
    const auto extension = path.extension().string();
    path.replace_filename(stem + "-replay" + extension);
    return path.string();
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

std::string multipart_field(const std::string& boundary, const std::string& name, const std::string& value) {
    return "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n";
}

std::string multipart_file(const std::string& boundary, const std::string& name, const std::string& filename, const std::string& value) {
    return "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"; filename=\"" + filename + "\"\r\nContent-Type: application/json\r\n\r\n" + value + "\r\n";
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

#if defined(GUANCE_RUM_WINDOWS)
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

Config from_c_config(const guance_rum_config* c) {
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
    config.max_queue_items = c->max_queue_items <= 0 ? 100000 : c->max_queue_items;
    config.max_queue_bytes = c->max_queue_bytes <= 0 ? 64LL * 1024 * 1024 : c->max_queue_bytes;
    config.http_timeout_ms = c->http_timeout_ms <= 0 ? 10000 : c->http_timeout_ms;
    config.proxy_url = str_or_empty(c->proxy_url);
    config.session_replay_segment_record_limit = c->session_replay_segment_record_limit <= 0 ? 500 : c->session_replay_segment_record_limit;
    config.session_replay_segment_bytes_limit = c->session_replay_segment_bytes_limit <= 0 ? 1024 * 1024 : c->session_replay_segment_bytes_limit;
    return config;
}

RumCore::RumCore(Config config)
    : config_(std::move(config)), session_id_(uuid32()) {
    session_sampled_ = hit_rate(config_.sample_rate);
    session_error_sampled_ = !session_sampled_ && hit_rate(config_.session_error_sample_rate);
    session_replay_sampled_ = hit_rate(config_.session_replay_sample_rate);
    session_replay_error_sampled_ = !session_replay_sampled_ && hit_rate(config_.session_replay_on_error_sample_rate);
    session_replay_recording_ = config_.session_replay_enabled && (session_replay_sampled_ || session_replay_error_sampled_);
    queue_ = std::make_unique<QueueStore>(config_.cache_path, config_.max_queue_items, config_.max_queue_bytes);
    replay_queue_ = std::make_unique<QueueStore>(replay_queue_path(config_.cache_path), config_.max_queue_items, config_.max_queue_bytes);
}

void RumCore::flush() {
    {
        std::lock_guard lock(mutex_);
        flush_replay_pending_locked();
    }

    while (true) {
        const auto batch = queue_->peek(50);
        if (batch.empty()) {
            break;
        }
        std::vector<std::string> lines;
        std::vector<int64_t> ids;
        for (const auto& item : batch) {
            ids.push_back(item.id);
            lines.push_back(item.line);
        }
        const auto result = send_to_dataway(config_, lines);
        record_rum_transport_result(result.delete_from_queue, result.retry_later, result.status_code, result.error_code, result.latency_ms);
        if (result.retry_later) {
            return;
        }
        if (result.delete_from_queue) {
            queue_->remove(ids);
        }
    }

    while (true) {
        const auto batch = replay_queue_->peek(5);
        if (batch.empty()) {
            return;
        }
        std::vector<int64_t> ids;
        for (const auto& item : batch) {
            const auto separator = item.line.find('\n');
            if (separator == std::string::npos) {
                ids.push_back(item.id);
                continue;
            }
            const auto content_type = item.line.substr(0, separator);
            const auto body = item.line.substr(separator + 1);
            if (config_.debug) {
                const auto payload = replay_segment_payload(content_type, body);
                if (payload) {
                    std::cout << "[Guance.RUM.Native.SessionReplay] upload payload structure:\n"
                              << *payload << std::endl;
                } else {
                    std::cout << "[Guance.RUM.Native.SessionReplay] upload payload structure unavailable." << std::endl;
                }
            }
            const auto result = send_session_replay_to_dataway(config_, content_type, body);
            record_replay_transport_result(result.delete_from_queue, result.retry_later, result.status_code, result.error_code, result.latency_ms);
            if (result.retry_later) {
                if (!ids.empty()) {
                    replay_queue_->remove(ids);
                }
                return;
            }
            if (result.delete_from_queue) {
                ids.push_back(item.id);
            }
        }
        replay_queue_->remove(ids);
    }
}

void RumCore::shutdown() {
    stop_view();
    flush();
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
    snapshot.session_sampled = session_sampled_;
    snapshot.session_error_sampled = session_error_sampled_;
    snapshot.session_replay_sampled = session_replay_sampled_;
    snapshot.session_replay_error_sampled = session_replay_error_sampled_;
    return snapshot;
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
    {
        std::lock_guard lock(mutex_);
        if (active_view_) {
            flush_replay_pending_locked();
        } else {
            replay_pending_records_.clear();
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
    {
        std::lock_guard lock(mutex_);
        flush_replay_pending_locked();
        view = active_view_;
        active_view_.reset();
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

std::string RumCore::start_action(const char* name, const char* type) {
    std::lock_guard lock(mutex_);
    Action action{uuid32(), str_or_empty(name), str_or_empty(type), {}, {}, {}, unix_time_nanoseconds(), monotonic_time_nanoseconds()};
    if (active_view_) {
        action.view_id = active_view_->id;
        action.view_name = active_view_->name;
        action.view_referrer = active_view_->referrer;
    }
    const auto id = action.id;
    active_actions_[id] = std::move(action);
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
        action = it->second;
        active_actions_.erase(it);
        track_action(action, elapsed_since(action.started_monotonic_ns));
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
    std::lock_guard lock(mutex_);
    Resource resource{uuid32(), str_or_empty(url), str_or_empty(method), {}, {}, {}, {}, unix_time_nanoseconds(), monotonic_time_nanoseconds()};
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

void RumCore::stop_resource(const char* resource_id, int status_code, int64_t response_size) {
    stop_resource_ext(resource_id, status_code, response_size, -1, nullptr, nullptr, nullptr, nullptr);
}

void RumCore::stop_resource_ext(const char* resource_id, int status_code, int64_t response_size, int64_t request_size, const char* resource_type, const char* trace_id, const char* span_id, const char* http_protocol) {
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
    if (response_size > 0) {
        event.fields["resource_size"] = response_size;
    }
    if (request_size > 0) {
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
}

void RumCore::start_session_replay() {
    std::lock_guard lock(mutex_);
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
#if defined(GUANCE_RUM_WINDOWS)
    if (windows.empty()) {
        windows = enumerate_process_windows();
    }
#endif

    int wireframe_id = 1;
    std::ostringstream wireframes;
    bool has_child = false;
#if defined(GUANCE_RUM_WINDOWS)
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
    int64_t end_ms) {
    const auto boundary = "guance-rum-replay-" + uuid32();
    const auto view_id = active_view_ ? active_view_->id : std::string{};
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
            << ",\"index_in_view\":" << replay_index_in_view_++
            << ",\"has_full_snapshot\":" << (has_full_snapshot ? "true" : "false")
            << ",\"source\":\"android\",\"records\":" << records_json << "}";
    const auto segment_json = segment.str();
    std::string body;
    body.reserve(segment_json.size() + 1024);
    body += multipart_field(boundary, "records_count", std::to_string(records_count));
    body += multipart_field(boundary, "index_in_view", std::to_string(replay_index_in_view_ - 1));
    body += multipart_field(boundary, "source", "android");
    body += multipart_field(boundary, "sdk_name", "guance-rum-windows-native");
    body += multipart_field(boundary, "sdk_version", config_.version);
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
    body += multipart_file(boundary, "segment", view_id.empty() ? "segment" : view_id, segment_json);
    body += "--" + boundary + "--\r\n";
    return {"multipart/form-data; boundary=" + boundary, body};
}

void RumCore::add_replay_record(
    std::string record_json,
    bool has_full_snapshot,
    const std::string& creation_reason,
    int64_t timestamp_ms,
    std::string coalesce_key) {
    if (!session_replay_recording_) {
        return;
    }

    if (record_json.empty()) {
        return;
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
        replay_pending_end_ms_);

    replay_pending_records_.clear();
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
    event.tags["sdk_name"] = "guance-rum-windows-native";
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

void RumCore::enqueue(RumEvent event) {
    if (!sampled_for(event.measurement)) {
        return;
    }
    rum_events_enqueued_.fetch_add(1);
    queue_->enqueue(format_line_protocol(event));
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

bool RumCore::sampled_for(const std::string& measurement) const {
    return session_sampled_ || (measurement == "error" && session_error_sampled_);
}

} // namespace guance::rum
