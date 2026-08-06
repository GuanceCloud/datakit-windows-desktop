#pragma once

#include <cstdint>
#include <filesystem>
#include <functional>

namespace guance::rum {

constexpr uint32_t kCrashEnvelopeMagic = 0x47435248u; // GCRH
constexpr uint16_t kCrashEnvelopeVersion = 1;
constexpr uint32_t kCrashEnvelopeComplete = 0x434F4D50u; // COMP

enum CrashEnvelopeFlags : uint32_t {
    CrashEnvelopeHasMinidump = 1u << 0,
    CrashEnvelopeCppTerminate = 1u << 1,
};

#pragma pack(push, 1)
struct CrashEnvelope {
    uint32_t magic = 0;
    uint16_t version = 0;
    uint16_t struct_size = 0;
    uint32_t completed = 0;
    uint32_t checksum = 0;
    int64_t timestamp_ns = 0;
    uint32_t exception_code_value = 0;
    uint32_t process_id = 0;
    uint32_t thread_id = 0;
    uint32_t flags = 0;
    uint64_t exception_address = 0;
    uint64_t instruction_pointer = 0;
    uint64_t stack_pointer = 0;
    char dump_file_name[128]{};
    uint8_t reserved[64]{};
};
#pragma pack(pop)

static_assert(sizeof(CrashEnvelope) == 256, "CrashEnvelope layout is a persistent contract");

CrashEnvelope finalize_crash_envelope(CrashEnvelope envelope) noexcept;
bool validate_crash_envelope(const CrashEnvelope& envelope) noexcept;

struct CrashRecoveryResult {
    int recovered = 0;
    int deferred = 0;
    int quarantined = 0;
};

class CrashEnvelopeStore {
public:
    using RecoveryCallback = std::function<bool(
        const CrashEnvelope& envelope,
        const std::filesystem::path& minidump_path)>;

    CrashEnvelopeStore(std::filesystem::path directory, int max_files, int64_t max_bytes);

    CrashRecoveryResult recover(const RecoveryCallback& callback);
    bool trim(
        int reserve_files = 0,
        int64_t reserve_bytes = 0,
        int64_t* remaining_bytes = nullptr);
    const std::filesystem::path& directory() const noexcept;

private:
    bool quarantine(const std::filesystem::path& path);
    std::filesystem::path minidump_path(const CrashEnvelope& envelope) const;

    std::filesystem::path directory_;
    int max_files_ = 3;
    int64_t max_bytes_ = 32LL * 1024 * 1024;
};

} // namespace guance::rum
