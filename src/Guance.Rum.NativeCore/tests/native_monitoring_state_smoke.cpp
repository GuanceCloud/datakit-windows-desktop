#include "crash_envelope.h"
#include "hang_state_machine.h"

#include <algorithm>
#include <cassert>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <string>

namespace {

using guance::rum::CrashEnvelope;
using guance::rum::CrashEnvelopeStore;
using guance::rum::HangEventKind;
using guance::rum::HangStateMachine;

std::filesystem::path test_directory() {
    const auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
    return std::filesystem::temp_directory_path() /
           ("guance-native-monitoring-" + std::to_string(suffix));
}

void write_binary(const std::filesystem::path& path, const void* data, std::size_t size) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(static_cast<const char*>(data), static_cast<std::streamsize>(size));
}

CrashEnvelope envelope(uint32_t exception_code = 0xC0000005u) {
    CrashEnvelope value{};
    value.timestamp_ns = 1'722'300'000'000'000'000LL;
    value.exception_code_value = exception_code;
    value.exception_address = 0x00007FF612341234ULL;
    value.process_id = 42;
    value.thread_id = 7;
    return guance::rum::finalize_crash_envelope(value);
}

void hang_state_machine_reports_only_after_recovery() {
    HangStateMachine machine({250, 500, 5'000, 5'000});

    assert(!machine.advance(1'000, true, "unused").event.has_value());
    assert(!machine.advance(2'000, false, "hang-a").event.has_value());

    const auto sample = machine.advance(2'500, false, "ignored");
    assert(sample.capture_stack);
    assert(sample.incident_id == "hang-a");
    assert(!machine.advance(2'750, false, "ignored").capture_stack);

    const auto recovered = machine.advance(2'800, true, "unused");
    assert(recovered.event.has_value());
    assert(recovered.event->kind == HangEventKind::LongTask);
    assert(recovered.event->duration_ms == 800);
    assert(recovered.event->incident_id == "hang-a");
    assert(recovered.event->threshold_ms == 500);
}

void hang_state_machine_escalates_once_and_cools_down() {
    HangStateMachine machine({250, 500, 5'000, 5'000});

    machine.advance(10'000, false, "hang-b");
    assert(machine.advance(15'000, false, "ignored").capture_stack);
    const auto recovered = machine.advance(16'000, true, "unused");
    assert(recovered.event.has_value());
    assert(recovered.event->kind == HangEventKind::ApplicationNotResponding);
    assert(recovered.event->duration_ms == 6'000);
    assert(recovered.event->threshold_ms == 5'000);

    assert(!machine.advance(16'100, false, "suppressed").event.has_value());
    assert(!machine.advance(20'999, true, "unused").event.has_value());
    assert(!machine.advance(21'000, false, "hang-c").event.has_value());
    const auto next = machine.advance(21'500, true, "unused");
    assert(next.event.has_value());
    assert(next.event->incident_id == "hang-c");
}

void hang_state_machine_handles_boundaries_and_clock_rollback() {
    HangStateMachine machine({250, 500, 5'000, 5'000});

    machine.advance(1'000, false, "below");
    assert(!machine.advance(1'499, true, "unused").event.has_value());

    machine.advance(2'000, false, "equal");
    const auto equal = machine.advance(2'500, true, "unused");
    assert(equal.event.has_value());
    assert(equal.event->kind == HangEventKind::LongTask);

    HangStateMachine rolled_back({250, 500, 5'000, 5'000});
    rolled_back.advance(5'000, false, "old-clock");
    assert(!rolled_back.advance(4'000, true, "unused").event.has_value());
    rolled_back.advance(4'100, false, "new-clock");
    const auto after_rollback = rolled_back.advance(4'600, true, "unused");
    assert(after_rollback.event.has_value());
    assert(after_rollback.event->incident_id == "new-clock");
}

void crash_envelope_validation_rejects_corruption() {
    const auto valid = envelope();
    assert(guance::rum::validate_crash_envelope(valid));

    auto wrong_version = valid;
    wrong_version.version++;
    assert(!guance::rum::validate_crash_envelope(wrong_version));

    auto corrupt = valid;
    corrupt.exception_address++;
    assert(!guance::rum::validate_crash_envelope(corrupt));

    auto incomplete = valid;
    incomplete.completed = 0;
    assert(!guance::rum::validate_crash_envelope(incomplete));
}

void crash_envelope_store_consumes_only_after_enqueue() {
    const auto directory = test_directory();
    std::filesystem::create_directories(directory);
    const auto valid_path = directory / "valid.envelope";
    const auto truncated_path = directory / "truncated.envelope";
    const auto valid = envelope();
    write_binary(valid_path, &valid, sizeof(valid));
    write_binary(truncated_path, &valid, sizeof(valid) / 2);

    CrashEnvelopeStore store(directory, 3, 1024 * 1024);
    int attempted = 0;
    const auto deferred = store.recover([&](const CrashEnvelope&, const std::filesystem::path&) {
        attempted++;
        return false;
    });
    assert(attempted == 1);
    assert(deferred.recovered == 0);
    assert(deferred.deferred == 1);
    assert(deferred.quarantined == 1);
    assert(std::filesystem::exists(valid_path));

    const auto consumed = store.recover([](const CrashEnvelope& recovered, const std::filesystem::path&) {
        return recovered.exception_code_value == 0xC0000005u;
    });
    assert(consumed.recovered == 1);
    assert(consumed.deferred == 0);
    assert(!std::filesystem::exists(valid_path));

    const auto duplicate = store.recover([](const CrashEnvelope&, const std::filesystem::path&) {
        return true;
    });
    assert(duplicate.recovered == 0);

    std::error_code ec;
    std::filesystem::remove_all(directory, ec);
}

void crash_envelope_quarantine_never_overwrites_or_deletes_on_collision() {
    const auto directory = test_directory();
    const auto bad_directory = directory / "bad";
    std::filesystem::create_directories(bad_directory);
    const auto invalid_path = directory / "collision.envelope";
    const char invalid[] = "invalid";
    write_binary(invalid_path, invalid, sizeof(invalid));
    write_binary(bad_directory / "collision.envelope.bad", invalid, sizeof(invalid));
    write_binary(bad_directory / "collision.envelope.1.bad", invalid, sizeof(invalid));

    CrashEnvelopeStore store(directory, 3, 1024 * 1024);
    const auto recovered = store.recover(
        [](const CrashEnvelope&, const std::filesystem::path&) { return true; });
    assert(recovered.quarantined == 1);
    assert(!std::filesystem::exists(invalid_path));
    assert(std::filesystem::exists(bad_directory / "collision.envelope.bad"));
    assert(std::filesystem::exists(bad_directory / "collision.envelope.1.bad"));
    assert(std::filesystem::exists(bad_directory / "collision.envelope.2.bad"));

    std::error_code ec;
    std::filesystem::remove_all(directory, ec);
}

void crash_envelope_retention_counts_quarantine_and_preserves_pairs() {
    const auto directory = test_directory();
    const auto bad_directory = directory / "bad";
    std::filesystem::create_directories(bad_directory);
    const char bytes[] = "retained";
    write_binary(directory / "old.envelope", bytes, sizeof(bytes));
    write_binary(directory / "old.dmp", bytes, sizeof(bytes));
    write_binary(directory / "new.envelope", bytes, sizeof(bytes));
    write_binary(directory / "new.dmp", bytes, sizeof(bytes));
    write_binary(bad_directory / "corrupt.envelope.bad", bytes, sizeof(bytes));

    CrashEnvelopeStore store(directory, 3, 1024 * 1024);
    store.trim(2);

    int retained = 0;
    for (const auto& entry : std::filesystem::recursive_directory_iterator(directory)) {
        if (entry.is_regular_file()) {
            retained++;
        }
    }
    assert(retained <= 1);
    assert(std::filesystem::exists(directory / "old.envelope") ==
           std::filesystem::exists(directory / "old.dmp"));
    assert(std::filesystem::exists(directory / "new.envelope") ==
           std::filesystem::exists(directory / "new.dmp"));

    std::error_code ec;
    std::filesystem::remove_all(directory, ec);
}

void crash_envelope_retention_never_deletes_deferred_records() {
    const auto directory = test_directory();
    std::filesystem::create_directories(directory);
    auto deferred = envelope();
    deferred.flags |= guance::rum::CrashEnvelopeHasMinidump;
    const char dump_name[] = "deferred.dmp";
    std::copy(std::begin(dump_name), std::end(dump_name), deferred.dump_file_name);
    deferred = guance::rum::finalize_crash_envelope(deferred);
    write_binary(directory / "deferred.envelope", &deferred, sizeof(deferred));
    write_binary(directory / "deferred.dmp", &deferred, sizeof(deferred));

    CrashEnvelopeStore store(directory, 3, 1024 * 1024);
    const bool has_capacity = store.trim(2, sizeof(CrashEnvelope));
    assert(std::filesystem::exists(directory / "deferred.envelope"));
    assert(!std::filesystem::exists(directory / "deferred.dmp"));
    assert(has_capacity);

    bool recovered_without_dump = false;
    store.recover([&](const CrashEnvelope&, const std::filesystem::path& dump_path) {
        recovered_without_dump = dump_path.empty();
        return false;
    });
    assert(recovered_without_dump);

    auto second = envelope();
    second = guance::rum::finalize_crash_envelope(second);
    write_binary(directory / "second.envelope", &second, sizeof(second));
    assert(!store.trim(2, sizeof(CrashEnvelope)));
    assert(std::filesystem::exists(directory / "deferred.envelope"));
    assert(std::filesystem::exists(directory / "second.envelope"));

    std::error_code ec;
    std::filesystem::remove_all(directory, ec);
}

} // namespace

int main() {
    hang_state_machine_reports_only_after_recovery();
    hang_state_machine_escalates_once_and_cools_down();
    hang_state_machine_handles_boundaries_and_clock_rollback();
    crash_envelope_validation_rejects_corruption();
    crash_envelope_store_consumes_only_after_enqueue();
    crash_envelope_quarantine_never_overwrites_or_deletes_on_collision();
    crash_envelope_retention_counts_quarantine_and_preserves_pairs();
    crash_envelope_retention_never_deletes_deferred_records();
    return 0;
}
