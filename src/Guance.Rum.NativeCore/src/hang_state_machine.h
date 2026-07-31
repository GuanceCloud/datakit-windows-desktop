#pragma once

#include <cstdint>
#include <optional>
#include <string>

namespace guance::rum {

struct HangThresholds {
    int64_t probe_interval_ms = 250;
    int64_t long_task_threshold_ms = 500;
    int64_t hang_threshold_ms = 5'000;
    int64_t cooldown_ms = 5'000;
};

enum class HangEventKind {
    LongTask,
    ApplicationNotResponding,
};

struct HangEvent {
    HangEventKind kind = HangEventKind::LongTask;
    int64_t duration_ms = 0;
    int64_t threshold_ms = 0;
    std::string incident_id;
};

struct HangDecision {
    bool capture_stack = false;
    std::string incident_id;
    std::optional<HangEvent> event;
};

class HangStateMachine {
public:
    explicit HangStateMachine(HangThresholds thresholds);

    HangDecision advance(int64_t now_ms, bool responsive, std::string candidate_incident_id);
    void reset();

private:
    enum class State {
        Responsive,
        Incident,
        Cooldown,
    };

    void begin_incident(int64_t now_ms, std::string incident_id);

    HangThresholds thresholds_;
    State state_ = State::Responsive;
    int64_t incident_started_ms_ = 0;
    int64_t cooldown_until_ms_ = 0;
    int64_t last_observed_ms_ = 0;
    bool has_last_observed_ = false;
    bool stack_capture_requested_ = false;
    std::string incident_id_;
};

} // namespace guance::rum
