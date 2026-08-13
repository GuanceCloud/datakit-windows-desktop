#pragma once

#include <string>
#include <string_view>

namespace guance::rum {

// Produces an RFC 1950 zlib stream containing DEFLATE-compressed data for
// HTTP Content-Encoding: deflate.
std::string deflate_compress(std::string_view input);

} // namespace guance::rum
