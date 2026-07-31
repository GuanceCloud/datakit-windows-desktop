#include "native_crash_reporter.h"

#if defined(GUANCE_RUM_WINDOWS)

#include "line_protocol.h"

#include <dbghelp.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <mutex>
#include <string>

namespace guance::rum {

struct NativeCrashReporter::MinidumpWorkerState {
    static constexpr LONG kRunning = 0;
    static constexpr LONG kCompleted = 1;
    static constexpr LONG kDetached = 2;

    HANDLE stop_event = nullptr;
    HANDLE request_event = nullptr;
    HANDLE complete_event = nullptr;
    HANDLE thread_handle = nullptr;
    HANDLE file = INVALID_HANDLE_VALUE;
    HMODULE module = nullptr;
    EXCEPTION_POINTERS* exception_pointers = nullptr;
    DWORD process_id = 0;
    DWORD thread_id = 0;
    int64_t byte_budget = 0;
    std::atomic<bool> succeeded{false};
    volatile LONG owner = kRunning;
};

namespace {

std::atomic<NativeCrashReporter*> active_reporter{nullptr};
std::atomic<uint32_t> active_handler_entries{0};
std::atomic<uint32_t> deferred_cleanup_entries{0};
std::mutex handler_mutex;
std::atomic<uint32_t> file_sequence{0};

void native_crash_reporter_module_anchor() {}

class HandlerEntryGuard {
public:
    HandlerEntryGuard() noexcept {
        active_handler_entries.fetch_add(1);
    }

    ~HandlerEntryGuard() {
        active_handler_entries.fetch_sub(1);
    }
};

int64_t unix_time_nanoseconds_crash_safe() noexcept {
    FILETIME file_time{};
    GetSystemTimeAsFileTime(&file_time);
    ULARGE_INTEGER ticks{};
    ticks.LowPart = file_time.dwLowDateTime;
    ticks.HighPart = file_time.dwHighDateTime;
    constexpr uint64_t kWindowsToUnixEpoch100ns = 116444736000000000ULL;
    if (ticks.QuadPart <= kWindowsToUnixEpoch100ns) {
        return 0;
    }
    return static_cast<int64_t>((ticks.QuadPart - kWindowsToUnixEpoch100ns) * 100ULL);
}

std::string crash_file_stem() {
    return "guance-crash-" + std::to_string(GetCurrentProcessId()) + "-" +
           std::to_string(GetTickCount64()) + "-" +
           std::to_string(file_sequence.fetch_add(1));
}

void capture_context_registers(const CONTEXT* context, CrashEnvelope& envelope) noexcept {
    if (context == nullptr) {
        return;
    }
#if defined(_M_X64)
    envelope.instruction_pointer = context->Rip;
    envelope.stack_pointer = context->Rsp;
#elif defined(_M_IX86)
    envelope.instruction_pointer = context->Eip;
    envelope.stack_pointer = context->Esp;
#elif defined(_M_ARM64)
    envelope.instruction_pointer = context->Pc;
    envelope.stack_pointer = context->Sp;
#endif
}

} // namespace

NativeCrashReporter::NativeCrashReporter(RecoveryCallback callback)
    : callback_(std::move(callback)) {}

NativeCrashReporter::~NativeCrashReporter() {
    stop();
}

void NativeCrashReporter::recover_pending(const NativeCrashReporterConfig& config) {
    std::error_code ec;
    if (!std::filesystem::exists(config.directory, ec) || ec) {
        return;
    }
    CrashEnvelopeStore store(config.directory, config.max_files, config.max_bytes);
    store.recover(callback_);
    store.trim();
}

bool NativeCrashReporter::configure(const NativeCrashReporterConfig& config) {
    if (store_ && !stop()) {
        return false;
    }
    if (deferred_cleanup_entries.load() != 0) {
        return false;
    }
    config_ = config;
    store_ = std::make_unique<CrashEnvelopeStore>(
        config_.directory,
        config_.max_files,
        config_.max_bytes);
    store_->recover(callback_);
    if (!store_->trim(
            config_.enable_minidump ? 2 : 1,
            sizeof(CrashEnvelope),
            &minidump_byte_budget_)) {
        store_.reset();
        return false;
    }
    if (!prepare_files() || !install_handlers()) {
        close_files(true);
        store_.reset();
        return false;
    }
    return true;
}

bool NativeCrashReporter::stop() {
    if (abandoned_) {
        return false;
    }
    bool was_active = false;
    {
        std::lock_guard lock(handler_mutex);
        NativeCrashReporter* expected = this;
        if (active_reporter.compare_exchange_strong(expected, nullptr)) {
            was_active = true;
            SetUnhandledExceptionFilter(previous_exception_filter_);
            std::set_terminate(previous_terminate_handler_);
        }
    }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (was_active && active_handler_entries.load() != 0 &&
           std::chrono::steady_clock::now() < deadline) {
        Sleep(1);
    }
    if (was_active && active_handler_entries.load() != 0) {
        // Reserve a module reference for the caller's deferred-cleanup handoff.
        // Ownership cannot move from inside this member function because the
        // cleanup worker could otherwise delete `this` before stop() returns.
        HMODULE cleanup_module = nullptr;
        if (GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(&native_crash_reporter_module_anchor),
            &cleanup_module)) {
            deferred_cleanup_module_ = cleanup_module;
            deferred_cleanup_entries.fetch_add(1);
            abandoned_ = true;
            return false;
        }

        // If Windows cannot create the cleanup handoff, pinning is the only safe
        // fallback: returning remains bounded and cannot unload code under the
        // handler, at the cost of process-lifetime resources in this rare path.
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&native_crash_reporter_module_anchor),
            &cleanup_module);
        abandoned_ = true;
        return false;
    }
    previous_exception_filter_ = nullptr;
    previous_terminate_handler_ = nullptr;
    close_files(!record_written_.load());
    store_.reset();
    handler_active_.clear();
    record_written_.store(false);
    minidump_byte_budget_ = 0;
    return true;
}

void NativeCrashReporter::handoff_deferred_cleanup(
    std::unique_ptr<NativeCrashReporter> reporter) noexcept {
    if (!reporter) {
        return;
    }

    if (reporter->deferred_cleanup_module_ == nullptr) {
        HMODULE ignored = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&native_crash_reporter_module_anchor),
            &ignored);
        reporter.release();
        return;
    }

    const HANDLE cleanup_thread = CreateThread(
        nullptr,
        0,
        &NativeCrashReporter::deferred_cleanup_worker,
        reporter.get(),
        CREATE_SUSPENDED,
        nullptr);
    if (cleanup_thread != nullptr) {
        reporter.release();
        if (ResumeThread(cleanup_thread) != static_cast<DWORD>(-1)) {
            CloseHandle(cleanup_thread);
            return;
        }
        TerminateThread(cleanup_thread, 1);
        WaitForSingleObject(cleanup_thread, 1'000);
        CloseHandle(cleanup_thread);
    }

    // Thread creation/resume failure leaves no safe bounded reclamation path.
    // Keep the object and module alive rather than freeing crash-handler state.
    HMODULE ignored = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&native_crash_reporter_module_anchor),
        &ignored);
    reporter.release();
}

LONG WINAPI NativeCrashReporter::unhandled_exception_filter(EXCEPTION_POINTERS* pointers) {
    HandlerEntryGuard entry;
    auto* reporter = active_reporter.load();
    if (reporter == nullptr) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    const auto previous = reporter->previous_exception_filter_;
    reporter->capture(pointers, false);
    if (previous != nullptr && previous != &NativeCrashReporter::unhandled_exception_filter) {
        return previous(pointers);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

void NativeCrashReporter::terminate_handler() {
    HandlerEntryGuard entry;
    auto* reporter = active_reporter.load();
    const auto previous = reporter == nullptr ? nullptr : reporter->previous_terminate_handler_;
    if (reporter != nullptr) {
        reporter->capture(nullptr, true);
    }
    if (previous != nullptr && previous != &NativeCrashReporter::terminate_handler) {
        previous();
    }
    std::abort();
}

DWORD WINAPI NativeCrashReporter::minidump_worker(void* context) {
    auto* state = static_cast<MinidumpWorkerState*>(context);
    const auto finish = [state](DWORD exit_code) -> DWORD {
        const LONG owner = InterlockedCompareExchange(
            &state->owner,
            MinidumpWorkerState::kCompleted,
            MinidumpWorkerState::kRunning);
        if (owner != MinidumpWorkerState::kDetached) {
            return exit_code;
        }

        const HANDLE thread_handle = state->thread_handle;
        const HANDLE stop_event = state->stop_event;
        const HANDLE request_event = state->request_event;
        const HANDLE complete_event = state->complete_event;
        const HANDLE file = state->file;
        const HMODULE module = state->module;
        delete state;
        CloseHandle(thread_handle);
        CloseHandle(stop_event);
        CloseHandle(request_event);
        CloseHandle(complete_event);
        CloseHandle(file);
        FreeLibraryAndExitThread(module, exit_code);
        return exit_code;
    };

    const HANDLE events[] = {state->stop_event, state->request_event};
    while (true) {
        const DWORD wait_result = WaitForMultipleObjects(2, events, FALSE, INFINITE);
        if (wait_result == WAIT_OBJECT_0) {
            return finish(0);
        }
        if (wait_result != WAIT_OBJECT_0 + 1) {
            return finish(1);
        }

        MINIDUMP_EXCEPTION_INFORMATION exception_information{};
        exception_information.ThreadId = state->thread_id;
        exception_information.ExceptionPointers = state->exception_pointers;
        exception_information.ClientPointers = FALSE;
        const BOOL wrote_dump = MiniDumpWriteDump(
            GetCurrentProcess(),
            state->process_id,
            state->file,
            MiniDumpNormal,
            state->exception_pointers == nullptr ? nullptr : &exception_information,
            nullptr,
            nullptr);
        LARGE_INTEGER dump_size{};
        const bool within_budget = GetFileSizeEx(state->file, &dump_size) &&
            dump_size.QuadPart <= state->byte_budget;
        if (!within_budget) {
            LARGE_INTEGER beginning{};
            SetFilePointerEx(state->file, beginning, nullptr, FILE_BEGIN);
            SetEndOfFile(state->file);
        }
        if (wrote_dump && within_budget) {
            FlushFileBuffers(state->file);
        }
        state->succeeded.store(wrote_dump != FALSE && within_budget);
        SetEvent(state->complete_event);
    }
}

DWORD WINAPI NativeCrashReporter::deferred_cleanup_worker(void* context) {
    auto* reporter = static_cast<NativeCrashReporter*>(context);
    while (active_handler_entries.load() != 0) {
        Sleep(1);
    }

    const HMODULE module = reporter->deferred_cleanup_module_;
    reporter->deferred_cleanup_module_ = nullptr;
    reporter->abandoned_ = false;
    reporter->stop();
    delete reporter;
    deferred_cleanup_entries.fetch_sub(1);
    FreeLibraryAndExitThread(module, 0);
    return 0;
}

void NativeCrashReporter::capture_cpp_terminate_now() noexcept {
    HandlerEntryGuard entry;
    auto* reporter = active_reporter.load();
    if (reporter != nullptr) {
        reporter->capture(nullptr, true);
    }
}

bool NativeCrashReporter::prepare_files() {
    const auto stem = crash_file_stem();
    envelope_path_ = store_->directory() / (stem + ".envelope");
    envelope_file_ = CreateFileW(
        envelope_path_.c_str(),
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr);
    if (envelope_file_ == INVALID_HANDLE_VALUE) {
        return false;
    }

    if (!config_.enable_minidump || minidump_byte_budget_ <= 0) {
        return true;
    }
    const auto dump_name = stem + ".dmp";
    minidump_path_ = store_->directory() / dump_name;
    minidump_file_ = CreateFileW(
        minidump_path_.c_str(),
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr);
    if (minidump_file_ == INVALID_HANDLE_VALUE) {
        minidump_path_.clear();
        return true;
    }
    std::snprintf(minidump_file_name_, sizeof(minidump_file_name_), "%s", dump_name.c_str());
    if (!start_minidump_worker()) {
        CloseHandle(minidump_file_);
        minidump_file_ = INVALID_HANDLE_VALUE;
        std::error_code ec;
        std::filesystem::remove(minidump_path_, ec);
        minidump_path_.clear();
        std::memset(minidump_file_name_, 0, sizeof(minidump_file_name_));
    }
    return true;
}

bool NativeCrashReporter::start_minidump_worker() {
    auto state = std::make_unique<MinidumpWorkerState>();
    state->stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    state->request_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    state->complete_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    state->file = minidump_file_;
    state->byte_budget = minidump_byte_budget_;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&native_crash_reporter_module_anchor),
        &state->module);
    if (state->stop_event == nullptr || state->request_event == nullptr ||
        state->complete_event == nullptr || state->module == nullptr) {
        for (const HANDLE event : {state->stop_event, state->request_event, state->complete_event}) {
            if (event != nullptr) {
                CloseHandle(event);
            }
        }
        if (state->module != nullptr) {
            FreeLibrary(state->module);
        }
        return false;
    }

    minidump_worker_thread_ = CreateThread(
        nullptr,
        0,
        &NativeCrashReporter::minidump_worker,
        state.get(),
        0,
        nullptr);
    if (minidump_worker_thread_ == nullptr) {
        CloseHandle(state->stop_event);
        CloseHandle(state->request_event);
        CloseHandle(state->complete_event);
        FreeLibrary(state->module);
        return false;
    }
    state->thread_handle = minidump_worker_thread_;
    minidump_worker_state_ = std::move(state);
    return true;
}

void NativeCrashReporter::stop_minidump_worker() noexcept {
    if (!minidump_worker_state_ || minidump_worker_thread_ == nullptr) {
        return;
    }
    SetEvent(minidump_worker_state_->stop_event);
    const DWORD stopped = WaitForSingleObject(minidump_worker_thread_, 2'000);
    if (stopped != WAIT_OBJECT_0) {
        const LONG owner = InterlockedCompareExchange(
            &minidump_worker_state_->owner,
            MinidumpWorkerState::kDetached,
            MinidumpWorkerState::kRunning);
        if (owner == MinidumpWorkerState::kRunning) {
            // The worker owns cleanup and pins this module until it can exit safely.
            minidump_worker_state_.release();
            minidump_worker_thread_ = nullptr;
            minidump_file_ = INVALID_HANDLE_VALUE;
            return;
        }
        if (WaitForSingleObject(minidump_worker_thread_, 2'000) != WAIT_OBJECT_0) {
            // kCompleted means the worker no longer touches capture inputs, but
            // the scheduler has not returned it yet. Preserve its state instead
            // of making shutdown unbounded or freeing memory under the thread.
            minidump_worker_state_.release();
            minidump_worker_thread_ = nullptr;
            minidump_file_ = INVALID_HANDLE_VALUE;
            return;
        }
    }
    const HMODULE module = minidump_worker_state_->module;
    CloseHandle(minidump_worker_thread_);
    minidump_worker_thread_ = nullptr;
    CloseHandle(minidump_worker_state_->stop_event);
    CloseHandle(minidump_worker_state_->request_event);
    CloseHandle(minidump_worker_state_->complete_event);
    minidump_worker_state_.reset();
    FreeLibrary(module);
}

bool NativeCrashReporter::install_handlers() {
    std::lock_guard lock(handler_mutex);
    NativeCrashReporter* expected = nullptr;
    if (!active_reporter.compare_exchange_strong(expected, this)) {
        return false;
    }
    previous_exception_filter_ = SetUnhandledExceptionFilter(&NativeCrashReporter::unhandled_exception_filter);
    previous_terminate_handler_ = std::set_terminate(&NativeCrashReporter::terminate_handler);
    return true;
}

void NativeCrashReporter::capture(EXCEPTION_POINTERS* pointers, bool cpp_terminate) noexcept {
    if (handler_active_.test_and_set()) {
        return;
    }
    if (envelope_file_ == INVALID_HANDLE_VALUE) {
        return;
    }

    CrashEnvelope envelope{};
    envelope.timestamp_ns = unix_time_nanoseconds_crash_safe();
    envelope.process_id = GetCurrentProcessId();
    envelope.thread_id = GetCurrentThreadId();
    if (cpp_terminate) {
        envelope.flags |= CrashEnvelopeCppTerminate;
    } else if (pointers != nullptr && pointers->ExceptionRecord != nullptr) {
        envelope.exception_code_value = pointers->ExceptionRecord->ExceptionCode;
        envelope.exception_address = reinterpret_cast<uint64_t>(
            pointers->ExceptionRecord->ExceptionAddress);
    }
    if (pointers != nullptr) {
        capture_context_registers(pointers->ContextRecord, envelope);
    }

    if (minidump_worker_state_ && minidump_worker_thread_ != nullptr) {
        minidump_worker_state_->exception_pointers = pointers;
        minidump_worker_state_->process_id = envelope.process_id;
        minidump_worker_state_->thread_id = envelope.thread_id;
        minidump_worker_state_->succeeded.store(false);
        ResetEvent(minidump_worker_state_->complete_event);
        if (SetEvent(minidump_worker_state_->request_event) &&
            WaitForSingleObject(minidump_worker_state_->complete_event, 2'000) == WAIT_OBJECT_0 &&
            minidump_worker_state_->succeeded.load()) {
            LARGE_INTEGER dump_size{};
            if (GetFileSizeEx(minidump_file_, &dump_size) &&
                dump_size.QuadPart <= minidump_byte_budget_) {
                envelope.flags |= CrashEnvelopeHasMinidump;
                std::memcpy(
                    envelope.dump_file_name,
                    minidump_file_name_,
                    sizeof(envelope.dump_file_name));
            } else {
                LARGE_INTEGER beginning{};
                SetFilePointerEx(minidump_file_, beginning, nullptr, FILE_BEGIN);
                SetEndOfFile(minidump_file_);
                FlushFileBuffers(minidump_file_);
            }
        }
    }

    const auto complete = finalize_crash_envelope(envelope);
    auto pending = complete;
    pending.completed = 0;
    DWORD written = 0;
    SetFilePointer(envelope_file_, 0, nullptr, FILE_BEGIN);
    if (!WriteFile(envelope_file_, &pending, sizeof(pending), &written, nullptr) ||
        written != sizeof(pending)) {
        return;
    }
    FlushFileBuffers(envelope_file_);
    SetFilePointer(
        envelope_file_,
        static_cast<LONG>(offsetof(CrashEnvelope, completed)),
        nullptr,
        FILE_BEGIN);
    written = 0;
    if (!WriteFile(
            envelope_file_,
            &complete.completed,
            sizeof(complete.completed),
            &written,
            nullptr) ||
        written != sizeof(complete.completed)) {
        return;
    }
    FlushFileBuffers(envelope_file_);
    record_written_.store(true);
}

void NativeCrashReporter::close_files(bool remove_unused) noexcept {
    stop_minidump_worker();
    if (minidump_file_ != INVALID_HANDLE_VALUE) {
        CloseHandle(minidump_file_);
        minidump_file_ = INVALID_HANDLE_VALUE;
    }
    if (envelope_file_ != INVALID_HANDLE_VALUE) {
        CloseHandle(envelope_file_);
        envelope_file_ = INVALID_HANDLE_VALUE;
    }
    if (remove_unused) {
        std::error_code ec;
        if (!envelope_path_.empty()) {
            std::filesystem::remove(envelope_path_, ec);
        }
        ec.clear();
        if (!minidump_path_.empty()) {
            std::filesystem::remove(minidump_path_, ec);
        }
    }
    envelope_path_.clear();
    minidump_path_.clear();
    std::memset(minidump_file_name_, 0, sizeof(minidump_file_name_));
}

} // namespace guance::rum

#else

namespace guance::rum {

NativeCrashReporter::NativeCrashReporter(RecoveryCallback callback)
    : callback_(std::move(callback)) {}

NativeCrashReporter::~NativeCrashReporter() = default;

void NativeCrashReporter::capture_cpp_terminate_now() noexcept {}

void NativeCrashReporter::recover_pending(const NativeCrashReporterConfig&) {}

bool NativeCrashReporter::configure(const NativeCrashReporterConfig&) {
    return false;
}

bool NativeCrashReporter::stop() { return true; }

void NativeCrashReporter::handoff_deferred_cleanup(
    std::unique_ptr<NativeCrashReporter>) noexcept {}

} // namespace guance::rum

#endif
