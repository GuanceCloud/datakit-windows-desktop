#include "line_protocol.h"

#include <algorithm>
#include <cassert>
#include <iostream>

int main() {
    const auto monotonic_started = guance::rum::monotonic_time_nanoseconds();
    const auto monotonic_finished = guance::rum::monotonic_time_nanoseconds();
    assert(monotonic_started >= 0);
    assert(monotonic_finished >= monotonic_started);
    assert(guance::rum::is_within_forward_window(1000, 900, 200));
    assert(!guance::rum::is_within_forward_window(900, 1000, 200));
    assert(!guance::rum::is_within_forward_window(1201, 1000, 200));

    guance::rum::RumEvent event;
    event.measurement = "action";
    event.tags["app_id"] = "app id";
    event.tags["action_name"] = "save,button";
    event.fields["duration"] = int64_t{123};
    event.fields["ok"] = true;
    event.fields["message"] = std::string{"a \"quoted\" value"};
    event.fields["multiline"] = std::string{"first\r\nsecond"};
    event.timestamp_ns = 42;

    const auto line = guance::rum::format_line_protocol(event);
    assert(line.find("app_id=app\\ id") != std::string::npos);
    assert(line.find("action_name=save\\,button") != std::string::npos);
    assert(line.find("duration=123i") != std::string::npos);
    assert(line.find("message=\"a \\\"quoted\\\" value\"") != std::string::npos);
    assert(line.find("multiline=\"first\\r\\nsecond\"") != std::string::npos);
    assert(std::count(line.begin(), line.end(), '\n') == 1);
    assert(line.rfind(" 42\n") == line.size() - 4);
    std::cout << line;
    return 0;
}
