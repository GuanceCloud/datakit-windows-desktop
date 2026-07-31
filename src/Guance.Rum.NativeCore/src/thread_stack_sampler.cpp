#include "thread_stack_sampler.h"

#if defined(GUANCE_RUM_WINDOWS)

#include <windows.h>

#include <chrono>
#include <iomanip>
#include <sstream>

namespace guance::rum {

namespace {

class SuspendedThread {
public:
    explicit SuspendedThread(uint32_t thread_id)
        : handle_(OpenThread(
              THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION | THREAD_SUSPEND_RESUME,
              FALSE,
              thread_id)) {}

    ~SuspendedThread() {
        if (suspended_ && handle_ != nullptr) {
            ResumeThread(handle_);
        }
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
    }

    bool suspend() {
        if (handle_ == nullptr || SuspendThread(handle_) == static_cast<DWORD>(-1)) {
            return false;
        }
        suspended_ = true;
        return true;
    }

    HANDLE get() const noexcept {
        return handle_;
    }

    void resume() noexcept {
        if (suspended_ && handle_ != nullptr) {
            ResumeThread(handle_);
            suspended_ = false;
        }
    }

private:
    HANDLE handle_ = nullptr;
    bool suspended_ = false;
};

std::string module_name(HMODULE module) {
    char path[MAX_PATH]{};
    const DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
    std::string value(path, length > 0 && length < MAX_PATH ? length : 0);
    const auto separator = value.find_last_of("\\/");
    return separator == std::string::npos ? value : value.substr(separator + 1);
}

} // namespace

std::string sample_thread_stack(uint32_t thread_id, int budget_ms) {
    if (thread_id == 0 || thread_id == GetCurrentThreadId() || budget_ms <= 0) {
        return {};
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(budget_ms);
    SuspendedThread thread(thread_id);
    if (!thread.suspend()) {
        return {};
    }

    CONTEXT context{};
    context.ContextFlags = CONTEXT_CONTROL;
    if (!GetThreadContext(thread.get(), &context)) {
        return {};
    }

    uint64_t instruction_pointer = 0;
#if defined(_M_X64)
    instruction_pointer = context.Rip;
#elif defined(_M_IX86)
    instruction_pointer = context.Eip;
#elif defined(_M_ARM64)
    instruction_pointer = context.Pc;
#else
    return {};
#endif
    thread.resume();
    if (instruction_pointer == 0 || std::chrono::steady_clock::now() >= deadline) {
        return {};
    }

    HMODULE module = nullptr;
    std::ostringstream output;
    if (GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(static_cast<uintptr_t>(instruction_pointer)),
            &module) &&
        module != nullptr) {
        output << module_name(module) << "+0x" << std::hex
               << (instruction_pointer - reinterpret_cast<uintptr_t>(module));
    } else {
        output << "0x" << std::hex << instruction_pointer;
    }
    return output.str();
}

} // namespace guance::rum

#else

namespace guance::rum {

std::string sample_thread_stack(uint32_t, int) {
    return {};
}

} // namespace guance::rum

#endif
