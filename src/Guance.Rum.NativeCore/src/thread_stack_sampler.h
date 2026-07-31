#pragma once

#include <cstdint>
#include <string>

namespace guance::rum {

std::string sample_thread_stack(uint32_t thread_id, int budget_ms);

} // namespace guance::rum
