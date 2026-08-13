#pragma once

#include <string>
#include <string_view>

namespace guance::rum {

// Produces an RFC 1951 raw DEFLATE stream suitable for HTTP
// Content-Encoding: deflate, matching the managed SDK transport.
std::string deflate_compress(std::string_view input);

} // namespace guance::rum
