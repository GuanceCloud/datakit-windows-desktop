#include "hang_state_machine.h"

#include <algorithm>
#include <utility>

namespace guance::rum {

HangStateMachine::HangStateMachine(HangThresholds thresholds)
    : thresholds_(thresholds) {}

HangDecision HangStateMachine::advance(
    int64_t now_ms,
    bool responsive,
    std::string candidate_incident_id) {
    HangDecision decision;

    if (has_last_observed_ && now_ms < last_observed_ms_) {
        reset();
    }
    last_observed_ms_ = now_ms;
    has_last_observed_ = true;

    if (state_ == State::Cooldown) {
        if (now_ms < cooldown_until_ms_) {
            return decision;
        }
        state_ = State::Responsive;
        cooldown_until_ms_ = 0;
    }

    if (state_ == State::Responsive) {
        if (!responsive) {
            begin_incident(now_ms, std::move(candidate_incident_id));
        }
        return decision;
    }

    const auto elapsed_ms = std::max<int64_t>(0, now_ms - incident_started_ms_);
    if (!responsive) {
        if (!stack_capture_requested_ && elapsed_ms >= thresholds_.long_task_threshold_ms) {
            stack_capture_requested_ = true;
            decision.capture_stack = true;
            decision.incident_id = incident_id_;
        }
        return decision;
    }

    if (elapsed_ms >= thresholds_.hang_threshold_ms) {
        decision.event = HangEvent{
            HangEventKind::ApplicationNotResponding,
            elapsed_ms,
            thresholds_.hang_threshold_ms,
            incident_id_,
        };
    } else if (elapsed_ms >= thresholds_.long_task_threshold_ms) {
        decision.event = HangEvent{
            HangEventKind::LongTask,
            elapsed_ms,
            thresholds_.long_task_threshold_ms,
            incident_id_,
        };
    }

    state_ = decision.event ? State::Cooldown : State::Responsive;
    cooldown_until_ms_ = decision.event ? now_ms + thresholds_.cooldown_ms : 0;
    incident_started_ms_ = 0;
    incident_id_.clear();
    stack_capture_requested_ = false;
    return decision;
}

void HangStateMachine::reset() {
    state_ = State::Responsive;
    incident_started_ms_ = 0;
    cooldown_until_ms_ = 0;
    has_last_observed_ = false;
    stack_capture_requested_ = false;
    incident_id_.clear();
}

void HangStateMachine::begin_incident(int64_t now_ms, std::string incident_id) {
    state_ = State::Incident;
    incident_started_ms_ = now_ms;
    incident_id_ = std::move(incident_id);
    stack_capture_requested_ = false;
}

} // namespace guance::rum
