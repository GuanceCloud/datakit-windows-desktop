#include "deflate.h"
#include "deflate_test_utils.h"

#include <cassert>
#include <string>

namespace {

void require_round_trip(const std::string& input) {
    const auto compressed = guance::rum::deflate_compress(input);
    assert(!compressed.empty());
    assert(guance::test::inflate_raw_deflate(compressed) == input);
}

} // namespace

int main() {
    require_round_trip({});
    require_round_trip("a");
    require_round_trip("view,service=desktop value=1i 1722300000000000000\n");
    assert(guance::rum::deflate_compress("") == std::string("\x03\x00", 2));
    assert(guance::rum::deflate_compress("a") == std::string("\x4b\x04\x00", 3));
    assert(guance::rum::deflate_compress("abc") == std::string("\x4b\x4c\x4a\x06\x00", 5));
    assert(guance::rum::deflate_compress("hello") == std::string("\xcb\x48\xcd\xc9\xc9\x07\x00", 7));

    std::string every_byte;
    for (int repeat = 0; repeat < 4; ++repeat) {
        for (int value = 0; value <= 255; ++value) {
            every_byte.push_back(static_cast<char>(value));
        }
    }
    require_round_trip(every_byte);

    std::string repeated;
    for (int index = 0; index < 4000; ++index) {
        repeated += "view,service=desktop,env=prod value=1i 1722300000000000000\n";
    }
    const auto compressed = guance::rum::deflate_compress(repeated);
    assert(guance::test::inflate_raw_deflate(compressed) == repeated);
    assert(compressed.size() < repeated.size() / 4);
    return 0;
}
