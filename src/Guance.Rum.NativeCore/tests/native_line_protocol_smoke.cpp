#include "line_protocol.h"

#include <cassert>
#include <iostream>

int main() {
    guance::rum::RumEvent event;
    event.measurement = "action";
    event.tags["app_id"] = "app id";
    event.tags["action_name"] = "save,button";
    event.fields["duration"] = int64_t{123};
    event.fields["ok"] = true;
    event.fields["message"] = std::string{"a \"quoted\" value"};
    event.timestamp_ns = 42;

    const auto line = guance::rum::format_line_protocol(event);
    assert(line.find("app_id=app\\ id") != std::string::npos);
    assert(line.find("action_name=save\\,button") != std::string::npos);
    assert(line.find("duration=123i") != std::string::npos);
    assert(line.find("message=\"a \\\"quoted\\\" value\"") != std::string::npos);
    assert(line.rfind(" 42\n") == line.size() - 4);
    std::cout << line;
    return 0;
}
