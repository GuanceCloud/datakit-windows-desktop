#pragma once

#include "crash_envelope.h"

#include <atomic>
#include <exception>
#include <filesystem>
#include <functional>
#include <memory>

#if defined(GUANCE_RUM_WINDOWS)
#include <windows.h>
#endif

namespace guance::rum {

struct NativeCrashReporterConfig {
    std::filesystem::path directory;
    bool enable_minidump = false;
    int max_files = 3;
    int64_t max_bytes = 32LL * 1024 * 1024;
};

class NativeCrashReporter {
public:
    using RecoveryCallback = CrashEnvelopeStore::RecoveryCallback;

    explicit NativeCrashReporter(RecoveryCallback callback);
    ~NativeCrashReporter();

    void recover_pending(const NativeCrashReporterConfig& config);
    bool configure(const NativeCrashReporterConfig& config);
    bool stop();
    static void handoff_deferred_cleanup(
        std::unique_ptr<NativeCrashReporter> reporter) noexcept;
    static void capture_cpp_terminate_now() noexcept;

private:
#if defined(GUANCE_RUM_WINDOWS)
    struct MinidumpWorkerState;

    static LONG WINAPI unhandled_exception_filter(EXCEPTION_POINTERS* pointers);
    static void terminate_handler();
    static DWORD WINAPI minidump_worker(void* context);
    static DWORD WINAPI deferred_cleanup_worker(void* context);

    bool prepare_files();
    bool start_minidump_worker();
    void stop_minidump_worker() noexcept;
    bool install_handlers();
    void capture(EXCEPTION_POINTERS* pointers, bool cpp_terminate) noexcept;
    void close_files(bool remove_unused) noexcept;

    RecoveryCallback callback_;
    NativeCrashReporterConfig config_;
    std::unique_ptr<CrashEnvelopeStore> store_;
    std::filesystem::path envelope_path_;
    std::filesystem::path minidump_path_;
    HANDLE envelope_file_ = INVALID_HANDLE_VALUE;
    HANDLE minidump_file_ = INVALID_HANDLE_VALUE;
    HANDLE minidump_worker_thread_ = nullptr;
    std::unique_ptr<MinidumpWorkerState> minidump_worker_state_;
    LPTOP_LEVEL_EXCEPTION_FILTER previous_exception_filter_ = nullptr;
    std::terminate_handler previous_terminate_handler_ = nullptr;
    std::atomic_flag handler_active_ = ATOMIC_FLAG_INIT;
    std::atomic<bool> record_written_{false};
    int64_t minidump_byte_budget_ = 0;
    bool abandoned_ = false;
    HMODULE deferred_cleanup_module_ = nullptr;
    char minidump_file_name_[128]{};
#else
    RecoveryCallback callback_;
#endif
};

} // namespace guance::rum
