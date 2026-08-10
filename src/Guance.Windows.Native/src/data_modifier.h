#pragma once

#include "guance_sdk.h"
#include "line_protocol.h"
#include "resource_collection.h"

namespace guance::rum {

struct DataModifierConfig {
    guance_data_modifier_callback data_modifier = nullptr;
    guance_line_data_modifier_callback line_data_modifier = nullptr;
    void* user_data = nullptr;
};

bool data_modifier_config_from_c(
    const guance_data_modifier_config& source,
    DataModifierConfig& destination);

void apply_data_modifiers(
    RumEvent& event,
    const ResourceCollectionConfig& privacy,
    const DataModifierConfig& modifiers);

} // namespace guance::rum
