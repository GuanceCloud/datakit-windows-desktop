#pragma once

#include "guance_rum.h"
#include "crash_envelope.h"
#include "hang_state_machine.h"
#include "line_protocol.h"
#include "queue_store.h"
#include "resource_collection.h"
#include "trace_context.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace guance::rum {

class NativeMonitoring;

struct Config {
    std::string dataway_url;
    std::string datakit_url;
    std::string client_token;
    std::string rum_app_id;
    std::string service_name = "df_rum_windows_native";
    std::string env = "prod";
    std::string version = "1.0.0";
    double sample_rate = 1.0;
    double session_error_sample_rate = 0.0;
    bool session_replay_enabled = false;
    double session_replay_sample_rate = 1.0;
    double session_replay_on_error_sample_rate = 0.0;
    bool debug = false;
    std::string cache_path;
    int max_queue_items = 100000;
    int64_t max_queue_bytes = 64LL * 1024 * 1024;
    int http_timeout_ms = 10000;
    std::string proxy_url;
    int session_replay_segment_record_limit = 500;
    int64_t session_replay_segment_bytes_limit = 1024 * 1024;
};

struct NativeDiagnostics {
    int64_t rum_events_enqueued = 0;
    int64_t rum_upload_success_count = 0;
    int64_t rum_upload_retry_count = 0;
    int64_t rum_upload_terminal_failure_count = 0;
    int64_t replay_upload_success_count = 0;
    int64_t replay_upload_retry_count = 0;
    int64_t replay_upload_terminal_failure_count = 0;
    int64_t last_rum_upload_status_code = 0;
    int64_t last_replay_upload_status_code = 0;
    int64_t last_rum_upload_error_code = 0;
    int64_t last_replay_upload_error_code = 0;
    int64_t last_rum_upload_latency_ms = 0;
    int64_t last_replay_upload_latency_ms = 0;
    bool session_sampled = false;
    bool session_error_sampled = false;
    bool session_replay_sampled = false;
    bool session_replay_error_sampled = false;
};

struct LogConfig {
    bool enable_custom_log = false;
    bool enable_link_rum_data = false;
    double sample_rate = 1.0;
    uint32_t level_filter_mask = 0;
    Tags global_context;
    int max_queue_items = 5000;
    int64_t max_queue_bytes = 32LL * 1024 * 1024;
    guance_rum_log_discard_strategy discard_strategy = GUANCE_RUM_LOG_DISCARD_NEW;
};

struct NativeLogDiagnostics {
    int64_t logs_enqueued = 0;
    int64_t logs_dropped = 0;
    int64_t upload_success_count = 0;
    int64_t upload_retry_count = 0;
    int64_t upload_terminal_failure_count = 0;
    int64_t last_upload_status_code = 0;
    int64_t last_upload_error_code = 0;
    int64_t last_upload_latency_ms = 0;
};

class RumCore {
public:
    explicit RumCore(Config config);
    ~RumCore();
    void flush();
    void shutdown();
    NativeDiagnostics diagnostics() const;
    NativeLogDiagnostics log_diagnostics() const;
    bool write_line(const char* line, std::size_t length);
    void set_user(const char* id, const char* name, const char* email);
    void clear_user();
    void add_global_context(const char* key, const char* value);
    void add_rum_context(const char* key, const char* value);
    void start_view(const char* name);
    void stop_view();
    void add_action(const char* name, const char* type, int64_t duration_ns);
    void add_launch_action(const guance_rum_launch& launch);
    std::string start_action(const char* name, const char* type);
    void stop_action(const char* action_id);
    std::string start_resource(const char* url, const char* method);
    std::string start_auto_resource(const char* url, const char* method);
    void stop_resource(const char* resource_id, int status_code, int64_t response_size);
    void stop_resource_ext(const char* resource_id, int status_code, int64_t response_size, int64_t request_size, const char* resource_type, const char* trace_id, const char* span_id, const char* http_protocol);
    bool configure_resource_collection(const guance_rum_resource_collection_config& config);
    bool configure_trace(const guance_rum_trace_config& config);
    bool configure_logging(const guance_rum_log_config& config);
    bool add_log(
        const char* content,
        const char* status,
        const guance_rum_log_property* properties,
        uint32_t property_count);
    std::optional<TraceContext> create_trace_context(
        const char* url,
        const char* method) const;
    void add_error(const char* stack, const char* message, const char* error_type, const char* source);
    void add_long_task(int64_t duration_ns, const char* stack);
    void start_session_replay();
    void stop_session_replay();
    void register_replay_window(uintptr_t hwnd);
    void capture_replay_click(uintptr_t hwnd, const char* target, double x, double y);
    void capture_replay_input(uintptr_t hwnd, const char* target);
    void capture_replay_resize(uintptr_t hwnd, const char* target, double width, double height);
    void set_session_replay_text_privacy(uintptr_t hwnd, guance_rum_session_replay_text_privacy privacy);
    void set_session_replay_touch_privacy(uintptr_t hwnd, guance_rum_session_replay_touch_privacy privacy);
    void set_session_replay_hidden(uintptr_t hwnd, bool hidden);
    bool enable_native_monitoring(const guance_rum_native_monitoring_config& config);
    void disable_native_monitoring();
    void add_ui_hang_event(const HangEvent& event, const std::string& stack);
    bool add_recovered_crash(
        const CrashEnvelope& envelope,
        const std::filesystem::path& minidump_path);
    std::filesystem::path default_native_crash_path() const;
    void log_native_monitoring(const std::string& message) const;

private:
    struct View {
        std::string id;
        std::string name;
        std::string referrer;
        int64_t started_ns = 0;
        int64_t started_monotonic_ns = 0;
        int action_count = 0;
        int resource_count = 0;
        int error_count = 0;
        int long_task_count = 0;
    };

    struct Resource {
        std::string id;
        std::string url;
        std::string method;
        std::string view_id;
        std::string view_name;
        std::string action_id;
        std::string action_name;
        int64_t started_ns = 0;
        int64_t started_monotonic_ns = 0;
    };

    struct Action {
        std::string id;
        std::string name;
        std::string type;
        std::string view_id;
        std::string view_name;
        std::string view_referrer;
        int64_t started_ns = 0;
        int64_t started_monotonic_ns = 0;
        int resource_count = 0;
        int error_count = 0;
        int long_task_count = 0;
        Fields fields;
    };

    struct ReplaySegment {
        int64_t created_ms = 0;
        std::string content_type;
        std::string body;
    };

    struct ReplayPendingRecord {
        int64_t timestamp_ms = 0;
        std::string json;
        std::string coalesce_key;
    };

    RumEvent base_event(const std::string& measurement, int64_t timestamp_ns);
    bool enqueue(RumEvent event);
    bool sampled_for(const std::string& measurement) const;
    void capture_session_replay_snapshot();
    std::string build_session_replay_snapshot_record(int64_t timestamp_ms);
    std::pair<std::string, std::string> build_session_replay_segment(std::string records_json, int records_count, bool has_full_snapshot, const std::string& creation_reason, int64_t start_ms, int64_t end_ms);
    void add_replay_record(std::string record_json, bool has_full_snapshot, const std::string& creation_reason, int64_t timestamp_ms, std::string coalesce_key = {});
    void flush_replay_pending_locked();
    void enqueue_replay_segment(std::string content_type, std::string body);
    void track_action(const Action& action, int64_t duration_ns);
    std::optional<Action> current_action_locked() const;
    void record_rum_transport_result(bool delete_from_queue, bool retry_later, int status_code, int error_code, int64_t latency_ms);
    void record_replay_transport_result(bool delete_from_queue, bool retry_later, int status_code, int error_code, int64_t latency_ms);
    void record_log_transport_result(bool delete_from_queue, bool retry_later, int status_code, int error_code, int64_t latency_ms);
    std::string start_resource_impl(const char* url, const char* method, bool automatic);

    Config config_;
    std::string session_id_;
    Tags global_context_;
    Tags rum_context_;
    Tags user_tags_;
    std::optional<View> active_view_;
    std::unordered_map<std::string, Action> active_actions_;
    std::unordered_map<std::string, Resource> resources_;
    std::unique_ptr<QueueStore> queue_;
    std::unique_ptr<QueueStore> replay_queue_;
    std::unique_ptr<QueueStore> log_queue_;
    std::unique_ptr<NativeMonitoring> native_monitoring_;
    ResourceCollectionConfig resource_collection_config_ = default_resource_collection_config();
    TraceConfig trace_config_;
    LogConfig log_config_;
    std::vector<ReplaySegment> replay_error_buffer_;
    std::vector<ReplayPendingRecord> replay_pending_records_;
    std::vector<uintptr_t> replay_windows_;
    std::unordered_map<uintptr_t, guance_rum_session_replay_text_privacy> replay_text_privacy_;
    std::unordered_map<uintptr_t, guance_rum_session_replay_touch_privacy> replay_touch_privacy_;
    std::unordered_map<uintptr_t, bool> replay_hidden_;
    mutable std::mutex mutex_;
    mutable std::mutex log_mutex_;
    bool session_sampled_ = true;
    bool session_error_sampled_ = false;
    bool session_replay_sampled_ = false;
    bool session_replay_error_sampled_ = false;
    bool session_replay_recording_ = false;
    int replay_index_in_view_ = 0;
    bool replay_pending_has_full_snapshot_ = false;
    std::string replay_pending_creation_reason_ = "incremental";
    int64_t replay_pending_start_ms_ = 0;
    int64_t replay_pending_end_ms_ = 0;
    std::size_t replay_pending_bytes_ = 0;
    std::atomic<int64_t> rum_events_enqueued_{0};
    std::atomic<int64_t> rum_upload_success_count_{0};
    std::atomic<int64_t> rum_upload_retry_count_{0};
    std::atomic<int64_t> rum_upload_terminal_failure_count_{0};
    std::atomic<int64_t> replay_upload_success_count_{0};
    std::atomic<int64_t> replay_upload_retry_count_{0};
    std::atomic<int64_t> replay_upload_terminal_failure_count_{0};
    std::atomic<int64_t> logs_enqueued_{0};
    std::atomic<int64_t> logs_dropped_{0};
    std::atomic<int64_t> log_upload_success_count_{0};
    std::atomic<int64_t> log_upload_retry_count_{0};
    std::atomic<int64_t> log_upload_terminal_failure_count_{0};
    std::atomic<int64_t> last_rum_upload_status_code_{0};
    std::atomic<int64_t> last_replay_upload_status_code_{0};
    std::atomic<int64_t> last_log_upload_status_code_{0};
    std::atomic<int64_t> last_rum_upload_error_code_{0};
    std::atomic<int64_t> last_replay_upload_error_code_{0};
    std::atomic<int64_t> last_log_upload_error_code_{0};
    std::atomic<int64_t> last_rum_upload_latency_ms_{0};
    std::atomic<int64_t> last_replay_upload_latency_ms_{0};
    std::atomic<int64_t> last_log_upload_latency_ms_{0};
};

Config from_c_config(const guance_rum_config* config);

} // namespace guance::rum
