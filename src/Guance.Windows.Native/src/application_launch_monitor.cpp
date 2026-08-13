#include "application_launch_monitor.h"

#include "line_protocol.h"

#include <condition_variable>
#include <cstdint>
#include <limits>
#include <mutex>
#include <optional>
#include <thread>
#include <utility>

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dwmapi.h>
#endif

namespace guance::rum {

class ApplicationLaunchMonitor::Impl {
public:
    Impl(
        ApplicationLaunchTimestamp sdk_initialized,
        LaunchCallback launch_callback,
        LogCallback log_callback)
        : sdk_initialized_(sdk_initialized),
          launch_callback_(std::move(launch_callback)),
          log_callback_(std::move(log_callback)) {}

    ~Impl() {
        stop();
    }

    bool start();
    void stop();
    void complete_cold_without_event();

private:
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    static void CALLBACK win_event_callback(
        HWINEVENTHOOK hook,
        DWORD event,
        HWND window,
        LONG object_id,
        LONG child_id,
        DWORD event_thread,
        DWORD event_time);
    static BOOL CALLBACK enum_window_callback(HWND window, LPARAM context);

    void run();
    void inspect_existing_windows();
    void handle_event(DWORD event, HWND window, LONG object_id, LONG child_id);
    void handle_window_created(HWND window);
    void handle_window_shown(HWND window);
    void handle_foreground(HWND window);
    void emit_first_frame();
    void update_foreground_baseline();
    bool is_main_window_candidate(HWND window) const;
    bool belongs_to_process(HWND window) const;
    static ApplicationLaunchTimestamp now();
    static int64_t process_start_time_ns();

    DWORD process_id_ = 0;
    DWORD worker_thread_id_ = 0;
    HWINEVENTHOOK object_hook_ = nullptr;
    HWINEVENTHOOK foreground_hook_ = nullptr;
    HWND existing_window_ = nullptr;
    HWND visible_existing_window_ = nullptr;
#endif

    ApplicationLaunchTimestamp sdk_initialized_;
    LaunchCallback launch_callback_;
    LogCallback log_callback_;
    std::unique_ptr<ApplicationLaunchState> state_;
    std::mutex state_mutex_;
    std::mutex lifecycle_mutex_;
    std::condition_variable started_cv_;
    std::thread worker_;
    bool start_finished_ = false;
    bool start_succeeded_ = false;
    bool stopping_ = false;
};

#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)

namespace {

thread_local void* active_launch_monitor = nullptr;
constexpr uint64_t kFileTimeUnixEpochOffset100ns = 116'444'736'000'000'000ULL;

uint64_t file_time_value(const FILETIME& value) {
    ULARGE_INTEGER combined{};
    combined.LowPart = value.dwLowDateTime;
    combined.HighPart = value.dwHighDateTime;
    return combined.QuadPart;
}

} // namespace

ApplicationLaunchTimestamp ApplicationLaunchMonitor::Impl::now() {
    return ApplicationLaunchTimestamp{
        unix_time_nanoseconds(),
        monotonic_time_nanoseconds()};
}

int64_t ApplicationLaunchMonitor::Impl::process_start_time_ns() {
    FILETIME created{};
    FILETIME exited{};
    FILETIME kernel{};
    FILETIME user{};
    if (GetProcessTimes(
            GetCurrentProcess(),
            &created,
            &exited,
            &kernel,
            &user) == FALSE) {
        return 0;
    }

    const auto created_100ns = file_time_value(created);
    if (created_100ns <= kFileTimeUnixEpochOffset100ns) {
        return 0;
    }
    const auto unix_100ns = created_100ns - kFileTimeUnixEpochOffset100ns;
    constexpr uint64_t max_ns = static_cast<uint64_t>((std::numeric_limits<int64_t>::max)());
    if (unix_100ns > max_ns / 100ULL) {
        return (std::numeric_limits<int64_t>::max)();
    }
    return static_cast<int64_t>(unix_100ns * 100ULL);
}

bool ApplicationLaunchMonitor::Impl::belongs_to_process(HWND window) const {
    if (window == nullptr) {
        return false;
    }
    DWORD process_id = 0;
    GetWindowThreadProcessId(window, &process_id);
    return process_id == process_id_;
}

bool ApplicationLaunchMonitor::Impl::is_main_window_candidate(HWND window) const {
    if (!IsWindow(window) || !belongs_to_process(window)) {
        return false;
    }
    if (GetAncestor(window, GA_ROOT) != window) {
        return false;
    }
    const auto style = static_cast<LONG_PTR>(GetWindowLongPtrW(window, GWL_STYLE));
    const auto extended_style = static_cast<LONG_PTR>(
        GetWindowLongPtrW(window, GWL_EXSTYLE));
    return (style & WS_CHILD) == 0 &&
        (extended_style & WS_EX_TOOLWINDOW) == 0 &&
        GetWindow(window, GW_OWNER) == nullptr;
}

BOOL CALLBACK ApplicationLaunchMonitor::Impl::enum_window_callback(
    HWND window,
    LPARAM context) {
    auto* self = reinterpret_cast<ApplicationLaunchMonitor::Impl*>(context);
    if (!self->is_main_window_candidate(window)) {
        return TRUE;
    }
    if (self->existing_window_ == nullptr) {
        self->existing_window_ = window;
    }
    if (self->visible_existing_window_ == nullptr && IsWindowVisible(window)) {
        self->visible_existing_window_ = window;
    }
    return TRUE;
}

void ApplicationLaunchMonitor::Impl::inspect_existing_windows() {
    existing_window_ = nullptr;
    visible_existing_window_ = nullptr;
    EnumWindows(enum_window_callback, reinterpret_cast<LPARAM>(this));
    if (existing_window_ == nullptr) {
        return;
    }

    {
        std::lock_guard lock(state_mutex_);
        state_->window_created(sdk_initialized_.monotonic_ns);
    }
    if (visible_existing_window_ != nullptr) {
        emit_first_frame();
    }
}

void ApplicationLaunchMonitor::Impl::emit_first_frame() {
    // DwmFlush provides a platform-level approximation of the first composed frame.
    // If composition is disabled or unavailable, the event timestamp below is used.
    DwmFlush();
    std::optional<ApplicationLaunchDecision> decision;
    {
        std::lock_guard lock(state_mutex_);
        decision = state_->first_frame(now());
    }
    if (decision) {
        launch_callback_(*decision);
        update_foreground_baseline();
    }
}

void ApplicationLaunchMonitor::Impl::update_foreground_baseline() {
    const auto foreground = GetForegroundWindow();
    std::lock_guard lock(state_mutex_);
    state_->foreground_changed(belongs_to_process(foreground), now());
}

void ApplicationLaunchMonitor::Impl::handle_window_created(HWND window) {
    if (!is_main_window_candidate(window)) {
        return;
    }
    std::lock_guard lock(state_mutex_);
    state_->window_created(now().monotonic_ns);
}

void ApplicationLaunchMonitor::Impl::handle_window_shown(HWND window) {
    if (!is_main_window_candidate(window)) {
        return;
    }
    {
        std::lock_guard lock(state_mutex_);
        state_->window_created(now().monotonic_ns);
    }
    emit_first_frame();
}

void ApplicationLaunchMonitor::Impl::handle_foreground(HWND window) {
    std::optional<ApplicationLaunchTimestamp> hot_started;
    {
        std::lock_guard lock(state_mutex_);
        hot_started = state_->foreground_changed(belongs_to_process(window), now());
    }
    if (!hot_started) {
        return;
    }

    DwmFlush();
    std::optional<ApplicationLaunchDecision> decision;
    {
        std::lock_guard lock(state_mutex_);
        decision = state_->hot_first_frame(now());
    }
    if (decision) {
        launch_callback_(*decision);
    }
}

void ApplicationLaunchMonitor::Impl::handle_event(
    DWORD event,
    HWND window,
    LONG object_id,
    LONG child_id) {
    if (event == EVENT_SYSTEM_FOREGROUND) {
        handle_foreground(window);
        return;
    }
    if (object_id != OBJID_WINDOW || child_id != CHILDID_SELF) {
        return;
    }
    if (event == EVENT_OBJECT_CREATE) {
        handle_window_created(window);
    } else if (event == EVENT_OBJECT_SHOW) {
        handle_window_shown(window);
    }
}

void CALLBACK ApplicationLaunchMonitor::Impl::win_event_callback(
    HWINEVENTHOOK,
    DWORD event,
    HWND window,
    LONG object_id,
    LONG child_id,
    DWORD,
    DWORD) {
    if (active_launch_monitor != nullptr) {
        static_cast<ApplicationLaunchMonitor::Impl*>(active_launch_monitor)
            ->handle_event(event, window, object_id, child_id);
    }
}

void ApplicationLaunchMonitor::Impl::run() {
    MSG message{};
    PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
    worker_thread_id_ = GetCurrentThreadId();
    process_id_ = GetCurrentProcessId();
    active_launch_monitor = this;

    object_hook_ = SetWinEventHook(
        EVENT_OBJECT_CREATE,
        EVENT_OBJECT_SHOW,
        nullptr,
        win_event_callback,
        process_id_,
        0,
        WINEVENT_OUTOFCONTEXT);
    foreground_hook_ = SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_FOREGROUND,
        nullptr,
        win_event_callback,
        0,
        0,
        WINEVENT_OUTOFCONTEXT);

    const bool hooks_ready = object_hook_ != nullptr && foreground_hook_ != nullptr;
    if (hooks_ready) {
        inspect_existing_windows();
    }
    {
        std::lock_guard lock(lifecycle_mutex_);
        start_succeeded_ = hooks_ready;
        start_finished_ = true;
    }
    started_cv_.notify_all();

    if (hooks_ready) {
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    } else if (log_callback_) {
        log_callback_("automatic application launch monitoring could not install WinEvent hooks");
    }

    if (foreground_hook_ != nullptr) {
        UnhookWinEvent(foreground_hook_);
        foreground_hook_ = nullptr;
    }
    if (object_hook_ != nullptr) {
        UnhookWinEvent(object_hook_);
        object_hook_ = nullptr;
    }
    active_launch_monitor = nullptr;
}

#endif

bool ApplicationLaunchMonitor::Impl::start() {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    std::unique_lock lock(lifecycle_mutex_);
    if (worker_.joinable()) {
        return start_succeeded_;
    }
    state_ = std::make_unique<ApplicationLaunchState>(
        process_start_time_ns(),
        sdk_initialized_);
    start_finished_ = false;
    start_succeeded_ = false;
    stopping_ = false;
    worker_ = std::thread([this] { run(); });
    started_cv_.wait(lock, [this] { return start_finished_; });
    return start_succeeded_;
#else
    return false;
#endif
}

void ApplicationLaunchMonitor::Impl::stop() {
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    DWORD worker_thread_id = 0;
    {
        std::lock_guard lock(lifecycle_mutex_);
        if (!worker_.joinable() || stopping_) {
            return;
        }
        stopping_ = true;
        worker_thread_id = worker_thread_id_;
    }
    if (worker_thread_id != 0) {
        PostThreadMessageW(worker_thread_id, WM_QUIT, 0, 0);
    }
    if (worker_.joinable() && worker_.get_id() != std::this_thread::get_id()) {
        worker_.join();
    }
#endif
}

void ApplicationLaunchMonitor::Impl::complete_cold_without_event() {
    {
        std::lock_guard lock(state_mutex_);
        if (!state_) {
            return;
        }
        state_->complete_cold_without_event();
    }
#if defined(GUANCE_WINDOWS_NATIVE_WINDOWS)
    update_foreground_baseline();
#endif
}

ApplicationLaunchMonitor::ApplicationLaunchMonitor(
    ApplicationLaunchTimestamp sdk_initialized,
    LaunchCallback launch_callback,
    LogCallback log_callback)
    : impl_(std::make_unique<Impl>(
          sdk_initialized,
          std::move(launch_callback),
          std::move(log_callback))) {}

ApplicationLaunchMonitor::~ApplicationLaunchMonitor() = default;

bool ApplicationLaunchMonitor::start() {
    return impl_->start();
}

void ApplicationLaunchMonitor::stop() {
    impl_->stop();
}

void ApplicationLaunchMonitor::complete_cold_without_event() {
    impl_->complete_cold_without_event();
}

} // namespace guance::rum
