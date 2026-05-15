#pragma once

#include "rum_core.h"

#include <string>
#include <vector>

namespace guance::rum {

struct TransportResult {
    bool delete_from_queue = false;
    bool retry_later = true;
    int status_code = 0;
    int error_code = 0;
    int64_t latency_ms = 0;
    std::string error_message;
};

TransportResult send_to_dataway(const Config& config, const std::vector<std::string>& lines);
TransportResult send_session_replay_to_dataway(const Config& config, const std::string& content_type, const std::string& body);

} // namespace guance::rum
